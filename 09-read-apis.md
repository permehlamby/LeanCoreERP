# Finance Core: Read and Query APIs

Status: DRAFT v0.6 for review (propagation pass of 2026-10-13, 17 sections 4 and 5, see 9.3; v0.5: fix pass 4 of 2026-10-13, decisions of 17, section 4: app_code in the envelope, `read.ext` removed, A6, E5, E6, missing read resources, as-of semantics of supply and demand; see 9.2). Builds on 00-design-principles v0.4, ADR-001 to ADR-174 and specs 02 to 08. Revised on 2026-10-13 for the scope review (14, sections 8 and 9) and the platform specs 15 and 16: D7 (read log, ADR-151), K1 (`chain_head`, ADR-158), K3 (audit events from the outbox, ADR-159), extension reads (15, ADR-137), change feed reads (15, ADR-138), supply and demand reads (16, ADR-140, ADR-144), new read permissions and resources. Second re-baseline (14, section 10): new outbox event types in the 4.8 mapping, field groups approved, VAT_RETURN chain one per company in 4.9, Q13 and Q14 closed. Third re-baseline (14, section 11): 4.8 maps `vat.settlement_reversed` (VAT_SETTLEMENT_REVERSED) and `retention.recomputed` (RETENTION_RECOMPUTED), both revaluation event types confirmed as REVALUATION_RUN, `invoice.reversed` named. Deltas in section 9.
Scope: the read side of the core: common read conventions (envelope, consistency, filtering, paging, authorisation), get and list for every entity, the named report operations (balances, trial balance, account ledger, open items, aging, partner balances, document drill-down, audit events, VAT positions, chain status, supply and demand, events, calendar helpers), personal-data read logging, and the indexes the reads need.
Out of scope: financial statement layouts (income statement, balance sheet) and every other report front-end (principles, section 4), bulk and archive exports (compliance domain), SAF-T and e-invoice data sets (compliance domain), full-text search engines, caching layers, physical replica topology.
Conventions as in 02, section 1, and the envelope, error model and permission mechanism of 06. Write contracts are in 06, configuration CRUD in 07.

## 1. Decisions taken

| Topic | Decision |
|---|---|
| Shape | Generic `get` and `list` on every entity (typed filters, sorting, cursor paging), plus a small set of named report operations for logic that clients cannot do reliably: balances, trial balance, account ledger, open items, aging, partner balances, document drill-down, audit events |
| History | Reports use current L2 values. The change history is queryable. Hard-closed periods are reproducible exactly, because L3 locks their L2 fields |
| Aggregation | One flexible balance query with group-by and filters in all four currencies, and an aging operation. Statement layout stays in apps |
| Read audit | Reads that return personal data are logged. Ordinary ledger reads are not |

## 2. Common conventions

### 2.1 Envelope

Request: `context` (`correlation_id` Req, `actor` Req, `permissions` Req, `app_code` (Req for `events:list` and `events:head`, Opt for all other reads: the claim of the identity provider when the caller is a registered app, 15, 3.1, F10), `company_id` Req for company-scoped reads, `purpose_code` Opt for personal-data reads, Q5), and a command-specific `query`. There is no idempotency key and no `mode`, reads have no side effects (except the read access log, 5.2, which is not an outbox event and takes no `outbox_head` lock, A6). A missing or inactive `app_code` where it is required: `APP.NOT_REGISTERED` or `APP.INACTIVE` (15, 4.3).

Response:

| Field | Notes |
|---|---|
| status | `OK` or `REJECTED` (06, 2.2). Errors use area `READ` and the error object of 06, 2.3 |
| result | Operation-specific |
| snapshot | `{taken_at, ledger_seq}`: the time and, per ledger of the company, the highest `chain_seq` the response is consistent with (2.2). Reads of orders, reservations and supply and demand also carry `outbox_position`: the highest outbox position the response is consistent with (2.2) |
| page | `{next_cursor, limit}` for list operations. `next_cursor` is null on the last page |

### 2.2 Consistency

- **One snapshot per request.** The whole response is read from one consistent snapshot of the database. Totals and rows of the same response always agree.
- **Ledger position.** Documents are immutable (L1) and appended in chain order, so `ledger_seq` identifies exactly which postings a response contains. A caller that just posted can require visibility with `min_ledger_seq`: if the serving database is behind, the read is rejected with `READ.STALE_SNAPSHOT` (retryable) instead of returning incomplete data. This lets reads run on replicas without breaking read-your-writes (Q2). The physical topology is not specified here.
- **Outbox position as consistency token (orders, reservations, supply and demand).** Orders, order lines and reservations change in place (L2 and S data) and keep no time series, so they have **no time-based `as_of`**. Their consistency token is the outbox position: the response carries `snapshot.outbox_position`, and a caller that needs to see its own change passes `min_outbox_position` (the `position` of the event it wrote or received), answered with `READ.STALE_SNAPSHOT` (retryable) if the serving database is behind. A caller cannot ask for the state of an order at an earlier time. Change history of orders is read from `order_change_log` and the order events (11).
- **Cursor stability.** For lists ordered by posting order, the first page fixes a `ledger_seq` cap and every later page repeats it. New postings do not appear between pages.
- **As-of dates, not as-of times.** Reports take a business date (`as_of_date`). L1 data is immutable, so a date-based report over L1 data is reproducible. L2 fields are read as they are now (Q decided: current state plus change history). Each report response carries `l2_stable`: true if every fiscal period touched is HARD_CLOSED (L3 makes the result exactly reproducible), false otherwise.

### 2.3 Authorisation and visibility

| Permission | Allows |
|---|---|
| `read.ledger` | Documents, lines, open items, clearings, balances, aging, VAT returns and positions, periods, fiscal calendars, posting windows, number series and their assignments (company access applies) |
| `read.inventory` | Stock reads of 10, section 8 (`stock-documents`, `stock-ledgers:query`, `stock-balances:query`, `stock-valuations:query`, `lot-traces:query`, `availability:query`), reservations, and with `read.orders` `supply-demand:query` (16, 4) |
| `read.orders` | Order and fulfilment reads of 11, section 8 (orders, deliveries, receipts, returns, dropship confirmations, invoice link records, `open-orders:query`, `uninvoiced:query`), and with `read.ledger` `credit-exposures:query` |
| `read.config` | Configuration and non-personal master data (accounts, dimensions, tax, partners without personal data, terms, item-partner terms, business calendars) and the calendar helpers `calendar:add-working-days`, `calendar:working-days-between` (16, 3.3) |
| `read.events` | `events:list`, `events:head` for the event types the calling app is subscribed to, except the sensitive families that need `read.audit` (2.3a, 15, 5.3) |
| `read.pii` | Personal-data fields of partners (identity, address, contact), partner bank accounts, VAT ids of persons. Every such read is logged (5.2) |
| `read.legal_snapshot` | The legal snapshot of a document the caller can already read (name, address, VAT id as printed on the invoice). Narrower than `read.pii`: it gives no access to the partner master. Every such read is logged (5.2, Q10 decided) |
| `read.audit` | Audit events (outbox events with `is_audit`, 4.8), change logs, master change log, command log, applied config packages, `chains:status`, anchors, migration batches and their control totals, reconciliations and commit chunks (13), `read_access_log`, export jobs, runs, holds and requests (12), `outbox:verify` (15, 7), and the sensitive event families in the app feed (2.3a). Running a verification (`chains:verify`) is a command with its own permission `chain.verify` (06), not a read |
| `read.cross_company` | Reads across several companies of the tenant in one request (balances, aging). The caller must have company access to each company |

