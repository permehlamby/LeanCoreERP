# Compliance: Retention, Personal Data, Exports and Tenant Lifecycle

Status: DRAFT v0.6 for review (propagation pass of 2026-10-13, decisions of 17, sections 4 and 5, see 12.3). v0.5 (fix pass 4: E1 to E4, E8, retention class corrections, hold and request wording, see 12.2). Builds on 00-design-principles v0.4, ADR-001 to ADR-174 and specs 02 to 11. Revised on 2026-10-13 for the scope review (14, sections 8 and 9) and the platform specs 15 and 16: D2 (`retention_until` is an S column, ADR-146), D3 (batch update of the partner maximum, ADR-147), D7 (read log of exports, ADR-151), K1 (`chain_head`, ADR-158), K3 (audit through the outbox, ADR-159), extension values (15, ADR-137, ADR-138), rehearsal tenants (13 Q10), migrated records (13 Q15). S5 is unchanged: the data subject request table stays. Second re-baseline (14, section 10): the anonymisation job recomputes the partner `retention_until` before acting (4.3, Q17), the snapshot overwrite is a logged legal L1 exception (4.3), the outbox purge count is reported in the run record (4.5, Q16, decided in 14 section 11), export read log wording aligned to "per job and field group" (7.1, D7), field groups approved (Q18). Third re-baseline (14, section 11): `outbox_events_purged` decided (4.5, 4.6, Q16), the SYSTEM log entry of `order_address_snapshot` goes to `order_change_log` (4.3, Q19), the correction of the partner maximum emits the new event `retention.recomputed` (4.3, Q19), Q16 to Q19 closed. Main references: P5 (tamper evidence), P7 (tenant export and deletion), P8 (GDPR and retention coexist via separation), P15 (EU regulatory readiness), ADR-008, ADR-064, ADR-068, ADR-090, ADR-092, ADR-046, 07 (Q3 personal data, retention config), 09 (read audit, exports deferred here).
Scope: retention rules and the anonymisation job, legal holds, data subject requests and processing restriction, the inventory of personal data, asynchronous export jobs (full tenant export, audit package, SAF-T data set, e-invoice data set, partner data export), issued-document evidence, tenant suspension, export and deletion, and the permissions and reads for all of it.
Out of scope: generation of national SAF-T or e-invoice files and their transport (apps, P15), legal advice on retention periods (each period is configuration to be verified per country), backup systems, key management for export encryption (customer or platform), identity management.
Conventions as in 02, section 1, pattern letters as in 07, section 2, envelope and errors as in 06, reads as in 09.

**Caution on law.** This spec defines mechanisms. It states no retention period, SAF-T element or e-invoice rule of any country as fact. Retention periods, SAF-T variants and e-invoice mappings are configuration and country packages, verified per country (P15, 07 Q10).

## 1. Decisions taken

| Topic | Decision |
|---|---|
| End of retention | The job **anonymises personal data** and nothing else. Ledger, stock and order rows hold no personal data and stay, with intact chains, until the tenant is deleted |
| Legal hold | A hold has a scope: tenant, company, partner or a set of documents. The job skips everything a hold covers |
| Data subjects | The core stores data subject requests with decisions and outcomes, and a processing restriction on the partner. Anonymisation happens at the earliest permitted date |
| Exports | Asynchronous jobs: full tenant export, audit package, SAF-T data set, e-invoice data set, and (derived) a partner data export |
| Artifacts | Open, neutral files (CSV or NDJSON) plus a JSON manifest, written to external object storage. The core stores job, manifest and storage references, never bulk data |
| Tenant deletion | Needs an acknowledged full export, no legal hold, two approvers and a waiting period. Statutory retention that has not ended can be overridden only by a recorded acknowledgement. A deletion certificate is kept |
| SAF-T | The data set follows the **OECD base structure**. National variants are added later as country packages |
| E-invoice | The EN 16931 attributes are added to configuration and master data, so the data set is complete from the core alone |

## 2. Overview

### 2.1 Tables

| # | Table | Pattern | Role |
|---|---|---|---|
| 1 | `retention_rule` | V | Retention period per company, record class and country |
| 2 | `legal_hold`, 3 `legal_hold_document` | M, E | Holds and their document sets |
| 4 | `data_subject_request` | M | Requests of persons and their handling |
| 5 | `anonymisation_run`, 6 `anonymisation_run_item` | E + S | Runs of the retention job and what they did |
| 7 | `export_job`, 8 `export_artifact` | E + S | Asynchronous exports and their files |
| 9 | `external_code_mapping` | V | Mapping of internal codes to external standards (SAF-T, EN 16931, national) |
| 10 | `invoice_issue_record` | E | Evidence of an issued invoice document (file reference and hash) |
| 11 | `tenant_deletion_request` | E + S | Approval workflow for tenant deletion |
| 12 | `tenant_deletion_certificate` | E (platform level) | Certificate that survives the tenant |

Changed tables (section 11): `document_legal_snapshot`, `order_address_snapshot`, `business_partner`, `business_partner_pii`, `company`, `company_bank_account`, `tax_code_version`, `item`, `sales_order`, `tenant`.

### 2.2 Principle: personal data lives in a few places

Ledger rows reference partners by id (P8). The inventory of personal data (section 3) is deliberately short. Everything the anonymisation job touches is listed there, and nothing else is personal data by design. Actor identities in audit trails are opaque identifiers whose mapping to real persons lives outside the core (Q3).

## 3. Inventory of personal data

| Location | Fields | Class | Cleared by |
|---|---|---|---|
| `business_partner_pii` | Names, address, contact, electronic address, legal registration id | Partner personal data (all partner kinds, 07 Q3) | Partner anonymisation |
| `partner_bank_account` | IBAN, BIC, holder | Partner personal data | Partner anonymisation |
| `partner_tax_registration` | `vat_id` | Personal for PERSON only | Partner anonymisation |
| `business_partner` | Credit limits (tenant), for PERSON | Personal for PERSON only | Partner anonymisation |
| `partner_company_relation` | Credit limit, for PERSON | Personal for PERSON only | Partner anonymisation |
| `document_legal_snapshot` | Name, address, VAT id, electronic address, registration id (L1*) | Document-level personal data | Snapshot anonymisation at `retention_until` |
| `order_address_snapshot` | Name, address, contact | Document-level personal data | Snapshot anonymisation at `retention_until` |
| Search columns | Normalised copies of the personal fields above (09, 2.4.1) | Personal data | Together with their source column |
| `extension_value` of definitions with `is_pii` = true | Values of app-defined attributes on partners and other records (15, 3.5, 3.6) | Personal data, only where the app flagged the definition | Cleared **at anonymisation of the partner**, on the partner and on the records that reference it (orders, order lines, open items, 4.3), never at deactivation (E3, E4). Included in the data subject export (7.2). Never copied to `outbox_event` payloads and logged in `master_change_log` without content (15, 3.6) |
| Opaque actor ids | `posted_by`, `changed_by`, `actor` and similar | Pseudonymous | Not cleared. The mapping outside the core is deleted (Q3) |
| `read_access_log` | Partner ids (nullable), export job ids, actors | Pseudonymous | Retention of the log, record class `READ_LOG` (09, Q6, 4.6) |
| `outbox_event` | Ids, event types, actor references | Pseudonymous. Contains no personal data by design (15, Q3) | Retention by class (4.6). Nothing is anonymised |
| Free text fields | `header_text`, `line_text`, `note`, `reason_text` | **Not personal data by design** | Not cleared. Apps must not place personal data there (Q4) |

Personal data is never part of any hash, any chain, `master_change_log` values, or the codes and keys of any record.

## 4. Retention

### 4.1 `retention_rule` (V)

PK: (`tenant_id`, `company_id`, `rule_id`, `effective_from`). Resolution: for a record class, the row with the highest `effective_from` not later than the moment the record is created. A rule change affects only records created afterwards (Q13).

