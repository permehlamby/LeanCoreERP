# Finance Core: Configuration and Master Data, Table Specs

Status: DRAFT v0.6, propagation pass 2026-10-13 (17, section 5 applied, delta in 13.2d). v0.6 changes: account role `GR_IR_WRITE_OFF` in the role table (6.3), `tenant_setting.anchor_pending_deadline` (5.2), `CONFIG.REVALUE_FLAG_ON_RETAINED_EARNINGS` (6.1, 6.3, 11.2), readiness warning for `GR_IR_WRITE_OFF` (11.3), `gr-ir:reverse-write-off` in the permission map (11.1). Earlier: v0.5 (fix pass 4 2026-10-13, 17, section 4 applied, delta in 13.2c). v0.5 changes: names `sales_invoice_mode`, `auto_reserve` and `effective_from` (X3), lock sort tuple (chain type, company, scope_key) (X4), `migration_document_max_lines` (X6), the configuration tables of 04 in the overview (F3), `item_partner` key with `company_key` (F4), end-version rule for V tables (F7), role table rules (F8), `retention_floor` (E2), `master_change_log` `app_id` and CLEAR (E3), tax determination tie-break and NOT_REGISTERED (F1), `return_type` on `tax_code_version_box`, series assignment `effective_from` as DATE (A3). Earlier v0.4 added: the `upsert` rule against `expected_version` confirmed (Q14, 14 section 11, no longer proposed), the settlement reversal command renamed `vat-returns:reverse-settlement` (permission `vat.settle`) in the permission map, and the status of the reverse commands as adopted. Earlier: v0.3 (second re-baseline pass, 14, sections 8, 9 and 10 applied) added: the `write.config` and `write.master` split confirmed (Q12), `match_mode` and `allow_invoice_before_receipt` kept in `company_order_setting_version` (S1, Q13), `sales_invoice_mode` default CORE_BUILDS for new companies (S2), a rule for `upsert` against `expected_version` (Q14, confirmed at v0.4), the `chain_head` scope key with one VAT_RETURN chain per company (K1), and the new reverse commands of 06 in the permission map. Earlier: v0.2, re-baselined for the decisions of the scope review (14, sections 8 and 9). Builds on 00-design-principles v0.4, ADR-001 to ADR-174 and specs 02 to 16. v0.2 adds: the role table with the required open-item mode per role and the unique key with the VAT registration (D5, ADR-149), the permission split `write.config` and `write.master` (D10, ADR-154), the completed table overview with the tables of specs 08 to 16 (including `chain_head`, K1, ADR-158, and the platform tables of 15 and 16), the thinned order setting with the versioned invoice mode and the auto-reservation setting (S1 to S3, ADR-155 to ADR-157), the rate usage rule without clearing (D9, ADR-153), the retention maintenance by batch job (D3, ADR-147), and the readiness checks of the later specs. ADR-137 to ADR-144 are raised in specs 15 and 16.
Scope: reference data, organisation (tenant, group, company), chart of accounts and account rules, system account roles, dimensions, currencies and exchange rates, tax configuration, business partners, banking, payment terms, change audit, configuration packages, the create/read/update/deactivate contract for these tables, and the **overview of all configuration and master tables of the core** (section 3), including those specified in other specs.
Out of scope: posting rules engine for business events (ADR-012), field specs of tables owned by other specs (items and units in 10, VAT in 08, orders in 11, retention and export in 12, migration in 13, extensions and app registration in 15, planning data in 16), read and query APIs, authentication and user management, tenant provisioning (platform).
Conventions, lock classes and types as in 02-journal-document-core, section 1. Transaction commands, error areas and the permission table are in 06.

## 1. Decisions taken

| Topic | Decision |
|---|---|
| Change pattern | **Configuration** is versioned: insert-only version rows with a validity, the history is the audit trail. **Master data** is current-state: one row, a `row_version`, and a generic `master_change_log`. Personal-data fields appear in the log as "changed" only, never with values |
| System accounts | One `account_role_assignment` table maps a fixed list of roles (rounding, FX, retained earnings, IC and so on), optionally per currency, counterpart or VAT registration, to accounts. The role table (6.3) states the required open-item mode of every role, also of the roles resolved by `account_determination` (D5, ADR-149) |
| Permissions | Writing configuration needs `write.config`, writing master data `write.master`, with the fixed exceptions `config.apply`, `company.activate` and `history.open` (11.1, D10, ADR-154) |
| Order settings | `company_order_setting_version` keeps the line tolerance defaults, `match_mode_default`, `allow_invoice_before_receipt`, the per-company `sales_invoice_mode` (CORE_BUILDS or APP_SUPPLIES, default CORE_BUILDS) and the `auto_reserve` setting, default off (5.7, S1 to S3) |
| Chains | The head of every hash chain is a row of one `chain_head` table (K1, ADR-158). The six head tables of earlier drafts are gone |
| Dimensions | Dimension values are tenant-wide with an optional company restriction (null = all companies of the tenant) |
| Partners | One tenant-level `business_partner` with customer and supplier roles, personal data in a separate anonymisable table, per-company relation rows |
| Reference data | Currencies, countries and similar lists are global, read-only tables without `tenant_id`. The only exception to P7 |
| Templates | Country templates (chart of accounts, tax codes, rules, return boxes, roles) are versioned packages applied through the normal config API. No template tables in the data model |
| Concurrency | `row_version` with `expected_version` on config and master updates |
| Tax class | Account default plus optional line override, stored on the tax detail. The rule table is key-extensible, because the final determination design (origin, destination, account, material) is not fixed |

## 2. Conventions

### 2.1 Table patterns

| Pattern | Meaning | Columns added to every table of the pattern |
|---|---|---|
| **R** Reference | Global, read-only, platform-maintained, no `tenant_id` | none |
| **V** Versioned config | Insert-only. A change is a new row with a later validity. No UPDATE, no DELETE. Current version = the row valid at the relevant date. **End-version (F7):** an object is retired by inserting a new version row with `valid_to` set, never by updating the earlier row. The guards of 2.3 apply to the `valid_from` of the end-version. After the `valid_to` the object does not apply | `created_at`, `created_by` |
| **M** Master, current-state | One current row. UPDATE by command with `expected_version`. Deactivation instead of deletion. Every change appended to `master_change_log` | `row_version` INT, `created_at`, `created_by`, `updated_at`, `updated_by` |
| **E** Event/log | Insert only | as stated per table |
| **S** System state | Written only by system commands as part of their transaction (heads, counters, offsets). Never through the generic operations of 11.1. No `master_change_log` rows (D2, ADR-146) | as stated per table |

Combinations in the overview (section 3) read as "E + S" (insert-only rows with S columns) or "M + S".

The shared columns are not repeated in the table specs below.

### 2.2 Two validity notions

| Column | Type | Meaning | Used for |
|---|---|---|---|
| `valid_from`, `valid_to` | DATE | Real-world validity, compared with the posting or document date | Tax codes, tax rules, rates, partner registrations, dimension values, accounts |
| `effective_from` | TS UTC | When a rule starts to apply to commands | Account roles, settings, `posting_window` (04). **Exception (A3):** `number_series_assignment.effective_from` is a DATE (the fiscal year start): series are resolved by the posting date's fiscal year, not by command time |
### 2.3 Rules for all config and master tables

- **No hard delete through the API.** "Delete" means deactivation (`active` = false, or `valid_to` set). Physical deletion happens only through the retention job and tenant deletion (compliance domain). The API therefore has create, read, update and deactivate (Q2).
- **Immutable once referenced.** Columns marked "immutable after posting" (IAP) cannot change once any posted record refers to the row. The command rejects with `CONFIG.IMMUTABLE_AFTER_POSTING`.
- **Closed-period guard.** A new version of a V table must have `valid_from` not earlier than the start of the first not-hard-closed period of every company it affects, so closed history is not rewritten. It must also be later than the latest existing version of the same object.
- **Referential integrity.** All references are composite and tenant-led (ADR-002). A referenced row cannot be deactivated if that would leave a mandatory role without account (e.g. the retained earnings account).
- **Audit.** V tables: `created_by`, `created_at` on each version. M tables: `master_change_log`. Both are insert-only for the application role. Every change also writes an outbox event (15, section 6) in the same transaction.
- **S columns.** An S column of an M table (for example `tax_account`, `first_used_at`, `retention_until`, `restriction_status`) is changed only by the system command that owns it, never through `{resource}:update`, and without `master_change_log` rows. Each such change has its own event as defined by the owning spec (D2, ADR-146). The L1 and L2 columns of config and master tables follow the generic rules above.
- **Permissions.** The generic operations need `write.config` or `write.master` by resource (11.1). Reads need `read.config` (09).

## 3. Table overview

### 3.1 Tables specified in this spec

| # | Table | Pattern | Layer |
|---|---|---|---|
| 1 | `ref_currency` | R | Reference |
| 2 | `ref_country` | R | Reference |
| 3 | `ref_country_eu_membership` | R | Reference |
| 4 | `tenant` | M | Organisation |
| 5 | `tenant_setting` | M | Organisation |
| 6 | `company_group` | M | Organisation |
| 7 | `company` | M | Organisation |
| 8 | `company_setting_version` | V | Organisation |
| 9 | `group_membership` | V | Organisation |
| 10 | `gl_account` | M | Chart of accounts |
| 11 | `account_dimension_rule` | M | Chart of accounts |
| 12 | `account_role_assignment` | V | Chart of accounts |
| 13 | `group_chart_account` | M | Chart of accounts |
| 14 | `account_group_mapping` | V | Chart of accounts |
| 15 | `dimension_type` | M | Dimensions |
| 16 | `dimension_value` | M | Dimensions |
| 17 | `tenant_currency` | M | Currency |
| 18 | `exchange_rate` | M (usage-locked) | Currency |
| 19 | `tax_class` | M | Tax |
| 20 | `tax_return_box` | M | Tax |
| 21 | `tax_code` | M | Tax |
| 22 | `tax_code_version` | V | Tax |
| 23 | `tax_code_version_box` | V | Tax |
| 24 | `tax_determination_rule` | V | Tax |
| 25 | `tax_determination_alternative` | V | Tax |
| 26 | `business_partner` | M | Partners |
| 27 | `business_partner_pii` | M (anonymisable) | Partners |
| 28 | `partner_company_relation` | M | Partners |
| 29 | `partner_bank_account` | M (anonymisable) | Partners |
| 30 | `partner_tax_registration` | M | Partners |
| 31 | `company_bank_account` | M | Partners |
| 32 | `payment_terms` | M | Partners |
| 33 | `master_change_log` | E | Audit |
| 34 | `applied_config_package` | E | Audit |

Tables 1 to 3 and 4 are platform-level (outside tenant isolation, A6). All others lead with `tenant_id`. `ref_country_eu_membership` is a global reference table like `ref_currency` and `ref_country` and is part of the ERD with them.