There is **no separate `read.ext` permission** (F9, 17 section 4). Extension values and definitions (15, 4.4) are readable with the `read.*` permission of the entity they extend (`read.config` for definitions). Values of `is_pii` definitions also need `read.pii` (5.1).

Rules:
- Company access is enforced in the data layer (P7 and 06). A row of a company the caller cannot access never appears and is indistinguishable from a missing row (`READ.NOT_FOUND`).
- Personal-data fields are **omitted** (not masked) when the caller lacks `read.pii`, and the response says `pii_omitted` = true. A `get` of a partner without `read.pii` returns the `business_partner` fields only.
- Reads of inactive rows: lists default to `active` = true, a filter `include_inactive` shows all.

### 2.3a Feed visibility versus audit class (E5)

Two separate attributes of an outbox event (15, 3.7, 7):

- **`is_audit`** governs **retention only**: audit events are kept under the record class `AUDIT_LOG` (12, 4.6) and form the stream of `audit-events:query` (4.8). It does not decide who may read an event in the app feed.
- **Feed visibility** is decided by the event family. `events:list` and `events:head` (4.12) return the event types the calling app is subscribed to (15, 3.3), including audit events of ordinary business families (ledger, clearing, period, VAT, orders, master data and so on), with `read.events`. The families `access.*` (`access.summary`), the compliance family (`retention.*`, `partner.anonymised`, `partner.restricted`, `dsr.*`, `legal_hold.changed`, `export.*`, `deletion.*`) and the app events (`app.*`) are returned only to a caller that also has `read.audit`, whatever the subscription says. Without it they are silently left out of `events:list` and a subscription to them is rejected (`EVENT.NOT_SUBSCRIBED`, 15, 3.3).

### 2.4 Filtering, sorting, paging

| Element | Contract |
|---|---|
| `filter` | Per resource a fixed list of filterable fields (section 3). Operators: `eq`, `in`, `range` (from, to, inclusive), `prefix` (text), `contains` (text, on allowlisted fields only, 2.4.1), `is_null`. All filters are ANDed. Unknown fields: `READ.INVALID_FILTER` |
| `sort` | A list of allowlisted fields with direction. The key is always appended as a tie-break, so ordering is total and deterministic. Others: `READ.SORT_NOT_ALLOWED` |
| `page.limit` | Default 100, maximum 500. More: `READ.LIMIT_EXCEEDED` |
| `page.cursor` | Opaque keyset cursor. Invalid or from another query: `READ.CURSOR_INVALID` |
| `count` | `exact` or none (default none). Exact counts can be expensive and are limited to resources with a count-friendly filter |
| `include` | Named related records to embed (e.g. `lines`, `tax`, `open_item`). Only documented includes. `extensions` on every get and list of an extendable entity (2.4.2) |
| `fields` | Optional projection to a subset of top-level fields |

Amounts are returned as decimal strings with the currency's scale, never floating point. Dates are ISO. Every money field has an explicit currency. The four-currency block used everywhere:

`amounts`: `{ tc: {amount, currency}, lc: {amount, currency}, gc: {amount, currency}, rc: {amount, currency} | null }`.

### 2.4.1 The `contains` operator (Q1 decided)

A logical operator for substring search, defined independently of how a platform implements it (ADR-001). Semantics:

- The value is matched as a literal substring, never as a pattern: no wildcards, no escape characters. Minimum 3 characters (`READ.CONTAINS_TOO_SHORT`).
- Case-insensitive and accent-insensitive. Both sides are normalised (Unicode canonical decomposition, accents removed, case folded).
- Allowlisted fields only:

| Resource | Fields |
|---|---|
| `journal-documents` | `header_text`, `external_reference`, `source_document_ref` |
| `journal-lines` | `line_text` |
| `business-partners:search` | `legal_name`, `trade_name`, address fields, `email` (personal data: needs `read.pii`, logged) |
| Config and master resources | `name`, `description` |

- **Guard rails.** A `contains` filter on a ledger resource (`journal-documents`, `journal-lines`) is accepted only together with a company and one of: a date range of at most 12 months (`posting_date` or `document_date`), an `account_id`, a `partner_id` or a document number range. Otherwise `READ.CONTAINS_REQUIRES_SCOPE`. This prevents full-table scans of the ledger by an unbounded substring search. The limit is a tenant setting.
- **Platform mapping.** The logical contract is met by a normalised search column and an index per field. Decided in 20, section 8 (ADR-214): the core computes the normalised column in application code (Unicode NFKD, combining marks removed, case-folded, whitespace collapsed) and the reference platform indexes it with a trigram index. If a platform cannot support it, `contains` is not enabled for that field and `READ.INVALID_FILTER` is returned.
- **Personal data.** A normalised search column of a personal-data field is itself personal data. The retention and anonymisation job (P8) must clear it together with the source column, and the compliance spec must list it. Search columns are never part of any hash.

### 2.4.2 Extension attributes in reads (15, 4.4, ADR-137)

Every get and list of an extendable entity (the types of 15, 4.1: master data, orders and order lines, open items) accepts `include=extensions`. It embeds the extension values of all namespaces the caller may read as a flat map `namespace.attr` to value. Values of `is_pii` definitions are included only with `read.pii` and are read-logged (5.1, 5.2). Journal documents, stock documents, fulfilment documents and clearings are not extendable and do not accept the include.

Lists of extendable entities accept filters `ext.<namespace>.<attr>` on definitions of the entity type. Operators as given in 15, 4.4: `=`, `!=`, `<`, `<=`, `>`, `>=`, `in`, `is_null`, and for TEXT `contains` with the scope guard and minimum length of 2.4.1 (for ledger-scale resources a `contains` on an extension needs the same scope as the other `contains` filters). An unknown namespace or attribute: `READ.INVALID_FILTER`. Extension filters are ANDed with all other filters, are not sort keys, and never change what a core rule sees: no core read model, balance or posting uses an extension value.

`extension-definitions:list` returns the definitions of an entity type (namespace, key, data type, limits, allowed values, `is_pii`, `active`). Filters: `entity_type`, `namespace`, `attr_key`, `active`. Permission `read.config` (extension definitions follow it, 2.3). Sort **key**.

### 2.5 Limits and errors

Error area `READ`: `NOT_FOUND`, `INVALID_FILTER`, `SORT_NOT_ALLOWED`, `LIMIT_EXCEEDED`, `CURSOR_INVALID`, `RESULT_TOO_LARGE` (a report would return more than 10,000 groups), `STALE_SNAPSHOT`, `TIMEOUT`, `CONTAINS_TOO_SHORT`, `CONTAINS_REQUIRES_SCOPE`, `PARTNER_RESTRICTED` (personal data of a restricted partner read without a statutory purpose, 12, 5.3). A report that is too large must be narrowed by filters. Bulk extraction is not a read-API job (compliance domain, Q7).

## 3. Get and list

`{resource}:get` (by key) and `{resource}:list` (filters above). Defaults for sort are given in bold.

