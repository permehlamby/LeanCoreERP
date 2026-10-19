# Inventory Management: Table Specs and Contract

Status: DRAFT v0.6 for review, re-baselined with the decisions of 14 (sections 8, 9, 10 and 11), specs 15 and 16 and the rulings of 17, sections 4 and 5 (fix pass 4, deltas at the end of section 9). v0.6 (17, section 5, propagation pass): synthetic FIFO layer of a migrated receipt (4.3), `INVENTORY.VALUE_REQUIRED` and `INVENTORY.CURRENCY_MIXED` (5.3, 6.2), role `GR_IR_WRITE_OFF` (3.9, 7), lock order renumbered to the seven classes of 04, 6.3 (6.4). v0.5: as-of stock reads sum by posting date (C1), negative stock on the cost key (C2), FIFO rules for adjustments, transfers and `RETURN_OUT` (C3), `stock_cost_layer.layer_date` (C4), location rules (C5), `landed-costs:reverse` test and columns (B7), FIFO receipt reversal (B8), one phase model with the locks at P7, lock tuple (chain type, company, scope key), one transaction currency per stock document, `journal_document.origin` STOCK. v0.4 (14, section 11): `landed-costs:reverse` rejects with `INVENTORY.ALLOCATION_NOT_REVERSIBLE` when the on-hand part is consumed, the `INVENTORY.ALLOCATION_*` codes are adopted, `purchase-invoices:reverse` rejects with the adopted `INVENTORY.ALLOCATION_EXISTS` while allocations stand, Q16 and Q17 closed. v0.3 added `landed-costs:reverse` (D1 follow-up), the `cost_source` split of partly invoice-covered receipts (D6), the counter position in the lock order (D3) and `chain_anchor` by `scope_key` (K1). Builds on 00-design-principles, ADR-001 to ADR-174 and specs 02 to 16. Main references: P1 and P12 (inventory posts to finance only through the core API, via business events and posting rules), ADR-012, ADR-040 (GL open-item accounts for GR/IR), ADR-052 (`detail_digest`), ADR-085 (customs outside finance). Amended by: ADR-145 (derived documents reversible only through the owning command), ADR-147 (single lock order), ADR-149 (role table), ADR-150 (closed list of cost exceptions), ADR-158 (`chain_head`), ADR-159 (outbox events), ADR-143 (location types), ADR-142 (`item_partner`), ADR-140 (`supply-demand:query`).
Scope: units, items, item groups, warehouses and locations (with location types), lot and serial tracking, the stock ledger with valuation (moving average and FIFO), reservations, stock status, count-based adjustments (stocktake sessions are app-owned), landed costs, account determination, the atomic coupling to the finance core, commands and reads.
Out of scope (order management spec, 11): purchase and sales orders, receipts and deliveries against orders, invoice matching, returns, dropship, pricing, customs declarations. Production and bills of material are not in release 1. Retention, export and anonymisation are specified in 12 and add no rules to this spec. Supply and demand reads, item-partner terms and business calendars are specified in 16, the change feed in 15.
Conventions, lock classes (L1, L2, L3, S), types and sign convention as in 02, section 1. Pattern letters (V, M, E, R) as in 07, section 2. Command envelope, idempotency and the error model as in 06, reads as in 09.

## 1. Decisions taken

| Topic | Decision |
|---|---|
| Scope R1 | Item master with units, warehouses and locations, stock ledger, valuation, lot and serial tracking, adjustments including count-based adjustments (stocktake sessions are owned by apps), stock-level reservations, landed costs, stock status (available, quarantine, blocked) |
| Valuation | Moving average (default) and FIFO. Standard cost is not in R1 |
| Accounting | Perpetual: every valued stock document posts a journal immediately |
| Coupling | Atomic. A stock command and the journal it derives commit in one transaction, or neither. Stock value and the GL cannot diverge |
| Negative stock | Rejected by default, allowed per item and warehouse by a setting. Defined on the **cost key** only (5.4, C2) |
| Costs | Costs are computed by the core. The only exceptions are the closed list of 5.6, each marked on the movement with `cost_source` COMPUTED, SUPPLIED, INVOICE or MIGRATION (D6, ADR-150) |
| Reversal | A derived document (stock document of an order flow, cost adjustment from a supplier invoice, `STOCK_OPENING`, and the journals derived from stock documents) is reversed only through the command that owns it. `stock-documents:reverse` rejects them (5.5, D1, ADR-145) |
| Locations | `warehouse_location.location_type` STANDARD, TRANSIT or QUARANTINE. TRANSIT stock is valued but not available. TRANSIT and QUARANTINE accept transfers in, status changes and adjustments and reject receipts and issues (3.6, C5). Two-step transfers use existing transfers (16, ADR-143) |
| Backdating | The movement date must lie in a postable finance period. Costing follows processing order. No retroactive recosting |
| Cost scope | A setting on the **group**: cost per company, item and batch, or per warehouse, item and batch |
| On-hand | No balance table. On-hand is derived from the stock ledger. Each movement row stores the running balance after it (processing order), so the balance **now** is a single indexed lookup of the latest row. **As-of reads for a past date sum quantity and value by posting date and processing order** (6.3, C1): the running columns are used only for the present. Over-issue is prevented by a lock on the item |
| Evidence | The stock ledger is hash-chained per company and anchored with the other chains. The chain head is a row of the single `chain_head` table (chain type STOCK, K1, ADR-158) |
| Events | Every stock command writes its outbox events in the same transaction (15, 5.1, ADR-159) |

## 2. Overview

### 2.1 Tables

| # | Table | Pattern | Role |
|---|---|---|---|
| 1 | `ref_unit` | R | Global unit codes |
| 2 | `item_group` | M | Grouping for account determination and reporting |
| 3 | `item` | M | Item master |
| 4 | `item_unit_conversion` | M | Item-specific unit conversions (override) |
| 5 | `unit_conversion` | M | Tenant-level general conversions between units of one dimension |
| 6 | `item_company` | M | Item settings per company (valuation method). Lock row for stock commands |
| 7 | `warehouse`, 8 `warehouse_location` | M | Physical structure. Locations have a `location_type` (3.6) |
| 9 | `item_warehouse` | M | Item settings per warehouse (negative stock) |
| 10 | `item_tracking` | M | Lot and serial numbers |
| 11 | `account_determination` | V | Maps inventory account roles to accounts by keys |
| 12 | `stock_document` | E + S | Header of a stock transaction, chain node |
| 13 | `stock_movement` | E | Stock ledger lines with valuation and running balances |
| 14 | `stock_cost_layer` | E | FIFO layers |
| 15 | `stock_layer_consumption` | E | Consumption of layers by issues |
| 16 | `stock_reservation`, 17 `stock_reservation_event` | E | Reservations as events |
| 18 | `landed_cost_allocation`, 19 `landed_cost_allocation_line` | E (+ S status) | Allocation of additional costs to receipts |
| 20 | `item_partner` | V | Supplier and customer terms per item. Specified in 16, 3.1 (information only, never enforced) |

### 2.2 Layering on the core

- Inventory tables lead with `tenant_id` and `company_id` where company-scoped (ADR-002, ADR-014). Items are tenant-level, stock is always company-level.
- A stock document that has value posts a journal through the **normal finance pipeline** (06, P2 to P7) in the same transaction. Inventory never writes finance tables itself.
- Warehouses belong to one company. Stock cannot be transferred between companies: that is an intercompany sale and purchase in order management (Q8).

## 3. Master data and configuration

### 3.1 `ref_unit` (R, global)

PK: `unit_code` (UN/ECE Recommendation 20 style). `name`, `dimension` (COUNT, MASS, VOLUME, LENGTH, AREA, TIME, OTHER), `valid_from`, `valid_to`. Platform-maintained like `ref_currency` (ADR-069).

### 3.2 `item_group` (M)