| Column | Type | Null | Notes |
|---|---|---|---|
| record_class | CODE | N | `LEDGER` (journal documents, lines, open items, clearings, revaluation, year-end), `VAT_RETURN`, `STOCK`, `ORDER`, `FULFILMENT`, `INVOICE_ISSUE`, `LEGAL_SNAPSHOT`, `ADDRESS_SNAPSHOT`, `AUDIT_LOG` (change logs, `master_change_log`, `order_change_log` and the audit events of the outbox, 4.6), `READ_LOG` (`read_access_log`, 4.6). The `command_log` is **not** in `AUDIT_LOG`: it is purged after its own tenant setting `command_log_retention_days` (06, Q2, 07) and has no record class (4.6) |
| country_code | CODE | N | Country whose law the rule reflects (normally the company country). A company may have rules for several countries |
| retention_years | SMALLINT | N | Whole years |
| start_event | CODE | N | `FISCAL_YEAR_END` (retention runs from the end of the fiscal year of the record, default) or `RECORD_DATE` |
| legal_basis | TEXT(200) | Y | Reference to the law or contract. Informational |
| effective_from | TS | N | Insert-only |

Of the company's rules for a class, the **longest** retention applies if several countries are configured (conservative). `retention_until` of a record is computed at creation from the rule and stored on records that need it (4.2). Company readiness (07, 11.3) warns if a record class has no rule.

**Consistency rule for snapshots (17).** The retention of a legal snapshot is **never shorter than the longest retention of its referencing records**. At creation, `retention_until` of a `document_legal_snapshot` is the later of (a) the `LEGAL_SNAPSHOT` rule and (b) the `retention_until` the `LEDGER` rule gives the journal document it belongs to (and the `INVOICE_ISSUE` rule for its issue records). In the same way an `order_address_snapshot` is at least as long as the `ORDER` and `FULFILMENT` retention of its order. Company readiness warns if the `LEGAL_SNAPSHOT` rule is shorter than the `LEDGER` rule or the `ADDRESS_SNAPSHOT` rule shorter than the `ORDER` rule. `retention:extend` on the referencing class does not shorten anything, and a later extension of a referencing record's retention is followed by `retention:extend` of the snapshot class (13).

A return line with disposition `SCRAP` (11) creates no stock document: its stock retention does not apply and the return is retained as a `FULFILMENT` record. There is no `STOCK` record to retain for it.

**Migrated records (13 Q15).** The `retention_until` of a migrated record is computed from the same rules, with the start date of the record as it was in the legacy system: history summaries from the end of their own legacy fiscal year, migrated open items and orders from their legacy document dates where supplied. Legal snapshots and address snapshots created by the migration count from the migration date. Where the legacy record date is not supplied, the migration date is the start (13 delta row for 12). This is one rule for all record classes, the rules of this section apply per record class.

### 4.2 Where `retention_until` is stored

`retention_until` is an **S column** everywhere it is stored (14 D2, ADR-146). It is set at creation from the rule, and later changed by system commands only: `retention:extend` (section 13) for the snapshots and the partner maximum, and the maintenance of the partner maximum. These changes are legal in hard-closed periods, are not logged in `change_log`, and each is an audit event of the outbox (`retention.extended`, or `retention.recomputed` for a correction of the partner maximum by recomputation, 4.3; 15, section 6; 4.6). The column is outside every hash. Rule changes alone never alter stored dates (Q13).

**Evidence of retention changes (E8).** A change of a stored `retention_until` has no chain of its own. The evidence is the outbox audit event (`retention.extended`, `retention.recomputed`) and the run record of `retention:extend`. **Note for ADR-146:** the passage "own chained event" is amended to "S changes without a chain of their own use the outbox audit event as evidence".

**Partner floor (E2).** `business_partner` also stores `retention_floor` (S, DATE, null): a stored minimum of the partner maximum. `retention:extend` on the partner maximum sets `retention_floor` (and `retention_until`) to the new date, and the recomputation of 4.3 never produces a `retention_until` earlier than `retention_floor`: the recomputed value is the later of the latest `retention_until` of the referencing records and `retention_floor`. The floor only increases. `retention:extend` is limited to the classes that store a date (13).

| Record | Column | Lock |
|---|---|---|
| `document_legal_snapshot` | `retention_until` (exists, 02). Reclassified from L1 to **S** (D2). The personal columns of the snapshot stay L1* | S |
| `order_address_snapshot` | `retention_until` (new, 11 delta). **S**, not L1 (D2) | S |
| `business_partner` | `retention_until` (new, S) and `retention_floor` (new, S, E2): `retention_until` is the later of `retention_floor` and the **latest** `retention_until` of any record that references the partner (snapshots, orders, open items, documents). Maintained by a scheduled batch job, **not** by posting or order commands, so no partner row is locked by a posting (14 D3, ADR-147). Recomputed and verified by `chains:verify` | S |

Other record classes need no column: they are not deleted before tenant deletion (decision), so their retention matters for the tenant deletion check (6.3) and for exports, which compute `retention_until` on the fly from the rules.

### 4.3 Anonymisation job

`anonymisation:run` (scheduled daily under a service identity, or by command with `retention.manage`).

| Parameter | Notes |
|---|---|
| mode | `DRY_RUN` (default for commands) or `EXECUTE`. A dry run produces the same run record and items without changing data. **Scheduled runs** start in DRY_RUN for the tenant's dry-run period (`tenant_setting.anonymisation_dry_run_weeks`, default 4, counted from the first scheduled run, Q2 decided) and switch to EXECUTE automatically afterwards. The report of every dry run is available as audit events so wrong retention rules can be corrected before anything is changed |
| scope | Optional: company, partner, record class. Default: everything due |

Two kinds of target:

**A. Document-level snapshots** (`document_legal_snapshot`, `order_address_snapshot`): anonymised when `retention_until` < today, and no active hold covers the record (4.4). Independent of the partner.

**B. Partners**: a partner is anonymised when all of the following hold:
1. `status` is BLOCKED or INACTIVE (never an ACTIVE partner), or a completed erasure request authorises it (5.2).
2. `business_partner.retention_until` < today, **after the job has recomputed it** (below).
3. No active hold covers the partner (4.4).
4. No open item with remaining amount on any account, no open order line, no reservation, no uninvoiced delivery or receipt of the partner exists in any company.
5. No data subject request of the partner is in a **blocking state**: a request with `status` RECEIVED or IN_PROGRESS, other than the ERASURE request that this run executes. A closed request (COMPLETED, REFUSED, PARTIALLY_COMPLETED, WITHDRAWN) never blocks, whatever its `decision_code`. `REFUSED_HOLD` is a `decision_code` of a closed request, not a status.

**Recomputation before acting (14 section 10, Q17).** The stored partner maximum is maintained by a batch job (4.2) and can lag a recent posting. For every candidate partner the anonymisation job therefore recomputes `business_partner.retention_until` as the later of `retention_floor` (E2) and the latest `retention_until` of all records that reference the partner (4.2) before evaluating conditions 2 to 5, and uses the recomputed value. If the recomputed date is not before today, the outcome is SKIPPED_RETENTION, and the stored value is corrected to the recomputed one (an S column, system change). A correction emits the audit event `retention.recomputed` (new, 14 section 11, `is_audit` = true, references only, 15, section 6). `retention.extended` stays reserved for `retention:extend`.

