# Platform Extensions: Extension Attributes, Change Feed and App Registration

Status: DRAFT v0.5 for review, propagation pass of 2026-10-13 (17, sections 4 and 5: E7 counter, `apps:reactivate`, event types of returns, GR/IR write-off, migration and reversal clearings, see 10.2). v0.4: fix pass 4 of 2026-10-13 applied (17, section 4: E3 to E8, A6, F3, F9 to F13, event version, missing event types, see 10.1), decisions applied (14, sections 8 to 11: Q15 (15), 09 Q13 event types, D10 `outbox:verify` permission, `ITEM_PARTNER` extendable, and from section 11 the new event types `vat.settlement_reversed` and `retention.recomputed`). Builds on 00-design-principles v0.4, ADR-001 to ADR-174 and specs 02 to 14. Decisions come from the scope review (14, sections 8 to 11). ADR-137 to ADR-139 were raised here and are in the log.
Scope: how apps built on top of the core attach their own data to core records (extension attributes), react to what happens in the core (change feed), and are identified to the core (app registration). Also the single append-only event stream behind the audit query (K3).
Out of scope: authentication and authorisation of apps (identity provider), transport of events (webhooks, queues, brokers), app-owned tables and processes, any business rule that depends on an extension value.
Conventions as in 02, section 1, pattern letters as in 07, section 2, envelope and errors as in 06, reads as in 09.

## 1. Decisions taken

| Topic | Decision |
|---|---|
| Purpose | The core stays small: apps own process data. They need a governed way to attach data to core records and a reliable way to learn about changes |
| Extension entities | Master data, orders and open items (Q1). **Not** posted journal documents, stock documents, fulfilment documents or clearings |
| Extension storage | Typed definitions, one generic value table with one column per type, validated by the core, filterable in reads (Q2) |
| Hash and rules | Extension values are **outside every hash** and **never used by a core rule**. No core validation, determination or posting reads them |
| Change feed | An `outbox_event` row is written in the same transaction as every state change. Monotonic position per tenant. At-least-once delivery to polling consumers. Transport is outside the core (Q3, Q4) |
| Event content | References and change type only, **no personal data** (Q3). Apps read data through the read API |
| Audit | `outbox_event.is_audit` marks audit-relevant events and governs their retention. `audit-events:query` reads the outbox. Chained events are referenced, not duplicated (K3, Q15). Feed visibility is a separate matter (3.3, E5) |
| App registry | `app_registration` holds app id, extension namespaces, allowed event types. Consumer offsets are stored in the core (Q5) |
| Identity | The core trusts the identity provider for authentication and permissions. It checks only that the calling `app_id` matches a registration and owns the namespace it writes |

## 2. Overview

```
 App A  --- extensions:set (own namespace) --->  core tables  ---+
 App B  <--- events:list since position --------  outbox_event <--+   (same transaction)
 App B  --- events:ack (offset) --------------->  app_consumer_offset
 Core   --- audit-events:query ---------------->  outbox_event where is_audit
```

Apps never write core tables directly. Larger app structures (an MRP plan, a dunning case, an approval workflow) stay in app-owned tables that refer to core ids. Extension attributes are for small typed facts that belong to one core record, such as a dunning level on an open item or a planner code on an item.

## 3. Tables

| # | Table | Pattern | Role |
|---|---|---|---|
| 1 | `app_registration` | M | Registered apps and their namespaces |
| 2 | `app_namespace` | M | Namespaces owned by an app |
| 3 | `app_event_subscription` | M | Event types an app may read |
| 4 | `app_consumer_offset` | M + S | Last acknowledged position per app and stream |
| 5 | `extension_definition` | M | Typed attribute definitions per entity type |
| 6 | `extension_value` | M | Values per entity record (3.6) |
| 6a | `extension_counter` | S | Version counter per (entity, namespace), never decreases (3.6a, E7) |
| 7 | `outbox_event` | E | The change feed and audit stream |
| 8 | `outbox_head` | S | Per-tenant position counter |

### 3.1 `app_registration` (M)

PK: (`tenant_id`, `app_id`). Unique: (`tenant_id`, `app_code`).

| Column | Type | Null | Notes |
|---|---|---|---|
| app_id | ID | N | |
| app_code | CODE | N | Stable, immutable |
| name | TEXT | N | |
| active | BOOL | N | A deactivated app cannot write extensions or ack events. Its namespaces stay readable |
| company_scope | JSON | Y | Null = all companies of the tenant, otherwise a list of company ids. Restricts events and extension writes to those companies. **Tenant-level entities and events with a null `company_id` are visible only to apps without `company_scope`** (tenant-level master data such as business partners, items, accounts of the tenant, `master.*` events on them, tenant-level compliance and configuration events). An app with `company_scope` never sees them and cannot write extension values on tenant-level entities |
| row_version | INT | N | Compare-and-set for `apps:update` (4.5) |

The external identity of an app (client id, certificate) is not stored here. The identity provider maps its principal to an `app_code` claim in the command envelope (06) and in the read context (09, 2.1). **One rule for the error (unified):** a missing claim, or a code that matches no registration, is rejected with `APP.NOT_REGISTERED`. A code that matches a registration whose `active` is false is rejected with `APP.INACTIVE`. **Exemptions (F10):** `app_code` is required for `extensions:*`, `extension-definitions:*`, `events:list`, `events:head` and `events:ack`. It is not required for `events:rewind` and the app registry commands of 4.5 (permission `apps.manage`, a platform or administrator action), and not for ordinary reads (09, 2.1).

