# Finance Core: Number Ranges and Fiscal Periods, Table Specs

Status: DRAFT v0.6 for review, propagation pass of 2026-10-13 applied (17, section 5: history-year contiguity variant, `history_until_period` parameter note, posting-rule codes that 06 must contain; delta row in section 12), fix pass 4 of 2026-10-13 applied (decisions of 17, section 4: A1 to A4, X1, X4, D8, series by fiscal year, legal series, special and closing period rules, S trigger rule; section 12 lists them), re-baselined on 2026-10-13, third pass applied (v0.4 decisions of 14, section 11: clearing pre-lock condition definitive, history years and migration year close; second pass: D3 counters before chain heads, D4 wording confirmed), for the scope review (14, sections 8 to 11): D3 (authoritative lock order, ADR-147), D4 (stale check and hard-close events, ADR-148), K1 (`chain_head`, ADR-158), K3 (outbox events, ADR-159), D10 (permission of `fiscal-years:open-history`, ADR-154) and the mechanical fixes (section 12). Builds on 00-design-principles v0.4, ADR-001 to ADR-174, 02-journal-document-core v0.4 and 03-clearing v0.4.
Scope: fiscal calendar, fiscal year, fiscal period and status, period event log, posting window, number series, series assignment, number range counters. Validation rules that use them at posting time.
Out of scope (other domains): revaluation run tables, year-end closing document content, authorisation model, generic configuration audit log, company master.
Conventions, lock classes (L1, L2, S), types and sign convention are as in 02-journal-document-core, section 1. Configuration tables follow P2: CRUD API, effective-dated, audited, no hard delete once referenced.

## 1. Decisions taken

| Topic | Decision |
|---|---|
| Period status | Current status is stored on `fiscal_period` (lock class S) and every change also appends a `period_event` row. `chains:verify` recomputes status from events |
| Calendar | Fiscal year starts in any month. 12 regular monthly periods plus up to N special periods (13 and up) that share the date range of period 12. No 4-4-5 or free period definitions in R1 |
| Year and period creation | Explicit `fiscal-years:open` command creates the year, all periods and all number counters in one audited step. No implicit creation at posting |
| Number series | A config assignment table maps (company, document type, optionally ledger) to a series, effective-dated. The core picks the series, the caller cannot choose. Each series restarts per fiscal year |
| Number storage | Documents store a numeric `sequence_no` (basis of the gapless check) and the formatted `document_number` |

## 2. Table overview

| # | Table | Layer | Role | Mutability |
|---|---|---|---|---|
| 1 | `fiscal_calendar` | Configuration | Calendar definition (first month, special period count) | Config CRUD, versioned, affects only years opened later |
| 2 | `fiscal_year` | Transaction-adjacent | One row per company and fiscal year | Immutable columns plus S status |
| 3 | `fiscal_period` | Transaction-adjacent | One row per period of a fiscal year | Immutable columns plus S status |
| 4 | `period_event` | Transaction-adjacent | Append-only, hash-chained log of year and period status changes | Insert only |
| ~~4a~~ | ~~`period_event_chain_head`~~ | | Removed (14 K1, ADR-158). The head is a row of the shared `chain_head` (02, 9.2) with `chain_type` = PERIOD_EVENT and empty `scope_key` | |
| 5 | `posting_window` | Configuration | Earliest and latest allowed posting date, effective-dated | Insert only (a change is a new row) |
| 6 | `number_series` | Configuration | Series definition and format | Config CRUD, restricted once numbers are issued |
| 7 | `number_series_assignment` | Configuration | Maps document type to series, effective-dated | Insert only |
| 8 | `number_range_counter` | Transaction-adjacent | Counter per series and fiscal year. Serialisation point | S |

Rows 2 to 4 and 8 are written only by core commands. They are not in the configuration CRUD API.

## 3. Fiscal calendar, year, period

### 3.1 `fiscal_calendar` (configuration)

PK: (`tenant_id`, `calendar_id`)

| Column | Type | Null | Notes |
|---|---|---|---|
| tenant_id, calendar_id | ID | N | |
| calendar_code | CODE | N | Unique per tenant |
| description | TEXT(100) | Y | |
| first_month | SMALLINT | N | 1 to 12. Month in which a fiscal year starts |
| special_period_count | SMALLINT | N | 0 to 4. Number of special periods (13 and up) |
| year_label_rule | CODE | N | START or END: whether the fiscal year label is the calendar year of the first or last day. Relevant when `first_month` is not 1 |

A company references a calendar in its company-level settings (ADR-015). Changing a calendar never alters years already opened, because `fiscal_year` stores the calendar and the resulting dates.

### 3.2 `fiscal_year`

