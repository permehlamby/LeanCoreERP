# Finance Core: Journal and Document Core, Table Specs

Status: DRAFT v0.6 for review, propagation pass of 2026-10-13 applied (17, section 5: document type GR_IR_WRITE_OFF, D9 rate rule at LC = 0, open question list closed; delta row in section 14), fix pass 4 of 2026-10-13 applied (decisions of 17, section 4: X1, X4, X5, A1, A2, B1 to B4, D9 and the mechanical items, section 14 lists them), re-baselined on 2026-10-13 (third pass: v0.4 decisions of 14, section 11 applied: mirror types, closed reason code list, external reversal codes, ADR wording in sections 12 and 13; second pass: reason codes, no `IC` type, VAT_RETURN scope_key, snapshot overwrite) for the scope review (14, sections 8 to 11): D1 (reversal rules, ADR-145), D2 (trigger and grant rules per column class, ADR-146), D3 (lock order, ADR-147), K1 (one `chain_head` table, ADR-158), K3 (outbox events, ADR-159), the deltas of 15 and 16, and the mechanical fixes of 14, sections 3 and 4 (section 14 lists them). Builds on 00-design-principles v0.4 and ADR-001 to ADR-174 (later ADRs as noted in the text).
Scope: document header, journal lines, tax lines, open-item attributes (side table), dimensions on lines, change log, hash chain.
Out of scope here (next domains): clearing records, number range tables, period/period-event tables, config tables (tax codes, rates, accounts, dimensions, posting rules), partner master data, revaluation/year-end run tables. They appear as referenced tables only.

## 1. Conventions

| Item | Convention |
|---|---|
| Types | Technology-neutral (ADR-001). `ID` = surrogate identifier (physical type decided in the mapping ADR). `AMT` = DECIMAL(19,4). `RATE` = DECIMAL(19,10). `CODE` = short VARCHAR. `TS` = UTC timestamp. `TEXT(n)` = VARCHAR(n). |
| Keys | `tenant_id` leads every PK, FK and index (ADR-002). `company_id` follows on all transaction tables (ADR-014). |
| Lock classes | **L1** locked, **L2** open in open period, **L3** frozen at hard close (applies to all L2 fields). **S** = system-maintained: changed only by a core command (not by the change-open-fields API). An S change is not written to `change_log`: it is evidenced by the owning command's own chained or audit event in the same transaction (D2, ADR-146, section 10). **S is a new class, accepted (ADR-031).** |
| Sign convention | One **signed** amount per currency: debit positive, credit negative. No separate debit/credit columns. D/C is derived (assumption A1). |
| Transaction currency | One transaction currency per document (assumption A2, consistent with ADR-016 and the balance rule in P4). |
| Enumerations | Stored as CODE with a check constraint or config reference. Lists in this spec are release 1 values. |

## 2. Table overview

| # | Table | Role | Mutability |
|---|---|---|---|
| 1 | `journal_document` | Document header, hash-chain node | L1 + L2 + S columns |
| 2 | `document_legal_snapshot` | Legal name/address snapshot (P8) | Anonymised by retention job only |
| 3 | `journal_line` | One row per posted line, incl. tax, rounding and FX lines | L1 + L2 columns |
| 3a | `journal_line_open_item` | Open-item attributes for control-account lines (1:1 optional) | L1 + L2 + S columns |
| 4 | `journal_line_dimension` | Free dimension values per line | L2 |
| 5 | `journal_line_tax` | Tax detail for lines of type TAX (1:1 extension of `journal_line`) | L1 |
| 6 | `journal_line_tax_base` | Links a tax line to the base line(s) it taxes | L1 |
| 6a | `journal_line_tax_evidence` | Evidence references for a tax line (defined in 08, 5.3) | L2 rows, never deleted |
| 7 | `change_log` | Field-level change records, generic, append-only | Insert only |
| 8 | `chain_head` | Chain head per tenant, company, chain type and scope, for all six chains. Serialisation point (K1, ADR-158). Replaces `ledger_chain_head` | S |
| 9 | `chain_anchor` | Periodic timestamped export of chain head (all chains). State PENDING then STAMPED (A2) | Insert, then one S update PENDING to STAMPED |

## 3. `journal_document`

PK: (`tenant_id`, `company_id`, `document_id`)
Unique: (`tenant_id`, `company_id`, `number_series_id`, `fiscal_year`, `sequence_no`)
Unique: (`tenant_id`, `company_id`, `number_series_id`, `fiscal_year`, `document_number`)
Unique: (`tenant_id`, `idempotency_key`)
Unique: (`tenant_id`, `company_id`, `ledger_id`, `chain_seq`)
Indexes (read APIs, 09): (`tenant_id`, `company_id`, `posting_date`, `chain_seq`), (`tenant_id`, `company_id`, `document_type`, `posting_date`), (`tenant_id`, `company_id`, `source_system`, `source_document_ref`), (`tenant_id`, `company_id`, `external_reference`), (`tenant_id`, `correlation_id`)

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| tenant_id | ID | N | L1 | |
| company_id | ID | N | L1 | |
| document_id | ID | N | L1 | Surrogate. Not the legal number |
| ledger_id | ID | N | L1 | Chain scope. R1: one ledger per company. Parallel ledgers later: one document per ledger, linked by a `posting_group_id` column added then (Q4 decided) |
| document_type | CODE | N | L1 | JOURNAL, AP_INVOICE, AP_CREDIT, AR_INVOICE, AR_CREDIT, PAYMENT, REVALUATION, REVAL_REVERSAL, YEAR_END, OPENING, VAT_SETTLE, REVERSAL, CLEARING, CLEARING_RESET (difference documents only, see 03-clearing), STOCK_RECEIPT, STOCK_ISSUE, STOCK_ADJUSTMENT, STOCK_TRANSFER, STOCK_STATUS_CHANGE, STOCK_COST_ADJUSTMENT, STOCK_RETURN, STOCK_OPENING (migration only), STOCK_REVERSAL (derived by inventory, see 10-inventory), DROPSHIP_COST (derived by order management, see 11-order-management), GR_IR_WRITE_OFF (derived by order management through `gr-ir:write-off`, see 11-order-management, 7.5; 17, section 5), HISTORY_SUMMARY, OPENING_ITEMS, CLOSING (migration, see 13-migration; `OPENING` is used by migration too; system types). `CLOSING` (X1, decided in 17, section 4) is the closing entry of a **migrated history year**, set by the HISTORY chunk. It is a document type of its own and not YEAR_END, so that `year-end:close` and `year-end:reopen` never see it. Its lines are of line_type CLOSING. It is excluded from P&L reads like YEAR_END and included in balance reads (05, 5.4). `GR_IR_WRITE_OFF` (17, section 5) is the document of `gr-ir:write-off` (11, 7.5): it clears a GR/IR residual against the account of the role `GR_IR_WRITE_OFF` (07, 6.3). It is system generated, has `origin` ORDER, and is reversed only by its owning command `gr-ir:reverse-write-off`. Its mirror keeps the type GR_IR_WRITE_OFF (no mirror type of its own). The price difference (`PRICE_VARIANCE`) and the write-off stay separate in the ledger |
| is_system_generated | BOOL | N | L1 | True for every type produced only by a core command and not accepted through `journal-documents:post`: REVALUATION, REVAL_REVERSAL (auto-reversal), YEAR_END, VAT_SETTLE, CLEARING, CLEARING_RESET, STOCK_*, DROPSHIP_COST, GR_IR_WRITE_OFF, HISTORY_SUMMARY, OPENING, OPENING_ITEMS, CLOSING. Revaluation documents are excluded from further revaluation (ADR-021) |
| number_series_id | ID | N | L1 | FK to number series (config) |
| fiscal_year | INT | N | L1 | Fiscal year of the **posting date** (A3, 04, 5.2). The series and the counter are resolved from it, never from the command time, so a late posting into a still open old year takes its number from the old year's series |
| sequence_no | BIGINT | N | L1 | Numeric counter value from the series for the fiscal year. Gapless (P6, ADR-007). Basis of the gapless check in `chains:verify` |
| document_number | CODE | N | L1 | Formatted from series format and `sequence_no` (04-number-range-and-period) |
| fiscal_period | INT | N | L1 | Regular period covering the posting date, or an explicitly requested special period (04). FK to `fiscal_period` |
| soft_close_override | BOOL | N | L1 | True if the posting used the "post to soft-closed period" authorisation (ADR-022). Makes such postings reportable |
| migration_batch_id | ID | Y | L1 | Set on documents committed by a migration batch (13-migration). Identifies migration documents and the controlled exceptions that applied |
| detail_digest | BINARY(32) | Y | L1 | Digest of detail records attached to the document that are not lines (e.g. revaluation run items, 05-revaluation-and-year-end). When several components exist (a stock document and an order record, 11), it is the SHA-256 of their digests concatenated in a fixed order. Part of the hash, so those details are covered by the ledger chain. Null if none |
| document_date | DATE | N | L1 | |
| posting_date | DATE | N | L1 | Must be inside the company posting window (ADR-025) |
| posted_at | TS | N | L1 | System timestamp UTC (P14) |
| transaction_currency | CODE | N | L1 | ISO 4217. One per document (A2) |
| status | CODE | N | S | `posted` or `reversed` only (ADR-013). Excluded from hash |
| reversal_of_document_id | ID | Y | L1 | Set on every mirror document (D1, ADR-145), see the constraints below. FK to this table |
| reversed_by_document_id | ID | Y | S | Set on the original when its **first** mirror is posted and never overwritten (B1). Further mirrors of the same original are possible only for REVALUATION and its automatic reversal in a rerun (05, 3.6) and are found through `reversal_of_document_id`. Excluded from hash |
| reversal_reason_code | CODE | Y | L1 | Required when `reversal_of_document_id` is set, for every mirror document including system-created ones (automatic reversal, rerun, clearing reset, reopen, migration rollback). Code list in the constraints below |
| auto_reversal_date | DATE | Y | L1 | Revaluation documents: date of the automatic reversal (ADR-021) |
| ic_transaction_id | ID | Y | L1 | Shared by both sides of an intercompany pair (ADR-016). Intercompany documents are ordinary AR_INVOICE and AP_INVOICE (and credits, payments) with this column set. `IC` is not a document type |
| ic_counterpart_company_id | ID | Y | L1 | Counterpart is found through the shared `ic_transaction_id`, no pointer column and no cyclic FK (Q5 decided, amends ADR-016 wording "pointer to the counterpart") |
| origin | CODE | N | L1 | **Set by the core, never by the caller** (X5, decided in 17, section 4): EXTERNAL (`journal-documents:post` and its variants, and every other caller-driven posting), ORDER (documents built by order management, 11, including the GR/IR write-off document), STOCK (documents derived by inventory, 10), VAT (VAT settlement and its mirror, 08), MIGRATION (documents of a migration batch, 13), SYSTEM (revaluation, year-end, clearing difference documents and their mirrors, 03 and 05). Rejection rules and the detection of order-built documents (06, 11) use `origin`, not `source_system`. A mirror document carries the `origin` of the command that posts it |
| source_system | CODE | N | L1 | P13. Free tracing field supplied by the caller or the integration. Carries no rule |
| source_document_ref | TEXT(100) | Y | L1 | P13. Null for core-internal documents |
| idempotency_key | TEXT(100) | N | L1 | Mandatory on all commands (ADR-004) |
| command_name | CODE | N | L1 | e.g. `journal-documents:post`, `journal-documents:reverse` |
| posted_by | TEXT(100) | N | L1 | User or service identity |
| correlation_id | TEXT(100) | N | L1 | |
| header_text | TEXT(200) | Y | L2 | |
| external_reference | TEXT(100) | Y | L2 | |
| chain_seq | BIGINT | N | L1 | Position in the chain for (tenant, company, ledger) |
| prev_hash | BINARY(32) | N | L1 | Hash of predecessor document. Genesis value for seq 1 |
| row_hash | BINARY(32) | N | L1 | See section 9 |
| hash_version | SMALLINT | N | L1 | Identifies algorithm and canonical field list. Allows scheme evolution without rewriting history |