| Resource | Filterable fields | Sort | Includes |
|---|---|---|---|
| `journal-documents` | `document_type`, `document_number` (eq, prefix), `fiscal_year`, `fiscal_period`, `posting_date` (range), `document_date` (range), `status`, `source_system`, `source_document_ref` (eq, prefix), `external_reference` (eq, prefix), `posted_by`, `correlation_id`, `ic_transaction_id`, `partner_id` (via legal snapshot or open item), `is_system_generated`, `migration_batch_id` (eq, `is_null`: finds the documents committed by a migration batch, 13) | **`chain_seq`**, `posting_date`, `document_number` | `lines`, `tax`, `open_items`, `dimensions`, `legal_snapshot` (needs `read.legal_snapshot`) |
| `journal-lines` | `document_id`, `account_id`, `line_type`, `posting_date` (range), `fiscal_year`, `fiscal_period`, `cost_center_id`, `project_id`, `profit_center_id`, `business_unit_id`, free dimension (type and value), `tax_code_id`, `partner_id` (open items) | **`posting_date`, `document_id`, `line_no`** | `document`, `open_item`, `tax` |
| `open-items` | `account_id`, `partner_id`, `clearing_status`, `due_date` (range), `currency`, `posting_date` (range), `payment_block` | **`due_date`**, `posting_date` | `line`, `clearings`, `extensions` (2.4.2) |
| `clearings` | `account_id`, `partner_id`, `clearing_type`, `clearing_date` (range), `document_id`, `matching_ref` | **`chain_seq`**, `clearing_date` | `items` |
| `invoice-issue-records` (12, 11). `read.ledger` | `invoice_kind`, `invoice_id`, `document_id`, `delivery_channel`, `issued_at` (range), `document_sha256` (eq) | **`issued_at`**, `issue_id` | |
| `revaluation-runs` | `fiscal_year`, `fiscal_period`, `status` | **`run_at`** | `items`, `rates` |
| `fiscal-years`, `fiscal-periods`, `period-events` | `fiscal_year`, `period_no`, `status`, `event_type` | **key** | |
| `number-series`, `number-counters` | `series_code`, `fiscal_year` | **key** | |
| `number-series-assignments` (04, 5.2) | `document_type`, `series_code`, `effective_from` (range), `as_of` | **`effective_from`** | |
| `fiscal-calendars` (04, 3.1) | `code`, `active`, `as_of` | **`code`** | |
| `posting-windows` (04, 4.1) | `effective_from` (range), `as_of` | **`effective_from`** | |
| `anchors` (02, 9.5). Needs `read.audit` | `company_id`, `chain_type`, `scope_key`, `chain_seq` (range), `anchored_at` (range), state (PENDING, STAMPED once 04 and 02 carry A2) | **`anchored_at`** | |
| `migration-batches` (13, 3.1). Needs `read.audit` | `company_id`, `status`, `cutover_date`, `source_system`, `opened_at` (range) | **`opened_at`** | `control_totals`, `reconciliations`, `commit_chunks` |
| `migration-control-totals` (13, 3.2), `migration-reconciliations` (13, 3.4), `migration-commit-chunks` (13, 3.6). Need `read.audit` | `batch_id` (Req). Control totals: `control_kind`. Reconciliations: `reconciliation_id`, `check_code`, `result`. Commit chunks: the status field of the chunk table | **key** | |
| `vat-returns` | `registration_id`, `return_type`, `fiscal_year`, `vat_period_no`, `return_kind`, filed or settled state | **`chain_seq`** | `lines`, `tax_lines` |
| `change-log` | `document_id`, `line_no`, `field_name`, `changed_by`, `changed_at` (range), `correlation_id` | **`changed_at`** | |
| `master-change-log` (07, 10.1) | `entity_type`, `entity_id`, `action`, `changed_by`, `changed_at` (range), `correlation_id`, `change_set_id`, `is_pii`. Values of `is_pii` rows are always null | **`changed_at`** | |
| `command-log` (06, 4.1) | `command_name`, `actor`, `executed_at` (range), `correlation_id` (eq), `company_id`, `idempotency_key` (eq) | **`executed_at`**, `command_log_id` | `results` (`command_log_result`) |
| `applied-config-package` (07, 10.2) | `company_id`, `package_code`, `package_version`, `applied_by`, `applied_at` (range) | **`applied_at`** | |
| Config and master resources of 07 | As in 07, 11.1: key, `code`, `active`, validity, `as_of` for versioned tables. Includes `item-partners` (16, 3.1: filters `item_id`, `partner_id`, `relation`, `is_preferred`, `as_of`) `business-calendars` (16, 3.2), `vat-deduction-keys` and `vat-deduction-rates` (07, 8.6: filters `registration_id`, `key_code`, `kind`, `rate_status`, `as_of`) | **`code`** | `extensions` for extendable entities (2.4.2) |
| `extension-definitions` | `entity_type`, `namespace`, `attr_key`, `active` (2.4.2) | **key** | |
| Order, fulfilment and stock resources of 10 and 11 | As in 10, section 8 and 11, section 8, each with the filter `migration_batch_id` (eq, `is_null`) where the table has the column (orders, deliveries, receipts, dropship confirmations, journal documents) | As in 10 and 11 | `extensions` for orders and order lines (2.4.2) |
| `business-partners` | `partner_code` (eq, prefix), `partner_kind`, `is_customer`, `is_supplier`, `status`, `ic_company_id`. Name search: 5.3 | **`partner_code`** | `company_relations`, `tax_registrations`, `pii` (needs `read.pii`), `extensions` |

The include `tax` of `journal-lines` and `journal-documents` returns the tax detail with the deduction columns (`deductible_pct`, `deduction_key`, `deduction_key_rate`, `deduction_share`, `gross_tax_amount_tc`) and the allocation rows of `journal_line_tax_base` (`nondeductible_tax_tc`, `nondeductible_line_no`, 02, 7.1 and 7.2, ADR-204), and the derived EU flags (`origin_eu`, `destination_eu`, ADR-200). The line type `TAX_NONDEDUCTIBLE` is a value of the `line_type` filter and of the `line_type` grouping of the report operations (4.1 and 4.3).

Document-level reads always show current L2 values next to L1 values and the S fields (status, reversed-by). Reversed documents stay in lists with `status` = `reversed`.

**Issue state (22, delta 6).** The lists `journal-documents` (document types `AR_INVOICE`, `AR_CREDIT`, `AP_INVOICE`, `AP_CREDIT`), `sales-invoices` and `purchase-invoices` accept the filter `issue_state`: `ISSUED` (at least one `invoice_issue_record` exists for the link record or the document) or `NOT_ISSUED` (none). It is computed from `invoice_issue_record` through the indexes of 12, 11, and is not a stored column. An app that wants posted and not issued invoices combines `issue_state` = `NOT_ISSUED` with `status` = `posted` (a reversed invoice that was never issued is `NOT_ISSUED` and `reversed`). The includes `issue_records` on the same lists return the records. A filter on another document type is `READ.INVALID_FILTER`.

## 4. Report operations

### 4.1 `balances:query`

The one aggregation operation. All other balance reports are views of it.

| Parameter | Req | Notes |
|---|---|---|
| company_ids | Req | One company, or several companies of the tenant with `read.cross_company` (4.1.2) |
| mode | Req | `CUMULATIVE`: sum of everything up to `as_of_date`. `MOVEMENT`: sum between `from_date` and `to_date` |
| as_of_date / from_date, to_date | Req | Posting dates |
| rule | Opt | `AUTO` (default) or `RAW` (4.1.1) |
| group_by | Opt | Any of: `company_id`, `account_id`, `account_type`, `account_group`, `fiscal_year`, `fiscal_period`, `posting_date`, `cost_center_id`, `project_id`, `profit_center_id`, `business_unit_id`, a free dimension type, `partner_id` (open items only), `transaction_currency`, `document_type`, `line_type`, `ledger_id` |
| filter | Opt | `account_id`, account number range, `account_type`, `account_group`, dimension values, `document_type`, `line_type`, `ledger_id`, `exclude_document_types`, `include_reversed` (default true) |
| sort, page | Opt | Over the group keys |