Anonymisation of a partner overwrites the columns of `business_partner_pii`, `partner_bank_account`, the `vat_id` of persons, the credit limits of persons, the search columns, sets `business_partner.status` = ANONYMISED and `anonymised_at`. `partner_code`, roles and `ic_company_id` stay. The personal columns are overwritten with a fixed marker or null (per column nullability). This overwrite is the only UPDATE the retention role may perform on L1* columns (02, 4). **Legal L1 exception for snapshots:** the overwrite of the personal fields of `document_legal_snapshot` and `order_address_snapshot` (target A) is a legal exception to the L1 rule, performed by the retention role only, and is logged with reason code `SYSTEM` (field names and run reference, no values, as the values are personal data): for `document_legal_snapshot` in `change_log` (entity type LEGAL_SNAPSHOT), for `order_address_snapshot` in `order_change_log` (11, 14 section 11, Q19). It is allowed in hard-closed periods like any S change of the retention role. The `is_pii` extension values (15, 3.6) of the partner **and of the records that reference it** (sales and purchase orders and their lines, open items, partner company relations) are cleared in the same transaction, with a `master_change_log` row of action CLEAR per entity and no value (E3, E4, 15). Extension values are cleared **only at anonymisation, never at deactivation or blocking** of a partner: until then a legal hold or a retention duty protects them like the partner data. After anonymisation `extensions:set` on the partner is rejected with `EXT.ENTITY_ANONYMISED` (15, 4.3). Each overwritten group appears in `master_change_log` with `action` = ANONYMISE and `is_pii` = true, no values, and each anonymised partner or snapshot emits an audit event `partner.anonymised` (`is_audit` = true, references only, 15, section 6). Run items record the outcome.

Effects: ledger, orders, stock, hashes and chains are untouched. Documents and partner references keep working. Reads of an anonymised partner return the marker, and documents show an anonymised legal snapshot. A new document for an ANONYMISED partner is rejected (`MASTER.PARTNER_ANONYMISED`).

### 4.4 Legal hold evaluation

A hold is **active** from placement until release. A record is covered if:
- the hold scope is TENANT, or
- COMPANY and the record belongs to that company, or
- PARTNER and the record references that partner (snapshot of a document of that partner, order, etc.), or
- DOCUMENT_SET and the record is in `legal_hold_document` (or is the legal snapshot or address snapshot of a listed document).

A partner is covered if a PARTNER hold names it, or a TENANT hold exists, or a COMPANY hold exists for **any company in which the partner has a record** (the partner PII is shared across companies, so a company hold protects it). A **DOCUMENT_SET hold covers only the records of 4.4 above (the listed documents, their legal and address snapshots), never the partner master data**: it does not stop the anonymisation of `business_partner_pii`, bank accounts and the other personal master data of the partners of those documents, and the partner maximum `retention_until` is not extended by it. The snapshots of the listed documents stay protected. A hold that must also protect the master data of a partner is a PARTNER hold. While any applicable hold is active, anonymisation and tenant deletion skip or reject the covered records. A hold also blocks the deletion of the tenant (6.3).

### 4.5 `anonymisation_run` (E + S) and `anonymisation_run_item` (E)

`anonymisation_run`: PK (`tenant_id`, `run_id`). Columns: `run_type` (SCHEDULED, REQUESTED), `mode`, `scope` (JSON-like summary), `started_at`, `completed_at`, `status` (S: RUNNING, COMPLETE, FAILED), `snapshots_anonymised`, `partners_anonymised`, `skipped_hold`, `skipped_retention`, `skipped_open_items`, `failed`, `outbox_events_purged` (Q16 decided: count of app events purged by 4.6, in DRY_RUN the count that would be purged), `requested_by`, `correlation_id`.
`anonymisation_run_item`: PK (`tenant_id`, `run_id`, `item_no`). `entity_type` (PARTNER, LEGAL_SNAPSHOT, ADDRESS_SNAPSHOT), `entity_id`, `company_id`, `field_groups` (PII_IDENTITY, PII_ADDRESS, PII_CONTACT, PII_BANK, PII_TAX_ID, PII_CREDIT, SEARCH, PII_EXTENSION), `outcome` (ANONYMISED, SKIPPED_HOLD, SKIPPED_RETENTION, SKIPPED_OPEN_ITEMS, SKIPPED_ACTIVE, SKIPPED_REQUEST, FAILED), `reason_code`. Ids only, insert-only. A daily scheduled run with nothing to do writes the run record and no items.

### 4.6 Retention of outbox events (15, 5.4)

| Class | Rule |
|---|---|
| Audit events (`outbox_event.is_audit` = true) | Retained under the record class `AUDIT_LOG`, never earlier than the records they describe. They are the single stream behind `audit-events:query` (K3, ADR-159). `is_audit` governs this retention only (09, 2.3a) |
| `command_log` | **Not** `AUDIT_LOG` and not a record class. Purged after `command_log_retention_days` (06, Q2, 07, default 90) by the retention job under the retention role. The unique keys of the created entities remain the last defence against double posting after the purge (06, Q2) |
| `read_access_log` | Record class `READ_LOG`. Purged by the retention job under the retention role after `read_log_retention_days` (09, Q6), which is the only DELETE on this table. Rows hold ids only and nothing is anonymised in them |
| App events (`is_audit` = false) | Purged by the retention job after `outbox_app_event_retention_days` (default 30) and only when every active consumer offset has passed the event. A consumer inactive for more than `outbox_consumer_max_lag_days` (default 90) no longer blocks the purge (`EVENT.POSITION_EXPIRED` on return). The purge is the only deletion of `outbox_event` that is not part of tenant deletion |

The purge touches no personal data: events hold none (15, Q3). It runs in the retention job under the retention role. **Decided (Q16, 14 section 11):** the purge runs inside the scheduled retention job and is reported in the `anonymisation_run` record as the count `outbox_events_purged`, with no run items and no event per purged row. It is not a run of its own: a day with only a purge writes the normal run record. No personal data is involved.

## 5. Data subject requests

### 5.1 `data_subject_request` (M)

PK: (`tenant_id`, `request_id`). Index (`tenant_id`, `partner_id`), (`tenant_id`, `status`, `due_date`).