Constraints (enforced in the posting command, with DB check/trigger as backstop):
- `reversal_of_document_id` is not null if and only if the document is a **mirror document** (D1, ADR-145): REVERSAL (`journal-documents:reverse`), REVAL_REVERSAL (05), CLEARING_RESET (03), STOCK_REVERSAL (10), and the mirror documents the owning commands create for the other derived documents (VAT settlement reversal before filing, 08. Dropship cost, order-built invoices and returns, GR/IR write-off through `gr-ir:reverse-write-off`, 11. The closing document at `year-end:reopen`, 05. The documents of a migration batch at `migration-batches:rollback`, 13). A mirror has `reversal_reason_code`, mirrors the original lines 1:1 (Q8) and is created only by the owning command.
- **Reason codes** (`reversal_reason_code`, D1 follow-up, 14 section 11): `AUTO_REVERSAL` (automatic reversal of a revaluation, 05), `RERUN` (revaluation rerun, 05), `REOPEN` (closing document at `year-end:reopen`, 05), `CORRECTION` (reversal to correct a posting), `ERROR` (posted in error), `RETURN` (return of goods or credit-note reversal through the owning command, 11). **The list is closed**: no other value is accepted and a change needs a new decision. `journal-documents:reverse` accepts only `CORRECTION` and `ERROR` from external callers and rejects `AUTO_REVERSAL`, `RERUN`, `REOPEN` and `RETURN` with `REVERSAL.REASON_NOT_ALLOWED` (06, 5.3). The other four are set only by the owning system commands. **Two vocabularies are kept apart (A4 follow-up):** `reversal_reason_code` on a mirror document is always one of the six codes above. The reason code in the payload of `year-end:reopen` and `periods:reopen` (vocabulary in 06) goes to `period_event.reason_code` (04, 3.4), and the mirror of `year-end:reopen` always gets `REOPEN`. The reason code of `clearings:reset` is limited to the codes callers may pass (`CORRECTION`, `ERROR`) and becomes the `reversal_reason_code` of the CLEARING_RESET document.
- **Rollback of a migration batch (B4, decided in 17, section 4).** `migration-batches:rollback` (13) is a mirror-producing command. Its mirrors carry `reversal_reason_code` = `CORRECTION`, mirror the original lines 1:1 and **keep the original `document_type`** of the document they reverse: HISTORY_SUMMARY, OPENING, OPENING_ITEMS, STOCK_OPENING and CLOSING. Stock documents of a rollback therefore do not use STOCK_REVERSAL. `origin` of these mirrors is MIGRATION.
- **Open items of a reversed document (B3, decided in 17, section 4).** When a mirror document carries open-item lines (any mirror-producing command, including the rollback), the same transaction clears each mirror open item against its original: one clearing per account and partner group, `clearing_type` CLEAR, `clearing_method` REVERSAL, no difference document because the mirror reverses the lines 1:1 and so nets to zero in every currency (03, 5.7). The command therefore also takes the CLEARING head and the original open items in the lock order of 04, 6.3. An original with cleared or partly cleared lines is still rejected first (`REVERSAL.HAS_CLEARED_LINES`).
- **Mirror document types (decided, 14 section 11).** Four mirror types exist and stay: REVERSAL (`journal-documents:reverse`), REVAL_REVERSAL (05), CLEARING_RESET (03) and STOCK_REVERSAL (10). Only types without their own mirror type keep the `document_type` of the original: YEAR_END, VAT_SETTLE, DROPSHIP_COST, GR_IR_WRITE_OFF, the migration types of a rollback (HISTORY_SUMMARY, OPENING, OPENING_ITEMS, STOCK_OPENING, CLOSING, see below), and order-built invoices and credit notes (AR_INVOICE, AR_CREDIT, AP_INVOICE, AP_CREDIT with an order reference, 11). This is the one place where the rule is stated, other specs refer to it. Every mirror, of either kind, carries `reversal_of_document_id`, so it is recognised by that column and not by its type.
- **Derived documents** are reversible only through their owning command: STOCK_*, DROPSHIP_COST, GR_IR_WRITE_OFF (`gr-ir:reverse-write-off`), VAT_SETTLE, HISTORY_SUMMARY, OPENING, OPENING_ITEMS, CLOSING (these four and STOCK_OPENING only through `migration-batches:rollback`), order-built invoices (11), CLEARING and CLEARING_RESET (`clearings:reset`), REVALUATION and REVAL_REVERSAL (`revaluation:run` rerun), YEAR_END (`year-end:reopen` only). `journal-documents:reverse` rejects them, so the atomic stock, order and clearing couplings cannot be broken by a direct reversal.
- Sum of `amount_tc`, `amount_lc`, `amount_gc` over lines is 0 (and `amount_rc` where configured). Cross-row, so enforced by the command, plus a verification query in the chain check.
- A document always has at least one line. A clearing without differences creates no document (03-clearing).
- `posting_date` inside window, period status allows posting (ADR-022, ADR-025). **Serialisation with hard-close (A1, decided in 17, section 4):** `periods:hard-close` and `year-end:close` lock the LEDGER and CLEARING heads before the PERIOD_EVENT head (04, 6.3). A posting may pre-read the period status for an early rejection without a lock, but it reads `fiscal_period` and `fiscal_year` again **in share mode after it holds its LEDGER head lock** and rejects if the status no longer allows posting. A posting therefore cannot commit into a period that hard-closes concurrently. The same re-check applies to every command that depends on a period status (04, 7.1, 7.2).
- **Exchange rates and `rate_type` of system lines (D9, decided in 17, section 4):** see the rule table in section 5.2. A closing document line, or a clearing, revaluation or other system line, whose amount is zero in the transaction currency or in the company currency follows that table. "Non-zero" in the rules of this spec and of 05 means non-zero in at least one of the currencies TC, LC, GC and RC.