PK: (`tenant_id`, `company_id`, `fiscal_year`)
Unique: (`tenant_id`, `company_id`, `start_date`)

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| tenant_id, company_id | ID | N | L1 | |
| fiscal_year | INT | N | L1 | Label per `year_label_rule` || calendar_id | ID | N | L1 | Calendar used at opening |
| start_date, end_date | DATE | N | L1 | Calendar year of 12 months. The first year of a company may be short: it starts on the first day of a later month of the calendar year and ends at the calendar year end (Q6 decided). Must follow directly after the previous year, no gap and no overlap. **Variant for history years (decided in 17, section 4, D8):** `fiscal-years:open-history` may open a history year **before an existing cutover year** (and before earlier history years). The rule then holds in both directions: the new year ends the day before the start of the earliest existing year of the company (no gap, no overlap), and it may precede that year, so the contiguity check compares with the next year instead of the previous one. The opened history years stay contiguous with each other and with the cutover year (7.3) |
| first_period_no | INT | N | L1 | 1 for normal years. For a short first year, the period number of the first created period (e.g. 4 for a start in the fourth month of the calendar year) |
| special_period_count | SMALLINT | N | L1 | Copied from the calendar at opening |
| is_history | BOOL | N | L1 | True for fiscal years opened by `fiscal-years:open-history` of a migration batch, before the cutover year (13, 4.2). Hard-closed and closed at the seal by the migration variant of year close (7.3). `year-end:close` is rejected on them. Once sealed they cannot be reopened (7.3). History periods **inside** the cutover year are marked by `history_until_period` below |
| history_until_period | INT | Y | L1 | **Only on the cutover fiscal year of a migration (D8, decided in 17, section 4), null on every other year.** The regular periods 1 (or `first_period_no`) up to and including this period number are history periods inside the cutover year (13, 4.2). Set when the cutover year is opened for a migration batch. **Parameter note:** the value is the number of the last history period of the year, that is the period before the cutover period, null if the cutover period is the first period of the year or if there is no history (13, 4.2). It is passed as the parameter `history_until_period` of the command that opens the cutover year, only together with an OPEN migration batch. The command parameter is defined in 13 (4.2) and 06 (command table). Together with `is_history` (years before the cutover year) it defines the history guards: a period is a **history period** if its year has `is_history` = true, or if it lies in a year with `history_until_period` set and `period_no` is at most that value. `PERIOD.HISTORY_YEAR` and `PERIOD.HISTORY_YEAR_SEALED` key on this definition (7.3) |
| status | CODE | N | S | OPEN or CLOSED (year-end closed, ADR-024) |
| year_end_document_id | ID | Y | S | The closing document (ADR-024). FK to `journal_document`. For a **history year** (`is_history` = true) it is the `CLOSING` document of the HISTORY chunk (02, 3, X1), set by the migration variant of year close at the seal (7.3) |
| opened_at, opened_by | TS, TEXT(100) | N | L1 | |
| last_event_id | ID | Y | S | Latest `period_event` for this year |

### 3.3 `fiscal_period`

PK: (`tenant_id`, `company_id`, `fiscal_year`, `period_no`)
FK: (`tenant_id`, `company_id`, `fiscal_year`) to `fiscal_year`.
Unique: (`tenant_id`, `company_id`, `start_date`) where `period_type` = REGULAR.
Index: (`tenant_id`, `company_id`, `start_date`, `end_date`): the lookup path from a posting date.

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| tenant_id, company_id, fiscal_year | ID, ID, INT | N | L1 | |
| period_no | INT | N | L1 | `first_period_no` to 12 regular (1 to 12 in a normal year), 13 to 12 + N special |
| period_type | CODE | N | L1 | REGULAR or SPECIAL |
| start_date, end_date | DATE | N | L1 | Regular periods: calendar month boundaries. Special periods: same as period 12 |
| status | CODE | N | S | OPEN, SOFT_CLOSED, HARD_CLOSED. All periods are created OPEN |
| last_event_id | ID | Y | S | Latest `period_event` for this period |

A special period is never selected by posting date. A command must name it explicitly, and then the posting date must lie inside the range of period 12 (assumption A3). Documents in a special period still carry a real posting date.

**Type consistency.** Every column that holds a period number (`period_no`, `first_period_no`, `period_event.period_no`, `history_until_period`, `fiscal_period` on other tables, 02, 5.1 and 05, 3.5) is INT. SMALLINT is used only for small counts and codes (`first_month`, `special_period_count`, `hash_version`).

**Status rules of special periods and the closing period (decided in 17, section 4, A4).**
- *Predecessor rule for special periods.* Special periods are ordered by number after period 12. A special period can be hard-closed only if its predecessor is HARD_CLOSED: period 12 for period 13, period 13 for period 14, and so on (7.3). It can be reopened only if no later period of the company is HARD_CLOSED (7.3).
- *The closing period* is the highest special period of the year (`special_period_count` at least 1 for years that use `year-end:close`). It is OPEN from the creation of the year until `year-end:close`, which hard-closes it, and it returns to OPEN only through `year-end:reopen`. **Manual status commands on the closing period are forbidden:** `periods:soft-close`, `periods:hard-close` and `periods:reopen` on it are rejected with `PERIOD.CLOSING_PERIOD_MANAGED` (new code). Every non-closing special period is managed by the ordinary status commands.

### 3.4 `period_event`

Append-only log required by ADR-023. Covers years and periods.
PK: (`tenant_id`, `event_id`)
Unique: (`tenant_id`, `company_id`, `event_seq`)
Unique: (`tenant_id`, `idempotency_key`)
Index: (`tenant_id`, `company_id`, `fiscal_year`, `period_no`, `event_seq`).

| Column | Type | Null | Notes |
|---|---|---|---|
| tenant_id, event_id | ID | N | |
| company_id | ID | N | |
| event_seq | BIGINT | N | Gapless per (tenant, company). Orders events for status recomputation |
| fiscal_year | INT | N | |
| period_no | INT | Y | Null for year-level events |
| event_type | CODE | N | YEAR_OPEN, PERIOD_SOFT_CLOSE, PERIOD_HARD_CLOSE, PERIOD_REOPEN, YEAR_END_CLOSE, YEAR_END_REOPEN. `year-end:close` writes PERIOD_HARD_CLOSE for the closing period and YEAR_END_CLOSE for the year (D4, ADR-148). `year-end:reopen` writes PERIOD_REOPEN for the closing period and YEAR_END_REOPEN |
| from_status | CODE | Y | Null for YEAR_OPEN |
| to_status | CODE | N | |
| reason_code | CODE | Y | Mandatory for the reopen events (ADR-023). This is the **payload reason code of the reopen command** (vocabulary in 06). It is not the `reversal_reason_code` of the mirror document that `year-end:reopen` posts, which is always `REOPEN` (02, 3) |
| reason_text | TEXT(500) | Y | Mandatory for the reopen events |
| authorisation | CODE | Y | Authorisation that permitted the command (e.g. period reopen, year-end reopen) |
| document_id | ID | Y | YEAR_END_CLOSE: the closing document (for a history year the CLOSING document of the HISTORY chunk, 7.3). YEAR_END_REOPEN: the closing document that is being reversed (known before the mirror is posted, 7.3), the mirror is reached through its `reversal_of_document_id` |
| event_at | TS | N | UTC |
| event_by | TEXT(100) | N | User or service identity |
| correlation_id | TEXT(100) | N | |
| idempotency_key | TEXT(100) | N | Mandatory on all commands (ADR-004) |
| prev_hash | BINARY(32) | N | Hash of the previous event of the company. Genesis value for `event_seq` 1 |
| row_hash | BINARY(32) | N | See 3.5 |
| hash_version | SMALLINT | N | |