### 3.2 Configuration and master tables specified in other specs

Listed here so that this overview is complete. The owning spec defines the columns. Pattern letters as in 2.1.

| Table | Pattern | Layer | Owner |
|---|---|---|---|
| `company_vat_registration` | M | Tax | 08, 4.1 |
| `fiscal_calendar` | M (versioned by 04, affects only years opened later) | Calendars and periods | 04, 3.1 |
| `posting_window` | V (insert only, `effective_from` TS) | Calendars and periods | 04, 4.1 |
| `number_series` | M (format closed once numbers are issued) | Number series | 04, 5.1 |
| `number_series_assignment` | V (insert only, `effective_from` DATE, A3) | Number series | 04, 5.2 |
| `ref_unit` | R | Reference (global, outside tenant isolation like 1 to 3) | 10, 3.1 |
| `item_group` | M | Inventory master | 10, 3.2 |
| `item` | M | Inventory master | 10, 3.3 |
| `item_unit_conversion`, `unit_conversion` | M | Inventory master | 10, 3.4 |
| `item_company` | M | Inventory master (also the stock lock row) | 10, 3.5 |
| `warehouse`, `warehouse_location` | M | Inventory master (`location_type`, 16, 6) | 10, 3.6 |
| `item_warehouse` | M | Inventory master | 10, 3.7 |
| `item_tracking` | M | Inventory master | 10, 3.8 |
| `account_determination` | V | Inventory and order account determination | 10, 3.9, 11, 3.3 |
| `company_order_setting_version` | V | Order settings (5.7) | 11, 3.1 |
| `retention_rule` | V | Compliance configuration | 12, 2.1 |
| `external_code_mapping` | V | Compliance configuration (SAF-T, EN 16931) | 12, 9.1 |
| `legacy_id_map` | M | Migration | 13, 3.5 |
| `item_partner` | V | Planning master data. Key carries `company_key` (0 = tenant row, a company id for a company override), like `account_role_assignment` (F4, 17). Written under `write.master` | 16, 3.1 |
| `business_calendar` | M | Calendars | 16, 3.2 |
| `business_calendar_version`, `business_calendar_exception` | V | Calendars. The key of the exception gets `created_at`, the latest row wins (F5) | 16, 3.2 |
| `company_calendar_assignment` | V | Calendars. Sentinel 0 for "company level" instead of NULL in the key (F6) | 16, 3.2 |
| `vat_deduction_key` | M | Tax. Named variable deduction rate per VAT registration (ADR-203) | 8.6 |
| `vat_deduction_rate` | V | Tax. Rate of a key, valid from a date, insert only (ADR-203) | 8.6 |

`journal_line_tax_evidence` (08, 2) is a transaction table (L2 rows on tax lines), not configuration, and is **not** listed here. `purchase_tolerance_override` of 11, 3.1a is **removed** (S1, ADR-155). Transaction, event and log tables are listed in their own specs, not here.

### 3.3 Platform tables of the extension and change-feed specs (15)

| Table | Pattern | Role | Owner |
|---|---|---|---|
| `app_registration` | M | Registered apps and their `app_code` | 15, 3.1 |
| `app_namespace` | M | Extension namespaces owned by an app | 15, 3.2 |
| `app_event_subscription` | M | Event types an app may read | 15, 3.3 |
| `app_consumer_offset` | M + S | Last acknowledged event position per app and consumer (`last_position` is S) | 15, 3.4 |
| `extension_definition` | M | Typed attribute definitions per entity type | 15, 3.5 |
| `extension_value` | M | Values per entity record. Outside every hash | 15, 3.6 |
| `outbox_event` | E | Change feed and audit stream (K3). Insert only except the retention purge | 15, 3.7 |
| `outbox_head` | S | Per-tenant position counter. Last lock of the order of 06, section 9 | 15, 3.8 |
| `chain_head` | S | One row per (`tenant_id`, `company_id`, `chain_type`, `scope_key`): `last_seq`, `last_hash`, `hash_version`, `updated_at`. Replaces `ledger_chain_head`, `clearing_chain_head`, `period_event_chain_head`, `vat_return_chain_head`, `stock_chain_head` and `fulfilment_chain_head` (K1, ADR-158) | 14, Q14 (columns), 06, 7.3 |

Chain types: LEDGER, CLEARING, PERIOD_EVENT, VAT_RETURN, STOCK, FULFILMENT. `scope_key` is the `ledger_id` for LEDGER and empty otherwise. The VAT_RETURN chain is one per company over all return types (14 section 10, K1). The event tables of the chains stay as specified in 02 to 05, 08, 10, 11. Lock order sorted by the tuple (chain type, company, scope_key) (X4, 06, 9), after the number counters. The `chain_head` rows are S rows: written only by the commands that append to the chain.

### 3.4 Readiness and the new tables

None of the tables of 3.3 is part of company readiness (11.3): apps and extensions are optional. The calendar and `item_partner` tables are optional as well.

## 4. Reference data (pattern R)

| Table | Key | Columns |
|---|---|---|
| `ref_currency` | `currency_code` (ISO 4217 alpha-3) | `numeric_code`, `name`, `minor_units` SMALLINT (decimal places, the amount scale per currency used in 06), `valid_from`, `valid_to` |
| `ref_country` | `country_code` (ISO 3166 alpha-2) | `alpha3`, `name`, `valid_from`, `valid_to` |
| `ref_country_eu_membership` | (`country_code`, `valid_from`) | `valid_to`. Date-dependent EU membership for VAT rules (e.g. a country joining or leaving) |

Maintained by platform releases. No API for tenants. Not part of any hash.

## 5. Organisation

### 5.1 `tenant` (M, platform-level)

PK: `tenant_id`. Columns: `tenant_code` (unique), `name`, `status` (ACTIVE, SUSPENDED, EXPORTED, DELETION_PENDING, DELETED, see 12, 6.1), `purpose` (PRODUCTION, REHEARSAL, TEST: rehearsal and test tenants have relaxed anchoring and deletion rules, 13, 2.2), `created_at`. Provisioning is a platform operation, out of scope.

### 5.2 `tenant_setting` (M)

PK: `tenant_id` (one row).

| Column | Type | Null | Notes |
|---|---|---|---|
| rate_fallback_days | SMALLINT | N | ADR-019: how far back a missing rate may be taken |
| command_log_retention_days | INT | N | 06 Q2, default 90 |
| max_batch_items | INT | N | 06 Q11, default 500 |
| lock_timeout_ms | INT | N | 06, section 9 |
| pivot_currency | CODE | Y | Currency used to derive cross rates (7.2). Null disables cross rates |
| read_log_retention_days | INT | Y | Retention of `read_access_log` rows (09, 5.2). Null: the longest retention rule of the tenant's companies |
| migration_max_history_years | SMALLINT | N | Maximum history fiscal years of a migration (13, 4.2). Default 3. The cutover `fiscal_year` carries `history_until_period` (D8, 04, 13): history guards key on it, this setting does not |
| migration_commit_chunk_documents | INT | N | Maximum documents and lines per commit chunk (13, 3.6). Default 20,000 |
| migration_document_max_lines | INT | N | Maximum lines per migration document, the same limit for all migration documents (13, 3.6, 6.3). Default 5,000 (X6) |
| migration_stage_batch_size | INT | N | Maximum records per `migration-records:stage` call |
| master_batch_max_items | INT | N | Maximum items per `{resource}:upsert-batch` call (11.5). Default 500 |
| contains_max_range_months | SMALLINT | N | Longest date range accepted together with a `contains` filter on ledger resources (09, 2.4.1). Default 12 |
| export_storage | TEXT(300) | Y | Reference of the external object storage for exports (credentials outside the core, 12, 7.1) |
| export_expiry_days | SMALLINT | N | Default 14. Not applied to the export that backs a tenant deletion: it does not expire before `waiting_until` + 7 days (E1, 12). No separate setting |
| deletion_waiting_days | SMALLINT | N | Waiting period of a tenant deletion, default 30, minimum 7 (12, 6.3) |
| dsr_response_days | SMALLINT | N | Response period for data subject requests, default 30 (12, 5.1) |
| anonymisation_dry_run_weeks | SMALLINT | N | Weeks of scheduled DRY_RUN before the job executes automatically. Default 4 (12, 4.3). `anonymisation_first_run_at` (TS, S) records the first scheduled run |
| outbox_app_event_retention_days | INT | N | Days after which app events (not audit events) may be purged, once all active consumers have read past them. Default 30 (15, 5.4) |
| outbox_consumer_max_lag_days | INT | N | A consumer inactive longer than this no longer blocks the purge and gets `EVENT.POSITION_EXPIRED` on return. Default 90 (15, 5.4) |
| extension_max_definitions | SMALLINT | N | Maximum active extension definitions per entity type and namespace. Default 50 (15, 3.5). Name confirmed (14, section 12 follow-up, 17, section 5). |
| anchor_pending_deadline | SMALLINT | N | Hours after which a `chain_anchor` that is still PENDING is reported as a warning by `chains:verify` (02, 9.5, A2). Default 24 (17, section 5). A warning only, the chain does not fail |

### 5.3 `company_group` (M)

PK: (`tenant_id`, `group_id`). Unique (`tenant_id`, `group_code`).

| Column | Type | Null | Notes |
|---|---|---|---|
| group_code, name | CODE, TEXT(100) | N | |
| group_currency | CODE | N | IAP. FK `ref_currency`. Group amount currency (ADR-017) |
| inventory_cost_scope | CODE | N | COMPANY or WAREHOUSE. Cost per company, item and batch, or per warehouse, item and batch (10, 3.10, ADR-097). IAP once any member company has a stock document. Default COMPANY |
| active | BOOL | N | |

### 5.4 `company` (M)

PK: (`tenant_id`, `company_id`). Unique (`tenant_id`, `company_code`).

| Column | Type | Null | Notes |
|---|---|---|---|
| company_code | CODE | N | |
| legal_name | TEXT(200) | N | Legal entity name. Organisation data, not personal data |
| legal_registration_id, legal_registration_scheme | TEXT(50), CODE | Y | Seller legal registration (EN 16931, 12, 9.2) |
| electronic_address, electronic_address_scheme | TEXT(100), CODE | Y | Seller endpoint (EN 16931) |
| country_code | CODE | N | IAP. Home country. FK `ref_country` |
| local_currency | CODE | N | IAP |
| reporting_currency | CODE | Y | IAP. Second currency, e.g. EUR for SAF-T (ADR-020) |
| fiscal_calendar_id | ID | N | IAP once a fiscal year is opened (04) |
| status | CODE | N | DRAFT, ACTIVE, INACTIVE. DRAFT until `companies:activate` passes the readiness check (11.3) |
| package_code, package_version | CODE, TEXT | Y | Last applied configuration package (informational, truth in `applied_config_package`) |