## 4. `document_legal_snapshot`

PK: (`tenant_id`, `company_id`, `document_id`)  1:1 with `journal_document`. Not part of the hash, because the retention job anonymises it (P8, ADR-008).

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| tenant_id, company_id, document_id | ID | N | L1 | |
| partner_id | ID | Y | L1 | Header-level partner, if the document has one |
| legal_name | TEXT(200) | Y | L1* | Minimal data required on invoice or voucher |
| address_line_1, address_line_2, postal_code, city | TEXT | Y | L1* | |
| country_code | CODE | Y | L1* | |
| partner_vat_id | TEXT(30) | Y | L1* | |
| electronic_address, electronic_address_scheme | TEXT(100), CODE | Y | L1* | Buyer endpoint as on the invoice (EN 16931, 12, 9.2) |
| legal_registration_id | TEXT(50) | Y | L1* | Buyer legal registration id as on the invoice (12, 9.2) |
| anonymised_at | TS | Y | S | Set by the retention job |
| retention_until | DATE | N | S | From company/country retention config, resolved at posting. Reclassified from L1 to S (D2, ADR-146) because `retention:extend` updates it (12). Never shortened. Outside the hash (the snapshot is not hashed). The extension is an audit event (`retention.extended`, 15, 6) |

\* L1 for all users and commands. Only the retention job may overwrite. This is a legal L1 exception (ADR-008) and is recorded in `change_log` with entity type LEGAL_SNAPSHOT and reason `SYSTEM` (8). The one defined exception to L1. `order_address_snapshot` (11) is not covered by this table and has its own spec. Its SYSTEM log entries (retention overwrite) go to `order_change_log` with entity type ORDER_ADDRESS_SNAPSHOT (11, 4.7), not to `change_log`: `change_log` uses reason `SYSTEM` only for LEGAL_SNAPSHOT.

## 5. `journal_line`

PK: (`tenant_id`, `company_id`, `document_id`, `line_no`)
FK: (`tenant_id`, `company_id`, `document_id`) to `journal_document`.
Indexes (all lead with `tenant_id`, `company_id`):
- (account_id, posting_date) for account and period queries without a header join
- (fiscal_year, fiscal_period, account_id) for trial balance
- (cost_center_id), (project_id), (profit_center_id) for reporting cuts
- (posting_date, account_id) for date-range scans of movements (09)

### 5.1 Core columns

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| tenant_id, company_id, document_id | ID | N | L1 | |
| line_no | INT | N | L1 | 1..n within the document |
| ledger_id | ID | N | L1 | P10, ADR-009. Equals header value in R1 |
| line_type | CODE | N | L1 | BASE, TAX, TAX_NONDEDUCTIBLE, ROUNDING, FX_REALISED, FX_UNREALISED, REVAL_ADJUSTMENT, TAX_ROUNDING, CLOSING, OPENING, CLEARING_DIFF, HISTORY_PL (13-migration). `TAX_NONDEDUCTIBLE` (ADR-204, 19, 4.1) is the non-deductible part of an input tax: generated by the core, on the account, dimensions and line text of the base line it belongs to, no `tax_code_id`, exempt from the tax code rule of the account (ADR-029) |
| account_id | ID | N | L1 | FK to GL account (company CoA) |
| posting_date | DATE | N | L1 | Copied from the header (Q3, decided). Must equal the header value, checked by the posting command and the chain verification |
| fiscal_year | INT | N | L1 | Copied from the header |
| fiscal_period | INT | N | L1 | Copied from the header |
| line_text | TEXT(200) | Y | L2 | |
| reversed_line_no | INT | Y | L1 | On a reversal document: the original line reversed |
| tax_code_id | ID | Y | L1 | On BASE lines per account rule (ADR-029). Version of the effective-dated code resolved at posting is stored on the tax detail table |

### 5.2 Amounts (ADR-020)

All L1. Signed (A1).

| Column | Type | Null | Notes |
|---|---|---|---|
| amount_tc | AMT | N | Transaction currency. Currency from header |
| amount_lc | AMT | N | Company currency |
| currency_lc | CODE | N | |
| rate_lc | RATE | N | Rate used. 1 if TC = LC |
| rate_lc_table | RATE | Y | Table rate when the caller overrode (ADR-019) |
| amount_gc | AMT | N | Group currency. IC pairs copy the group amount (ADR-016) |
| currency_gc | CODE | N | |
| rate_gc | RATE | N | |
| rate_gc_table | RATE | Y | |
| amount_rc | AMT | Y | Reporting currency. Null if not configured for the company |
| currency_rc | CODE | Y | |
| rate_rc | RATE | Y | |
| rate_rc_table | RATE | Y | |
| rate_type | CODE | N | `SPOT` or `CLOSING` (ADR-019). The rate type `VAT` is used only for the VAT reporting amounts of tax lines (08). `DERIVED` for system lines whose amounts are sums of posted amounts (year-end closing lines), where the rates are the implied ratios |
| rate_date | DATE | N | Date of the rate actually used (may be earlier than posting date within the fallback window). For `DERIVED` lines the posting date of the document |

**Rate rules per system line type (D9).** Rates and rate types of lines whose amounts are derived from other amounts. Table rates (`rate_*_table`) are null on all `DERIVED` lines.

| Line type | Situation | `rate_lc` | `rate_gc`, `rate_rc` | `rate_type` |
|---|---|---|---|---|
| BASE, TAX, TAX_NONDEDUCTIBLE, ROUNDING, TAX_ROUNDING | TC non-zero | Rate of ADR-019 (1 if TC = LC) | Rate of ADR-019 | SPOT or CLOSING as posted |
| FX_REALISED, CLEARING_DIFF (03, 3.1) | Always `amount_tc` = 0 | 1 | 1 | DERIVED |
| FX_UNREALISED, REVAL_ADJUSTMENT (05, 3.3) | Always `amount_tc` = 0 | 1 | 1 | DERIVED |
| CLOSING (YEAR_END document, 05, 5.2) | `amount_tc` = `amount_lc`, non-zero | 1 | Implied ratio `amount_x / amount_lc` | DERIVED |
| CLOSING, OPENING, HISTORY_PL (migration, 13) | `amount_tc` = 0 or `amount_lc` = 0 (D9, 17, section 5) | 1 | Let A be the **first non-zero amount in the order LC, GC, RC**. `rate_x` = `amount_x` / A for each other currency, and 1 for the currency of A itself. A currency whose own amount is 0 gets rate 1 | DERIVED |

General rules: a rate is never null and never 0. When the ratio to LC cannot be formed because `amount_lc` is 0 (LC = 0 and GC or RC not 0), the rates are the ratio to the first non-zero amount as in the last row (D9, final). The amounts are authoritative and the rates are derived from them: the rates on `DERIVED` lines are informational and are not used to recompute amounts. A document whose lines are non-zero only in GC or RC (LC = 0 on all lines) is valid, because "non-zero" means non-zero in at least one currency.

### 5.3 Dimensions (ADR-010, P11)

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| cost_center_id | ID | Y | L2 | Rule per account: mandatory, optional, forbidden |
| project_id | ID | Y | L2 | |
| profit_center_id | ID | Y | L2 | Profit center/segment |
| business_unit_id | ID | Y | L2 | Branch/business unit as dimension (ADR-014) |

Free dimensions: see `journal_line_dimension`. Any L2 change is validated as a posting against account rules and dimension validity periods (P3).