Result rows: the group keys, and for each row `debit`, `credit` and `net` as four-currency blocks (LC, GC, RC always, TC only when the row has a single transaction currency, that is when `transaction_currency` is a group key or filter), and `line_count`. Debit and credit are the sums of positive and negative signed amounts (02, A1), derived at read time.

#### 4.1.1 Rule AUTO

`AUTO` applies the year-end design (05, 5.4, ADR-054) per account type, so callers cannot get it wrong:

| Account type | Cumulative balance at `as_of_date` | Movement |
|---|---|---|
| Balance sheet (ASSET, LIABILITY, EQUITY) | All lines up to the date, all fiscal years | Lines in the range |
| P&L (REVENUE, EXPENSE) | Fiscal year to date: lines of the fiscal year containing `as_of_date`, up to the date, **excluding** documents of type YEAR_END and CLOSING | Lines in the range, excluding YEAR_END and CLOSING |

`CLOSING` (the system-generated closing entry of a migrated history year, set by the HISTORY chunk, 13, X1 of 17) is treated like YEAR_END: it is excluded from P&L reads so that the comparatives of history years are preserved, and it is included in balance sheet reads (the retained earnings effect is carried). `RAW` sums exactly what the filters select, with no account-type rule. `RAW` is for audit and reconciliation, and it includes YEAR_END and CLOSING documents.

Revaluation documents and their reversals are ordinary ledger documents and are included, so balances show the closing-rate adjustment between a revaluation date and its reversal date (05). Reversed documents and their reversals both count (they offset).

#### 4.1.2 Several companies

With several `company_ids` (Q4 decided): allowed with `read.cross_company` for any companies of the tenant the caller can access, whether or not they belong to one group. The core performs no translation, consolidation or elimination (ADR-017).

**Rule: amounts are never summed across different currency codes.** Every amount is stored with its currency code (ADR-020). The aggregation adds `currency_lc`, `currency_gc` and `currency_rc` (and `transaction_currency` where applicable) to the row identity. Where all selected lines share a currency, a group has one row. Where companies have different local, group or reporting currencies, the same group key appears in several rows, one per currency combination, each labelled with its currencies. A caller that wants a single figure restricts the companies (or `group_by` `company_id`) or converts outside the core. This replaces any mixed-currency error, nothing is ever silently added up across currencies.

### 4.2 `trial-balances:query`

A named view of `balances:query` for the most common audit report.

| Parameter | Req | Notes |
|---|---|---|
| company_id | Req | |
| fiscal_year, from_period, to_period | Req | |
| group_by | Opt | `account_id` (default), optionally with dimensions |
| include_special_periods | Opt | Default true |

Per account: `opening` (cumulative before `from_period`, with the AUTO rule: P&L opening is zero at the fiscal year start), `debit` and `credit` of the periods, `closing`. Each is a four-currency block. The response proves the report foots: `total_debit` equals `total_credit` in every currency, and `l2_stable` tells whether all periods are hard-closed.

### 4.3 `account-ledgers:query`

Lines of one account with a running balance (the general ledger as a report).

| Parameter | Req | Notes |
|---|---|---|
| company_id, account_id | Req | |
| from_date, to_date | Req | Posting dates |
| filter | Opt | Dimensions, `partner_id`, `document_type`, `line_type` |
| page | Opt | |

Order: `posting_date`, document `chain_seq`, `line_no`. Response: an `opening_balance` row (four currencies, by the AUTO rule), then lines with the document reference, text, dimensions, the amounts, and a running balance per currency, then a `closing_balance` row. Running balances carry across pages through the cursor.

### 4.4 `open-items:query`

Open items as of a date, computed from line amounts and clearing records, not from the current clearing cache (05, A1).

| Parameter | Req | Notes |
|---|---|---|
| company_id | Req | |
| as_of_date | Opt | Default today. Lines posted on or before the date, cleared amounts from `clearing_item` with `clearing_date` on or before the date |
| filter | Opt | `account_id`, `partner_id`, `due_date` range, currency, `payment_block`, `include_cleared` (default false) |
| sort, page | Opt | Default `due_date` |

Per item: the line and document references, partner, `due_date`, `baseline_date`, the original amounts, `cleared` amounts, `remaining` amounts as four-currency blocks, `days_past_due` (as of the date), and the S-cache status for comparison. For an `as_of_date` earlier than today the status is computed, never read from the S columns. Valuation is at booked amounts. Closing-rate valuation is not in R1 (Q3).

### 4.5 `aging:query`

| Parameter | Req | Notes |
|---|---|---|
| company_id | Req | `company_ids` with `read.cross_company` for several, with the currency rule of 4.1.2 |
| as_of_date | Opt | Default today |
| basis | Opt | `DUE_DATE` (default), `BASELINE_DATE`, `DOCUMENT_DATE` |
| buckets | Opt | List of day boundaries, default `[0, 30, 60, 90]` giving: not yet due or current, 1-30, 31-60, 61-90, over 90 (Q8) |
| group_by | Opt | `partner_id`, `account_id`, `currency`, `company_id` |
| filter | Opt | As open items |

Based on the same computation as 4.4. Per group and bucket: the remaining amount as a four-currency block and the item count. A row total and a grand total per currency are included.

### 4.6 `partner-balances:query`

Per partner (and optionally per company and account): the sum of remaining open items as of a date, in four currencies, plus the oldest due date and the number of overdue items. A convenience over 4.4. At booked amounts (Q3).

### 4.7 `documents:get-context` (drill-down)

One document with its whole context, the endpoint behind "show me the document".

| Include | Content |
|---|---|
| `lines`, `tax`, `open_items`, `dimensions` | As in section 3, all lines including generated ones |
| `legal_snapshot` | Needs `read.legal_snapshot`, logged |
| `reversal` | The reversed or reversing document, with the reason |
| `intercompany` | The counterpart document through `ic_transaction_id`, if the caller has access to that company |
| `clearings` | For each open-item line: the clearings that touched it with amounts and dates, and the difference document |
| `revaluation`, `vat` | Run items that include a line of this document, VAT returns that report its tax lines, and evidence rows |
| `changes` | Change log entries of the document (L2 history) |
| `origin` | The `command_log` entry (command, actor, correlation id, executed at) |
| `integrity` | `chain_seq`, `prev_hash`, `row_hash`, the last anchor at or after this `chain_seq` |

Related reads that need other permissions are omitted with a flag, not rejected (`omitted: ["legal_snapshot"]`).

### 4.8 `audit-events:query`

A single normalised view answering "who did what, when". Since the scope review (14 K3, ADR-159) it is **not** a union over the audit sources: it reads the one append-only stream `outbox_event` where `is_audit` = true (15, 3.7 and section 7). The detailed logs stay the evidence for field-level changes. The outbox row is the ordered index over them and carries a reference to the source (`chain_ref` for chained records, the log row for others, 15, section 7).

Event shape (from the outbox columns): `event_time` (`occurred_at`), `actor` (`actor_ref`), `app_id`, `event_type`, `entity_type`, `entity_id` (`entity_key`), `company_id`, `correlation_id`, `reason_code` (from the detail), `position` (the outbox position, a stable cursor), and a `detail` reference (source table and key). Filters: time range, actor, `app_id`, event types, entity, company, `correlation_id`. `include=detail` adds the source row (change log row, order change row, chained record header, and so on). Needs `read.audit`. Events of one command share a `correlation_id`, so an auditor can reconstruct a whole operation.