Group membership is not a column (see 5.6). A company must be ACTIVE for any posting.

### 5.5 `company_setting_version` (V)

PK: (`tenant_id`, `company_id`, `effective_from`). Resolution: the row with the highest `effective_from` not later than the command time (as `posting_window`).

| Column | Type | Null | Notes |
|---|---|---|---|
| tax_rounding_mode | CODE | N | PER_DOCUMENT or PER_LINE (ADR-027) |
| tax_amount_tolerance | AMT | N | Allowed difference between supplied and computed tax (ADR-027), in local currency |
| rate_override_tolerance_pct | RATE | N | Allowed deviation of a supplied rate from the table rate (ADR-019) |
| soft_close_requires_reason | BOOL | N | Whether posting into SOFT_CLOSED needs a free-text reason in the request (R1: false) |
| einvoice_rule_set | CODE | Y | The fixed rule set of the e-invoice completeness check (12, 10, ADR-221, 22, delta 3). A closed list defined by the core release, one value per supported edition of EN 16931 (first value `EN16931_2017_A1`). Null: the check is not run and the data set reports `completeness` = NOT_CHECKED. An unknown code is rejected with `CONFIG.EINVOICE_RULE_SET_UNKNOWN`. A new edition is a new code and a new version row from the date the company moves. Package-supplied values come with the edition decision (open) |

### 5.6 `group_membership` (V)

PK: (`tenant_id`, `company_id`, `valid_from`). Columns: `group_id`, `valid_to` (set by the next version or by leaving). A company belongs to one group at a time (no overlap, checked on insert). Posted data is never restated on a change (ADR-018). **Cost scope rule (10, Q10, 3.10):** a new membership row is rejected with `CONFIG.COST_SCOPE_CONFLICT` if the new group's `inventory_cost_scope` differs from the scope in force for a company that already has a stock document. A change of `company_group.inventory_cost_scope` itself stays IAP (5.3).

Business units are dimension values (section 6.5), not organisation tables.

### 5.7 `company_order_setting_version` (V), thinned (S1, S2, S3)

Defined in 11, 3.1. The scope review (14, section 8) reduces it to the settings below. The matching tolerances of earlier drafts (`price_tolerance_pct`, `price_tolerance_amount_lc`, `quantity_tolerance_pct`) and the table `purchase_tolerance_override` are removed (S1, ADR-155). PK: (`tenant_id`, `company_id`, `effective_from`). Resolution as `company_setting_version` (5.5).

| Column | Type | Null | Notes |
|---|---|---|---|
| default_over_tolerance_pct, default_under_tolerance_pct | RATE | N | Defaults for the line tolerance of new order lines (L2 on the line). The only tolerance the core enforces: invoice and fulfilment quantity not above ordered plus the line tolerance. Initially 0 |
| match_mode_default | CODE | N | THREE_WAY or TWO_WAY, default for new purchase order lines (11, 3.1). Kept (S1, 14 section 10) |
| allow_invoice_before_receipt | BOOL | N | Allows TWO_WAY purchase lines for stock items (11, 3.1, 11 Q2). Kept (S1, 14 section 10) |
| sales_invoice_mode | CODE | N | **Default CORE_BUILDS for new companies** (S2, 14 section 10). CORE_BUILDS (the core selects the invoice lines by `invoice_policy`) or APP_SUPPLIES (the app sends complete lines with order and delivery references, and price and discount per line, stored beside the order price without a core price rule). Versioned with `effective_from`: a switch applies to invoices created from that time, earlier invoices are unaffected. Same validation, tax determination and posting in both modes (S2, ADR-156, 06, 7.4) |
| auto_reserve | BOOL | N | Default **false**. When true, a sales order line for a stocked item is reserved at creation, BEST_EFFORT only. Stricter modes (REQUIRED) are removed. Reservation records and commands stay as shared data (S3, ADR-157) |

`match_mode_default` and `allow_invoice_before_receipt` of 11, 3.1 stay in this table (decided, 14 section 10, Q13). The core enforces only references, quantity limit, control total and the GR/IR split. The initial version row of a new company sets `sales_invoice_mode` = CORE_BUILDS, so the readiness check of 11.3 passes without an explicit choice.

## 6. Chart of accounts and dimensions

### 6.1 `gl_account` (M)

PK: (`tenant_id`, `company_id`, `account_id`). Unique (`tenant_id`, `company_id`, `account_no`).

| Column | Type | Null | Notes |
|---|---|---|---|
| account_no | CODE | N | Company's account number. Mutable only while the account has no postings (Q5) |
| name | TEXT(100) | N | |
| account_type | CODE | N | IAP. ASSET, LIABILITY, EQUITY, REVENUE, EXPENSE. Balance sheet = ASSET, LIABILITY, EQUITY. P&L = REVENUE, EXPENSE (drives revaluation and year-end, 05) |
| account_group | CODE | Y | Free grouping code for reporting |
| standard_account_code | CODE | Y | Mapping to the standard reporting structure for SAF-T (ADR-011) |
| open_item_mode | CODE | N | IAP. NONE, CONTROL, GL_OPEN_ITEM (ADR-040) |
| currency_code | CODE | Y | IAP. If set, only this transaction currency may be posted |
| revalue_flag | BOOL | N | Balance sheet accounts only (05). Must be false for P&L accounts. Rejected when the account has mandatory dimension rules (05 Q3). **Rejected on the account of the role `RETAINED_EARNINGS`** (17, section 5, 05 5.1): setting `revalue_flag` on it, and assigning the role to an account with `revalue_flag`, reject with `CONFIG.REVALUE_FLAG_ON_RETAINED_EARNINGS` (11.2) |
| tax_code_rule | CODE | N | MANDATORY, OPTIONAL, FORBIDDEN (ADR-029) |
| default_tax_class_id | ID | Y | Account default tax class (section 8.1) |
| posting_blocked | BOOL | N | Blocks new postings, keeps the account readable |
| tax_account | BOOL | N | S. Set by the system when the account becomes a VAT account of a tax code version or the account of a `VAT_SETTLEMENT` role. Never reset. Such accounts accept only tax lines and settlement lines (08, 6.7) |
| valid_from, valid_to | DATE | N, Y | Posting date must lie within. Both not IAP |
| active | BOOL | N | False hides the account from new use |

### 6.2 `account_dimension_rule` (M)

PK: (`tenant_id`, `company_id`, `account_id`, `dimension_type_id`). Column `rule`: MANDATORY, OPTIONAL, FORBIDDEN (P11, ADR-010). Absence of a row means OPTIONAL. A change applies to new postings and to L2 changes from then on. It does not affect posted data (Q8).

### 6.3 `account_role_assignment` (V)

Maps a role the core understands to an account.
PK: (`tenant_id`, `company_id`, `assignment_id`). Unique (`tenant_id`, `company_id`, `role_code`, `currency_key`, `counterpart_key`, `vat_registration_key`, `effective_from`) where the keys are the value or a fixed marker for null. `vat_registration_key` is the nullable `vat_registration_id` (D5, ADR-149): without it two registrations could not each have a `VAT_SETTLEMENT` row.

| Column | Type | Null | Notes |
|---|---|---|---|
| role_code | CODE | N | From the fixed list below |
| currency_code | CODE | Y | Per-currency override. Null = default |
| counterpart_company_id | ID | Y | Intercompany roles only |
| vat_registration_id | ID | Y | `VAT_SETTLEMENT` only: the VAT registration the role belongs to (08, 6.2). Part of the unique key |
| account_id | ID | N | FK `gl_account`. Must satisfy the role's account rules |
| effective_from | TS | N | UTC |

#### Role table (D5, ADR-149)

The authoritative list of all account roles of the core, with the **required open-item mode** of the account (`gl_account.open_item_mode`, 6.1). Two tables resolve roles: `account_role_assignment` (this section, roles that do not depend on item group, warehouse, item or partner) and `account_determination` (10, 3.9, roles that do). A role is written only if the account satisfies its row in this table, else `CONFIG.ROLE_ACCOUNT_RULE_VIOLATED` with the role code, the account and the violated rule in `params`. `open_item_mode` and `account_type` are IAP, so an assigned account cannot later leave the rule.

| Role | Resolved by | Used by | Account type rule | Required open-item mode |
|---|---|---|---|---|
| `ROUNDING` | `account_role_assignment`, per currency optional | Rounding lines per currency (principle P4 of 00) | P&L | NONE |
| `TAX_ROUNDING` | `account_role_assignment` | ADR-027 | P&L | NONE |
| `FX_REALISED` | `account_role_assignment` | Clearing, ADR-038 | P&L | NONE |
| `FX_UNREALISED_GAIN`, `FX_UNREALISED_LOSS` | `account_role_assignment`, per currency optional | Revaluation, ADR-050 | P&L | NONE |
| `RETAINED_EARNINGS` | `account_role_assignment` | Year-end, ADR-055 | EQUITY, no mandatory dimension rules, **no `revalue_flag`** (`CONFIG.REVALUE_FLAG_ON_RETAINED_EARNINGS`) | NONE |
| `IC_RECEIVABLE`, `IC_PAYABLE` | `account_role_assignment`, per counterpart company | Intercompany, ADR-016 | Balance sheet | NONE |
| `VAT_SETTLEMENT` | `account_role_assignment`, per VAT registration | `vat-returns:settle`, ADR-028 and ADR-080 | Balance sheet | NONE |
| `MIGRATION_CLEARING`, `HISTORY_RESULT` | `account_role_assignment` | Migration (13, 4.1). Required before a migration batch can be opened | Balance sheet, no mandatory dimensions | NONE |
| `GR_IR`, `DROPSHIP_CLEARING` | `account_determination` | Goods receipt and dropship clearing, matched by supplier invoices (10, 7, 11) | Per 10 | **GL_OPEN_ITEM** (no partner) |
| `GR_IR_WRITE_OFF` | `account_determination`, same keys as the other determination roles (10, 3.9, 11, 3.3; catch-all rule allowed) | Counter account of `gr-ir:write-off` and its reversal `gr-ir:reverse-write-off` (11, 7.5, 02, 3). Separate from `PRICE_VARIANCE`: price difference and write-off stay separate in the ledger (17, section 5) | Per 11 (not fixed by D5) | NONE |
| `AR_CONTROL`, `AP_CONTROL` | `account_determination` | Receivable and payable control accounts of invoices (11, 3.3) | Per 11 | **CONTROL** |
| `INVENTORY`, `COGS`, `PRICE_VARIANCE`, `ADJUSTMENT_GAIN`, `ADJUSTMENT_LOSS`, `NEG_STOCK_VARIANCE`, `LANDED_COST_CLEARING`, `STOCK_TRANSFER_CLEARING`, `REVENUE`, `PURCHASE_EXPENSE` | `account_determination` | Inventory and order journals (10, 7, 11, 3.3) | Per 10 and 11 | NONE |