### 5.4 Intercompany marker

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| ic_counterpart_company_id | ID | Y | L1 | On IC lines (ADR-017, ADR-226). Intercompany flag is derived: not null |

**Which lines carry the marker (ADR-226, 23, 6).** The commands that post an intercompany document pair (`intercompany-documents:post`, `ic-invoices:create`) set the marker on the generated IC account lines, on the control lines (AR or AP, partner of the counterpart) and on the BASE lines (revenue, expense, inventory, asset). They never set it on TAX, TAX_NONDEDUCTIBLE, ROUNDING or TAX_ROUNDING lines: tax is owed to the tax authority and is not eliminated. The stock and cost journals derived by `ic-deliveries:post` are outside this rule: they get no marker from this delta (10, 11, 6.7, 23, O1). A line posted by `journal-documents:post` has no marker, because the request has no such field (06, 5.2). The column is already L1 and part of the line hash (9.3): which lines carry it changes for documents posted from this delta on, so no `hash_version` is needed and no document is rewritten. Reads use the marker as filter and group key (09, 4.1).

Open-item attributes are not on this table. They live in `journal_line_open_item` (section 5A, ADR-005, P9). A line is an open item if and only if that row exists.

## 5A. `journal_line_open_item`

Side table (1:1, optional) for lines on open-item-managed accounts. Each GL account has an open-item mode in configuration: NONE, CONTROL (AR/AP control account, partner mandatory) or GL_OPEN_ITEM (open-item-managed GL account such as bank clearing, no partner). Row existence replaces an `is_open_item` flag, and is decided at posting from the account's mode, so later account config changes never reclassify posted lines. The posting command rejects a CONTROL or GL_OPEN_ITEM line without this row, rejects this row on a NONE account, and rejects a CONTROL line without partner (P9). `partner_id` is null only for GL_OPEN_ITEM accounts (change driven by 03-clearing, Q7). **Exemption:** lines of type REVAL_ADJUSTMENT on a CONTROL or GL_OPEN_ITEM account carry no open-item row and no partner (system-generated, the partner is carried by `revaluation_run_item`, 05, 3.3). Other system line types (CLOSING, HISTORY_PL, OPENING) are exempt only as their owning specs state (05, 13).
PK: (`tenant_id`, `company_id`, `document_id`, `line_no`)  FK to `journal_line`.
Indexes: (`tenant_id`, `company_id`, `partner_id`, `clearing_status`), (`tenant_id`, `company_id`, `partner_id`, `due_date`).
Account is not repeated here. Aging queries join `journal_line` for `account_id` and amounts (see Q3 on denormalisation).

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| tenant_id, company_id, document_id, line_no | ID | N | L1 | |
| partner_id | ID | Y | L1 | Mandatory for CONTROL accounts, null for GL_OPEN_ITEM. Reference only, no PII (P8) |
| baseline_date | DATE | Y | L2 | Start of payment term calculation |
| payment_terms_id | ID | Y | L2 | |
| due_date | DATE | Y | L2 | Mandatory for CONTROL accounts |
| discount_due_date | DATE | Y | L2 | |
| discount_amount_tc | AMT | Y | L2 | Cash discount available. Informational: the core posts no discount (Q9 decided). A discount is applied by the caller as an ordinary document (e.g. credit memo) that is then cleared against the invoice (03-Q1) |
| payment_block | CODE | Y | L2 | |
| dunning_block | CODE | Y | L2 | |
| payment_reference | TEXT(100) | Y | L2 | Structured creditor/debtor reference |
| bank_account_id | ID | Y | L2 | Partner bank account for payment |
| legacy_document_date, legacy_posting_date | DATE | Y | L1 | Original dates of a migrated open item. Used for aging and tracing, the document's own posting date is the cutover date (13, 6.3) |
| legacy_document_ref | TEXT(100) | Y | L1 | Legacy document and line reference of a migrated open item |
| clearing_status | CODE | N | S | OPEN, PARTIAL, CLEARED. Maintained by the clearing commands. Excluded from hash |
| cleared_amount_tc | AMT | N | S | Sum of `clearing_item` TC amounts for this line. Remaining open amount = `journal_line.amount_tc` minus this |
| cleared_amount_lc | AMT | N | S | Same for local currency. Needed for revaluation and aging in LC |
| cleared_amount_gc | AMT | N | S | |
| cleared_amount_rc | AMT | Y | S | |
| last_clearing_id | ID | Y | S | FK to `clearing` (03-clearing), the most recent clearing touching this line |

The side table isolates all S columns of the line domain in one table with its own grant (section 10). `journal_line` itself holds only L1 and L2 columns.

## 6. `journal_line_dimension`

PK: (`tenant_id`, `company_id`, `document_id`, `line_no`, `dimension_type_id`)
FK to `journal_line`. FK to dimension type and dimension value (config/master).

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| tenant_id, company_id, document_id, line_no | ID | N | L1 | |
| dimension_type_id | ID | N | L1 | Tenant-defined free dimension type |
| dimension_value_id | ID | Y | L2 | Changes logged. Clearing a value sets it to null (tombstone). The row is never deleted, so the transaction layer has no DELETE at all (Q6 decided) |

## 7. Tax lines

### 7.1 `journal_line_tax`

1:1 extension for lines where `line_type = TAX` (ADR-027). The tax amount itself is the `journal_line` amount (four currencies).
PK: (`tenant_id`, `company_id`, `document_id`, `line_no`)  FK to `journal_line`.

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| tenant_id, company_id, document_id, line_no | ID | N | L1 | |
| tax_code_id | ID | N | L1 | |
| tax_code_version_id | ID | N | L1 | Effective-dated version valid at posting. Gives rate, VAT account and return box mapping as of posting (ADR-026, ADR-028) |
| tax_type | CODE | N | L1 | OUTPUT, INPUT, EXEMPT, ZERO_RATED |
| tax_rate | RATE | N | L1 | Percent. Copied from the version, so later changes never alter it |
| base_amount_tc | AMT | N | L1 | Sum of linked base lines |
| base_amount_lc | AMT | N | L1 | |
| base_amount_gc | AMT | N | L1 | |
| base_amount_rc | AMT | Y | L1 | |
| determined_tax_code_id | ID | N | L1 | Result of rule determination (ADR-026) |
| tax_class_id | ID | Y | L1 | Tax class used in the determination: the line's supplied class, else the account's default class (07, 8.5). Null if neither existed |
| origin_country | CODE | N | L1 | Origin used in the determination: line value, else document value, else the default by direction (07, Q1). Stored so the determination is reproducible |
| destination_country | CODE | N | L1 | Destination used, same resolution |
| partner_vat_status | CODE | Y | L1 | VAT status of the partner in the relevant country at posting (REGISTERED, NOT_REGISTERED), as used by the rule. Null if the rule did not use it |
| tax_date | DATE | N | L1 | Tax point: the line's supplied date, else the header's `tax_date`, else `document_date` (08, 5.1). Decides the VAT period |
| vat_currency | CODE | N | L1 | Currency of the VAT reporting amounts: the registration's return currency (08, 5.1a) |
| vat_base_amount | AMT | N | L1 | Base amount for VAT returns, in `vat_currency` |
| vat_tax_amount | AMT | N | L1 | Tax amount for VAT returns, in `vat_currency` |
| vat_rate | RATE | N | L1 | Rate transaction currency to `vat_currency` used for the two amounts |
| vat_rate_date | DATE | Y | L1 | Date of the VAT rate row used. Null when the booking rate was used |
| is_override | BOOL | N | L1 | True when the supplied code differs from the determined one. Only valid for a permitted alternative |
| tax_amount_supplied_tc | AMT | Y | L1 | If caller supplied the tax amount. Difference to computed goes to the tax rounding account (ADR-027) |
| rounding_mode | CODE | N | L1 | PER_DOCUMENT or PER_LINE, from company setting at posting |
| origin_eu | BOOL | N | L1 | True if `origin_country` had EU membership at `tax_date` (`ref_country_eu_membership`, 07, 4). A country without a valid membership row is false. Resolved input of the determination (ADR-200, 07, 8.4) |
| destination_eu | BOOL | N | L1 | Same for `destination_country` (ADR-200) |
| deductible_pct | RATE | N | L1 | Fixed deductible share in percent from the code version at posting. 100 for every tax line without partial deduction (ADR-203, 19, 5.1) |
| deduction_key | CODE | Y | L1 | Deduction key used (07, 8.6), null without |
| deduction_key_rate | RATE | Y | L1 | Rate of the key at `tax_date`, copied |
| deduction_share | RATE | N | L1 | Effective share in percent, 10 decimals. 100 when no partial deduction. Always 100 on the OUTPUT side of a reverse-charge pair, the split applies to the deductible (INPUT) side only |
| deduction_cap_amount_tc | AMT | Y | L1 | The supplied deductible tax amount when the code has `deduction_cap` = SUPPLIED_AMOUNT (06, 3.1) |
| gross_tax_amount_tc | AMT | N | L1 | The computed tax before the split, in transaction currency. Equals the line amount when `deduction_share` is 100. The line amount itself is the deductible part |

