# Finance Core: Command Contracts

Status: DRAFT v0.6, propagation pass 2026-10-13 (17, sections 4 and 5 applied, delta in 13.3). v0.6 changes: error code index completed from specs 02 to 16 and unified names (`POSTING.OUTSIDE_WINDOW`, `PERIOD.NOT_FOUND`, `PERIOD.HARD_CLOSED`, `PERIOD.SOFT_CLOSED_NOT_AUTHORISED`, `VAT.SUPERSEDED`, `VAT.NOT_FILED` dropped), new areas `POSTING` and `SERIES`, new commands `gr-ir:reverse-write-off` and `apps:reactivate`, document type `GR_IR_WRITE_OFF`, owning-command table complete, lock classes renumbered (fiscal_period and fiscal_year class 5, open items 6, `outbox_head` 7). Earlier v0.5, fix pass 4 2026-10-13 (17, section 4 applied, delta in 13.2). v0.5 changes: stale check at hard-close only (X2), lock order and phase model (X4, X7, A1), `journal_document.origin` set by the core (X5), `CLOSING` document type (X1), new commands (`sales-returns:reverse`, `purchase-returns:reverse`, `gr-ir:write-off`, apps and namespace commands, calendar and series commands), new error codes, `vat-returns:reverse-settlement` payload (X8), TAX_EVIDENCE in `change-open-fields` (F2), `read.ext` removed (F9), `chains:verify` drops the event (decided), permissions of `tenant-deletions:*` and `exports:*`. Earlier v0.4 added: the settlement reversal command renamed `vat-returns:reverse-settlement` (permission `vat.settle`) with the new error `VAT.NOT_SETTLED`, the closed reason code list with `journal-documents:reverse` accepting only CORRECTION and ERROR from external callers (Q20 closed), the mirror type rule (the four own mirror types stay, "keeps the original type" only for types without one), the adopted error codes (`MIGRATION.*` as in 13, 7.1, `INVENTORY.INSUFFICIENT_STOCK`, `INVENTORY.ALLOCATION_*`, `ORDER.CREDIT_NOTE_*`, `ORDER.INVOICE_HAS_CREDIT_NOTES`, `PERIOD.HISTORY_YEAR`, `REVERSAL.REASON_REQUIRED`, `REVERSAL.HAS_CLEARED_LINES`), the three reverse commands `landed-costs:reverse`, `sales-credit-notes:reverse`, `purchase-credit-notes:reverse` as adopted (contracts in 10 and 11), the confirmed `upsert` rule (07, 11.1) and the correction that `outbox:verify` has the permission `read.audit`. Earlier: v0.3, second re-baseline pass 2026-10-13 (14, sections 8, 9 and 10 applied), added: the owning commands `sales-credit-notes:reverse`, `purchase-credit-notes:reverse`, `landed-costs:reverse` and `vat-settlements:reverse` (D1 follow-up), the reason code list, no separate `IC` document type, the IC permissions `ic.order`, `ic.deliver`, `ic.invoice` and `read.audit` for `outbox:verify` (D10), `scope_key` instead of `ledger_id` on chain commands and one VAT_RETURN chain per company (K1), counters before chain heads confirmed and the clearing pre-lock rule (D3), `cost_source` per movement line (D6), and the `MIGRATION` and `INVENTORY.INSUFFICIENT_STOCK` codes (proposed at v0.3, adopted at v0.4). Earlier: v0.2, re-baselined for the decisions of the scope review (14, sections 8 and 9). Builds on 00-design-principles v0.4, ADR-001 to ADR-174 and specs 02 to 16. v0.2 adds: the fixed permission table and the command-to-permission map (D10, ADR-154), the reversal-only-through-owning-command rule (D1, ADR-145), the complete error areas (VAT, MIGRATION, INVENTORY, ORDER, MATCH, compliance, EXT, APP, EVENT), the `app_code` claim and outbox events (K3, ADR-159, 15), `chain_head` (K1, ADR-158), the amended lock order (D3, ADR-147), the thinned matching and reservation policy (S1, S3: ADR-155, ADR-157), and the normalised command names. ADR-137 to ADR-144 are raised in specs 15 and 16.
Scope: the logical contract of the transaction-layer commands: envelope, idempotency, validation pipeline, error model, command catalogue, request and response fields of the commands on the journal, clearing, revaluation, period and year-end tables, and the authorisation names of all domains (section 8).
Out of scope: transport mapping (REST, gRPC, queue), authentication, the posting rules engine for business events (ADR-012, own domain), the field-level contracts of configuration and master data (07, section 11), VAT (08, section 7), inventory (10, section 6), orders (11, section 6), compliance (12, section 13), migration (13, section 7), extensions and the change feed (15), and read and query APIs (09, except `chains:verify` and `revaluation:preview`).
Contract notation: field tables with `Req` (required), `Opt` (optional). Types as in 02, section 1. The contract is technology-neutral (ADR-001). Naming is `resource:action`.

## 1. Decisions taken

| Topic | Decision |
|---|---|
| Amounts | The caller sends transaction-currency amounts only, optionally explicit rate overrides. The core derives LC, GC, RC, tax lines and rounding lines and returns the complete posted document |
| Errors | Validation runs in ordered phases. Within a phase all independent errors are collected, each with code, path and parameters. A later phase is skipped when an earlier one makes it meaningless |
| Idempotency | One `command_log` (successful commands only). Same key and same request fingerprint returns the original result. Same key and a different fingerprint is a conflict. Rejected commands are not recorded |
| Change control | `change-open-fields` carries the expected old value of every field (compare-and-set). A mismatch rejects the whole command |
| Reversal | `journal-documents:reverse` reverses only documents that no other command owns. Derived and system documents are reversed only through their owning command (5.3). Every mirror document carries `reversal_of_document_id` and a reason code of the closed list AUTO_REVERSAL, RERUN, REOPEN, CORRECTION, ERROR, RETURN. The four mirror types REVERSAL, REVAL_REVERSAL, CLEARING_RESET and STOCK_REVERSAL stay. A mirror keeps the `document_type` of the original only where the type has no mirror type of its own (YEAR_END, VAT_SETTLE, DROPSHIP_COST, GR_IR_WRITE_OFF, the migration types, order-built invoices and credit notes) (D1, ADR-145, 14 section 11) |
| Authorisation | One fixed permission table (section 8). Every command of the catalogues names exactly one permission, written in P0 (D10, ADR-154) |
| Events | Every successful EXECUTE command writes its outbox events in the same transaction (15, 5.1, K3, ADR-159) |

## 2. Conventions common to all commands

- **Synchronous and atomic.** A command validates and persists in one database transaction and returns the final result or the errors (P2, ADR-004). There are no partial results.
- **No drafts.** There is no status `draft` and no way to save an incomplete document (ADR-013).
- **Tenant from identity.** `tenant_id` comes from the authenticated identity, never from the payload (ADR-002).
- **Complete result.** Every successful command returns the created or changed records, including generated lines and numbers, so callers never need a follow-up read to learn what was posted.
- **Events in the same transaction.** State-changing commands collect their outbox events (15, section 6) and write them in P7 with `correlation_id` of the command. A rejected command, a rolled-back command and VALIDATE mode write none. Batch wrappers write events per successful item.

### 2.1 Request envelope

| Field | Req | Notes |
|---|---|---|
| context.idempotency_key | Req | Max 100 characters. Where a command derives keys (`<key>#<n>`, 4.3), the base key is capped at **90 characters** so the derived key fits TEXT(100). Scope: tenant. Mandatory on every command (ADR-004). Optional in VALIDATE mode, never consumed there |
| context.correlation_id | Req | Caller's trace id, max 100 characters (TEXT(100) on `command_log`, `outbox_event` and `master_change_log`). Stored on all created records (P13) |
| context.source_system | Req | Code of the calling system or module (P13). A free tracing field. The core never uses it for a decision: the origin of a document is `journal_document.origin`, set by the core (X5, 5.2) |
| context.mode | Opt | `EXECUTE` (default) or `VALIDATE` (section 6) |
| context.company_id | Req for company-scoped commands | All transaction commands are company-scoped. Intercompany commands name both companies in the payload |
| context.actor | Req | User or service identity. Becomes `posted_by` and similar columns |
| context.app_code | Opt | Claim from the identity provider when the caller is a registered app (15, 3.1). Required for `extensions:*`, `extension-definitions:*` and `events:ack`, `:list`, `:head`. **Not required** for `events:rewind` and the commands under `apps.manage` (F10). A missing or inactive code on the required commands rejects with `APP.NOT_REGISTERED` |
| context.permissions | Req | Authorisation names held by the actor (section 8). The core checks presence only (04, A5) |
| payload | Req | Command-specific, sections 5 and 7 |

### 2.2 Response envelope

Success:

| Field | Notes |
|---|---|
| status | `OK` |
| idempotent_replay | True if the response is a replay of an earlier identical command |
| command_log_id | Id of the `command_log` entry (null in VALIDATE mode) |
| result | Command-specific, sections 5 and 7 |
| warnings[] | Error-shaped objects with severity `WARNING` (Q3) |

Rejection:

| Field | Notes |
|---|---|
| status | `REJECTED` |
| errors[] | One or more error objects (2.3). Nothing was persisted |

A system failure after validation passed (database unavailable) is not a rejection. It is reported as `status` = `FAILED` with a single `SYSTEM.*` error and, where safe, `retryable` = true. Retrying with the same idempotency key is always safe.

### 2.3 Error object

| Field | Notes |
|---|---|
| code | Stable machine code, namespaced: `AREA.NAME` (2.4) |
| severity | `ERROR` or `WARNING` |
| phase | Pipeline phase that produced it (P0 to P7, section 3) |
| path | JSON-pointer-style path to the offending request field, e.g. `/lines/2/dimensions/cost_center_id`. Empty for document-level errors |
| params | Object with values needed to render and act on the error: offending values, allowed values, limits |
| retryable | True if the same request may succeed without change (concurrency conflicts, lock timeouts) |

The core returns codes and parameters only, no localised text. Rendering is an app concern (Q4).

### 2.4 Error code areas