**Role rules (F8).** (1) `open_item_mode` and `account_type` of an account cannot change while a role row references it (the change rejects with `CONFIG.REFERENCED`), also where the field is not yet IAP. (2) One account has one role: an account may appear under several rows of the same `role_code` (per currency, counterpart or registration), never under two role codes (`CONFIG.ROLE_ACCOUNT_RULE_VIOLATED`, rule `ONE_ROLE_PER_ACCOUNT`). (3) `currency_code`: a role row with `currency_code` set needs an account whose `gl_account.currency_code` is null or the same currency. A default row (null currency) needs an account with null `gl_account.currency_code`. (4) `tax_account`: an account with `tax_account` = true accepts only the role `VAT_SETTLEMENT`. The system sets `tax_account` on the account of a `VAT_SETTLEMENT` row (6.1). Every other role rejects a `tax_account` account. Violations reject with `CONFIG.ROLE_ACCOUNT_RULE_VIOLATED`. The same rules apply to the roles of `account_determination` (10, 3.9).

The earlier statement of 10 that "GR_IR must be GL_OPEN_ITEM, others NONE" is replaced by this table. The account type rules of the determination roles stay with 10 and 11 (this table lists only what D5 fixes). Role codes beyond release 1 need an ADR.

Resolution at command time: the most specific row (currency, counterpart and VAT registration match) with the highest `effective_from` not later than now. No match: the command that needs the role is rejected with `CONFIG.ROLE_NOT_ASSIGNED` and the role code in `params`. Companies cannot be activated without the roles needed for the enabled features (11.3).

### 6.4 `group_chart_account` (M) and `account_group_mapping` (V)

Group reporting needs a common chart (ADR-011, ADR-017).
- `group_chart_account`: PK (`tenant_id`, `group_id`, `group_account_id`). Unique code. `name`, `account_type`, `active`.
- `account_group_mapping` (V): PK (`tenant_id`, `company_id`, `account_id`, `valid_from`). `group_account_id`, `valid_to`. Mapping of a company account to a group account, effective-dated. One group account per company account at a time.

### 6.5 Dimensions

#### `dimension_type` (M)

PK: (`tenant_id`, `dimension_type_id`). Unique (`tenant_id`, `type_code`).

| Column | Type | Null | Notes |
|---|---|---|---|
| type_code, name | CODE, TEXT(100) | N | |
| kind | CODE | N | IAP. STANDARD or FREE |
| standard_role | CODE | Y | IAP. For STANDARD only: COST_CENTER, PROJECT, PROFIT_CENTER, BUSINESS_UNIT. Maps to the typed columns on `journal_line` (ADR-034). The four standard types exist in every tenant and cannot be deactivated |
| active | BOOL | N | |

#### `dimension_value` (M)

PK: (`tenant_id`, `dimension_value_id`). Unique (`tenant_id`, `dimension_type_id`, `value_code`).

| Column | Type | Null | Notes |
|---|---|---|---|
| dimension_type_id | ID | N | IAP |
| company_id | ID | Y | Restriction. Null = all companies of the tenant. IAP once referenced |
| value_code, name | CODE, TEXT(100) | N | |
| parent_value_id | ID | Y | Optional rollup hierarchy within the type. No cycles |
| valid_from, valid_to | DATE | N, Y | Posting date must lie within (P11, ADR-010) |
| active | BOOL | N | |

A journal line's typed dimension column must reference a value whose type has the matching `standard_role`, whose `company_id` is null or equals the posting company. A change of `company_id` to a different company is rejected once the value is referenced.

## 7. Currency and exchange rates

### 7.1 `tenant_currency` (M)

PK: (`tenant_id`, `currency_code`). `active`. A currency must be listed here and active to be used as transaction, local, group or reporting currency in the tenant.

### 7.2 `exchange_rate` (M, usage-locked)

ADR-019: rates are configuration, the core never fetches them, apps or integrations load them through the API.
PK: (`tenant_id`, `rate_id`). Unique (`tenant_id`, `rate_type`, `from_currency`, `to_currency`, `valid_from`).
Index: (`tenant_id`, `rate_type`, `from_currency`, `to_currency`, `valid_from`) for the lookup.

| Column | Type | Null | Notes |
|---|---|---|---|
| rate_type | CODE | N | SPOT, CLOSING or VAT (VAT reporting amounts of tax lines, 08, 5.1a). IAP |
| from_currency, to_currency | CODE | N | IAP |
| valid_from | DATE | N | IAP. Rate applies from this date until the next |
| rate | RATE | N | Mutable only while `first_used_at` is null |
| source | TEXT(100) | Y | Provider or feed name |
| first_used_at | TS | Y | S. Set by the posting and revaluation commands when a rate row is used in a posted record. Clearing performs no rate lookup and never sets it (D9, ADR-153). After that the row can be neither changed nor deactivated (ADR-019) |

Derivation (Q9 decided), in this order, for the pair (A to B), rate type and date:
1. **Direct:** the row for A to B valid at the date, with the fallback of ADR-019.
2. **Inverse:** if only B to A exists, rate = 1 / rate(B to A).
3. **Cross:** if neither exists and `tenant_setting.pivot_currency` is set, rate = rate(A to pivot) x rate(pivot to B), each obtained by steps 1 and 2.
4. None found: `RATE.RATE_NOT_FOUND`.
Derived rates are rounded to the scale of the `RATE` type and stored as used on the line. Every contributing table row gets `first_used_at` set. The fallback window applies to each contributing row separately.

An error is corrected by creating a new row only if no row exists for that date. If the wrong rate is already used, correction happens through reversal and repost of the documents, never by changing the rate. Because rates are usage-locked rows, they are an M table with a special guard, and are listed in `master_change_log`.

## 8. Tax configuration

### 8.1 `tax_class` (M)

PK: (`tenant_id`, `tax_class_id`). Unique code. `name`, `active`. A free list defining classes for items and services (e.g. standard goods, food, books, domestic services). The item master supplies the class through `item.tax_class_id` (10, 3.3), see 8.5.

### 8.2 `tax_return_box` (M)

PK: (`tenant_id`, `country_code`, `return_type`, `box_code`). `name`, `box_group`, `active`. The boxes of the country's VAT returns (ADR-028), per return type (STANDARD, EC_LIST, OSS, IOSS, 08, 6.1). Loaded from the country package.

### 8.3 `tax_code` (M) and `tax_code_version` (V)

`tax_code`: PK (`tenant_id`, `company_id`, `tax_code_id`). Unique (`tenant_id`, `company_id`, `code`). Columns: `code`, `description`, `tax_type` (IAP: OUTPUT, INPUT, EXEMPT, ZERO_RATED), `vat_registration_id` (IAP, FK `company_vat_registration`, 08), `active`.

`tax_code_version`: PK (`tenant_id`, `company_id`, `tax_code_id`, `valid_from`). The version in force at the posting date is stored on every tax line (`tax_code_version_id` in 02).

| Column | Type | Null | Notes |
|---|---|---|---|
| valid_from, valid_to | DATE | N, Y | |
| rate | RATE | N | Percent. 0 for EXEMPT and ZERO_RATED |
| vat_account_id | ID | N | VAT account for the tax line (ADR-026). Balance sheet account |
| reverse_charge | BOOL | N | True: the code produces an output and a deductible tax line (08, 5.2) |
| en16931_vat_category | CODE | Y | VAT category of the standard for e-invoices (S, Z, E, AE, K, G, O, L, M). Required for ZERO_RATED, EXEMPT, reverse charge and intra-EU codes (12, 9.2) |
| exemption_reason_code, exemption_reason_text | CODE, TEXT(200) | Y | Exemption reason of the e-invoice VAT breakdown |
| reverse_charge_account_id | ID | Y | Required when `reverse_charge`: account of the deductible (INPUT) side |
| ec_list_category | CODE | Y | GOODS, SERVICES, TRIANGULATION. Marks the code for the EC Sales List |
| supply_kind | CODE | Y | GOODS or SERVICES. Used by OSS and IOSS return rows |
| evidence_required_type | CODE | Y | Evidence type expected for lines with this code (08, 5.3) |
| deductible_pct | RATE | N | Fixed deductible share of the input tax in percent, 0 to 100, default 100 (ADR-203, 19, 3.1). Only for `tax_type` INPUT (including reverse-charge codes). A version of another type with a value other than 100, a `deduction_key` or a `deduction_cap` is rejected with `CONFIG.DEDUCTION_ON_NON_INPUT_CODE` |
| deduction_key | CODE | Y | Names a deduction key of the code's VAT registration (8.6). The share is `deductible_pct` times the key rate at the tax date. Unknown key: `CONFIG.DEDUCTION_KEY_UNKNOWN` |
| deduction_cap | CODE | N | NONE (default) or SUPPLIED_AMOUNT: the caller supplies the deductible tax amount per line (06, 5.2, `deductible_tax_amount_tc`) |

`tax_code_version_box` (V): PK (`tenant_id`, `company_id`, `tax_code_id`, `valid_from`, `line_side`, `return_type`, `box_code`). Columns `line_side` (PRIMARY, or COUNTER for the deductible side of a reverse-charge pair, 08), `return_type` (STANDARD, EC_LIST, OSS, IOSS: part of the key because `tax_return_box` is keyed (`country_code`, `return_type`, `box_code`), 8.2), `amount_kind` (BASE or TAX), `sign` (+1 or -1). Maps a tax code version to return boxes (ADR-028). **Side derivation:** COUNTER is valid only on a version with `reverse_charge` = true of a code with `tax_type` INPUT, and denotes the deductible INPUT side (account `reverse_charge_account_id`). Every other row is PRIMARY.

### 8.4 `tax_determination_rule` (V) and alternatives

Determines the tax code for a taxed base line (ADR-026). The key set is extensible, because the final design (origin, destination, account, material) is open (Q1): every key column is nullable and null means "any". Adding a key later means a new nullable column, and existing rules keep their meaning.

`tax_determination_rule`: PK (`tenant_id`, `company_id`, `rule_id`, `valid_from`).

