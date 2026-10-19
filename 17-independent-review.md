# Independent Review of the Re-baselined Design

Status: REVIEW for decision (2026-10-13). Four read-only agents that did not write the specs reviewed 02 to 05, 06 to 09, 10 to 13, and 15, 16, the ADR log, the principles and the ERD (about 160 findings in total, many duplicates across reports). This document consolidates them by topic. Nothing in the specs has been changed by this review. Items marked **V** were verified by me against the files. All others are as reported by the reviewers (**R**) and are verified when fixed. Decisions marked **[D]** need your ruling. Everything else is mechanical.

## 1. Convergent findings (reported by several reviewers)

| ID | Finding | Sources | Status |
|---|---|---|---|
| X1 | **Closing entries of migrated history years** use a document type `CLOSING` that 02 does not define (only a line type), 13:221 says P&L includes them while 13:326 says it excludes them, and the P&L rule excludes only YEAR_END, so history comparatives net to zero | 02-05, 06-09, 10-13, 15+ | **V** [D] |
| X2 | **ADR-148 and ADR-053's amended note and 06:585 and 06:127 still say the stale check runs on every posting.** Your ruling (14 section 10) is: at hard-close of every relevant period | 02-05, 06-09, ADR | **V**, mechanical |
| X3 | **Order setting names differ:** 07 `invoice_mode`, `auto_reservation`, 11 and ERD `sales_invoice_mode`, `auto_reserve`. 06 and ADR-156 say `valid_from`, 07 and 11 say `effective_from` | all | **V**, mechanical |
| X4 | **Chain-head lock sort tuple differs:** 04 and 02 say (type, company, scope_key), 06, 07, 10, 11, 15 and ADRs say (type, scope_key). IC postings lock two LEDGER heads, so the order can invert and deadlock | 02-05, 06-09, 10-13, 15+ | **V**, mechanical |
| X5 | **Orders-built documents are identified by a caller-supplied field** (`source_system` = ORDER_MGMT), so an external caller can fake or bypass it | 02-05, 06-09 | R [D] |
| X6 | **Missing in 06 or 07:** `migration_document_max_lines` in 07, `ORDER.INVOICE_LINE_REFERENCE_MISSING` in 06, permissions for `tenant-deletions:*`, `exports:*`, `master.item_partner` | all | R, mechanical |
| X7 | **P7 and phase model differ:** 06 puts locks in P7 and recomputes P5 under them, 10 uses "P4a", 11 says "P4 after the locks of P7" | 06-09, 10-13, ADR | R, mechanical |
| X8 | **VAT settlement reversal contract differs** between 06 (`settlement_document_id`) and 08 (`return_id`, shifted date, all six reason codes) | 06-09, 15+ | R, mechanical + [D] on date shifting |

## 2. Decisions needed, grouped by topic

### A. Concurrency and period close