Every audit source maps to outbox event types (15, section 6) and to the event types shown by the view:

| Source (evidence) | Outbox event types (`is_audit` = true) | Event types of the view |
|---|---|---|
| `journal_document`, `command_log` | `ledger.document_posted`, `ledger.document_reversed` (each successful batch item writes its own event, 15, 5.1) | POSTED, REVERSED |
| `change_log` (L2 changes of journal records) | `ledger.line_changed` | L2_CHANGE |
| `clearing` | `clearing.posted`, `clearing.reset` | CLEARED, CLEARING_RESET |
| `period_event` | `period.status_changed`, `year.opened`, `year.closed`, `year.reopened` | PERIOD_*, YEAR_* |
| `revaluation_run` | `revaluation.run_completed`, `revaluation.rerun` | REVALUATION_RUN (one view type for both, confirmed in 14 section 11, the outbox event type tells them apart) |
| `vat_return`, `journal_line_tax_evidence` | `vat.return_prepared`, `vat.return_filed`, `vat.settled`, `vat.settlement_reversed` (new, 14 section 11, from `vat-returns:reverse-settlement`), `vat.evidence_added` (new, 17 F2, 08, 5.3) | VAT_PREPARED, VAT_FILED, VAT_SETTLED, VAT_SETTLEMENT_REVERSED, VAT_EVIDENCE_ADDED |
| `master_change_log` | `master.created`, `master.changed`, `master.deactivated`, `master.reactivated` | MASTER_CREATE, MASTER_UPDATE, MASTER_DEACTIVATE, MASTER_REACTIVATE |
| `master_change_log` (action ANONYMISE), `anonymisation_run` | `partner.anonymised` (per partner or snapshot), `retention.applied` (the run) | ANONYMISE, ANONYMISATION_RUN |
| `applied_config_package`, configuration versions | `package.applied`, `config.version_created`, `company.activated` | PACKAGE_APPLIED, CONFIG_VERSION_CREATED, COMPANY_ACTIVATED |
| `chain_anchor` | `anchor.created` (`chain.verified` is dropped: `chains:verify` is read-only, writes no state and no event) | ANCHOR_CREATED |
| `stock_document` | `stock.document_posted`, `stock.document_reversed` (`stock.balance_changed` is not an audit event) | STOCK_POSTED, STOCK_REVERSED (10-inventory) |
| `migration_batch`, `migration_reconciliation` | `migration.batch_status_changed` (E6: one event per batch status change, summary `batch_id`, `status`, none per migrated record), and `migration.sealed` (one event at the seal, carrying the record counts and the chain ranges the batch created, 15, section 6). The transition to SEALED writes `migration.sealed` as its status-change event | MIGRATION_OPENED, MIGRATION_VALIDATED, MIGRATION_RECONCILED, MIGRATION_COMMITTING, MIGRATION_COMMITTED, MIGRATION_ABORTED, MIGRATION_ROLLED_BACK (from `migration.batch_status_changed`, one view type per status), MIGRATION_SEALED (from `migration.sealed`) (13-migration) |
| `legal_hold` | `legal_hold.changed` | HOLD_PLACED, HOLD_RELEASED |
| `retention:extend` | `retention.extended` | RETENTION_EXTENDED |
| Correction of the partner maximum `retention_until` (anonymisation job, 12, 4.3) | `retention.recomputed` (new, 14 section 11) | RETENTION_RECOMPUTED |
| `data_subject_request`, restriction | `dsr.received`, `dsr.decided`, `partner.restricted` | DSR_RECEIVED, DSR_DECIDED, PARTNER_RESTRICTED |
| `export_job` | `export.created` (request and completion), `export.downloaded` (each `exports:get-download-links` call) | EXPORT_REQUESTED, EXPORT_COMPLETED, EXPORT_DOWNLOADED |
| `tenant_deletion_request` | `deletion.scheduled` (request and approvals), `deletion.cancelled`, `deletion.executed` | TENANT_DELETION_REQUESTED, TENANT_DELETION_APPROVED, TENANT_DELETION_CANCELLED, TENANT_DELETION_EXECUTED |
| `invoice_issue_record` | `invoice.issued` | INVOICE_ISSUED |
| `order_event`, `order_change_log`, deliveries, receipts, returns, dropship confirmations, invoice link records | `order.created`, `order.changed`, `order.line_closed`, `order.cancelled`, `order.date_changed`, `delivery.posted`, `receipt.posted`, `return.posted`, `invoice.posted`, `dropship.confirmed` and their reversals (including `invoice.reversed`, 11, and `return.reversed` from `sales-returns:reverse` and `purchase-returns:reverse`, 17 B5, B6). Order lifecycle commands map as in 15, section 6 (line added, line cancelled and closed write `order.changed` or `order.line_closed`) | ORDER_PLACED, ORDER_CHANGED, ORDER_CANCELLED, ORDER_DATE_CHANGED, DELIVERY_POSTED, RECEIPT_POSTED, RETURN_POSTED, DROPSHIP_CONFIRMED, INVOICE_CREATED, SUPPLIER_INVOICE_MATCHED (11-order-management, 16) |
| `journal_document` of type GR_IR_WRITE_OFF | `gr_ir.written_off` (`gr-ir:write-off`), `gr_ir.write_off_reversed` (`gr-ir:reverse-write-off`), 15, section 6, 11, 7.5 | GR_IR_WRITTEN_OFF, GR_IR_WRITE_OFF_REVERSED (11-order-management) |
| `read_access_log` | `access.summary` (A6: one event per day, field group and company (partner group) written by a job, never per read, 5.2. Needs `read.audit`). Individual reads are evidence in `read_access_log` itself (`read.audit`, 5.2), not in the outbox | ACCESS_SUMMARY |
| `extension_definition`, `extension_value`, app registry | `extension.defined`, `extension.changed` (audit only for `is_pii` definitions), `app.registered`, `app.changed`, `app.reactivated` (`apps:reactivate`, 15, 4.5), `app.consumer_rewound`. All `app.*` events need `read.audit` in the feed (E5) | EXTENSION_DEFINED, EXTENSION_PII_CHANGED, APP_REGISTERED, APP_CHANGED, APP_REACTIVATED, APP_CONSUMER_REWOUND |

Events that are not audit events (`stock.balance_changed`, `reservation.*` including `reservation.adjusted`, `open_item.*`, `extension.changed` of non-PII definitions) are app feed events only (`events:list`, 4.12) and do not appear in this view. `is_audit` governs retention and the content of this view, not who may read an event in the feed (2.3a, E5). The advisory check `outbox:verify` (15, section 7) reports chained records and master changes without a matching audit event. Chained records created by a migration batch are not reported as gaps: the range of `chain_seq` per chain that the `migration.sealed` event lists counts as covered by that batch event (E6). Audit events are retained under the record class `AUDIT_LOG` (12, 4.6). Because the stream is the table, there is no computed union and no source can be forgotten silently, a missing source is a missing event type (Q11).

### 4.9 `chains:status`

Read counterpart of `chains:verify` (06). It lists **all six chain types** of the company from the single `chain_head` table (14 K1, ADR-158), one entry per head row (`company_id`, `chain_type`, `scope_key`):