| Column | Type | Null | Notes |
|---|---|---|---|
| partner_id | ID | N | A partner of kind PERSON (organisations have no data subject rights, Q14) |
| request_type | CODE | N | ACCESS, RECTIFICATION, ERASURE, RESTRICTION, OBJECTION, PORTABILITY |
| received_at | TS | N | |
| due_date | DATE | N | Computed from received date plus the tenant's response period (config), editable with reason |
| status | CODE | N | RECEIVED, IN_PROGRESS, COMPLETED, REFUSED, PARTIALLY_COMPLETED, WITHDRAWN. S, changed by commands |
| decision_code | CODE | Y | e.g. FULFILLED, REFUSED_RETENTION, REFUSED_HOLD, REFUSED_NOT_APPLICABLE. A decision of a closed request, never a status |
| blocking_until | DATE | Y | For a refused erasure: the date from which erasure becomes possible (the partner's `retention_until`) |
| decided_by, decided_at | | Y | |
| outcome_ref | TEXT(100) | Y | Anonymisation run item, export job id or similar |
| notes | TEXT(500) | Y | Handling notes. Must not contain personal data of the subject beyond the partner id (Q4) |
| row_version | INT | N | |

Changes are logged in `master_change_log`.

### 5.2 Handling

| Request | What the core does |
|---|---|
| ACCESS, PORTABILITY | `exports:request` of type `PARTNER_DATA_EXPORT` (7.2): all personal data of the partner and the records referencing the partner, in the open formats of 7.1. The job id is the outcome |
| RECTIFICATION | Personal data is corrected through the normal master data update (07). Posted legal snapshots are **not** rewritten (legal documents as issued). The decision records which snapshots were not corrected |
| ERASURE | The command evaluates 4.3 B for the partner. If anonymisation is permitted now, it executes a REQUESTED run for that partner and sets COMPLETED. If not, the request is REFUSED or PARTIALLY_COMPLETED with `decision_code` and `blocking_until`. The partner is **restricted** (below) until anonymisation. The scheduled job anonymises on the first date permitted |
| RESTRICTION, OBJECTION | The partner's `restriction_status` is set (below) |

### 5.3 Processing restriction

`business_partner` gets `restriction_status` (S): NONE or RESTRICTED (11 and 07 deltas). Set and cleared by `data-subject-requests` commands only, each change an audit event (`partner.restricted`, 15, section 6).

| Effect while RESTRICTED | Rule (Q5) |
|---|---|
| New orders, deliveries, receipts, invoices, credit notes, returns | Rejected: `MASTER.PARTNER_RESTRICTED` |
| Payments, clearings, reversals, credit notes **that settle existing items**, VAT and statutory documents | Allowed, they are statutory or needed to close existing obligations |
| Reads of personal data | Need `read.pii` and a `purpose_code` from the configured statutory purposes (STATUTORY, LEGAL_CLAIM, DATA_SUBJECT_REQUEST). Otherwise `READ.PARTNER_RESTRICTED` |
| Exports and SAF-T | Include the partner's statutory data. Marked restricted in the manifest |

## 6. Tenant lifecycle

### 6.1 Tenant status

`tenant.status` (07, 5.1) gains DELETION_PENDING: ACTIVE, SUSPENDED, EXPORTED (an acknowledged full export exists, still ACTIVE for use), DELETION_PENDING, DELETED. Platform-level operations: `tenants:suspend`, `tenants:reactivate` (platform administrators, outside the finance contracts), and the deletion workflow below. Tenants have a `purpose` (PRODUCTION, REHEARSAL, TEST, 07, 5.1). The workflow of 6.3 has a relaxed variant for REHEARSAL tenants (6.5). A SUSPENDED tenant rejects all commands and reads except exports, holds, data subject requests and the deletion workflow.

### 6.2 `tenant_deletion_request` (E + S)

PK: (`tenant_id`, `request_id`). One non-cancelled request at a time.

| Column | Type | Null | Notes |
|---|---|---|---|
| requested_by, requested_at | | N | Holder of `tenant.delete.request` |
| export_job_id | ID | Y | The full export that backs the deletion (FK `export_job`, type TENANT_EXPORT, status COMPLETE) |
| export_acknowledged_by, export_acknowledged_at | | Y | Someone confirmed receipt and readability of the export |
| approver_1, approved_1_at, approver_2, approved_2_at | | Y | Two distinct identities with `tenant.delete.approve`, neither the requester |
| waiting_until | TS | Y | Set at the second approval: approval time plus the waiting period (tenant setting, default 30 days, at least 7) |
| retention_override | BOOL | N | True if statutory retention had not ended for all data (6.3) |
| retention_override_ack_by, retention_override_reason | | Y | Required when `retention_override` |
| status | CODE | N | S: REQUESTED, EXPORT_PENDING, AWAITING_APPROVAL, WAITING, CANCELLED, EXECUTING, DELETED |
| cancelled_by, cancelled_at, cancel_reason | | Y | Cancellation is possible until EXECUTING |

### 6.3 Preconditions and workflow

1. `tenant-deletions:request` creates the request and (optionally) requests the full export.
2. `exports:acknowledge` on the export job by a person with `export.tenant`.
3. Checks at approval time and again at execution: no active legal hold (any scope), no data subject request in a blocking state (4.3 B, status RECEIVED or IN_PROGRESS), the export job is COMPLETE, **not expired** (its `expires_at` is at least `waiting_until` + 7 days, 7.1, E1, so that the default of 14 days cannot expire the export during a 30-day waiting period), acknowledged and **newer than the last posting** of the tenant (otherwise a fresh export is required), the tenant is quiescent (all companies INACTIVE or the tenant SUSPENDED).
4. Retention check: for every company and record class, the latest `retention_until` (computed from rules, 4.2) must be in the past. If not, the request needs `retention_override` = true with an acknowledgement and reason from the approvers: the customer accepts responsibility for retention. The override and the unmet classes are recorded.
5. Two approvers approve (`tenant-deletions:approve`). The waiting period starts. The requester or any approver can cancel (`tenant-deletions:cancel`) until execution.
6. After `waiting_until`, `tenant-deletions:execute` (a platform operation) rechecks 3 and 4 and executes: status DELETION_PENDING, all rows with the tenant id are deleted by the platform deletion role (the only DELETE the system performs on transaction tables), the certificate is written, the tenant registry row becomes DELETED. The rows deleted include the platform extension tables of 15 (`app_registration`, `app_namespace`, `app_event_subscription`, `app_consumer_offset`, `extension_definition`, `extension_value`), `outbox_event` and `outbox_head`, and the chain heads (`chain_head`).

**Audit trail of the workflow (K3, ADR-159).** The request, the export acknowledgement, each approval, the cancellation and the execution are audit events of the outbox (`deletion.scheduled`, `deletion.cancelled`, `deletion.executed`, `export.created`, 15, section 6). Because the tenant's outbox is deleted with the tenant, the evidence that survives is the `tenant_deletion_certificate` (6.4), which records the request, the approvers and the execution.

### 6.4 `tenant_deletion_certificate` (E, platform level)

Survives the tenant. PK: `certificate_id`. Columns: `tenant_id` (reference only), `tenant_code`, `request_id`, `export_job_id`, `export_manifest_digest`, `final_chain_heads` (one entry per `chain_head` row of all six chain types: type, company, scope key, `last_seq`, `last_hash`), `last_anchor_refs` (the last anchor of each chain with TSA id and reference), `row_counts_by_table`, `retention_override` and the unmet classes, `requested_by`, `approvers`, `executed_at`, `executed_by`, `certificate_digest`. No personal data. The certificate lets a customer prove later what existed at deletion and that the exported data matches the final chain heads.

### 6.5 Rehearsal tenants (13, Q10)

A tenant with `purpose` = REHEARSAL (a throwaway tenant for a migration rehearsal, 13) uses the workflow of 6.3 with these relaxations, and nothing else changes:

| Step | PRODUCTION | REHEARSAL |
|---|---|---|
| Approvers | Two distinct approvers, neither the requester | **One** approver, distinct from the requester |
| Waiting period | `deletion_waiting_days`, at least 7 | **None**: `waiting_until` = approval time |
| Retention check (6.3 step 4) | Unmet retention needs `retention_override` with acknowledgement | **No retention override**. The check does not apply, the request records that the tenant is a rehearsal |
| Export, hold and quiescence checks (6.3 step 3) | Required | Required. A rehearsal tenant may hold real personal data and is protected like production until it is deleted (13, A6) |
| Certificate | As 6.4 | As 6.4, marked `purpose` = REHEARSAL. External anchoring is not used in rehearsal tenants (13, Q10), so `last_anchor_refs` may be empty |

`tenant_deletion_request.retention_override` is always false for a rehearsal tenant, and `approver_2` and `approved_2_at` stay null. The error code `TENANT.APPROVER_NOT_DISTINCT` applies to the single approver.

Backups and replicas of deleted data are outside the core (Q12). The platform keeps backups for 35 days (proposal, 20, section 12, ADR-218), so deleted tenant data and anonymised personal data stay in backups for that period (erasure lag). The deletion certificate and the privacy notice state the period. After the acknowledged export (6.3 step 3) the platform destroys the tenant key of the export and anchor material (crypto-shredding, 20, section 10, ADR-216). Database backups are not covered by it.

## 7. Exports

### 7.1 Common contract

`export_job` (E + S): PK (`tenant_id`, `export_id`). Created by `exports:request`, run by a worker. Storage mapping (20, section 10, ADR-216): the `exports` bucket of one EU region with the key prefix `{tenant_id}/`, the tenant key (envelope encryption), short-lived pre-signed download URLs, no credentials in the core tables, no object lock, expiry by lifecycle rule.

| Column | Type | Null | Notes |
|---|---|---|---|
| job_type | CODE | N | `TENANT_EXPORT`, `AUDIT_PACKAGE`, `SAFT_DATASET`, `EINVOICE_DATASET`, `PARTNER_DATA_EXPORT` |
| company_id | ID | Y | Null for tenant-wide jobs |
| parameters | | N | Type-specific, stored as a canonical document: period, variant, partner, include_pii, encryption key reference |
| requested_by, requested_at, correlation_id, idempotency_key | | N | 06 |
| status | CODE | N | S: REQUESTED, RUNNING, COMPLETE, FAILED, CANCELLED, EXPIRED |
| snapshot | | Y | The consistent snapshot used: `{taken_at, ledger_seq per ledger, other chain positions}` (09, 2.2) |
| manifest_digest | BINARY(32) | Y | SHA-256 of the manifest artifact |
| artifact_count, total_bytes | | Y | |
| storage_ref | TEXT(300) | Y | Container or prefix in the external storage configured for the tenant (tenant setting) |
| expires_at | TS | Y | When artifacts are deleted. Default 14 days after completion (tenant setting). **E1:** an export referenced by a `tenant_deletion_request` (`export_job_id`) does not expire before `waiting_until` + 7 days: `expires_at` is the later of the default and that date, recomputed when the second approval sets `waiting_until` (6.2), and the expiry job skips such an export while the request is not CANCELLED or DELETED |
| started_at, completed_at, failure_reason | | Y | |
| acknowledged_by, acknowledged_at | | Y | Set by `exports:acknowledge` (TENANT_EXPORT) |

`export_artifact` (E): PK (`tenant_id`, `export_id`, `artifact_no`). `name`, `format` (CSV, NDJSON, JSON), `table_or_entity`, `row_count`, `byte_size`, `sha256`, `storage_path`.

**Package layout.** Each job writes: one data file per table or logical entity (CSV with header row, UTF-8, or NDJSON), a `manifest.json` (job parameters, snapshot, the list of artifacts with row counts and SHA-256, chain heads, anchors, the verification specification version), and a `README` describing the files. Files are checked against the manifest by a standard hash tool. No proprietary format.

**Mechanics.**
- Runs in one consistent snapshot, stored on the job (09, 2.2). New postings after the snapshot are not included, and the manifest says so.
- Writes artifacts to external storage with the tenant's credentials configured outside the core. The job stores references, never content.
- Encryption: artifacts are encrypted with a key reference given in the parameters (the customer's public key or a platform key). The core never holds the private key (Q6).
- Reads of personal data by an export are logged in `read_access_log` as **one row per job and field group**, never one row per partner: `partner_id` is null, `export_job_id` is set, `partner_count` holds the number of partners whose data the job read, `operation` = `exports:<type>`, `purpose_code` from the request (Q15, 14 D7, ADR-151). The table is defined in 09, 5.2. A PARTNER_DATA_EXPORT for one partner may set `partner_id` and `partner_count` = 1.
- Creation, completion and each download of an export are audit events (`export.created`, `export.downloaded`, 15, section 6).
- Artifacts expire and are deleted. `status` becomes EXPIRED. The manifest in `export_job` remains.

**Commands and permissions.**

| Command | Permission | Notes |
|---|---|---|
| `exports:request` | `export.tenant`, `export.audit`, `export.saft`, `export.einvoice`, `export.partner_data` (by type) | Idempotent. Rejected if a job of the same type, company and parameters is RUNNING (`EXPORT.ALREADY_RUNNING`) |
| `exports:get`, `:list` | `read.audit` | Status, manifest, artifacts |
| `exports:get-download-links` | The type's export permission | Short-lived signed links to the artifacts. Each call is an audit event (`export.downloaded`) |
| `exports:cancel` | Requester or `export.manage` | Only REQUESTED or RUNNING |
| `exports:acknowledge` | `export.tenant` | TENANT_EXPORT only. Records who confirmed readability |

### 7.2 Export types

**TENANT_EXPORT** (portability, exit, prerequisite of deletion). All tables of the tenant, grouped by domain: configuration (with all versions, calendars and app registrations), master data (including personal data: the export is the customer's own data), extension definitions and values (including `is_pii` values), transaction data of all domains, all chains, `chain_head` rows and anchors, all logs and audit tables, `outbox_event` (audit and app events still retained), `command_log` and `read_access_log` (pseudonymous), migration batches with their control totals and reconciliations, and the manifest. The manifest carries, for every chain, the head and the last anchor, so the receiver can verify. A **verification specification** (the canonical serialisation per `hash_version`, field lists and algorithms, 02, section 9) is part of the package, so verification does not depend on the platform.

**AUDIT_PACKAGE** (per company and period, for an auditor). Journal documents, lines, tax lines, open items, dimensions, clearings and items, revaluation runs and items, period events, year-end documents, VAT returns and tax lines, stock documents and movements, fulfilment documents, order change logs, change logs, master change log entries for the period, audit events of the outbox for the period, **migration batches, staged-record control totals and reconciliations** that touch the company or period (13), chain nodes, `chain_head` rows and anchors for the range, and the result of `chains:verify` for the same range. Personal data is excluded by default (ids only, `include_pii` = false). With `include_pii` = true the package needs `read.pii` and is logged. A package states the exact scope and snapshot in its manifest.

**SAFT_DATASET.** See section 8.

**EINVOICE_DATASET.** See section 9.

**PARTNER_DATA_EXPORT** (access and portability). For one partner: all personal data of the partner (`business_partner`, PII, bank accounts, registrations, relations with credit limits, `is_pii` extension values of the partner and of records referencing it, 15), all legal and address snapshots referencing the partner, and the records referencing the partner in all companies the caller can access (orders, deliveries, invoice link records, open items, clearings, documents, with amounts and dates, **without** other parties' personal data). Needs `export.partner_data` and `read.pii`. Produced from the same consistent snapshot, in the formats of 7.1.

## 8. SAF-T data set

An export job of type `SAFT_DATASET` for a company and period (or fiscal year) and a **variant** (`OECD_BASE` in release 1, Q8). It produces the **data** of the audit file in the open formats of 7.1. Generating the XML file and national submission stay with apps (P15).

The data set maps the core's tables to the groups of the OECD SAF-T base structure. **Group-level mapping, to be verified against the schema version of the OECD standard before implementation (P15).**

| SAF-T group | Content | Core source |
|---|---|---|
| Header | Company identification, fiscal year, period, currency, software and data set information | `company`, `company_vat_registration`, `fiscal_year`, `fiscal_period`, job parameters |
| Master files: general ledger accounts | Account number, name, standard account code, opening and closing balances | `gl_account` (`standard_account_code`), balances by 09 `balances:query` rules |
| Master files: customers and suppliers | Identification, names and addresses, tax registrations, bank accounts | `business_partner`, `business_partner_pii`, `partner_tax_registration`, `partner_bank_account` (personal data, needs `read.pii`, logged) |
| Master files: tax table | Tax codes with rates and descriptions | `tax_code`, `tax_code_version`, external code mapping for the standard's tax code (9, `SAFT_OECD`) |
| Master files: other (analysis types, products, UOM) | Dimension types and values, items, units | `dimension_type`, `dimension_value`, `item`, `ref_unit` |
| General ledger entries | Journals, transactions (one per journal document), transaction lines with account, debit or credit amount (derived from the signed amount, 02 A1), dimensions as analysis, tax information from the tax detail, customer or supplier id on open-item lines | `journal_document`, `journal_line`, `journal_line_tax`, `journal_line_dimension`, `journal_line_open_item` |
| Source documents: sales invoices, purchase invoices | Documents with lines, tax, totals | `sales_invoice`, `purchase_invoice` and their lines, journal documents of AR and AP types for invoices posted without orders |
| Source documents: payments | Payments and settlement information | Journal documents of type PAYMENT and the clearings that settle open items |
| Source documents: stock movements | Goods movements | `stock_document`, `stock_movement` |

Rules:
- Only data within the snapshot and the requested period (or year). Documents are included by `posting_date` and `fiscal_year`. System documents (revaluation, year-end closing, VAT settlement, stock-derived journals) are included with their document type so the file can classify them.
- Amounts in local currency and, where required by the variant, in reporting currency (02, 5.2). Debit and credit are derived from the signed amounts.
- The mapping of internal codes to the codes of the standard or of national variants is data (`external_code_mapping`, section 9), loaded by country packages.
- The data set carries the standard account code mapping and states which account lacks one.
- The manifest records the variant and the version of the mapping used.

## 9. External code mapping and EN 16931 attributes

### 9.1 `external_code_mapping` (V)

PK: (`tenant_id`, `mapping_id`, `valid_from`). Unique (`tenant_id`, `standard_code`, `entity_type`, `entity_id`, `valid_from`).

| Column | Type | Null | Notes |
|---|---|---|---|
| standard_code | CODE | N | `SAFT_OECD`, `EN16931`, or a national variant code such as one added by a country package |
| entity_type | CODE | N | `TAX_CODE`, `ACCOUNT`, `UNIT`, `PAYMENT_MEANS`, `DOCUMENT_TYPE`, `ITEM_GROUP` and others |
| entity_id | TEXT(100) | N | Canonical text of the key of the internal object |
| external_code | TEXT(50) | N | Code in the external standard |
| external_text | TEXT(200) | Y | |
| valid_from, valid_to | DATE | N, Y | |

Mappings are configuration, applied through packages (07, 11.4). Data sets read the mapping valid at the document or period date.

### 9.2 EN 16931 attributes added to configuration and master data (Q9 decided)

| Table | New attribute | Notes |
|---|---|---|
| `tax_code_version` | `en16931_vat_category` (S, Z, E, AE, K, G, O, L, M), `exemption_reason_code`, `exemption_reason_text` | The VAT category of the invoice's tax breakdown and the exemption reason. Required for ZERO_RATED, EXEMPT, reverse charge and intra-EU codes (config validation). The category codes are those of the standard, verified against its code list |
| `company` | `legal_registration_id`, `legal_registration_scheme`, `electronic_address`, `electronic_address_scheme` | Seller legal registration and endpoint (organisation data) |
| `business_partner_pii` | `electronic_address`, `electronic_address_scheme`, `legal_registration_id` | Buyer data, personal data, anonymised with the partner |
| `document_legal_snapshot` | `electronic_address`, `electronic_address_scheme`, `legal_registration_id` (L1*) | The buyer data **as on the invoice**, copied at posting (ADR-064) |
| `company_bank_account` | `payment_means_code` (default code for credit transfer), `use_on_invoices`, `account_scheme` and `account_identifier` (ADR-223, 07, 9.5) | Payment means and the account shown on invoices. The identifier form carries accounts without an IBAN, for example a Danish FIK creditor number |
| `item` | `standard_identifier`, `standard_identifier_scheme` | Item standard id (e.g. GTIN) |
| `sales_order` | `buyer_reference`, `contract_reference` (L2) | Buyer's routing and contract reference |

## 10. E-invoice data set

Export type `EINVOICE_DATASET`, or the synchronous read `einvoice-datasets:get` for one invoice (small, same content). It returns the **data fields of the EN 16931 semantic model** for one issued invoice or credit note of order management (`sales_invoice`) in the open formats of 7.1, as structured JSON with the standard's business term identifiers (BT and BG numbers) as keys. The XML syntax, the national extension (CIUS) and the transport are apps (P15). **The term mapping below is group-level and must be verified against the version of the standard before implementation.**

| Group | Source |
|---|---|
| Document level (number, issue date, due date, type code, currency, tax currency, buyer reference, order reference, contract reference) | `journal_document` (number, dates), `sales_invoice` (kind, credit link), `sales_order` (`customer_reference`, `buyer_reference`, `contract_reference`) |
| Seller | `company` (name, address, registration, endpoint), `company_vat_registration` (VAT id of the registration of the tax lines) |
| Buyer | `document_legal_snapshot` (name, address, VAT id, endpoint, registration as on the invoice) |
| Delivery | Ship-to snapshot (`order_address_snapshot`), delivery dates from deliveries |
| Payment instructions | `company_bank_account` (`use_on_invoices`, payment means code, and `iban` or `account_identifier` according to `account_scheme` for the payment account, BT-84), payment terms text. Remittance information (BT-83): the `payment_reference` of the AR control line of the invoice, empty if not set (ADR-223). The data set lists a missing reference only where the country package or the rule set of the company requires it. The prefix of a country (for example the Danish card type prefix) is applied by the app |
| VAT breakdown | `journal_line_tax` grouped by `en16931_vat_category` and rate: taxable amount, tax amount, exemption reason |
| Document totals | Net, tax, gross, rounding, amount due, from the journal document |
| Invoice lines | `sales_invoice_line` (quantity, unit code from `ref_unit` mapping, net price, discount, line net amount), item name, `standard_identifier`, VAT category and rate of the line's tax line |

**Rule set (ADR-221, 22, delta 3).** The completeness check below is a fixed rule set per edition of EN 16931, selected by `einvoice_rule_set` of the `company_setting_version` in force at the `document_date` of the invoice (07, 5.5). The rule sets ship with the core release and are never edited after release: a new edition is a new rule set and a new code. With no rule set the data set reports `completeness` = NOT_CHECKED and is otherwise produced as before. The package `STD_EN16931` lists the same terms for review and the core never reads it. A conformance test compares the term list of the package with the rule set of the same edition: a difference is a defect of one of the two.

Rules: only invoices and credit notes built by order management (Q10) have invoice lines. A completeness check reports missing mandatory data (VAT category, endpoint, registration ids) as a structured list, and the data set is still produced with the gaps listed. Credit notes reference the invoice they credit.

## 11. Issued document evidence

`invoice_issue_record` (E): PK (`tenant_id`, `company_id`, `issue_id`). Written by `invoices:record-issue` (`sales.invoice`, also usable for supplier documents through the same table with `invoice_kind`): `invoice_kind` (SALES, PURCHASE), exactly one of `invoice_id` (the link record of an order-built invoice or credit note) and `document_id` (the journal document of a direct invoice or credit note of type `AR_INVOICE`, `AR_CREDIT`, `AP_INVOICE` or `AP_CREDIT`, 22, delta 1), `format` (e.g. XML, PDF), `document_reference` (location of the issued file in the apps' archive), `document_sha256`, `issued_at`, `delivery_channel` (EMAIL, PEPPOL, PORTAL, POST, OTHER), `recorded_by`, `idempotency_key`. Insert only, one invoice can have several records (re-issues). It proves **which file was issued and when** without the core holding or rendering it (Q11). The record's retention class is `INVOICE_ISSUE`. The audit package includes these records.

**Targets (22, delta 1).** `invoices:record-issue` takes exactly one of `invoice_id` and `document_id`. With `invoice_id`, `invoice_kind` must match the link record. With `document_id`, `invoice_kind` is derived from the document type (AR types SALES, AP types PURCHASE) and a supplied value that disagrees is rejected. Rejections: `ORDER.ISSUE_TARGET_INVALID` (none or both supplied, or kind mismatch), `ORDER.ISSUE_TARGET_NOT_FOUND`, `ORDER.ISSUE_DOCUMENT_TYPE_INVALID` (a document of another type). A mirror (reversal) document cannot be a target. Both columns are nullable with a check that exactly one is set. Indexes: (`tenant_id`, `company_id`, `invoice_kind`, `invoice_id`) and (`tenant_id`, `company_id`, `document_id`). A direct invoice still has **no EN 16931 data set**, because it has no invoice lines (10): only issue evidence is added.

## 12. Deltas to earlier specs

| Spec | Change |
|---|---|
| 02 | `document_legal_snapshot` new L1* columns `electronic_address`, `electronic_address_scheme`, `legal_registration_id` (9.2). Anonymisation by the retention job only |
| 07 | `tenant.status` adds DELETION_PENDING. `business_partner` new S columns `retention_until` and `restriction_status`. `business_partner_pii`, `company`, `company_bank_account`, `tax_code_version` new attributes (9.2). New table `external_code_mapping` (9.1). New tables of this spec are config or log tables. `tenant_setting` gains `export_storage`, `export_expiry_days`, `deletion_waiting_days`, `dsr_response_days`. `master_change_log.action` adds ANONYMISE. Company readiness warns about missing retention rules. `partner_company_relation` credit limit cleared for PERSON at anonymisation |
| 09 | `read_access_log`: export reads logged per job and field group. `READ.PARTNER_RESTRICTED`. Audit events add anonymisation runs, data subject requests, export jobs, holds and tenant deletion requests. The deferred async report job (09 Q7) is realised by exports |
| 10 | `item` new `standard_identifier` and scheme |
| 11 | `sales_order` new L2 `buyer_reference`, `contract_reference`. `order_address_snapshot` new **S** `retention_until` (D2). `sales_invoice` gets `invoices:record-issue` |
| 06 | Error areas `EXPORT`, `RETENTION`, `HOLD`, `DSR`, and `MASTER.PARTNER_RESTRICTED`, `MASTER.PARTNER_ANONYMISED`. Permissions of the commands below |
| 02 `chain_anchor`, 04 | None. Exports never write to chains |
| ADR-223 (2026-10-19) | `company_bank_account` gets `account_scheme` and `account_identifier`, `iban` becomes nullable (07, 9.5). The payment instructions of the data set (10) read the identifier form for BT-84 and the invoice `payment_reference` for BT-83. `CONFIG.BANK_ACCOUNT_IDENTIFIER_INVALID` (06) |
| 22 (2026-10-19) | Delta 1: `invoice_issue_record` gets nullable `document_id` beside `invoice_id` (exactly one set), two indexes, three `ORDER.ISSUE_*` codes (11). Delta 3: the completeness check of 10 is a fixed rule set per EN 16931 edition selected by `einvoice_rule_set` (07, 5.5), ADR-221. Delta 6: `issue_state` filter and list `invoice-issue-records` (09, 3) |

These deltas are applied with this spec.

### 12.1 Deltas of the 2026-10-13 re-baseline (14, sections 8 and 9, specs 15 and 16)

| Source | Change in this spec | Effect on other specs |
|---|---|---|
| D2, ADR-146 | `retention_until` is an S column on `document_legal_snapshot`, `order_address_snapshot` and `business_partner`. `retention:extend` is a legal S change in hard-closed periods, audited by `retention.extended` (4.2, 13) | 02: `document_legal_snapshot.retention_until` moves from L1 to S in the column table and in the trigger rules. 11: `order_address_snapshot.retention_until` is S |
| D3, ADR-147 | The partner maximum is maintained by a batch job, not per posting (4.2, Q17) | 04, 6.3: no lock position for `business_partner.retention_until` |
| D7, ADR-151 | Export reads are logged one row per job and field group, never per partner: `partner_id` null, `export_job_id`, `partner_count` (7.1) | 09, 5.2 |
| K1, ADR-158 | Certificate chain heads come from `chain_head` (6.4). Tenant deletion deletes `chain_head` (6.3) | 02, 9.5 |
| K3, ADR-159 | Audit events of retention, anonymisation, holds, data subject requests, exports, downloads and tenant deletion are outbox events (4.3, 4.6, 5.3, 6.3, 7.1, 13) | 09, 4.8 maps each of them to an audit event type |
| 14 section 10 | Anonymisation job recomputes partner `retention_until` before acting (4.3, Q17). Snapshot overwrite is a legal L1 exception logged with reason `SYSTEM` (4.3, third pass: `order_address_snapshot` in `order_change_log`). Outbox purge reported as `outbox_events_purged` in the run record (4.5, 4.6, Q16). `order_address_snapshot.retention_until` and `document_legal_snapshot.retention_until` described as S. Field groups approved (Q18) | 02 (`change_log` reason `SYSTEM`), 09, 5.2 |
| S5 | Unchanged: `data_subject_request` stays | none |
| 13 Q10 | Rehearsal tenant variant of the deletion workflow (6.5) | 07 `tenant.purpose` already exists |
| 13 Q15, delta row for 12 | One retention rule for migrated records, counted from the legacy dates where supplied, from the migration date for snapshots and where no date is supplied (4.1) | 13 delta row is consistent with this wording |
| 13 | Audit package includes migration batches, control totals and reconciliations (7.2) | none |
| 14 section 11 | `outbox_events_purged` decided (Q16). SYSTEM log entry of `order_address_snapshot` goes to `order_change_log` (Q19). Correction of the partner maximum emits `retention.recomputed` (4.2, 4.3). Q16 to Q19 closed | 02 (`change_log` reason `SYSTEM` only for LEGAL_SNAPSHOT), 11 (`order_change_log` entity type for the address snapshot), 09, 4.8 (RETENTION_RECOMPUTED), 15, section 6 |
| 15, ADR-137, ADR-138 | Anonymisation clears `is_pii` extension values, the subject export includes them (3, 4.3, 7.2). Retention purge of app events (4.6). Tenant deletion removes the tables of 15 section 3 (6.3) | 15, section 10 row for 12 is now applied |
| 06 | New error code `RETENTION.CANNOT_SHORTEN` (13) | 06 error list |

### 12.2 Deltas of fix pass 4 (17, section 4)

| Ref | Change in this spec | Effect on other specs |
|---|---|---|
| E1 | The export backing a tenant deletion does not expire before `waiting_until` + 7 days (7.1, 6.3) | none |
| E2 | `business_partner.retention_floor` (S), respected by the recomputation (4.2, 4.3). `retention:extend` limited to `LEGAL_SNAPSHOT`, `ADDRESS_SNAPSHOT` and the partner maximum, new code `RETENTION.CLASS_HAS_NO_DATE` (13) | 07 (new S column), 06 (error list) |
| E3, E4 | `is_pii` extension values cleared at anonymisation, also on referencing orders, order lines and open items, never at deactivation. `master_change_log` action CLEAR, no values. `EXT.ENTITY_ANONYMISED` after anonymisation (3, 4.3) | 07 (`master_change_log.action` adds CLEAR), 15 |
| E8 | Evidence of retention changes is the outbox audit event. Note for ADR-146: "own chained event" becomes "outbox audit event" (4.2) | ADR-146 |
| Classes | `command_log` is not `AUDIT_LOG`, purged after `command_log_retention_days`. `read_access_log` is the class `READ_LOG`, purged by the retention role (4.1, 4.6). `AUDIT_LOG` lists the change logs and audit events | 06, 09 |
| Snapshots | Legal snapshot retention is never shorter than the longest retention of its referencing records, with a readiness warning (4.1). SCRAP returns are `FULFILMENT` records (4.1) | 07 readiness check |
| Holds, requests | DOCUMENT_SET hold does not protect partner master data (4.4). "Blocking state" and `REFUSED_HOLD` defined (4.3, 5.1, 6.3) | none |
| Naming | `snapshot_anonymised` renamed `snapshots_anonymised` (4.5) | none |

### 12.3 Deltas of the propagation pass (17, section 5, v0.6)

The pass grepped this spec for `read.ext`, `chain.verified`, `access.read_logged`, `ledger_id` on `chain_anchor` and for event names that differ from 15, section 6. None was found. The event names used here (`retention.extended`, `retention.recomputed`, `partner.anonymised`, `partner.restricted`, `export.created`, `export.downloaded`, `deletion.*`) match 15. "Class" in this spec means a retention record class (4.1, 4.6), not a lock class, so the renumbering of the lock classes in 04, 6.3 (fiscal period and year class added, open items 6, `outbox_head` 7) changes no reference here. The one lock reference, to 04, 6.3 (no lock position for `business_partner.retention_until`, 12.1 row D3), stays valid. The state "blocking" of a data subject request is RECEIVED or IN_PROGRESS, a DOCUMENT_SET hold does not cover partner master data, and a SCRAP return is retained as `FULFILMENT` (12.2): all already stated, confirmed. No effect on other specs.

## 13. Commands and permissions

| Command | Permission | Notes |
|---|---|---|
| `retention-rules:create-version` and reads | `retention.manage` | Configuration pattern of 07 |
| `legal-holds:place`, `:release`, `:add-documents` | `hold.manage` | Reason, authority and case reference mandatory. Release needs a reason. Idempotent, audited |
| `retention:extend` | `retention.manage` | Extends `retention_until` of existing records for one record class and company from a given date, never shortens (`RETENTION.CANNOT_SHORTEN`). **Limited to the classes that store a date (E2):** `LEGAL_SNAPSHOT`, `ADDRESS_SNAPSHOT` and the partner maximum (which also sets `retention_floor`, 4.2). Any other class is rejected with the new code `RETENTION.CLASS_HAS_NO_DATE` (to be added to 06): the other classes keep no stored date and follow their rules, so their retention is lengthened by a new `retention_rule` version, which affects records created afterwards (Q13). `retention_until` is an S column (4.2, D2), so the change is legal in hard-closed periods, needs no `change_log` row and writes the audit event `retention.extended` (K3). Reason and legal basis mandatory, one run record per use, audited (Q13 decided). Rule changes alone never alter stored dates |
| `anonymisation:run` | `retention.manage` | DRY_RUN default |
| `data-subject-requests:create`, `:update-status`, `:decide`, `:complete`, `:withdraw` | `dsr.manage` | Decide and complete evaluate 4.3 B and 5.2 |
| `exports:*` | By type (7.1) | |
| `invoices:record-issue` | `sales.invoice` | |
| `tenant-deletions:request`, `:approve`, `:cancel`, `:get` | `tenant.delete.request`, `tenant.delete.approve` | Approvers distinct from the requester and each other |
| `tenant-deletions:execute`, `tenants:suspend`, `tenants:reactivate` | Platform operator | Platform level. REHEARSAL tenants: relaxed workflow (6.5) |
| Reads of runs, holds, requests, jobs | `read.audit` | Holds and requests need `read.pii` only if they name a partner |

All commands are synchronous (jobs are started synchronously and run asynchronously), atomic, idempotent and support VALIDATE (`anonymisation:run` DRY_RUN is its own mode). New error codes (22, delta 1: `ORDER.ISSUE_TARGET_INVALID`, `ORDER.ISSUE_TARGET_NOT_FOUND`, `ORDER.ISSUE_DOCUMENT_TYPE_INVALID`): `EXPORT.ALREADY_RUNNING`, `EXPORT.NOT_COMPLETE`, `EXPORT.EXPIRED`, `EXPORT.STORAGE_NOT_CONFIGURED`, `RETENTION.NO_RULE`, `RETENTION.CANNOT_SHORTEN`, `RETENTION.CLASS_HAS_NO_DATE`, `HOLD.ACTIVE_BLOCKS`, `DSR.NOT_A_PERSON`, `DSR.REFUSED_RETENTION`, `TENANT.DELETION_PRECONDITION_FAILED`, `TENANT.APPROVER_NOT_DISTINCT`.

## 14. Assumptions

| Ref | Item |
|---|---|
| A1 | The anonymisation job never touches ledger, stock, order or VAT rows. It overwrites only the personal columns listed in section 3 |
| A2 | `retention_until` and the longest-wins rule are conservative defaults. Each period is configuration, verified per country |
| A3 | Organisations' contact data (names, addresses, contacts) are treated as personal data and anonymised, as decided in 07 Q3 |
| A4 | Export artifacts are the customer's data. The core never holds the encryption key |
| A5 | Exports use the consistent snapshot of 09, so concurrent postings do not appear half-included |

## 15. Open questions

| # | Question |
|---|---|
| Q1 | **Decided:** per record class and company, the retention starts at the end of the fiscal year of the record (default, `RECORD_DATE` selectable per rule), and the longest period wins if several countries are configured. |
| Q2 | **Decided:** the partner preconditions of 4.3 B stand. Scheduled runs are DRY_RUN for a configurable period (default 4 weeks) and then execute automatically, with no per-run approval. |
| Q3 | **Decided:** the core stores opaque actor ids only, never names or e-mail addresses. The mapping to persons lives outside the core. |
| Q4 | **Decided:** policy only, stated in the contract and in the data inventory (3). No pattern detection. A risk to remember: personal data typed into free text is never anonymised. |
| Q5 | **Decided:** the processing restriction blocks new transactions, and allows settlements and statutory documents as listed in 5.3. |
| Q6 | **Decided:** a customer public key (or key reference) per job, customer-owned storage configured per tenant, default expiry 14 days. The core never holds a decryption key. |
| Q7 | **Decided:** one consistent snapshot per export job, however long it runs. A replica dedicated to exports is a platform choice. |
| Q8 | **Decided:** release 1 follows the OECD base structure. The schema version and the first national variants are specified before the data set is implemented, after verification of the mapping against the published schema. The manifest records the version used. |
| Q9 | **Decided:** EN 16931 attributes in configuration and master data (9.2), the buyer endpoint is optional, the data set reports missing mandatory attributes as a list. The exact attribute list is verified against the standard version before implementation. |
| Q10 | **Decided:** the e-invoice data set covers invoices and credit notes built by order management. Directly posted AR documents and incoming supplier e-invoices are not covered. |
| Q11 | **Decided:** `invoice_issue_record` and `invoices:record-issue` are in the core (section 11). |
| Q12 | **Decided:** backups and replicas after tenant deletion are contractual and operational, documented in the deletion procedure and referenced on the certificate. The model does not track backup expiry. |
| Q13 | **Decided:** rule changes apply to new records only. A privileged `retention:extend` command lengthens existing retention dates, never shortens (section 13). |
| Q14 | **Decided:** data subject requests only for partners of kind PERSON. A request by a contact person of an organisation is a rectification or erasure of contact data on the organisation partner. |
| Q15 | **Decided:** one `read_access_log` row per export job and field group, never per partner, with `partner_id` null, the partner count, purpose and job id (14 D7, ADR-151). |
| Q16 | **Decided (14, section 11):** the outbox purge (4.6) is reported in the `anonymisation_run` record as the count `outbox_events_purged`. A purge-only day is not a separate run. |
| Q17 | **Decided (14, section 10):** the anonymisation job recomputes the partner `retention_until` before acting (4.3 B). Closed. |
| Q18 | **Decided (14, section 10):** field groups `PII_EXTENSION`, `ORDER_ADDRESS`, `PII_CREDIT`, `SEARCH` are approved. Closed. |
| Q19 | **Decided (14, section 11):** the SYSTEM log entry of the `order_address_snapshot` overwrite goes to `order_change_log` (11), not `change_log`. A correction of the partner maximum `retention_until` by recomputation emits the new audit event `retention.recomputed` (15, section 6); `retention.extended` stays reserved for `retention:extend`. |

## 16. ADRs raised by this spec (now in 01-adr-log, which is authoritative)

**ADR-118: Retention by anonymisation, ledger kept until tenant deletion.** The job anonymises personal data only. Ledger, stock, order and VAT rows contain no personal data and are never purged before tenant deletion.

**ADR-119: Retention rules and stored retention dates.** Versioned `retention_rule` per company, class and country, computed `retention_until` on snapshots and, as a system-maintained maximum, on the partner.

**ADR-120: Legal holds with scopes.** Tenant, company, partner or document set, evaluated by the job and by tenant deletion. A company hold protects shared partners referenced by that company.

**ADR-121: Data subject requests and processing restriction.** Requests with decisions, a restriction status on the partner, and erasure that anonymises at the earliest permitted date.

**ADR-122: Asynchronous exports as open files plus manifest.** Five job types, external storage, CSV or NDJSON, manifest with hashes, chain heads and anchors, verification specification inside the package, encryption by customer key.

**ADR-123: SAF-T and e-invoice as data sets.** The core provides the data in open formats, mapped group by group to the OECD base structure and the EN 16931 semantic model, through `external_code_mapping` and additional master data. File generation, national variants and transport stay with apps.

**ADR-124: Tenant deletion workflow.** Acknowledged export, no hold, two approvers, waiting period, retention override by recorded acknowledgement, platform-level certificate with final chain heads and export hash.

**ADR-125: Issued document evidence.** Insert-only records of which invoice file was issued, its hash and channel, without the core holding or rendering the file.

**ADR-126: Opaque actor identities.** The core stores identifiers only. The mapping to persons is outside the core.