Status on `fiscal_period` and `fiscal_year` is written in the same transaction as the event. For every period, `status` equals `to_status` of its highest `event_seq`. `chains:verify` checks this.

### 3.5 Event chain (Q1 decided)

Same scheme as the ledger and clearing chains (02, section 9): unkeyed SHA-256, one chain per (`tenant_id`, `company_id`), `event_seq` doubles as chain sequence.

`row_hash = H( hash_version | tenant_id | company_id | event_seq | prev_hash | all other columns of period_event except the hash columns, in fixed order )`

All `period_event` columns are immutable, so the whole row is hashed. For YEAR_END_CLOSE and YEAR_END_REOPEN the referenced `document_id` is part of the hash, and the year-end document stays verifiable through the ledger chain.

**3.5a Canonical form (hash_version 2, written during implementation, finding F-IMPL-10).** 3.5 gives the principle but not the byte layout. The layout is the one of the ledger chain (02, 9.3a): the same value encoding and the same field and block rules. `row_hash` is the SHA-256 of these fields in this order:

1. the hash version as text (`2`), `tenant_id`, `company_id`, `event_seq` as text, and `prev_hash` as 64 lower-case hex characters, each as one field;
2. one block with the remaining columns of 3.4 in table order, without the hash columns: `event_id`, `fiscal_year`, `period_no`, `event_type`, `from_status`, `to_status`, `reason_code`, `reason_text`, `authorisation`, `document_id`, `event_at`, `event_by`, `correlation_id`, `idempotency_key`. Types: ID, INT, text, and TS for `event_at` (UTC, six fraction digits, `Z`). NULL is the length `FFFFFFFF`.