| `chain_type` | `scope_key` | Entries per company |
|---|---|---|
| `LEDGER` | `ledger_id` | One per ledger |
| `CLEARING` | empty | One |
| `PERIOD_EVENT` | empty | One |
| `VAT_RETURN` | empty | One (over all return types, 14 section 10, 08, 6.6) |
| `STOCK` | empty | One |
| `FULFILMENT` | empty | One |

Per entry: the head (`last_seq`, `last_hash`, `hash_version`, `updated_at`) and the last anchor (time, `tsa_id`, and its state when anchors have one, A2 of 17). The result of a verification run is not stored and not shown here: `chains:verify` is a command that returns its result to the caller (06). Filters: `company_id`, `chain_type`, `scope_key`. Needs `read.audit`. The separate `stock-chains:status` of 10, section 8 is merged into this operation (STOCK and FULFILMENT are entries here), and 10 refers to `chains:status`. Verification itself stays in 06 (`chains:verify`, permission `chain.verify`).

### 4.10 `vat-positions:get`

Defined in 08, 7.4. Contract: parameters `registration_id`, `return_type`, optional `as_of_date`. Returns the aggregation of tax lines not currently reported in a snapshot (what a prepared return would contain), by box, EC list row or OSS row, with late lines flagged. Same row shapes as `vat_return_line`. Needs `read.ledger`. With partial deduction (ADR-204, 19, 7) every BOX row also returns `nondeductible_amount`, the non-deductible tax of the lines of the row (gross tax minus the tax line amount, in the return currency). It is informational and never part of `tax_amount`.

### 4.11 `supply-demand:query`

Defined in 16, section 4 (ADR-140). A snapshot read with the conventions of section 2 and the limits of 16, section 4. Its consistency token is the **outbox position** (2.2): the response carries `snapshot.outbox_position`, and the optional parameter `min_outbox_position` gives read-your-writes (`READ.STALE_SNAPSHOT` if the serving database is behind). There is no time-based `as_of` (orders and reservations keep no time series). Parameters: `company_id`, `item_id` list (maximum 500), `warehouse_id` list, `bucket` (DAY, WEEK, MONTH), `from_date`, `to_date` (at most 366 buckets), `min_outbox_position`, `include_status`, `group_by` (optional list of `location_type`, `ic`, see 16, 4). Returns per item and warehouse (warehouse null for dropship and service lines): `on_hand_by_status` (excluding TRANSIT locations), `in_transit_qty`, `reserved_qty`, and buckets (including `PAST_DUE` and `UNDATED`) with `sales_demand_qty`, `purchase_supply_qty`, `reserved_demand_qty`, `dropship_qty`, and the `ic` flag where the result is grouped by it. Nothing is stored, nothing is netted or promised. Needs `read.inventory` and `read.orders`. It uses the snapshot rules of `availability:query` (10, 6.3) and the open-quantity measure of 11 (ordered minus delivered, not below zero, over-tolerance not counted, referenced by 16, 4), so the reads agree.

### 4.12 `events:list` and `events:head`

Defined in 15, 5.3 (ADR-138). `events:list` returns app events and audit-relevant events from `outbox_event` in position order: `since_position` (exclusive), `limit` (default 500, maximum 5,000), filters `event_type`, `company_id`, `entity_type`, `entity_key`, returns `next_position` and `head_position`. Only event types the calling app is subscribed to (15, 3.3), and the families `access.*`, compliance and app events only with `read.audit` in addition (2.3a, E5). `events:head` returns the current head position. Both need `read.events` and an `app_code` (2.1), take no snapshot parameters (the position is the consistency token) and use the cursor semantics of 15, not of 2.4. Audit events as such are read through `audit-events:query` (4.8) with `read.audit`. Acknowledging and rewinding offsets are commands (15, 5.3, 06), not reads. `events:rewind` needs `apps.manage` and no `app_code` (F10).

### 4.13 Calendar helpers

`calendar:add-working-days` and `calendar:working-days-between` (16, 3.3, ADR-144): `{calendar_id | company_id + warehouse_id, date, days}` returns a date, or the number of working days between two dates. Read-only, permission `read.config`. No ledger, clearing, tax or period rule uses a calendar, and the business-day rule of A5 below is unchanged for aging.

## 5. Personal data

### 5.1 What counts

Fields of `business_partner_pii`, `partner_bank_account`, `partner_tax_registration.vat_id` of PERSON partners (07, Q3), the credit limits of PERSON partners (`business_partner`, `partner_company_relation`), the order address snapshots (`order_address_snapshot`, 11), the values of extension definitions with `is_pii` (15), and the legal snapshot of documents (ADR-064). The first group needs `read.pii`, the legal snapshot needs `read.legal_snapshot`. All are logged on every read, in lists, gets, drill-down includes, searches and exports (12, 7.1). The inventory of personal data is in 12, section 3.

### 5.2 `read_access_log` (E)

PK: (`tenant_id`, `read_id`). Index (`tenant_id`, `partner_id`, `read_at`), (`tenant_id`, `actor`, `read_at`), (`tenant_id`, `export_job_id`). Insert only, written in the same transaction as the read, so a response is never returned without its log row. Extended by 14 D7 (ADR-151) so that it fits the export logging of 12, 7.1.

| Column | Type | Null | Notes |
|---|---|---|---|
| tenant_id, read_id | ID | N | |
| read_at | TS | N | UTC |
| actor | TEXT(100) | N | |
| operation | CODE | N | e.g. `business-partners:get`, `documents:get-context`, `business-partners:search`, `exports:<type>` |
| company_id | ID | Y | Context company |
| partner_id | ID | Y | Interactive reads: one row per partner whose personal data was returned. Null for an export job, which logs one row per job, not per partner |
| partner_count | INT | Y | Export jobs: the number of partners whose personal data the job read. Null for interactive reads (one partner per row) |
| export_job_id | ID | Y | Set when the read was made by an export job (12, 7.1) |
| document_id | ID | Y | Set when the legal snapshot of a document was read |
| field_group | CODE | N | PII_IDENTITY, PII_ADDRESS, PII_CONTACT, PII_BANK, PII_TAX_ID, PII_CREDIT (credit limits of PERSON partners), ORDER_ADDRESS (order address snapshots), SEARCH (a search or filter on personal fields, 5.3), PII_EXTENSION (`is_pii` extension values, 15), LEGAL_SNAPSHOT |
| purpose_code | CODE | Y | From the request context (Q5) |
| correlation_id | TEXT(100) | N | |
| result_position | INT | Y | Position in a list or search result. Null for export jobs |

No UPDATE or DELETE grants. The log holds only ids, so partner anonymisation does not need to touch it. Retention is a tenant setting (Q6). It is not part of a hash chain (operational audit). **A6 (17 section 4):** read access is logged **only** in `read_access_log`. A read writes no outbox event and never takes the `outbox_head` lock, so PII reads do not serialise with writes. The event `access.read_logged` per read no longer exists. Instead a scheduled job writes one `access.summary` outbox event per day and partner group (the field group and company of the log rows, counts of reads, distinct actors and distinct partners, no ids of persons, 15, section 6, `is_audit` = true, needs `read.audit` in the feed, 2.3a). The log row is inserted before the response is returned, in the transaction of the read on the primary database. A read that returns no personal data writes no log row and may run on a replica (2.2, Q2). A PII read served from a replica snapshot still commits its log row on the primary before the response is returned.

The log is purged by the retention job under the retention role after `read_log_retention_days` (Q6), which is the only DELETE the system performs on it (12, 4.6).