PK: (`tenant_id`, `item_group_id`). Unique code. `name`, `active`. Used by account determination and reports. Default dimensions for derived journals (Q14 decided): `default_cost_center_id`, `default_profit_center_id`, `default_business_unit_id` (FK `dimension_value`, nullable, validated against the standard role of the value's type).

### 3.3 `item` (M)

PK: (`tenant_id`, `item_id`). Unique (`tenant_id`, `item_no`).

| Column | Type | Null | Notes |
|---|---|---|---|
| item_no | CODE | N | Business key. Mutable only while no stock document uses the item (as `account_no`) |
| name | TEXT(200) | N | |
| description | TEXT(1000) | Y | |
| item_type | CODE | N | IAP. GOODS (stocked), SERVICE (never stocked), NON_STOCK (purchased, expensed, never stocked) |
| base_unit | CODE | N | IAP. FK `ref_unit`. All quantities are converted to it |
| quantity_scale | SMALLINT | N | Decimal places of base quantities (0 for pieces, 3 for kilograms). IAP |
| item_group_id | ID | N | FK `item_group` |
| tax_class_id | ID | Y | Default tax class for order and invoice lines, and the source of the line's `tax_class_id` for tax determination (07, 8.5) |
| tracking_mode | CODE | N | IAP. NONE, LOT, SERIAL |
| expiry_tracked | BOOL | N | IAP. Lots need an expiry date. Only for LOT |
| weight_kg, volume_m3 | DECIMAL | Y | Basis for landed cost allocation by weight or volume |
| standard_identifier, standard_identifier_scheme | TEXT(50), CODE | Y | Item standard id for e-invoice lines (e.g. GTIN, 12, 9.2) |
| commodity_code | TEXT(20) | Y | Customs tariff reference, informational (customs data stays out of finance, ADR-085) |
| status | CODE | N | ACTIVE, BLOCKED, INACTIVE |

### 3.4 `item_unit_conversion` (M)

PK: (`tenant_id`, `item_id`, `unit_code`). `factor_to_base` (RATE: one `unit_code` = factor base units). Item-specific conversions, mainly for packaging units (pallet, carton), which override any general conversion.

`unit_conversion` (M, tenant-level, Q11 decided): PK (`tenant_id`, `from_unit`, `to_unit`). `factor` (RATE: one `from_unit` = factor `to_unit`). Both units must have the same `dimension` in `ref_unit`. Used for general conversions such as kilogram to gram.

Lookup for a line in unit U of an item with base unit B: (1) U equals B: factor 1. (2) The item's own row for U. (3) The tenant row U to B, or the inverse of B to U. No chaining of conversions. Nothing found: `INVENTORY.UNIT_CONVERSION_MISSING`. A converted quantity that does not fit the base quantity scale exactly (fraction beyond `quantity_scale` after conversion) is rejected with `INVENTORY.QUANTITY_SCALE_EXCEEDED` (Q12).

### 3.5 `item_company` (M)

PK: (`tenant_id`, `company_id`, `item_id`). The row **is the lock row** of the item in a company (6.4).

| Column | Type | Null | Notes |
|---|---|---|---|
| valuation_method | CODE | N | IAP once the item has a stock document in the company. MOVING_AVERAGE or FIFO. Ignored for non-stock items |
| default_warehouse_id | ID | Y | |
| active | BOOL | N | A company must activate an item before it can be stocked there |

### 3.6 `warehouse` and `warehouse_location` (M)

`warehouse`: PK (`tenant_id`, `company_id`, `warehouse_id`). Unique code. `name`, `country_code` (the country the goods ship from. It is the default **origin country** for tax determination of sales from this warehouse, 07 Q1, 08), `address` (organisation data), `active`, and default dimensions for derived journals `default_cost_center_id`, `default_profit_center_id`, `default_business_unit_id` (Q14 decided). A default must be a dimension value usable in the warehouse's company (`company_id` null or equal), checked on write.
`warehouse_location`: PK (`tenant_id`, `company_id`, `warehouse_id`, `location_id`). `location_code`, `name`, `active`, `location_type` (CODE, N, L1: STANDARD, TRANSIT or QUARANTINE, cannot change once any stock movement refers to the location, `MASTER.LOCATION_TYPE_LOCKED`, 16, 6). Locations are optional: stock without a location is stock of the warehouse and counts as STANDARD.

| Location type | Rule (16, 6.2, ADR-143) |
|---|---|
| STANDARD | As before |
| TRANSIT | Stock is **not available**: excluded from `availability:query` and from available on-hand, shown as in transit. Not reservable, not deliverable by sales deliveries. Valued normally, stays in the item balance and the inventory account. Accepts `TRANSFER_IN`, `STATUS_CHANGE` and `ADJUSTMENT` movements. Rejects `RECEIPT` and `ISSUE` movements |
| QUARANTINE | Not reservable, not deliverable. Accepts `TRANSFER_IN`, `STATUS_CHANGE` and `ADJUSTMENT` movements, rejects `RECEIPT` and `ISSUE` movements. Accepts only stock status QUARANTINE or BLOCKED (`INVENTORY.LOCATION_STATUS_MISMATCH`) |

**Movement rules per location type (C5, 17).** For TRANSIT and QUARANTINE, the accepted document types are TRANSFER (the `TRANSFER_IN` line into the location, and the `TRANSFER_OUT` line out of it, which is the second step of a two-step transfer), STATUS_CHANGE and ADJUSTMENT. A `RECEIPT` or `ISSUE` movement on such a location is rejected with `INVENTORY.LOCATION_NOT_AVAILABLE`. Other movement types that never arise from a caller (`OPENING_IN` of the migration, 13, 6.4, `RETURN_IN`, `RETURN_OUT`, `COST_ADJUST`) follow the status rule only. The QUARANTINE status rule applies to every movement into the location.

A two-step transfer is two ordinary `STOCK_TRANSFER` documents: into a TRANSIT location, then out of it into the target location. The core adds no in-transit document or process. Reserving or delivering from a TRANSIT or QUARANTINE location is rejected with `INVENTORY.LOCATION_NOT_AVAILABLE`.

### 3.7 `item_warehouse` (M)

PK: (`tenant_id`, `company_id`, `warehouse_id`, `item_id`). `allow_negative_stock` BOOL (default false). `min_quantity`, `reorder_quantity` (informational reorder parameters, kept, S7 unchanged, planning is an app concern). Supplier terms per item (lead time, minimum order quantity, order multiple, origin country) are in `item_partner` (16, 3.1).

### 3.8 `item_tracking` (M)

Lot and serial identities. PK: (`tenant_id`, `item_id`, `tracking_id`). Unique (`tenant_id`, `item_id`, `tracking_no`).

| Column | Type | Null | Notes |
|---|---|---|---|
| tracking_no | TEXT(50) | N | Lot number or serial number, IAP |
| kind | CODE | N | LOT or SERIAL, from the item's `tracking_mode`, IAP |
| expiry_date | DATE | Y | Required for expiry-tracked lots. IAP once used |
| production_date | DATE | Y | |
| supplier_ref | TEXT(100) | Y | Supplier's lot or serial reference |
| status | CODE | N | ACTIVE or RECALLED. A recalled tracking row blocks issues (`INVENTORY.TRACKING_RECALLED`) |

Rows are created by the stock command that first receives the lot or serial, in the same transaction (3.8.1). A serial exists once per item in the tenant, so the same serial can be seen in two companies' ledgers only after an intercompany sale (Q9).

#### 3.8.1 Tracking rules

- Items with `tracking_mode` NONE carry no tracking. Items with LOT or SERIAL must carry a `tracking_no` on every movement line (`INVENTORY.TRACKING_REQUIRED`).
- SERIAL: each movement line has quantity of exactly one base unit (`INVENTORY.SERIAL_QUANTITY_INVALID`). A serial receipt of 10 units is 10 lines. A serial cannot be received while on hand, and cannot be issued unless on hand.
- Expired lots (`expiry_date` before the movement's posting date) can be issued, with the warning `INVENTORY.LOT_EXPIRED` in the response (Q15 decided: warn only). The movement stores `lot_expired` = true (4.2), so expired issues are reportable. Preventing shipment of expired goods is an app control, the core records it.

### 3.9 `account_determination` (V)

Posting rules for inventory events, expressed as account determination (ADR-012, Q4). The journal structure of each event is fixed (section 7). Configuration chooses the **accounts**.
PK: (`tenant_id`, `company_id`, `determination_id`, `valid_from`).

| Column | Type | Null | Notes |
|---|---|---|---|
| role_code | CODE | N | `INVENTORY`, `COGS`, `GR_IR`, `PRICE_VARIANCE`, `ADJUSTMENT_GAIN`, `ADJUSTMENT_LOSS`, `NEG_STOCK_VARIANCE`, `LANDED_COST_CLEARING`, `STOCK_TRANSFER_CLEARING`, and for order management (11, 3.3) `AR_CONTROL`, `AP_CONTROL`, `REVENUE`, `PURCHASE_EXPENSE`, `DROPSHIP_CLEARING`, `GR_IR_WRITE_OFF` |
| item_group_id | ID | Y | Null = any |
| warehouse_id | ID | Y | Null = any |
| partner_id | ID | Y | Null = any. Used by the order management roles (`AR_CONTROL`, `AP_CONTROL`, `REVENUE`), 11, 3.3 |
| item_id | ID | Y | Null = any |
| valid_from, valid_to | DATE | N, Y | Against the stock document's posting date |
| priority | SMALLINT | N | Tie-breaker |
| account_id | ID | N | FK `gl_account`. The account's open-item mode must be the one required by the role (table below, checked on write, error code defined with the role table in 07) |

Required open-item mode per role (D5, ADR-149, the authoritative role table is in 07, 6.3):

| Role | Required open-item mode |
|---|---|
| `GR_IR`, `DROPSHIP_CLEARING` | `GL_OPEN_ITEM` (no partner, cleared by matching, 11) |
| `AR_CONTROL`, `AP_CONTROL` | `CONTROL` |
| `INVENTORY`, `COGS`, `PRICE_VARIANCE`, `ADJUSTMENT_GAIN`, `ADJUSTMENT_LOSS`, `NEG_STOCK_VARIANCE`, `LANDED_COST_CLEARING`, `STOCK_TRANSFER_CLEARING`, `REVENUE`, `PURCHASE_EXPENSE`, `GR_IR_WRITE_OFF` | `NONE` |

Matching is the same algorithm as tax determination (07, 8.4): the most specific non-null key set wins, then priority, overlap of equal specificity and priority is rejected on write (`CONFIG.OVERLAPPING_RULE`), and no match rejects the posting with `INVENTORY.ACCOUNT_DETERMINATION_MISSING` naming role and keys. **Role default for `DROPSHIP_CLEARING`:** when no rule of the role `DROPSHIP_CLEARING` matches (no dedicated rule exists), the rules of the role `GR_IR` apply to the dropship journal (11, 5.3). `INVENTORY.ACCOUNT_DETERMINATION_MISSING` is returned for `DROPSHIP_CLEARING` only if no `GR_IR` rule matches either, and then names the role `GR_IR`. Company readiness (07, 11.3) checks that every role needed by the enabled features has at least a catch-all rule. The matched `account_id` and `determination_id` are stored on the journal line's source reference for traceability.

### 3.10 Group setting

`company_group` (07, 5.3) gains `inventory_cost_scope` (IAP once any member company has a stock document): COMPANY or WAREHOUSE. It applies to all companies of the group at the movement's posting date. A company outside any group uses COMPANY. A `group_membership` change that would give a company with stock documents a different cost scope is rejected (`CONFIG.COST_SCOPE_CONFLICT`, Q10).

## 4. Transaction data

### 4.1 `stock_document` (E + S)

PK: (`tenant_id`, `company_id`, `stock_document_id`)
Unique: (`tenant_id`, `company_id`, `number_series_id`, `fiscal_year`, `sequence_no`)
Unique: (`tenant_id`, `idempotency_key`), (`tenant_id`, `company_id`, `chain_seq`)

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| tenant_id, company_id, stock_document_id | ID | N | L1 | |
| document_type | CODE | N | L1 | `STOCK_RECEIPT`, `STOCK_ISSUE`, `STOCK_TRANSFER`, `STOCK_ADJUSTMENT`, `STOCK_STATUS_CHANGE`, `STOCK_COST_ADJUSTMENT`, `STOCK_RETURN`, `STOCK_OPENING` (migration only, values and FIFO layer dates supplied, derives debit inventory credit `MIGRATION_CLEARING`, 13, 6.4), `STOCK_REVERSAL`. `STOCK_RETURN` is posted only by order management returns (11, 5.4) and values each movement from the original movement it refers to (`cost_source` COMPUTED, 5.6). `STOCK_COST_ADJUSTMENT` documents derived from purchase invoices use the invoice's journal document as their derived journal (external journal mode, 11, 7.2) |
| number_series_id, fiscal_year, sequence_no, document_number | | N | L1 | Same mechanism as 04: series assigned by document type, gapless, formatted number |
| fiscal_period | INT | N | L1 | Resolved from the posting date, like journal documents |
| document_date, posting_date | DATE | N | L1 | Posting date must be postable in finance (window, period status, 04, 7.1) |
| posted_at | TS | N | L1 | |
| reason_code | CODE | Y | L1 | Required for adjustments and reversals |
| status | CODE | N | S | `posted` or `reversed`. Outside the hash |
| reversal_of_stock_document_id | ID | Y | L1 | Set on every mirror document (`STOCK_REVERSAL`), with `reason_code` (D1) |
| reversed_by_stock_document_id | ID | Y | S | |
| journal_document_id | ID | Y | L1 | The derived finance document. Null for movements without value (quantity-only) |
| soft_close_override | BOOL | N | L1 | As in 02 |
| currency_tc | CODE | Y | L1 | The **one transaction currency** of the document (ADR-035): every movement line with a `value_tc` carries this currency. Null if no line has a transaction value. A request with lines in several transaction currencies is rejected at P1 with `INVENTORY.CURRENCY_MIXED` (split into one document per currency). The derived journal uses it as its transaction currency (section 7) |
| source_system, source_document_ref | CODE, TEXT(100) | N, Y | L1 | P13. Order management sets the order and delivery references here |
| idempotency_key, command_name, posted_by, correlation_id | | N | L1 | 06 |
| header_text | TEXT(200) | Y | L2 | |
| movement_count | INT | N | L1 | |
| detail_digest | BINARY(32) | Y | L1 | `row_hash` of the order management fulfilment document that caused this stock document (delivery, receipt, return, 11, 5.5). Null otherwise. Part of the hash |
| movement_digest | BINARY(32) | N | L1 | SHA-256 over all `stock_movement` and `stock_layer_consumption` L1 columns in fixed order |
| chain_seq, prev_hash, row_hash, hash_version | | N | L1 | Chain per company, same scheme as 02, section 9 |

Hash: `row_hash` covers all L1 columns, including `journal_document_id` and `movement_digest`. The derived journal document's `detail_digest` (02, ADR-052) is set to this `row_hash`. There is no circularity: the journal id is allocated first, the stock hash does not include the journal hash, and the journal binds the stock hash.

### 4.2 `stock_movement` (E)

PK: (`tenant_id`, `company_id`, `stock_document_id`, `line_no`).
Indexes (the balance lookups, 6.3): (`tenant_id`, `company_id`, `item_id`, `cost_warehouse_id`, `tracking_id`, `chain_seq_pos`) for the cost key, and (`tenant_id`, `company_id`, `item_id`, `warehouse_id`, `location_id`, `tracking_id`, `stock_status`, `chain_seq_pos`) for the stock key, where `chain_seq_pos` is (`chain_seq` of the document, `line_no`) and each lookup reads the highest position. Also (`tenant_id`, `company_id`, `posting_date`, `item_id`) for the ledger report and (`tenant_id`, `item_id`, `tracking_id`) for traceability.

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| tenant_id, company_id, stock_document_id, line_no | | N | L1 | |
| movement_type | CODE | N | L1 | `RECEIPT`, `ISSUE`, `TRANSFER_OUT`, `TRANSFER_IN`, `ADJUST_IN`, `ADJUST_OUT`, `STATUS_OUT`, `STATUS_IN`, `COST_ADJUST`, `RETURN_IN`, `RETURN_OUT`, `OPENING_IN` (migration, 13, 6.4) |
| cost_source | CODE | N | L1 | COMPUTED, SUPPLIED, INVOICE or MIGRATION (5.6, D6, ADR-150). COMPUTED unless a listed exception applies |
| valuation_ref_document_id, valuation_ref_line_no | ID, INT | Y | L1 | Set on `RETURN_IN` and `RETURN_OUT`: the original movement whose value (proportional to the returned quantity) is used (11, 5.4). Listed in 5.6 (10 Q5, 11 Q6). For a return of a **migrated** delivery or receipt, which has no original movement, the reference names the fulfilment line (`sales_delivery_line` or `purchase_receipt_line`) and the value is its `value_lc` proportional to the returned quantity (11, 5.4, D5). For FIFO, a `RETURN_IN` creates a new layer at the original cost, and a `RETURN_OUT` consumes the layer of the referenced original receipt (5.3), for a migrated receipt its synthetic layer (4.3) |
| posting_date | DATE | N | L1 | Copied from the document (as `journal_line`, 02 Q3) |
| item_id | ID | N | L1 | |
| warehouse_id, location_id | ID | N, Y | L1 | |
| tracking_id | ID | Y | L1 | Lot or serial, per the item |
| stock_status | CODE | N | L1 | AVAILABLE, QUARANTINE, BLOCKED |
| quantity | DECIMAL | N | L1 | Signed, in `unit_code`. Receipts positive, issues negative |
| unit_code | CODE | N | L1 | |
| base_quantity | DECIMAL | N | L1 | Signed, in the item's base unit at its `quantity_scale`. The basis of all stock arithmetic |
| value_lc | AMT | N | L1 | Signed change of inventory value in the company's local currency. Zero for quantity-only movements |
| unit_cost_lc | RATE | Y | L1 | Informational: `value_lc / base_quantity` rounded. The value is authoritative |
| value_tc, currency_tc | AMT, CODE | Y | L1 | Transaction value of priced receipts (order price), kept for traceability. `currency_tc` equals the `currency_tc` of the stock document (one transaction currency per document, 4.1). The LC value uses the rate of the posting date (ADR-019) |
| cost_warehouse_id | ID | Y | L1 | The cost key's warehouse: the movement's warehouse if the group's cost scope is WAREHOUSE, else null |
| cost_qty_after | DECIMAL | N | L1 | Quantity of the cost key after this movement |
| cost_value_after_lc | AMT | N | L1 | Value of the cost key after this movement |
| stock_qty_after | DECIMAL | N | L1 | Quantity of the stock key after this movement |
| lot_expired | BOOL | N | L1 | True if the lot was expired at the posting date of an issue (3.8.1) |
| count_ref | TEXT(100) | Y | L1 | Reference of the count (stocktake) the adjustment stems from, set by the caller on count-based adjustments (6.2) |
| expected_book_quantity | DECIMAL | Y | L1 | On count-based adjustments: the book quantity the caller saw when counting (stock key, base unit), kept as evidence |
| source_line_ref | TEXT(100) | Y | L1 | Order or delivery line reference (P13) |
| reversal_of_line_no | INT | Y | L1 | On `STOCK_REVERSAL` documents |
| line_text | TEXT(200) | Y | L2 | |

Rules: a TRANSFER is two lines (`TRANSFER_OUT`, `TRANSFER_IN`) in one document, equal base quantity and value. A status change is two lines (`STATUS_OUT`, `STATUS_IN`) on the same warehouse, location, tracking. Both have value zero effect on the item total. `COST_ADJUST` lines have base quantity zero and a non-zero value (landed cost).

### 4.3 `stock_cost_layer` (E) and `stock_layer_consumption` (E)

For items with `valuation_method` FIFO.

`stock_cost_layer`: PK (`tenant_id`, `company_id`, `stock_document_id`, `line_no`) (the layer is its creating receipt line). `item_id`, `cost_warehouse_id`, `tracking_id`, `layer_seq` (BIGINT, gapless per cost key, from the movement order), `layer_date` (DATE, L1, C4: the date of the layer for ageing and reports. A layer created by a receipt, return or transfer takes the posting date of its movement, a layer of a `STOCK_OPENING` document takes the `layer_date` supplied by the migration, 13, 5 and 6.4, and a layer created by a FIFO transfer keeps the `layer_date` of the layer it came from, 5.3. It never changes the consumption order, which follows `layer_seq`), `quantity_base` (original), `unit_cost_lc` (RATE: the layer value divided by quantity, at an increased scale), `value_lc` (original, AMT). Also an S column `exhausted` BOOL (default false, Q1 decided): set to true by the issue command in the same transaction when the layer's consumption reaches its quantity. It is outside the hash, recomputed and verified by `chains:verify` from the consumption rows, and set back to false only by a reversal that returns quantity to the layer. Index (`tenant_id`, `company_id`, `item_id`, `cost_warehouse_id`, `tracking_id`, `exhausted`, `layer_seq`).

`stock_layer_consumption`: PK (`tenant_id`, `company_id`, `stock_document_id`, `line_no`, `layer_doc_id`, `layer_line_no`). The issue line consuming, the layer consumed, `quantity_base`, `value_lc`. Insert only.

**Synthetic layer of a migrated receipt (17 section 5, FIFO edge a).** A migrated receipt has no stock document, so it has no layer of its own. For a FIFO item the migration therefore creates a **synthetic cost layer** from the receipt's legacy `value_lc` (11, 5.2, D5): the `OPENING_IN` movement of the `STOCK_OPENING` document (13, 6.4) that carries the received goods is valued at the legacy `value_lc` of the receipt line for the receipt quantity, takes the receipt date as `layer_date`, and its layer carries the L1 column `receipt_legacy_ref` (TEXT(100), null on all other layers: the `legacy_ref` of the migrated receipt line, resolved through `legacy_id_map`, 13, 3.5). The layer is an ordinary layer of the cost key (it holds the quantity, there is no second layer for the same goods) and is consumed by issues in `layer_seq` order like any other. A `RETURN_OUT` that references the migrated receipt line consumes exactly this layer (5.3), so the returned goods leave at the legacy receipt value and the `INVENTORY.RECEIPT_CONSUMED` test of 5.3 applies to it. For moving-average items no layer exists and the return value is the `value_lc` of the receipt line proportional to the returned quantity (4.2).

The remaining quantity of a layer is `quantity_base` minus the sum of its consumption (computed, the truth). The `exhausted` flag is a cache that lets an issue find the oldest layers with stock left through one index.

### 4.4 Stock chain head (K1, ADR-158)

The head of the stock chain is the row (`tenant_id`, `company_id`, `chain_type` = STOCK, `scope_key` empty) of the single `chain_head` table (columns `last_seq`, `last_hash`, `hash_version`, `updated_at`, all S; defined in 02, 9). Locked by every stock command in the lock order of 6.4 (heads sorted by the tuple (chain type, company, scope_key)). Anchored in `chain_anchor` with `chain_type` = STOCK and `scope_key` empty (02, 9.5).

### 4.5 Reservations

`stock_reservation` (E): PK (`tenant_id`, `company_id`, `reservation_id`). `item_id`, `warehouse_id`, `tracking_id` (optional: reserve a specific lot), `demand_ref_type` (ORDER_LINE, MANUAL), `demand_ref` (TEXT(100)), `unit_code`, `created_at`, `created_by`, `correlation_id`, `idempotency_key`.
`stock_reservation_event` (E): PK (`tenant_id`, `company_id`, `reservation_id`, `event_no`). `event_type` (CREATE, INCREASE, DECREASE, RELEASE, CONSUME), `base_quantity` (signed change of the reserved quantity), `event_at`, `event_by`, `reason_code`, `stock_document_id` (set for CONSUME: the issue that fulfilled it).

Reserved quantity of a reservation = sum of its events. Available = on-hand with status AVAILABLE minus the reserved quantity of active reservations (computed, with the item lock). Stock in TRANSIT or QUARANTINE locations is never available and cannot be reserved (`INVENTORY.LOCATION_NOT_AVAILABLE`, 3.6). Reservations are created explicitly by `reservations:create` (apps, ATP) or, if the company setting `auto_reserve` is on (11, 3.1, S3, ADR-157), best effort at order creation. A reservation cannot exceed available at creation (`INVENTORY.RESERVATION_EXCEEDS_AVAILABLE`). An issue against a reservation consumes it. An issue by someone else that would eat into reserved stock is rejected unless it is the reserving demand (`INVENTORY.STOCK_RESERVED`).

### 4.6 Stocktake (app-owned, Q13 decided)

The core has no stocktake tables. Count sessions, counting lists and count entry are app-owned (ADR-013 spirit: workflow lives in apps). The core provides one thing: a **count-based adjustment** in `stock-documents:post` (6.2), which sets the stock key to a counted quantity under the item lock and compares the caller's expected book quantity, so counting while stock moves cannot produce a wrong adjustment. The result is an ordinary STOCK_ADJUSTMENT document with `count_ref` on its lines.

### 4.7 Landed costs

`landed_cost_allocation` (E): PK (`tenant_id`, `company_id`, `allocation_id`). `stock_document_id` (the STOCK_COST_ADJUSTMENT document), `cost_source_document_id` and `cost_source_line_no` (the finance line carrying the cost, normally the freight or duty invoice line on the `LANDED_COST_CLEARING` account), `allocation_method` (VALUE, QUANTITY, WEIGHT, VOLUME), `total_cost_lc` (AMT, negative on a mirror allocation), `reversal_of_allocation_id` (ID, L1, null unless the row is the mirror allocation created by `landed-costs:reverse`, 6.8), `reason_code` (CODE, L1, set on the mirror allocation, 6.8), `status` (CODE, **S**: POSTED, REVERSED or MIRROR. POSTED becomes REVERSED when a mirror exists, a mirror allocation has the status MIRROR), `created_at`, `created_by`.
`landed_cost_allocation_line` (E): PK (`tenant_id`, `company_id`, `allocation_id`, `line_no`). The target `receipt_document_id` and `receipt_line_no`, `allocated_cost_lc`, `on_hand_part_lc` (the part added to inventory), `consumed_part_lc` (the part already issued, posted to cost of goods sold). On a mirror allocation the lines carry the negated amounts. Rules in 6.6 and 6.8.

## 5. Valuation rules

All arithmetic is in base quantity and local currency. Rounding is at the currency scale for values and the item's `quantity_scale` for quantities.

### 5.1 Cost key

The cost key is (company, [warehouse if the group's `inventory_cost_scope` is WAREHOUSE], item, tracking_id). Each movement stores the running quantity and value of its cost key after it. The latest movement row of a cost key is its balance.

### 5.2 Moving average

- **Receipt** (positive, valued): value added = `value_lc`. `cost_qty_after` = before + quantity. `cost_value_after_lc` = before + value. The average is value divided by quantity (shown as `unit_cost_after`, derived).
- **Issue** (negative): value of the issue = `ROUND(cost_value_before × issued_quantity / cost_qty_before)`, with one exception: when the issue takes the whole remaining quantity, it takes the whole remaining value, so value never lingers on zero stock. `value_lc` is the negative of that.
- **Transfer** within a company with cost scope COMPANY: out and in at the same value, the cost key does not change in value. With cost scope WAREHOUSE: out at the source warehouse's average, in at the same value at the destination, which changes the destination's average.
- **Adjustment in** without cost: valued at the current average (`cost_source` COMPUTED) or at the supplied value if given (`cost_source` SUPPLIED, 5.6). **Adjustment out**: as an issue. For FIFO items see 5.3.
- **Zero-quantity cost adjustment** (`COST_ADJUST`): changes value only, quantity unchanged, so the average moves (6.6).

### 5.6 Cost sources (D6, ADR-150)

Costs are computed by the core from base quantity and local currency. The following **closed list** of exceptions is the only place where a value does not come from the cost computation of 5.2 to 5.4. Every movement carries `cost_source` (4.2), so each exception is visible on the document and reportable.

| `cost_source` | Where it applies |
|---|---|
| COMPUTED | The default: the value is computed by 5.2 to 5.4. Includes priced receipts (caller sends the transaction price only, the core converts), the part of an order receipt not covered by an earlier invoice (see 5.6.1), issues, landed cost allocations (6.6), and **returns** (`RETURN_IN`, `RETURN_OUT`), whose value is derived by the core from the referenced original movement through `valuation_ref_*` (11, 5.4) |
| SUPPLIED | A value supplied by the caller on an adjustment in (5.2, "or the supplied value if given") |
| INVOICE | A receipt line valued at the value of an earlier supplier invoice (11, 7.1, ADR-115). A partly covered receipt is split: the covered quantity is an INVOICE line, the rest a COMPUTED line (5.6.1). Also `COST_ADJUST` movements derived from supplier invoice price differences (11, 7.2) |
| MIGRATION | `OPENING_IN` movements and FIFO layers of `STOCK_OPENING`, whose values and layer dates are supplied by the migration (13, 6.4) |

No other value can be supplied. A request field that would supply a cost outside this list is rejected (`expected_cost` is not accepted, 6.2).

#### 5.6.1 Partly invoice-covered receipt (D6)

`cost_source` is per movement line, so one line has exactly one source. A purchase receipt line (11, 7.1) that is only partly covered by an earlier supplier invoice (two-way invoicing) is therefore **split into two movement lines** of the same `STOCK_RECEIPT` document: one `RECEIPT` line for the covered quantity at the invoice value with `cost_source` INVOICE, and one `RECEIPT` line for the remaining quantity at the order price with `cost_source` COMPUTED. A fully covered line is one INVOICE line, an uncovered line one COMPUTED line. Both lines carry the same `source_line_ref`, and their quantities sum to the received quantity. For FIFO the two lines create two layers, for moving average the two values add to the cost key.

### 5.3 FIFO

- **Receipt**: creates a layer with the movement's quantity and value.
- **Issue**: consumes layers of the cost key in `layer_seq` order. The issue's value is the sum of the consumed values (quantity-proportional per layer, remainder rule on the last unit of a layer). Each consumption is a row in `stock_layer_consumption`.
- Backdated receipts create their layer **at the end of the layer sequence**, since costing follows processing order (decided), never between existing layers.
- **Adjustment in without value (C3):** creates a layer at the unit cost of the **last layer** of the cost key (the layer with the highest `layer_seq`, also an exhausted one, `cost_source` COMPUTED). If the cost key has no layer yet and no value is supplied, the adjustment is **rejected with `INVENTORY.VALUE_REQUIRED`** (17 section 5, FIFO edge b: the caller supplies a value). With a supplied value (`cost_source` SUPPLIED) the layer is created at that value. An **adjustment out** consumes layers like an issue.
- **Transfers (C3):** with cost scope COMPANY the cost key does not change, so a transfer moves no layers and creates none. With cost scope WAREHOUSE, `TRANSFER_OUT` consumes layers of the source cost key like an issue (in `layer_seq` order), and `TRANSFER_IN` creates in the destination cost key one new layer per consumed layer, at the **layer cost** (consumed value and quantity of that layer) and with the `layer_date` of the source layer. The new layers go to the end of the destination's layer sequence. Equal base quantity and value of both lines (4.2) follow from this.
- **`RETURN_OUT` (C3):** consumes the layer of the **referenced original receipt** (`valuation_ref_document_id`, `valuation_ref_line_no`, 4.2) for the returned quantity, at that layer's unit cost, and not the oldest layer. For a **migrated receipt** the referenced layer is its synthetic layer (4.3). A returned quantity that exceeds the remaining quantity of that layer is rejected with `INVENTORY.RECEIPT_CONSUMED`. `RETURN_IN` creates a new layer at the original cost (4.2).

### 5.4 Negative stock (Q2, C2)

Negative stock is defined on the **cost key only** (5.1), never on the stock key. An issue is rejected with `INVENTORY.INSUFFICIENT_STOCK` if it would make the cost key quantity (`cost_qty_after`) negative and `allow_negative_stock` of the `item_warehouse` row of the issuing warehouse is false. The stock key quantity (`stock_qty_after`) is informational for this rule and is not tested for negative. Availability and reservation checks use the stock key (4.5).

If `allow_negative_stock` is true and an issue exceeds the cost key's quantity, the issue is valued at the most recent unit cost of the cost key (`cost_value_after_lc / cost_qty_after` of the last row with positive quantity, or the last receipt cost). The cost key quantity becomes negative, and its running value negative accordingly. A later receipt first covers the negative quantity: the part of the receipt covering the negative quantity is valued at the receipt cost, and the difference to the value already issued for that quantity is posted to `NEG_STOCK_VARIANCE` in the same stock document. The remainder enters stock normally. For FIFO, the negative issue consumes no layer, and the covering part of the receipt creates no layer.

### 5.5 Reversal

`stock-documents:reverse` mirrors every movement 1:1 with opposite sign and the **original value** of each line, and mirrors consumed layers for FIFO issues (the consumption is reversed and the quantity returned to the same layers). Rules: the reversal date is explicit, no shifting (06 Q8). A receipt of a **moving average** item can be reversed only if the resulting cost key quantity is not negative (`INVENTORY.RECEIPT_CONSUMED` otherwise) or negative stock is allowed (5.4). A receipt of a **FIFO** item (B8) can be reversed only if the **remaining quantity of its layer** (`quantity_base` minus the consumption of the layer, 4.3) equals the reversed quantity, i.e. nothing of the layer has been consumed. Otherwise the reversal is rejected with `INVENTORY.RECEIPT_CONSUMED`, also when the total quantity of the cost key would stay positive and also when negative stock is allowed, because consumption cannot be re-pointed to other layers. A document with reserved or tracked quantities follows the same checks as a new movement. The derived journal is reversed by the mirror rule (ADR-048), with `reversal_of_document_id` and the reason code (D1).

**Owning command (D1, ADR-145).** `stock-documents:reverse` reverses only stock documents posted directly through `stock-documents:post`. It rejects derived documents: stock documents of order flows (those with `detail_digest` set: deliveries, receipts, returns, which are reversed through their order management command, 11), `STOCK_COST_ADJUSTMENT` documents derived from purchase invoices (reversed with the invoice, 11, 7.4), `STOCK_COST_ADJUSTMENT` documents of landed cost allocations (reversed by `landed-costs:reverse`, 6.8), `STOCK_OPENING` (reversed only by `migration-batches:rollback`, 13) and `STOCK_REVERSAL` documents themselves. The journal derived from a stock document is likewise reversible only through the stock document's owning command: `journal-documents:reverse` rejects every document of a stock system type (section 7). The owning command reverses stock and journal atomically.

## 6. Commands

All commands: synchronous, atomic, idempotent, VALIDATE mode, error areas `INVENTORY` and, for the derived journal, the existing areas (`PERIOD`, `MASTER`, `ACCOUNT_RULE`, `BALANCE`, 06). A rejection of the derived journal rejects the whole stock command and carries the finance error codes with paths pointing at the stock request.

### 6.1 Catalogue

| Command | Purpose | Permission |
|---|---|---|
| `stock-documents:post` | Post a receipt, issue, transfer, adjustment or status change (types RECEIPT, ISSUE, TRANSFER, ADJUSTMENT, STATUS_CHANGE, as 6.2). Cost adjustments are not posted directly: `STOCK_COST_ADJUSTMENT` documents arise only from `landed-costs:allocate` (6.6) and supplier invoice matching (11, 7.2) | `stock.post` |
| `stock-documents:reverse` | Reverse a directly posted stock document and its journal. Requires `reason_code`. Rejects derived documents (5.5, D1) | `stock.reverse` |
| `reservations:create`, `:adjust`, `:release` | Maintain reservations | `stock.reserve` |
| `landed-costs:allocate` | Allocate additional costs to receipts | `landed_cost.allocate` |
| `landed-costs:reverse` | Reverse a landed cost allocation with its stock cost adjustment and journal, atomically. Requires `reason_code` (6.8, D1 follow-up) | `landed_cost.allocate` |
| Master and config CRUD | Items, groups, warehouses, tracking, determination | Pattern of 07, section 11 |

Documents of order flows (deliveries, receipts against orders) use `stock-documents:post` internally through order management commands (11). External callers outside order management may post the types above directly: that is the explicit journal path alongside the rules path (P12).

### 6.2 `stock-documents:post`

Request payload:

| Field | Req | Notes |
|---|---|---|
| document_type | Req | RECEIPT, ISSUE, TRANSFER, ADJUSTMENT, STATUS_CHANGE |
| document_date, posting_date | Req | |
| reason_code | Req for ADJUSTMENT | |
| header_text | Opt | |
| source_document_ref | Opt | |
| lines[] | Req | |

`lines[]` item:

| Field | Req | Notes |
|---|---|---|
| line_ref | Req | Caller's reference, echoed back, `line_no` assigned in order (06, Q7) |
| item_id | Req | |
| warehouse_id, location_id | Req, Opt | |
| quantity, unit_code | Req | Positive. The direction follows the document type (RECEIPT adds, ISSUE removes) |
| tracking | Req if item tracked | `{tracking_no, expiry_date, production_date, supplier_ref}`. On receipt of a new lot or serial, the tracking row is created. Otherwise it must exist |
| stock_status | Opt | Default AVAILABLE. For STATUS_CHANGE: `from_status`, `to_status` |
| unit_price, currency | Req for priced RECEIPT, Opt otherwise | One `currency` per document: lines with different currencies are rejected at P1 with `INVENTORY.CURRENCY_MIXED` (4.1). Receipt value = quantity × unit price, converted at the posting date rate (`cost_source` COMPUTED). A RECEIPT without a price is a quantity-only receipt and requires `reason_code`. On an ADJUSTMENT in, a given price is a supplied value (`cost_source` SUPPLIED, 5.6). An ADJUSTMENT in without a price is valued by 5.2 or 5.3, and for a FIFO cost key without a layer it is rejected with `INVENTORY.VALUE_REQUIRED` |
| to_location_id and location types | | A TRANSFER into a TRANSIT location is the first step of a two-step transfer, out of it the second (3.6). TRANSIT and QUARANTINE locations accept TRANSFER (in and out), STATUS_CHANGE and ADJUSTMENT. RECEIPT and ISSUE on them, and reservations, are rejected (`INVENTORY.LOCATION_NOT_AVAILABLE`), a status other than QUARANTINE or BLOCKED into a QUARANTINE location is rejected (`INVENTORY.LOCATION_STATUS_MISMATCH`) |
| to_warehouse_id, to_location_id | Req for TRANSFER | |
| adjustment | Req for ADJUSTMENT | Either `{mode: "DELTA", direction: "IN"\|"OUT", quantity}`, or the count-based form `{mode: "TO_COUNTED", counted_quantity, expected_book_quantity, count_ref}`. In the count-based form the core reads the stock key balance under the item lock, rejects with `INVENTORY.BOOK_QUANTITY_CHANGED` if it differs from `expected_book_quantity` (parameters: the current value), and otherwise posts the difference as `ADJUST_IN` or `ADJUST_OUT`. Zero difference creates no movement |
| expected_cost | Opt | Not accepted: costs are computed by the core, with the closed list of exceptions in 5.6 (Q5) |

Pipeline (06 phases):

| Phase | Checks |
|---|---|
| P0 | Permission `stock.post` |
| P1 | Schema, quantity scales, idempotency, one transaction currency per document (`INVENTORY.CURRENCY_MIXED`) |
| P2 | Items active and stocked, `item_company` active, warehouses and locations, tracking rows, unit conversions, currencies |
| P3 | The finance calendar: window, period, year, status (as for postings) |
| P4 | Tracking rules (3.8.1), expiry, recall, stock status of the source stock, `allow_negative_stock` (an issue that would make the cost key quantity negative with `allow_negative_stock` false is rejected with `INVENTORY.INSUFFICIENT_STOCK`, adopted in 06, 5.4). Balance-dependent checks run on unlocked reads here and are **repeated under the locks of P7** |
| P5 | Derivation per line in processing order: base quantity, running balances, value by 5.2 or 5.3, layers and consumption, negative stock handling, derived journal lines through account determination and the event templates of section 7. In VALIDATE mode this is a non-binding preview. In EXECUTE the result is **recomputed under the locks of P7** and replaces the earlier value (06, 3: one phase model) |
| P6 | Stock consistency: quantities, reservation protection, serial rules. The derived journal passes the finance P3 to P6 checks as if posted directly (balance in four currencies, account rules, dimension defaults). Re-run on the recomputed result of P7 |
| P7 | **All locks of the lock order of 6.4 are taken first** (ADR-147, 04, 6.3 as amended), then the balance-dependent checks of P4, the derivation of P5 and the checks of P6 are recomputed under the locks. Then allocate numbers, insert stock document, movements, layers and consumption, the derived journal, set `detail_digest`, update the chain heads (`chain_head` rows of types LEDGER and STOCK), write `command_log` and the outbox events (15, 5.1: collected during the command, positions allocated last, ADR-159) |

Result: the stock document with all movements including derived values, the layers consumed, and the derived journal document in the shape of 06, 5.2. Warnings: negative stock issued, expired lot issued (`INVENTORY.LOT_EXPIRED`).

### 6.3 Reading balances

**Now (C1).** The stock key balance is the `stock_qty_after` of the stock key's latest movement, the cost key balance the `cost_qty_after` and `cost_value_after_lc` of the cost key's latest movement. Both are one-row index lookups. On-hand across several keys (all locations of a warehouse) is a sum of the latest row per key (a grouped lookup). All of this happens in the item's lock context for commands, and in a consistent snapshot for reads (09).

**As of a past date (C1).** The running columns follow **processing order**, and a backdated movement carries a later processing position than movements with a later posting date, so the latest row up to a date is not the balance of that date. A read as of date D therefore **sums** `base_quantity` and `value_lc` of all movements of the key with `posting_date` ≤ D. Movements with the same posting date are taken in processing order (`chain_seq_pos`), which matters only for a running view. A ledger view between two dates (`stock-ledgers:query`, 8) shows the cumulative sums in the order (`posting_date`, `chain_seq_pos`). Those sums can differ from the stored running columns where backdated movements exist. The running columns are used **only for the present**. The reconciliation of stock value to the GL for a date is the sum by posting date against the GL balance by posting date (8).

### 6.4 Concurrency and lock order

The `item_company` row of each item touched by a command is locked in sorted order of (company, item). This serialises all stock activity of one item in a company, which prevents over-issue and keeps the running balances correct. It is coarse (all warehouses of the item) and acceptable for SME volumes.

**One authoritative lock order (D3, ADR-147, defined in 04, 6.3):** (1) order rows (order management commands only, 11, 9), (2) stock `item_company` rows sorted by (company, item), (3) number range counters (D3 follow-up: counters lock **before** the chain heads, order as in 04, 6.3), (4) `chain_head` rows sorted by the tuple (**chain type, company, scope_key**), with the chain types in the fixed order LEDGER, CLEARING, PERIOD_EVENT, VAT_RETURN, STOCK, FULFILMENT (the VAT_RETURN head is one per company with empty scope key, K1). The company is part of the sort key, so a command that locks the heads of two companies (intercompany) locks them in the same order everywhere, (5) `fiscal_year` and `fiscal_period` rows (the class added in 04, 6.3: a stock command takes the row of its posting date in **share** mode after its head locks and re-checks the status), (6) open items sorted by id (for example the GR/IR open items that a stock-derived clearing touches), (7) `outbox_head` last (15, 8). The counters (3) are locked before any head (4), counters sorted as in 04, 6.3. A clearing that can produce an exchange difference (for example a derived clearing in a foreign currency) pre-locks the LEDGER head and the counter of its difference document at their positions, so that the difference document needs no lock out of order. A stock command therefore takes `item_company` rows, then counters, then LEDGER and STOCK heads, then the period row in share mode. `business_partner.retention_until` is not updated by postings (it is updated by a batch job, 12), so it has no position and is not a hot row. All locks are taken at the start of P7, before the recomputation of P5 (6.2). Deadlocks follow the retry rules of 06, section 3.

### 6.5 Reservations

`reservations:create` locks the item, computes available (on hand AVAILABLE status minus active reservations), and appends a CREATE event. `adjust` appends INCREASE or DECREASE events. `release` appends RELEASE. The stock issue that fulfils a reservation appends CONSUME in the same transaction. Locations of type TRANSIT and QUARANTINE are rejected (3.6). Reservation events emit `reservation.*` outbox events (15, 6).

### 6.6 `landed-costs:allocate`

Allocates an amount from a finance line (freight or duty invoice line on `LANDED_COST_CLEARING`) to receipt lines.

| Field | Req | Notes |
|---|---|---|
| cost_source | Req | `{document_id, line_no}` of the finance line. Its remaining unallocated amount must cover the allocation |
| allocation_method | Req | VALUE, QUANTITY, WEIGHT, VOLUME |
| targets[] | Req | Receipt lines `{stock_document_id, line_no}`. Weight and volume methods need item weight or volume (`INVENTORY.ALLOCATION_BASIS_MISSING`) |
| posting_date | Req | |

Allocation per target is proportional to the method's basis, with the remainder on the last target. For each target line, with receipt quantity R and quantity still on hand from that receipt O (FIFO: remaining quantity of its layer. Moving average: the lesser of R and the cost key's current quantity): `on_hand_part = allocated × O / R`, `consumed_part = allocated − on_hand_part`. The stock document (type STOCK_COST_ADJUSTMENT) gets one `COST_ADJUST` line per target for the on-hand part (FIFO: increases the layer value, moving average: increases the cost key value). The derived journal: debit `INVENTORY` for the on-hand part, debit `COGS` for the consumed part, credit `LANDED_COST_CLEARING`. Q3 discusses the treatment of the consumed part.

### 6.7 Count-based adjustments

Stocktake sessions are app-owned (4.6). An app that counted stock sends one `stock-documents:post` of type ADJUSTMENT with count-based lines (6.2). Each line states the counted quantity and the book quantity the app saw. A line whose book quantity changed since counting is rejected with the current value, and the app recounts or resubmits with the new expected value. Adjustments are valued at average or FIFO cost like any adjustment (5.2, 5.3) and get a `reason_code`.

### 6.8 `landed-costs:reverse` (D1 follow-up, ADR-145)

The owning command of a landed cost allocation, its `STOCK_COST_ADJUSTMENT` document and the derived journal. `stock-documents:reverse` and `journal-documents:reverse` reject them (5.5, 7).

| Field | Req | Notes |
|---|---|---|
| allocation_id | Req | The `landed_cost_allocation` to reverse. An allocation that is itself a reversal or is already reversed is rejected |
| reason_code | Req | Closed list AUTO_REVERSAL, RERUN, REOPEN, CORRECTION, ERROR, RETURN (06, 5.3). External callers use CORRECTION or ERROR. Missing: `REVERSAL.REASON_REQUIRED` |
| posting_date | Req | The reversal date is explicit, no shifting (06 Q8). Must be postable in finance |

Permission: `landed_cost.allocate` (the permission of the original command). Pipeline as `stock-documents:post` (6.2): P0 permission, P1 schema and idempotency, P2 allocation exists, P3 finance calendar for the posting date, P4 the reversibility test below on unlocked reads, P5 preview of the mirror derivation, P6 consistency, P7 the locks in the order of 6.4 (item rows of all targets, counters, LEDGER and STOCK heads), then the reversibility test and the derivation are recomputed under the locks, insert and write.

Effect, in one transaction (all or nothing):
- A mirror stock document of type **`STOCK_REVERSAL`** (not `STOCK_COST_ADJUSTMENT`, 4.1) with one `COST_ADJUST` mirror line per original line at the **original value** with opposite sign (as 5.5, FIFO layer values and moving-average cost key values return to their level before the allocation). It sets `reversal_of_stock_document_id` and the reason code (4.1). The original gets `status` = `reversed`.
- The derived journal is reversed by the mirror rule (ADR-048): credit `INVENTORY` for the on-hand part, credit `COGS` for the consumed part, debit `LANDED_COST_CLEARING`. The mirror journal has the existing mirror type `STOCK_REVERSAL` (14, section 11: the four mirror types stay, "keeps the original type" applies only to types without a mirror type), with `reversal_of_document_id` and the reason code.
- A **mirror allocation** record is inserted (`reversal_of_allocation_id` = the original, `reason_code`, status MIRROR, negated amounts, 4.7) and the original's S column `status` becomes REVERSED. No other column of the original changes. So the **remaining unallocated amount** of the cost source line (6.6) is restored and can be allocated again.
- Outbox events in the same transaction: `ledger.document_reversed`, `stock.document_reversed`, `stock.balance_changed` per stock key moved (15, 6).

**Reversibility test (B7, 17 section 4; 14, section 11).** `landed-costs:reverse` is allowed **only if there is no later movement on the cost key since the allocation**, for every target line: no stock movement of any type on the line's cost key (5.1) whose processing position (`chain_seq_pos`) is after the allocation's stock document, other than movements of documents that are themselves reversals of the allocation. This is the strict test: a later receipt, issue, transfer or adjustment on the cost key blocks the reversal, because FIFO layer values and the moving average then no longer allow the original value to be taken back. Otherwise the command is rejected with `INVENTORY.ALLOCATION_NOT_REVERSIBLE` and nothing is posted. The correction is a new allocation (`landed-costs:allocate`, 6.6), not a reversal. A mirror allocation is likewise not reversible.

**Purchase invoices.** `purchase-invoices:reverse` (11, 7.4) rejects with `INVENTORY.ALLOCATION_EXISTS` while a charge line of that invoice has an allocation that is not reversed. The allocation is reversed first with `landed-costs:reverse`, then the invoice.

Error codes (area `INVENTORY`, adopted in 14, section 11, entered in 06): `INVENTORY.ALLOCATION_NOT_FOUND` (no such allocation), `INVENTORY.ALLOCATION_ALREADY_REVERSED`, `INVENTORY.ALLOCATION_NOT_REVERSIBLE` (a later movement exists on the cost key of a target line, or the allocation is itself a mirror). `INVENTORY.ALLOCATION_EXISTS` (adopted, entered in 06): a purchase invoice with charge lines that have unreversed allocations cannot be reversed. Derived stock and finance errors reject the whole command with their own codes (6, introduction).

## 7. Journal templates (account determination keys: item group, warehouse, item)

The journal structure per event is fixed. Amounts are the movement values.

| Event | Debit | Credit | Notes |
|---|---|---|---|
| Receipt (priced) | `INVENTORY` | `GR_IR` | GR_IR is a GL open-item account without partner (ADR-040). Supplier invoice matching clears it (order management) |
| Receipt without order, price given | `INVENTORY` | `GR_IR` | Same. A supplier invoice later matches it through the **receipt reference** on its invoice line (11, 7.2, D3), because there is no order line to match against. A residual that never gets an invoice is cleared by `gr-ir:write-off` (11, 7.5) |
| Receipt with zero value | none | none | Quantity-only. `journal_document_id` null |
| GR/IR write-off (11, 7.5) | `GR_IR` for a credit residual, else `GR_IR_WRITE_OFF` | `GR_IR_WRITE_OFF` for a credit residual, else `GR_IR` | Not a stock event: posted by `gr-ir:write-off` (document type `GR_IR_WRITE_OFF`, `origin` ORDER), no stock document. The counter account is the role `GR_IR_WRITE_OFF` (3.9), separate from `PRICE_VARIANCE` |
| Issue for sale | `COGS` | `INVENTORY` | Revenue is posted by the sales invoice (order management) |
| Issue, other | Per reason: `ADJUSTMENT_LOSS` or `COGS` | `INVENTORY` | Reason code selects the role |
| Adjustment in | `INVENTORY` | `ADJUSTMENT_GAIN` | |
| Adjustment out | `ADJUSTMENT_LOSS` | `INVENTORY` | |
| Transfer, same inventory account | none | none | Only if value effect nil |
| Transfer, different inventory accounts | `INVENTORY` of the destination | `INVENTORY` of the source | Both found by warehouse key. If `STOCK_TRANSFER_CLEARING` is configured it is used in between (optional) |
| Status change | none | none | |
| Cost adjustment (landed cost) | `INVENTORY` (on hand), `COGS` (consumed) | `LANDED_COST_CLEARING` | |
| Negative stock settlement | `INVENTORY` | `NEG_STOCK_VARIANCE` (or reversed) | Part of the receipt document |
| Reversal | The mirror of the original journal | | 1:1 (ADR-048) |

Dimensions (Q14 decided): each of cost center, profit center and business unit is resolved independently for a stock document line: the warehouse's default if set, else the item group's default. The resolved value is copied to every derived journal line whose account rule for that dimension type is not FORBIDDEN. If the rule is MANDATORY and no default resolves, the posting is rejected with `ACCOUNT_RULE.DIMENSION_REQUIRED`. Defaults are validated like caller-supplied values (validity at the posting date, company restriction). For a transfer, the destination's defaults apply to the debit line and the source's to the credit line. Project and free dimensions are not defaulted in R1. The derived journal is a document of a system type (`STOCK_RECEIPT`, `STOCK_ISSUE`, `STOCK_ADJUSTMENT`, `STOCK_TRANSFER`, `STOCK_STATUS_CHANGE`, `STOCK_COST_ADJUSTMENT`, `STOCK_RETURN`, `STOCK_OPENING`, `STOCK_REVERSAL`, the list of the stock document types of 4.1 and of 02, section 3: a journal exists only where the document has value, so `STOCK_STATUS_CHANGE` documents have none today), `is_system_generated`, with `source_system` = INVENTORY and `source_document_ref` the stock document number. The core sets **`journal_document.origin` = STOCK** (02, X5: set by the core, never by the caller) on every stock-derived journal, including the journal of a derived `STOCK_COST_ADJUSTMENT` document that uses an invoice journal (external journal mode, 11, 7.2), which keeps `origin` = ORDER. **Transaction currency (ADR-035):** the derived journal has the one transaction currency of the stock document (`currency_tc`, 4.1), which for receipts of order flows is the **order currency** of the order line. A stock document without a transaction value (issues, adjustments valued by the core, transfers) posts its journal in the company local currency as transaction currency (`amount_tc` = `amount_lc`). External callers cannot post these types through `journal-documents:post`, and `journal-documents:reverse` rejects them: they are reversed only through the owning stock command (5.5, D1, ADR-145).

## 8. Reads (09 conventions, permission `read.inventory`)

| Operation | Notes |
|---|---|
| `items:get`, `:list`, and the other master resources | Pattern of 09, section 3 |
| `stock-documents:get`, `:list` | Filters: type, dates, item, warehouse, tracking, `source_document_ref`. Includes `movements`, `journal`, `layers`, `integrity` |
| `stock-ledgers:query` | Movements of an item (and key filters) between dates, with balances. For the present the stored running balances, for a historic view the cumulative sums in the order (`posting_date`, `chain_seq_pos`) (6.3) |
| `stock-balances:query` | On-hand (and on-hand by status, reserved, available) as of a date, group by item, warehouse, location, tracking, status. **As-of dates sum `base_quantity` of movements up to the posting date** (6.3, C1). Only the present date may use the latest running row |
| `stock-valuations:query` | Quantity and value by item and group (sums of `value_lc` by posting date), and the **reconciliation to the GL**: value per `INVENTORY` account versus the GL balance of that account as of the date. The difference is shown. It is zero for the present unless something was posted directly (by construction, A2). For a historic date it is a computed comparison and not guaranteed by construction |
| `lot-traces:query` | Forward and backward trace of a lot or serial: receipts, where it went, what issues it fed, via the movements and `source_document_ref` |
| `availability:query` | Available quantity for item and warehouse now (on hand AVAILABLE minus reserved). Stock in TRANSIT and QUARANTINE locations is excluded |
| `supply-demand:query` | Facts per item, warehouse and time bucket for ATP and MRP: on hand by status (excluding TRANSIT), in transit, reserved, open sales demand, open purchase supply. Snapshot, nothing stored. Defined in 16, section 4 (`read.inventory` and `read.orders`, ADR-140) |
| Chain status | `chains:status` with chain type STOCK (09, 4.9) |

Gets and lists of master data accept `include=extensions` on extendable entity types (`ITEM`, `ITEM_COMPANY`, `ITEM_WAREHOUSE`, `WAREHOUSE`, `WAREHOUSE_LOCATION`, `ITEM_PARTNER`, 15, 4.4).

## 9. Deltas to earlier specs

| Spec | Change |
|---|---|
| 02 | New system document types for derived journals (7), not reversible through `journal-documents:reverse` (D1). `journal_document.origin` = STOCK on stock-derived journals (7, X5). `journal_document.detail_digest` is used by stock (ADR-052). `chain_anchor` is keyed by `chain_type` and `scope_key` (K1), the stock anchor has `chain_type` STOCK and empty `scope_key`. The stock chain head is a `chain_head` row |
| 04 | Lock order of 6.4 (D3, X4): order rows, `item_company` rows, number range counters, `chain_head` rows by (chain type, company, scope_key), `fiscal_year` and `fiscal_period` rows (share mode for period-dependent commands), open items by id, `outbox_head` last: the seven classes of 04, 6.3, which shows the counters before the chain heads. Number series assignments cover the stock document types |
| 06 | Error area `INVENTORY` with `LOCATION_NOT_AVAILABLE` and `LOCATION_STATUS_MISMATCH`, and `MASTER.LOCATION_TYPE_LOCKED` (16, 6.2). New `INVENTORY` codes of `landed-costs:reverse` (6.8). Permissions of 6.1 (`landed-costs:reverse` uses `landed_cost.allocate`). External `post` allow-list unchanged (system types excluded). `stock-documents:reverse` rejects derived documents (5.5), `journal-documents:reverse` rejects landed cost allocation documents and names `landed-costs:reverse` as owning command (`params.owning_command`, 06, 5.3) |
| 07 | `company_group.inventory_cost_scope`. Role table with required open-item mode per role (3.9, D5). Company readiness (11.3) checks the account determination catch-all roles. `tax_class` link from items. `item_partner` and calendars (16) |
| 08, 12 | none from this spec |
| 09 | Reads of section 8 including `supply-demand:query`, permission `read.inventory`, audit events add stock documents. `chains:status` covers STOCK |
| 11 | `cost_source` INVOICE for receipts valued at an earlier invoice value and for invoice-derived `COST_ADJUST`. Reservations and deliveries reject TRANSIT and QUARANTINE locations. Role `GR_IR_WRITE_OFF` for `gr-ir:write-off` (11, 3.3, 7.5) |
| 13 | `STOCK_OPENING` movements carry `cost_source` MIGRATION. In-transit stock is opened into a TRANSIT location (16, 9). A FIFO layer of the opening may be the synthetic layer of a migrated receipt (`receipt_legacy_ref`, 4.3, 13, 6.4) |
| 15 | Every stock command writes outbox events in its transaction (`stock.document_posted`, `stock.document_reversed`, `stock.balance_changed`, `reservation.*`). `landed-costs:reverse` writes `ledger.document_reversed`, `stock.document_reversed` and `stock.balance_changed` (6.8) |
| 16 | `warehouse_location.location_type` (3.6, location movement rules of C5: TRANSIT and QUARANTINE accept `TRANSFER_IN`, status changes and adjustments, reject receipts and issues, 16, 6.2 to be aligned). Availability excludes TRANSIT and QUARANTINE. Reservations reject those locations. `item_warehouse.min_quantity` and `reorder_quantity` stay (14, S7 unchanged) |

Added in v0.4 (14, section 11): 6.8 (not reversible once consumed, `INVENTORY.ALLOCATION_EXISTS` on invoice reversal, adopted allocation codes), Q16 and Q17 closed. 06 delta: the `INVENTORY.ALLOCATION_*` codes are entered, `INVENTORY.ALLOCATION_EXISTS` is adopted (cleanup pass, 14 section 12). 11 delta: `purchase-invoices:reverse` rejects while charge-line allocations are not reversed.

**Added in v0.5 (17, section 4, fix pass 4).** C1: as-of reads sum by posting date, running columns only for the present (6.3, 8, A2). C2: negative stock on the cost key only (5.4, 6.2). C3: FIFO adjustment in without value, transfers and `RETURN_OUT` (5.3). C4: `stock_cost_layer.layer_date` (4.3). C5: location movement rules (3.6, 6.2). B7: `landed-costs:reverse` test (no later movement on the cost key), `landed_cost_allocation` columns `reversal_of_allocation_id`, `reason_code`, `status` (4.7), mirror stock document type `STOCK_REVERSAL` (6.8, consistent with 4.1). B8: FIFO receipt reversal checks the layer remaining quantity (5.5). Journal system types list completed with `STOCK_RETURN`, `STOCK_OPENING`, `STOCK_STATUS_CHANGE` (7). 6.1 no longer lists cost adjustment as a directly posted type. Table numbers of 2.1 run without gaps. One phase model, locks at P7 (6.2, no P4a). Lock tuple (chain type, company, scope_key), counters before heads (6.4). One transaction currency per stock document (`currency_tc`, 4.1) and the transaction currency of stock-derived journals (7). `journal_document.origin` = STOCK (7). `DROPSHIP_CLEARING` falls back to the `GR_IR` rules when it has no dedicated rule (3.9). Receipt reference for direct receipts (7). Deltas to other specs: 02 (`journal_document.origin`), 06 (`INVENTORY.LOCATION_NOT_AVAILABLE` now also for receipt and issue movements on TRANSIT and QUARANTINE), 11 (D3 write-off), 16 (6.2 location rules).

**Added in v0.6 (17, section 5, propagation pass).** FIFO edges: a migrated receipt gets a synthetic cost layer from its legacy `value_lc` (4.3, 5.3, 4.2), an adjustment in without value and without an existing layer is rejected with `INVENTORY.VALUE_REQUIRED` (5.3, 6.2), mixed transaction currencies in one stock document are rejected at P1 with `INVENTORY.CURRENCY_MIXED` (4.1, 6.2). The earlier text that a FIFO `RETURN_OUT` of a migrated receipt has no layer and that an adjustment in without a layer is valued at zero is removed. Role `GR_IR_WRITE_OFF` (3.9, 7). Lock order of 6.4 renumbered to the seven classes of 04, 6.3 (`fiscal_year` and `fiscal_period` in share mode at position 5, open items 6, `outbox_head` 7). 06 delta: `INVENTORY.VALUE_REQUIRED` and `INVENTORY.CURRENCY_MIXED` to be entered. 13 delta: synthetic layer (6.4).

These deltas are applied in v0.2 (re-baseline of 14, sections 8 and 9). Added in v0.3 (14, section 10): `landed-costs:reverse` (6.8), the split of partly invoice-covered receipts (5.6), counters before chain heads (6.4), `chain_anchor` by `scope_key` (4.4, 02 delta), and the stock chain head and chain status stated only through `chain_head` and `chains:status`.

### 9.x Delta of ADR-206 (2026-10-14)

No change to the cost layers or the derivation of `COST_ADJUST` movements. Stock purchase invoices built by order management reject tax codes with partial deduction, a deduction key or a cap (`ORDER.PARTIAL_DEDUCTION_NOT_SUPPORTED`, 11, 7.2), so the non-deductible part never has to enter a cost layer in release 1 (19, section 10). A later version decides whether it does.

## 10. Assumptions

| Ref | Item |
|---|---|
| A1 | All stock arithmetic uses base quantity and local currency. GC and RC amounts exist only on the derived journal lines |
| A2 | The inventory value is the sum of `value_lc` of the stock ledger. For the **present** it equals the GL balance of the `INVENTORY` accounts by construction (atomic coupling, direct postings aside). For a historic date the as-of value is a sum by posting date (6.3) and the comparison with the GL as of that date is a computed check, not a construction guarantee (C1) |
| A3 | Non-stock and service items never touch the stock ledger. Orders for them create finance postings through order management only |
| A4 | The stock ledger has no L2 data except text, so there is no change log for movements |
| A5 | A company that has no determination for a role cannot post stock documents that need it (company readiness check) |

## 11. Open questions

| # | Question |
|---|---|
| Q1 | **Decided:** an S column `exhausted` on `stock_cost_layer`, verified by recomputation (4.3). The consumption rows stay the truth. |
| Q2 | **Decided:** negative stock settlement as in 5.4: issue at last unit cost, variance to `NEG_STOCK_VARIANCE` when a receipt covers the shortage. |
| Q3 | **Decided:** the consumed part of a landed cost goes to cost of goods sold (6.6). |
| Q4 | **Decided:** fixed journal templates plus account determination (section 7). No configurable rule engine in R1. |
| Q5 | **Decided, amended by D6 (ADR-150):** costs are computed by the core. The only exceptions are the closed list of 5.6, each marked with `cost_source` (COMPUTED, SUPPLIED, INVOICE, MIGRATION). A known-value correction is a priced receipt or an adjustment at a supplied value (SUPPLIED). |
| Q6 | **Decided, amended by 17 (B8):** a receipt reversal that would leave negative cost key quantity is rejected (`INVENTORY.RECEIPT_CONSUMED`) unless negative stock is allowed for the item and warehouse. For FIFO the reversal is rejected unless the receipt's layer is unconsumed (remaining quantity check, 5.5). |
| Q7 | **Decided:** stock documents use number series and counters as in 04 (gapless per series and fiscal year). |
| Q8 | **Decided:** no stock transfer across companies. It is an intercompany sale and purchase in order management (a delivery in one company, a receipt in the other). |
| Q9 | **Decided:** `item_tracking` is tenant-level per item. A serial is on hand in at most one company at a time. |
| Q10 | **Decided:** a membership change that would give a company with stock documents a different cost scope is rejected (`CONFIG.COST_SCOPE_CONFLICT`). |
| Q11 | **Decided (differs from the proposal):** a tenant-level `unit_conversion` table for general conversions between units of one dimension, with per-item rows as overrides for item-specific units (3.4). |
| Q12 | **Decided:** quantity scale per item base unit. Conversions that do not fit the scale exactly are rejected. |
| Q13 | **Decided (differs from the proposal):** stocktake sessions are app-owned. The core offers count-based adjustment lines with a compare-and-set on the expected book quantity (4.6, 6.2). No stocktake tables. |
| Q14 | **Decided (differs from the proposal):** default cost center, profit center and business unit on warehouse and item group, resolved per dimension (warehouse first, then item group) and copied to derived journal lines (section 7). |
| Q15 | **Decided (differs from the proposal):** issuing expired lots is allowed with the warning `INVENTORY.LOT_EXPIRED`, recorded as `lot_expired` on the movement. The core no longer prevents shipping expired goods, apps must enforce that if needed. |
| Q16 | **Decided (14, section 11, amended by 17 B7):** `landed-costs:reverse` is rejected with `INVENTORY.ALLOCATION_NOT_REVERSIBLE` if any later movement exists on the cost key of a target line since the allocation (6.8). Correction is a new allocation. `purchase-invoices:reverse` rejects with `INVENTORY.ALLOCATION_EXISTS` until the allocations of its charge lines are reversed (6.8). |
| Q17 | **Decided (14, section 11):** `INVENTORY.ALLOCATION_NOT_FOUND`, `_ALREADY_REVERSED` and `_NOT_REVERSIBLE` are adopted and entered in 06. `INVENTORY.ALLOCATION_EXISTS` is new and is now adopted and entered in 06 (14, section 12). |

## 12. ADRs raised by this spec (now in 01-adr-log, which is authoritative)

**ADR-093: Inventory module on the core, posting only through the finance pipeline.** Stock tables are company-scoped, stock documents with value derive a journal that goes through the normal finance pipeline in the same transaction. Refines P1 and P12.

**ADR-094: Atomic business-event coupling.** A stock command and its derived journal commit together or not at all. Rejections of the journal reject the stock command with the finance error codes.

**ADR-095: Perpetual inventory with account determination.** Journal structure per event is fixed, accounts come from a versioned determination table keyed by item group, warehouse and item, using the matching rules of tax determination. Replaces free posting rules for R1 (Q4).

**ADR-096: Valuation: moving average and FIFO, costing by processing order.** Costs are computed from base quantity and local currency. Backdated movements are not recosted. Issues that empty a cost key take the whole remaining value.

**ADR-097: Cost scope set on the group.** Per company, item and batch, or per warehouse, item and batch, locked once movements exist.

**ADR-098: Stock ledger with running balances and an item lock.** No balance table. Each movement stores the running quantity and value after it for its cost key and stock key, the balance is the latest row, and the `item_company` row serialises stock activity.

**ADR-099: Hash-chained stock ledger.** Stock documents are chain nodes per company, anchored with the other chains, and bound to their journal through `detail_digest`.

**ADR-100: Lot and serial tracking.** Tenant-level `item_tracking` identities, serial quantity one, expiry and recall rules.

**ADR-101: Reservations as events, count-based adjustments, landed costs by allocation.** Reservations are append-only events. Stocktake sessions are app-owned and the core offers count-based adjustment lines with compare-and-set on the expected book quantity. Landed cost allocation splits on-hand and consumed parts, the consumed part to cost of goods sold.

**ADR-102: Inventory dimension defaults and unit conversions.** Dimensions of derived journals come from warehouse then item group defaults, per dimension, skipped where the account forbids them. Unit conversions are a tenant table for same-dimension units with per-item overrides, quantity scale is per item.

**ADR-103: Expired lots issue with a warning.** The movement records `lot_expired`. Enforcement is an app concern.

Amended at the re-baseline (14, sections 8, 9 and 10; `landed-costs:reverse` extends ADR-145, ADR numbers for the follow-up decisions are assigned in 01-adr-log): ADR-096 is amended by **ADR-150** (closed list of cost exceptions, `cost_source`, 5.6). ADR-094 and ADR-098 are subject to **ADR-147** (one lock order, derivation after the locks, 6.4). ADR-099 is amended by **ADR-158** (stock chain head in `chain_head`). ADR-095 is amended by **ADR-149** (open-item mode per role, 3.9). The derived-document reversal rule is **ADR-145** (5.5). Outbox events are **ADR-159**. Location types are **ADR-143**, `item_partner` **ADR-142**, `supply-demand:query` **ADR-140** (raised in 16).