Tax lines are created for every taxed base, including EXEMPT and ZERO_RATED bases with a zero tax amount (Q2 decided), so return box totals and base amounts come from this table alone.

### 7.2 `journal_line_tax_base`

PK: (`tenant_id`, `company_id`, `document_id`, `tax_line_no`, `base_line_no`)
FK (tax line) to `journal_line_tax`. FK (base line) to `journal_line`. Both in the same document.
Many-to-many, because per-document rounding makes one tax line cover several base lines.

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| tenant_id, company_id, document_id | ID | N | L1 | |
| tax_line_no | INT | N | L1 | |
| base_line_no | INT | N | L1 | |
| base_amount_tc | AMT | N | L1 | Portion of this base line allocated to the tax line. Sum per tax line equals `journal_line_tax.base_amount_tc` |
| nondeductible_tax_tc | AMT | N | L1 | Part of the non-deductible tax allocated to this base line (largest remainder over `base_amount_tc`, ties to the lowest `line_no`, 19, 4.4). 0 when nothing is allocated (ADR-204) |
| nondeductible_line_no | INT | Y | L1 | The `TAX_NONDEDUCTIBLE` line of this allocation. Null when `nondeductible_tax_tc` is 0 |

Check at P6 (06): per tax line the sum of `nondeductible_tax_tc` equals `gross_tax_amount_tc` minus the tax line amount in transaction currency, and each `TAX_NONDEDUCTIBLE` line equals its allocation in all four currencies.

### 7.3 `journal_line_tax_evidence`

Defined in 08, 5.3 (L2 rows, 1:n extension of `journal_line_tax`). Rows are added and changed through `journal-documents:change-open-fields` with entity type `TAX_EVIDENCE` (`sub_key` = `evidence_no`), so they are logged in `change_log` and follow the L2 rules of 04, 7.2. Not part of the hash. No DELETE.

## 8. `change_log`

Generic, append-only (your decision). Written by the `journal-documents:change-open-fields` command for **L2 changes only** (D2, ADR-146), plus the one logged L1* exception (the retention job overwrite of the legal snapshot, section 4) and the L2 note of a clearing (03). S columns are not logged here: each S change has its own chained event instead (the mirror document and its chain node for `status` and `reversed_by_document_id`, the `clearing` and `clearing_item` records for the open-item S columns, the `period_event` for period status, the audit event of 15 for `retention_until`). This amends ADR-031, which logged S changes here with reason `SYSTEM`. `chains:verify` recomputes the S columns of `journal_line_open_item` from `clearing_item` (03-clearing).
PK: (`tenant_id`, `change_id`)
Index: (`tenant_id`, `company_id`, `document_id`, `line_no`), (`tenant_id`, `company_id`, `clearing_id`), (`tenant_id`, `correlation_id`), (`tenant_id`, `changed_at`)

| Column | Type | Null | Notes |
|---|---|---|---|
| tenant_id | ID | N | |
| change_id | ID | N | |
| change_set_id | ID | N | Groups all field changes of one command call |
| company_id | ID | N | |
| entity_type | CODE | N | DOCUMENT, LINE, LINE_OPEN_ITEM (L2 columns of `journal_line_open_item`, 5A: `baseline_date`, `payment_terms_id`, `due_date`, `discount_due_date`, `discount_amount_tc`, `payment_block`, `dunning_block`, `payment_reference`, `bank_account_id`), LINE_DIMENSION, LEGAL_SNAPSHOT, CLEARING (03-clearing), TAX_EVIDENCE (08, 5.3) |
| document_id | ID | Y | Set for every entity type except CLEARING. Null for CLEARING |
| clearing_id | ID | Y | Set only for entity type CLEARING (the `note` of a `clearing`, 03, 4.1). Exactly one of `document_id` and `clearing_id` is set (check constraint) |
| line_no | INT | Y | Null for header changes, and for CLEARING. Set for LINE, LINE_OPEN_ITEM, LINE_DIMENSION and TAX_EVIDENCE |
| sub_key | TEXT(50) | Y | E.g. `dimension_type_id` for LINE_DIMENSION |
| field_name | CODE | N | Physical column name |
| value_type | CODE | N | AMOUNT, DATE, TEXT, ID, CODE, BOOL |
| old_value | TEXT(500) | Y | Canonical text form. Null on insert of a value |
| new_value | TEXT(500) | Y | Null when a value is cleared |
| lock_class | CODE | N | `L2` for every change of the change command. `L1*` for the retention job overwrite of a legal snapshot field (entity type LEGAL_SNAPSHOT, the one logged L1 exception, section 4). S changes are not logged here |
| period_status | CODE | N | Status of the governing period when changed: OPEN or SOFT_CLOSED for L2 changes (HARD_CLOSED rejects, L3). `NONE` for the L1* retention overwrite, which is allowed in any period and is not period-governed. The governing period is the period of the document header (`fiscal_year`, `fiscal_period`) for DOCUMENT, LINE, LINE_OPEN_ITEM, LINE_DIMENSION and TAX_EVIDENCE, and for entity type CLEARING the period of the clearing itself (`clearing.fiscal_year`, `clearing.fiscal_period`, resolved from `clearing_date`, 03, 4.1) |
| reason_code | CODE | N | Mandatory for L2 changes. `SYSTEM` only for the retention job overwrite of the snapshot (`lock_class` L1*) |
| reason_text | TEXT(500) | Y | |
| changed_by | TEXT(100) | N | |
| changed_at | TS | N | UTC |
| correlation_id | TEXT(100) | N | |
| idempotency_key | TEXT(100) | N | The change command is idempotent too (ADR-004) |

No UPDATE or DELETE grants for any role. L1 changes never appear here, because they are impossible.
Dimension changes (L2) carry the validation result in the command response, not in this table.

## 9. Hash chain (document-level, proposed resolution of ADR-006)

### 9.1 Scope and chaining

- One chain per (`tenant_id`, `company_id`, `ledger_id`). Node = `journal_document`.
- `chain_seq` starts at 1 and increments by 1. `prev_hash` of seq n equals `row_hash` of seq n-1. Seq 1 uses a fixed genesis value per chain.
- Postings are appended in posting-transaction order. This matches number order (P6, ADR-025): both follow commit order, not posting date.
- Reopened periods and revaluation reruns only append new documents. Nothing is rewritten (ADR-023, ADR-021). This closes the open item in P5 on revaluation reruns.
- IC pairs (ADR-016): two documents in two companies, so they sit in two chains and are linked by `ic_transaction_id`.

### 9.2 `chain_head` (K1, ADR-158)

One table holds the head of every chain of the platform. It replaces `ledger_chain_head` here, `clearing_chain_head` (03), `period_event_chain_head` (04), `vat_return_chain_head` (08), `stock_chain_head` (10) and `fulfilment_chain_head` (11). The event and document tables stay as they are.

PK: (`tenant_id`, `company_id`, `chain_type`, `scope_key`)

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| tenant_id, company_id | ID | N | L1 | |
| chain_type | CODE | N | L1 | LEDGER, CLEARING, PERIOD_EVENT, VAT_RETURN, STOCK, FULFILMENT |
| scope_key | TEXT(50) | N | L1 | `ledger_id` for LEDGER, empty string for the other five. VAT_RETURN is one chain per company over all return types (K1) |
| last_seq | BIGINT | N | S | Last `chain_seq` (or `event_seq`) of the chain |
| last_hash | BINARY(32) | N | S | `row_hash` of the last node |
| hash_version | SMALLINT | N | S | Hash scheme of the chain (the node rows carry their own `hash_version`) |
| updated_at | TS | N | S | |