An interactive list or search returning N partners with personal data writes up to N rows, capped at the page size (500). The cost is accepted because personal-data reads are rare compared with ledger reads. An export job writes **one row per job and field group** (never per partner) with `partner_id` null, `partner_count` and `export_job_id`, however many partners it reads (12, 7.1).

### 5.3 Partner search

`business-partners:search` finds partners by name or address text (prefix or `contains` on `legal_name`, `trade_name`, `postal_code`, `city`, `email`, 2.4.1). It reads the PII table, therefore needs `read.pii` and is logged like any other personal-data read (field group SEARCH, plus the groups of the fields returned). Without `read.pii` only `partner_code` and role filters are possible. Searching personal data in the core avoids copying it into app-side indexes (Q9).

## 6. Index and column additions required by the reads

| Table | Addition | Reason |
|---|---|---|
| `clearing_item` | New L1 column `clearing_date` DATE (copied from `clearing`), index (`tenant_id`, `company_id`, `open_item_document_id`, `open_item_line_no`, `clearing_date`) | As-of open items and aging without joining `clearing` |
| `journal_document` | Indexes (`tenant_id`, `company_id`, `posting_date`, `chain_seq`), (`tenant_id`, `company_id`, `document_type`, `posting_date`), (`tenant_id`, `company_id`, `source_system`, `source_document_ref`), (`tenant_id`, `company_id`, `external_reference`) | Document lists and lookups from source systems |
| `journal_line` | Indexes already in 02 (account and posting date, fiscal year and account). Add (`tenant_id`, `company_id`, `posting_date`, `account_id`) for date-range scans | Account ledger and movement queries |
| `journal_line_open_item` | Indexes already in 02, 5A (partner and status, partner and due date) | Aging |
| `change_log`, `master_change_log` | Indexes already specified, add (`tenant_id`, `correlation_id`) on both (`change_log`: in 02. `master_change_log`: not yet in 07, 10.1) | Audit events by correlation id |
| `command_log` | Index (`tenant_id`, `correlation_id`) (in 06, 4.1) | Audit events and drill-down |
| `read_access_log` | Index (`tenant_id`, `export_job_id`) (5.2) | Which reads an export job made |
| `outbox_event` | PK (`tenant_id`, `position`) (15, 3.7). Secondary indexes (confirmed, in 15, 3.7): (`tenant_id`, `is_audit`, `occurred_at`), (`tenant_id`, `correlation_id`), (`tenant_id`, `entity_type`, `entity_key`, `position`) | `audit-events:query` (time range, correlation, entity), `events:list` filters |

A materialised balance cache is not part of R1 (05 Q5). The balance operation scans lines through these indexes. If volumes require it, a derived cache can be added behind the same operation without changing the contract.

## 7. Assumptions

| Ref | Item |
|---|---|
| A1 | Report amounts always come from the ledger lines (L1), never from the S caches, except where the S value is shown for comparison (open items) |
| A2 | The read API never mutates data. The only write is the read access log row, in the transaction of the read, and never an outbox event or an `outbox_head` lock (5.2, A6). `events:ack` and `events:rewind` are commands (15), not reads |
| A3 | Sorting by chain order is stable under concurrent posting through the `ledger_seq` cap (2.2) |
| A4 | `RESULT_TOO_LARGE` is a deliberate refusal. Clients narrow filters or page by group keys. Big extractions use the compliance export jobs |
| A5 | All business-day logic (overdue, buckets) uses calendar days between dates in the caller's `as_of_date`. No report uses a business-day calendar. Calendars exist as configuration for apps (16, 3.2) and are reachable through the helpers of 4.13 only |

## 8. Open questions

| # | Question |
|---|---|
| Q1 | **Decided (differs from the proposal):** a `contains` operator on allowlisted text fields (2.4.1): literal, case- and accent-insensitive, at least 3 characters, guarded by a scope requirement on ledger resources. Physical index technology is decided in the platform mapping ADR, because it is not portable (ADR-001). Search columns of personal data must be cleared by the retention job. |
| Q2 | **Decided:** `snapshot.ledger_seq` plus optional `min_ledger_seq` with `READ.STALE_SNAPSHOT`, so reads can run on replicas without losing read-your-writes. For orders, reservations and supply and demand the token is the outbox position (`min_outbox_position`, 2.2). |
| Q3 | **Decided:** open items, aging and partner balances at booked amounts only in R1. Closing-rate partner balances can be derived from the run items later. |
| Q4 | **Decided (differs from the proposal):** reads may span any companies of the tenant with `read.cross_company`, regardless of group membership. Amounts are never summed across different currency codes: rows split by currency combination (4.1.2). No translation or consolidation. |
| Q5 | **Decided:** `purpose_code` is optional in the context of personal-data reads and logged when present. The list of valid purposes is configuration (compliance domain). |
| Q6 | **Decided:** `read_access_log` retention is a tenant setting (`read_log_retention_days`), default the longest company retention rule. The rows hold only ids. |
| Q7 | **Decided:** results over the limit are refused with `RESULT_TOO_LARGE`. Large extracts belong to the asynchronous export jobs of the compliance domain. |
| Q8 | **Decided:** aging buckets are caller-supplied per request, default 0-30-60-90. The response states the buckets used. |
| Q9 | **Decided:** partner name and address search runs in the core (5.3), needs `read.pii` and is logged. No app-side copies of personal data are required. |
| Q10 | **Decided:** a separate `read.legal_snapshot` permission, logged with field group LEGAL_SNAPSHOT. Invoice rendering does not need `read.pii`. |
| Q11 | **Decided, amended by 14 K3 (ADR-159):** the audit-event view reads the outbox stream (`outbox_event` where `is_audit`), no longer a computed union over the source logs. Time range, actor or entity filters are still required to keep wide queries bounded. |
| Q12 | **Decided:** lists hide inactive config and master rows by default (`include_inactive` shows them), `get` by key always returns the row. |
| Q13 | **Decided (14, section 10):** outbox event types `ledger.line_changed`, `revaluation.run_completed`, `revaluation.rerun`, `dsr.received`, `dsr.decided`, `invoice.issued`, `master.reactivated` are added (all audit, no PII in the summary) and mapped in 4.8. The view-type names in the right column of 4.8 are approved. Per-item batch events replace the former event type BATCH_ITEM. The types are in 15, section 6 (15 v0.3). Their summary fields there (15, section 6) were checked against this table and 11 and are consistent. 14 section 11 adds `vat.settlement_reversed` and `retention.recomputed`. 17 (fix pass 4) adds `vat.evidence_added`, `migration.sealed` and `access.summary`, drops `access.read_logged` and `chain.verified`, and maps them in 4.8. |
| Q14 | **Decided (14, section 10):** field groups `PII_EXTENSION`, `ORDER_ADDRESS`, `PII_CREDIT`, `SEARCH` are approved (5.2). The export log has one row per job and field group, never per partner. |

## 9. Deltas to earlier specs

| Spec | Change |
|---|---|
| 03 `clearing_item` | New L1 column `clearing_date` (6). Included in the clearing hash |
| 02 | Index additions (6) |
| 06 | New error area `READ` (2.5). Permissions of 2.3 added to 06, section 8 |
| 07 | Config `get`, `list` follow 2.4. `tenant_setting` gains `read_log_retention_days` |

These deltas are applied with this spec.

### 9.1 Deltas of the 2026-10-13 re-baseline (14, sections 8 and 9, specs 15 and 16)