### 3.2 `app_namespace` (M)

PK: (`tenant_id`, `namespace`). `app_id` owns it. `namespace` is a lowercase code, immutable, unique per tenant. Core namespaces (`core`) are reserved. Only the owning app can write values and definitions in the namespace. Every caller with read permission can read values.

### 3.3 `app_event_subscription` (M)

PK: (`tenant_id`, `app_id`, `event_type`). An app can list and ack only the event types it is subscribed to. `event_type` may end in `*` for a family (`stock.*`). **Feed visibility (E5, 09, 2.3a):** a subscription to a type grants feed access to its events, whether or not they are audit events (`is_audit` governs retention only). The exceptions are the sensitive families `access.*`, the compliance family and the app events (`app.*`): they are returned only to a caller that also holds `read.audit`, whatever the subscription says, and an app cannot subscribe to them without it (`EVENT.NOT_SUBSCRIBED`). Visibility by company: events with a null `company_id` are visible only to apps without `company_scope` (3.1).

### 3.4 `app_consumer_offset` (M + S)

PK: (`tenant_id`, `app_id`, `consumer_id`). `consumer_id` lets an app run several readers. `last_position` (S, BIGINT), `updated_at`. `events:ack` is the only command that normally changes it, and it moves it forward, never back. The one exception is the explicit `events:rewind` (permission `apps.manage`, no `app_code` needed) which sets it backward to allow replay and writes an audit event (`app.consumer_rewound`).

### 3.5 `extension_definition` (M)

PK: (`tenant_id`, `definition_id`). Unique: (`tenant_id`, `entity_type`, `namespace`, `attr_key`).

| Column | Type | Null | Notes |
|---|---|---|---|
| entity_type | CODE | N | One of the extendable types (section 4.1) |
| namespace | CODE | N | FK `app_namespace` |
| attr_key | CODE | N | Lowercase, unique within the entity type and namespace. Immutable |
| data_type | CODE | N | TEXT, DECIMAL, DATE, BOOLEAN, REF. Immutable (never changed after creation, see the rules below) |
| max_length / precision, scale | INT | Y | Limits for TEXT and DECIMAL |
| allowed_values | JSON | Y | Closed list for TEXT |
| ref_entity_type | CODE | Y | For REF: the core entity type the value must point to, validated at write |
| is_pii | BOOL | N | Treated as personal data: logged on read (09, 5.1, 5.2), cleared by anonymisation (12, 4.3), included in subject exports, excluded from outbox payloads (always excluded), logged in `master_change_log` without content (3.6). **Immutable once any value exists** for the definition (`EXT.DEFINITION_LOCKED`). It can be set freely only while the definition has no value |
| is_required_on_create | BOOL | N | Always false in R1. Core commands never wait for an extension |
| active | BOOL | N | A deactivated definition keeps its values readable and rejects new writes |

Rules: definitions are created by the owning app (`extension-definitions:create`, 4.3). `data_type`, `entity_type`, `attr_key` cannot change. `is_pii` cannot change once a value exists. Other columns can change by command with `expected_version`, logged in `master_change_log`. Maximum active definitions per entity type and namespace: the tenant setting `extension_max_definitions` (07, 5.2, default 50) to keep apps from re-creating a schema in the core.

### 3.6 `extension_value` (M)

PK: (`tenant_id`, `definition_id`, `entity_key`). `entity_key` is the canonical string of the entity's primary key **in primary key order, without `tenant_id`** (for example `partner_id/company_id` for a `PARTNER_COMPANY_RELATION`, 07, whose key is (`tenant_id`, `partner_id`, `company_id`)).

| Column | Type | Null | Notes |
|---|---|---|---|
| version_stamp | BIGINT | N | The value of the counter of (entity, namespace) (3.6a) after the write that set or changed this row. Informational: the compare-and-set rule reads the counter, not this column (E7) |
| company_id | ID | Y | Copied from the entity when it is company-scoped, for row security and exports |
| value_text | TEXT | Y | |
| value_decimal | DECIMAL(28,8) | Y | |
| value_date | DATE | Y | |
| value_bool | BOOL | Y | |
| value_ref | TEXT | Y | Canonical key of the referenced entity |

Exactly one value column is set, as given by the definition. Null values are not stored: clearing a value deletes the row, and the history is the log. Every change (set, change, clear) is appended to `master_change_log` with entity type `EXTENSION_VALUE`, the action (CREATE, UPDATE or the new action CLEAR), the `app_id` (new column of `master_change_log`, 07 delta, null for core changes), and old and new value, and emits an outbox event. **For definitions with `is_pii` = true the log row carries no values** (old and new value null, `is_pii` = true on the row, as for the other personal columns of 12, section 3): only entity, definition and action are logged. Clearing at anonymisation writes a CLEAR row without content (12, 4.3).

**Compare and set (E7, 17 section 5).** `extensions:set` takes `expected_version` **per (entity, namespace)**: it is compared with the stored counter of 3.6a (0 if no counter row exists yet). A mismatch is `EXT.VERSION_CONFLICT`. On success the counter is increased by 1 in the same transaction, whatever the number of values the command sets or clears, and the new value is returned as `version`. An extension write never changes the `row_version` of the owner row (partner, order, open item, and so on) and takes no lock on it (section 8), so an extension write never causes a `VERSION_CONFLICT` on a core command and the reverse. Indexes by (`tenant_id`, `definition_id`, value) support filters.