The posting command locks the number-range counter row first and then the LEDGER row inside the posting transaction (counters before heads, X4), reads `last_hash`, writes the new document, and updates the head. That is the one serialisation point per chain. One verification routine (`chains:verify`) and one lock-order rule serve all six chains: heads are locked sorted by the tuple (chain type in the order above, company, scope_key), inside the authoritative lock order of 04, 6.3 (D3, ADR-147, X4). The outbox position lock is always last (K3, ADR-159). `periods:hard-close` and `year-end:close` lock the LEDGER and CLEARING heads of the company before the PERIOD_EVENT head (A1), so a hard-close waits for postings and clearings in flight and the revaluation stale check (05, 3.7) sees a stable ledger. Commands that only read heads (`anchors:create`, `chains:verify`) take share locks in the same sorted order and never write a head they do not own.

### 9.3 Hash input (hash_version 1 and 2)

`row_hash = H( hash_version | tenant_id | company_id | ledger_id | chain_seq | prev_hash | header_L1_fields | line_digest )`

- `header_L1_fields`: all L1 columns of `journal_document` except `chain_seq`, `prev_hash`, `row_hash`, in the fixed order of section 3.
- `line_digest = H( concat of line_hash(line_no) for lines ordered by line_no )`, where `line_hash` covers all L1 columns of `journal_line`, plus the L1 columns of `journal_line_tax`, `journal_line_tax_base` and `journal_line_open_item` (`partner_id`, `legacy_document_date`, `legacy_posting_date`, `legacy_document_ref`) for that line. Clearings have their own hash chain (03-clearing, section 6), which includes the `row_hash` of a linked difference document.
- Excluded by construction: all L2 columns, all S columns, `journal_line_dimension`, `document_legal_snapshot`, `change_log`. These can legitimately change or be anonymised.
- Canonical serialisation: fixed column order, UTF-8, ISO dates, amounts as plain decimal strings with fixed scale, NULL as an explicit marker. Algorithm, canonical form and field list are identified by `hash_version`.

**`hash_version` 2 (ADR-200, ADR-204).** The input is the same as above. The field list of `line_hash` additionally covers the new L1 columns of `journal_line_tax` (`origin_eu`, `destination_eu`, `deductible_pct`, `deduction_key`, `deduction_key_rate`, `deduction_share`, `deduction_cap_amount_tc`, `gross_tax_amount_tc`) and of `journal_line_tax_base` (`nondeductible_tax_tc`, `nondeductible_line_no`) in the order of their tables, and `line_type` may be `TAX_NONDEDUCTIBLE`. New documents are written with `hash_version` 2. Documents posted before keep `hash_version` 1 and verify with the version 1 field list: `chains:verify` selects the field list per document by its `hash_version`, and a chain may contain both. No document is rewritten. Migrated documents (13) take the version in force at the commit.

Algorithm (Q7 decided): unkeyed SHA-256 (all `hash_version` values). Anyone with the data can verify the chain. Protection against an attacker with database write access comes from the externally timestamped anchors of 9.5.

### 9.3a Canonical form of `hash_version` 2 (decided at implementation, 2026-10-21, ADR-211)