The genesis value is the one of 02, 9.3a with `chain_type` PERIOD_EVENT and an empty `scope_key`. Two implementations (C# and Node) reproduce `test-vectors/period-event-hash-v2.json` (ADR-211). `chains:verify` recomputes the hash from the stored columns.

Chain head: the row of `chain_head` (02, 9.2) with key (`tenant_id`, `company_id`, `chain_type` = PERIOD_EVENT, `scope_key` = empty), columns `last_seq` (= `event_seq`), `last_hash`, `hash_version`, `updated_at` (all S). Locked by every status command at the position given by 6.3 (K1, ADR-158). Anchored through `chain_anchor` with `chain_type` = PERIOD_EVENT. The table `period_event_chain_head` no longer exists.

## 4. Posting window

### 4.1 `posting_window` (configuration, insert only)

ADR-025: per company, earliest and latest allowed posting date, effective-dated and audited. A change is a new row, so the history is the audit trail.
PK: (`tenant_id`, `company_id`, `window_id`)
Unique: (`tenant_id`, `company_id`, `effective_from`)

| Column | Type | Null | Notes |
|---|---|---|---|
| tenant_id, company_id, window_id | ID | N | |
| effective_from | TS | N | UTC instant from which the row applies. The row in force is the one with the highest `effective_from` not later than the time of the command |
| earliest_posting_date | DATE | N | |
| latest_posting_date | DATE | N | Must not be earlier than `earliest_posting_date` |
| reason_code | CODE | Y | |
| created_by | TEXT(100) | N | |
| created_at | TS | N | |

`effective_from` refers to when the rule applies to commands, not to posting dates. Both bounds apply to the posting date of the command.

## 5. Number series

### 5.1 `number_series` (configuration)

PK: (`tenant_id`, `company_id`, `series_id`)
Unique: (`tenant_id`, `company_id`, `series_code`)

| Column | Type | Null | Notes |
|---|---|---|---|
| tenant_id, company_id, series_id | ID | N | |
| series_code | CODE | N | e.g. `AR-INV`, `GL-JRN` |
| description | TEXT(100) | Y | |
| prefix | TEXT(10) | Y | Literal prefix in the formatted number |
| include_fiscal_year | BOOL | N | Insert the fiscal year after the prefix. **Must be true when `is_legal` is true** (config validation, `SERIES.LEGAL_REQUIRES_YEAR`, new code). The sequence restarts per fiscal year (1), so without the year in the number the legal `document_number` would repeat across years and the uniqueness of the legal number per series would be lost |
| separator | TEXT(1) | Y | Between the parts |
| number_length | SMALLINT | N | Zero-padded width of `sequence_no`. Larger values are not truncated, the number just gets longer |
| start_number | BIGINT | N | First number of each fiscal year. Default 1 |
| is_legal | BOOL | N | True for gapless legal series: the series of journal documents, and delivery and receipt series where the company flags them (11, Q15). False for non-legal series: orders, fulfilment documents and dropship confirmations (11), migration document types (13, Q11) |

**What `is_legal` changes mechanically (decided in 17, section 4):** (a) `include_fiscal_year` must be true, so `document_number` is unique per series across all years, not only per fiscal year. (b) The gapless check of 6.2 is an error for a legal series (`chains:verify` fails the chain) and is reported as an informational finding for a non-legal series. (c) `is_legal` cannot be changed once a number has been issued from the series, like the format fields. (d) The number is consumed only by a committed record in both cases (6.1), so the allocation is identical, the difference lies in the validation and verification. Other legal duties (retention, export) follow the document, not the series (12).

Format example: prefix `AR`, include year, separator `-`, length 6, sequence 123 in fiscal year 2026 gives `AR-2026-000123`.
Format fields and `start_number` can change only while no number has been issued from the series in any fiscal year (checked against `number_range_counter`). After that, the series is closed to format changes. A different format needs a new series (Q7).

### 5.2 `number_series_assignment` (configuration, insert only)

PK: (`tenant_id`, `company_id`, `assignment_id`)
Unique: (`tenant_id`, `company_id`, `document_type`, `ledger_key`, `effective_from`) where `ledger_key` is `ledger_id`, or a fixed marker when `ledger_id` is null.

| Column | Type | Null | Notes |
|---|---|---|---|
| tenant_id, company_id, assignment_id | ID | N | |
| document_type | CODE | N | As in `journal_document.document_type` (02, 3), system-generated types included: the stock and dropship types derived by inventory and order management and the migration types. Also the order and fulfilment document types of 11, which are numbered by series but are not journal documents |
| ledger_id | ID | Y | Null means all ledgers. A ledger-specific row wins over a null row (R1: one ledger, so unused) |
| series_id | ID | N | FK to `number_series`. Several document types may share a series |
| effective_from | DATE | N | **The `start_date` of a fiscal year** (A3, decided in 17, section 4), no longer a UTC instant. The assignment applies to the fiscal years whose `start_date` is on or after it. The config command requires the value to equal the `start_date` of a fiscal year of the company that is already opened (`SERIES.EFFECTIVE_FROM_INVALID`, new code). This fixes the earlier mismatch of a timestamp compared with a date |
| created_by, created_at | TEXT(100), TS | N | |

Resolution at posting (A3): determine the fiscal year of the **posting date** (`journal_document.fiscal_year`, 02, 3), not of the command time. For (company, document type, ledger), take the ledger-specific row if any, else the null-ledger row, in each case the one with the highest `effective_from` not later than the `start_date` of that fiscal year. No match rejects the posting with a structured error. A new assignment affects only fiscal years that start on or after its `effective_from`. A late posting into a still open old year therefore takes its number from the series of that old year, and the counter locked in 6.1 is the counter of that series and that fiscal year.

Continuity rule (Q7 decided, restated for DATE): if numbers have already been issued for the document type in a fiscal year (checked against `number_range_counter` of the series assigned for that year), a new assignment for that type must have `effective_from` later than the `start_date` of that fiscal year, so it applies from a later year. Otherwise the config command is rejected. One continuous sequence per document type per year is guaranteed.

Because `effective_from` of the posting window (4.1) is a command-time instant and that of the series assignment is a fiscal year start date, the two are deliberately different types.

## 6. `number_range_counter`

One row per series and fiscal year. The serialisation point for gapless numbering (ADR-007).
PK: (`tenant_id`, `company_id`, `series_id`, `fiscal_year`)
FK: (`tenant_id`, `company_id`, `series_id`) to `number_series`. (`tenant_id`, `company_id`, `fiscal_year`) to `fiscal_year`.

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| tenant_id, company_id, series_id, fiscal_year | ID, ID, ID, INT | N | L1 | |
| last_number | BIGINT | N | S | Starts at `start_number` minus 1. Updated only by the posting commands |
| updated_at | TS | N | S | |

Rows are created by `fiscal-years:open` for every series of the company, and by `number-series:create` for every fiscal year already open. A counter is never created by a posting, which removes the first-use race.

### 6.1 Allocation

In the posting transaction, after the validation of the document:
1. Resolve the series from the fiscal year of the posting date (5.2).
2. Lock the counter row of (series, fiscal year of the posting date), in the lock order of 6.3.
3. `sequence_no = last_number + 1`, update `last_number`, render `document_number` (5.1).
4. Insert the document. Commit.

A failure anywhere rolls back the counter update, so no number is lost. A number is consumed only by a committed document (P6).

### 6.2 Verification

`chains:verify` per series and fiscal year checks: `sequence_no` values form an unbroken run from `start_number` to `last_number`, `last_number` equals the highest `sequence_no` on a document, and `document_number` matches the format and the sequence.

### 6.3 Lock order (authoritative, D3, ADR-147)

This is the one lock order of the platform. It replaces the extension list of 03, 5.6 and the partial lists in 10, 11 and 15. Every command takes the locks it needs in this order, and sorted as stated within a class. No command waits for a lock of an earlier class while it holds a later one.

| # | Locks | Sort |
|---|---|---|
| 1 | Order rows (`sales_order`, `purchase_order`) of an order command | (company, order kind, id) (11, section 9) |
| 2 | `item_company` rows of the items touched by a stock command | (company, item) (10, 6.4) |
| 3 | `number_range_counter` row(s) | (company_id, series_id, fiscal_year). **Counters before heads** (X4) |
| 4 | `chain_head` rows (02, 9.2, K1, ADR-158) | The tuple (chain type, company_id, scope_key), with chain type in the order LEDGER, CLEARING, PERIOD_EVENT, VAT_RETURN, STOCK, FULFILMENT (X4: the one sort tuple of the platform, 06, 07, 10, 11 and 15 use it too) |
| 5 | `fiscal_year` and `fiscal_period` rows | By (company_id, fiscal_year, period_no). Status commands take them exclusively when they update the status, after the heads. Commands that depend on a status take them in **share** mode and re-read the status (A1, rules below) |
| 6 | `journal_line_open_item` rows (clearing, GR/IR and other open item commands) | By key (company, document, line), that is by id |
| 7 | `outbox_head` (K3, ADR-159, 15, 5.2) | One row per tenant. **Always last** |

Rules:
- **Derivation runs after the locks.** Balances, cleared amounts, tolerances, progress and other measures that depend on locked rows are computed only once the command holds its locks, so they are computed under the lock and so match the locked rows (D3). This holds for the rows the command locks only. It does not make a derived result such as a revaluation immune against later postings and clearings, which the stale check at hard-close covers (05, 3.7).
- **Serialisation of period close with posting, clearing and L2 change (A1, decided in 17, section 4).** `periods:hard-close` and `year-end:close` lock the **LEDGER and CLEARING heads of the company before the PERIOD_EVENT head** (class 4 sorted gives this), then take the `fiscal_period` and `fiscal_year` rows exclusively (class 5), then `outbox_head`. Period-dependent commands (posting, clearing, `clearings:reset`, `journal-documents:change-open-fields`, revaluation run, intercompany variants) may pre-read the status without a lock for an early rejection, but they **read the `fiscal_period` and `fiscal_year` rows in share mode after they hold their head locks** and re-check the status there. A command that has no head lock (the L2 change) takes the share lock on the `fiscal_period` row of the document and holds it until commit, so a concurrent hard-close waits for it and a change that starts after the hard-close commit sees HARD_CLOSED. Since a command holding a share lock on a period row never waits for a head afterwards (heads are class 4, periods class 5), no lock cycle arises.
- `year-end:close` posts the closing document and appends the period events in one transaction. It takes the number counter, then the LEDGER head, then the CLEARING head, then the PERIOD_EVENT head, then the VAT_RETURN, STOCK and FULFILMENT heads in share mode for the anchors (02, 9.5), then the period and year rows, which the sort order already gives. `revaluation:run` takes the LEDGER and CLEARING heads (05, 3.4, A5).
- `business_partner.retention_until` is not updated per posting (12, batch job), so no lock position exists for it and it is not a hot row.
- Intercompany commands (ADR-016) lock the rows of both companies, sorted as above (chain heads by chain type, then company).
- **Number counters lock before chain heads** (class 3 before class 4, X4), decided in 14, section 10 (D3) and 17, section 4. A command that may post a document locks the counter and the LEDGER head first, whatever it locks later.
- **Clearing (D3, 03, 5.6, decided in 14, section 11):** a clearing pre-locks the counter of the difference-document series and the LEDGER head, then the CLEARING head, then the open items, when its items do not net to zero in LC, GC and RC at full amounts (immutable L1 amounts) or are partially cleared. The condition is re-checked after the open-item locks. An unneeded pre-lock consumes no number.

## 7. Rules that use these tables

### 7.1 Period resolution and posting check

Given a posting command with a posting date, and optionally an explicit special period:
1. Posting date inside the company's posting window in force (4.1), else reject.
2. Find the `fiscal_period` that covers the date: regular period by date range, or the requested special period (then the date must lie in period 12's range). No period (year not opened) rejects the posting.
3. Year `status` must be OPEN (a closed year rejects).
4. Period status OPEN: allowed. SOFT_CLOSED: only with the authorisation `post.soft_closed` ("post to soft-closed period", 06, Q10), and then `soft_close_override` = true on the document. HARD_CLOSED: reject, except reversal-and-repost with a later posting date in an open period (ADR-022).
5. Intercompany: steps 1 to 4 for both companies, and the command rejects if either fails (ADR-016).
6. Steps 2 to 4 are run twice: once as a pre-read for an early rejection, and again after the command holds its head locks, reading `fiscal_year` and `fiscal_period` in share mode (A1, 6.3). Only the second read decides. The status is therefore stable until commit, and a hard-close cannot commit in between. The year-opened check in step 2 and the posting window check in step 1 need no second read.
Rejection codes of this section. **06 must contain exactly these codes** (06 is updated by another agent in the same pass, 17, section 5): posting date outside the window (`POSTING.OUTSIDE_WINDOW`), no period or year not opened (`PERIOD.NOT_FOUND`), closed year (`PERIOD.YEAR_CLOSED`), hard-closed period (`PERIOD.HARD_CLOSED`), soft-closed without authorisation (`PERIOD.SOFT_CLOSED_NOT_AUTHORISED`), no series assignment (`SERIES.NO_ASSIGNMENT`).

### 7.2 L2 change check

A `journal-documents:change-open-fields` command (L2 columns only) reads the period of the document's header (`fiscal_year`, `fiscal_period`). OPEN: allowed. SOFT_CLOSED: allowed only with the authorisation `change.soft_closed` (a separate permission from `post.soft_closed`, 06, Q10), recorded on the `change_log` row (period_status). HARD_CLOSED: reject (L3). The status is read in share mode on the `fiscal_period` row and held until commit (A1, 6.3). For the L2 columns of a clearing (`note`) the governing period is the clearing's own period (03, 4.1, 02, 8). S columns are not subject to this check (D2, ADR-146): clearing S changes are governed by the clearing date (03), status changes by the period commands.

### 7.3 Status commands

| Command | Allowed from | To | Conditions |
|---|---|---|---|
| `fiscal-years:open` | n/a | year OPEN, all periods OPEN | Dates continue the previous year without gap or overlap. May lie outside the posting window, which still controls posting (Q8 decided). Short first year only for the company's first year (Q6 decided). Creates counters. For the cutover year of a migration the field `history_until_period` is **not** set here: it is written by `migration-batches:open` (parameter `history_until_period`, the last history period, written to the cutover fiscal year; D8, 3.2, 13, 4.2 and 7). History years are opened by `fiscal-years:open-history`. Emits `year.opened` (new event, 15) |
| `fiscal-years:open-history` | n/a | history year OPEN (`is_history` = true), all periods OPEN | Only for an OPEN migration batch. Years before the cutover year, contiguous, up to `migration_max_history_years`. Same creation rules as `fiscal-years:open`. Permission `history.open` (D10, ADR-154, 06). History years are hard-closed and closed at the seal (13, section 9) |
| `periods:soft-close` | OPEN | SOFT_CLOSED | **Rejected for a history period before the seal** (year with `is_history`, or period of the cutover year at most `history_until_period`, 3.2) with `PERIOD.HISTORY_YEAR`: the seal closes history periods (13, 9.1). **Rejected for the closing period** with `PERIOD.CLOSING_PERIOD_MANAGED` (3.3) |
| `periods:hard-close` | OPEN, SOFT_CLOSED | HARD_CLOSED | **Locks (A1):** LEDGER and CLEARING heads of the company, then PERIOD_EVENT, then the `fiscal_period` row exclusively, `outbox_head` last (6.3). The stale check, the predecessor check and the status change run after these locks. **Anchor (A2):** writes the anchor requests (state PENDING) in the same transaction, the timestamp token is fetched after commit (02, 9.5). **Revaluation stale check** (D4, ADR-051, ADR-053, 05, 3.7): for every period whose date range contains a valuation date, that is a regular period and also a special period 13 and up (which shares the range of period 12), the recomputed revaluation must equal the ACTIVE run, else reject. Replaces the example "a revaluation whose auto-reversal is not yet posted" of ADR-022. The generic pending-system-job rule stays for later system jobs. Predecessor period (previous period of the same year, or the last period of the previous year, special periods in order after 12) must be HARD_CLOSED. The first period of a company has no predecessor (Q5 decided). **Special periods:** the predecessor of special period 13 is period 12, of 14 is 13, and so on (3.3). **Rejected for a history period before the seal** with `PERIOD.HISTORY_YEAR` (the seal closes history periods, 13, 9.1). **Rejected for the closing period** with `PERIOD.CLOSING_PERIOD_MANAGED` (3.3: only `year-end:close` hard-closes it) |
| `periods:reopen` | HARD_CLOSED to SOFT_CLOSED or OPEN. SOFT_CLOSED to OPEN | | No later period of the company hard-closed. Fiscal year not CLOSED. Reason code and text (ADR-023). Privileged authorisation. The payload reason code goes to `period_event.reason_code` (3.4). **Rejected for a sealed history period** (sealed year with `is_history`, or period of the cutover year at most `history_until_period` after the seal) with `PERIOD.HISTORY_YEAR_SEALED`. **Rejected for the closing period** with `PERIOD.CLOSING_PERIOD_MANAGED`: it is reopened only by `year-end:reopen`. A period with a later hard-closed period is rejected with `PERIOD.REOPEN_LATER_PERIOD_CLOSED` (new). Corrections of history use adjustment postings (14, section 11) |
| `year-end:close` | year OPEN | year CLOSED | **Locks (A1):** number counter of the YEAR_END series, LEDGER head, CLEARING head, PERIOD_EVENT head, the VAT_RETURN, STOCK and FULFILMENT heads in share mode for the anchors, the `fiscal_period` and `fiscal_year` rows exclusively, `outbox_head` last (6.3). All checks run after the locks. Preconditions: the year is OPEN (`CLOSE.YEAR_NOT_OPEN`, new). All regular periods HARD_CLOSED (`CLOSE.YEAR_NOT_ALL_CLOSED`). **Every non-closing special period HARD_CLOSED** (A4, decided in 17, section 4), else `CLOSE.SPECIAL_PERIOD_NOT_CLOSED` (new). The closing period (the highest special period, so `special_period_count` is at least 1, `CLOSE.CLOSING_PERIOD_MISSING`) exists and is OPEN, else `CLOSE.CLOSING_PERIOD_NOT_OPEN` (new). The stale check above runs for the closing period before the closing document is posted. **Anchor (A2):** the anchor requests for all six chain types are written in the transaction with state PENDING (02, 9.5), the tokens are fetched after commit, so a timestamp service outage never blocks the close. **Rejected on history years** (`is_history` = true) with `PERIOD.HISTORY_YEAR` (14, section 12): the closing entries of a history year are posted by the HISTORY chunk and closed by the migration variant below. Posts the closing document into the closing period and hard-closes it. Appends **PERIOD_HARD_CLOSE for the closing period and YEAR_END_CLOSE for the year** (D4), so the status trigger of section 8 finds the event that matches the period status change. No opening balances are created (05-revaluation-and-year-end, amends ADR-024) |
| `year-end:reopen` | year CLOSED | year OPEN | **Locks (A1):** as `year-end:close`. No later fiscal year of the company is CLOSED (`CLOSE.REOPEN_LATER_YEAR_CLOSED`, new). The following year has no posting, except the automatic reversal documents of the closed year's last revaluation (`CLOSE.REOPEN_BLOCKED_BY_POSTINGS`). **Order of steps (A4, decided in 17, section 4), all in one transaction:** (1) reopen the closing period HARD_CLOSED to OPEN and append PERIOD_REOPEN, (2) set the year to OPEN and append YEAR_END_REOPEN (`document_id` = the closing document being reversed), (3) post the mirror of the closing document (a mirror document of type YEAR_END, D1, `reversal_reason_code` `REOPEN`), which now lands in an OPEN period of an OPEN year. The payload reason code and text go to the two events (3.4). Privileged and audited (ADR-024, 05). **Rejected on a sealed history year** with `PERIOD.HISTORY_YEAR_SEALED`. Emits `year.reopened`, `period.status_changed` and `ledger.document_reversed` |

**Migration variant of year close (14, sections 10 and 11, 13, 9.1).** `migration-batches:seal` closes the history periods and history years by status: it writes PERIOD_HARD_CLOSE for each history period (periods of years with `is_history`, and periods of the cutover year up to `history_until_period`, 3.2) and, per history year, PERIOD_HARD_CLOSE for the closing period plus YEAR_END_CLOSE (`period_no` null). It is internal to the seal and not a callable command, posts no document and uses the closing entries of the HISTORY chunk. **X1 (decided in 17, section 4):** the closing entry of a history year is the `CLOSING` document (02, 3). The YEAR_END_CLOSE event of that year carries it as `document_id`, and `fiscal_year.year_end_document_id` is set to it. If a history year has no closing lines, `year_end_document_id` stays null. The cutover year itself is not closed by the seal, only its history periods are hard-closed. It **skips the revaluation stale check of D4** (no revaluation runs in history years). After the seal the history years and their periods cannot be reopened (`periods:reopen`, `year-end:reopen`).

Every command appends a `period_event`, updates the S columns and requires an idempotency key. In the same transaction it writes its outbox events (`period.status_changed`, `year.opened` for `fiscal-years:open` and `fiscal-years:open-history` (new event, 15), `year.closed`, `year.reopened`, 15, section 6) and takes `outbox_head` last (6.3, K3, ADR-159).

## 8. DB protection

| Table | App role grants |
|---|---|
| `fiscal_calendar`, `number_series` | Config API role: CRUD. No DELETE once referenced. Format changes blocked once numbers exist (5.1) |
| `posting_window`, `number_series_assignment` | INSERT only |
| `fiscal_year`, `fiscal_period` | INSERT by `fiscal-years:open`. UPDATE of `status`, `year_end_document_id`, `last_event_id` by the period command role only. No DELETE |
| `period_event` | INSERT only |
| `chain_head` (PERIOD_EVENT row) | UPDATE by the period command role only (02, section 10) |
| `number_range_counter` | INSERT by year-open and series-create. UPDATE of `last_number`, `updated_at` by the posting command role only. No DELETE |

Trigger backstop (S columns allowed in any period status, D2, ADR-146, one S rule for all S tables in 02, section 10): reject an UPDATE of any L1 column of `fiscal_year` and `fiscal_period`. Reject any UPDATE of the S columns `status`, `last_event_id` (both tables) and `year_end_document_id` (`fiscal_year`) without a matching `period_event` row in the same transaction, whose id equals the new `last_event_id` and whose `to_status` equals the new status (for every change, including the hard-close of the closing period by `year-end:close`, which writes a PERIOD_HARD_CLOSE event, and including a history year, where `year_end_document_id` is the CLOSING document of the YEAR_END_CLOSE event). The owning event of `number_range_counter.last_number` is the record that consumes the number (a `journal_document` row, or the order or fulfilment record of 11): reject any update that is not exactly +1 per such record in the transaction (or +n for n records of one series, such as intercompany). The `chain_head` PERIOD_EVENT row is updated only together with a `period_event` row, `last_seq` +1.

## 9. Resolved by this spec, and assumptions

| Ref | Item |
|---|---|
| A1 | Period status is a stored S cache of `period_event`, verified by `chains:verify` (ADR-041) |
| A2 | The fiscal year label rule is a calendar setting. Years are 12 calendar months, except a short first year (Q6) |
| A3 | Special periods share the date range of period 12 and are selected only by explicit request. Adjustment postings carry a posting date inside that range |
| A4 | Number ordering follows commit order, as accepted in ADR-025. The `sequence_no` run is gapless, the posting date order is not guaranteed |
| A5 | The authorisation model (who holds which authorisation) is outside the core's tables. The core receives the identity's permissions in the command context and records the authorisation used on the `period_event`, the `change_log` row (`change.soft_closed`), or the document (`soft_close_override`, from `post.soft_closed`). Two soft-close permissions exist (Q2, 06, Q10) |
| A6 | Applied to 02: `journal_document` gets `sequence_no` and `soft_close_override`, and `fiscal_period` may be a special period with an FK to `fiscal_period` |

## 10. Open questions

| # | Question |
|---|---|
| Q1 | **Decided:** `period_event` is hash-chained (3.5), with its own chain head per company, anchored through `chain_anchor`. |
| Q2 | **Decided, later amended by 06-Q10:** `post.soft_closed` recorded as `soft_close_override` on the document, and a separate `change.soft_closed` for L2 changes, recorded on the change log. No approver identity in the core (four-eyes belongs to apps). |
| Q3 | **Decided:** the posting date of a special-period document lies inside period 12's date range (A3). |
| Q4 | **Decided:** numeric sequence with optional prefix, fiscal year, separator and zero padding. Alphanumeric or check characters only if a target country requires them (P15 per-country review). |
| Q5 | **Decided:** hard-close requires the predecessor period to be HARD_CLOSED (7.3). |
| Q6 | **Decided:** short first year allowed (periods before the start month are not created, numbering stays consistent, `first_period_no` on the year). Extended first year (13 to 18 months) not supported in R1. |
| Q7 | **Decided:** a new series assignment for a type with issued numbers takes effect only after the end of that fiscal year (5.2). |
| Q8 | **Decided:** `fiscal-years:open` may create years beyond the posting window. The window stays the control for posting. |
| Q9 | **Decided:** the next domain is revaluation and year-end runs, which closes the "pending system job" dependency at hard-close. |

## 11. ADRs raised by this spec (now in 01-adr-log, which is authoritative)

**ADR-041: Period status as stored cache of an append-only event log.** `fiscal_period.status` and `fiscal_year.status` are S columns, written together with a `period_event` row, recomputed and verified. Refines ADR-022 and ADR-023.

**ADR-042: Fiscal calendar structure.** Any first month, 12 regular periods, up to four special periods sharing period 12's dates, selected explicitly. No free period definitions in R1.

**ADR-043: Explicit fiscal year opening.** `fiscal-years:open` creates year, periods and number counters in one audited command. Postings never create them. Refines ADR-022 and ADR-025.

**ADR-044: Number series by assignment and counter per series and year.** Config assignment table by document type (and ledger), effective-dated and insert only. Counter rows are created at year open and are the only lock point. Documents store numeric `sequence_no` and the formatted `document_number`. Refines ADR-007.

**ADR-045: Soft-close override recorded on the document.** `soft_close_override` is an L1 column on `journal_document`. Refines ADR-022.

## 12. Re-baseline of 2026-10-13: changes in this spec and deltas toward other specs

| Ref | Change in 04 |
|---|---|
| D3, ADR-147 | Section 6.3 is the one authoritative lock order: orders, `item_company`, number counters, `chain_head` rows by chain type then scope, open items by id, `outbox_head` last. Derivation after the locks. `business_partner.retention_until` has no lock position (batch job) |
| Third pass (14, section 11) | Clearing pre-lock condition stated as decided (6.3). `year-end:close` rejected on `is_history` years with `PERIOD.HISTORY_YEAR`, the migration variant of year close skips the D4 stale check, sealed history years cannot be reopened (`year-end:reopen`, `periods:reopen`) (3.2, 7.3). Mirror types: no change here, the `year-end:reopen` mirror is a YEAR_END document as in 02, 3 |
| Second pass (14, section 10) | D3: counters before chain heads is decided, clearing pre-lock rule added (6.3). D4 confirmed: the stale check runs at the hard-close of every relevant period (7.3), `year-end:close` writes PERIOD_HARD_CLOSE for the closing period (`period_no` set) plus YEAR_END_CLOSE with `period_no` null (3.4, 7.3). Open points 1 to 3 below resolved |
| D4, ADR-148 | Hard-close runs the revaluation stale check (ADR-051, ADR-053, 05, 3.7) for regular periods and for special periods 13 and up. The old wording "no pending system job, e.g. a revaluation whose auto-reversal is not yet posted" is replaced (7.3). `year-end:close` appends PERIOD_HARD_CLOSE for the closing period and YEAR_END_CLOSE (3.4, 7.3). `year-end:reopen` gets the condition "no later fiscal year CLOSED" (7.3) |
| D10, ADR-154 | New row for `fiscal-years:open-history` with permission `history.open` (7.3) |
| K1, ADR-158 | `period_event_chain_head` removed, row of `chain_head` with chain type PERIOD_EVENT (2, 3.5, 8) |
| K3, ADR-159 | Status commands write outbox events, `outbox_head` last (7.3, 6.3) |
| 15, section 10 | Applied: 6.3 (`outbox_head` last, chain heads sorted by chain type then scope), outbox events of status commands |
| 16, section 9 | No delta toward 04 |
| 14, section 3 | `is_legal` text corrected for non-legal series (5.1). Series assignment document types cover stock, dropship, migration and order types (5.2). `fiscal-years:open-history` row (7.3). `year-end:reopen` condition (7.3). Soft-close authorisations named `post.soft_closed` and `change.soft_closed` in 7.1, 7.2 and A5 |
| 14, section 4 | Chain types: the six are listed in 6.3 and 02, 9.2. Command names normalised (`fiscal-years:open`, `periods:*`, `chains:verify`) |
| Fix pass 4 (17, section 4, v0.5) | **A1:** hard-close and `year-end:close` lock LEDGER and CLEARING heads before PERIOD_EVENT, period rows as lock class 5, share-mode re-check after head locks (6.3, 7.1, 7.2, 7.3). **A2:** anchor request PENDING in the close transaction (7.3). **A3:** series resolved by the fiscal year of the posting date, `number_series_assignment.effective_from` is a DATE (fiscal year start), continuity rule restated (5.2, 6.1). **A4:** `year-end:close` requires every non-closing special period HARD_CLOSED, `year-end:reopen` order period, year, mirror (7.3). **X1:** `year_end_document_id` of a history year is the CLOSING document (3.2, 7.3). **X4:** lock tuple and counters before heads (6.3). **D8:** `history_until_period` on the cutover year, history guards (3.2, 7.3). **Legal series:** `include_fiscal_year` required when `is_legal`, mechanical effects stated (5.1). **Special and closing period rules:** predecessor rule for special periods, manual status commands on the closing period forbidden (3.3, 7.3). **S trigger rule:** owning events of period, year and counter columns (8). **Types:** period numbers are INT (3.2, 3.3, 3.4). **Reason vocabularies:** reopen payload code goes to `period_event` (3.4). **Events:** `year.opened` (7.3). New error codes for 06: `PERIOD.CLOSING_PERIOD_MANAGED`, `PERIOD.REOPEN_LATER_PERIOD_CLOSED`, `CLOSE.YEAR_NOT_OPEN`, `CLOSE.SPECIAL_PERIOD_NOT_CLOSED`, `CLOSE.CLOSING_PERIOD_NOT_OPEN`, `CLOSE.REOPEN_LATER_YEAR_CLOSED`, `SERIES.LEGAL_REQUIRES_YEAR`, `SERIES.EFFECTIVE_FROM_INVALID`, and the posting rejections of 7.1 (`POSTING.OUTSIDE_WINDOW`, `PERIOD.NOT_FOUND`, `PERIOD.YEAR_CLOSED`, `PERIOD.HARD_CLOSED`, `PERIOD.SOFT_CLOSED_NOT_AUTHORISED`, `SERIES.NO_ASSIGNMENT`), which 06 must contain (no longer conditional, propagation pass). New setting for 07: `anchor_pending_deadline` (02, 9.5). New parameter for 13/06: cutover year opening sets `history_until_period` (noted in 3.2) |
| Propagation pass (17, section 5, v0.6) | Contiguity rule of 3.2 gets the history-year variant (a history year may be opened before an existing cutover year). `history_until_period` parameter note (3.2, 7.3). Posting-rule codes of 7.1 are codes 06 must contain. References to lock classes in 6.3 and its deltas checked: classes 3 (counters), 4 (chain heads) and 5 (period and year rows) match the table of 6.3, no change needed |
| Propagation pass 2 (v0.6) | `history_until_period` is written by `migration-batches:open` (parameter `history_until_period`, value is the last history period, written to the cutover fiscal year), not by `fiscal-years:open` (7.3 row corrected). `fiscal-years:open-history` stays the command that opens history years. No other change |
| Deltas toward other specs | 02: `chain_head` defined in 9.2. 10, 11: their lock order text (10, 6.4 and 11, section 9) must refer to 6.3 here. 13: `fiscal-years:open-history` permission is `history.open` (D10), not `migration.manage` |

## 13. Open points

| # | Point |
|---|---|
| 1 | **Resolved (14, section 10, D3):** number counters lock before chain heads. Clearing pre-locks counter and LEDGER head when a difference can arise (6.3, 03, 5.6) |
| 2 | **Resolved (14, section 10, D4):** the stale check runs at the hard-close of every relevant period, including special periods 13 and up. A stale result is fixed by `periods:reopen` of period 12 and a rerun |
| 3 | **Resolved (14, section 10, D4):** `year-end:close` writes PERIOD_HARD_CLOSE for the closing period plus YEAR_END_CLOSE at year level with `period_no` null |
| 4 | **Resolved (14, section 12):** two codes, entered in 06: `PERIOD.HISTORY_YEAR` (close commands on history periods before seal) and `PERIOD.HISTORY_YEAR_SEALED` (reopen of sealed history years) |
| 5 | **Resolved (14, section 12):** `periods:soft-close`, `periods:hard-close` and `year-end:close` are rejected on history periods before the seal with `PERIOD.HISTORY_YEAR` |