### 3.6a `extension_counter` (S)

PK: (`tenant_id`, `entity_type`, `entity_key`, `namespace`). The version counter of E7: one row per (entity, namespace), created by the first write.

| Column | Type | Null | Notes |
|---|---|---|---|
| counter | BIGINT | N | Starts at 0 and is increased by 1 by every successful `extensions:set`, `extensions:set-batch` item or `extensions:clear` on that (entity, namespace). **It never decreases and is never deleted**, so it survives the clearing of all values, the deletion of value rows and anonymisation (the max+1 token of v0.4 could fall back when rows were cleared and so repeat a version) |
| updated_at | TIMESTAMP | N | Set with each increase |

The row is locked for update inside the extension write and nowhere else (it is not part of the lock order of section 8 and no core command touches it). A command that touches several namespaces of one entity compares and increases each counter. The counter is removed only by tenant deletion (12).

### 3.7 `outbox_event` (E)

PK: (`tenant_id`, `position`). `position` BIGINT, monotonic per tenant, assigned at the end of the transaction (section 5.2).

| Column | Type | Null | Notes |
|---|---|---|---|
| position | BIGINT | N | |
| event_type | CODE | N | Section 6 |
| event_version | SMALLINT | N | Version of the contract (fields and meaning of `summary`) of the event type, starts at 1 and is raised when a summary field is added, removed or changes meaning. Consumers read it to handle several versions |
| company_id | ID | Y | Null for tenant-level events (visible only to apps without `company_scope`, 3.1) |
| entity_type, entity_key | CODE, TEXT | N | Reference to the changed record |
| entity_version | BIGINT | Y | `row_version`, `chain_seq` or `ledger_seq` of the record after the change |
| change | CODE | N | CREATED, CHANGED, DEACTIVATED, POSTED, REVERSED, STATUS, CLEARED, RESET, ANONYMISED, SUMMARY (`access.summary`), and so on |
| occurred_at | TS UTC | N | Commit-time of the command (the command's `occurred_at`) |
| actor_ref | TEXT | N | User or app principal (the `master_change_log` convention) |
| app_id | ID | Y | Set when the actor was an app |
| correlation_id | TEXT(100) | N | The command's `correlation_id` as in 06 (TEXT(100)), so one command's events group together |
| is_audit | BOOL | N | Section 7. Governs retention (5.4, 12, 4.6) and membership of `audit-events:query`, **not** feed visibility (3.3) |
| chain_ref | JSON | Y | For chained records: chain type, scope, `chain_seq`, `row_hash`. Evidence stays in the chain, not here |
| summary | JSON | Y | Compact non-PII facts a consumer needs to decide whether to read (for example `{"item_id":..,"warehouse_id":..,"qty_delta":..}`), listed per event type in section 6. Never names, addresses, bank data or any `is_pii` extension |

Secondary indexes: (`tenant_id`, `is_audit`, `occurred_at`), (`tenant_id`, `correlation_id`), (`tenant_id`, `entity_type`, `entity_key`, `position`).

Insert only. UPDATE and DELETE are rejected for the application role, except the purge of app events by the retention role (5.4, 12, 4.6) and tenant deletion (12, 6.3). Event types and their summary fields are part of the API contract and versioned through `event_version`.

### 3.8 `outbox_head` (S)

PK: `tenant_id`. `last_position` BIGINT. The lock that serialises position allocation (section 5.2).

## 4. Extension attributes

### 4.1 Extendable entity types

| Entity type | Key | Notes |
|---|---|---|
| `BUSINESS_PARTNER`, `PARTNER_COMPANY_RELATION` | as in 07 | PII-flag per definition |
| `ACCOUNT`, `DIMENSION_VALUE`, `COMPANY` | as in 07 | |
| `ITEM`, `ITEM_COMPANY`, `ITEM_WAREHOUSE`, `WAREHOUSE`, `WAREHOUSE_LOCATION` | as in 10 | |
| `ITEM_PARTNER` | as in 16, 3.1 (`item_id`, `partner_id`, `company_key`, `valid_from`) | Versioned (V) master table, insert-only, extendable (16, Q3). A value belongs to one version row and does not carry over to a later version: the app writes it again on the new version |
| `SALES_ORDER`, `SALES_ORDER_LINE`, `PURCHASE_ORDER`, `PURCHASE_ORDER_LINE` | as in 11 | Writable until the order or line is closed or cancelled. Closed records keep readable values |
| `OPEN_ITEM` | (`company_id`, `document_id`, `line_no`) | Writable also in hard-closed periods, because open-item attributes are not L1 or L2 and carry no financial effect |

Posted journal documents, stock documents, fulfilment documents and clearings are not extendable: they are immutable evidence (P3). An app that needs a process fact about them stores it in its own table with the document id.

### 4.2 Rules

| Rule | Text |
|---|---|
| Owner | A value can be written, changed or cleared only by the app that owns the namespace of its definition. The core checks the command's `app_code` |
| Types | The value must match `data_type`, limits and `allowed_values`. REF values must exist and be of `ref_entity_type` in the same tenant |
| Company scope | An app with `company_scope` can write only for entities of those companies |
| No core effect | No core command reads an extension value. Nothing is determined, validated, hashed or posted from it |
| Compare and set | `extensions:set` takes `expected_version` per (entity, namespace), compared with the stored counter `extension_counter.counter` of (entity, namespace), which never decreases (3.6a, E7, 17 section 5). Without it, the command is last-writer-wins within the namespace. An extension write never bumps the owner row's `row_version` |
| Lifecycle | **Deactivating a master record never clears extension values** (E4): a legal hold or retention duty may still apply. Anonymising a partner clears the `is_pii` values of the partner and of the records referencing it (12, 4.3). Retention never deletes records (12, ADR-118), so values disappear only by clearing at anonymisation or by tenant deletion |
| Anonymised partner | `extensions:set` and `extensions:clear` on an ANONYMISED partner are rejected with `EXT.ENTITY_ANONYMISED` (E4). Reads of its remaining non-PII values stay allowed |
| Anonymisation | `is_pii` values are cleared by the anonymise command, on the partner and on orders, order lines and open items that reference it, and listed in the data-subject export (12) |
| Tenant deletion | Definitions, values, registrations and events are removed by tenant deletion (12) |

### 4.3 Commands

| Command | Purpose | Permission |
|---|---|---|
| `extension-definitions:create`, `:update`, `:deactivate` | Manage definitions in an owned namespace | `ext.define` |
| `extensions:set` | Set or change values on one record: `{entity_type, entity_key, values: {namespace.attr: value}, expected_version}`. Several values in one command are atomic | `ext.write` |
| `extensions:clear` | Clear named values | `ext.write` |
| `extensions:set-batch` | Up to `tenant_setting.max_batch_items` records (07, 5.2, default 500), independent atomic items (ADR-062) | `ext.write` |

Error area `EXT`: `NOT_OWNER`, `UNKNOWN_DEFINITION`, `TYPE_MISMATCH`, `VALUE_NOT_ALLOWED`, `REF_INVALID`, `ENTITY_NOT_EXTENDABLE`, `ENTITY_CLOSED`, `ENTITY_ANONYMISED`, `DEFINITION_LIMIT`, `DEFINITION_LOCKED` (`is_pii` changed after values exist), `COMPANY_OUT_OF_SCOPE`, `VERSION_CONFLICT`. Error area `APP` for registration and the claim: `NOT_REGISTERED` (claim missing or matches no registration), `INACTIVE` (registration deactivated, until `apps:reactivate`), `NAMESPACE_TAKEN`. The registry commands of 4.5 use the `CONFIG` codes of 06 (`DUPLICATE_CODE`, `ROW_VERSION_MISMATCH`, `INVALID_REFERENCE`).

### 4.5 App registry commands (F3, F13)

All commands use the envelope, idempotency, error model and VALIDATE mode of 06. Permission `apps.manage`, no `app_code` required (3.1). Every change is appended to `master_change_log` (entity types `APP`, `APP_NAMESPACE`, `APP_SUBSCRIPTION`, with `app_id` null as the actor is a person) and writes the audit events of section 6 (`app.registered`, `app.changed`, `app.reactivated`).

| Command | Payload | Rules |
|---|---|---|
| `apps:register` | `app_code` (Req, unique, immutable), `name` (Req), `company_scope` (Opt) | Creates the registration with `active` = true and `row_version` 1. A duplicate `app_code` is `CONFIG.DUPLICATE_CODE`. Event `app.registered` |
| `apps:update` | `app_id`, `expected_version` (Req), `name` and/or `company_scope` | Compare-and-set on `row_version` (`CONFIG.ROW_VERSION_MISMATCH`). Narrowing `company_scope` does not delete existing values. Event `app.changed` |
| `apps:deactivate` | `app_id`, `expected_version` (Req), `reason_text` (Req) | Sets `active` = false. Its namespaces stay readable, extension writes and acks are rejected with `APP.INACTIVE`. Event `app.changed` |
| `apps:reactivate` | `app_id`, `expected_version` (Req), `reason_text` (Req) | Sets `active` = true on an inactive registration (17 section 5). Namespaces, definitions, values, subscriptions and consumer offsets are unchanged, so the app resumes at its last acknowledged position (an expired position is `EVENT.POSITION_EXPIRED`, 5.4). Reactivating an app that is not inactive is `APP.NOT_INACTIVE`; a stale `expected_version` is `CONFIG.ROW_VERSION_MISMATCH`. Event `app.reactivated`, a `master_change_log` row with action REACTIVATE |
| `app-namespaces:create` | `app_id`, `namespace` (Req, lowercase code, immutable, not `core`) | `APP.NAMESPACE_TAKEN` if the namespace exists in the tenant. Event `app.changed` |
| `app-subscriptions:set` | `app_id`, `expected_version` (of the registration, Req), `event_types[]` (exact types or families ending in `*`) | Replaces the whole subscription set. A sensitive family (3.3) needs the caller to hold `read.audit` (else `EVENT.NOT_SUBSCRIBED`). An unknown event type: `SCHEMA.INVALID_VALUE`. Event `app.changed` |

The error codes and the permission mapping are entered in 06 (2.4, 7.4, 8.2).

### 4.4 Reads

Every get and list of an extendable entity (09, 07, 10, 11) gets `include=extensions` (all namespaces the caller may read, flat `namespace.attr` map). Lists accept filters `ext.<namespace>.<attr>` with the operators `=`, `!=`, `<`, `<=`, `>`, `>=`, `in`, `is_null` and, for TEXT, `contains` (with the scope guard of 09). `is_pii` values are read-logged (09, 5.1 and 5.2, field group `PII_EXTENSION`). Definitions: `extension-definitions:list` (permission `read.config`, 09, 2.3: there is no separate `read.ext` permission, extension reads follow the `read.*` permission of the entity, F9).

## 5. Change feed

### 5.1 What is written

Every command that changes core state writes its events **in the same transaction**: the event exists if and only if the change is committed. One command can write several events with the same `correlation_id`. VALIDATE mode writes none. Batch variants write events per successful item. **Reads write no events** (A6, 17 section 4): read access is logged only in `read_access_log` (09, 5.2), and the outbox receives one `access.summary` event per day and partner group from a scheduled job, never one per read. A read therefore never takes the `outbox_head` lock and may run on a replica (09, 2.2).

### 5.2 Position allocation

The position must be monotonic and gapless **as seen by a reader**, so a consumer can resume from the last position it processed and never miss an event.

| Step | Rule |
|---|---|
| Collect | During the command, events are collected in memory in order |
| Allocate | At the end, before commit and after every other lock (section 8), the command locks `outbox_head`, takes `last_position + 1 .. + n` and writes the events |
| Commit | The lock is held until commit. A concurrent command waits at this step. A reader that sees position p therefore sees all lower positions |
| Rollback | A rolled-back command allocates nothing, so there are no gaps |

The lock is held for the short time between the end of the command and commit. It serialises event-producing commits **per tenant**. This is the price of a simple, strict order (Q4) and is acceptable for the SME target. If throughput becomes a problem, the allocation can move to per-company positions without changing the reader contract more than adding a company key (A3).

Platform mapping (20, section 6, ADR-212): `outbox_head` is a row locked with `FOR UPDATE` last in the transaction. Positions are never taken from a database sequence, because a sequence is not transactional and is not gapless as seen by a reader. There is no message broker in release 1: `events:list` is the delivery, a webhook relay is a job that reads from the cursor, and `LISTEN/NOTIFY` is only a wake-up hint. A broker is added later, fed from the outbox, when measured feed load requires it.

### 5.3 Reading

| Operation | Notes |
|---|---|
| `events:list` | `since_position` (exclusive), `limit` (default 500, max 5,000), filters `event_type`, `company_id`, `entity_type`, `entity_key`. Returns events in position order, `next_position` and `head_position`. Only subscribed types (3.3). The families `access.*`, compliance and app events also need `read.audit` (E5, 09, 2.3a). Events with a null `company_id` are returned only to apps without `company_scope` (3.1). `read.events`, `app_code` required |
| `events:head` | The current head position of the tenant. `read.events`, `app_code` required |
| `events:ack` | Sets the consumer offset: `{consumer_id, position}`. Only forward. Permission `events.consume` |
| `events:rewind` | Sets the offset backward for replay (`{app_id, consumer_id, position}`, the one command that lowers `last_position`, 3.4). Permission `apps.manage`, no `app_code` required. Audit event `app.consumer_rewound` |

Delivery is **at-least-once**: a consumer that fails before ack reads the same events again and must be idempotent (events carry `correlation_id` and `entity_version`). Order is total per tenant, so order per entity is guaranteed. Transport (push, webhooks, queues) is built outside the core on top of `events:list`.

### 5.4 Retention of events

| Class | Rule |
|---|---|
| Audit events (`is_audit` = true) | Kept under the record class `AUDIT_LOG` of 12 (4.6), never earlier than the records they describe |
| App events (false) | Purged by the retention job after `outbox_app_event_retention_days` (setting, default 30) **and** only when every active consumer offset has passed the event. A consumer inactive for more than `outbox_consumer_max_lag_days` (default 90) no longer blocks the purge and gets `EVENT.POSITION_EXPIRED` when it returns, so it must resynchronise from the read APIs |

The purge is the only deletion of `outbox_event`. Positions are never reused.

## 6. Event types

Each event type has a fixed `entity_type`, `change` set, `summary` fields and `event_version` (3.7). Summaries never contain personal data.

| Family | Event types | Summary fields |
|---|---|---|
| Ledger | `ledger.document_posted`, `ledger.document_reversed` | `document_type`, `posting_date`, `period`, `ledger_ids`, `ledger_seq` |
| Ledger L2 changes | `ledger.line_changed` (audit, detail row is the `change_log` row) | `document_id`, `line_no`, changed field names (no values) |
| Revaluation | `revaluation.run_completed`, `revaluation.rerun` (audit, both map to view type REVALUATION_RUN in 09, 4.8) | `run_id`, `fiscal_year`, `fiscal_period`, `run_no`, `item_count` (column names of `revaluation_run`, 05) |
| Open items | `open_item.created`, `open_item.cleared`, `open_item.reset`, `open_item.changed` | `account_id`, `partner_id`, `due_date`, `amount_tc`, `currency` |
| Clearing | `clearing.posted`, `clearing.reset` | `clearing_id`, `partner_id`, `account_id` |
| Period | `period.status_changed`, `year.opened` (new: a fiscal year is opened, including history years), `year.closed`, `year.reopened` | `fiscal_year`, `period_no`, `status` |
| VAT | `vat.return_prepared`, `vat.return_filed`, `vat.settled`, `vat.settlement_reversed` (new, from `vat-returns:reverse-settlement`, 08, 7.3a), `vat.evidence_added` (new, audit, from `change-open-fields` on `TAX_EVIDENCE`, 08, 5.3) | `return_id`, `period`. `vat.evidence_added`: `document_id`, `line_no`, `evidence_no` |
| Stock | `stock.document_posted`, `stock.document_reversed`, `stock.balance_changed` | `item_id`, `warehouse_id`, `location_id`, `stock_status`, `qty_delta`. One `balance_changed` per stock key moved |
| Reservation | `reservation.created`, `reservation.adjusted` (new: INCREASE or DECREASE event of a reservation, 10, 4.5), `reservation.released`, `reservation.consumed` | `item_id`, `warehouse_id`, `qty`, `order_line_ref` |
| Orders | `order.created`, `order.changed`, `order.line_closed`, `order.cancelled`, `order.date_changed` | `order_kind`, `order_id`, `line_no`, `status`, `requested_date`, `confirmed_date` |

**Order lifecycle mapping.** The `order_event` types of 11 (PLACED, CHANGED, LINE_ADDED, LINE_CLOSED, LINE_CANCELLED, CANCELLED, CLOSED) write these outbox events: PLACED writes `order.created`. CHANGED, LINE_ADDED and LINE_CANCELLED write `order.changed` (`status` and `line_no` in the summary say what happened). LINE_CLOSED writes `order.line_closed`. CLOSED (the order closes after its last line) writes `order.changed` with `status` = CLOSED. CANCELLED writes `order.cancelled`. A change of dates writes `order.date_changed` (16, 5.2).
| Invoice issue | `invoice.issued` (audit) | `issue_id`, `invoice_id` or `document_id` (whichever the record has), `invoice_kind` (columns of `invoice_issue_record`, 12 section 11, references only) |
| Fulfilment | `delivery.posted`, `receipt.posted`, `return.posted`, `return.reversed` (reversal of `return.posted`, from `sales-returns:reverse` and `purchase-returns:reverse`, 17 B5, B6), `invoice.posted`, `invoice.reversed` (reversal of `invoice.posted`, used by 11), `dropship.confirmed`, `delivery.reversed`, `receipt.reversed`, `dropship.reversed` | `document_id`, `order_ids`, quantities by line |
| GR/IR | `gr_ir.written_off` (from `gr-ir:write-off`, document type GR_IR_WRITE_OFF), `gr_ir.write_off_reversed` (from `gr-ir:reverse-write-off`) | `document_id`, `order_ids`, `account_id`, `amount_lc` |
| Master data | `master.created`, `master.changed`, `master.deactivated`, `master.reactivated` | `entity_type`, `entity_key`, changed field names (no values) |
| Configuration | `config.version_created`, `package.applied`, `company.activated` | `entity_type`, `valid_from` |
| Extensions | `extension.changed`, `extension.defined` | `entity_type`, `entity_key`, `namespace`, `attr_key` (no values) |
| Compliance | `retention.applied`, `retention.extended`, `retention.recomputed` (new, audit: correction of the partner maximum `retention_until`, 12, 4.3), `partner.anonymised`, `partner.restricted`, `dsr.received`, `dsr.decided`, `legal_hold.changed`, `export.created`, `export.downloaded`, `deletion.scheduled`, `deletion.cancelled`, `deletion.executed` | references |
| Access | `access.summary` (new, A6: written once per day and partner group by a scheduled job, replaces `access.read_logged`, which no longer exists) | `day`, `company_id`, `field_group`, `read_count`, `actor_count`, `partner_count` (counts only, no ids of persons) |
| Chains | `anchor.created` (`chain.verified` is dropped: `chains:verify` writes no state and no event) | chain type, scope, range |
| Migration | `migration.batch_status_changed` (one event per batch status change, E6), `migration.sealed` (new, E6: the seal, carries the counts and the chain ranges the batch created) | `batch_id`, `status`. `migration.sealed`: `batch_id`, record counts per record type and `chain_ranges` (per chain type and scope: first and last `chain_seq`) |
| Apps | `app.registered`, `app.changed`, `app.reactivated` (from `apps:reactivate`, 4.5), `app.consumer_rewound` | `app_id` |

`clearing.posted` and `open_item.cleared` are also emitted by the clearings that the reversal commands create automatically (B3: the mirror open items cleared against the originals, with a clearing record and no document) and by `migration-batches:rollback` (B4), not only by `clearings:post`. `clearing.reset` and `open_item.reset` follow the same rule. Every command named in this section writes its events in the transaction of the command (5.1).

The transition to SEALED writes `migration.sealed` as its status-change event, so a seal produces one event, not two. Every other batch status change writes `migration.batch_status_changed`. `outbox:verify` (7) treats the ranges listed in `migration.sealed` as covered by that event.

Credit holds, ATP caches and MRP triggers subscribe to `ledger.*`, `open_item.*`, `stock.balance_changed`, `reservation.*`, `order.*` and `master.*` (for partner changes, `entity_type` BUSINESS_PARTNER) to refresh their own state and then read the details by API. `partner.anonymised` and `partner.restricted` are compliance events and need `read.audit` (3.3), so they are not part of such a subscription.

## 7. Audit through the outbox (K3)

`audit-events:query` (09, 4.8) reads `outbox_event` where `is_audit` = true instead of a union of logs.

| Event class | `is_audit` | Source of truth for the content |
|---|---|---|
| Chained records (ledger, clearing, period, VAT return, stock, fulfilment) | true | The chained record. The event carries `chain_ref` only |
| Revaluation runs (`revaluation.run_completed`, `revaluation.rerun`) | true | `revaluation_run` and its documents. No `chain_ref` of its own, the documents are chained and have their own `ledger.document_posted` events |
| Master and configuration changes (including `master.reactivated`) | true | `master_change_log`, `applied_config_package`. The event refers to the log row |
| Invoice issue (`invoice.issued`) | true | `invoice_issue_record`. The event is the index, the record is the evidence |
| Order changes | true | `order_change_log` and `order_event` |
| L2 changes of journal records (`ledger.line_changed`) | true | `change_log` |
| Compliance (including `dsr.received`, `dsr.decided`, `retention.recomputed`), access (`access.summary`), tenant, migration (`migration.batch_status_changed`, `migration.sealed`), app and extension-definition events, and `vat.evidence_added` | true | The event itself is the record, except where 09, 4.8 names a source table |
| Extension value changes, stock balance changes, reservations (including `reservation.adjusted`), open-item changes | false, except extension changes on `is_pii` definitions which are true | `master_change_log` for extension values |

`is_audit` governs retention (5.4) and membership of `audit-events:query` only. Who may read an event in the app feed is decided by subscription and, for `access.*`, the compliance family and app events, by `read.audit` (3.3, 09, 2.3a, E5).

All event types added in the second pass of the re-baseline (`ledger.line_changed`, `revaluation.run_completed`, `revaluation.rerun`, `dsr.received`, `dsr.decided`, `invoice.issued`, `master.reactivated`) and in the third pass (`vat.settlement_reversed`, `retention.recomputed`) are `is_audit` = true, and their `summary` holds references and field names only, no personal data (09, Q13, 14 section 11). `invoice.reversed` follows `invoice.posted` (chained fulfilment record, audit).

The detailed logs stay the evidence for field-level changes (K2 not adopted). The outbox is the single ordered index over them. `audit-events:query` returns the outbox row and, with `include=detail`, the detail row. Sources named in 09 4.8 map to event types, and sources missing from that table (reactivation, anonymisation, export download, retention extension, tenant deletion steps) are covered here from the start.

Verification: `chains:verify` does not depend on the outbox. A consistency check (`outbox:verify`) confirms for each chained record and each master change that the corresponding audit event exists. It is advisory and reports gaps. Chained records created by a migration batch are not reported as gaps: the `chain_ranges` of the batch's `migration.sealed` event cover them (E6). Permission `read.audit` (14, section 10, D10). The outbox is **not** a hash chain: the evidence is the chains and logs it points to.

## 8. Lock order and concurrency

The outbox position is allocated **last**, after every lock of 04 6.3 as amended: order rows, stock `item_company`, number counters, chain heads sorted by the tuple (chain type, company, scope key), `fiscal_year` and `fiscal_period` rows (class 5), open items by id, then `outbox_head`. This guarantees no deadlock, because nothing waits for another lock while holding `outbox_head`. Extension writes take no core lock on the owner row (its `row_version` is never bumped, 3.6): they lock the `extension_counter` row of the entity and namespace (3.6a, for `expected_version`) and the affected `extension_value` rows, plus `outbox_head` at the end. Reads take none of these locks (A6).

## 9. Permissions

| Permission | Allows |
|---|---|
| `ext.define` | Manage definitions in the caller's namespaces |
| `ext.write` | Set and clear values in the caller's namespaces |
| `read.events` | `events:list`, `events:head` for subscribed types (not the sensitive families, 3.3) |
| `read.audit` | `audit-events:query` and `outbox:verify`, and the sensitive families in the feed (`access.summary`, compliance and app events, 3.3) |
| `events.consume` | `events:ack` |
| `apps.manage` | The app registry commands of 4.5 (`apps:register`, `:update`, `:deactivate`, `:reactivate`, `app-namespaces:create`, `app-subscriptions:set`) and `events:rewind`. No `app_code` required |

There is **no `read.ext` permission** (F9): extension values and definitions are read with the `read.*` permission of the entity they extend (`read.config` for definitions, 09, 2.3). Values of `is_pii` definitions also need the PII read permission of 09.

## 10. Deltas to earlier specs

| Spec | Change |
|---|---|
| 02, 03, 05, 08, 10, 11, 12, 13 | Every state-changing command writes outbox events in its transaction (section 5.1) with the event types of section 6. No change to their hash definitions |
| 04 6.3 | `outbox_head` is the last lock. Chain heads sorted by chain type, then scope (14 K1) |
| 06 | Command envelope gets `app_code` claim. Error areas `EXT`, `APP`, `EVENT` (`POSITION_EXPIRED`, `NOT_SUBSCRIBED`, `LIMIT_EXCEEDED`). Permissions of section 9. Command catalogue gets the commands of 4.3 and 5.3 |
| 07 | Table overview lists the tables of section 3. Readiness check: none (apps are optional) |
| 09 | `include=extensions` and `ext.*` filters on gets and lists. `audit-events:query` reads the outbox (section 7). New reads `events:list`, `events:head`, `extension-definitions:list`. PII read log covers `is_pii` extension values |
| 12 | Anonymisation clears `is_pii` extension values. Subject export includes them. Retention purge of app events (5.4). Tenant deletion removes the tables of section 3 |
| Second pass (14, section 10) | Event types `ledger.line_changed`, `revaluation.run_completed`, `revaluation.rerun`, `dsr.received`, `dsr.decided`, `invoice.issued`, `master.reactivated` added to sections 6 and 7 (09, Q13). `ITEM_PARTNER` in 4.1 (16). `outbox:verify` needs `read.audit` (9, 06 permission table). Decisions Q1 to Q3 of section 12 were already applied in 4.1, 3.5 and 5.2 |
| Third pass (14, section 11) | New event types `vat.settlement_reversed` (08, 7.3a) and `retention.recomputed` (12, 4.3) in section 6, both `is_audit` = true. `invoice.reversed` named in the Fulfilment family (11 uses it). Summary fields of `revaluation.*` (`fiscal_period`, as in 05) and `invoice.issued` (`issue_id`, `invoice_id`, `invoice_kind`, as in 12 section 11) aligned. Misplaced paragraph in section 7 moved out of the table. Status v0.3 |
| 13 | Migrated records emit no events per record, so consumers are not flooded: one `migration.batch_status_changed` per batch status change and `migration.sealed` at the seal (E6). Extension values can be loaded by a normal command after the seal |

### 10.1 Deltas of fix pass 4 (17, section 4)

| Ref | Change in this spec | Effect on other specs |
|---|---|---|
| E3, E4 | `is_pii` immutable once values exist (3.5, `EXT.DEFINITION_LOCKED`). Values logged without content, `master_change_log` gets `app_id` and the action CLEAR (3.6). Values cleared at anonymisation only, never at deactivation (4.2, the conflicting "deactivating clears" sentence is gone). `EXT.ENTITY_ANONYMISED` (4.2, 4.3) | 07 (`master_change_log`), 12 (4.3) |
| E7 | `extension_value.row_version`, `expected_version` per (entity, namespace), no bump of the owner row (3.6, 4.2, 8) | none |
| E5 | Feed visibility separate from `is_audit` (3.3, 5.3, 7) | 09, 2.3a, 4.12 |
| A6 | Reads write no event. `access.read_logged` removed, `access.summary` added (5.1, 6) | 09, 5.2 |
| E6 | `migration.batch_status_changed` per status change, `migration.sealed` added, `outbox:verify` covers migrated ranges (6, 7, 10) | 09, 4.8, 13 |
| F3, F13 | App registry commands `apps:register`, `:update`, `:deactivate`, `app-namespaces:create`, `app-subscriptions:set` (4.5) | 06 catalogue and permissions |
| F9, F10 | `read.ext` removed (9). `app_code` exemptions (3.1). `APP.NOT_REGISTERED` and `APP.INACTIVE` unified (3.1). `last_position` wording (3.4). `company_scope` and tenant-level entities (3.1) | 06 (`context.app_code` row), 09 |
| Events | `event_version` column (3.7). New event types `reservation.adjusted`, `year.opened`, `migration.sealed`, `access.summary`, `vat.evidence_added`, order lifecycle mapping (6). `chain.verified` dropped. `partner.*` subscription example corrected to `master.*` (6) | 09, 4.8, 12 |
| Mechanical | `correlation_id` TEXT(100) (3.7). Lock list includes number counters and the sort tuple (8). Command name `extension-definitions:create` (3.5). PII read log reference 09, 5.1 and 5.2 (4.4). Batch limit `max_batch_items` (4.3). `ITEM_PARTNER` is a V table, key with `company_key` (4.1). Entity key example in primary key order (3.6). Retention purge sentence of 3.7 refers to 5.4 and 12 (the purge deletes app events only, retention never deletes records) | 16 |

### 10.2 Deltas of the propagation pass v0.5 (17, sections 4 and 5)

| Ref | Change in this spec | Effect on other specs |
|---|---|---|
| Propagation pass | E7: the version is a stored counter per (entity, namespace) in `extension_counter` (3.6a, key `tenant_id`, `entity_type`, `entity_key`, `namespace`), never decreasing; replaces "highest row_version + 1" and the `row_version` of 10.1. `apps:reactivate` with `APP.NOT_INACTIVE` (4.5). Section 6: `return.reversed` and the other reversal events named, `gr_ir.written_off`, `gr_ir.write_off_reversed`, `app.reactivated`, reversal and rollback clearings emit `clearing.posted` and `open_item.cleared`, SEALED writes only `migration.sealed`. `ITEM_PARTNER` extension values attach to the version row key (4.1) | 09, 4.8 (mapped), 16 |


## 11. Assumptions

| Ref | Item |
|---|---|
| A1 | Apps authenticate through the identity provider. The core only receives the principal and the `app_code` claim |
| A2 | The event stream is polled. Push transport is outside the core |
| A3 | Per-tenant serialisation of event positions is acceptable at SME volumes. Per-company positions are the fallback |
| A4 | Extension attributes are small facts. Apps keep larger structures in their own tables |

## 12. Open questions

| # | Question |
|---|---|
| Q1 | **Decided:** `extensions:set` on an `OPEN_ITEM` is allowed in hard-closed periods, because it has no financial effect. |
| Q2 | **Decided:** events are filtered at read time only. Registration restricts event types and company scope, not individual filters. |
| Q3 | **Decided:** 50 definitions per entity type and namespace, as a setting. |

## 13. ADRs raised by this spec (to be added to 01-adr-log)

- **ADR-137: Extension attributes.** Typed, namespaced, governed by owning app, outside hashes and never read by core rules. Allowed on master data, orders and open items only.
- **ADR-138: Change feed through an outbox.** Events written in the command transaction, position per tenant allocated last under a counter lock, at-least-once polling, no personal data in events, retention by class. The outbox is also the audit index (K3). Chains and logs stay the evidence.
- **ADR-139: App registration.** Apps are identified by an `app_code` claim from the identity provider. Namespaces and event subscriptions are registered in the core, consumer offsets are stored in the core.