9.3 says "fixed column order, UTF-8, ISO dates, plain decimal strings with fixed scale, explicit NULL marker". That does not let two implementations produce the same bytes. This section fixes them. Test vectors are in `test-vectors/hash-v2.json` and every implementation must reproduce them (ADR-211: the C# library and `test-vectors/verify-hash-v2.mjs` do). Only `hash_version` 2 is implemented: no document of version 1 exists yet, so the version 1 field list of 9.3 is history only.

**Value encoding** (by column type):

| Type | Bytes |
|---|---|
| ID | The UUID in lower case with hyphens, 36 ASCII characters |
| CODE, TEXT(n) | The UTF-8 bytes of the stored text. The hash applies no normalisation: commands store text in Unicode normal form C |
| INT, SMALLINT, BIGINT | ASCII decimal, a minus sign for negative numbers, no plus sign, no leading zeros, `0` for zero |
| BOOL | `true` or `false` |
| DATE | `YYYY-MM-DD` |
| TS | UTC, `YYYY-MM-DDTHH:MM:SS.ffffffZ`, always six fraction digits (microseconds) |
| AMT | Plain decimal with exactly 4 fraction digits, minus sign for negative, no thousands separator. Zero is `0.0000`, never `-0.0000`. A value with more than 4 fraction digits is an error, it is never rounded |
| RATE | As AMT with exactly 10 fraction digits |
| BINARY(32) | 64 lower-case hexadecimal characters |

**Field.** A 4-byte unsigned length in big-endian order, then the value bytes. A NULL value is the length `FFFFFFFF` without value bytes. An empty text is length 0 and is not NULL.

**Block.** The concatenation of fields, wrapped as one field (its length covers all inner fields). A missing optional block is a NULL field. A list is a block that holds one field per element.

**Row hash** = SHA-256 of the concatenation of these fields, in this order: `hash_version` (INT), `tenant_id`, `company_id`, `ledger_id`, `chain_seq` (BIGINT), `prev_hash` (hex), the **header block**, `line_digest` (hex).
- Header block: the L1 columns of `journal_document` in the order of section 3, without `chain_seq`, `prev_hash` and `row_hash`: `tenant_id`, `company_id`, `document_id`, `ledger_id`, `document_type`, `is_system_generated`, `number_series_id`, `fiscal_year`, `sequence_no`, `document_number`, `fiscal_period`, `soft_close_override`, `migration_batch_id`, `detail_digest`, `document_date`, `posting_date`, `posted_at`, `transaction_currency`, `reversal_of_document_id`, `reversal_reason_code`, `auto_reversal_date`, `ic_transaction_id`, `ic_counterpart_company_id`, `origin`, `source_system`, `source_document_ref`, `idempotency_key`, `command_name`, `posted_by`, `correlation_id`, `hash_version`. The S columns `status` and `reversed_by_document_id` and the L2 columns `header_text` and `external_reference` are not in it.
- `line_digest` = SHA-256 of the concatenation of the **raw 32 bytes** of `line_hash` of each line, ordered by `line_no`.
- `line_hash` = SHA-256 of the concatenation of four fields: the **line block**, the **tax block** (NULL unless the line has a `journal_line_tax` row), the **tax base list** (a block with one block per `journal_line_tax_base` row whose `tax_line_no` is this line, ordered by `base_line_no`, an empty block when there is none) and the **open item block** (NULL unless the line has a `journal_line_open_item` row).
- Line block, the L1 columns of `journal_line` in the order of 5.1, 5.2 and 5.4: `tenant_id`, `company_id`, `document_id`, `line_no`, `ledger_id`, `line_type`, `account_id`, `posting_date`, `fiscal_year`, `fiscal_period`, `reversed_line_no`, `tax_code_id`, `amount_tc`, `amount_lc`, `currency_lc`, `rate_lc`, `rate_lc_table`, `amount_gc`, `currency_gc`, `rate_gc`, `rate_gc_table`, `amount_rc`, `currency_rc`, `rate_rc`, `rate_rc_table`, `rate_type`, `rate_date`, `ic_counterpart_company_id`. The L2 columns (`line_text` and the typed dimensions) are not in it.
- Tax block: all columns of 7.1 in table order (`tenant_id` to `gross_tax_amount_tc`). Tax base block: `tenant_id`, `company_id`, `document_id`, `tax_line_no`, `base_line_no`, `base_amount_tc`, `nondeductible_tax_tc`, `nondeductible_line_no`. Open item block: `tenant_id`, `company_id`, `document_id`, `line_no`, `partner_id`, `legacy_document_date`, `legacy_posting_date`, `legacy_document_ref`.

**Genesis.** `prev_hash` of `chain_seq` 1 is the SHA-256 of the UTF-8 text `genesis|{tenant_id}|{company_id}|{chain_type}|{scope_key}` (the `scope_key` of 9.2, empty for the five chain types without a scope).

### 9.4 Verification

`chains:verify` (API, read-only) per chain (chain type, scope) and sequence range: recomputes `row_hash` from stored L1 data, checks `prev_hash` linkage and gap-free `chain_seq`, checks balance per currency, and checks number continuity. Returns the first broken `chain_seq` or OK.

### 9.5 `chain_anchor`

Covers all chains of the tenant, the six chain types of `chain_head` (9.2): LEDGER, CLEARING (03), PERIOD_EVENT (04), VAT_RETURN (08), STOCK (10), FULFILMENT (11).
PK: (`tenant_id`, `anchor_id`)
Index: (`tenant_id`, `company_id`, `chain_type`, `scope_key`, `chain_seq`).

| Column | Type | Null | Notes |
|---|---|---|---|
| tenant_id, anchor_id | ID | N | |
| company_id | ID | N | |
| chain_type | CODE | N | LEDGER, CLEARING, PERIOD_EVENT, VAT_RETURN, STOCK, FULFILMENT |
| scope_key | TEXT(50) | N | As in `chain_head`: `ledger_id` for LEDGER, empty otherwise (VAT_RETURN: one chain per company, scope_key empty). Replaces the former `ledger_id` column |
| chain_seq | BIGINT | N | Sequence at anchoring time |
| row_hash | BINARY(32) | N | Chain head at that sequence |
| anchored_at | TS | N | UTC |
| anchor_digest | BINARY(32) | N | SHA-256 over (tenant, company, chain_type, scope_key, chain_seq, row_hash, anchored_at). This is what is sent to the timestamp authority |
| anchor_state | CODE | N | S. `PENDING` (anchor request written, token not yet fetched) or `STAMPED` (token stored). Set to PENDING on insert (A2, decided in 17, section 4) |
| timestamp_token | BLOB | Y | S. Trusted timestamp token over `anchor_digest` (RFC 3161 style). Null until STAMPED. Verifiable by third parties, no key in the core |
| tsa_id | TEXT(100) | Y | S. Identifies the timestamp authority that issued the token. Null until STAMPED |
| stamped_at | TS | Y | S. UTC time the token was stored. Null until STAMPED |
| anchor_ref | TEXT(200) | Y | S. Where the token and the export were stored outside the database. Set at stamping |

Created by an `anchors:create` command. A verification run compares stored anchors with the recomputed chain. Cadence (Q7 decided): daily per chain, plus one anchor at every `periods:hard-close` and `year-end:close`. The core holds no signing key: custody is the timestamp authority's.

**Anchoring after commit (A2).** No external call runs inside a transaction. The anchor request (the row with `anchor_state` = PENDING and its `anchor_digest`) is written in the transaction of the command that needs it, including the close commands, so **a close requires the anchor request, not the timestamp response**. After commit a job sends the digest to the timestamp authority and updates the row once, PENDING to STAMPED, with token, `tsa_id`, `stamped_at` and `anchor_ref`. A timestamp service outage therefore never blocks a close or year-end. `chains:verify` reports a PENDING anchor older than the deadline `anchor_pending_deadline` (07, tenant setting, default 24 hours) as a **warning** and does not fail the chain. A verification of a PENDING anchor checks the digest against the recomputed chain and states that no external timestamp exists yet.

**Locks of `anchors:create` (A1).** The anchor reads the heads of the chains it covers. Standalone, it takes share locks on the covered `chain_head` rows in the sorted order of 04, 6.3 (chain type, company, scope_key) and reads `last_seq` and `last_hash` of each under that lock, so the digest matches a committed head. Inside `periods:hard-close` and `year-end:close` the LEDGER, CLEARING and PERIOD_EVENT heads are already locked by the command, and the VAT_RETURN, STOCK and FULFILMENT heads of the company are read in share mode after them, which the sort order already gives. It writes no head and takes no number counter.

### 9.6 Outbox events (K3, ADR-159)

Every state-changing command of this domain (`journal-documents:post`, `:reverse`, `:change-open-fields`, the intercompany variants and the batch variant per item) writes its outbox events in the same transaction (15, 5.1): `ledger.document_posted`, `ledger.document_reversed`, `open_item.created` and `open_item.changed`, with `chain_ref` for the chained document. L2 changes are audit events whose detail row is the `change_log` row (15, 7). The `outbox_head` lock is the last lock of the transaction (04, 6.3). No hash definition changes.

## 10. DB-level protection (P5, ADR-001)

Platform mapping (20, section 3, ADR-209): the command roles named in this section are group roles without login. The processes connect with four login roles (`core_owner` for migrations, `core_app`, `core_job`, `core_retention`) that are members of the group roles of the commands they run. Row-level security is forced on every tenant table. Hashes are computed in the application library, never in the database (20, section 5, ADR-211).

| Table | App role grants |
|---|---|
| `journal_document` | INSERT. UPDATE only on L2 columns (change command role) and on the S columns `status`, `reversed_by_document_id` (the posting command role that posts the mirror document). No DELETE |
| `journal_line` | INSERT. UPDATE only on L2 columns. No DELETE |
| `journal_line_open_item` | INSERT. UPDATE only on L2 columns for the change command, and on S columns (`clearing_status`, `cleared_amount_tc/lc/gc/rc`, `last_clearing_id`) for the clearing command role. No DELETE |
| `journal_line_dimension` | INSERT, UPDATE of `dimension_value_id` (L2, logged by the command). No DELETE |
| `journal_line_tax`, `journal_line_tax_base` | INSERT only |
| `journal_line_tax_evidence` | INSERT and UPDATE of L2 columns by the change command role. No DELETE |
| `change_log` | INSERT only |
| `chain_anchor` | INSERT by the anchoring command roles. UPDATE only of the S columns `anchor_state`, `timestamp_token`, `tsa_id`, `stamped_at`, `anchor_ref` by the anchoring job role, once per row |
| `chain_head` | UPDATE of the S columns by the command role that owns the chain type only (posting, clearing, period, VAT return, stock, fulfilment) |
| `document_legal_snapshot` | INSERT. UPDATE only by the retention job role (L1* fields and `anonymised_at`) and by the `retention:extend` command (`retention_until`, S) |

Triggers as backstop, **per column class** (D2, ADR-146):

| Class | Rule |
|---|---|
| L1 | Reject any UPDATE that changes an L1 column (compare old and new values), in any period. **Exception L1\*:** the legal snapshot fields of section 4 may be overwritten by the retention job role only, in any period, together with a `change_log` row (entity type LEGAL_SNAPSHOT, `lock_class` L1\*, reason `SYSTEM`) in the same transaction. This is the one L1 exception |
| L2 | Reject any change when the period is hard-closed (L3). Reject any change not accompanied by a `change_log` row in the same transaction |
| S | **One rule for every S column of every table of the platform.** Allowed in any period status, including hard-closed. Allowed only for the owning command role and only together with the owning event of that column in the same transaction. No `change_log` row is required or written. The trigger checks that the event row exists in the same transaction (see the owner table below) |

Owning events of the S columns (the one table behind the S rule):

| S table and columns | Owning event in the same transaction |
|---|---|
| `journal_document.status`, `reversed_by_document_id` | The mirror document (its chain node), 3 |
| `journal_line_open_item` clearing columns | The `clearing` and `clearing_item` rows (03, 4.3) |
| `chain_head` (`last_seq`, `last_hash`, `hash_version`, `updated_at`) | The chain node written by the owning command: the document (LEDGER), the clearing (CLEARING), the `period_event` (PERIOD_EVENT), the VAT return event row (VAT_RETURN, 08), the stock document or movement record (STOCK, 10), the fulfilment record (FULFILMENT, 11). `last_seq` must increase by exactly 1 per node |
| `number_range_counter.last_number`, `updated_at` | The document or order record that consumes the number (04, section 8). Exactly +1 per record, or +n for n records of one series in one command |
| `fiscal_period.status`, `last_event_id`, `fiscal_year.status`, `year_end_document_id`, `last_event_id` | The `period_event` row with the matching id and `to_status` (04, section 8) |
| `revaluation_run.status`, `superseded_by_run_id` | The new run row and its REVALUATION and REVAL_REVERSAL documents of the same transaction (05, section 6) |
| `chain_anchor` state columns | The stored timestamp token itself, set once, PENDING to STAMPED only, `anchor_digest` unchanged (9.5) |
| `document_legal_snapshot.retention_until`, `anonymised_at` | The audit event of 15 (`retention.extended`) or the retention job's `change_log` row (L1\*) |
| Other S columns (extension and platform tables, 15) | The audit event named in the owning spec |

Column-level grants exist on both PostgreSQL and SQL Server. The trigger approach uses only common features (ADR-001).

## 11. Resolved by this spec and assumptions

| Ref | Item |
|---|---|
| A1 | Signed amounts, no D/C columns. SAF-T debit/credit derived in the read API |
| A2 | One transaction currency per document. Multi-currency documents (e.g. payment with a fee in another currency) would need a per-line currency. Not supported in R1 |
| A3 | Dimension storage: typed FK columns for the four standard types, child table for free types |
| A4 | Tax lines are rows of `journal_line`, so balance, hash and reversal logic needs no second path |
| A5 | Revaluation rerun and reopen interaction with the hash chain: append-only, resolved |

## 12. Open questions

No open question remains in this spec (propagation pass v0.6). All entries below are decided.

| # | Question |
|---|---|
| Q1 | **Decided: accept lock class S** for `status` and `reversed_by_document_id` on the header and the clearing columns on `journal_line_open_item`. ADR-031 becomes ACCEPTED. |
| Q2 | **Decided: yes.** Zero-amount TAX lines for EXEMPT and ZERO_RATED bases. |
| Q3 | **Decided: yes.** `posting_date`, `fiscal_year`, `fiscal_period` are copied onto `journal_line` as L1. Open-item aging still joins `journal_line` for account and amount. |
| Q4 | **Decided:** parallel ledgers later use one document per ledger, linked by a posting-group ID. No schema change to keys. |
| Q5 | **Decided:** the shared `ic_transaction_id` is the link. No counterpart pointer column. ADR-016 is amended accordingly (ADR log). |
| Q6 | **Decided:** tombstone. The dimension row stays, value set to null. No DELETE anywhere in the transaction layer. |
| Q7 | **Decided:** unkeyed SHA-256 chain plus signed anchors stored outside the database. Anchors every day and at every hard-close, timestamped by an external timestamp authority, no key held in the core. ADR-006 can move to ACCEPTED. |
| Q8 | **Decided:** reversal documents mirror the original lines 1:1 (same `line_no`, opposite sign, `reversed_line_no` set), including tax, rounding and FX lines. No recomputation. |
| Q9 | **Decided:** informational fields only. The core posts no discount. |

## 13. ADRs raised by this spec (now in 01-adr-log, which is authoritative; ADR-035 ACCEPTED 2026-10-13)

**ADR-030: Document-level hash chain** (resolves part of ADR-006). Chain node is the document header, with a digest over line and tax L1 data. Chain head row is the serialisation point. L2 and S fields are outside the hash.

**ADR-031: Lock class S, system-maintained fields** (ACCEPTED, Q1). Fields changed only by core commands, excluded from the hash. Amended by ADR-146 (D2), which is the rule now in force (section 8, 10): S changes are not written to `change_log`, each has its own chained event (the mirror document, the clearing records, the period event, the audit event), and S changes are allowed in hard-closed periods. The original wording (log in `change_log` with reason `SYSTEM`) is superseded.

**ADR-032: Tax detail as 1:1 extension of journal lines, with M:N base link.** Refines ADR-027.

**ADR-033: Open-item attributes in a 1:1 optional side table `journal_line_open_item`.** Refines ADR-005. Row existence marks an open item. Keeps `journal_line` free of S columns.

**ADR-034: Dimension storage: typed columns for standard types, child table for free types. Generic field-level change log.** Closes the storage-shape deferral in ADR-010.

**ADR-035: Signed amounts and single transaction currency per document** (A1, A2).

## 14. Re-baseline of 2026-10-13: changes in this spec and deltas toward other specs

Decisions from 14, sections 8 to 11, and the deltas of 15 (section 10) and 16 (section 9) that concern this spec.

| Ref | Change in 02 |
|---|---|
| D1, ADR-145 | Reversal rules (3, constraints): `reversal_of_document_id` and `reversal_reason_code` on every mirror document, derived types reversible only through the owning command, `journal-documents:reverse` rejects them. YEAR_END only through `year-end:reopen` |
| D2, ADR-146 | Trigger and grant rules per column class (10). `retention_until` on `document_legal_snapshot` is S. `change_log` is written for L2 changes only (8), S changes have their own chained event. Amends ADR-031 |
| D3, ADR-147 | Lock order is authoritative in 04, 6.3 (9.2). No per-posting update of `business_partner.retention_until` (batch job, 12) |
| K1, ADR-158 | `ledger_chain_head` replaced by the shared `chain_head` (9.2). `chain_anchor.ledger_id` becomes `scope_key` (9.5). Anchors cover the six chain types |
| K3, ADR-159 | Outbox events in the command transaction, `outbox_head` lock last (9.6) |
| 15, section 10 | Every state-changing command writes outbox events. No change to the hash definitions |
| 16, section 9 | No change in this spec. Extendable entity types of 15 do not include journal documents (15, 4.1) |
| 14, section 3 | Added document types STOCK_STATUS_CHANGE and STOCK_OPENING (3). `is_system_generated` list completed (3). `journal_line_tax_evidence` in the overview, grants and `change_log.entity_type` TAX_EVIDENCE (2, 7.3, 8, 10). `legacy_*` open-item columns in the line hash (9.3). REVAL_ADJUSTMENT exemption from the partner and open-item rule (5A) |
| 14, section 4 | Chain types listed once, all six (9.2, 9.5). Command names normalised to the 06 style (`journal-documents:*`, `chains:verify`, `anchors:create`, `periods:hard-close`) |
| Third pass (14, section 11) | Mirror types stated once (3, constraints): REVERSAL, REVAL_REVERSAL, CLEARING_RESET and STOCK_REVERSAL stay, the keep-original-type rule applies only to YEAR_END, VAT_SETTLE, DROPSHIP_COST and order-built invoices and credit notes. Reason code list closed, `journal-documents:reverse` accepts only CORRECTION and ERROR from external callers (3). Q5 and ADR-031 wording in sections 12 and 13 aligned with ADR-016 and ADR-146 |
| Second pass (14, section 10) | Reason code list AUTO_REVERSAL, RERUN, REOPEN, CORRECTION, ERROR, RETURN and a rule that mirrors keep the original `document_type` (3, restricted to types without own mirror type in the third pass). `IC` removed as document type, IC documents are AR/AP_INVOICE with `ic_transaction_id` (3). VAT_RETURN chain one per company, `scope_key` empty (9.2, 9.5). Retention job overwrite of snapshot fields stated as a legal L1 exception recorded with reason `SYSTEM` (4). D3: number counters lock before chain heads, authoritative in 04, 6.3 |
| Fix pass 4 (17, section 4, v0.5) | **X1:** document type CLOSING (3), in `is_system_generated`. **X5:** column `origin` (3). **X4:** lock tuple (chain type, company, scope_key), counters before heads (9.2). **A1:** posting re-checks the period in share mode after the head locks, hard-close locks LEDGER and CLEARING heads first (3 constraints, 9.2). **A2:** `chain_anchor` state PENDING and STAMPED, token nullable, `anchor_pending_deadline` warning, no external call in a transaction (9.5). **B1:** `reversed_by_document_id` is the first mirror (3). **B3:** automatic clearing of mirror open items (3 constraints, 03, 5.7). **B4:** rollback is a mirror-producing command, reason CORRECTION, migration types keep their type (3). **D9:** rate rule table per system line type and "non-zero in any currency" (5.2, 3). **Reason vocabularies:** payload reason of the reopen commands goes to `period_event`, REOPEN to the mirror (3). **Trigger table:** one S rule for every S table with its owning event, L1* exception (10). **`change_log`:** `clearing_id`, entity type LINE_OPEN_ITEM, `lock_class` L1\*, `period_status` NONE, governing period of a clearing note (8). **Stale text:** 02:13 "still logged" corrected (1) |
| Propagation pass (17, section 5, v0.6) | Document type GR_IR_WRITE_OFF: system generated, `origin` ORDER, owning command `gr-ir:reverse-write-off`, mirror keeps the type (3: type list, `is_system_generated`, origin, mirror and derived-document rules). D9 rate rule at LC = 0 final: ratio to the first non-zero amount, amounts authoritative (5.2). CLOSING and `origin` confirmed present (3). Open question list closed (12) |
| ADR-200, ADR-203 to ADR-206 (2026-10-14) | `journal_line_tax`: `origin_eu`, `destination_eu` (7.1, ADR-200) and the deduction columns (7.1, ADR-203). `journal_line_tax_base`: `nondeductible_tax_tc`, `nondeductible_line_no` (7.2). New line type `TAX_NONDEDUCTIBLE` (5.1) with its rate rule (5.2). `hash_version` 2 (9.3) |
| ADR-226 (2026-10-19, spec 23) | 5.4: the lines that carry `ic_counterpart_company_id` (IC account lines, control lines and BASE lines of an IC document pair, never tax or rounding lines). No hash change |
| Skipped | exemptions of CLOSING, HISTORY_PL and OPENING lines from the partner and open-item rule (owned by 05 and 13). Number series fiscal year resolution is in 04 |
