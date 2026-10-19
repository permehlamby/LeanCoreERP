# Migration: Taking a Company Live from a Legacy System

Status: DRAFT v0.6 for review, re-baselined with the decisions of 14 (sections 8 to 11), specs 15 and 16 and the rulings of 17, sections 4 and 5 (fix pass 4, deltas at the end of section 10). v0.6 (17, section 5, propagation pass): `history_until_period` is a parameter of `migration-batches:open` (4.2, 7), rollback handling of `CLOSING` documents (7), `MIGRATION.ABORT_NOT_ALLOWED` used by `abort` (7, 7.1), one event for the SEALED transition (`migration.sealed`, 2.1, 3.1, 9), synthetic FIFO layer of a migrated receipt (5, 6.4, 6.5). v0.5: history-year closing entries are the new document type `CLOSING` (X1, 6.1), `journal_document.origin` = MIGRATION (X5), DRAFT company exception (D6, 6), abort in COMMITTING, ROLLED_BACK terminal, rollback as a mirror-producing command with reason CORRECTION and removal of `legacy_id_map` and tracking rows (D6, B4, 7), OPENING and OPENING_ITEMS split per transaction currency and one currency per stock document (D7, 6.2 to 6.4), `fiscal_year.history_until_period` (D8, 4.2), migration events per status change plus `migration.sealed` (E6, 2.1, 9), `OPEN_ITEMS_VS_TB` restricted to CONTROL and GL_OPEN_ITEM accounts (8), `RETAINED_EARNINGS` role required for history (4.1), history years opened before the cutover year (4.2). v0.4 (14, section 11): the seal variant of year close skips the D4 stale check, sealed history years cannot be reopened and are corrected by adjustment postings, the `MIGRATION` error codes of 7.1 are accepted, `year-end:close` on a history year is rejected with `PERIOD.HISTORY_YEAR`, Q19 and Q20 closed. v0.3 defined the migration variant of year close run by the seal (Q16 decided) and recorded the correction path between commit and seal as rollback and recommit (Q17 decided). Builds on 00-design-principles, ADR-001 to ADR-174 and specs 02 to 16. Main references: P3 and P5 (immutable, chained ledger), P9 (open items on control accounts), ADR-040 (GL open items), ADR-054 and ADR-055 (opening documents and closing entries, amended by ADR-152), ADR-052 (`detail_digest`), ADR-084 (VAT accounts), ADR-096 and 10 (computed costs, with the closed exception list of ADR-150), 11 (orders), 04 (short first year, series), 12 (tenant deletion, retention). Amended by: ADR-145 (derived documents reversible only through the owning command), ADR-150 (`cost_source` MIGRATION), ADR-152 (chunk order, one GR/IR check, exception scope, atomic per chunk), ADR-143 (in-transit stock), ADR-141 (order line dates), ADR-159 (outbox events: one per batch status change and `migration.sealed`, none per record, E6).
Scope: the architecture and controlled exceptions for migrating master data, ledger history for comparatives, opening balances and open items, stock on hand (including in-transit goods), open orders; migration batches, staged records, control totals and reconciliation; commit and seal; rehearsals in throwaway tenants; legacy id mapping.
Out of scope: extraction and transformation (the external ETL), the mapping rules of any specific legacy product, fixed assets and other modules the core does not have, historic documents other than P&L period summaries.
Conventions as in 02, section 1, pattern letters as in 07, section 2, envelope and errors as in 06, reads as in 09.

## 1. Decisions taken

| Topic | Decision |
|---|---|
| Objects | Master data, GL balances and open items, stock on hand, open orders and in-transit goods |
| History | **Period summaries for comparatives, P&L only.** The balance sheet exists from the cutover. No original historic documents |
| Cutover | Any period start. With history summaries, the cutover fiscal year is a **full** fiscal year whose months before the cutover are hard-closed history periods. Without history, the company may start with a short first year (04 Q6) |
| Engine | A hybrid: **ETL is external**. The core offers a thin batch layer that stages already-transformed canonical records, validates them on receipt, reconciles them against totals the ETL declares, and commits |
| Commit | **Resumable chunks, atomic per chunk** (Q9, ADR-152). Each chunk is one atomic transaction of bounded size. The batch as a whole is not atomic unless it fits one chunk. Chunks run in the fixed order of section 7: HISTORY (including the closing entries of the history years), OPENING_GL, OPEN_ITEMS, STOCK, ORDERS, FULFILMENT, RESERVATIONS, LEGACY_TAX. The company stays in DRAFT during commit, so a partly committed state is never live. A failed commit resumes after the fix, or is rolled back by `migration-batches:rollback` |
| Exceptions | Apply to the documents of a batch that is OPEN, COMMITTING or COMMITTED, until the seal (ADR-152, 6). The intermediate states VALIDATED and RECONCILED belong to the same unsealed batch and keep the exceptions for VALIDATE runs. **A DRAFT company is a controlled exception** (D6, 6): migration documents post into a company that is still DRAFT |
| Reversal | Migration documents are derived documents: reversible only through the owning command, `migration-batches:rollback` (D1, ADR-145), which is a **mirror-producing command with reason code CORRECTION** (B4, 7). `journal-documents:reverse` and `stock-documents:reverse` reject them |
| Origin | Every migration journal document carries `journal_document.origin` = MIGRATION, set by the core and never by a caller (X5, 02). Migration documents are recognised by `origin` and `migration_batch_id` |
| VAT | Cutover may fall inside a VAT period: legacy tax of the partial period is declared as summary lines that the first return includes (Q1) |
| Rehearsal | Rehearsals run in throwaway tenants that are discarded by tenant deletion. The production load repeats the rehearsed procedure |
| Legacy ids | A `legacy_id_map` table in the core |
| Year close of history years | The closing entries are posted by the HISTORY chunk as documents of the new type **`CLOSING`** (X1, 6.1), not `YEAR_END`. The seal runs a **migration variant of year close** that writes `PERIOD_HARD_CLOSE` and `YEAR_END_CLOSE` for the already posted closing entries, sets `fiscal_year.year_end_document_id` to the `CLOSING` document and creates no document. `year-end:close` cannot be used on history years (Q16, 9.1) |
| Corrections before the seal | A correction of a migration document between commit and seal is a rollback and a recommit (Q17) |
| After seal | The seal is final. Sealed history years and their periods cannot be reopened (`periods:reopen` and `year-end:reopen` reject history periods with `PERIOD.HISTORY_YEAR_SEALED`). Mistakes are corrected by adjustment postings through the normal controls (9.1, Q20) |

## 2. Architecture

### 2.1 Phases

| Phase | What happens | Where |
|---|---|---|
| 1. Prepare | Tenant and company exist. Configuration, master data and roles are loaded through the normal APIs (07, 11.4) with legacy ids recorded. Company readiness is checked | ETL calls core APIs |
| 2. Open batch | `migration-batches:open` for the company with the cutover date, the history scope and the declared control totals | Core |
| 3. Stage | The ETL sends canonical records (section 5). Each is validated on receipt with the normal pipelines in VALIDATE mode and stored with its result. Records can be replaced until commit | Core |
| 4. Reconcile | `migration-batches:reconcile` computes in-core totals from the staged records and compares them with the declared control totals and with each other. Repeatable | Core |
| 5. Commit | `migration-batches:commit` posts the batch for the company in **resumable chunks, each atomic**, in the fixed order of section 7, under the controlled exceptions of section 6. Events are not emitted per record. The batch emits one `migration.batch_status_changed` event per status change (E6, 15 delta) | Core |
| 6. Verify and seal | Post-commit checks run against the ledger (section 9). If all pass, `migration-batches:seal` hard-closes the history periods and closes the history years by the **migration variant of year close** (9.1, no new documents), closes the batch and removes the exceptions. The transition to SEALED writes one `migration.sealed` event with the counts and the chain ranges of the batch as its status-change event (E6, 9, 15, 6) | Core |

### 2.2 Rehearsals

A rehearsal is the same procedure in a **throwaway tenant** (`tenant.purpose` = REHEARSAL, 7.1). It proves volumes, the mapping, the reconciliation and the timing. It is discarded by tenant deletion (12, 6), with relaxed rules for rehearsal tenants (Q10). There is no export or import of an entire tenant for cloning in release 1: the ETL loads every rehearsal and the production run from the legacy extract. The rehearsal result (reconciliation report) is the go-live gate, but the core does not enforce a link between a rehearsal tenant and the production tenant.

### 2.3 What the core never does

The core does not read legacy systems, transform legacy data or decide mappings. It accepts canonical records, validates them against its own rules, reconciles totals and posts. Everything legacy-specific stays in the ETL.

## 3. Tables

| # | Table | Pattern | Role |
|---|---|---|---|
| 1 | `migration_batch` | M + S | One batch per company and cutover |
| 2 | `migration_control_total` | E | Totals declared by the ETL |
| 3 | `migration_record` | E | Staged canonical records with validation results |
| 4 | `migration_reconciliation` | E | Results of reconciliation runs |
| 5 | `legacy_id_map` | M | Mapping of legacy ids to core ids |
| 6 | `migration_commit_chunk` | E + S | Progress of a chunked commit |
| 7 | `legacy_tax_summary` | E + S | Legacy VAT of the partial VAT period at cutover, included in the first return (3.6) |