| Column | Type | Null | Notes |
|---|---|---|---|
| valid_from, valid_to | DATE | N, Y | Compared with the posting date |
| transaction_type | CODE | Y | SALES or PURCHASE. Derived from the document type (AR types: SALES, AP types: PURCHASE) |
| origin_country | CODE | Y | Country the goods or service come from. Resolved from the line, else the document, else the default by direction: company country for sales, partner country for purchases (Q1 decided) |
| destination_country | CODE | Y | Country the goods or service go to. Same resolution. Default: partner country for sales, company country for purchases |
| origin_eu | BOOL | Y | Derived key (ADR-200): true if the resolved origin country had EU membership at the `tax_date`, false if not, from `ref_country_eu_membership` (4). A country without a valid membership row is false. Null means any. The company's own country is a member or not like any other |
| destination_eu | BOOL | Y | Derived key (ADR-200): the same for the resolved destination country |
| partner_vat_status | CODE | Y | REGISTERED or NOT_REGISTERED, from `partner_tax_registration` for the **destination country** of the line. An absent registration row or an expired one (`valid_to` before the tax date) is NOT_REGISTERED (F1) |
| account_id | ID | Y | The account being posted (the base line's account) |
| tax_class_id | ID | Y | Material or service class: the line's class, else the account's default class |
| priority | SMALLINT | N | Explicit rank: the lowest value wins first (ADR-192). Equal priority is settled by specificity, then by `rule_id` |
| tax_code_id | ID | N | Result |

Matching algorithm:
1. Candidates are rules valid at the posting date whose non-null key columns all equal the request's values (the company is implied). `origin_eu` and `destination_eu` are compared with the EU membership of the resolved origin and destination country at the tax date (ADR-200). A rule with only an EU flag therefore covers every member state, including the company's own country: a domestic rule with explicit countries and a lower `priority` value is the way to exclude it.
2. The candidate with the lowest `priority` wins (explicit rank, ADR-192). Equal priority: the candidate with the most non-null key columns wins (most specific). Still equal (possible for rules with different key sets of the same size): the lowest `rule_id` wins (F1). The result is always deterministic.
3. Equal priority on the same key set is rejected when the rule is written (`CONFIG.OVERLAPPING_RULE`). Rules with different key sets and equal priority are not checked against each other and are settled by specificity and then by the `rule_id` tie-break of step 2.
4. No candidate: `TAX.NO_RULE_MATCHED` (06).

`tax_determination_alternative` (V): PK (`tenant_id`, `company_id`, `rule_id`, `valid_from`, `alt_tax_code_id`). The tax codes a caller may supply instead of the determined one, for rule versions (ADR-026).

**Partnerless documents (F1).** A document without `partner_id` has no partner country. For a taxed base line it needs an explicit `origin_country` and `destination_country` (line or document), else the command is rejected (`TAX.ORIGIN_DESTINATION_REQUIRED`, 06). The direction defaults below apply only when a partner exists.

Request fields that feed the keys: header `partner_id` (for partner country and VAT status), document type (transaction type), line `tax_class_id` (optional), base line `account_id`. Origin and destination are optional fields on the document and on each line (line overrides document, then defaults by direction, 06 and Q1). The resolved origin, destination, partner VAT status and the derived EU flags are stored on `journal_line_tax`.

### 8.5 Tax class on the line

The class used for determination is the line's `tax_class_id` if supplied. Order and invoice lines for items default it from `item.tax_class_id` (10, 3.3) when the order or invoice command builds the line. If no class is supplied, the account's `default_tax_class_id` applies. It is stored on `journal_line_tax` (delta to 02). If none exists, rules with a null `tax_class_id` can still match.

### 8.6 Deduction keys and rates (ADR-203, 19)

A deduction key is a named variable deduction rate of one VAT registration (pro rata, estimate, sector, area). The core does not compute it: it is loaded like an exchange rate (A1 of 19).

`vat_deduction_key` (M): PK (`tenant_id`, `company_id`, `registration_id`, `key_code`). Columns `kind` (PRO_RATA, ESTIMATE, SECTOR, AREA), `description`, `active`, `row_version`. The country package creates the keys its codes use (18), the company creates others. `kind` is IAP once a rate of the key has been used (`CONFIG.DEDUCTION_KEY_REFERENCED`). Deactivation is rejected while an active code version names the key.

`vat_deduction_rate` (V, insert only): PK (`tenant_id`, `company_id`, `registration_id`, `key_code`, `valid_from`).

| Column | Type | Null | Notes |
|---|---|---|---|
| valid_from, valid_to | DATE | N, Y | Compared with the `tax_date` of the tax line (19, A2). V rules of 2.3 apply: a version ends by inserting a row with `valid_to`, never by update |
| rate_pct | RATE | N | 0 to 100 |
| rate_status | CODE | N | PROVISIONAL or FINAL. Informational in release 1, the hook of the year-end adjustment (19, 9) |
| basis_note | TEXT(200) | Y | For example the period of the turnover the rate rests on. Free text, no personal data |
| created_by, created_at | TEXT(100), TS | N | |

Resolution: the row valid at the tax date. None: the posting is rejected with `TAX.DEDUCTION_RATE_NOT_FOUND`. A rate that a posted line used is never changed. A correction is a new version from a later date, and retroactive corrections are an adjustment (19, 9), not a rate edit. Changes are audited in `master_change_log` (key) and by the version rows (rate). Companies with a code that names a key need a rate valid at the first tax date: readiness warning (11.3).

## 9. Partners and banking

### 9.1 `business_partner` (M)

PK: (`tenant_id`, `partner_id`). Unique (`tenant_id`, `partner_code`).

| Column | Type | Null | Notes |
|---|---|---|---|
| partner_code | CODE | N | Business key, no personal data allowed in it |
| partner_kind | CODE | N | IAP. ORGANISATION or PERSON |
| is_customer, is_supplier | BOOL | N | A role can be added any time. Removed only if no posted record uses it |
| ic_company_id | ID | Y | Partner that is a company of the tenant (intercompany). Used by IC accounts and elimination |
| tenant_credit_limit | AMT | Y | Credit limit of the customer across all companies of the tenant, in `tenant_credit_limit_currency`. Informational, never enforced. Compared with the sum of exposures of all companies by `credit-exposures:query` with scope TENANT (11, 8.1). For partners of kind PERSON, the credit limits (this one and the company-level one) are personal data: omitted without `read.pii`, cleared by the anonymisation job |
| tenant_credit_limit_currency | CODE | Y | Required when `tenant_credit_limit` is set |
| status | CODE | N | ACTIVE, BLOCKED, ANONYMISED |
| retention_until | DATE | Y | S. The latest `retention_until` of any record referencing the partner (12, 4.2). Maintained by a **batch job**, not by posting or order commands, so no command locks or writes this row (D3, ADR-147). It is a derived maximum and is recomputed in full by the job. Its change is an S change without `master_change_log` row (2.3, D2) |
| retention_floor | DATE | Y | S. Stored minimum of `retention_until` (E2). Set only by `retention:extend` (12), which is limited to record classes that store a date. The batch recomputation of `retention_until` takes the maximum of the derived value and `retention_floor`, so it can never shorten an extension. Its change is an S change with its own audit event, no `master_change_log` row |
| restriction_status | CODE | N | S. NONE or RESTRICTED. Set only by data subject request commands (12, 5.3). Default NONE |
| anonymised_at | TS | Y | Set by the retention job (P8) |

Ledger rows reference only `partner_id` (P8). Nothing in this table is personal data.

### 9.2 `business_partner_pii` (M, anonymisable)

PK: (`tenant_id`, `partner_id`). 1:1 with `business_partner`.

| Column | Type | Null | Notes |
|---|---|---|---|
| legal_name, trade_name | TEXT(200) | N, Y | |
| address_line_1, address_line_2, postal_code, city | TEXT | Y | |
| country_code | CODE | N | Also drives the partner country for tax rules (not erased: country is kept as a non-identifying value in the tax registration table) |
| email, phone, contact_person | TEXT | Y | |
| electronic_address, electronic_address_scheme | TEXT(100), CODE | Y | Buyer endpoint for e-invoices (12, 9.2) |
| legal_registration_id | TEXT(50) | Y | Legal registration id (12, 9.2) |

The legal snapshot of a document is copied from here at posting (ADR-064). The retention job overwrites the personal columns and sets `business_partner.status` = ANONYMISED. Changes to these fields appear in `master_change_log` with `is_pii` = true and without values (`old_value`, `new_value` null).

### 9.3 `partner_company_relation` (M)

PK: (`tenant_id`, `partner_id`, `company_id`).

| Column | Type | Null | Notes |
|---|---|---|---|
| is_customer, is_supplier | BOOL | N | Role for this company |
| default_payment_terms_id | ID | Y | FK `payment_terms` |
| credit_limit | AMT | Y | Credit limit of the customer in this company, in `credit_limit_currency`. Informational for the core: it is never enforced, apps decide. Compared with exposure by `credit-exposures:query` (11, 8) |
| credit_limit_currency | CODE | Y | Required when `credit_limit` is set |
| default_currency | CODE | Y | |
| payment_block, dunning_block | CODE | Y | Defaults copied to new open items (L2 on the item) |
| status | CODE | N | ACTIVE or BLOCKED. BLOCKED rejects new postings with this partner in this company (`MASTER.PARTNER_INACTIVE`) |

### 9.4 `partner_tax_registration` (M)

PK: (`tenant_id`, `partner_id`, `country_code`, `valid_from`).

| Column | Type | Null | Notes |
|---|---|---|---|
| vat_status | CODE | N | REGISTERED or NOT_REGISTERED in that country. Input to tax determination |
| vat_id | TEXT(30) | Y | Not personal data for organisations. Treated as PII for `partner_kind` = PERSON (Q3) |
| valid_to | DATE | Y | |

VAT ID validation (e.g. against a national service) is an app concern.

### 9.5 `partner_bank_account` (M, anonymisable) and `company_bank_account` (M)

`partner_bank_account`: PK (`tenant_id`, `partner_id`, `bank_account_id`). `iban`, `bic`, `account_holder`, `currency_code`, `active`. Personal data, anonymised with the partner. Referenced by `journal_line_open_item.bank_account_id` (L2).

`company_bank_account`: PK (`tenant_id`, `company_id`, `bank_account_id`). Columns:

| Column | Type | Null | Notes |
|---|---|---|---|
| account_scheme | CODE | N | Default `IBAN`. `IBAN` is defined by the core. Other values are defined by packages (for example `DK_FIK` for a creditor number of the Danish joint payment slip, `DK_GIRO` for a giro account) and listed in the app content of the package. The core stores the value and does not read the catalogue (ADR-223) |
| iban | TEXT | Y | Nullable since ADR-223. Required when `account_scheme` is `IBAN`, forbidden otherwise |
| account_identifier | TEXT(50) | Y | The identifier of an account that has no IBAN, for example an 8 digit creditor number. Required when `account_scheme` is not `IBAN`, forbidden otherwise. The core checks only that it is present, up to 50 characters, and unique (below). The format and any check digit belong to the package and the app |
| (unchanged) | | | `bic`, `currency_code`, `gl_account_id` (the bank GL account, a creditor number row names the GL account of the real bank account behind it), `payment_means_code` (default code for credit transfer, a row of another scheme carries the code of its means, for example 93 for a FIK creditor number), `use_on_invoices` (12, 9.2) and `active` keep their definitions |

Rules (ADR-223): the combination of `account_scheme` and `iban` or `account_identifier` is unique per company. A company has one row per IBAN, creditor number or giro account, so the same real account can appear in two rows with the same `gl_account_id`. A violation of the required and forbidden rules above is `CONFIG.BANK_ACCOUNT_IDENTIFIER_INVALID`. Bank statement lookup by IBAN (22, 5.1) finds IBAN rows only. Organisation data. The link supports bank statement matching in apps.

### 9.6 `payment_terms` (M)

PK: (`tenant_id`, `payment_terms_id`). Unique code.

| Column | Type | Null | Notes |
|---|---|---|---|
| code, description | CODE, TEXT(100) | N | |
| baseline_rule | CODE | N | DOCUMENT_DATE, POSTING_DATE, END_OF_MONTH |
| net_days | SMALLINT | N | |
| discount_days | SMALLINT | Y | |
| discount_pct | RATE | Y | |
| active | BOOL | N | |

Terms are informational for the core: due dates are supplied by the caller (06, 5.2). Changing terms never affects posted items. Apps use terms to propose due dates.

## 10. Audit and packages

### 10.1 `master_change_log` (E)

Generic and append-only. Covers all M tables (not V tables, whose rows are the history).
PK: (`tenant_id`, `change_id`). Indexes (`tenant_id`, `entity_type`, `entity_id`, `changed_at`) and (`tenant_id`, `correlation_id`) (the latter for audit events by correlation id, 09, 6). The audit stream behind `audit-events:query` is the outbox (15, 7, K3): every change here has an outbox event with `is_audit` = true that refers to the log row, and this table stays the field-level evidence (K2 not adopted).

| Column | Type | Null | Notes |
|---|---|---|---|
| tenant_id, change_id | ID | N | |
| change_set_id | ID | N | Groups all fields of one command |
| entity_type | CODE | N | Table name. Includes `EXTENSION_VALUE` and `EXTENSION_DEFINITION` (15, 3.5, 3.6) |
| entity_id | TEXT(100) | N | Canonical text of the key |
| action | CODE | N | CREATE, UPDATE, DEACTIVATE, REACTIVATE, ANONYMISE (retention job, 12, 4.3), CLEAR (extension values cleared at anonymisation, E3, E4, 15) |
| app_id | ID | Y | The app that made the change (extension entries, 15). Null for other changes (E3) |
| field_name | CODE | Y | Null for CREATE (the whole row is the new state) and DEACTIVATE |
| is_pii | BOOL | N | True for personal-data fields |
| old_value, new_value | TEXT(500) | Y | Always null when `is_pii` |
| reason_code | CODE | Y | |
| changed_by | TEXT(100) | N | |
| changed_at | TS | N | |
| correlation_id | TEXT(100) | N | |
| command_log_id | ID | N | |

No UPDATE or DELETE grants. The log is not part of a hash chain (it is operational audit, not ledger evidence). Anonymisation does not touch it, because it never held the values.

### 10.2 `applied_config_package` (E)

PK: (`tenant_id`, `applied_id`). Columns: `company_id` (null for tenant-level content), `package_code`, `package_version`, `applied_at`, `applied_by`, `command_log_id`, `content_digest` (SHA-256 of the package content), `summary` (counts per table). Insert only.

## 11. Contract for configuration and master data

The envelope, idempotency, error object and VALIDATE mode of 06 apply unchanged. Config and master commands are synchronous, atomic and idempotent, written in P7 together with their audit rows, and recorded in `command_log`.

### 11.1 Generic operations

| Operation | For pattern | Payload | Notes |
|---|---|---|---|
| `{resource}:create` | M, V | Fields of the row, optional `legacy_ref` | V: first version. Duplicate code: `CONFIG.DUPLICATE_CODE` |
| `{resource}:update` | M | Key, `expected_version`, changed fields, optional `legacy_ref` | Conflict: `CONFIG.ROW_VERSION_MISMATCH` with the current version. IAP fields: `CONFIG.IMMUTABLE_AFTER_POSTING` |
| `{resource}:create-version` | V | Key, new validity (`valid_from` or `effective_from`), changed fields | Guards of 2.3 |
| `{resource}:deactivate`, `{resource}:reactivate` | M | Key, `expected_version`, `reason_code` | Referenced mandatory roles block deactivation |
| `{resource}:get`, `:list` | all | Key or filters. `as_of` date or time for V tables | Read contract details in the read-API domain |

Resource names follow the tables (`gl-accounts`, `tax-codes`, `business-partners`, and so on). `business_partner_pii`, relation and registration rows are updated through their parent resource, in one atomic command with one `change_set_id`.

**`legacy_ref` and `upsert` (13, 3.5).** A create or update request may carry `legacy_ref` (`source_system`, `legacy_id`). The command then writes the `legacy_id_map` row in the same transaction. With `upsert` = true the map decides: an unknown legacy id creates, a known one updates the changed fields and requires `expected_version` (single create and update, see the rule below), identical content is a no-op reported as `unchanged`. Without `upsert`, a request whose legacy id already exists follows 13, 3.5 (update under the `expected_version` rules). **Rule (Q14, decided, 14 section 11):** on single `create` and `update`, `upsert` = true on a row that already exists, found by `legacy_ref` or by key, requires `expected_version` and rejects with `CONFIG.ROW_VERSION_MISMATCH` on a mismatch. Without a legacy match, `upsert` inserts and `expected_version` is not allowed. `upsert` without `legacy_ref` is rejected (`SCHEMA.REQUIRED_FIELD_MISSING`). Only the batch variant of 11.5 is exempt from `expected_version`, because the batch is the authority. The batch variant of 11.5 is the same call in a wrapper. Not every resource has a legacy mapping: the map covers the entity types of 13, 3.5.

**Write permissions (D10, ADR-154, Q12).** Create, update, create-version, deactivate, reactivate and the batch variants need exactly one permission by resource. The assignment below is the decided map (14 section 10, Q12), including `exchange_rate` and `payment_terms` under `write.config`:

| Permission | Resources |
|---|---|
| `write.config` | `tenant_setting`, `company_group`, `company` (not activation), `company_setting_version`, `group_membership`, `gl_account`, `account_dimension_rule`, `account_role_assignment`, `group_chart_account`, `account_group_mapping`, `tenant_currency`, `exchange_rate`, `tax_class`, `tax_return_box`, `tax_code` and versions, `tax_determination_rule` and alternatives, `company_vat_registration`, `account_determination`, `company_order_setting_version`, `external_code_mapping`, `business_calendar` and versions and exceptions, `company_calendar_assignment`, `payment_terms`, `vat_deduction_key`, `vat_deduction_rate` |
| `write.master` | `dimension_type`, `dimension_value`, `business_partner` with PII, relations, tax registrations and bank accounts, `company_bank_account`, `item_group`, `item` and unit conversions, `item_company`, `warehouse`, `warehouse_location`, `item_warehouse`, `item_tracking`, `item_partner` |
| `config.apply` | `config-packages:apply` (11.4). The package may touch any resource, the single permission replaces the per-resource ones for that command |
| `company.activate` | `companies:activate` (11.3) |
| `history.open` | `fiscal-years:open-history` (13, 4.2) |
| (reverse commands, 06) | `sales-credit-notes:reverse`, `purchase-credit-notes:reverse`, `sales-returns:reverse`, `purchase-returns:reverse`, `gr-ir:write-off`, `gr-ir:reverse-write-off`, `landed-costs:reverse`, `vat-returns:reverse-settlement` are transaction commands. They need the permission of the original command (06, 8.2; `vat.settle` for `vat-returns:reverse-settlement`), not `write.config` or `write.master` |
| `period.admin` (F3) | `fiscal_calendar`, `posting_window`, `number_series`, `number_series_assignment` (04): `fiscal-calendars:*`, `posting-windows:create`, `number-series:create` and `:update`, `number-series-assignments:create` (06, 7.3). They are **not** under `write.config` |
| `apps.manage` | `app_registration`, `app_namespace`, `app_event_subscription` (15): `apps:register`, `:update`, `:deactivate`, `app-namespaces:create`, `app-subscriptions:set` (06, 7.4) |
| (other specs) | `retention_rule` (`retention.manage`, 12) |

Reads use `read.config` (09). The permission table is fixed (06, 8). A resource not listed above is added to this map by the spec that introduces it.

### 11.2 Error codes (area `CONFIG`)

`DUPLICATE_CODE`, `ROW_VERSION_MISMATCH`, `IMMUTABLE_AFTER_POSTING`, `REFERENCED`, `VALID_FROM_NOT_LATER`, `VALID_FROM_IN_CLOSED_PERIOD`, `OVERLAPPING_RULE`, `ROLE_NOT_ASSIGNED`, `ROLE_ACCOUNT_RULE_VIOLATED` (an account that does not satisfy the role table of 6.3, including the required open-item mode), `REVALUE_FLAG_ON_RETAINED_EARNINGS` (new, 17, section 5: `revalue_flag` on the account of the role `RETAINED_EARNINGS`, either by setting the flag or by assigning the role, 6.1), `INVALID_REFERENCE`, `CURRENCY_NOT_ENABLED`, `DIMENSION_COMPANY_MISMATCH`, `HIERARCHY_CYCLE`, `COMPANY_NOT_READY`, `COST_SCOPE_CONFLICT` (a `group_membership` change or `inventory_cost_scope` change that would give a company with stock documents a different cost scope, 10, Q10), `DEDUCTION_ON_NON_INPUT_CODE` (a tax code version that is not of type INPUT with a `deductible_pct` other than 100, a `deduction_key` or a `deduction_cap`, 8.3), `DEDUCTION_KEY_UNKNOWN` (a code version names a key that does not exist for its registration, 8.3), `DEDUCTION_KEY_REFERENCED` (a change of `kind` of a key whose rates were used, 8.6).
### 11.3 Company readiness and activation

`companies:check-readiness` (read-only) and `companies:activate` verify that a company can post:

| Check | Source |
|---|---|
| Local, reporting and group currency enabled in `tenant_currency` | 7.1 |
| Fiscal calendar assigned, at least one fiscal year open (04) | 04 |
| Current `company_setting_version` exists | 5.5 |
| Number series assigned for each document type in use (04), including the order management document types when orders are used (11, 3.2) | 04 |
| A current `company_order_setting_version` exists when orders are used (11, 3.1, 5.7), with `sales_invoice_mode` set | 11 |
| Roles: `ROUNDING` for each currency in use or a default, `TAX_ROUNDING`, `FX_REALISED`, `FX_UNREALISED_GAIN` and `_LOSS`, `RETAINED_EARNINGS`; IC roles per counterpart if intercompany is used; `VAT_SETTLEMENT` per VAT registration if VAT is used | 6.3 |
| Account rules of role accounts satisfied, including the required open-item mode of the role table | 6.3 |
| Account determination: every role needed by the enabled features (inventory, orders, dropship) has at least a catch-all rule (all keys null) | 10, 3.9 |
| At least one active `company_vat_registration` if VAT codes are used | 08, 4.1 |
| Migration roles `MIGRATION_CLEARING` and `HISTORY_RESULT` assigned when a migration batch is open or about to be opened | 13, 4.1 |
| Role `GR_IR_WRITE_OFF` resolvable in account determination (at least a catch-all rule) when `gr-ir:write-off` is used (warning, not error: the command is rejected with `CONFIG.ROLE_NOT_ASSIGNED` if the role is missing when it runs) | 6.3, 11, 7.5 |
| Retention rule for each record class of the company (warning, not error) | 12, 4.1 |
| Tax determination covers every active tax code (warning, not error) | 8.4 |
| A rate valid at the first tax date for every deduction key that an active tax code version names (warning, not error) | 8.6 |
| `einvoice_rule_set` set in the current `company_setting_version` when the company has a seller `electronic_address` (warning, not error) | 5.5, 12, 10 |

The result lists every failed check with the code `CONFIG.COMPANY_NOT_READY`. A company in DRAFT status accepts configuration but no postings. `companies:check-readiness` needs `read.config`, `companies:activate` needs `company.activate` (06, 8). Apps, extensions, calendars and `item_partner` are not checked (3.4).

### 11.4 `config-packages:apply`

Applies a versioned package (JSON document) to a company or the tenant: creates accounts, dimension types and values, tax classes, return boxes, tax codes with versions, determination rules, role assignments, payment terms. Rules:
- Create-only and idempotent per `package_code` and `package_version` for a company. Re-applying the same version changes nothing. A new version creates only objects that do not exist.
- The package is validated as a whole in VALIDATE mode first (all references resolve, role accounts satisfy rules, no overlapping rules).
- One audit record in `applied_config_package`, and `master_change_log` rows (CREATE) for M objects.
- Packages are maintained outside the data model (platform or partner repository). Format, ownership, apply semantics (inputs, items, user changes win) and the verification gate are specified in 18-country-packages (DRAFT, ADR-193 to ADR-197 PROPOSED). Q10 is closed by it once accepted.
- Permission `config.apply` (06, 8). Country packages may also create business-calendar exceptions (public holidays, 16, 3.2).

### 11.5 Batch variants for master data (13, Q12 decided)

For the resources `gl-accounts`, `business-partners` (with PII, company relations, bank accounts and tax registrations as one object), `items` (with unit conversions and company and warehouse settings), `dimension-values`, `tax-codes` (with versions), `warehouses`, `payment-terms`, `exchange-rates`: `{resource}:upsert-batch`.

| Aspect | Rule |
|---|---|
| Payload | `items[]` (at most `tenant_setting.master_batch_max_items`, default 500), each with `item_ref` (unique in the call), its own `idempotency_key`, `legacy_ref` (required for `upsert`), and the object in the shape of `{resource}:create` |
| Semantics | A wrapper of independent atomic commands (as `post-batch`, ADR-062): items are processed sequentially in array order, each in its own transaction with its own pipeline and `master_change_log` rows, no batch transaction |
| Upsert | By `legacy_ref` through `legacy_id_map` (13, 3.5): unknown creates, known updates changed fields (no `expected_version` needed, the batch is the authority), identical content is a no-op with `unchanged`. The same `legacy_ref` and `upsert` fields exist on the single-record create and update, where `expected_version` applies to an existing row (decided rule, 11.1, Q14). `upsert` without `legacy_ref` is rejected |
| Response | `summary` and per-item status (`CREATED`, `UPDATED`, `UNCHANGED`, `REJECTED`, `FAILED`, `NOT_PROCESSED`) with errors as in 06 |
| Dependencies | An item may refer to objects created earlier in the same call or in earlier calls (by `legacy_ref`). The ETL orders the loads |
| Immutable fields | IAP fields (`IMMUTABLE_AFTER_POSTING`) reject on update as usual |
| Permission | The permission of the resource (11.1): `write.config` or `write.master`. VALIDATE mode supported |

## 12. What the transaction specs need from this layer

| Needed by | Needs | Table |
|---|---|---|
| 02 `journal_line.account_id`, rules on post | Account, open-item mode, tax code rule, default tax class, validity, blocked flag | `gl_account` |
| 02 dimension columns and rules | Dimension types, values with validity and company restriction, account rules | `dimension_type`, `dimension_value`, `account_dimension_rule` |
| 02 open item `partner_id`, `payment_terms_id`, `bank_account_id` | Partner, terms, partner bank account | 9.x |
| 02 legal snapshot (ADR-064) | Partner name and address at posting | `business_partner_pii` |
| 02 tax lines, ADR-026 | Tax code versions, rules, alternatives, return boxes | 8.x |
| 02 rates and tolerances (ADR-019) | Rates, fallback days, override tolerance | `exchange_rate`, `tenant_setting`, `company_setting_version` |
| 02 rounding and tax rounding mode | Role accounts, rounding mode | 5.5, 6.3 |
| 03 realised FX | `FX_REALISED` role | 6.3 |
| 04 posting window, series, calendar | Already specified | 04 |
| 05 revaluation | `revalue_flag`, FX gain/loss roles, closing rates | 6.1, 6.3, 7.2 |
| 05 year-end | `RETAINED_EARNINGS` role, account types for P&L | 6.1, 6.3 |
| 03, 05 IC | `IC_RECEIVABLE`, `IC_PAYABLE` per counterpart, partner `ic_company_id` | 6.3, 9.1 |
| 06 contract | Currency scale per currency (`minor_units`) | `ref_currency` |
| 06 permissions | Write permission per resource | 11.1 |
| 08 VAT | Registrations, `VAT_SETTLEMENT` per registration | `company_vat_registration` (08, 4.1), 6.3 |
| 02, 06, 19 partial deduction | Deduction attributes of the code version, keys and key rates | 8.3, 8.6 |
| 02, 06, 08 EU scenarios | `origin_eu` and `destination_eu` rule keys, EU membership by date | 8.4, 4 |
| 10 inventory, 11 orders | Role table with required open-item mode, account determination catch-all check, `item.tax_class_id`, order settings | 6.3, 11.3, 8.5, 5.7 |
| 12 compliance | Retention rules and external code mappings as configuration, personal data inventory | 3.2, 9, 10 |
| 13 migration | Roles, `legacy_id_map`, `legacy_ref` and `upsert`, `tenant.purpose`, batch variants | 6.3, 3.2, 11.1, 11.5 |
| 15 platform | Registrations, namespaces, extension definitions, outbox, settings | 3.3, 5.2 |
| 16 planning data | `item_partner`, business calendars | 3.2, 11.1 |

## 13. Deltas to earlier specs

### 13.1 Deltas applied at v0.1 (with this spec's first draft)

| Spec | Change |
|---|---|
| 02 `journal_line_tax` | New column `tax_class_id` (ID, nullable, L1): the class used in determination (8.5). Included in the line hash |
| 06 `journal-documents:post` | New optional line field `tax_class_id`. New optional `origin_country` and `destination_country` on the document (defaults) and on the line (override) |
| 02 `journal_line_tax` | Also new L1 columns `origin_country`, `destination_country`, `partner_vat_status` (resolved determination inputs) |
| 06 section 2.4 | Add error area `CONFIG` (11.2) |
| 02 and 06 partner reference | `partner_id` references `business_partner`. Blocked or inactive partners reject (`MASTER.PARTNER_INACTIVE`) |
| 05 | Gain/loss, retained earnings and FX roles are rows of `account_role_assignment`. 05 Q7 (one account per company with currency overrides) is exactly the `currency_code` column of the role table |

These deltas are applied in 02 and 06 with this spec.

### 13.2e ADR-200 and ADR-203 (2026-10-14)

Applied in this spec: `deductible_pct`, `deduction_key`, `deduction_cap` on `tax_code_version` (8.3), `origin_eu` and `destination_eu` as determination keys with the matching rule and the stored flags (8.4), the new section 8.6 with `vat_deduction_key` and `vat_deduction_rate`, the overview rows (3.2), `write.config` for the two resources (11.1), the error codes (11.2) and a readiness warning (11.3). Carried by other specs: 02 (flags and deduction columns on `journal_line_tax`, `hash_version` 2), 06 (field, pipeline, codes), 08, 09, 11, 13 (see 19, section 11).

### 13.2d Propagation pass v0.6 (17, section 5)

One row. Applied in this spec: role `GR_IR_WRITE_OFF` (open-item mode NONE, resolved by `account_determination` with the keys of the other roles, 6.3), `tenant_setting.anchor_pending_deadline` (hours, default 24, 5.2) and `extension_max_definitions` confirmed (5.2), `CONFIG.REVALUE_FLAG_ON_RETAINED_EARNINGS` (6.1, 6.3, 11.2), readiness warning for `GR_IR_WRITE_OFF` when `gr-ir:write-off` is used (11.3), `gr-ir:reverse-write-off` in the permission map (11.1). Confirmed present: `master_change_log` with `app_id`, action CLEAR and the correlation index (10.1), `business_partner.retention_floor` (9.1), the F1 tie-break (highest specificity, then lowest `priority`, then lowest `rule_id`) and NOT_REGISTERED for an absent or expired registration in 8.4 (the tax text is in 07, 8.4, and VAT use of it in 08), `item_partner` key with `company_key` 0 = tenant row (3.2). Carried by other specs: 11 (role `GR_IR_WRITE_OFF` instead of `PRICE_VARIANCE` in 7.5, Q26), 06 (`gr-ir:reverse-write-off`, new code), 10 (role list of 3.9).

### 13.2c Fix pass 4 v0.5 (17, section 4)

Applied in this spec: X3 (`sales_invoice_mode`, `auto_reserve`, `effective_from`), X4 (lock tuple), X6 (`migration_document_max_lines`, `item_partner` inside `write.master`, permissions for the new commands), F3 (overview rows for 04 tables, `period.admin` row of 11.1), F4 to F6 (key notes in 3.2), F7 (end-version, 2.1), F8 (role rules, 6.3), F1 (tie-break, NOT_REGISTERED, partnerless documents, 8.4), `tax_code_version_box` key with `return_type` and COUNTER derivation (8.3), A3 (`effective_from` DATE of series assignments, 2.2), D8 (mention in 5.2), E1 (export expiry note, 5.2), E2 (`retention_floor`, 9.1), E3 (`app_id`, CLEAR, 10.1), `journal_line_tax_evidence` removed from the configuration list (3.2). Carried by other specs: 04 (A3 DATE, `history_until_period`), 11 and ERD (names), 12 (`retention_floor`, export expiry), 15 (`app_id`), 16 (key notes F4 to F6).

### 13.2b Third re-baseline pass v0.4 (14, section 11)

Applied in this spec: 11.1 (`vat-settlements:reverse` renamed `vat-returns:reverse-settlement`, permission `vat.settle`), the `upsert` rule confirmed in 11.1, 11.5 and Q14 (existing row requires `expected_version`, no legacy match inserts, no `legacy_ref` rejects, only the batch variant is exempt). Carried by other specs: 13, 3.5 (same `upsert` wording), 08 (rename of the settlement reversal command and `VAT.NOT_SETTLED`).

### 13.2a Second re-baseline pass v0.3 (14, section 10)

Applied in this spec: 5.7 (`match_mode_default`, `allow_invoice_before_receipt`, `sales_invoice_mode` default CORE_BUILDS), 11.1 (permission map confirmed, reverse commands, `upsert` rule, proposed at v0.3 and decided at v0.4), 3.3 (one VAT_RETURN chain per company), 11.5 and Q12 to Q14. Carried by other specs: 08 `vat-returns:reverse-settlement` (named `vat-settlements:reverse` at v0.3) and the VAT_RETURN chain without scope, 10 `landed-costs:reverse` and `cost_source` per movement line, 11 the two credit note reverse commands, 13 the proposed `MIGRATION` codes and the `upsert` wording (3.5).

### 13.2 Re-baseline v0.2 (14, sections 8 and 9)

Changes made in this spec, and what the other specs must carry:

| Spec | Change |
|---|---|
| 07 (this spec) | Table overview completed (3.2, 3.3). Role table with required open-item mode (6.3, D5). `account_role_assignment` unique key with `vat_registration_key` (D5). `exchange_rate.first_used_at` without clearing (D9). `retention_until` by batch job (D3). S columns rule (2.3, D2). Write permissions (11.1, D10). `legacy_ref` and `upsert` on single create and update (11.1). Thinned `company_order_setting_version` with `sales_invoice_mode` and `auto_reserve` (5.7, S1 to S3). Readiness checks added (11.3). `tenant_setting` gains the outbox and extension settings (5.2). Item tax class applied (8.5). `COST_SCOPE_CONFLICT` code and membership rule (5.6, 11.2). 11.4 before 11.5. `master_change_log` correlation index (10.1) |
| 06 | Fixed permission table and the command-to-permission map (06, 8). Error areas of all specs (06, 2.4). `chain_head` (K1) |
| 08 | `vat_return_chain_head` becomes one `chain_head` row per company with empty `scope_key` (K1, 14 section 10, supersedes the return-type scope). Settlement reversal only before filing and only through the owning command (D1, D9). 08, 6.2 role per registration is covered by the new unique key |
| 10 | The role-to-mode check is `CONFIG.ROLE_ACCOUNT_RULE_VIOLATED` against the role table of 6.3 (10, 3.9 already refers to it) |
| 11 | `company_order_setting_version` thinned (5.7). `purchase_tolerance_override` and `purchase.match_override` removed. Reservation default off. `sales-invoices:create` honours `sales_invoice_mode`. IC permissions `ic.order`, `ic.deliver`, `ic.invoice` (06, Q14 decided). New `sales-credit-notes:reverse` and `purchase-credit-notes:reverse` (06, 5.3). `match_mode_default` and `allow_invoice_before_receipt` stay (5.7) |
| 12, 13 | 12 owns `retention_rule`. 13 uses `history.open` for `fiscal-years:open-history` (D10) and names its `MIGRATION` error codes (06, Q16). `migration_commit_max_documents` is replaced by `migration_commit_chunk_documents` (5.2) |
| 15, 16 | Tables listed in 3.3 and 3.2. Settings of 5.2. No readiness check |

## 14. Assumptions

| Ref | Item |
|---|---|
| A1 | Account structure in R1 is flat per company with a group code and a standard code. No account hierarchy tables. Financial statement structures are a reporting concern |
| A2 | Dimension rules are current-state. Re-validation of L2 changes and new postings use the rules in force at command time, not at the posting date |
| A3 | A tax code belongs to one company. A tenant with many companies duplicates codes through packages. Shared tax codes across companies are not modelled |
| A4 | Payment terms are informational: the core stores terms and checks references, but due dates always come from the caller |
| A5 | Exchange rates are tenant-level. Currency pairs are stored directionally. Inverse and cross rates are derived by the core in the lookup (7.2, Q9 decided), or loaded explicitly, in which case the direct row wins |
| A6 | The platform tenant table and the reference tables (`ref_currency`, `ref_country`, `ref_country_eu_membership`, `ref_unit`) are outside tenant isolation by design, as is `tenant_deletion_certificate` (12). All other tables are subject to row-level isolation (P7). The list of P7 exceptions in 00 and ADR-069 is to be completed with `ref_unit` and `tenant_deletion_certificate` |
| A7 | Extension values, app registrations and the outbox are platform tables of the tenant. They carry `tenant_id` and are isolated like all others |
| A8 | The permission split `write.config` and `write.master` is by resource and not by pattern (V or M) (11.1) |

## 15. Open questions

| # | Question |
|---|---|
| Q1 | **Decided (R1 shape):** origin and destination are ISO countries stated at line level with document-level defaults, and, if neither is given, defaults by direction (sales: company country to partner country, purchases the reverse). The resolved values and the partner VAT status used are stored on `journal_line_tax` (02). Jurisdiction below country is not in R1, it can be added later as another nullable rule key. **Remaining for the VAT domain:** how reverse charge, intra-EU supplies and OSS/IOSS (deferred in ADR-026) use these keys, and whether the key set needs more than the current six. |
| Q2 | **Decided:** deactivation only, no hard delete through the API. Physical deletion only by the retention job and tenant deletion. |
| Q3 | **Decided:** `business_partner_pii` and `partner_bank_account` are anonymised for every partner kind at retention end. `partner_tax_registration.vat_id` is anonymised only for partners of kind PERSON. Partner code, roles and `ic_company_id` are not personal data. |
| Q4 | **Decided:** unique partner code per tenant only. No merge. A duplicate is blocked and both ids keep their history. Detection and any display roll-up are app concerns. |
| Q5 | **Decided:** `account_no` is mutable only while the account has no postings. Renumbering later means a new account and a transfer posting. |
| Q6 | **Decided:** keep both validity dates and the `posting_blocked` and `active` flags. |
| Q7 | **Decided:** one group-chart mapping per company account, effective-dated. Local statutory reporting uses `standard_account_code`. |
| Q8 | **Decided:** dimension rules apply as of command time. No versioned rule table. |
| Q9 | **Decided:** direct rates are loaded, the core derives the inverse and, through the tenant pivot currency, cross rates (7.2). |
| Q10 | **Decided:** a maintained, versioned set of packages per country, applied through `config-packages:apply`. The format and the maintenance process are specified before the first onboarding. |
| Q11 | **Decided:** a company is deactivated, never deleted, merged or moved between tenants. `INACTIVE` keeps all data and reads and rejects postings. Leaving the platform is covered by tenant-level export and deletion (compliance domain). |
| Q12 | **Decided (14 section 10, D10):** the split of 11.1 stands, including `exchange_rate` and `payment_terms` under `write.config`. |
| Q13 | **Decided (14 section 10, S1):** `match_mode_default` and `allow_invoice_before_receipt` stay in `company_order_setting_version` (5.7). |
| Q14 | **Decided (14 section 11):** on single create and update, `upsert` on an existing row requires `expected_version`, without a legacy match it inserts, `upsert` without `legacy_ref` is rejected, only the batch variant (11.5) is exempt. 13, 3.5 must carry the same wording. |
| Q15 | **Decided (14, D5):** the role table of 6.3 is authoritative for the required open-item mode of all roles, including the roles of `account_determination`. Unique key of `account_role_assignment` includes the nullable VAT registration. |
| Q16 | **Decided (14, D10, S1 to S3):** see 11.1 and 5.7. The extension and outbox settings of 5.2 are named by 15; `extension_max_definitions` is confirmed. |

## 16. ADRs raised by this spec (now in 01-adr-log, which is authoritative)

**ADR-065: Configuration versioned, master data current-state.** V tables are insert-only with validity, M tables are single rows with `row_version` and a generic `master_change_log`. Personal-data fields are logged without values. Refines P2.

**ADR-066: Account-role table.** A fixed role list maps to accounts per company, optionally per currency or counterpart, effective-dated. Companies cannot be activated without required roles. *Amended by ADR-149 (D5): the role table states the required open-item mode of every role, and the unique key includes the nullable VAT registration.*

**ADR-067: Tenant-wide dimension values with optional company restriction.** Refines ADR-010.

**ADR-068: Tenant-level business partner with roles and separate personal data.** `business_partner`, `business_partner_pii`, per-company relation. Refines ADR-008.

**ADR-069: Global reference tables.** Currencies, countries and EU membership are read-only and carry no `tenant_id`. The single, documented exception to ADR-002. *Amended: `ref_unit` (10) is also global, and `tenant_deletion_certificate` (12) is outside tenant keys (A6).*

**ADR-070: Configuration packages.** Versioned packages applied through the config API replace template tables. Refines ADR-011.

**ADR-071: `row_version` concurrency for config and master data.** Complements ADR-060 for human-edited objects.

**ADR-072: Tax class from account default with line override, extensible determination keys.** Tax rule keys are nullable columns (null = any), most specific rule wins, overlaps rejected on write. Refines ADR-026. *Amended by ADR-200: the derived keys `origin_eu` and `destination_eu` are added from `ref_country_eu_membership` (8.4).*

**ADR-073: No hard delete in configuration and master data.** "Delete" is deactivation. Physical deletion only by the retention job and tenant deletion.

## 17. ADRs of the re-baseline that shape this spec (in 01-adr-log, which is authoritative)

| ADR | Decision | Where used here |
|---|---|---|
| ADR-146 (D2) | S columns changed only by their owning system command, without log rows, each with its own event. `retention_until` is an S column | 2.3, 9.1 |
| ADR-147 (D3) | One lock order. `retention_until` maintained by a batch job | 9.1, 3.3 |
| ADR-149 (D5) | Role table with required open-item mode per role. Unique key of `account_role_assignment` with nullable `vat_registration_id` | 6.3 |
| ADR-153 (D9) | Clearing does not touch `first_used_at` | 7.2 |
| ADR-154 (D10) | Fixed permissions, `write.config`, `write.master`, `config.apply`, `company.activate`, `history.open` | 11.1 |
| ADR-155 (S1) | Matching thinned to line tolerance. `purchase_tolerance_override` removed | 3.2, 5.7 |
| ADR-156 (S2) | Versioned `sales_invoice_mode` per company (validity column `effective_from`, X3) | 5.7 |
| ADR-157 (S3) | `auto_reserve` per company, default off, BEST_EFFORT only | 5.7 |
| ADR-158 (K1) | One `chain_head` table | 3.3 |
| ADR-159 (K3) | The outbox is the single audit and feed stream. The detail logs stay the evidence | 2.3, 10.1 |
| ADR-137 to ADR-139 | Extension attributes, change feed, app registration | 3.3, 5.2 |
| ADR-142, ADR-144 | `item_partner`, business calendars | 3.2 |