| ID | Finding | Proposal |
|---|---|---|
| A1 | Hard-close, posting, clearing and L2 change are not serialised. The status commands lock only PERIOD_EVENT, so a posting can commit into a period that hard-closes concurrently and the stale check is wrong (02-05 #1) | `periods:hard-close` and `year-end:close` lock LEDGER and CLEARING heads before PERIOD_EVENT. Period-dependent commands read `fiscal_period` in share mode and re-check status after their head locks |
| A2 | External timestamp call inside close transactions. `chain_anchor.timestamp_token` is NOT NULL and anchors are created inside `periods:hard-close` and `year-end:close`. A timestamp-service outage blocks year-end (02-05 #2) | Anchoring becomes a post-commit step with state PENDING then STAMPED. `timestamp_token` nullable until stamped. The close requires the anchor request, not its response |
| A3 | Series assignment resolves by command time, so a late posting in the old open year takes a number from the new series (02-05 #3). `effective_from` is a timestamp compared with a date | Resolve by the posting date's fiscal year. `effective_from` becomes the fiscal year start date |
| A4 | Year-end can close a year with an OPEN special period 13, and posts a reopen mirror into a CLOSED year, and has no code for "closing period not OPEN" (02-05 #12, #13) | `year-end:close` requires every non-closing special period HARD_CLOSED. `year-end:reopen` orders: reopen period, year to OPEN, then mirror posting. Add the error codes |
| A5 | Revaluation lock claim is false: the run does not lock the CLEARING head (02-05 #20) | The run takes the CLEARING head. Remove the "cannot be stale" claim and rely on the hard-close stale check |
| A6 | Reads write outbox events (`access.read_logged`) and so take the tenant-wide `outbox_head` lock, serialising PII reads with all writes and breaking replica reads (15+ #28) | Read-access events are written to `read_access_log` only and not to the outbox. The outbox gets a daily summary event (`access.summary`) per partner scope |

### B. Reversal, clearing and revaluation mechanics

| ID | Finding | Proposal |
|---|---|---|
| B1 | Rerun reverses the same REVALUATION twice and `reversed_by` has one slot (02-05 #4) | Define: multiple REVAL_REVERSAL mirrors per original are allowed, `reversed_by` is the first. Rerun step 1 mirrors only an original that has no auto reversal yet, and step 2 is not a mirror of a mirror but the new run's own pair |
| B2 | Clearing reset with a difference document has an impossible item list (02-05 #7) | Reset item list: no item on D's offset line, one item on the reset's own offset line, D's offset becomes CLEARED by the reset clearing. Write this out in 03 with amounts |
| B3 | Reversal of a document with open-item lines: unclear whether original and mirror open items auto-clear (02-05 #21) | The reversal clears each mirror open item against its original automatically in the same transaction (clearing without document), with a clearing record |
| B4 | Migration rollback posts mirrors but 02's reason list and mirror types do not cover migration types (02-05 #6) | Add rollback to the mirror-producing commands, reason code CORRECTION, mirror keeps the original type for HISTORY_SUMMARY, OPENING, OPENING_ITEMS, STOCK_OPENING |
| B5 | Returns have no reverse command (10-13 H1) | Add `sales-returns:reverse` and `purchase-returns:reverse` (permissions `sales.return`, `purchase.return`) |
| B6 | Delivery reversal does not check returns (10-13 M3) | Reject with `ORDER.DELIVERY_RETURNED`, same for dropship |
| B7 | Landed-cost reversal has no test and no columns (10-13 M7, 15+ #21) | Test: no later movement on the cost key since the allocation. Add `reversal_of_allocation_id`, `reason_code`, status. Mirror stock document type is STOCK_REVERSAL |
| B8 | FIFO receipt reversal checks only total quantity (10-13 H11) | Check the receipt layer's remaining quantity, else `INVENTORY.RECEIPT_CONSUMED` |
| B9 | VAT replacement settlement can double-settle (06-09 #2) | Difference is computed against the last settled snapshot along the `replaces_return_id` chain. `vat-returns:reverse-settlement` is blocked on a superseded return while a successor settlement is active |

### C. Stock valuation

| ID | Finding | Proposal |
|---|---|---|
| C1 | As-of reads use running balances that follow processing order, so backdated movements give wrong as-of values and the "reconciles by construction" claim fails (10-13 H5) | As-of reads sum quantity and value by posting date and processing order. Running columns are used only for "now". Remove "by construction" for historic dates |
| C2 | Negative stock mixes the stock key and the cost key (10-13 M8) | Negative handling is defined on the cost key only |
| C3 | FIFO is silent on adjustment-in without value, transfers and RETURN_OUT (10-13 M9) | Adjustment-in without value uses the last layer cost. Transfers move layers at their layer cost. RETURN_OUT consumes the layer of the referenced original receipt |
| C4 | `layer_date` is supplied by migration but has no column (10-13 M10) | Add `layer_date` to `stock_cost_layer` |
| C5 | Location rules differ: TRANSIT and QUARANTINE reject all receipts and issues in 10, and QUARANTINE accepts only quarantine status in 16, so nothing can enter except transfers (15+ #10) | TRANSIT and QUARANTINE accept TRANSFER_IN, STATUS_CHANGE and ADJUSTMENT. They reject RECEIPT and ISSUE. QUARANTINE accepts only status QUARANTINE or BLOCKED |

### D. Orders, GR/IR and migration

| ID | Finding | Proposal |
|---|---|---|
| D1 | THREE_WAY invoice cap is "received", not "received minus returned" (10-13 M1) | Cap at received minus returned |
| D2 | ORDERED goods lines: CORE_BUILDS invoices without delivery, APP_SUPPLIES requires a delivery reference. Delivery reversal ignores referenced delivery lines (10-13 M2) | ORDERED goods lines may be invoiced before delivery in both modes. A delivery reference, when given, protects the delivery line from reversal (`ORDER.DELIVERY_INVOICED`) |
| D3 | GR/IR residual, orphan and direct-receipt matching have no path (10-13 M5) | Add `gr-ir:write-off` (permission `purchase.invoice`) that clears a GR/IR residual to an account from account determination. Direct receipts without an order are matched through a receipt reference on the supplier invoice line |
| D4 | Open quantity has three definitions and no purchase "open to receive" (15+ #7) | One definition per side in 11: open = ordered minus delivered (received), not below zero, over-tolerance does not count as demand. 16, 09 and credit exposure reference it |
| D5 | Returns and price splits on migrated and dropship documents have no valuation source (10-13 H7) | Migrated deliveries and receipts carry `value_lc` from the legacy record as valuation basis. A dropship return links sales return to purchase return by a `dropship_confirmation_id` |
| D6 | Migration commit posts into a DRAFT company without an exception, a batch can stick in COMMITTING, rollback blocks a new batch (10-13 H3, H4, M13) | DRAFT company is a controlled exception for an unsealed batch. `abort` allowed in COMMITTING when no chunk is DONE. ROLLED_BACK is terminal and not counted as non-aborted. Rollback removes `legacy_id_map` rows of the batch |
| D7 | Several transaction currencies per migration document conflict with ADR-035 (10-13 H6) | Split migration OPENING and OPENING_ITEMS documents per transaction currency. One currency per stock document |
| D8 | History periods inside the cutover year have no marker, so the history guards do not apply (10-13 H10) | Add `history_until_period` to the cutover `fiscal_year` and key `PERIOD.HISTORY_YEAR` on it |
| D9 | Closing document rates: undefined for LC = 0, and "non-zero" has no currency (02-05 #14), rate rules for TC = 0 system lines undefined (02-05 #15, 15+ #33) | Non-zero means non-zero in any currency. The implied rate for LC = 0 is derived from the other currency, and TC = 0 system lines use rate 1 and rate type DERIVED |

### E. Compliance and platform

| ID | Finding | Proposal |
|---|---|---|
| E1 | Tenant deletion cannot execute under defaults: export expires after 14 days, waiting period is 30 (10-13 H8) | The deletion-backing export does not expire before `waiting_until` plus 7 days |
| E2 | Partner `retention_until` recomputation can shorten a `retention:extend` extension (10-13 H9) | Add `retention_floor` as a stored minimum that the recomputation respects. Restrict `retention:extend` to classes that store a date |
| E3 | Extension logging and GDPR: values logged in `master_change_log`, no `app_id`, no clear action, `is_pii` changeable after values exist (15+ #3) | `is_pii` values are logged without content. Add `app_id` and a CLEAR action to `master_change_log`. `is_pii` is immutable once values exist |
| E4 | Extension clearing at deactivation bypasses legal holds and nothing clears values on orders and open items of an anonymised partner (15+ #12) | Extension values are cleared at anonymisation, never at deactivation. Anonymisation clears `is_pii` values on records referencing the partner. `extensions:set` on an ANONYMISED partner is rejected (`EXT.ENTITY_ANONYMISED`) |
| E5 | Audit events through the app feed need `read.audit`, which consumers should not have (15+ #6) | Separate feed visibility from audit class: subscription to a type grants feed access to its non-sensitive events. `access.*`, compliance and app events are readable only with `read.audit` |
| E6 | Migration events: one at the seal vs one per status, and `outbox:verify` reports migrated chained records as gaps (15+ #13) | One event per batch status change plus one `migration.sealed` event carrying counts and the chain ranges. `outbox:verify` accepts a migrated range as covered by the batch event |
| E7 | Extension compare-and-set undefined (15+ #15) | `extension_value` gets `row_version`. `expected_version` is per (entity, namespace). An extension write never bumps the owner row's `row_version` |
| E8 | Retention changes versus ADR-146's "own chained event" (10-13 M19) | Amend ADR-146: S changes without a chain use the outbox audit event |

### F. Tax and configuration

| ID | Finding | Proposal |
|---|---|---|
| F1 | Tax determination: tie between rules of different key sets, undefined `partner_vat_status` country and absence, no origin and destination for partnerless documents (06-09 #8) | Tie-break: highest specificity count, then lowest `rule_id`. Absent or expired registration is NOT_REGISTERED in the destination country. Partnerless documents require explicit origin and destination or are rejected |
| F2 | TAX_EVIDENCE cannot be changed by `change-open-fields` and not after hard-close (06-09 #5) | Add the four fields and `line_no` to 06. Allow evidence changes in hard-closed periods as a controlled S-like exception (append-only evidence, own audit event) |
| F3 | Configuration tables of 04 missing in 07 and in the permission map (06-09 #7) | Add `fiscal_calendar`, `posting_window`, `number_series`, `number_series_assignment` to 07 and the commands to 06 with permission `period.admin` |
| F4 | `item_partner` key cannot hold a company override (15+ #4, 06-09 #36) | PK gets a `company_key` marker (0 for tenant rows) like `account_role_assignment` |
| F5 | `business_calendar_exception` cannot store corrections (15+ #5) | PK gets `created_at`, latest row wins |
| F6 | `company_calendar_assignment` has a nullable column in the PK (15+ #26) | Sentinel value 0 for "company level" |
| F7 | V-tables cannot be retired and `valid_to` updates conflict with insert-only (06-09 #12) | "End-version": insert a row with `valid_to` set, no update |
| F8 | Role table constraints missing (06-09 #13) | Mode and type of an account cannot change while a role references it. One account has one role. Roles define `currency_code` and `tax_account` rules |
| F9 | `read.ext` is both a permission and "part of every read.*" (06-09 #17) | `read.ext` is implied by each `read.*` of the entity and removed from the fixed permission list |
| F10 | `app_code` missing in the read envelope and conflicts with `events:rewind` (06-09 #16) | Add `app_code` to the 09 envelope. `events:rewind` and `apps.manage` do not require an app |
| F11 | Supply-demand: dropship and service lines have no warehouse, `ic` flag and `group_by` not in parameters, `as_of` unsupported for orders (15+ #8, #9) | Dropship and service lines are reported under warehouse null. Add `group_by`. Replace time `as_of` by an outbox position as consistency token |
| F12 | `confirmed_date` at order creation needs `order.promise` but 06 maps create to `sales.order` only (15+ #11) | `confirmed_date` is rejected on create. It is set only by `change-line-dates` |
| F13 | App registration has no command contracts (15+ #14) | Add `apps:register`, `:update`, `:deactivate`, `app-namespaces:create`, `app-subscriptions:set` with `expected_version` and `master_change_log` |

## 3. Mechanical fixes (no decision)

Applied by one cleanup pass after your rulings: the items of section 1 marked mechanical, the stale text and "to be written" notes in 06, 08, 09, 11, 15, 16 and 00, ADR amended notes and back-pointers (ADR-062, 069, 128, 129, 131, 147, 150, 159 and the pointers from ADR-160 to 174), the ERD updates (pages 4, 7, 10, 11, 12, 13, 14, 16, 18), the sales and purchase invoice column specs in 11, `ic_order_link` columns, column and name drift (`snapshots_anonymised`, `PARTIALLY`), type slips (`fiscal_period` INT vs SMALLINT), missing event types (`reservation.adjusted`, `year.opened`, order lifecycle mapping), idempotency key length, `correlation_id` type (TEXT(100)), error codes without a use, S-column trigger rule and `change_log` shape (clearing key, open-item entity type, L1* exception), and the `command_log` retention versus AUDIT_LOG class.

## 4. Decisions taken (2026-10-13)

All proposals of section 2 are approved except where noted. Binding for the fix pass.

| Ref | Decision |
|---|---|
| X1 | **Differs from the proposal:** history-year closing entries are a **new document type `CLOSING`** (system generated, set by the HISTORY chunk), not YEAR_END. `CLOSING` is added to 02 (type list, `is_system_generated`), 06 (system types, owning command `migration-batches:rollback`), 09 and 05 so that it is **excluded from P&L reads like YEAR_END** (comparatives are preserved), included in balance reads. `fiscal_year.year_end_document_id` is set for history years to the CLOSING document. 13:221 and 13:326 are made consistent |
| X2 | Stale check at hard-close only (fix ADR-148, ADR-053 note, 06:127, 06:585) |
| X3 | One name set: `sales_invoice_mode`, `auto_reserve`, validity column `effective_from` everywhere (06, 07, 11, 13, ADR-156, ERD) |
| X4 | Chain-head lock sort tuple everywhere: (chain type, company, scope_key); counters before heads |
| X5 | `journal_document.origin` L1 column set by the core (EXTERNAL, ORDER, STOCK, VAT, MIGRATION, SYSTEM), never by the caller. `source_system` stays a free tracing field. Rejection rules in 06 use `origin` |
| X6, X7 | Mechanical: missing settings, codes, permissions in 06/07, one phase model (locks at P7, recomputation under the locks, no "P4a") |
| X8 | `vat-returns:reverse-settlement` payload: `return_id`, `reversal_date`, `reason_code` (CORRECTION or ERROR), `reason_text`. No date shifting. 06 refers to 08 7.3a |
| A1 | `periods:hard-close` and `year-end:close` lock LEDGER and CLEARING heads before PERIOD_EVENT. Period-dependent commands read `fiscal_period` in share mode and re-check status after their head locks |
| A2 | Anchoring after commit: anchor request written in the close transaction with state PENDING, token fetched after commit (STAMPED). `timestamp_token` nullable until stamped. `chains:verify` warns about PENDING anchors beyond a deadline |
| A3 | Series resolved by the posting date's fiscal year. `effective_from` of series assignments becomes a DATE (fiscal year start) |
| A4 | `year-end:close` requires every non-closing special period HARD_CLOSED. `year-end:reopen` order: reopen period, year to OPEN, then mirror posting. New codes for closing period not OPEN and special period open |
| A5 | `revaluation:run` also locks the CLEARING head. The claim "cannot be stale" is removed |
| A6 | Read access is logged only in `read_access_log`. Outbox gets a daily `access.summary` per partner group from a job. Reads never touch `outbox_head` |
| B1 | Multiple REVAL_REVERSAL mirrors per original allowed, `reversed_by` = first. Rerun step 1 mirrors only an original without existing auto reversal, step 2 is the new run's own pair |
| B2 | Clearing reset with difference document D: no item on D's offset line, one item on the reset's own offset line, D's offset becomes CLEARED by the reset. Exact item list with amounts written in 03 |
| B3 | Reversal of a document with open-item lines clears mirror open items against originals automatically, in the same transaction, with a clearing record (no document) |
| B4 | `migration-batches:rollback` is a mirror-producing command, reason code CORRECTION, mirrors keep the original type (incl. CLOSING) |
| B5, B6 | Add `sales-returns:reverse`, `purchase-returns:reverse` (permissions `sales.return`, `purchase.return`). Delivery reversal and dropship reversal rejected with `ORDER.DELIVERY_RETURNED` when returns exist |
| B7, B8 | `landed-costs:reverse` allowed only if no later movement on the cost key since the allocation. Allocation gets `reversal_of_allocation_id`, `reason_code`, status. Mirror stock type STOCK_REVERSAL. FIFO receipt reversal checks the layer's remaining quantity, else `INVENTORY.RECEIPT_CONSUMED` |
| B9 | Replacement-return settlement is the difference against the last settled snapshot along the `replaces_return_id` chain. `vat-returns:reverse-settlement` is rejected on a superseded return while a successor settlement is active |
| C1 | As-of stock reads sum quantity and value by posting date and processing order. Running columns only for "now". "By construction" holds only for the present |
| C2 | Negative stock handling is defined on the cost key only |
| C3 | Adjustment-in without value uses the last layer cost. Transfers move layers at layer cost. RETURN_OUT consumes the layer of the referenced original receipt |
| C4, C5 | `stock_cost_layer.layer_date`. TRANSIT and QUARANTINE accept TRANSFER_IN, STATUS_CHANGE, ADJUSTMENT and reject RECEIPT and ISSUE. QUARANTINE accepts only status QUARANTINE or BLOCKED |
| D1, D2 | THREE_WAY cap = received minus returned. ORDERED goods lines may be invoiced before delivery in both modes. A given delivery reference protects the delivery line from reversal (`ORDER.DELIVERY_INVOICED`) |
| D3 | `gr-ir:write-off` (permission `purchase.invoice`) clears a GR/IR residual to an account from account determination. Direct receipts without an order are matched through a receipt reference on the supplier invoice line |
| D4 | Open quantity = ordered minus delivered (received), not below zero. Over-tolerance does not count. One definition per side in 11, referenced by 16, 09 and credit exposure. Purchase "open to receive" defined |
| D5 | Migrated deliveries and receipts carry `value_lc` from the legacy record as valuation basis for returns. Dropship return links sales return to purchase return by `dropship_confirmation_id` |
| D6 | DRAFT company is a controlled exception for an unsealed batch. `abort` allowed in COMMITTING when no chunk is DONE. ROLLED_BACK is terminal and does not count as non-aborted. Rollback removes the batch's `legacy_id_map` rows |
| D7 | Migration OPENING and OPENING_ITEMS documents are split per transaction currency. One currency per stock document |
| D8 | Cutover `fiscal_year` gets `history_until_period`. History guards key on it and on `is_history` for years before the cutover year |
| D9 | "Non-zero" means non-zero in at least one currency. Implied rate at LC = 0 is derived from the other currency. TC = 0 system lines use rate 1 and rate type DERIVED. A rule table per system line type is written in 02 |
| E1 | The export backing a tenant deletion does not expire before `waiting_until` + 7 days |
| E2 | `retention_floor` stored minimum respected by the recomputation. `retention:extend` limited to classes that store a date |
| E3, E4 | `is_pii` extension values are logged without content. `master_change_log` gets `app_id` and a CLEAR action. `is_pii` immutable once values exist. Values cleared at anonymisation (also on orders and open items referencing the partner), never at deactivation. `extensions:set` on an anonymised partner rejected (`EXT.ENTITY_ANONYMISED`) |
| E5 | Feed visibility is separate from audit class. Subscription to a type grants feed access to its non-sensitive events. `access.*`, compliance and app events need `read.audit`. `is_audit` governs retention only |
| E6 | One event per batch status change plus `migration.sealed` with counts and chain ranges. `outbox:verify` accepts a migrated range as covered by the batch event |
| E7 | `extension_value.row_version`. `expected_version` per (entity, namespace). Extension writes never bump the owner row's `row_version` |
| E8 | ADR-146 amended: S changes without own chain use the outbox audit event as evidence |
| F1 | Tie-break: highest specificity, then lowest `rule_id`. Absent or expired registration is NOT_REGISTERED in the destination country. Partnerless documents need explicit origin and destination or are rejected |
| F2 | TAX_EVIDENCE: `line_no` and the four evidence fields added to 06. Evidence may be added in hard-closed periods as an append-only controlled exception with its own audit event. Existing evidence never changes |
| F3, F13 | 07 gets `fiscal_calendar`, `posting_window`, `number_series`, `number_series_assignment` and 06 their commands with permission `period.admin`. New commands `apps:register`, `:update`, `:deactivate`, `app-namespaces:create`, `app-subscriptions:set` with `expected_version` and `master_change_log`, permission `apps.manage` |
| F4 to F8 | `item_partner` PK gets `company_key` (0 = tenant). `business_calendar_exception` PK gets `created_at`, latest wins. `company_calendar_assignment` uses sentinel 0 instead of NULL in the key. V tables are ended by inserting a row with `valid_to`. Role rules: mode and type of an account cannot change while a role references it, one role per account, roles define `currency_code` and `tax_account` rules |
| F9 to F12 | `read.ext` removed as a separate permission (follows each `read.*`). `app_code` added to the 09 envelope, `events:rewind` and `apps.manage` need no app. Supply-demand: dropship and service lines under warehouse null, `group_by` and `ic` flag added, time `as_of` replaced by an outbox position as consistency token. `confirmed_date` rejected on create, set only by `change-line-dates` |

## 5. Decisions from fix pass 4 (2026-10-13)

| Ref | Decision |
|---|---|
| B1 | Revaluation rerun keeps the four-document form: mirror of R0, mirror of A0, new pair. Several mirrors per original, `reversed_by` = first, old run superseded before the new run is inserted |
| D9 | Rate at LC = 0 and GC or RC not 0: the ratio to the first non-zero amount, amounts are authoritative and the rate is derived |
| E7 | `extension_value` version is a stored counter per (entity, namespace) that never decreases (survives clears). `expected_version` compares to it |
| Apps | Add `apps:reactivate` (permission `apps.manage`) |
| gr-ir:write-off | New account role `GR_IR_WRITE_OFF` (open-item mode NONE), document type `GR_IR_WRITE_OFF`, reverse command `gr-ir:reverse-write-off` as owning command. Price difference and write-off stay separate in the ledger |
| FIFO edges | (a) A migrated receipt gets a synthetic cost layer from its legacy `value_lc`, so RETURN_OUT consumes it. (b) Adjustment-in without value and without an existing layer is rejected (`INVENTORY.VALUE_REQUIRED`). (c) Mixed transaction currencies in one stock document are rejected at P1 with `INVENTORY.CURRENCY_MIXED` |
| Agent judgements accepted | `migration.sealed` is the status-change event for SEALED (one event). `access.summary` per field group and company. DOCUMENT_SET hold does not cover partner master data (a PARTNER hold does). "Blocking state" of a data subject request is RECEIVED or IN_PROGRESS. A SCRAP return is retained as FULFILMENT. Order lifecycle CLOSED, LINE_ADDED, LINE_CANCELLED write `order.changed`. `CLEARING.NOT_RESETTABLE` for reversal clearings. `is_legal` mechanical effects (gap check is an error for legal series, `is_legal` immutable once numbers are issued). New lock class for `fiscal_period` and `fiscal_year` in 04 6.3. Settings `anchor_pending_deadline` and `extension_max_definitions`. `VAT.SUPERSEDED` (not `VAT.SUCCESSOR_SETTLEMENT_ACTIVE`). `tax_evidence` audit event name `vat.evidence_added`. Momsbestemmelse keeps `priority` with `rule_id` as last tie-break |

## 6. Status after the independent review (2026-10-14)

| Item | State |
|---|---|
| Specs | 02 to 13 at v0.6, 15 at v0.5, 16 at v0.4. All decisions of sections 4 and 5 applied by the fix and propagation passes |
| ADR log | ADR-001 to ADR-192, all ACCEPTED, no duplicates, ascending, no undefined references (script check) |
| Principles | 00 at v0.4, ADR range 001..192 |
| ERD | 18 pages, regenerated with the pass 4 and propagation changes. XML validated (18 diagrams, balanced tags, unique ids, no overlaps by the generator's checker). Not opened in draw.io, so layout is not visually verified |
| Error codes | Every `AREA.CODE` used in specs 02 to 16 is in 06 (script check, `APP.NOT_INACTIVE` added in the check) |
| Stale names | Only history and "replaces" notes remain (old head tables, `purchase_tolerance_override`, `vat-settlements:reverse` and similar) |

### Open items

| # | Item | Proposal |
|---|---|---|
| 1 | Synthetic FIFO layer for migrated receipts (optional `receipt_legacy_ref` on `STOCK_OPENING_LINE` and `stock_cost_layer`, L1) | **Confirmed 2026-10-14** |
| 2 | 03 open point 4: pre-lock re-check releases the locks and restarts with the pre-lock | **Confirmed 2026-10-14** |
| 3 | Section 4 of this document still shows the old F1 wording and B1 wording. Section 5 and ADR-191/192 supersede them | None, history |
| 4 | Provisional names: `fiscal-calendars:*`, `posting-windows:create`, `number-series:*`, `number-series-assignments:create`, `extension_counter`, `anchor_pending_deadline` | Confirm at first implementation |