### 3.1 `migration_batch`

PK: (`tenant_id`, `batch_id`). Unique: one batch per (`tenant_id`, `company_id`) whose status is **not ABORTED and not ROLLED_BACK** (D6: both are terminal and do not count as non-aborted, so a new batch can be opened after either).

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| tenant_id, batch_id, company_id | ID | N | L1 | |
| cutover_date | DATE | N | L1 | The first day of the first live period. Must be the start date of a regular fiscal period |
| source_system | CODE | N | L1 | Code of the legacy system, as in `legacy_id_map` |
| history_start_period | DATE | Y | L1 | First day of the first history period. Null if no history is migrated |
| status | CODE | N | S | OPEN, VALIDATED, RECONCILED, COMMITTING, COMMITTED, SEALED, ABORTED, ROLLED_BACK. SEALED, ABORTED and ROLLED_BACK are **terminal** (D6). Every status change writes one outbox event (E6, 15, 6): `migration.batch_status_changed` (`batch_id`, `status`), except the transition to SEALED, whose status-change event is `migration.sealed` |
| opened_by, opened_at | | N | L1 | |
| committed_at, committed_by, sealed_at, sealed_by | | Y | S | |
| commit_requested_by, commit_requested_at, commit_approved_by, commit_approved_at | | Y | S | Four-eyes in PRODUCTION tenants (7, Q13 decided). Requester and approver must be distinct |
| seal_requested_by, seal_requested_at, seal_approved_by, seal_approved_at | | Y | S | Same for the seal |
| accepted_reconciliation_id | ID | Y | S | The reconciliation run the commit relied on |
| correlation_id, idempotency_key | | N | L1 | |
| row_version | INT | N | | |

### 3.2 `migration_control_total` (E)

PK: (`tenant_id`, `batch_id`, `control_no`). Declared by `migration-batches:declare-totals` (replaceable until commit by new rows with a later `declared_at`, the latest per key counts).

| Column | Type | Null | Notes |
|---|---|---|---|
| control_kind | CODE | N | `TB_BY_ACCOUNT` (cutover trial balance per account), `PL_YTD_BY_ACCOUNT` (year-to-date P&L at cutover), `HISTORY_PL_BY_PERIOD_ACCOUNT`, `OPEN_ITEMS_BY_ACCOUNT`, `STOCK_VALUE_BY_ACCOUNT`, `STOCK_QTY_BY_ITEM`, `OPEN_ORDER_LINES`, `DELIVERED_NOT_INVOICED`, `RECEIVED_NOT_INVOICED` |
| key_1, key_2 | TEXT(100) | Y | Account number, period, item, depending on the kind |
| amount_lc, amount_gc, amount_rc | AMT | Y | Signed |
| quantity | DECIMAL | Y | |
| count | INT | Y | |
| source_report_ref | TEXT(200) | Y | Which legacy report the figure comes from |
| declared_at, declared_by | | N | |

### 3.3 `migration_record` (E)

PK: (`tenant_id`, `batch_id`, `record_no`). Index (`tenant_id`, `batch_id`, `record_type`, `legacy_ref`, `record_no`).

| Column | Type | Null | Notes |
|---|---|---|---|
| record_type | CODE | N | See section 5 |
| legacy_ref | TEXT(100) | N | The legacy key of the record. A record with the same type and `legacy_ref` and a higher `record_no` supersedes the earlier one until commit |
| payload | | N | Canonical record content as a canonical document |
| payload_digest | BINARY(32) | N | SHA-256 |
| status | CODE | N | RECEIVED, VALID, INVALID |
| errors | | Y | Structured errors of the validation (06, 2.3), including paths into the payload |
| received_at, received_by | | N | |
| superseded | BOOL | N | S. True once a later record replaces it |

### 3.4 `migration_reconciliation` (E)

PK: (`tenant_id`, `batch_id`, `reconciliation_id`, `check_no`). One run produces many rows.

| Column | Type | Null | Notes |
|---|---|---|---|
| check_code | CODE | N | Section 8 |
| key | TEXT(100) | Y | Account, period, item |
| declared_lc, declared_gc, declared_rc | AMT | Y | |
| computed_lc, computed_gc, computed_rc | AMT | Y | |
| difference_lc, difference_gc, difference_rc | AMT | Y | Computed minus declared |
| result | CODE | N | PASS, FAIL, WARNING |
| detail | TEXT(500) | Y | |
| run_at, run_by | | N | |

### 3.5 `legacy_id_map` (M)

PK: (`tenant_id`, `source_system`, `entity_type`, `legacy_id`). Unique (`tenant_id`, `entity_type`, `core_id`, `source_system`).

| Column | Type | Null | Notes |
|---|---|---|---|
| entity_type | CODE | N | ACCOUNT, PARTNER, ITEM, WAREHOUSE, DIMENSION_VALUE, TAX_CODE, PAYMENT_TERMS, SALES_ORDER, PURCHASE_ORDER, DOCUMENT and others |
| legacy_id | TEXT(100) | N | |
| core_id | TEXT(100) | N | Canonical text of the core key |
| batch_id | ID | Y | Set for entries created by a migration batch. `migration-batches:rollback` **removes the rows of its batch** (D6, 7), so a new batch can map the same legacy ids again. Rows written by master data loads (batch null) stay |
| mapped_at, mapped_by | | N | |
| row_version | INT | N | |

Written by the configuration and master data commands when a request carries `legacy_ref` (07, section 11 delta), and by migration commit. Master data loads become idempotent: a request with an existing legacy id updates (with `expected_version` rules). With the explicit `upsert` mode (07, 11.1, Q14): `upsert` on an existing row (found by `legacy_ref` or key) requires `expected_version` and rejects with `CONFIG.ROW_VERSION_MISMATCH` on a mismatch. Without a legacy match it inserts (`expected_version` not allowed). `upsert` without `legacy_ref` is rejected. Only the batch variant (`{resource}:upsert-batch`, 07, 11.5) is exempt from `expected_version`, because the batch is the authority. The map is queried in both directions (`legacy-ids:resolve`, `:list`). Changes are audited in `master_change_log`. Legacy ids contain no personal data by rule (12, section 3).

### 3.6 `migration_commit_chunk` and `legacy_tax_summary`

`migration_commit_chunk`: PK (`tenant_id`, `batch_id`, `chunk_no`). Columns `area` (HISTORY, OPENING_GL, OPEN_ITEMS, STOCK, ORDERS, FULFILMENT, RESERVATIONS, LEGACY_TAX, listed in the fixed commit order), `from_record_no`, `to_record_no`, `document_count`, `status` (S: PENDING, DONE, FAILED), `failure_codes`, `started_at`, `completed_at`. The chunk plan is computed by `commit`. Chunks run in the fixed order of section 7, each in **one atomic transaction** of at most `migration_commit_chunk_documents` documents and records (default 20,000). The lines of one document are bounded separately by `migration_document_max_lines` (default 5,000): the **maximum number of lines of one migration document of any type** (`HISTORY_SUMMARY`, `CLOSING`, `OPENING`, `OPENING_ITEMS`, `STOCK_OPENING`). A larger source is split into several documents, and OPENING and OPENING_ITEMS documents are also split per transaction currency (6.2, 6.3). The batch is atomic only if it fits one chunk. Both settings are in `tenant_setting` (07 delta).

`legacy_tax_summary` (E + S): PK (`tenant_id`, `company_id`, `summary_id`). Created by commit from `LEGACY_TAX_SUMMARY` records (5).

| Column | Type | Null | Notes |
|---|---|---|---|
| batch_id | ID | N | |
| registration_id | ID | N | VAT registration (08, 4.1) |
| fiscal_year, vat_period_no | INT, SMALLINT | N | The VAT period that contains the cutover date |
| return_type | CODE | N | STANDARD, EC_LIST, OSS, IOSS |
| row_type, box_code, partner_vat_id, country_code, category, rate, supply_kind | | | Row keys as `vat_return_line` (08, 6.4) |
| currency_code, base_amount, tax_amount | CODE, AMT, AMT | N | Legacy tax of the partial period in the return currency |
| tax_code_id | ID | Y | Core tax code, informational |
| vat_account_id | ID | Y | The VAT account that carried the legacy tax, for settlement |
| ledger_lc, ledger_gc, ledger_rc | AMT | Y | The legacy ledger amounts on `vat_account_id` (signed), settled with the first return |
| reported_in_return_id | ID | Y | S. Set when `vat-returns:prepare` includes the row (once per return type) |

## 4. Roles and calendar

### 4.1 Account roles (07, 6.3, new)

| Role | Used for | Account rule |
|---|---|---|
| `MIGRATION_CLEARING` | Balances every migration area document against the others. Must be zero when the batch is sealed | Balance sheet, open-item mode NONE, no mandatory dimensions |
| `HISTORY_RESULT` | Suspense for the P&L history summaries (the counter line). Zero at the end of the opening document | Equity or balance sheet, NONE, no mandatory dimensions |

Both are required before a batch can be opened (`CONFIG.ROLE_NOT_ASSIGNED`). **`RETAINED_EARNINGS` (07, 6.3, 05) is required as well** when the batch has history (`history_start_period` not null): the HISTORY chunk posts the closing entries of the history years to it (6.1), so it is a batch open requirement and not first discovered by a failing chunk. It follows the rules of 05 (equity, no mandatory dimension rules, no `revalue_flag`).