| Area | Examples (non-exhaustive) |
|---|---|
| `SCHEMA` | `REQUIRED_FIELD_MISSING`, `INVALID_TYPE`, `INVALID_VALUE`, `UNKNOWN_FIELD`, `DUPLICATE_LINE_REF`, `DUPLICATE_ITEM_REF`, `BATCH_TOO_LARGE`, `NO_EFFECTIVE_CHANGE`, `TOO_MANY_ERRORS` |
| `AUTH` | `PERMISSION_MISSING`, `COMPANY_NOT_ALLOWED` |
| `IDEMPOTENCY` | `KEY_REUSED_DIFFERENT_REQUEST`, `KEY_EXPIRED_REUSE` |
| `POSTING` | `OUTSIDE_WINDOW` (a posting date outside the posting window, 04, 4.1). Replaces `PERIOD.OUTSIDE_POSTING_WINDOW` |
| `SERIES` | `NO_ASSIGNMENT` (no number series assignment for the document type, ledger and fiscal year of the posting date, 04, 5.2. Replaces `MASTER.NUMBER_SERIES_NOT_ASSIGNED`), `LEGAL_REQUIRES_YEAR` (`is_legal` series without `include_fiscal_year`, 04, 5.1), `EFFECTIVE_FROM_INVALID` (`number-series-assignments:create` with an `effective_from` that is not a fiscal year start or breaks the continuity rule, 04, 5.2, A3) |
| `MASTER` | `ACCOUNT_NOT_FOUND`, `ACCOUNT_INACTIVE`, `ACCOUNT_POSTING_BLOCKED`, `PARTNER_NOT_FOUND`, `PARTNER_INACTIVE`, `PARTNER_RESTRICTED`, `PARTNER_ANONYMISED` (12), `CURRENCY_UNKNOWN`, `LOCATION_TYPE_LOCKED` (16, 6.2) |
| `PERIOD` | `NOT_FOUND` (no period for the date or the year is not opened, replaces `NO_PERIOD_FOR_DATE`), `HARD_CLOSED` (replaces `PERIOD_HARD_CLOSED`), `SOFT_CLOSED_NOT_AUTHORISED` (a SOFT_CLOSED period without `post.soft_closed` or `change.soft_closed`, replaces `PERIOD_SOFT_CLOSED_NO_AUTH`), `YEAR_CLOSED`, `SPECIAL_PERIOD_DATE_INVALID`, `CLOSING_PERIOD_MANAGED` (a manual status command on the closing period, which only `year-end:close` and `year-end:reopen` change, 04, 3.3), `REOPEN_LATER_PERIOD_CLOSED` (`periods:reopen` while a later period is HARD_CLOSED, 04, 7.3), `HISTORY_YEAR` (`year-end:close`, `periods:soft-close` and `periods:hard-close` on an `is_history` period before the migration seal, which closes history years instead, 13, 9.1. A period counts as history when its year is before the cutover year, or it lies in the cutover year up to `fiscal_year.history_until_period`, D8), `HISTORY_YEAR_SEALED` (`periods:reopen` and `year-end:reopen` on a sealed history year or history period) |
| `RATE` | `RATE_NOT_FOUND`, `RATE_OVERRIDE_OUT_OF_TOLERANCE` |
| `ACCOUNT_RULE` | `PARTNER_REQUIRED`, `PARTNER_FORBIDDEN`, `DUE_DATE_REQUIRED`, `DIMENSION_REQUIRED`, `DIMENSION_FORBIDDEN`, `DIMENSION_VALUE_INVALID`, `TAX_CODE_REQUIRED`, `TAX_CODE_FORBIDDEN`, `TAX_ACCOUNT_DIRECT_POSTING` |
| `TAX` | `NO_RULE_MATCHED`, `CODE_NOT_PERMITTED`, `AMOUNT_OUT_OF_TOLERANCE`, `BASE_LINE_NOT_FOUND`, `ORIGIN_DESTINATION_REQUIRED` (a taxed base line of a partnerless document without explicit origin and destination, 07, 8.4, F1), `EVIDENCE_IMMUTABLE` (`change-open-fields` on an existing evidence row: evidence is append-only, 5.4, F2), `DEDUCTION_RATE_NOT_FOUND` (the deduction key of the code has no rate at the tax date, 19, 3.2), `DEDUCTION_AMOUNT_REQUIRED` (a line with a code whose `deduction_cap` is SUPPLIED_AMOUNT without `deductible_tax_amount_tc`, 19, 6.2), `DEDUCTION_AMOUNT_NOT_ALLOWED` (`deductible_tax_amount_tc` on a line whose code has no supplied cap), `DEDUCTION_AMOUNT_EXCEEDS_LIMIT` (the supplied amount is negative or above `ROUND(computed tax x deductible_pct / 100)`, 19, 3.3) |
| `BALANCE` | `UNBALANCED_TC`, `UNBALANCED_LC`, `UNBALANCED_GC`, `UNBALANCED_RC`, `ROUNDING_ACCOUNT_MISSING` |
| `REVERSAL` | `NOT_FOUND`, `ALREADY_REVERSED`, `TYPE_NOT_REVERSIBLE` (params: `document_type`, `owning_command`, 5.3), `HAS_CLEARED_LINES`, `REASON_REQUIRED`, `REASON_NOT_ALLOWED` (an external caller of `journal-documents:reverse`, `clearings:reset`, `vat-returns:reverse-settlement` or another reverse command with a reason code other than CORRECTION or ERROR, params: `allowed_values`). Reversal-specific order, stock and landed-cost conditions have their own codes in `ORDER` and `INVENTORY` |
| `LOCK` | `FIELD_LOCKED_L1`, `FIELD_LOCKED_L3`, `FIELD_UNKNOWN` |
| `CONFLICT` | `EXPECTED_VALUE_MISMATCH`, `CONCURRENT_UPDATE`, `ITEM_ALREADY_CLEARED` |
| `CLEARING` | `ITEMS_NOT_SAME_ACCOUNT`, `ITEMS_NOT_SAME_PARTNER`, `OVER_CLEARING`, `TC_DIFFERENCE`, `NOT_AN_OPEN_ITEM`, `ALREADY_RESET`, `ITEM_OTHER_COMPANY`, `NOT_RESETTABLE` (`clearings:reset` on a reversal clearing, the automatic clearing of a mirror document (B3) or of a rollback, 03) |
| `REVAL` | `PERIOD_NOT_OPEN`, `OUTSIDE_POSTING_WINDOW` (the reversal or posting date of the run lies outside the posting window, 05, 3.4 rule 2), `SOFT_CLOSE_NOT_AUTHORISED` (the run's period is SOFT_CLOSED and the caller lacks `post.soft_closed`, 05), `NEXT_PERIOD_NOT_OPEN`, `NEXT_YEAR_NOT_OPENED`, `CLOSING_RATE_MISSING` |
| `CLOSE` | `PREDECESSOR_NOT_HARD_CLOSED`, `REVALUATION_STALE`, `REVALUATION_MISSING`, `YEAR_NOT_ALL_CLOSED`, `CLOSING_PERIOD_MISSING`, `CLOSING_PERIOD_NOT_OPEN` (`year-end:close` while the closing period is not OPEN, A4), `SPECIAL_PERIOD_NOT_CLOSED` (`year-end:close` while a non-closing special period is not HARD_CLOSED, A4. The name used in 04, 7.3 and 05, 5.1, it replaces `SPECIAL_PERIOD_OPEN` of v0.5), `YEAR_NOT_OPEN` (`year-end:close` on a year whose status is not OPEN, 04, 7.3), `YEAR_NOT_CLOSED` (`year-end:reopen` on a year that is not CLOSED, 05, 5.3), `RETAINED_EARNINGS_INVALID` (`year-end:close` without a valid RETAINED_EARNINGS role account, 05, 5.1), `REOPEN_LATER_YEAR_CLOSED` (`year-end:reopen` while a later year is CLOSED, 04, 7.3), `REOPEN_BLOCKED_BY_POSTINGS` |
| `CONFIG` | Configuration and master data commands, 07, section 11.2: `DUPLICATE_CODE`, `ROW_VERSION_MISMATCH`, `IMMUTABLE_AFTER_POSTING`, `REFERENCED`, `VALID_FROM_NOT_LATER`, `VALID_FROM_IN_CLOSED_PERIOD`, `OVERLAPPING_RULE`, `ROLE_NOT_ASSIGNED`, `ROLE_ACCOUNT_RULE_VIOLATED`, `INVALID_REFERENCE`, `CURRENCY_NOT_ENABLED`, `DIMENSION_COMPANY_MISMATCH`, `HIERARCHY_CYCLE`, `COMPANY_NOT_READY`, `COST_SCOPE_CONFLICT` (10, Q10), `REVALUE_FLAG_ON_RETAINED_EARNINGS` (an account with `revalue_flag` assigned the RETAINED_EARNINGS role, or the flag set on the account that holds it, 05, 5.1 and Q3, 07), `DEDUCTION_ON_NON_INPUT_CODE` (a tax code version of type OUTPUT, EXEMPT or ZERO_RATED with `deductible_pct` other than 100, a `deduction_key` or a `deduction_cap`, 19, 3.1), `DEDUCTION_KEY_UNKNOWN` (a code version names a key that does not exist for its registration, 19, 3.2), `DEDUCTION_KEY_REFERENCED` (a change of the kind of a key whose rates were used) |
| `VAT` | VAT commands, 08, section 7: `NO_REGISTRATION`, `REGISTRATION_INACTIVE`, `PERIOD_NOT_COMPLETE`, `PREVIOUS_PERIOD_NOT_PREPARED`, `ALREADY_PREPARED`, `SUPERSEDED` (a command on a superseded return, among them `vat-returns:reverse-settlement` while a successor settlement along `replaces_return_id` is active, B9. Replaces `SUCCESSOR_SETTLEMENT_ACTIVE`), `NOTHING_TO_REPORT`, `ALREADY_FILED`, `ALREADY_SETTLED`, `NOT_SETTLED` (`vat-returns:reverse-settlement` on a return without an active settlement), `SETTLEMENT_DATE_NOT_POSTABLE`, `EVIDENCE_MISSING` (warning) |
| `INVENTORY` | Inventory commands, 10, section 6, and 16, section 6: `UNIT_CONVERSION_MISSING`, `QUANTITY_SCALE_EXCEEDED`, `TRACKING_REQUIRED`, `SERIAL_QUANTITY_INVALID`, `TRACKING_RECALLED`, `LOT_EXPIRED` (warning), `ACCOUNT_DETERMINATION_MISSING`, `RESERVATION_EXCEEDS_AVAILABLE`, `STOCK_RESERVED`, `RECEIPT_CONSUMED` (a FIFO receipt reversal when the receipt layer's remaining quantity is below the receipt quantity, B8), `BOOK_QUANTITY_CHANGED`, `ALLOCATION_BASIS_MISSING`, `LOCATION_NOT_AVAILABLE`, `LOCATION_STATUS_MISMATCH`, `INSUFFICIENT_STOCK` (adopted: an issue beyond available stock where `allow_negative_stock` is false, 10, 6.2 P4, Q16), `ALLOCATION_NOT_FOUND`, `ALLOCATION_ALREADY_REVERSED`, `ALLOCATION_NOT_REVERSIBLE` (`landed-costs:reverse` when a later movement exists on the cost key since the allocation, correction is a new allocation, B7), `ALLOCATION_EXISTS` (`purchase-invoices:reverse` while charge-line allocations of the invoice are not reversed, 10, 6.8), `VALUE_REQUIRED` (an adjustment-in without value and without an existing cost layer, 17 section 5), `CURRENCY_MIXED` (a stock document with more than one transaction currency, rejected at P1, 17 section 5, D7). Only codes that a spec uses are listed |
| `MIGRATION` | Migration commands, 13, section 7. Adopted as in 13, 7.1 (14 section 11): `BATCH_NOT_FOUND`, `BATCH_STATUS_INVALID` (params: `status`, `allowed_statuses`, any batch command in a status it does not allow), `BATCH_EXISTS` (`open` while a non-aborted batch exists for the company), `COMPANY_NOT_DRAFT` (`open`), `CUTOVER_NOT_PERIOD_START` (`open`), `CUTOVER_YEAR_NOT_OPEN` (`open`), `HISTORY_UNTIL_INVALID` (`open`, `history_until_period` is not the period before the cutover period, D8), `CONTROL_KIND_INVALID` (`declare-totals`), `HISTORY_YEARS_EXCEEDED`, `HISTORY_YEAR_NOT_CONTIGUOUS` (`fiscal-years:open-history`), `RECORD_TYPE_INVALID`, `STAGE_BATCH_TOO_LARGE` (`migration-records:stage`), `RECORDS_INVALID` (`validate`, params: count), `RECONCILIATION_MISSING`, `RECONCILIATION_FAILED`, `CHUNK_FAILED` (`commit`, params: `chunk_no`, `area`, `failure_codes`), `ROLLBACK_NOT_ALLOWED` (`rollback`), `ABORT_NOT_ALLOWED` (`abort` when a chunk is DONE, or the status is COMMITTED, SEALED, ABORTED or ROLLED_BACK. Allowed in COMMITTING when no chunk is DONE, D6), `SEAL_CHECK_FAILED` (`seal`, params: the failed checks), `CLEARING_NOT_ZERO` (`seal`), `APPROVER_NOT_DISTINCT`, `NO_PENDING_REQUEST` (`approve`). Missing migration roles reject with `CONFIG.ROLE_NOT_ASSIGNED`. Reconciliation check names (`RECORDS_VALID`, `TB_BALANCES`, and so on) are check results, not error codes |
| `ORDER` | Order commands, 11, sections 6 and 10, and 16, 5.2: `OVER_DELIVERY`, `PRICE_LOCKED_INVOICED`, `DELIVERY_INVOICED`, `RECEIPT_INVOICED`, `RETURN_EXCEEDS_DELIVERED`, `DUPLICATE_SUPPLIER_INVOICE`, `LINE_CLOSED`, `INVOICE_NOT_ALLOWED`, `DROPSHIP_LINK_INVALID`, `DATE_INVALID`, `CREDIT_NOTE_NOT_FOUND`, `CREDIT_NOTE_ALREADY_REVERSED` (`sales-credit-notes:reverse`, `purchase-credit-notes:reverse`), `INVOICE_HAS_CREDIT_NOTES` (an invoice reverse while credit notes of it exist: reverse them first), `INVOICE_LINE_REFERENCE_MISSING` (an APP_SUPPLIES invoice line without the required order or delivery reference, 11, 6.4, X6), `DELIVERY_RETURNED` (`sales-deliveries:reverse`, `dropship-confirmations:reverse` and the IC delivery reverse while returns reference the delivery, B6), `RETURN_NOT_FOUND`, `RETURN_ALREADY_REVERSED` (`sales-returns:reverse`, `purchase-returns:reverse`, B5), `RETURN_CREDITED` (a return reverse while a credit note of the return is not reversed, reverse the credit note first, 11, 5.4), `CREDIT_EXCEEDS_INVOICED` (a credit above the return line or invoice line cap, 11, 6.5), `CONFIRMED_DATE_ON_CREATE` (`confirmed_date` on an order create, F12, 11, 16), `GRIR_ITEM_INVALID` (`gr-ir:write-off` with an item that is not an open GR/IR item of the company or is already cleared, 11, 7.5), `GRIR_RESIDUAL_CHANGED` (the current residual differs from `expected_residual_lc`, params: the current value, 11, 7.5), `GRIR_WRITE_OFF_NOT_FOUND` (`gr-ir:reverse-write-off` on a document that is not a `GR_IR_WRITE_OFF` document of the company, 11, 7.5a), `GRIR_WRITE_OFF_REVERSED` (`gr-ir:reverse-write-off` on an already reversed write-off, 11, 7.5a). `gr-ir:reverse-write-off` also uses `REVERSAL.REASON_REQUIRED` and `REVERSAL.REASON_NOT_ALLOWED` (11, 7.5a). `GR_IR_NOTHING_TO_WRITE_OFF` of v0.5 is dropped (no spec uses it). `DELIVERY_INVOICED` also covers an invoice line that carries a delivery reference to the delivery line (D2). `INVOICE_REVERSED` (`sales-credit-notes:create` and `purchase-credit-notes:post` on a reversed invoice), `PARTIAL_DEDUCTION_NOT_SUPPORTED` (an order-built purchase invoice or credit note line for a stock item whose tax code has `deductible_pct` below 100, a `deduction_key` or a `deduction_cap`, ADR-206, 19, 10) |
| `MATCH` | Supplier invoice matching, 11, 7: `QUANTITY_EXCEEDS_RECEIVED` (THREE_WAY cap is received minus returned, D1), `GROSS_MISMATCH`, `TAX_OUT_OF_TOLERANCE`, `NO_RECEIPT`, `TWO_WAY_NOT_ALLOWED`. `PRICE_OUT_OF_TOLERANCE` is **removed**: a price deviation is never rejected, it is posted as a price-difference line and recorded on the match record (S1, ADR-155) |
| `ORDER` and `CONFIG` (additions of 22, 2026-10-19) | `ORDER.ISSUE_TARGET_INVALID`, `ORDER.ISSUE_TARGET_NOT_FOUND`, `ORDER.ISSUE_DOCUMENT_TYPE_INVALID` (`invoices:record-issue`, 12, 11), `CONFIG.EINVOICE_RULE_SET_UNKNOWN` (`einvoice_rule_set`, 07, 5.5), `CONFIG.BANK_ACCOUNT_IDENTIFIER_INVALID` (a `company_bank_account` with an `iban` and `account_identifier` combination that breaks the rule of its `account_scheme`, ADR-223, 07, 9.5), `CONFIG.NOT_A_GROUP_MEMBER`, `CONFIG.OWNERSHIP_INVALID`, `CONFIG.OWNERSHIP_CYCLE`, `CONFIG.TRANSLATION_METHOD_INVALID` (`company_consolidation_setting_version`, ADR-224, 07, 5.6a), `CONFIG.GROUP_ACCOUNT_OTHER_GROUP` (`account_group_mapping`, ADR-227, 07, 6.4) |
| `EXPORT`, `RETENTION`, `HOLD`, `DSR`, `TENANT` | Compliance commands, 12, section 13: `EXPORT.ALREADY_RUNNING`, `EXPORT.NOT_COMPLETE`, `EXPORT.EXPIRED`, `EXPORT.STORAGE_NOT_CONFIGURED`, `RETENTION.NO_RULE`, `RETENTION.CLASS_HAS_NO_DATE` (`retention:extend` on a class that stores no date, E2), `RETENTION.CANNOT_SHORTEN`, `HOLD.ACTIVE_BLOCKS`, `DSR.NOT_A_PERSON`, `DSR.REFUSED_RETENTION`, `TENANT.DELETION_PRECONDITION_FAILED`, `TENANT.APPROVER_NOT_DISTINCT`. Restricted and anonymised partners: `MASTER.PARTNER_RESTRICTED`, `MASTER.PARTNER_ANONYMISED` |
| `PACKAGE` | Configuration packages, 07, 11.4 and 18, section 7 (ADR-196, ADR-197, ADR-217): `NOT_RELEASED` (a package whose state is not RELEASED on a PRODUCTION tenant), `SIGNATURE_INVALID` (the signature does not verify against the trust set of the tenant purpose, 20, 11), `DIGEST_MISMATCH` (a file digest differs from the signing manifest), `UNKNOWN_FORMAT_VERSION` (an unknown major version of the format), `REQUIRES_NOT_SATISFIED` (a required package is missing or outside its range), `INPUT_INVALID` (an input is missing, of the wrong type or outside the values the package allows), `UNKNOWN_POLICY_KEY` (a key outside the closed catalogue), `POLICY_VALUE_NOT_AVAILABLE` (a value the core does not yet implement, for example `PAYMENT_DATE`, ADR-199), `REFERENCE_UNRESOLVED` (a symbolic reference that resolves to no object, including a package whose tables or columns the core release does not have, for example before ADR-200 and ADR-203 are implemented) |
| `EXT`, `APP`, `EVENT` | Extensions, app registration and the change feed (15, 4.3, 5.3). `EXT`: `NOT_OWNER`, `UNKNOWN_DEFINITION`, `TYPE_MISMATCH`, `VALUE_NOT_ALLOWED`, `REF_INVALID`, `ENTITY_NOT_EXTENDABLE`, `ENTITY_CLOSED`, `ENTITY_ANONYMISED` (`extensions:set` on an ANONYMISED partner, E4), `DEFINITION_LOCKED` (`extension-definitions:update` of a field that is locked once values exist, such as `is_pii`, E3, 15), `DEFINITION_LIMIT`, `COMPANY_OUT_OF_SCOPE`, `VERSION_CONFLICT` (`expected_version` differs from the stored counter of (entity, namespace), E7). `APP`: `NOT_REGISTERED` (a command that requires `app_code` without one, or with an unknown code), `INACTIVE` (an `app_code` of an inactive app, until `apps:reactivate`), `NOT_INACTIVE` (`apps:reactivate` on an app that is already active), `NAMESPACE_TAKEN`. `EVENT`: `POSITION_EXPIRED`, `NOT_SUBSCRIBED`, `LIMIT_EXCEEDED` |
| `READ` | Read and query APIs, 09, section 2.5: `NOT_FOUND`, `INVALID_FILTER`, `SORT_NOT_ALLOWED`, `LIMIT_EXCEEDED`, `CURSOR_INVALID`, `RESULT_TOO_LARGE`, `STALE_SNAPSHOT`, `TIMEOUT`, `CONTAINS_TOO_SHORT`, `CONTAINS_REQUIRES_SCOPE`, `PARTNER_RESTRICTED` |
| `SYSTEM` | `DEADLOCK`, `LOCK_TIMEOUT`, `UNAVAILABLE` |

The domain specs own the meaning of their codes. This table lists the codes that exist in specs 02 to 16 at v0.6 and is the index. Where a spec still writes an older name (`PERIOD.PERIOD_HARD_CLOSED`, `MASTER.NUMBER_SERIES_NOT_ASSIGNED`, `CLOSE.SPECIAL_PERIOD_OPEN`, `TAX.ORIGIN_DESTINATION_MISSING`, `VAT.SUCCESSOR_SETTLEMENT_ACTIVE`, `VAT.NOT_FILED`), the name of this table is in force (13.3). A code that appears in a spec but not here is a defect of this table.

## 3. Validation pipeline

One pipeline for all commands. Each command defines its checks per phase.

| Phase | Name | Blocking | Content |
|---|---|---|---|
| P0 | Authentication and permission | Yes | Identity, tenant, company access, permission for the command |
| P1 | Schema and idempotency | Yes | Structure, types, required fields, unknown fields. Idempotency lookup: replay returns the stored result, conflicting fingerprint rejects |
| P2 | Reference resolution | Per reference | Companies, ledger, accounts, partners, currencies, dimension values, tax codes, referenced documents, number series assignment. An unresolved reference blocks the checks that depend on it, not the checks on other lines |
| P3 | Calendar | No | Posting window, period lookup, year and period status, soft-close authorisation. For intercompany: both companies |
| P4 | Business rules | No | Account rules (partner, due date, dimensions, tax code), dimension validity, tax determination, rate lookup and override tolerance, document-type rules |
| P5 | Derivation | No | Compute LC, GC, RC, tax lines, rounding lines, FX or difference lines. Skipped if P2 or P4 left an input unresolved |
| P6 | Consistency | No | Balance in each currency, cross-line rules, cumulative limits (clearing over-clearing), reversal preconditions |
| P7 | Persist | n/a | Inside one transaction: **locks** in the single order of section 9 (this is the only place where locks are taken, there is no intermediate phase between P6 and P7), then **recomputation** under the locks of everything that depends on mutable state (P5 results for running balances, order progress, line tolerance, and the re-check of period status), number allocation, inserts and S updates, chain head and hash, outbox events (position allocated last), `command_log`. Only conflicts and system errors can occur here |

Rules:
- P0 and P1 errors end the pipeline. P2 to P6 run together and their independent errors are returned in one response, ordered by phase then by request position.
- A check whose inputs are unresolved reports nothing. It does not produce knock-on errors (an unknown account yields `MASTER.ACCOUNT_NOT_FOUND` only, not `ACCOUNT_RULE.*` for the same line).
- A response carries at most 100 errors. If more exist, the 100th is `SCHEMA.TOO_MANY_ERRORS`.
- P7 re-checks anything another transaction could have changed since P3 to P6 (period status including special periods 13+, item still open). **Period-dependent commands** (posting, clearing, reversal, revaluation, L2 change) read `fiscal_period` in share mode in P3 and re-check the status in P7 **after their head locks** (A1). `periods:hard-close` and `year-end:close` lock the LEDGER and CLEARING heads before the PERIOD_EVENT head, so a posting cannot commit into a period that hard-closes concurrently. The revaluation stale check of 05 runs **only at hard-close** of every relevant period, including special periods 13+ (X2, 14 section 10, ADR-148). It does not run on postings. A change since validation yields `CONFLICT.*` or `PERIOD.*` with `retryable` as appropriate.
- Derivation that reads state other commands change (stock running balances, order progress and tolerance, GR/IR open items) runs **after** the P7 locks (D3, ADR-147), as the recomputation step of P7. The P5 result of EXECUTE is recomputed under the locks and replaces any earlier value. There is one phase model: P0 to P6 without locks, P7 locks then recomputation (X7). P5 in VALIDATE mode is a non-binding preview (section 6).
- Deadlocks and lock timeouts in P7 are retried inside the core a bounded number of times. Only if that fails does the caller see `SYSTEM.DEADLOCK` or `SYSTEM.LOCK_TIMEOUT` with `retryable` = true.

## 4. Idempotency and `command_log`

### 4.1 `command_log`

PK: (`tenant_id`, `command_log_id`)
Unique: (`tenant_id`, `idempotency_key`)
Index: (`tenant_id`, `correlation_id`), because `audit-events:query` and the outbox group one operation by `correlation_id` (09, 4.8, 15, 3.7)
Insert only. Written in P7 of every successful EXECUTE command, in the same transaction as the result.

| Column | Type | Null | Notes |
|---|---|---|---|
| tenant_id, command_log_id | ID | N | |
| company_id | ID | Y | Null for tenant-level commands |
| idempotency_key | TEXT(100) | N | |
| command_name | CODE | N | e.g. `journal-documents:post`. Names follow the catalogue (`resource:action`) |
| request_fingerprint | BINARY(32) | N | SHA-256 over the canonical payload (fixed field order, normalised numbers and dates). Excludes `correlation_id`, `mode`, `idempotency_key` |
| actor | TEXT(100) | N | |
| correlation_id | TEXT(100) | N | |
| executed_at | TS | N | UTC |

### 4.2 `command_log_result`

PK: (`tenant_id`, `command_log_id`, `result_seq`)

| Column | Type | Null | Notes |
|---|---|---|---|
| tenant_id, command_log_id | ID | N | |
| result_seq | INT | N | 1..n |
| entity_type | CODE | N | The upper-case name of the created or affected record's table, without prefix. Journal domain: DOCUMENT, CLEARING, PERIOD_EVENT, REVALUATION_RUN, CHANGE_SET, ANCHOR, FISCAL_YEAR. Other domains add their own: `VAT_RETURN` (08), `STOCK_DOCUMENT`, `RESERVATION`, `LANDED_COST_ALLOCATION` (10), `SALES_ORDER`, `PURCHASE_ORDER`, `SALES_DELIVERY`, `PURCHASE_RECEIPT`, `SALES_RETURN`, `PURCHASE_RETURN`, `SALES_INVOICE`, `PURCHASE_INVOICE`, `DROPSHIP_CONFIRMATION`, `IC_ORDER_LINK` (11), `MASTER_CHANGE_SET` (the `change_set_id` of `master_change_log`, 07), `APPLIED_CONFIG_PACKAGE` (07), `MIGRATION_BATCH` (13), `EXTENSION_DEFINITION`, `EXTENSION_VALUE`, `APP_REGISTRATION` (15). The compliance entities (12) follow the same rule |
| entity_id | ID | N | Id of the created or affected record (for DOCUMENT: `document_id`, company is on the log row) |

### 4.3 Semantics

- Same `idempotency_key` and same `request_fingerprint`: the command is not executed again. The response is rebuilt from the referenced records and marked `idempotent_replay` = true. It reflects the current state of those records (e.g. a document that was reversed since shows its new status), which is the correct state to report.
- Same key and a different fingerprint: `IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST`. Nothing runs.
- A rejected command leaves no record. Fixing the request and retrying with the same key is allowed and the request is evaluated afresh.
- Concurrent execution of the same key: the unique constraint serialises them. The second waits, then replays.
- Commands that create several records of one entity type (intercompany pair, revaluation with reversal, rerun) store derived keys on the records: the entities' own unique `idempotency_key` columns get `<key>#1`, `<key>#2` and so on, in result order. The base key lives only in `command_log`. A command that derives keys rejects a base key longer than 90 characters with `SCHEMA.INVALID_VALUE`, so the derived key fits the entity's TEXT(100) column.
- The entities' unique keys (documents, clearings, period events, runs) remain as the last line of defence against double posting, even after `command_log` entries are purged (Q2).

## 5. Commands on journal documents

### 5.1 Catalogue

| Command | Purpose | Permission | Tables written |
|---|---|---|---|
| `journal-documents:post` | Post a complete document from base lines | `journal.post` | `journal_document`, `document_legal_snapshot`, `journal_line`, `journal_line_dimension`, `journal_line_tax`, `journal_line_tax_base`, `journal_line_open_item`, `number_range_counter`, `chain_head` (chain type LEDGER), `outbox_event`, `command_log` |
| `journal-documents:reverse` | Reverse a posted document 1:1 (only documents no other command owns, 5.3) | `journal.reverse` | `journal_document` (new, plus S update on the original), `journal_line*` mirrored, `clearing` and cleared S columns for the automatic original-to-mirror clearing of open-item lines (5.3), counters, `chain_head`, `outbox_event` |
| `journal-documents:change-open-fields` | Change L2 fields | `journal.change` | L2 columns, `journal_line_dimension`, `change_log`, `outbox_event` |
| `journal-documents:post-batch` | Post many independent documents in one call, each its own atomic command (5.6) | `journal.post` | As `post`, once per posted item |
| `intercompany-documents:post` | Post a linked pair of documents (ADR-016) | `intercompany.post` | As `post`, for two companies |
| `intercompany-documents:reverse` | Reverse both sides atomically (ADR-016). Owning command of IC documents (5.3) | `intercompany.post` | As `reverse`, for two companies |

The commands of the other domains and their permissions are in the map of section 8.2. The reverse commands for credit notes (`sales-credit-notes:reverse`, `purchase-credit-notes:reverse`) and landed costs (`landed-costs:reverse`) are not in this catalogue: they are listed in 5.3 (owning commands), 7.4 (commands of the other domains) and 8.2 (permissions).

### 5.2 `journal-documents:post`

Request payload:

| Field | Req | Notes |
|---|---|---|
| document_type | Req | Allow-list for external callers: `JOURNAL`, `AP_INVOICE`, `AP_CREDIT`, `AR_INVOICE`, `AR_CREDIT`, `PAYMENT`. System types (`REVALUATION`, `REVAL_REVERSAL`, `YEAR_END`, `CLEARING`, `CLEARING_RESET`, `VAT_SETTLE`, the `STOCK_*` types, `DROPSHIP_COST`, `GR_IR_WRITE_OFF`, `HISTORY_SUMMARY`, `CLOSING`, `OPENING`, `OPENING_ITEMS`, `STOCK_OPENING`, and similar) are produced only by their own commands (Q9). `CLOSING` is the system-generated closing-entry document of a migrated history year, set by the HISTORY chunk of `migration-batches:commit` (X1, 02, 13). `IC` is not a document type (decided, 14 section 10): an IC document is a document of any allow-listed type that carries `ic_transaction_id` (typically `AR_INVOICE` or `AP_INVOICE`, the type of its side, 5.5). 02 and 03 must drop the `IC` type where they still list it (14, section 3) |
| ledger_id | Opt | Defaults to the company's single ledger in R1 |
| document_date | Req | |
| posting_date | Req | |
| special_period | Opt | Special period number (13+). Posting date must lie in period 12's range |
| transaction_currency | Req | One per document (ADR-035) |
| header_text | Opt | |
| external_reference | Opt | |
| tax_date | Opt | Default tax point for the lines. Default: `document_date` (08, 5.1) |
| origin_country | Opt | Default origin of the goods or service for tax determination. Default if omitted: company country for sales types, partner country for purchase types (07, Q1). Without a `partner_id` there is no default: a taxed base line then needs explicit origin and destination, else `TAX.ORIGIN_DESTINATION_REQUIRED` (07, 8.4, F1) |
| destination_country | Opt | Default destination. Default if omitted: partner country for sales types, company country for purchase types |
| source_document_ref | Opt | Caller's own document reference (P13) |
| origin | **Never sent** | `journal_document.origin` (L1) is set by the core from the command that creates the document: EXTERNAL (`journal-documents:post`, `:post-batch`, `intercompany-documents:post`), ORDER (order-management commands), STOCK, VAT, MIGRATION, SYSTEM (revaluation, clearing, year-end). A request that carries it is rejected (`SCHEMA.UNKNOWN_FIELD`). `journal-documents:post` accepts only `document_type` of the allow-list above and always writes `origin` = EXTERNAL (X5) |
| partner_id | Req for `AP_INVOICE`, `AP_CREDIT`, `AR_INVOICE`, `AR_CREDIT`. Opt otherwise | Header partner. The core builds the legal snapshot from the partner's master data as of the posting date. The caller cannot supply or override snapshot fields (Q6 decided) |
| lines[] | Req | At least 2 base lines |

`lines[]` item:

| Field | Req | Notes |
|---|---|---|
| line_ref | Req | Caller's reference, unique in the request. Echoed back in the response. The core assigns `line_no` in request order |
| account_id | Req | |
| amount_tc | Req | Signed, debit positive, credit negative (A1 of 02) |
| line_text | Opt | |
| tax_class_id | Opt | Tax class of the material or service. Default: the account's default class. Input to tax determination (07, 8.4) |
| tax_date | Opt | Overrides the document's tax point for this line (08, Q8) |
| origin_country, destination_country | Opt | Override the document defaults for this line (for example a drop-shipped line from another country). Input to tax determination |
| tax_code_id | Opt | Omitted: the core determines it by rules (ADR-026). Supplied: validated, a different supplied code is accepted only as a permitted alternative |
| tax_amount_tc | Opt | Expected tax for this base line. Must match the computed amount within the configured tolerance (ADR-027). It is the **full** tax of the supplier document: with partial deduction (19) the check and the difference to the tax rounding account apply to the full tax before the split |
| deductible_tax_amount_tc | Req for codes with `deduction_cap` = SUPPLIED_AMOUNT, forbidden otherwise | The deductible tax of the line, signed like `amount_tc`. Checked at P4 against `0 <= value <= ROUND(computed tax x deductible_pct / 100)`. The deductible tax is `ROUND(value x key rate / 100)` (19, 3.3, 6.2). The core derives the non-deductible part and its lines |
| rate_override | Opt | `{lc, gc, rc}` rates. Within tolerance of the table rate (ADR-019). Both stored on the line |
| dimensions | Opt | `cost_center_id`, `project_id`, `profit_center_id`, `business_unit_id`, `free[]` of `{dimension_type_id, dimension_value_id}` |
| open_item | Req for CONTROL and GL_OPEN_ITEM accounts, forbidden otherwise | `partner_id` (CONTROL: Req), `due_date` (CONTROL: Req), `baseline_date`, `payment_terms_id`, `discount_due_date`, `discount_amount_tc`, `payment_block`, `dunning_block`, `payment_reference`, `bank_account_id` |

What the caller never sends: tax lines, rounding lines, local, group and reporting amounts, document number, period, chain fields. Tax is computed on lines that carry a tax code (or for which rules determine one). The caller supplies the gross control-account line (receivable, payable) and the net base lines. The core adds tax lines and rounding lines and checks the balance.

Pipeline content:

| Phase | Checks |
|---|---|
| P0 | Permission `journal.post`. Company access |
| P1 | Schema. `line_ref` unique. Amount scale per currency. Idempotency |
| P2 | Company, ledger, document type allow-list, accounts (exist, active at posting date, posting allowed), partners, dimension values, tax codes, currency, series assignment for (company, document type, ledger) |
| P3 | Posting window. Period lookup, or explicit special period. Year OPEN. Period status: OPEN, or SOFT_CLOSED with `post.soft_closed` permission (then `soft_close_override` = true and a warning is returned) |
| P4 | Per line: partner and due date rule by open-item mode, dimension rules (mandatory, forbidden), dimension value validity at posting date, tax code rule of the account (ADR-029), tax determination (ADR-026, with the derived keys `origin_eu` and `destination_eu`, ADR-200), rate lookup with ADR-019 fallback, override tolerance. Deduction share (ADR-203): `deductible_pct`, `deduction_key` and its rate at the tax date, the supplied deductible amount (`TAX.DEDUCTION_*`) |
| P5 | Derive LC, GC, RC per line with rates. Compute tax lines and `journal_line_tax`, `journal_line_tax_base`. Rounding per company setting (ADR-027). Split of every input tax line into the deductible part (the tax line) and the non-deductible part: `TAX_NONDEDUCTIBLE` lines on the base line accounts with the allocation in `journal_line_tax_base` (ADR-204, 19, 3.3 and 4). Rounding lines per currency to configured accounts (design principle 4 of 00, not pipeline phase P4) |
| P6 | Balance in TC, LC, GC, RC. Header and line consistency, including the allocation check of the non-deductible tax (02, 7.2). Legal snapshot completeness for document types that require one |
| P7 | Lock order of section 9. Allocate `sequence_no` and `document_number`. Insert document, snapshot, lines and detail tables. Update the `chain_head` row and hash. Outbox events, `command_log` |

Response `result`:

| Field | Notes |
|---|---|
| document | Header: `document_id`, `document_number`, `sequence_no`, `fiscal_year`, `fiscal_period`, dates, status `posted`, `soft_close_override`, `chain_seq`, `row_hash` |
| lines[] | All lines including generated ones: `line_no`, `line_ref` (null for generated), `line_type`, account, TC, LC, GC, RC amounts and rates, tax detail where relevant |
| warnings[] | Rate fallback used, tax code differs from determined code, soft-close override used |

### 5.3 `journal-documents:reverse`

| Field | Req | Notes |
|---|---|---|
| document_id | Req | |
| reversal_date | Opt | Default: the original posting date. If that date is not postable the command is rejected (`PERIOD.*`) and the caller resubmits with a later date in an open period (ADR-022). The core never shifts the date (Q8) |
| reason_code | Req | `REVERSAL.REASON_REQUIRED` if missing. Closed list of six: AUTO_REVERSAL, RERUN, REOPEN, CORRECTION, ERROR, RETURN. `journal-documents:reverse` accepts only CORRECTION and ERROR from external callers and rejects AUTO_REVERSAL, RERUN, REOPEN and RETURN with `REVERSAL.REASON_NOT_ALLOWED` (params: `allowed_values`). The other codes are set by the system commands that produce mirror documents (AUTO_REVERSAL: revaluation reversal, RERUN: rerun, REOPEN: `year-end:reopen`, RETURN: returns and credit notes. `migration-batches:rollback` uses CORRECTION, B4). The list is closed (Q20, decided) |
| reason_text | Opt | |

Preconditions (P2 and P6): document exists and has status `posted`. **Reversal only through the owning command (D1, ADR-145).** This command reverses only documents of the external allow-list types (5.2: JOURNAL, AP_INVOICE, AP_CREDIT, AR_INVOICE, AR_CREDIT, PAYMENT) that were posted through `journal-documents:post`, that is documents with `journal_document.origin` = EXTERNAL (X5). The test is `origin`, set by the core, never the caller's `source_system`. Every other document (`origin` other than EXTERNAL, or a system type) is rejected with `REVERSAL.TYPE_NOT_REVERSIBLE`, with the owning command in `params.owning_command`:

| Document | Owning command |
|---|---|
| CLEARING (difference document) | `clearings:reset` (7.1). A CLEARING_RESET document is itself not reversible: a reset is undone by a new clearing |
| REVALUATION, REVAL_REVERSAL | `revaluation:run` (a rerun, 05) |
| STOCK_* documents (`origin` = STOCK) | `stock-documents:reverse` (10, 5.5), or the order command that created the stock document (11). Return documents: `sales-returns:reverse`, `purchase-returns:reverse` (B5). Mirror stock type STOCK_REVERSAL |
| DROPSHIP_COST | `dropship-confirmations:reverse` (11). Rejected with `ORDER.DELIVERY_RETURNED` when returns exist (B6) |
| AR and AP invoice documents built by order management (`origin` = ORDER) | `sales-invoices:reverse`, `purchase-invoices:reverse` (11) |
| AR and AP credit documents built by order management | `sales-credit-notes:reverse`, `purchase-credit-notes:reverse` (adopted, D1 follow-up, 14 section 11, contract in 11). Errors `ORDER.CREDIT_NOTE_NOT_FOUND`, `ORDER.CREDIT_NOTE_ALREADY_REVERSED`. An invoice with credit notes cannot be reversed until they are reversed (`ORDER.INVOICE_HAS_CREDIT_NOTES`) |
| Landed cost allocation documents | `landed-costs:reverse` (adopted, D1 follow-up, 14 section 11, contract in 10). Allowed only if no later movement exists on the cost key since the allocation (B7). The allocation gets `reversal_of_allocation_id`, `reason_code` and a status (10). Errors `INVENTORY.ALLOCATION_NOT_FOUND`, `INVENTORY.ALLOCATION_ALREADY_REVERSED`, `INVENTORY.ALLOCATION_NOT_REVERSIBLE`. Mirror stock type STOCK_REVERSAL |
| IC documents (any document type carrying `ic_transaction_id`) | `intercompany-documents:reverse` for postings of `intercompany-documents:post`. Order-built IC pairs: `ic-deliveries:reverse`, `ic-invoices:reverse` (11, 6.7) |
| VAT_SETTLE (`origin` = VAT) | `vat-returns:reverse-settlement` (renamed from `vat-settlements:reverse`, 14 section 11, contract in 08, 7.3a). Only before filing: after the return is filed it rejects with `VAT.ALREADY_FILED` (D9, ADR-153), without an active settlement with `VAT.NOT_SETTLED`, on a superseded return with an active successor settlement with `VAT.SUPERSEDED` (B9) |
| HISTORY_SUMMARY, CLOSING, OPENING, OPENING_ITEMS, STOCK_OPENING (`origin` = MIGRATION) | `migration-batches:rollback` (13, 7), before the seal only. A mirror-producing command with reason code CORRECTION. The mirror keeps the original type, including CLOSING (B4, X1). `MIGRATION.ROLLBACK_NOT_ALLOWED` otherwise |
| YEAR_END | `year-end:reopen` (7.3) only. A history year's CLOSING document is not YEAR_END and is reversed only by the rollback |
| CLOSING (`origin` = MIGRATION, system generated, set by the HISTORY chunk, X1) | `migration-batches:rollback` (13, 7), as the row above. Included in balance reads, excluded from P&L reads like YEAR_END (09) |
| GR_IR_WRITE_OFF (`origin` = ORDER, new document type of `gr-ir:write-off`) | `gr-ir:reverse-write-off` (17, section 5, 11, 7.5). Owning command of the type. Takes `reason_code` CORRECTION or ERROR. Mirror keeps the type GR_IR_WRITE_OFF (the type has no mirror type of its own). Not reversible through `journal-documents:reverse` |

No line has cleared amounts (`REVERSAL.HAS_CLEARED_LINES`, ADR-039). **Open-item lines (B3):** the mirror document creates mirror open items. In the same transaction the core clears each mirror open item against its original automatically (a clearing record without a clearing document, so no difference line), and the original items stay CLEARED by that record. The result is a new document that mirrors every line 1:1 (ADR-048) with opposite sign, `reversed_line_no` set, `reversal_of_document_id` set, own number and chain node. The original gets `status` = `reversed` and `reversed_by_document_id` (S columns, ADR-031). Response: the reversal document in the same shape as `post`.

**Mirror documents of all owning commands** follow the same rule: every document that mirrors another (REVERSAL, REVAL_REVERSAL, CLEARING_RESET, the stock and order reversal documents, the VAT settlement reversal, rerun reversals, the YEAR_END reopen mirror, the rollback mirrors) sets `reversal_of_document_id`, `reversed_line_no` and a reason code. **Mirror type rule (14 section 11):** the four existing mirror types stay: REVERSAL (mirror of a JOURNAL or an external-type document), REVAL_REVERSAL, CLEARING_RESET and STOCK_REVERSAL. A mirror keeps the `document_type` of the original only for types without a mirror type of their own: YEAR_END, VAT_SETTLE, DROPSHIP_COST, GR_IR_WRITE_OFF, the migration types (HISTORY_SUMMARY, CLOSING, OPENING, OPENING_ITEMS, STOCK_OPENING) and the invoices and credit notes built by order management (a mirror of an order-built AR_INVOICE is an AR_INVOICE). A REVALUATION may have several REVAL_REVERSAL mirrors (a rerun, B1): `reversed_by_document_id` of the original is the first one, and a rerun mirrors only an original that has no automatic reversal yet (05). Reason codes are the closed list AUTO_REVERSAL, RERUN, REOPEN, CORRECTION, ERROR, RETURN (D1 widens the rule of 02 that allows `reversal_of_document_id` only for REVERSAL and REVAL_REVERSAL). The owning command takes the reason code in its own payload. A YEAR_END document is therefore never reversed by `journal-documents:reverse`. How the reopen mirror is treated in the P&L of the closed year is a rule of 05 (`year-end:reopen`).

### 5.4 `journal-documents:change-open-fields`

| Field | Req | Notes |
|---|---|---|
| document_id | Req | |
| changes[] | Req | At least one |
| reason_code | Req | Mandatory for L2 changes (P3) |
| reason_text | Opt | |

`changes[]` item:

| Field | Req | Notes |
|---|---|---|
| entity_type | Req | `DOCUMENT`, `LINE`, `LINE_DIMENSION`, `TAX_EVIDENCE` (08, 5.3: `sub_key` = `evidence_no`, new rows by `expected_old_value` null) |
| line_no | Req for LINE, LINE_DIMENSION and TAX_EVIDENCE | For TAX_EVIDENCE: the tax line the evidence belongs to (F2) |
| sub_key | Req for LINE_DIMENSION and TAX_EVIDENCE | LINE_DIMENSION: `dimension_type_id`. TAX_EVIDENCE: `evidence_no` (a new number for a new row) |
| field_name | Req | Physical column name from the L2 list below |
| expected_old_value | Req | The value the caller saw, in canonical text form. `null` for an empty field |
| new_value | Req | `null` clears the field (for free dimensions: tombstone, ADR-034) |

Changeable fields (L2): header `header_text`, `external_reference`. Line `line_text`, `cost_center_id`, `project_id`, `profit_center_id`, `business_unit_id`. Open-item `baseline_date`, `payment_terms_id`, `due_date`, `discount_due_date`, `discount_amount_tc`, `payment_block`, `dunning_block`, `payment_reference`, `bank_account_id`. Free dimension `dimension_value_id`. Tax evidence (entity `TAX_EVIDENCE`, F2): `evidence_type`, `reference`, `evidence_date`, `note`. Evidence is **append-only**: a change adds a new row (`expected_old_value` null for each field). A change to an existing evidence row rejects with `TAX.EVIDENCE_IMMUTABLE`, in every period status. Every other field rejects with `LOCK.FIELD_LOCKED_L1` or `LOCK.FIELD_UNKNOWN`. S columns cannot be changed here at all.

Pipeline content: P2 document exists, lines exist. P3 period of the document header: OPEN allowed, SOFT_CLOSED needs `change.soft_closed` (recorded as `period_status` and authorisation on the log rows), HARD_CLOSED rejects (`LOCK.FIELD_LOCKED_L3`, L3). **Exception (F2):** a command whose `changes[]` contains only new TAX_EVIDENCE rows is accepted on a document of a HARD_CLOSED period, as a controlled exception under `journal.change`. It writes `change_log` rows and its own audit event (name in 08 and 15) and never touches L1 data. Any other change in the same command rejects the whole command. P4 changed dimension values are validated like a posting: account rules (mandatory, forbidden), dimension validity at the posting date of the document. `due_date` stays mandatory on CONTROL items. P6 all `expected_old_value` checks. In P7 the rows are re-read under lock and compared again. A mismatch rejects the whole command with `CONFLICT.EXPECTED_VALUE_MISMATCH` and the current value in `params` (all-or-nothing, no partial change).

Result: one `change_set_id`, and one `change_log` row per changed field (02, section 8). An unchanged field (`expected_old_value` equals `new_value`) is ignored with a warning, and a command with no effective change is rejected as `SCHEMA.NO_EFFECTIVE_CHANGE`.

### 5.5 `intercompany-documents:post`

Payload: `posting_date`, `document_date`, `transaction_currency`, `ic_reference`, and a `sides[]` array of exactly two entries, each with `company_id`, `document_type`, `ledger_id`, `lines[]` as in 5.2. Each side carries its own header text and reference.

Rules (ADR-016): both companies' calendar checks (P3) must pass for the shared posting date, no shifting. The group-currency amount is computed once and copied to both sides, local amounts use each company's rates. IC accounts per counterpart company come from configuration and are generated, not supplied. Both documents share `ic_transaction_id` (every document type that carries it is an IC document) and use derived idempotency keys `<key>#1` and `<key>#2`. Locks in the order of section 9, sorted across both companies by the tuple (chain type, company, scope_key): the two LEDGER heads are locked in that order (X4). The caller needs access to both companies (`AUTH.COMPANY_NOT_ALLOWED`). Marker (ADR-226, 02, 5.4): the command sets `ic_counterpart_company_id` on the generated IC account lines, the control lines and the BASE lines of both documents, never on tax or rounding lines. The caller cannot supply it. Result: both documents in the shape of 5.2. `intercompany-documents:reverse` takes `ic_transaction_id`, `reversal_date`, `reason_code`, and reverses both atomically. Both periods must be open for the reversal date.

### 5.6 `journal-documents:post-batch`

A convenience wrapper that runs `journal-documents:post` for many documents in one call (Q5 decided). It adds no new accounting rule: **every item is an ordinary, independent, atomic `post` command.**

Request envelope exceptions: `context.idempotency_key` is not used on the batch itself. Each item carries its own key, so a batch is retried by simply sending it again. `context.company_id` applies to all items (one company per batch).

Request payload:

| Field | Req | Notes |
|---|---|---|
| items[] | Req | 1 to N items, N the tenant setting `max_batch_items` (07, 5.2, default 500, ADR-062). Larger batches reject as a whole with `SCHEMA.BATCH_TOO_LARGE` |
| stop_on_error | Opt | Default false. If true, processing stops after the first rejected or failed item, and the remaining items return `NOT_PROCESSED` |

`items[]` item:

| Field | Req | Notes |
|---|---|---|
| item_ref | Req | Caller's reference, unique in the batch. Echoed in the response |
| idempotency_key | Req | The item's own key. Same rules as 4.3 |
| payload | Req | Exactly the payload of `journal-documents:post` (5.2) |

Semantics:
- **P0 and P1 on the envelope** (permission, schema, size, `item_ref` uniqueness) run once. An error there rejects the whole batch before any item is touched.
- **Items are processed one by one in array order**, each in its own transaction. Numbers and chain order therefore follow array order. Locks are released between items. There is no all-or-nothing batch and no batch-wide transaction (Q11).
- **Each item has its own pipeline P0 to P7**, its own errors, and its own `command_log` entry on success. There is no batch-level log entry. `context.correlation_id` is shared by all items.
- An item that is rejected leaves no trace, as with a single command. Retrying the batch replays the items already posted (`idempotent_replay` = true) and evaluates the others afresh.
- Items do not see each other during validation. A rule that depends on an earlier item of the same batch applies as if that item were posted before, because it is (sequential processing).

Response `result`:

| Field | Notes |
|---|---|
| summary | `total`, `posted`, `rejected`, `failed`, `not_processed` |
| items[] | In request order. Each: `item_ref`, `status` (`OK`, `REJECTED`, `FAILED`, `NOT_PROCESSED`), `idempotent_replay`, and either `result` (as in 5.2) or `errors[]`/`error` as in section 2.2 |

The envelope `status` is `OK` whenever the batch was processed, even if items were rejected. Callers must read `summary`. A `VALIDATE` batch validates every item and returns the same shape, with no persistence.

The same wrapper pattern is used by `{resource}:upsert-batch` for master data (07, 11.5) and by `extensions:set-batch` (15, 4.3). Each has its own limit setting and the permission of the wrapped command.

## 6. VALIDATE mode

`context.mode` = `VALIDATE` runs P0 to P6 exactly as in EXECUTE and stops. It writes nothing, consumes no number, no `command_log` entry and no idempotency key (Q1).

Success response: `status` = `OK`, `result` = what would be posted. For `journal-documents:post` that is the full derived document with all lines, tax lines, rounding lines, rates and warnings, with `document_number`, `sequence_no`, `chain_seq`, `row_hash` null. Errors are returned as in EXECUTE, with the same codes.

Guarantee: a successful validation is not a promise. Concurrent changes (a period closed, an item cleared, a rate table update) can make the later EXECUTE fail. The response is stamped with `validated_at`.

Supported for every command that creates or changes transaction data. For `revaluation:run` the VALIDATE result is the preview of 7.2. VALIDATE writes no outbox events (15, 5.1).

## 7. Commands on clearing, revaluation, periods and year-end

### 7.1 Clearing

| Command | Permission | Payload | Result |
|---|---|---|---|
| `clearings:post` | `clearing.post` | `account_id`, `partner_id` (null only for GL_OPEN_ITEM), `clearing_date`, `clearing_method`, `matching_ref`, `note`, `items[]` of `{open_item_document_id, open_item_line_no, cleared_amount_tc}` (`cleared_amount_tc` optional, default: the full remaining amount) | `clearing` (id, `chain_seq`, `row_hash`), `items[]` with derived LC, GC, RC, the S status of each item after clearing, and `document` (the difference document in the shape of 5.2) when one was posted |
| `clearings:reset` | `clearing.reset` | `clearing_id`, `clearing_date`, `reason_code` (CORRECTION or ERROR from external callers, else `REVERSAL.REASON_NOT_ALLOWED`), `reason_text` | The reset `clearing` with mirrored items, and the CLEARING_RESET document (with `reversal_of_document_id` = the difference document, D1) if the original had a difference document |

Pipeline content for `clearings:post` (03, section 5): P0 permission `clearing.post`. P2 items exist and are open items, account and partner resolved. P3 `clearing_date` against window, period, year (same rules as a posting). P4 all items share account and partner, none fully cleared (`CLEARING.NOT_AN_OPEN_ITEM`, `ITEMS_NOT_SAME_ACCOUNT`, `ITEMS_NOT_SAME_PARTNER`). P5 allocation of LC, GC, RC, residual lines (ADR-038). P6 cumulative cleared amount within the line amount (`CLEARING.OVER_CLEARING`), TC nets to zero for same-currency sets (`CLEARING.TC_DIFFERENCE`, ADR-038). P7 lock order of section 9 (counters before chain heads, chain heads by (chain type, company, scope_key), open items sorted by id). Clearing performs no rate lookup and does not touch `exchange_rate.first_used_at` (D9, ADR-153). A competing clearing on the same item makes the loser fail with `CONFLICT.ITEM_ALREADY_CLEARED`, `retryable` = true.

`clearings:reset` rejects with `CLEARING.ALREADY_RESET` and when a reset would give an item a negative cleared amount. With a difference document D, the reset has no item on D's offset line, one item on the reset's own offset line, and D's offset becomes CLEARED by the reset (B2, exact item list with amounts in 03).

### 7.2 Revaluation

| Command | Permission | Payload | Result |
|---|---|---|---|
| `revaluation:run` | `revaluation.run` | `fiscal_year`, `fiscal_period` (regular) | `revaluation_run` (id, `run_no`, `item_count`, `items_digest`), the REVALUATION and REVAL_REVERSAL documents (shape of 5.2), and, for a rerun, the four reversal documents (owning command of these documents, 5.3) |
| `revaluation:preview` | `revaluation.run` | Same | Side-effect free. `items_digest`, `item_count`, totals of adjustment per currency, and `matches_active_run` (true if the digest equals that of the ACTIVE run) |

Rules: 05, sections 3.4 and 3.6. `revaluation:run` locks the LEDGER and the CLEARING head (A5). A run can become stale through later postings or clearings, and this is caught only by the stale check at hard-close (X2). Errors `REVAL.PERIOD_NOT_OPEN`, `REVAL.NEXT_PERIOD_NOT_OPEN`, `REVAL.NEXT_YEAR_NOT_OPENED`, `REVAL.CLOSING_RATE_MISSING`. `revaluation:run` is idempotent per key as any command. A second run for the same period without a new key is a rerun by design, not a duplicate.

### 7.3 Periods, years, anchors

| Command | Payload | Permission | Notes |
|---|---|---|---|
| `fiscal-years:open` | `fiscal_year`, optional `start_date` and `calendar_id` (first year of a company only: `first_period_no` derived from `start_date`) | `period.admin` | 04, section 7.3 |
| `fiscal-years:open-history` | See 13, 4.2 | `history.open` | Opens history fiscal years for a migration (D10, ADR-154: replaces `migration.manage` of 13 for this command). It opens history years only. `history_until_period` is not written here: it is a parameter of `migration-batches:open` (D8, 13, 7.1) |
| `fiscal-calendars:create`, `:update` (resource commands of `fiscal_calendar`, 04, 3.1) | Fields per 04, `expected_version` on update | `period.admin` | F3. Affects only years opened later |
| `posting-windows:create` | `earliest_posting_date`, `latest_posting_date`, `effective_from`, `reason_code` (04, 4.1) | `period.admin` | Insert only (F3) |
| `number-series:create`, `:update` | Fields per 04, 5.1, `expected_version` on update | `period.admin` | `create` also creates the `number_range_counter` rows for every fiscal year already open (04, 6). Format fields reject once numbers are issued (F3) |
| `number-series-assignments:create` | `document_type`, optional `ledger_id`, `series_id`, `effective_from` (a DATE, the fiscal year start, A3) | `period.admin` | Insert only. Series are resolved by the posting date's fiscal year. The continuity rule of 04, 5.2 applies (F3, A3) |
| `periods:soft-close` | `fiscal_year`, `period_no` | `period.admin` | OPEN to SOFT_CLOSED. Rejected on an `is_history` period before the seal with `PERIOD.HISTORY_YEAR` |
| `periods:hard-close` | `fiscal_year`, `period_no` | `period.hard_close` | `CLOSE.PREDECESSOR_NOT_HARD_CLOSED`, `CLOSE.REVALUATION_STALE`, `CLOSE.REVALUATION_MISSING`. Locks the LEDGER and CLEARING heads before the PERIOD_EVENT head (A1). Writes an anchor **request** (state PENDING) in the transaction, the timestamp token is fetched after commit (STAMPED, A2, ADR-046). The stale check runs here and only here, and also covers special periods 13+ (X2, D4, ADR-148). Rejected on an `is_history` period before the seal with `PERIOD.HISTORY_YEAR` |
| `periods:reopen` | `fiscal_year`, `period_no`, `target_status`, `reason_code`, `reason_text` | `period.reopen` | Reason code and text mandatory (ADR-023). `PERIOD.HISTORY_YEAR_SEALED` on a period of a sealed history year. `PERIOD.CLOSING_PERIOD_MANAGED` on the closing period, `PERIOD.REOPEN_LATER_PERIOD_CLOSED` while a later period is HARD_CLOSED (04, 7.3). `periods:soft-close` and `periods:hard-close` also reject the closing period with `PERIOD.CLOSING_PERIOD_MANAGED` |
| `year-end:close` | `fiscal_year` | `year_end.close` | 05, section 5.1. `CLOSE.YEAR_NOT_OPEN`, `CLOSE.RETAINED_EARNINGS_INVALID`. Locks as `periods:hard-close` (A1) and writes the anchor request after commit (A2). Requires every non-closing special period HARD_CLOSED (`CLOSE.SPECIAL_PERIOD_NOT_CLOSED`) and the closing period OPEN (`CLOSE.CLOSING_PERIOD_NOT_OPEN`) (A4). `CLOSE.YEAR_NOT_ALL_CLOSED`, `CLOSE.CLOSING_PERIOD_MISSING`. Rejected on a history year before seal with `PERIOD.HISTORY_YEAR` (the migration seal runs the migration variant instead, 13, 9.1). Writes PERIOD_HARD_CLOSE events for the closing period and the year in addition to YEAR_END_CLOSE (D4, ADR-148) |
| `year-end:reopen` | `fiscal_year`, `reason_code`, `reason_text` | `year_end.reopen` | `CLOSE.YEAR_NOT_CLOSED`, `CLOSE.REOPEN_LATER_YEAR_CLOSED`, `CLOSE.REOPEN_BLOCKED_BY_POSTINGS`. Order inside the transaction: reopen the closing period, set the year to OPEN, then post the mirror (A4). Reason mandatory. The only command that reverses a YEAR_END document (5.3, D1). `PERIOD.HISTORY_YEAR_SEALED` on a sealed history year |
| `anchors:create` | `chain_type`, optional `scope_key` | `anchor.create` | Also run by a daily scheduler under a service identity (ADR-046). Writes an anchor request (PENDING) and fetches the timestamp token after commit (STAMPED, `timestamp_token` null until then, A2). Chain types and `scope_key`: see below |
| `vat-returns:prepare` | See 08, 7.1 | `vat.prepare` | VAT domain. Error area `VAT`. VALIDATE mode supported (`vat-returns:preview`) |
| `vat-returns:record-filing` | See 08, 7.2 | `vat.file` | Records the filing once. No command reverses a filing (D9) |
| `vat-returns:settle` | See 08, 7.3 | `vat.settle` | Posts the VAT_SETTLE document |
| `vat-returns:reverse-settlement` | `return_id`, `reversal_date`, `reason_code` (CORRECTION or ERROR), `reason_text` (08, 7.3a) | `vat.settle` | Owning command of VAT_SETTLE (5.3), replaces `vat-settlements:reverse`. **No date shifting** (X8, Q8): a `reversal_date` that is not postable rejects with `PERIOD.*` or `VAT.SETTLEMENT_DATE_NOT_POSTABLE`. Only before filing, `VAT.ALREADY_FILED` after (D9, ADR-153). `VAT.NOT_SETTLED` if the return has no active settlement, `VAT.SUPERSEDED` on a superseded return with an active successor settlement (B9). Event `vat.settlement_reversed` |
| `chains:verify` | `chain_type`, optional `scope_key`, `from_seq`, `to_seq` | `chain.verify` | Read-only: writes no event, no run record and no `command_log` entry (the event `chain.verified` and a "last verification run" are dropped, decided). Returns `OK` or the first broken sequence number with the kind of break (hash, link, gap, balance, number continuity, S column mismatch, digest mismatch), plus a warning for every anchor still PENDING beyond its deadline (A2) |

**Chain types (K1, ADR-158).** All chains of a company are rows of one `chain_head` table keyed (`tenant_id`, `company_id`, `chain_type`, `scope_key`). Six chain types exist: LEDGER, CLEARING, PERIOD_EVENT, VAT_RETURN, STOCK, FULFILMENT. `scope_key` is the `ledger_id` for LEDGER and empty otherwise. The VAT_RETURN chain is **one per company over all return types**, so its `scope_key` is empty (14 section 10, K1, 08). `anchors:create` and `chains:verify` take one chain type and the optional `scope_key` (it replaces the former `ledger_id` parameter): required only for LEDGER, where omitting it means all ledgers of the company, ignored when the chain type has no scope. One verification routine serves all types, and the daily anchor run covers all chains of the company.

Period commands return the new status and the `period_event` (id, `event_seq`, `row_hash`). Their VALIDATE result lists each precondition and whether it holds.

### 7.4 Commands of the other domains

The field-level contracts are in the domain specs. All use the envelope, idempotency, error model, pipeline and VALIDATE mode of this spec. Permissions are in the map of 8.2.

| Domain | Commands (contract) |
|---|---|
| Configuration and master data | `{resource}:create`, `:update`, `:create-version`, `:deactivate`, `:reactivate`, `{resource}:upsert-batch`, `config-packages:apply` (payload and checks in 07, 11.4), `config-packages:diff` (read-only, no `command_log` entry and no event, as `chains:verify`), `companies:activate`, `companies:check-readiness` (07, section 11), `exchange-rates:mark-used` (07, 7.3, ADR-225). Calendar, posting-window and number-series commands are in 7.3 (`period.admin`) |
| Inventory | `stock-documents:post`, `:reverse`, `reservations:create`, `:adjust`, `:release`, `landed-costs:allocate`, `landed-costs:reverse` (10, 6.1, 6.8, B7). `landed-costs:reverse` rejects when a later movement exists on the cost key since the allocation. The allocation carries `reversal_of_allocation_id`, `reason_code` and a status, the mirror stock type is STOCK_REVERSAL (10). A FIFO receipt reversal checks the receipt layer's remaining quantity (`INVENTORY.RECEIPT_CONSUMED`, B8). `cost_source` (COMPUTED, SUPPLIED, INVOICE, MIGRATION) is a column of the **movement line**, not of the document header (D6, ADR-150, 10). A partly invoice-covered receipt is split into an INVOICE line and a COMPUTED line, returns are COMPUTED |
| Orders | The commands of 11, 6.1, `sales-credit-notes:reverse` and `purchase-credit-notes:reverse` (11), **new at v0.5:** `sales-returns:reverse` (`sales.return`), `purchase-returns:reverse` (`purchase.return`) and `gr-ir:write-off` (`purchase.invoice`), **new at v0.6:** `gr-ir:reverse-write-off` (`purchase.invoice`, owning command of GR_IR_WRITE_OFF, 17 section 5), contracts in 11 (7.5, 7.5a), and `change-line-dates` (16, 5.2). `sales-deliveries:reverse` and `dropship-confirmations:reverse` reject with `ORDER.DELIVERY_RETURNED` when returns exist (B6). Reverse commands take reason code CORRECTION or ERROR. `gr-ir:write-off` clears a GR/IR residual to an account from account determination (D3). Direct receipts without an order are matched through a receipt reference on the supplier invoice line (D3). `confirmed_date` is rejected on order create and set only by `change-line-dates` (F12). Invoicing of sales orders runs in the mode of the company setting (CORE_BUILDS or APP_SUPPLIES), see the note below |
| Compliance | 12, section 13 |
| Migration | 13, section 7. `migration-batches:open` takes the parameter `history_until_period` and writes it to the cutover `fiscal_year` (D8). `fiscal-years:open-history` opens the history years |
| Extensions and change feed | `extension-definitions:*`, `extensions:*`, `events:*` (15, 4.3, 5.3) |
| App registration (F13) | `apps:register`, `apps:update`, `apps:deactivate`, `apps:reactivate`, `app-namespaces:create`, `app-subscriptions:set` (15, 3.1 to 3.3, 4.5). Permission `apps.manage`, no `app_code` needed. `update`, `deactivate`, `reactivate` and `set` carry `expected_version`. Each writes `master_change_log` rows and an outbox event (`app.reactivated` for `apps:reactivate`). Errors `APP.NAMESPACE_TAKEN`, `APP.INACTIVE`, `CONFIG.ROW_VERSION_MISMATCH` |

**`sales-invoices:create` and the invoice mode (S2, ADR-156).** One command for sales invoices. The company setting `sales_invoice_mode` (versioned with `effective_from`, 07, 5.7, X3) decides who selects the lines: CORE_BUILDS (the core selects the lines by `invoice_policy`, as in 11) or APP_SUPPLIES (the app sends complete lines with order and delivery references). Validation of references and limits, tax determination, posting and the pipeline are identical in both modes. A switch of the setting applies to invoices created after its `effective_from`. The request shape per mode is defined in 11, 6.4.

**Supplier invoice matching (S1, ADR-155).** `purchase-invoices:post` enforces hard integrity only: valid references, invoice quantity not above ordered quantity plus the line tolerance (`over_tolerance_pct`, L2 on the order line), the gross control total, and the GR/IR clearing with the price-difference split. A price deviation is **always posted** as a price-difference line (account determination) beside the GR/IR clearing and recorded on the match record. There is no price tolerance, no tolerance override table and no match-override permission. Acceptance of a price deviation is an app decision made before the invoice is sent.

## 8. Authorisation names

The core checks that the named permission is present in `context.permissions` (P0). Granting is outside the core (04, A5). The table in 8.1 is **fixed**: it is the complete list of permission names of the core (D10, ADR-154). A new permission needs an ADR. Every command of the catalogues (5.1, 7, 8.2) names exactly one permission. The only exception is `change-line-dates`, which needs `order.promise` instead of the order permission when it changes `confirmed_date` (8.2).

### 8.1 Permission table

| Area | Permission | Allows |
|---|---|---|
| Journal | `journal.post`, `journal.reverse`, `journal.change` | post and post-batch, reverse, change-open-fields |
| Journal | `post.soft_closed` | Posting, reversal, clearing, revaluation and other posting commands with a date in a SOFT_CLOSED period. Sets `soft_close_override` on the document |
| Journal | `change.soft_closed` | `change-open-fields` on a document of a SOFT_CLOSED period. Recorded on the `change_log` rows. Amends ADR-022, which has one authorisation (Q10 decided) |
| Journal | `intercompany.post` | Intercompany commands, needed for both companies |
| Clearing | `clearing.post`, `clearing.reset` | Clearing commands |
| Revaluation | `revaluation.run` | `revaluation:run` (and preview) |
| Periods | `period.admin`, `period.hard_close`, `period.reopen` | Period commands |
| Year-end | `year_end.close`, `year_end.reopen` | Year-end commands |
| Chains | `anchor.create`, `chain.verify` | `anchors:create` and `chains:verify`. Reading chain status (`chains:status`, 09) is a read and needs `read.audit`, not `chain.verify` |
| Configuration | `write.config` | Create, update, new version and deactivate of configuration resources (07, 11.1 map) |
| Master data | `write.master` | Create, update, deactivate of master data resources (07, 11.1 map), including `item_partner` (16, 8) |
| Configuration | `config.apply` | `config-packages:apply` |
| Configuration | `company.activate` | `companies:activate` |
| Migration | `history.open` | `fiscal-years:open-history` |
| Migration | `migration.manage`, `migration.stage`, `migration.commit` | Migration commands (13, section 7) |
| VAT | `vat.prepare`, `vat.file`, `vat.settle` | VAT commands (08, section 7) |
| Inventory | `stock.post`, `stock.reverse`, `stock.reserve`, `landed_cost.allocate` | Inventory commands (10, section 6) |
| Orders | `sales.order`, `sales.deliver`, `sales.return`, `sales.invoice`, `purchase.order`, `purchase.receive`, `purchase.return`, `purchase.invoice`, `dropship.confirm`, `ic.order`, `ic.deliver`, `ic.invoice` | Order management commands (11, section 6). The reverse commands of credit notes and returns use the permission of the original command, `gr-ir:write-off` and `gr-ir:reverse-write-off` use `purchase.invoice`. The `ic.*` permissions need an ADR amendment of ADR-154 (they are in the fixed table, Q14). `purchase.match_override` is **removed** (S1, ADR-155) |
| Orders | `order.promise` | Set `confirmed_date` on order lines (16, 5.2) |
| Compliance | `retention.manage`, `hold.manage`, `dsr.manage`, `export.tenant`, `export.audit`, `export.saft`, `export.einvoice`, `export.partner_data`, `export.manage`, `tenant.delete.request`, `tenant.delete.approve` | Compliance commands (12, section 13) |
| Extensions | `ext.define`, `ext.write` | Manage definitions and write values in the caller's namespaces (15, 9) |
| Change feed | `events.consume`, `apps.manage` | `events:ack`. `apps:register`, `:update`, `:deactivate`, `:reactivate`, `app-namespaces:create`, `app-subscriptions:set` and `events:rewind` (15, 9). `apps.manage` needs no `app_code` |
| Reads | `read.ledger`, `read.config`, `read.pii`, `read.legal_snapshot`, `read.audit`, `read.cross_company`, `read.inventory`, `read.orders`, `read.events` | Read APIs (09, section 2.3, 10, section 8, 11, section 8, 15, section 9). `read.ext` is **not** a permission (F9): extension values are readable with the `read.*` permission of the entity they extend |

Platform-level operations (`tenant-deletions:execute`, `tenants:suspend`, `tenants:reactivate`, tenant provisioning) are performed by the platform operator and are not tenant permissions (12, section 13).

### 8.2 Command-to-permission map

| Commands | Permission |
|---|---|
| `journal-documents:post`, `:post-batch` | `journal.post` |
| `journal-documents:reverse` | `journal.reverse` |
| `journal-documents:change-open-fields` | `journal.change` |
| `intercompany-documents:post`, `:reverse` | `intercompany.post` |
| `clearings:post` / `clearings:reset` | `clearing.post` / `clearing.reset` |
| `revaluation:run`, `:preview` | `revaluation.run` |
| `fiscal-years:open`, `periods:soft-close`, `fiscal-calendars:create`, `:update`, `posting-windows:create`, `number-series:create`, `:update`, `number-series-assignments:create` | `period.admin` |
| `periods:hard-close` / `periods:reopen` | `period.hard_close` / `period.reopen` |
| `year-end:close` / `year-end:reopen` | `year_end.close` / `year_end.reopen` |
| `anchors:create` / `chains:verify` | `anchor.create` / `chain.verify` |
| `fiscal-years:open-history` | `history.open` |
| `{resource}:create`, `:update`, `:create-version`, `:deactivate`, `:reactivate`, `:upsert-batch` of configuration resources, business calendars and their assignments (16, not the fiscal calendar and number series of 04, which are `period.admin`) | `write.config` |
| The same operations on master data resources including `item_partner` | `write.master` |
| `vat-deduction-keys:create`, `:update`, `:deactivate`, `vat-deduction-rates:create-version` (07, 8.6) | `write.config` |
| `exchange-rates:mark-used` (07, 7.3, ADR-225) | `write.config` |
| `config-packages:apply` | `config.apply` |
| `config-packages:diff` (read-only), `config-packages:list-applied` and `:get` (the generic reads of 09, 3) | `read.config` |
| `companies:activate` | `company.activate` |
| `companies:check-readiness` | `read.config` |
| `vat-returns:prepare` (and preview) / `:record-filing` / `:settle`, `vat-returns:reverse-settlement` | `vat.prepare` / `vat.file` / `vat.settle`, `vat.settle` |
| `migration-batches:open`, `:declare-totals`, `:validate`, `:reconcile`, `:abort` | `migration.manage` |
| `migration-records:stage` | `migration.stage` |
| `migration-batches:commit`, `:rollback`, `:seal`, `:approve` | `migration.commit` |
| `stock-documents:post` / `:reverse` | `stock.post` / `stock.reverse` |
| `reservations:create`, `:adjust`, `:release` | `stock.reserve` |
| `landed-costs:allocate`, `landed-costs:reverse` | `landed_cost.allocate` |
| `sales-orders:*` (create, change, add-line, close-line, cancel) | `sales.order` |
| `sales-deliveries:post`, `:reverse` | `sales.deliver` |
| `sales-returns:post`, `sales-returns:reverse` | `sales.return` |
| `sales-invoices:create`, `:reverse`, `sales-credit-notes:create`, `sales-credit-notes:reverse`, `invoices:record-issue` | `sales.invoice` |
| `purchase-orders:*` | `purchase.order` |
| `purchase-receipts:post`, `:reverse` | `purchase.receive` |
| `purchase-returns:post`, `purchase-returns:reverse` | `purchase.return` |
| `purchase-invoices:post`, `:reverse`, `purchase-credit-notes:post`, `purchase-credit-notes:reverse`, `gr-ir:write-off`, `gr-ir:reverse-write-off` | `purchase.invoice` |
| `dropship-confirmations:post`, `:reverse` | `dropship.confirm` |
| `ic-orders:create`, `:change` | `ic.order` |
| `ic-deliveries:post`, `:reverse` | `ic.deliver` (11, 6.1) |
| `ic-invoices:create`, `:reverse` | `ic.invoice` (11, 6.1) |
| `sales-orders:change-line-dates`, `purchase-orders:change-line-dates` | `sales.order` / `purchase.order` for `requested_date`, `order.promise` for `confirmed_date` (16, 5.2) |
| `retention-rules:create-version`, `retention:extend`, `anonymisation:run` | `retention.manage` |
| `legal-holds:*` | `hold.manage` |
| `data-subject-requests:*` | `dsr.manage` |
| `exports:request`, `exports:get-download-links` | The `export.*` permission of the export type (12, 7.1) |
| `exports:get`, `exports:list` | `read.audit` |
| `exports:cancel` | `export.manage` |
| `exports:acknowledge` | `export.tenant` |
| `tenant-deletions:request`, `:cancel` | `tenant.delete.request` |
| `tenant-deletions:approve` | `tenant.delete.approve` |
| `tenant-deletions:get` | `read.audit` |
| `extension-definitions:create`, `:update`, `:deactivate` | `ext.define` |
| `extensions:set`, `:clear`, `:set-batch` | `ext.write` |
| `events:ack` | `events.consume` |
| `events:rewind`, `apps:register`, `apps:update`, `apps:deactivate`, `apps:reactivate`, `app-namespaces:create`, `app-subscriptions:set` | `apps.manage` |
| `events:list`, `events:head` | `read.events` |
| `outbox:verify` | `read.audit` |
| `calendar:add-working-days`, `calendar:working-days-between`, `legacy-ids:resolve`, `:list` | `read.config` |

Notes: `migration-batches:approve` needs a second identity (13, 7). `purchase-invoices:post` has no override permission (S1). `outbox:verify` (15, 7) has the permission `read.audit` (D10, row above). Reads other than those listed use the `read.*` permission of their spec.

## 9. Concurrency summary

- Locks in P7 follow **one authoritative order** (D3, ADR-147), which replaces the partial lists of 04, 6.3, 10, 6.4, 11 and 15, section 8:
  1. order rows (`sales_order`, `purchase_order`, sorted by company, kind, id),
  2. stock rows (`item_company`, sorted by company, item),
  3. number counters (sorted by company, series), always before the chain heads (confirmed, D3, 14 section 10),
  4. `chain_head` rows sorted by the tuple (**chain type, company, scope_key**) (X4, 17), with chain types in the order LEDGER, CLEARING, PERIOD_EVENT, VAT_RETURN, STOCK, FULFILMENT, across all companies of the command. An intercompany posting therefore locks its two LEDGER heads in company order,
  5. `fiscal_year` and `fiscal_period` rows, by (company, fiscal year, period_no): exclusive for the status commands, share mode for period-dependent commands after their head locks (A1, 04, 6.3 class 5),
  6. open items (sorted by id),
  7. `outbox_head`, always last (15, 8).
  **Clearing pre-lock rule.** A `clearings:post` that can produce an exchange difference document (a clearing across transaction currencies or with a residual) cannot know after the item locks whether it needs the LEDGER head and a number counter. It therefore pre-locks the number counter and the LEDGER chain head before the open items, in the order above, whenever a difference is possible. Open items stay last of the three groups, so the order is unchanged for all other commands (D3, 14 section 10).
  `periods:hard-close` and `year-end:close` take the LEDGER and CLEARING heads of the company (and their counters if they post) before the PERIOD_EVENT head, by the same tuple order (A1). Reads (`read_access_log`, 09) never touch `outbox_head` (A6). Recomputation that depends on mutable state runs after these locks, in P7 (section 3). `business_partner.retention_until` is not locked or written by postings: a batch job maintains it (D3, 07, 9.1).
- Number allocation and the `chain_head` update make postings of one series and one ledger serialise. Contention is accepted for SME volumes (ADR-007). Outbox positions serialise event-producing commits per tenant, for a short time at the end of the transaction (15, 5.2).
- Commands never wait indefinitely: a lock timeout returns `SYSTEM.LOCK_TIMEOUT` (retryable). The value is configuration.

## 10. Assumptions

| Ref | Item |
|---|---|
| A1 | Every batch item is an independent atomic command. `post-batch` is a wrapper, not a transaction unit (5.6) |
| A2 | The core assigns `line_no` in request order, base lines first, then generated lines (tax, then rounding). Callers address lines by `line_ref` in the request and by `line_no` afterwards |
| A3 | Response objects for documents always include all four amounts and rates per line, even if the caller sent TC only |
| A4 | `command_log` is not part of any hash chain. It is operational, not evidence. Evidence lives in the chained entities |
| A5 | Replay returns the current state of the records, not a frozen snapshot of the first response |
| A6 | Outbox events are written in the command's transaction and are not part of the idempotency replay: a replay writes no new events (15, 5.1) |
| A7 | The permission table of 8.1 is closed. New permissions need an ADR (D10) |

## 11. Open questions

| # | Question |
|---|---|
| Q1 | **Decided:** VALIDATE mode is available on all commands that create or change transaction data (same pipeline up to P6, non-binding). |
| Q2 | **Decided:** `command_log` retention is a tenant setting, default 90 days. After a purge, a retry with the same key hits the unique `idempotency_key` of the created entity and returns `IDEMPOTENCY.KEY_EXPIRED_REUSE`. |
| Q3 | **Decided, amended at v0.2:** the core warnings of this spec are four: rate fallback used, tax code differs from determined (permitted alternative), soft-close override used, unchanged field in a change command. Domain specs add warnings of their own: `VAT.EVIDENCE_MISSING` (08), `INVENTORY.LOT_EXPIRED` and negative stock issued (10), reconciliation WARNING results (13). |
| Q4 | **Decided:** codes and parameters only. Apps render and localise. A reference table of codes and parameters is published with the contract. |
| Q5 | **Decided:** add a batch command now. `journal-documents:post-batch` (5.6) is a wrapper of independent atomic posts. Details decided in Q11. |
| Q6 | **Decided:** the core builds the legal snapshot from partner master data at posting. The caller cannot override it. Invoice types require a header `partner_id`. |
| Q7 | **Decided:** `line_ref` in requests, `line_no` after posting (A2). |
| Q8 | **Decided:** the reversal date defaults to the original posting date. No automatic shifting. A caller resubmits with a later date when the original period is not postable. |
| Q9 | **Decided:** the six types of 5.2 (JOURNAL, AP_INVOICE, AP_CREDIT, AR_INVOICE, AR_CREDIT, PAYMENT). Migration documents (`HISTORY_SUMMARY`, `OPENING`, `OPENING_ITEMS`, `STOCK_OPENING`) are system types produced only by the migration commands (13). |
| Q10 | **Decided:** two permissions, `post.soft_closed` and `change.soft_closed`. Amends ADR-022 and the "one authorisation" decision of 04 Q2. `soft_close_override` on the document records use of `post.soft_closed` only. Use of `change.soft_closed` is recorded on the `change_log` rows. |
| Q11 | **Decided (ADR-062):** (a) maximum 500 items per batch, the tenant setting `max_batch_items`. (b) `stop_on_error` defaults to false. (c) No all-or-nothing batch, because one long transaction would hold number-range and chain-head locks for the whole batch and block all other postings of the company. (d) The wrapper pattern also serves master data (`upsert-batch`, 07) and extension values (`extensions:set-batch`, 15). Batches of other commands (clearing, change) can follow later. (e) Items sequential in array order. |
| Q12 | **Decided (14, D1, ADR-145, amended by 14 section 11):** derived and system documents are reversed only through their owning command. `journal-documents:reverse` rejects them (5.3). `reversal_of_document_id` is allowed for every mirror document and needs a reason code. The four mirror types REVERSAL, REVAL_REVERSAL, CLEARING_RESET and STOCK_REVERSAL stay, the original type is kept only for types without a mirror type. YEAR_END is reversed only by `year-end:reopen`. |
| Q13 | **Decided (14, D10, ADR-154):** the permission table of 8.1 is fixed and every command names one permission. Which resources fall under `write.config` and which under `write.master` is the map of 07, 11.1 (07, 11.1, confirmed, 07 Q12). |
| Q14 | **Decided (14 section 10, D1 follow-up, D10):** (a) IC permissions are `ic.order` (`ic-orders:*`), `ic.deliver` (`ic-deliveries:*`), `ic.invoice` (`ic-invoices:*`), as 11, 6.1 already names them. (b) Order-built credit documents are reversed by `sales-credit-notes:reverse` and `purchase-credit-notes:reverse` (permission of the original command). Their contracts are in 11. |
| Q15 | **Decided (D1 follow-up, D9, renamed by 14 section 11):** the owning command of VAT_SETTLE is `vat-returns:reverse-settlement` (permission `vat.settle`), before filing only (`VAT.ALREADY_FILED` after, `VAT.NOT_SETTLED` without a settlement). It replaces `vat-settlements:reverse`. Payload `return_id`, `reversal_date`, `reason_code` (CORRECTION or ERROR), `reason_text`, no date shifting (X8, 7.3). Its contract is 08, 7.3a. On a superseded return with an active successor settlement it rejects with `VAT.SUPERSEDED` (B9). |
| Q16 | **Decided (14 section 11):** (a) the `MIGRATION` codes of 13, 7.1 are adopted and listed in 2.4. (b) `INVENTORY.INSUFFICIENT_STOCK`, `INVENTORY.ALLOCATION_*`, `ORDER.CREDIT_NOTE_*`, `ORDER.INVOICE_HAS_CREDIT_NOTES` and `PERIOD.HISTORY_YEAR` are adopted and listed in 2.4. 10, 6.2 P4 uses `INSUFFICIENT_STOCK`. 13, 7.1 and 05 use the same names. **Names decided in the cleanup pass (14, section 12):** `PERIOD.HISTORY_YEAR` (`year-end:close`, `periods:soft-close`, `periods:hard-close` on an `is_history` period before seal), `PERIOD.HISTORY_YEAR_SEALED` (`periods:reopen`, `year-end:reopen` on a sealed history year), `ORDER.INVOICE_REVERSED` (credit note on a reversed invoice), `REVERSAL.REASON_NOT_ALLOWED` (external reverse call with AUTO_REVERSAL, RERUN, REOPEN or RETURN), `INVENTORY.ALLOCATION_EXISTS` (invoice reversal while charge-line allocations exist). |
| Q17 | **Decided (14 section 10):** `IC` is not a document type. IC documents are AR_INVOICE or AP_INVOICE with `ic_transaction_id` (5.2). 02 and 03 must be aligned. |
| Q18 | **Decided (D10):** `outbox:verify` has the permission `read.audit` (8.2). |
| Q19 | **Decided (D3):** number counters lock before the chain heads (9). |
| Q20 | **Decided (14 section 11):** `journal-documents:reverse` rejects AUTO_REVERSAL, RERUN, REOPEN and RETURN from external callers (CORRECTION and ERROR only, 5.3). The six-code list is closed. |
| Q21 | **Decided (14 section 11):** `upsert` on an existing row requires `expected_version`, without a legacy match it inserts, `upsert` without `legacy_ref` is rejected, only the batch variant is exempt (07, 11.1, 11.5, Q14). |

## 12. ADRs raised by this spec (now in 01-adr-log, which is authoritative)

**ADR-056: Command contracts: synchronous, atomic, complete result.** Every command validates and persists in one transaction and returns the full created or changed records. No partial results, no drafts.

**ADR-057: Caller sends TC amounts only.** The core derives LC, GC, RC, tax lines and rounding lines. Rate overrides are optional and bounded by tolerance (ADR-019). Refines ADR-020 and ADR-027. *Amended by ADR-150 (D6): the closed list of cost exceptions (supplied value, invoice value, migration), marked with `cost_source` per movement line (COMPUTED, SUPPLIED, INVOICE, MIGRATION).*

**ADR-058: Phased validation with collected errors.** One pipeline (P0 to P7) for all commands, independent errors collected per phase, structured errors with code, path and parameters. The core returns no localised text.

**ADR-059: Idempotency through `command_log`.** Successful commands only, request fingerprint, derived keys for multi-record commands, entity-level unique keys as backstop. Refines ADR-004.

**ADR-060: Compare-and-set on L2 changes.** `change-open-fields` carries expected old values and is all-or-nothing. No version columns.

**ADR-061: VALIDATE mode.** Same pipeline up to consistency checks, no side effects, non-binding. Available on all commands that create or change transaction data. Resolves the `:validate` open item (ADR-013).

**ADR-062: Batch posting as a wrapper of independent commands.** `journal-documents:post-batch` runs each item as an ordinary atomic, idempotent `post`, sequentially, with per-item results. No batch transaction, no batch-level log entry. The limit of 500 items is settled (tenant setting `max_batch_items`). The pattern is reused for master data and extension values.

**ADR-063: Two soft-close permissions.** `post.soft_closed` for postings (recorded as `soft_close_override` on the document) and `change.soft_closed` for L2 changes (recorded on the change log). Amends ADR-022 and ADR-045.

**ADR-064: Legal snapshot built by the core.** The snapshot is copied from partner master data as of the posting date. Callers cannot supply or override it. Refines ADR-008.

## 13. ADRs of the re-baseline that shape this spec (in 01-adr-log, which is authoritative)

| ADR | Decision | Where used here |
|---|---|---|
| ADR-145 (D1) | Reversal of derived documents only through the owning command, `reversal_of_document_id` for all mirror documents. Follow-up: four owning commands (`vat-settlements:reverse` renamed `vat-returns:reverse-settlement`), closed reason code list (external callers CORRECTION and ERROR), the four mirror types stay and the original type is kept only for types without one, no `IC` document type | 5.2, 5.3, 7.3, Q12, Q14, Q15, Q17, Q20 |
| ADR-147 (D3) | One authoritative lock order, counters before chain heads, clearing pre-lock, derivation after the locks | 3, 9, Q19 |
| ADR-148 (D4) | Stale check at hard-close of every relevant period including special periods (amended by X2: not on every posting), `year-end:close` writes PERIOD_HARD_CLOSE. ADR-148 and the amended note of ADR-053 must be corrected in the ADR log | 3, 7.3 |
| ADR-150 (D6) | Closed list of cost exceptions | ADR-057 note |
| ADR-153 (D9) | VAT settlement reversal only before filing, clearing does not touch `first_used_at` | 5.3, 7.1, 7.3 |
| ADR-154 (D10) | Fixed permission table, one permission per command. IC permissions `ic.order`, `ic.deliver`, `ic.invoice`, `outbox:verify` with `read.audit` | 8, Q13, Q14, Q18 |
| ADR-155 (S1) | Matching thinned to line tolerance, no price tolerance, no override | 2.4, 7.4, 8 |
| ADR-156 (S2) | `sales-invoices:create` with the versioned `sales_invoice_mode` | 7.4 |
| ADR-157 (S3) | Auto-reservation optional, default off, BEST_EFFORT only | 07, 5.7 |
| ADR-158 (K1) | One `chain_head` table, optional `scope_key` (LEDGER only), one VAT_RETURN chain per company | 5.1, 7.3, 9 |
| ADR-159 (K3) | `outbox_event` is the single append-only stream behind the audit query and the app feed | 2, 8 |
| ADR-137 to ADR-139 | Extension attributes, change feed, app registration (15) | 2.1, 2.4, 8 |
| ADR-141 | Order line dates and `order.promise` (16) | 8 |

### 13.1 Deltas of the third re-baseline pass v0.4 (14, section 11)

Applied in this spec: rename to `vat-returns:reverse-settlement` and `VAT.NOT_SETTLED` (2.4, 5.3, 7.3, 8.2, Q15), closed reason code list and rejection of unfit codes (5.3, Q20), mirror type rule (1, 5.3, Q12), adopted error codes (2.4, Q16), `PERIOD.HISTORY_YEAR` on `year-end:close` (7.3), three reverse commands adopted (5.3, 7.4, 8.2), `upsert` rule (Q21), `outbox:verify` permission wording (8.2 notes, Q18). Carried by other specs: 02 and 03 (mirror types and `reversal_of_document_id` wording, no `IC` type), 04 and 05 (`PERIOD.HISTORY_YEAR`), 08 (rename, `VAT.NOT_SETTLED`, event `vat.settlement_reversed`, contract), 10 (`landed-costs:reverse` contract, `INVENTORY.ALLOCATION_*`, `INSUFFICIENT_STOCK` in 6.2 P4), 11 (credit note reverse contracts, `ORDER.CREDIT_NOTE_*`, `ORDER.INVOICE_HAS_CREDIT_NOTES`), 13 (drop "proposed" in 7.1, `upsert` wording in 3.5, close Q19 with `PERIOD.HISTORY_YEAR`), 15 (event name).

### 13.2 Deltas of fix pass 4 v0.5 (17, section 4)

Applied in this spec: X1 (`CLOSING` type, 5.2, 5.3), X2 (stale check at hard-close only, 3, 7.2, 7.3, 13), X3 (names), X4 and X7 (lock tuple, one phase model, 3, 5.5, 9), X5 (`origin`, 5.2, 5.3), X6 (codes, permissions), X8 (reverse-settlement payload, 7.3), A1 to A5 (locks, anchor PENDING and STAMPED, `CLOSE.SPECIAL_PERIOD_OPEN`, `CLOSE.CLOSING_PERIOD_NOT_OPEN`, reopen order), B1 to B9 (5.3, 7.4), D1 to D3, D8, E4 (`EXT.ENTITY_ANONYMISED`), F1 to F3, F9, F10, F12, F13, key length (2.1, 4.3), `chains:verify` read-only without event. Carried by other specs and open there: 02 and 09 (`origin` column and P&L exclusion of CLOSING, the `chain.verified` event and "last verification run" of 09 are dropped), 05 (`CLOSING` and anchors), 08 (7.3a payload: `return_id`, `reversal_date`, `reason_code` CORRECTION or ERROR, no `posting_date` default shifting, `VAT.SUCCESSOR_SETTLEMENT_ACTIVE`), 10 and 11 (contracts of `sales-returns:reverse`, `purchase-returns:reverse`, `gr-ir:write-off`, `landed-costs:reverse` columns, `ORDER.*` codes, 11 setting names), 12 (cancel and acknowledge permissions as in 8.2), 15 (`apps:*` contracts, `EXT.ENTITY_ANONYMISED`, event names), 04 (A3 DATE, `history_until_period`), ADR log (ADR-148, ADR-053, ADR-154 `ic.*` note). Not decided by 17 and left open: reverse command and document type of `gr-ir:write-off` documents, the audit event name of TAX_EVIDENCE additions after hard-close, the anchor-pending deadline setting.

Cleanup pass (14, section 12): error codes `PERIOD.HISTORY_YEAR`, `PERIOD.HISTORY_YEAR_SEALED`, `ORDER.INVOICE_REVERSED`, `REVERSAL.REASON_NOT_ALLOWED` (replaces the `SCHEMA.INVALID_VALUE` placeholder in 5.3) and `INVENTORY.ALLOCATION_EXISTS` are entered in 2.4. `periods:soft-close`, `periods:hard-close` and `year-end:close` reject `is_history` periods before the seal (7.3). Pointer line to the credit-note and landed-cost reverse commands added in 5.1.

### 13.3 Deltas of propagation pass v0.6 (17, sections 4 and 5)

Applied in this spec: error index completed by grep over specs 02 to 05, 08, 10 to 13, 15 and 16 (new areas `POSTING` and `SERIES`; `PERIOD.NOT_FOUND`, `HARD_CLOSED`, `SOFT_CLOSED_NOT_AUTHORISED`, `CLOSING_PERIOD_MANAGED`, `REOPEN_LATER_PERIOD_CLOSED`; `CLOSE.YEAR_NOT_OPEN`, `YEAR_NOT_CLOSED`, `SPECIAL_PERIOD_NOT_CLOSED`, `RETAINED_EARNINGS_INVALID`, `REOPEN_LATER_YEAR_CLOSED`; `REVAL.OUTSIDE_POSTING_WINDOW`, `SOFT_CLOSE_NOT_AUTHORISED`; `CLEARING.NOT_RESETTABLE`; `RETENTION.CLASS_HAS_NO_DATE`; `ORDER.RETURN_CREDITED`, `CREDIT_EXCEEDS_INVOICED`, `CONFIRMED_DATE_ON_CREATE`, `GRIR_ITEM_INVALID`, `GRIR_RESIDUAL_CHANGED`, `GRIR_WRITE_OFF_NOT_FOUND`, `GRIR_WRITE_OFF_REVERSED` (spelling of 11; `GR_IR_NOTHING_TO_WRITE_OFF` dropped), `MIGRATION.HISTORY_UNTIL_INVALID`; `MIGRATION.ABORT_NOT_ALLOWED`; `INVENTORY.VALUE_REQUIRED`, `CURRENCY_MIXED`; `EXT.DEFINITION_LOCKED`; `CONFIG.REVALUE_FLAG_ON_RETAINED_EARNINGS`), names unified (`SPECIAL_PERIOD_NOT_CLOSED` replaces `SPECIAL_PERIOD_OPEN`, `VAT.SUPERSEDED` replaces `SUCCESSOR_SETTLEMENT_ACTIVE`, `VAT.NOT_FILED` dropped, `TAX.ORIGIN_DESTINATION_REQUIRED` is the name), new commands `gr-ir:reverse-write-off` and `apps:reactivate`, document type `GR_IR_WRITE_OFF` and `CLOSING` in the owning-command table, Q15 closed, lock order of section 9 with seven classes (checked against 04, 6.3), `history_until_period` attributed to `migration-batches:open` (7.3, 7.4; `fiscal-years:open-history` only opens history years). Carried by other specs and open there: 04 (`PERIOD_HARD_CLOSED` style names already as in 2.4), 05 (`CLOSE.SPECIAL_PERIOD_NOT_CLOSED` as used), 08 (`TAX.ORIGIN_DESTINATION_MISSING` at 63 and 389 renamed to `TAX.ORIGIN_DESTINATION_REQUIRED`, `VAT.NOT_FILED` note at 391), 10 (`INVENTORY.VALUE_REQUIRED`, `INVENTORY.CURRENCY_MIXED` text), 13 (`MIGRATION.ABORT_NOT_ALLOWED` "to be entered in 06" note at 285), 11 ("to be entered in 06" notes at 276, 395, 592), 15 (`apps:reactivate` contract, `app.reactivated`), 12 (`RETENTION.CLASS_HAS_NO_DATE` note). `CONFIG.REVALUE_FLAG_ON_RETAINED_EARNINGS` has no use in a spec yet: 07 must state it (05 Q3 says config rejects it) or the code is dropped.

### 13.4 Deltas of ADR-200 and ADR-203 to ADR-206 (2026-10-14)

Applied in this spec: request field `deductible_tax_amount_tc` and the full-tax meaning of `tax_amount_tc` (5.2), pipeline P4 to P6 (derived EU keys, deduction share, split, allocation check), error codes `TAX.DEDUCTION_RATE_NOT_FOUND`, `TAX.DEDUCTION_AMOUNT_REQUIRED`, `TAX.DEDUCTION_AMOUNT_NOT_ALLOWED`, `TAX.DEDUCTION_AMOUNT_EXCEEDS_LIMIT`, `CONFIG.DEDUCTION_ON_NON_INPUT_CODE`, `CONFIG.DEDUCTION_KEY_UNKNOWN`, `CONFIG.DEDUCTION_KEY_REFERENCED`, `ORDER.PARTIAL_DEDUCTION_NOT_SUPPORTED` (2.4), commands `vat-deduction-keys:*` and `vat-deduction-rates:create-version` with `write.config` (8.2). Carried by other specs: 02 (`TAX_NONDEDUCTIBLE`, new columns, `hash_version` 2), 07 (code version columns, `origin_eu` and `destination_eu`, key and rate tables), 08, 09, 11 and 13 (see 19, section 11).
### 13.5 Deltas of spec 22 (2026-10-19)

Applied in 12, 07, 09, 11 and 15: `invoices:record-issue` takes exactly one of `invoice_id` and `document_id` (12, 11, error codes `ORDER.ISSUE_TARGET_INVALID`, `ORDER.ISSUE_TARGET_NOT_FOUND`, `ORDER.ISSUE_DOCUMENT_TYPE_INVALID`, index in 2.4), the company setting `einvoice_rule_set` and `CONFIG.EINVOICE_RULE_SET_UNKNOWN` (07, 5.5, 12, 10, ADR-221), the filter `issue_state` and the list `invoice-issue-records` (09, 3, 11, 8). No new command. Permissions unchanged (`invoices:record-issue` keeps `sales.invoice`). Idempotency key conventions for apps are in 22, section 3 and add no rule to 4.3.

### 13.6 Delta of ADR-223 (2026-10-19)

Applied in 07 and 12: `company_bank_account` gets `account_scheme` and `account_identifier`, `iban` is nullable, error code `CONFIG.BANK_ACCOUNT_IDENTIFIER_INVALID` (2.4). The resource commands of `company_bank_account` (07, 11.1) take the two fields and reject a violation of the rule. Permission unchanged (`write.master`, 07, 11.1). No new command.

### 13.7 Deltas of ADR-224 to ADR-227 (2026-10-19, spec 23)

Applied in 07, 02, 09, 11 and 15 and here: the new command `exchange-rates:mark-used` with permission `write.config` (7.4, 8.2, 07, 7.3), the IC marker rule of the IC commands (5.5, 02, 5.4), the error codes `CONFIG.NOT_A_GROUP_MEMBER`, `CONFIG.OWNERSHIP_INVALID`, `CONFIG.OWNERSHIP_CYCLE`, `CONFIG.TRANSLATION_METHOD_INVALID` and `CONFIG.GROUP_ACCOUNT_OTHER_GROUP` (2.4). The resource commands of `company_consolidation_setting_version` follow the configuration pattern of 07, 11.1 (create-version, deactivate, upsert batch). The new read `ic-reconciliations:query` and the additions to `balances:query` are in 09 (4.1, 4.14). No new permission.

### 13.8 Package deltas (ADR-193 to ADR-197, ADR-217, 2026-10-19)

Applied here and in 07, 09, 12: the error area `PACKAGE` with nine codes (2.4), the read-only command `config-packages:diff` and the permission `read.config` for it and for `config-packages:list-applied` and `:get` (7.4, 8.2), the payload and the order of checks of `config-packages:apply` (07, 11.4: archive upload, signature before digests before gate before inputs, items, user changes win). VALIDATE mode of the apply is the one of section 6. The `command_log_result` entity type `APPLIED_CONFIG_PACKAGE` already exists (4.2). `config-packages:diff` writes no `command_log` row and no event, like `chains:verify`; the apply keeps the rules of 4.