| Source | Change in this spec | Other specs |
|---|---|---|
| D7, ADR-151 | `read_access_log`: `partner_id` nullable, new `export_job_id` and `partner_count`, wider `field_group` list (credit limits, search, order addresses, extensions). Exports log one row per job (5.2) | 12, 7.1 is aligned |
| K1, ADR-158 | `chains:status` lists all six chain types from `chain_head` (4.9), VAT_RETURN as one entry per company with empty `scope_key`. The duplicate `stock-chains:status` is merged | 10, section 8: `stock-chains:status` is removed and refers to `chains:status` |
| K3, ADR-159 | `audit-events:query` reads `outbox_event` where `is_audit`. Every audit source is mapped, including reactivation, anonymisation, export download, `retention:extend`, tenant deletion cancel and execute (4.8) | 15, section 6 gets the types of Q13 (14 section 10) |
| 14 section 10 | 4.8 mapping completed with the seven new event types. Field groups of 5.2 approved. Export log one row per job and field group. `chains:status` VAT_RETURN entry (4.9). Q13, Q14 closed | 15 (event types), 12 |
| 14 section 11 | 4.8 maps `vat.settlement_reversed` (VAT_SETTLEMENT_REVERSED) and `retention.recomputed` (RETENTION_RECOMPUTED). Both revaluation event types map to REVALUATION_RUN (confirmed). `invoice.reversed` named in the order row | 15, section 6 (applied in 15 v0.3), 08, 12 |
| 15, ADR-137 | `include=extensions` and `ext.*` filters (2.4.2). `extension-definitions:list`. PII read log covers `is_pii` extension values (5.1, 5.2) | 07, 10, 11: gets and lists of extendable entities follow 2.4.2 |
| 15, ADR-138 | `events:list`, `events:head` (4.12). Permission `read.events` (`read.ext` was removed in fix pass 4, 9.2) | 06: `events:ack`, `events:rewind` commands |
| 16, ADR-140, ADR-144 | `supply-demand:query` (4.11) and the calendar helpers (4.13) join the read catalogue. `item-partners` and `business-calendars` resources | 10, 11: unchanged reads |
| Permissions | `read.inventory`, `read.orders`, `read.events` added to 2.3 (`read.ext` removed again, 9.2) | 06, section 8 (D10, ADR-154) |
| Resources | `command-log`, `master-change-log`, `applied-config-package` added to section 3. Filter `migration_batch_id` on journal documents and on the order, fulfilment and migration-affected resources | 13 |
| Mechanical | Index references corrected (6): `journal_line_open_item` indexes are in 02, 5A, not 03. The `journal_document` and `change_log` indexes are already in 02 | 07: `master_change_log` still lacks the correlation index |

### 9.2 Deltas of fix pass 4 (17, section 4)

| Ref | Change in this spec | Other specs |
|---|---|---|
| F9, F10 | `app_code` in the request context (2.1). `read.ext` removed from 2.3, extension reads follow each `read.*`. `extension-definitions:list` needs `read.config` | 06 permission list, 15, section 9 |
| A6 | Reads log only in `read_access_log`, never an outbox event or `outbox_head` lock. `access.read_logged` replaced by the daily `access.summary` (5.2, 4.8, A2) | 15, section 6 |
| `chain.verified` | Dropped, together with "last verification run" in `chains:status` (4.8, 4.9). `read.audit` no longer claims verification (2.3) | 15, section 6 |
| E5 | Feed visibility separate from `is_audit` (2.3a, 4.12) | 15, 3.3 |
| E6 | `migration.batch_status_changed` per status change plus `migration.sealed`, mapping of 4.8 corrected, `outbox:verify` covers migrated ranges | 13, 15 |
| Resources | `anchors`, `migration-batches`, `migration-control-totals`, `migration-reconciliations`, `migration-commit-chunks`, `fiscal-calendars`, `posting-windows`, `number-series-assignments` added to section 3 | 02, 04, 13 |
| As-of | Outbox position as consistency token for orders, reservations and supply and demand, no time `as_of` (2.2, 4.11) | 16, 4 |
| Events | `vat.evidence_added`, `year.opened`, `reservation.adjusted` mapped (4.8) | 15, section 6 |
| Stale | Index row for `outbox_event` and the `access.read_logged` references corrected | none |

### 9.3 Deltas of the propagation pass v0.6 (17, sections 4 and 5)

| Ref | Change in this spec | Other specs |
|---|---|---|
| Propagation pass | X1: `CLOSING` excluded from P&L reads like YEAR_END, included in balance reads and `RAW` (4.2 rule table, ADR-088). 4.8 mapping: `return.reversed` (returns), `gr_ir.written_off` and `gr_ir.write_off_reversed` (new row), `app.reactivated` (app row), `access.summary` per day, field group and company. `ORDER_ADDRESS` confirmed in the field group list (5.2). Migration status events and `migration.sealed`, `vat.evidence_added` already mapped at v0.5 | 15, section 6 (event entries), 02 and 05 (CLOSING type) |

### 9.4 Deltas of ADR-200 and ADR-203 to ADR-206 (2026-10-14)

| Ref | Change in this spec | Other specs |
|---|---|---|
| ADR-203, ADR-204 | Resources `vat-deduction-keys` and `vat-deduction-rates` (section 3). The include `tax` returns the deduction columns and the allocation rows. `TAX_NONDEDUCTIBLE` is a `line_type` value for filters and grouping. `vat-positions:get` reports `nondeductible_amount` per BOX row (4.10) | 02, 07, 08, 19 |
| ADR-200 | The include `tax` returns `origin_eu` and `destination_eu` | 02, 07 |

## 10. ADRs raised by this spec (now in 01-adr-log, which is authoritative)

**ADR-086: Read API shape.** Generic `get` and `list` on every entity with typed filters, deterministic sorting and cursor paging, plus named report operations for balances, trial balance, account ledger, open items, aging, partner balances, document drill-down, audit events and chain status.

**ADR-087: Consistent snapshots and ledger position.** One snapshot per read. Responses carry `ledger_seq` per ledger, cursors cap at it, and `min_ledger_seq` gives read-your-writes on replicas. Reports are as-of dates and carry `l2_stable`.

**ADR-088: Balance rules.** One balance query. P&L accounts are fiscal-year-to-date excluding YEAR_END and CLOSING, balance sheet accounts cumulative (rule AUTO), RAW for reconciliation. Multi-company reads need `read.cross_company`, may span any companies of the tenant, and never sum across different currency codes (rows split by currency).

**ADR-089: Open items as of a date from clearing records.** Remaining amounts are computed from lines and `clearing_item` with `clearing_date`, so historic aging is exact. `clearing_item` carries `clearing_date`.

**ADR-090: Personal-data reads are permissioned, omitted without permission and logged.** `read.pii`, field omission instead of masking, and an insert-only `read_access_log` written in the read's transaction. Partner name search runs in the core and is logged. *Amended by ADR-151 (14 D7): `partner_id` nullable, export jobs logged one row per job with `export_job_id` and `partner_count`.*

**ADR-091: Audit events view.** A computed, normalised view over all audit sources with a shared `correlation_id`. *Amended by ADR-159 (14 K3): the view reads the single outbox stream, it is no longer a computed union.*

**ADR-092: Logical `contains` operator.** A literal, case- and accent-insensitive substring filter on allowlisted text fields, with a scope guard on ledger resources. Index technology is left to the platform mapping, search columns of personal data are cleared by the retention job.