### 4.2 Fiscal years and periods

- **History years.** `fiscal-years:open-history` (permission `history.open`, D10, only for an OPEN batch) opens fiscal years before the cutover fiscal year, contiguous, up to `tenant_setting.migration_max_history_years` (default 3). Their periods are created OPEN (04). A history year has `is_history` = true (L1 on `fiscal_year`). The closing entries of the history years are posted in the HISTORY chunk (6.1). The history years and periods are hard-closed at the seal (section 9). **Contiguity (L13):** the cutover fiscal year exists before the batch is opened (7), so a history year is opened **before an existing year**. The rule of 04, 3.1 ("must follow directly after the previous year, no gap and no overlap") is therefore evaluated for history years against the **next** year: the first history year must end the day before the cutover year starts, and each further history year must end the day before the earliest existing history year starts. Years are opened from the cutover year backwards. 04 states this variant for `fiscal-years:open-history` (04 delta).
- **Cutover fiscal year.** With history: a full fiscal year whose periods before the cutover period are **history periods** (OPEN until the seal, then HARD_CLOSED) and whose periods from the cutover period are live. **Marker (D8):** the cutover `fiscal_year` carries `history_until_period` (L1, stored by 04, 3.2). It is set by **`migration-batches:open` through its parameter `history_until_period`** (7), not by `fiscal-years:open-history`, which opens only the years before the cutover year. It is the number of the last history period of the year, that is the period before the cutover period (null if the cutover date is the first period of the year, and null without history). A period is a history period if its year has `is_history` = true, or if its year has `history_until_period` set and its `period_no` is at most that value. The history guards (`PERIOD.HISTORY_YEAR`, `PERIOD.HISTORY_YEAR_SEALED`, 7.1, 9.1) key on this definition. Without history: the company may use a short first year starting at the cutover period (04 Q6).
- **Cutover date** is the start date of a regular period. Mid-VAT-period cutovers are discussed in Q1.
- The posting window is not required to cover history years (the migration commands bypass the window, 6). It must cover the live periods before go-live.

## 5. Canonical records

Records are sent with `migration-records:stage` (idempotent per `legacy_ref`, batched in sizes up to a tenant setting). Each type has a canonical payload. Amount fields in all four currencies are the **legacy booked values**, never converted by the core (exception to ADR-057, section 6).

| record_type | Content | Becomes |
|---|---|---|
| `HISTORY_PL_LINE` | `fiscal_year`, `period_no`, `account_no`, `amount_lc/gc/rc` (signed net movement), optional dimension values | Lines of the `HISTORY_SUMMARY` document of that period (6.1) |
| `OPENING_BALANCE` | `account_no`, `balance_lc/gc/rc` (signed, as of the cutover date), optional `currency_balances` per transaction currency for foreign-currency balance sheet accounts that are revalued (`revalue_flag`) | Lines of the opening GL document (6.2) |
| `OPEN_ITEM` | Account, partner, `document_date`, `posting_date` (original), `due_date`, `baseline_date`, `payment_terms`, `currency`, `amount_tc/lc/gc/rc` (the **remaining open amount at the cutover date**, signed, in the item's transaction currency and in LC, GC and RC as legacy booked. A partly paid legacy item is staged with what is still open. The original amount is not staged), `legacy_document_ref`, `legacy_line_ref`, optional dimensions, `payment_block` | A line with an open-item row in an `OPENING_ITEMS` document (6.3) |
| `GLOPEN_ITEM` | As `OPEN_ITEM` without partner, for GL open-item accounts (bank clearing, GR/IR not tied to a receipt) | Same |
| `STOCK_OPENING_LINE` | `item`, `warehouse`, `location`, `tracking` (lot or serial, expiry), `stock_status`, `base_quantity`, `value_lc`, for FIFO items `layer_date` (stored as `stock_cost_layer.layer_date`, 10, 4.3) and `unit_cost` (one record per layer), and optionally `receipt_legacy_ref` (the `legacy_ref` of the `RECEIVED_NOT_INVOICED` record whose goods this layer holds: the line is then the **synthetic layer** of that migrated receipt, 6.4). **In-transit goods** are lines whose `location` is a location of type TRANSIT (10, 3.6, 16, 9) | A movement of the `STOCK_OPENING` document (6.4) |
| `OPEN_SALES_ORDER_LINE` | Header data (customer, order currency, dates, ship-to, payment terms, references), line data (item, `open_quantity`, unit, price, discount, warehouse, invoice policy, tax class, origin and destination country, fulfilment type, dropship link), **`requested_date` and `confirmed_date` when known** (16, 5, ADR-141), `legacy_order_ref` (mandatory), `legacy_line_ref` (optional, Q3 decided) | A sales order and line with the open quantity as ordered quantity (6.5). The legacy references are stored in the order columns `legacy_order_ref` and `legacy_line_ref` (11, 4.1, 4.2) |
| `OPEN_PURCHASE_ORDER_LINE` | The mirror, with `requested_date` and `confirmed_date` when known and no reservation data | A purchase order and line (6.5) |
| `DELIVERED_NOT_INVOICED` | Customer, order line reference, `base_quantity`, delivery date, `net_amount`, **`value_lc`** (D5: the legacy cost value of the delivered quantity, the valuation basis for returns of the migrated delivery, 11, 5.1, 5.4) | A migrated sales delivery for invoicing (6.5) |
| `RECEIVED_NOT_INVOICED` | Supplier, order line reference, `base_quantity`, receipt date, `value_tc/lc` (the legacy GR/IR value, which is also the valuation basis for returns of the migrated receipt, 11, 5.2, D5), currency | A migrated purchase receipt with a GR/IR open item (6.5) |
| `DROPSHIP_PENDING` | Sales and purchase line references, quantity, confirmation date, value | A migrated dropship confirmation (6.5) |
| `LEGACY_TAX_SUMMARY` | VAT registration, VAT period (year, number), return type, row keys (box, or EC list partner row, or OSS row), base and tax amounts, VAT account and its legacy ledger amounts | A `legacy_tax_summary` row (3.6). Needed when the cutover date is not the start of a VAT period of a registration (Q1 decided) |

Master data is **not** staged. It goes through the normal APIs before the batch (phase 1), with `legacy_ref` on each request. This includes `item_partner` terms, business calendars, warehouse locations with their `location_type` (TRANSIT locations must exist before the batch). Extension attribute values can be loaded by the normal command after the seal (15 delta).

### 5.1 Validation on receipt

Each staged record is validated in VALIDATE mode against its target pipeline (06) with the migration exceptions applied: references resolve (accounts, partners, items, warehouses, tax codes), currencies are enabled, accounts of the right open-item mode, amounts are well formed, quantities fit the item scale and units convert. Cross-record checks (sums, completeness) happen in reconcile. A record that fails is stored `INVALID` with its errors. Fixes are new records with the same `legacy_ref`.

## 6. Controlled exceptions

Permission `migration.commit` (held by identities that may commit and seal, four-eyes recommended, Q13) is required, and the exceptions apply **only** to documents created by `migration-batches:commit` (and the reversals of `migration-batches:rollback`) of a batch that is **OPEN, COMMITTING or COMMITTED, until the seal** (D8, ADR-152), which sets `migration_batch_id` (L1, new) on the documents. The exceptions are not limited to the batch status OPEN: a commit runs in status COMMITTING, and corrections between commit and seal happen in COMMITTED. No external command can use them.

| Rule | Exception during commit | Why |
|---|---|---|
| Posting window (ADR-025) | Not applied to history periods | History years are before the window |
| Period status (ADR-022) | History periods are OPEN until the seal, then HARD_CLOSED | History documents must post before closing |
| Amounts (ADR-057) | LC, GC, RC and the transaction amount are taken as supplied, with the implied rates stored (`rate_type` DERIVED, 02, 5.2). Exchange rate lookup is skipped | Legacy booked values must be reproduced exactly |
| Control accounts need a partner (P9) | History and opening GL documents may post aggregate lines without partner on subledger accounts only where stated (6.2) | Aggregates versus open items |
| `tax_account` rule (ADR-084) | Opening balances on VAT accounts allowed in the opening GL document. They equal the legacy ledger amounts of the legacy tax summary (reconciled, check `VAT_VS_TB`) | Pending VAT of the partial VAT period. The first return settles them (3.6, 8) |
| Mandatory dimensions (P11) | Not enforced on history and opening lines (reported as warnings in reconcile) | Legacy dimension completeness cannot be assumed |
| Computed costs (ADR-096, 10 Q5, 10 5.6) | Stock opening values and FIFO layers are supplied. The movements carry `cost_source` MIGRATION (ADR-150) | Legacy valuation |
| Document types and reversal | `HISTORY_SUMMARY`, `OPENING`, `OPENING_ITEMS`, `STOCK_OPENING` and `CLOSING` (the closing entries of history years, X1) are system types, never accepted by `journal-documents:post`, and **not reversible** through `journal-documents:reverse` or `stock-documents:reverse`. The owning command is `migration-batches:rollback` (D1, ADR-145), a **mirror-producing command with reason code CORRECTION** (B4): its mirror documents keep the original type (`HISTORY_SUMMARY`, `CLOSING`, `OPENING`, `OPENING_ITEMS`, `STOCK_OPENING`) and carry `reversal_of_document_id` (02, 3). All of them have `journal_document.origin` = MIGRATION | Derived documents are reversed only through the command that owns them |
| Company status | The normal rule that a company must be ACTIVE to post does not apply to documents of an **unsealed batch** (D6): the batch commits into a company that is still **DRAFT**. This is the only posting into a DRAFT company, and no other command can use it. The company is activated after the seal (7, `migration-batches:open`) | The company stays DRAFT during commit so a partly committed state is never live |
| Numbering | Migration document types use non-legal number series. Legal series are untouched (Q11) | Legacy history is not part of the legal sequences |
| Order lifecycle | Migrated orders, deliveries, receipts and dropship confirmations have `origin` = MIGRATED and `migration_batch_id` (11, 4.1, 5.1 to 5.3) | Their history is not in the core |

All exceptions end at the seal. Every migration document carries `migration_batch_id` so reports and the audit package can identify and, if wished, exclude them.

### 6.1 `HISTORY_SUMMARY` documents

One document per history period, per ledger (R1: one). Lines: one `HISTORY_PL_LINE` record per account and optional dimension combination (type `HISTORY_SUMMARY`, line type `HISTORY_PL`), `transaction_currency` = company currency, `amount_tc` = `amount_lc`, and **one counter line** on the `HISTORY_RESULT` account with the opposite of the sum, in LC, GC and RC. Posting date: the last day of the period. The document balances by construction.

**Closing entries of the history years (D8, ADR-152, X1).** The HISTORY chunk also posts, for each history year before the cutover year and after the summaries of that year, the closing entries as one document of type **`CLOSING`** per year (a document type of 02, system generated, set by the HISTORY chunk, not `YEAR_END`, which is posted only by `year-end:close`): they zero the year's P&L accounts into the retained earnings account (role `RETAINED_EARNINGS`, 4.1), as the year-end logic of 05 would (ADR-054, ADR-055). **Closing period and date:** the document is posted in the **closing period** of the history year (the highest special period of the year, 05, 5.1, which therefore must exist, Q7) with the posting date **the last day of the fiscal year**. Its lines are of line type `CLOSING`, with `rate_type` DERIVED (the amounts are sums of posted amounts, 02, 5.2). They are posted in the first chunk, not at the seal, so that the retained earnings account carries the history result when OPENING_GL runs and the targeting of 6.2 sees it. The status closing of the history periods and years (hard-close, PERIOD_HARD_CLOSE and YEAR_END_CLOSE events, 04, 05) still happens only at the seal, by the migration variant of year close (9.1), because corrections and rollback need the history periods to stay OPEN until then. The closing entries are migration documents with `migration_batch_id` and `origin` = MIGRATION. They are reversed only by `migration-batches:rollback`, not by `year-end:reopen`. **Report treatment (X1):** `CLOSING` is **excluded from P&L reads like `YEAR_END`** (09, 4.1, `exclude_document_types` default for P&L and movement views, 05), so the history comparatives of the P&L are preserved and are not netted to zero by the closing entries, and it is **included in balance reads** (trial balance, balance sheet, retained earnings), where the closing entries move the result of the history year into retained earnings. At the seal the migration variant of year close sets `fiscal_year.year_end_document_id` of each history year to its `CLOSING` document (9.1).

### 6.2 Opening GL document (`OPENING`)

One or more documents dated the cutover date in the first live period, **split per transaction currency (D7, ADR-035: one transaction currency per document)**: one set of `OPENING` documents in the company currency for the balances without a foreign-currency component, and one set per foreign transaction currency for the `currency_balances` of revalued foreign-currency accounts. Each document is bounded by `migration_document_max_lines` and balances in its own transaction currency and in LC, GC and RC. Lines come from `OPENING_BALANCE` records for **non-subledger-managed balance sheet accounts**. Subledger-managed accounts are those with open-item mode CONTROL or GL_OPEN_ITEM, the accounts determined as `INVENTORY` by account determination (10, 3.9) and the `GR_IR` accounts. Their balances come only from 6.3 to 6.5.

OPENING_GL runs **after** the HISTORY chunk (section 7). The document is computed by **balance targeting**: for each such account, the line amount is `declared target − current cumulative ledger balance` at the time the chunk runs. Because HISTORY is already posted, the current balance includes the `HISTORY_RESULT` counter lines of the history summaries and the closing entries of the history years, and earlier opening documents in the batch. Accounts without history effect have a current balance of zero, so the line is the declared balance. For `HISTORY_RESULT` the target is zero, so the line zeroes the accumulated counter lines (it is not zero-by-accident: the history summaries are already there). For the retained earnings account, the declared target is the legacy balance, and the closing entries of the history years (6.1) are taken into account by targeting. The counter line of each document is on `MIGRATION_CLEARING`, in the transaction currency of that document, for the net of the subledger-managed accounts, so that each document balances.

### 6.3 Open items (`OPENING_ITEMS`)

Documents dated the cutover date, **split per transaction currency (D7)**: an `OPENING_ITEMS` document holds the items of one transaction currency only, so a customer with items in two currencies appears in two documents. A line per `OPEN_ITEM` or `GLOPEN_ITEM` record on the subledger account, with the open-item row (partner, due date, terms, original dates in `document_date` of the line's attributes). Original posting date and document date of the legacy item are kept on the open-item row (new L1 columns `legacy_document_date`, `legacy_posting_date`, `legacy_document_ref`) for aging and tracing, while the document's own posting date is the cutover date. Amounts are the **remaining open amounts** at the cutover date as supplied (5), with all four currencies. Documents are split by the tenant setting `migration_document_max_lines` (maximum number of lines of one migration document, default 5,000, the same limit for all migration document types, 3.6), after the split by transaction currency, and the documents are grouped into chunks by `migration_commit_chunk_documents` (3.6). The counter line per document is on `MIGRATION_CLEARING`, in the transaction currency of the document. Aging of migrated items uses the legacy due dates. Realised FX at later clearing uses the supplied booked amounts (ADR-038).

### 6.4 Stock opening (`STOCK_OPENING`)

A stock document type of 10, with movement type `OPENING_IN`: one movement per `STOCK_OPENING_LINE` record (a FIFO item has one movement per layer with `layer_date` as the layer's date, stored in `stock_cost_layer.layer_date` (10, 4.3), and the supplied unit cost). **One transaction currency per stock document (D7, 10, 4.1):** the values are supplied in local currency, so every `STOCK_OPENING` document has the company currency as its transaction currency and no foreign `value_tc`. **Synthetic layer of a migrated receipt (17 section 5, FIFO edge a, 10, 4.3).** For a FIFO item the receipt is carried by a `STOCK_OPENING_LINE` that names it through the optional `receipt_legacy_ref` (L1, also on `stock_cost_layer`): the line is valued at the legacy `value_lc` of the receipt line and takes the receipt date as `layer_date`. The mechanism is confirmed (user approval 2026-10-14) and described in 10, 4.3 and in 6.5 (received not invoiced). 

### 6.5 Orders and in-transit goods

**Open orders.** Each open line becomes a normal order line with `base_quantity` = the **open quantity**, a `source_document_ref` and `legacy_order_ref` (and `legacy_line_ref` when supplied, 11, 4.1 and 4.2), `origin` = MIGRATED on the order, and `migration_batch_id`. `requested_date` and `confirmed_date` are carried when known (16, 5). History of earlier deliveries stays in the legacy system. Reservations: the RESERVATIONS chunk reserves best effort against the opening stock **only if** the company setting `auto_reserve` (11, 3.1, default off, ADR-157) is on for the company at the cutover date. Otherwise the chunk is empty and apps reserve after the seal.

**Delivered not invoiced (sales).** A migrated `sales_delivery` with `origin` = MIGRATED, `stock_document_id` null, no journal, quantity as delivered, so the core can invoice it later. Its lines carry `value_lc` from the record (D5), the valuation basis for returns (11, 5.1, 5.4). The legacy system already recognised cost of goods sold. Revenue follows the invoice from the core (11, A1).

**Received not invoiced (purchase).** A migrated `purchase_receipt` with `origin` = MIGRATED, no stock movement (stock is in 6.4) and a **GR/IR open item**: a credit line on the `GR_IR` account in an `OPENING_ITEMS` document at the receipt value (in the transaction currency of the record, D7), referenced from the receipt line (`gr_ir_journal_line`, L1 on `purchase_receipt_line`, 11, 5.2). The receipt line's `value_lc` is the legacy GR/IR value and the valuation basis for returns (D5). For a FIFO item that value is also the value of the **synthetic cost layer** of the receipt, which the STOCK chunk creates through the `STOCK_OPENING_LINE` that names the receipt (6.4, 10, 4.3), so a purchase return of the migrated receipt consumes that layer. The supplier invoice later matches and clears it exactly as for live receipts (11, 7.2, ADR-109).

**Dropship pending.** A migrated `dropship_confirmation` with the same pattern: no stock, a GR/IR open item at the purchase value, referenced by its `gr_ir_journal_line` (11, 5.3).

Migrated fulfilment documents are nodes of the fulfilment chain (11, 5.5, the `chain_head` row of type FULFILMENT) and are marked `origin` = MIGRATED with `migration_batch_id`. Migrated records emit no per-record outbox events. The batch writes one `migration.batch_status_changed` event per status change, except that the transition to SEALED writes `migration.sealed` (counts and chain ranges) as its status-change event (E6, 9, 15 delta). Orders, deliveries and receipts migrated this way get their own number series (non-legal) and keep the legacy numbers in `legacy_order_ref`.

## 7. Commands

All commands are synchronous, atomic and idempotent, except that `commit` and `rollback` are atomic **per chunk** (3.6) and resume on a repeated call. Error area `MIGRATION` is added to 06. Every posting command writes no per-record outbox events (6.5).

| Command | Permission | Notes |
|---|---|---|
| `migration-batches:open` | `migration.manage` | Company, cutover date, source system, history scope, and the parameter **`history_until_period`** (INT, Opt: the number of the last history period of the cutover year, that is the period before the cutover period. Null if the cutover date is the first period of the year and without history). **This is the command that marks the cutover year for the batch:** it writes the value to the existing cutover `fiscal_year` (04, 3.2, L1, set once, D8, 4.2) in the same transaction. It must equal the period number before the period that contains `cutover_date`, else `MIGRATION.HISTORY_UNTIL_INVALID`. Requires company status **DRAFT** (company activation happens after the seal), the roles of 4.1 (including `RETAINED_EARNINGS` when history is in scope), an open cutover year. One batch per company that is not ABORTED or ROLLED_BACK (D6). Writes `migration.batch_status_changed` (E6) |
| `migration-batches:declare-totals` | `migration.manage` | Control totals of 3.2. Replaceable until commit |
| `fiscal-years:open-history` | `history.open` | 4.2 (permission per D10) |
| `migration-records:stage` | `migration.stage` | Batches of canonical records. Idempotent per type and `legacy_ref`. Returns per-record validation results |
| `migration-records:list` | `read.audit` | Records with status and errors |
| `migration-batches:validate` | `migration.manage` | Re-validates all current records. Status VALIDATED if none is INVALID. Writes `migration.batch_status_changed` when the status changes (E6) |
| `migration-batches:reconcile` | `migration.manage` | Section 8. Writes a reconciliation run. Repeatable. Writes `migration.batch_status_changed` when the status changes to RECONCILED (E6) |
| `migration-batches:commit` | `migration.commit` | Requires company status DRAFT (the controlled exception of 6) and the latest reconciliation to have no FAIL. Computes the chunk plan (3.6) and posts in this fixed order (D8, ADR-152): (1) HISTORY: history summaries and the closing entries of the history years, (2) OPENING_GL, (3) OPEN_ITEMS, (4) STOCK (including in-transit stock), (5) ORDERS, (6) FULFILMENT (migrated deliveries, receipts and dropship confirmations with their opening GR/IR items), (7) RESERVATIONS, (8) LEGACY_TAX. Each chunk is **one atomic transaction**. Status COMMITTING, then COMMITTED when all chunks are DONE (each status change writes `migration.batch_status_changed`, E6). A commit that stops in COMMITTING with no chunk DONE can be aborted (`abort`) or resumed. Calling commit again **resumes** from the first PENDING or FAILED chunk, with unchanged staged records (a changed record needs a new reconciliation first) |
| `migration-batches:rollback` | `migration.commit` | Only in COMMITTING or COMMITTED, before the seal. It is the **owning command** of all migration documents (D1, ADR-145) and a **mirror-producing command with reason code CORRECTION** (B4, 02, 3): it reverses all documents of the batch in reverse chunk order, including the `CLOSING` documents of the history years (reversals under the batch exceptions, mirror documents that **keep the original type** (`HISTORY_SUMMARY`, `CLOSING`, `OPENING`, `OPENING_ITEMS`, `STOCK_OPENING`) with `reversal_of_document_id` and the reason code CORRECTION, `origin` = MIGRATION, chains stay intact, 02 ADR-048), cancels migrated orders, reverses migrated fulfilment documents, releases reservations and **removes the bookkeeping rows of the batch (D6): the `legacy_id_map` rows with this `batch_id` (3.5) and the `item_tracking` rows that the `STOCK_OPENING` documents created (6.4), the latter only where no document outside the batch refers to them**, and the chunk state is reset (3.6: the chunks of a rolled-back batch are not reused, a new batch plans its own). Batch status **ROLLED_BACK, terminal**, which does not count as non-aborted (3.1). **CLOSING documents (17 section 5):** the mirror of a `CLOSING` document of a history year **keeps the type `CLOSING`** (and is excluded from P&L reads like the original, 6.1), and the rollback **resets** `fiscal_year.year_end_document_id` of each history year (to null) and the **history year status** (to OPEN, with its history periods OPEN) wherever the batch had set them, so that no year points to a mirrored closing document. Before the seal this normally changes nothing, since the seal sets both (9.1). History fiscal years and periods remain (and `fiscal_year.history_until_period` stays). A new batch can then be opened for the company. Writes `migration.batch_status_changed` (E6) |
| `migration-batches:seal` | `migration.commit` | Section 9, including the migration variant of year close (9.1). Status SEALED. Writes **`migration.sealed`** as the status-change event of the transition (one event, not two, E6, 15, 6) |
| `migration-batches:abort` | `migration.manage` | Allowed before commit, **and in COMMITTING when no chunk is DONE** (D6: a commit that failed or was interrupted in its first chunk has posted nothing, because every chunk is atomic). Not allowed once any chunk is DONE (use `rollback`) and not in COMMITTED, SEALED, ABORTED or ROLLED_BACK: rejected with `MIGRATION.ABORT_NOT_ALLOWED` (7.1). Status ABORTED (terminal), frees the company for a new batch. Writes `migration.batch_status_changed` (E6) |
| `migration-batches:approve` | `migration.commit` | Four-eyes (Q13 decided). In a PRODUCTION tenant, `commit` and `seal` by one identity only **request**: nothing is posted until a second identity with `migration.commit` calls `approve` for that request (`MIGRATION.APPROVER_NOT_DISTINCT` if the same identity). In REHEARSAL and TEST tenants one identity suffices and `approve` is not needed. A request is cancelled by `migration-batches:abort` or by a changed record (it must be requested again) |
| `legacy-ids:resolve`, `:list` | `read.config` | Mapping lookups |

### 7.1 Error codes (area `MIGRATION`, **accepted**)

The codes below are accepted (14, section 11) and entered in the code list of 06, together with `MIGRATION.APPROVER_NOT_DISTINCT` and `CONFIG.ROLE_NOT_ASSIGNED`:

| Command | Codes |
|---|---|
| All batch commands | `MIGRATION.BATCH_NOT_FOUND`, `MIGRATION.BATCH_STATUS_INVALID` (params: `status`, `allowed_statuses`) |
| `migration-batches:open` | `MIGRATION.BATCH_EXISTS` (a non-aborted batch exists for the company), `MIGRATION.COMPANY_NOT_DRAFT`, `MIGRATION.CUTOVER_NOT_PERIOD_START`, `MIGRATION.CUTOVER_YEAR_NOT_OPEN`, `MIGRATION.HISTORY_UNTIL_INVALID` (new, to be entered in 06: `history_until_period` is not the period before the cutover period). Missing roles: `CONFIG.ROLE_NOT_ASSIGNED` |
| `migration-batches:declare-totals` | `MIGRATION.CONTROL_KIND_INVALID` |
| `fiscal-years:open-history` | `MIGRATION.HISTORY_YEARS_EXCEEDED` (above `migration_max_history_years`), `MIGRATION.HISTORY_YEAR_NOT_CONTIGUOUS` (the year does not end the day before the earliest existing year starts, 4.2) |
| `migration-records:stage` | `MIGRATION.RECORD_TYPE_INVALID`, `MIGRATION.STAGE_BATCH_TOO_LARGE`. Record validation errors are stored on the record (3.3), not returned as command errors |
| `migration-batches:validate` | `MIGRATION.RECORDS_INVALID` (params: count) |
| `migration-batches:commit` | `MIGRATION.RECONCILIATION_MISSING` (none, or a record changed after it), `MIGRATION.RECONCILIATION_FAILED`, `MIGRATION.CHUNK_FAILED` (params: `chunk_no`, `area`, `failure_codes`) |
| `migration-batches:rollback` | `MIGRATION.ROLLBACK_NOT_ALLOWED` (status SEALED, ABORTED or ROLLED_BACK, or no committed chunk) |
| `migration-batches:abort` | `MIGRATION.ABORT_NOT_ALLOWED` (entered in 06: a chunk is DONE, or the status is COMMITTED, SEALED, ABORTED or ROLLED_BACK. Returned by `abort` in these cases instead of `MIGRATION.BATCH_STATUS_INVALID`, and its `params` name `rollback` as the command to use when a chunk is DONE) |
| `migration-batches:seal` | `MIGRATION.SEAL_CHECK_FAILED` (params: the failed checks of section 9), `MIGRATION.CLEARING_NOT_ZERO` |
| `migration-batches:approve` | `MIGRATION.APPROVER_NOT_DISTINCT` (exists), `MIGRATION.NO_PENDING_REQUEST` |
| `year-end:close` on a history year | `PERIOD.HISTORY_YEAR` (9.1, entered in 06 and 05). Also for `periods:soft-close` and `periods:hard-close` on history periods before seal. A **history period** is a period of a year with `is_history`, or a period of the cutover year with `period_no` ≤ `fiscal_year.history_until_period` (D8, 4.2) |
| `periods:reopen`, `year-end:reopen` on a history period or year | Rejected for sealed history years (9.1). Error `PERIOD.HISTORY_YEAR_SEALED` (14, section 12). Manual `periods:soft-close`, `periods:hard-close` and `year-end:close` on a history period before the seal are rejected with `PERIOD.HISTORY_YEAR` |

### 7.2 VALIDATE mode and chunk settings

VALIDATE mode is supported on `commit`: it runs the whole posting pipeline for the full batch without persisting, which is the strongest rehearsal check inside one tenant. Commit is chunked (3.6, Q9 decided): the chunk size is `tenant_setting.migration_commit_chunk_documents` and the lines per document are bounded by `tenant_setting.migration_document_max_lines` (6.3). These are the only two names: the earlier ceiling setting `migration_commit_max_documents` no longer exists.

## 8. Reconciliation

`migration-batches:reconcile` evaluates the staged records (what a commit would post), not the ledger. Checks (results `PASS`, `FAIL`, `WARNING`):

| Check | Compares | Fails if |
|---|---|---|
| `RECORDS_VALID` | Number of INVALID current records | Any |
| `TB_BALANCES` | Declared `TB_BY_ACCOUNT` totals | They do not net to zero in LC, GC and RC |
| `TB_ACCOUNTS_COMPLETE` | Declared TB accounts versus `OPENING_BALANCE` records and subledger sources | An account with a declared balance has neither |
| `HISTORY_PERIODS_BALANCE` | Each history period's `HISTORY_PL_LINE` sums and counter | A period document would not balance (always balances by construction: the check reports the net result per period) |
| `HISTORY_VS_DECLARED` | `HISTORY_PL_LINE` sums per period and account versus declared `HISTORY_PL_BY_PERIOD_ACCOUNT` | Any difference |
| `PL_YTD_VS_HISTORY` | Sum of the cutover year's history periods per P&L account versus declared `PL_YTD_BY_ACCOUNT` and the declared TB | Any difference |
| `OPEN_ITEMS_VS_TB` | Sum of `OPEN_ITEM` and `GLOPEN_ITEM` per account versus the declared TB balance of that account, **restricted to accounts with open-item mode `CONTROL` or `GL_OPEN_ITEM`** (M11) and **excluding** the `GR_IR` accounts (those are checked only by `GR_IR_VS_TB`). The `INVENTORY` accounts have no open items and are checked by `STOCK_VALUE_VS_TB` | Any difference (per currency column) |
| `STOCK_VALUE_VS_TB` | Sum of `STOCK_OPENING_LINE` values per `INVENTORY` account (by determination, in-transit lines included) versus the declared TB balance | Any difference |
| `GR_IR_VS_TB` | The **single** GR/IR check (D8, ADR-152): `RECEIVED_NOT_INVOICED`, `DROPSHIP_PENDING` and `GLOPEN_ITEM` records on `GR_IR` accounts, together, versus the declared TB balance of each GR/IR account. No other check covers GR/IR accounts | Any difference |
| `STOCK_QTY` | Quantities per item versus declared `STOCK_QTY_BY_ITEM` | Any difference |
| `ORDER_COUNTS` | Open order lines, delivered-not-invoiced lines and received-not-invoiced lines versus declared counts | Any difference |
| `CLEARING_ZERO` | Projected `MIGRATION_CLEARING` after all areas | Not zero in any currency |
| `VAT_VS_TB` | Sum of `LEGACY_TAX_SUMMARY` ledger amounts per VAT account versus the declared TB balance of that account | Any difference |
| `VAT_PERIOD_COMPLETE` | A registration whose VAT period contains the cutover date has `LEGACY_TAX_SUMMARY` records for the part before the cutover (or a declared zero) | Missing for a registration |
| `RETAINED_EARNINGS` | Target of the retained earnings account versus the `CLOSING` documents of the history years that the HISTORY chunk will post (6.1) and the targeting line of OPENING_GL. Also fails if the `RETAINED_EARNINGS` role is not assigned while history is in scope (4.1) | A difference not explained by the targeting |
| `DIMENSIONS` | History and opening lines on accounts with mandatory dimension rules | WARNING per account |
| `FX_ITEMS` | Open items in a foreign currency: implied rates outside a plausibility band | WARNING |
| `ZERO_TOLERANCE` | All differences above | There is **no tolerance**: any difference is a FAIL. Rounding differences must be resolved by the ETL |

A commit requires the latest reconciliation run to have no FAIL. WARNINGs are listed in the commit result.

## 9. Seal

`migration-batches:seal` runs the **post-commit verification** against the ledger, then finalises:

1. The ledger cumulative balance per account, as of the cutover date, equals the declared TB (all four currencies), and the cutover year's P&L accounts equal the declared YTD. `MIGRATION_CLEARING` is zero in every currency.
2. Open items by account equal the declared open items, with the `GR_IR` accounts compared once against the declared TB (the single GR/IR check of section 8). Stock value per `INVENTORY` account equals the GL balance (10, 8, valuation reconciliation). `chains:verify` passes for all chains of the company (LEDGER, CLEARING, PERIOD_EVENT, VAT_RETURN, STOCK, FULFILMENT, as far as they have rows).
3. If any check fails the seal is rejected with a report and nothing changes. The batch stays COMMITTED. A correction before the seal is by `migration-batches:rollback` (all documents of the batch, the owning command, D1, mirror documents with reason code CORRECTION, B4) and a new commit after restaging the changed records. Individual migration documents are not reversible directly (ADR-145).
4. On success, in one transaction: the **migration variant of year close** (9.1) runs for the history periods and years: history periods (periods of the history years and periods of the cutover year up to `history_until_period`, 4.2) are HARD_CLOSED (predecessor rule, 04), the history fiscal years **before the cutover year** are closed by status and get `year_end_document_id` set to their `CLOSING` document (the closing entries already exist since the HISTORY chunk, 6.1, so reports by fiscal year are exact: the P&L **excludes** the `CLOSING` documents like `YEAR_END`, so the comparatives are preserved, and balance reads include them, ADR-054, ADR-055, X1), with the `PERIOD_HARD_CLOSE` events for the periods and `YEAR_END_CLOSE` for each year as 04 and 05 require, executed by the migration role. The batch is SEALED, the controlled exceptions end, anchors are created for all chains of the company by `chain_type` and `scope_key` (ADR-046, K1), and the outbox event is written (E6): **`migration.sealed`**, the status-change event of the transition to SEALED (there is no separate `migration.batch_status_changed` for it), with the **counts** (documents, lines, orders, fulfilment documents, open items, stock movements, per record area) and the **chain ranges** (per chain type and scope key, the first and last `chain_seq` of the migrated chained records). `outbox:verify` accepts a migrated range as covered by this batch event and does not report the migrated chained records as gaps (15 delta).

### 9.1 Migration variant of year close (Q16 decided, 14 section 10)

`year-end:close` (05) cannot be used on history years: it would post a new closing document, and the closing entries of the history years already exist from the HISTORY chunk (6.1). The seal therefore runs a **migration variant** of the year close, internal to `migration-batches:seal` and not a callable command:

- It creates **no document** and no number. It uses the `CLOSING` documents posted by the HISTORY chunk as the closing document of each history year before the cutover year and sets `fiscal_year.year_end_document_id` of each such year to it (X1).
- For every history period (of the history years and of the cutover year up to `history_until_period`, 4.2) it writes `PERIOD_HARD_CLOSE` (PERIOD_EVENT chain, `period_no` set), in the predecessor order of 04.
- For every history year before the cutover year it writes `PERIOD_HARD_CLOSE` for the closing period and `YEAR_END_CLOSE` at year level (`period_no` null) (D4), and sets the year status to closed.
- It runs under the seal's permission `migration.commit` and the four-eyes request of Q13, in the seal's single transaction. It takes the PERIOD_EVENT head in the lock order of 04, 6.3.
- **No stale check.** The migration variant skips the D4 stale check: no revaluation runs in history periods, and the history documents were posted by the same batch (14, section 11).
- **No reopening after the seal.** A rollback (7) before the seal leaves the history periods OPEN, so no reopen is needed. After the seal, `periods:reopen` and `year-end:reopen` reject history periods and history years (`is_history`, and periods of the cutover year up to `history_until_period`, D8) with `PERIOD.HISTORY_YEAR_SEALED`. A correction after the seal is an adjustment posting through the normal controls (below, Q14).
- `year-end:close` called for a history year (`fiscal_year.is_history`), and manual `periods:soft-close` or `periods:hard-close` on a history period before the seal, are rejected with `PERIOD.HISTORY_YEAR`.

After the seal the migration documents are normal, immutable, chained documents. The migration permission can no longer post, and rollback is no longer possible. A mistake found later is corrected by adjustment postings through the normal controls to the right accounts, not by reversing migration documents (ADR-145). Corrections never post to `MIGRATION_CLEARING` (Q14). A repeated reconciliation can be read through `audit-events:query`.

## 10. Deltas to earlier specs

| Spec | Change |
|---|---|
| 02 | System document types `HISTORY_SUMMARY`, `OPENING`, `OPENING_ITEMS` (`OPENING` existed) and **`CLOSING`** (X1: system generated, set by the HISTORY chunk, owning command `migration-batches:rollback`, `CLOSING` is added to the type list and to `is_system_generated`, the line type `CLOSING` exists), line type `HISTORY_PL`. `journal_document.origin` = MIGRATION on all of them (X5). Closing entries of history years posted by the HISTORY chunk (6.1). Rollback is a mirror-producing command with reason code CORRECTION, the mirror keeps the original type (B4). New L1 column `journal_document.migration_batch_id`. `journal_line_open_item` new L1 columns `legacy_document_date`, `legacy_posting_date`, `legacy_document_ref` (6.3), **included in the line hash definition** of 02. Migration documents are not reversible through `journal-documents:reverse` (D1) |
| 04 | `fiscal-years:open-history` and `fiscal_year.is_history` (L1) and `fiscal_year.history_until_period` (L1, D8, 4.2), written by `migration-batches:open` (parameter `history_until_period`, 7) on the cutover year. The contiguity rule of 04, 3.1 is evaluated for history years against the next year (L13, 4.2, 04 must state it). History periods hard-closed at seal by the migration variant of year close, with `PERIOD_HARD_CLOSE` for the periods and `YEAR_END_CLOSE` (`period_no` null) for the years (D4, 9.1), without the stale check. `periods:reopen` rejects history periods of a sealed batch. Series assignments for migration document types |
| 05 | The seal closes the history years by status through the migration variant of year close (9.1): it writes `PERIOD_HARD_CLOSE` and `YEAR_END_CLOSE` for the `CLOSING` documents already posted by the HISTORY chunk, sets `year_end_document_id` and creates no document. P&L reads exclude `CLOSING` like `YEAR_END` (X1, 05 and 09). `year-end:close` rejects history years (`is_history`) with `PERIOD.HISTORY_YEAR`. The migration variant skips the D4 stale check. `periods:reopen` and `year-end:reopen` reject sealed history periods and years with `PERIOD.HISTORY_YEAR_SEALED` (04, 05). 05 must state the migration variant, the skipped stale check and the rejections |
| 07 | Roles `MIGRATION_CLEARING` and `HISTORY_RESULT` (4.1, with open-item mode NONE in the role table, D5). `legacy_id_map` (3.5). `tenant.purpose` (PRODUCTION, REHEARSAL, TEST). Create and update requests carry optional `legacy_ref` and `upsert` mode. `tenant_setting` gains `migration_max_history_years`, `migration_commit_chunk_documents` (replaces `migration_commit_max_documents`), `migration_document_max_lines` (default 5,000, the maximum lines of one migration document of any type, 3.6), `migration_stage_batch_size`. Company readiness checks migration roles when a batch is open. `item_partner` and calendars are loaded as master data before the batch (5) |
| 10 | Stock document type `STOCK_OPENING`, movement type `OPENING_IN`, supplied values and `layer_date` (stored in `stock_cost_layer.layer_date`, 10, 4.3, 6.4), `cost_source` MIGRATION (10, 5.6). Synthetic cost layer of a migrated receipt from its legacy `value_lc` (`receipt_legacy_ref`, 6.4, 10, 4.3). One transaction currency per stock document (10, 4.1). A rollback mirror of a `STOCK_OPENING` document keeps the type `STOCK_OPENING` (B4). In-transit stock is opened into a TRANSIT location (16, 9) |
| 11 | `origin` (LIVE, MIGRATED) on sales and purchase orders, deliveries, receipts and dropship confirmations. `migration_batch_id` on them. `legacy_order_ref` and `legacy_line_ref` on orders and lines. `purchase_receipt_line.gr_ir_journal_line` and `dropship_confirmation.gr_ir_journal_line` (L1). `value_lc` on migrated delivery and receipt lines as valuation basis for returns (D5). `requested_date` and `confirmed_date` on migrated order lines (the only writer of `confirmed_date` besides `change-line-dates`, 11, F12) |
| 06 | Error area `MIGRATION` with the accepted codes of 7.1 (`MIGRATION.ABORT_NOT_ALLOWED` is entered, `MIGRATION.HISTORY_UNTIL_INVALID` is new), and `PERIOD.HISTORY_YEAR`, `PERIOD.HISTORY_YEAR_SEALED`. Permissions `migration.manage`, `migration.stage`, `migration.commit` and `history.open` (D10) |
| 08 | `vat-returns:prepare` includes the `legacy_tax_summary` rows of the VAT period that contains the cutover, once per return type, and sets `reported_in_return_id` (3.6, Q1). The wording `source` = LEGACY in 08 has no column in `vat_return_line`: 08 defines how the rows are identified in the return |
| 09 | Audit events add migration batches and reconciliations. Reports can filter and exclude by `migration_batch_id`, `origin` = MIGRATION and the migration document types. **P&L reads exclude `CLOSING` like `YEAR_END`, balance reads include it** (X1, 09, 4.1) |
| 12 | Retention of migrated records counts from the legacy dates as in Q15 (history summaries from the end of their legacy fiscal year, open items and orders from their legacy document dates where supplied). Legal and address snapshots created by the migration count from the migration date. This replaces the earlier rule "from the migration date for classes without a record date". The audit package includes migration batches, control totals and reconciliations. The rehearsal tenant variant of the deletion workflow (Q10) is defined in 12 |
| 15 | Migrated records emit no per-record outbox events (E6). One `migration.batch_status_changed` event is written per batch status change, except the transition to SEALED, which writes `migration.sealed` as its status-change event with counts and chain ranges (new event type, `is_audit` true, summary of counts and ranges only). `outbox:verify` accepts a migrated range as covered by the batch event. Extension values can be loaded by a normal command after the seal |
| 16 | `requested_date` and `confirmed_date` on migrated open order lines. In-transit stock as `STOCK_OPENING` lines into a TRANSIT location. Staging records of orders (5) carry the two fields |

Added in v0.4 (14, section 11): no stale check in the seal variant, no reopening of sealed history years, `PERIOD.HISTORY_YEAR` and `PERIOD.HISTORY_YEAR_SEALED` (cleanup pass, 14 section 12) and the accepted `MIGRATION` codes (7.1, 9.1, 04, 05 and 06 rows), Q19 and Q20 closed. 08 delta: none new. The settlement reversal command is named `vat-returns:reverse-settlement` (14, section 11), not mentioned in this spec.

These deltas are applied in v0.2 (re-baseline of 14, sections 8 and 9). Added in v0.3 (14, section 10): the migration variant of year close (9.1, 04 and 05 rows), the `MIGRATION` error codes (7.1, proposed in v0.3 and accepted in v0.4), Q16 and Q17 decided.

**Added in v0.5 (17, section 4, fix pass 4).** X1: closing entries of history years are documents of the new type `CLOSING` (6, 6.1, 9, 9.1), excluded from P&L reads like `YEAR_END` and included in balance reads, `year_end_document_id` set at the seal, closing period and date stated (6.1), the contradiction of 6.1 and 9 removed, the garbled `exclude_document_types` sentence replaced. X5: `journal_document.origin` = MIGRATION (1, 6). D6: DRAFT company exception (6), abort in COMMITTING when no chunk is DONE (7, 7.1), ROLLED_BACK terminal and not counted as non-aborted (3.1), rollback removes `legacy_id_map` rows of the batch and `item_tracking` rows (7). B4: rollback is a mirror-producing command with reason CORRECTION and mirrors keep the original type (6, 7). D7: OPENING and OPENING_ITEMS split per transaction currency, one currency per stock document (6.2 to 6.4). D8: `history_until_period` on the cutover year, history guards keyed on it (4.2, 7.1, 9, 9.1). E6: events per status change and `migration.sealed` (2.1, 6.5, 9, 15 row), "no events" statements removed. `layer_date` references 10, 4.3 (5, 6.4). `OPEN_ITEMS_VS_TB` restricted to CONTROL and GL_OPEN_ITEM accounts (8). `RETAINED_EARNINGS` required at batch open when history is in scope (4.1, 8). Open-item amount is the remaining amount at cutover (5, 6.3). `migration_document_max_lines` is the maximum lines of one migration document of any type (3.6, 6.3). History years opened before the existing cutover year (4.2, L13). `value_lc` on `DELIVERED_NOT_INVOICED` (5, D5). Deltas to other specs: 02 (`CLOSING`, `origin`), 04 (`history_until_period`, contiguity variant), 05 and 09 (P&L excludes `CLOSING`), 06 (`MIGRATION.ABORT_NOT_ALLOWED`), 10 (`layer_date`, currency per stock document), 11 (`value_lc` basis), 15 (`migration.sealed`, per-status events).

**Added in v0.6 (17, section 5, propagation pass).** `history_until_period` is a parameter of `migration-batches:open`, which writes it to the cutover `fiscal_year` (4.2, 7, 7.1, new code `MIGRATION.HISTORY_UNTIL_INVALID`). Rollback of a batch with `CLOSING` documents: mirrors keep the type `CLOSING`, `year_end_document_id` and the history year status are reset (7). `MIGRATION.ABORT_NOT_ALLOWED` is used by `abort` (7, 7.1). One event for the SEALED transition: `migration.sealed` is its status-change event, other transitions write `migration.batch_status_changed` (2.1, 3.1, 6.5, 7, 9, 15 row), consistent with 15, 6. Synthetic cost layer of a migrated receipt from its legacy `value_lc` through `receipt_legacy_ref` on a `STOCK_OPENING_LINE` (5, 6.4, 6.5, 10 row). Deltas: 04 (`migration-batches:open` writes `history_until_period`, 04 and 06 currently name `fiscal-years:open` and `fiscal-years:open-history`), 06 (`MIGRATION.HISTORY_UNTIL_INVALID`), 10 (`receipt_legacy_ref`).

**Propagation pass 2 (v0.6).** The synthetic FIFO layer mechanism of a migrated receipt (optional `receipt_legacy_ref` on `STOCK_OPENING_LINE` and `stock_cost_layer`, L1; 5, 6.4, 6.5) is marked **provisional, to be confirmed** where it is described. Text otherwise unchanged. `history_until_period` is written by `migration-batches:open` (04, 7.3 now agrees).

**Delta of ADR-200 and ADR-204 (2026-10-14).** No change to the commit logic or the line-type exceptions: migration documents carry no tax lines (VAT history is the `legacy_tax_summary` of 3.6), so `TAX_NONDEDUCTIBLE` lines and the derived EU flags do not occur in them. Documents committed by a migration batch carry the `hash_version` in force at the commit (02, 9.3, `hash_version` 2).

## 11. Assumptions

| Ref | Item |
|---|---|
| A1 | The ETL provides a legacy trial balance at the cutover date that balances in LC (and in GC and RC where used). The core does not repair an unbalanced legacy extract |
| A2 | History comparatives cover P&L accounts only, in LC, GC and RC, optionally by dimension. No balance sheet comparatives |
| A3 | Legal numbering continues in the live series. Legacy document numbers stay in the legacy archive and as `legacy_*` references |
| A4 | The legacy system or its archive is retained for the statutory period of the history it holds, because the core holds no original historic documents |
| A5 | Master data is loaded before the batch through the normal APIs. Bulk loads call the existing commands repeatedly (Q12) |
| A6 | A rehearsal tenant contains real personal data if the extract has it, and is protected like production until deleted (12) |

## 12. Open questions

| # | Question |
|---|---|
| Q1 | **Decided (differs from the proposal):** a cutover inside a VAT period is allowed. The ETL declares the legacy tax of the partial period as `LEGACY_TAX_SUMMARY` records (box rows, EC list rows, OSS rows, with VAT account and ledger amounts). They are stored in `legacy_tax_summary`, included in the first VAT return preparation (08 delta), and the opening VAT balances are allowed and reconciled against them. |
| Q2 | **Decided:** subledger-managed accounts are those with open-item mode CONTROL or GL_OPEN_ITEM, the `INVENTORY` accounts and `GR_IR`. All others are plain opening balances. An `OPENING_BALANCE` record for a subledger account is rejected. |
| Q3 | **Decided (differs from the proposal):** the open quantity becomes the ordered quantity, prices come from the legacy line, earlier deliveries are not recreated. Only `legacy_order_ref` is mandatory, `legacy_line_ref` is optional. Without a line reference, a line can be traced only by its position within the legacy order (the ETL supplies the position as part of `legacy_ref`). |
| Q4 | **Decided:** delivered-not-invoiced and received-not-invoiced goods are migrated as fulfilment documents without stock effect, with a GR/IR open item for receipts (6.5). |
| Q5 | **Decided:** stock opening values and FIFO layers are supplied. Negative opening quantities are not allowed. |
| Q6 | **Decided:** monthly (per fiscal period) summaries, up to `migration_max_history_years` (default 3), dimension detail optional. No yearly summary option. |
| Q7 | **Decided, amended by Q16:** history years before the cutover year are closed at the seal by the migration variant of year close (9.1), not by `year-end:close`. Their calendars need at least one special period. |
| Q8 | **Decided:** LC, GC and RC are required wherever the company uses GC and RC. If the legacy lacks them the ETL derives them and documents the rates. The core never invents booked values. |
| Q9 | **Decided (differs from the proposal):** volumes are unknown, so commit is chunked and resumable (3.6), with rollback by reversal (7). A batch that fits one chunk commits atomically. The company stays in DRAFT until the seal so partial state is never live. |
| Q10 | **Decided:** REHEARSAL tenants relax external anchoring (none) and tenant deletion (one approver, no waiting period, no retention override, 12). Everything else behaves like production. |
| Q11 | **Decided:** live legal series continue from the legacy numbers through `start_number` for the cutover year (04, 5.1). Migration documents use non-legal series. |
| Q12 | **Decided:** batch variants for master data (`{resource}:upsert-batch`, 07, 11.5): many objects per call, per-item results, idempotency per item, `legacy_ref` per object. |
| Q13 | **Decided:** in PRODUCTION tenants commit and seal need two distinct identities with `migration.commit` (request and approve, section 7). Rehearsal tenants skip the second approval. |
| Q14 | **Decided:** the seal requires `MIGRATION_CLEARING` to be zero in every currency. Later corrections are adjustment postings through the normal controls to the right accounts, never to `MIGRATION_CLEARING`. |
| Q15 | **Decided:** retention of migrated records counts from the legacy dates: history summaries from the end of their own legacy fiscal year, migrated open items and orders from their legacy document dates where supplied. Legal snapshots and address snapshots created by the migration count from the migration date. Rules of 12 apply per record class. |
| Q16 | **Decided (14 section 10):** the seal runs a migration variant of year close for the history years. It writes `PERIOD_HARD_CLOSE` and `YEAR_END_CLOSE` for the closing entries already posted by the HISTORY chunk and creates no document. `year-end:close` cannot be used on history years (9.1). |
| Q17 | **Decided (14 section 10):** a correction of a migration document between commit and seal is a rollback of the batch and a recommit after restaging. There is no finer correction path (7, 9). |
| Q18 | **Decided (D8):** commit order HISTORY (with closing entries), OPENING_GL, OPEN_ITEMS, STOCK, ORDERS, FULFILMENT, RESERVATIONS, LEGACY_TAX. One GR/IR check against the TB (`GR_IR_VS_TB`). Exceptions apply to OPEN, COMMITTING and COMMITTED batches until the seal. Commit is atomic per chunk. Setting names `migration_commit_chunk_documents` and `migration_document_max_lines` (ADR-152). |
| Q19 | **Decided (14 section 11):** the `MIGRATION` codes of 7.1 are accepted. `year-end:close` on a history year is rejected with `PERIOD.HISTORY_YEAR` (9.1). |
| Q20 | **Decided (14 section 11):** the seal variant of year close skips the D4 stale check. Sealed history years cannot be reopened: `periods:reopen` and `year-end:reopen` reject history periods with `PERIOD.HISTORY_YEAR_SEALED`. Corrections use adjustment postings (9.1). Closed in 14, section 12 (code decided). |

## 13. ADRs raised by this spec (now in 01-adr-log, which is authoritative; ADR-128 reflects the chunked commit, ADR-135 and ADR-136 were added while deciding Q1 and Q12)

**ADR-128: Migration batch layer with external ETL.** ETL is external. The core stages validated canonical records, reconciles them against declared control totals with zero tolerance, commits in resumable chunks, each atomic, under controlled exceptions and seals.

**ADR-129: P&L history for comparatives with a suspense result account.** History periods carry P&L summaries balanced against `HISTORY_RESULT`, which the opening document zeroes. History years are closed at the seal by the migration variant of year close (9.1), the closing entries having been posted by the HISTORY chunk. No balance sheet comparatives.

**ADR-130: Balance targeting for opening balances.** Opening lines are `declared target − current ledger balance` for non-subledger accounts. Subledger accounts are opened only through open items, stock opening and migrated fulfilment, balanced against `MIGRATION_CLEARING`, which must be zero at the seal.

**ADR-131: Controlled migration exceptions.** Window, period status, derived amounts, partner rule, VAT account rule, dimension rules and computed costs are relaxed only for documents committed by a migration batch, which carry `migration_batch_id`. The exceptions end at the seal.

**ADR-132: Migrated fulfilment and orders.** Open orders carry the open quantity. Delivered-not-invoiced and received-not-invoiced are migrated fulfilment documents without stock effect, with GR/IR open items for receipts.

**ADR-133: `legacy_id_map`.** A core mapping of legacy ids to core ids, written by master data loads and migration, queried in both directions.

**ADR-134: Rehearsal tenants.** Rehearsals run in tenants marked REHEARSAL, discarded by tenant deletion with relaxed rules. The core does not clone tenants in release 1.

Amended at the re-baseline (14, sections 8, 9 and 10; ADR numbers for the follow-up decisions on the migration variant of year close are assigned in 01-adr-log): **ADR-152** (migration commit order, one GR/IR check, exception scope until the seal, atomic per chunk) amends ADR-128 (the commit is atomic per chunk, not per company), ADR-129 and ADR-130 (the closing entries of history years are posted in the first chunk, so targeting sees them), ADR-131 (exception scope), and ADR-054 and ADR-055 (opening documents and closing entries). **ADR-145** (reversal through the owning command) means migration documents are reversed only by `migration-batches:rollback`. **ADR-150** adds `cost_source` MIGRATION for supplied stock opening values. In-transit stock follows **ADR-143**, order line dates **ADR-141**, events at the seal **ADR-159**.
