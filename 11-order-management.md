# Order Management: Table Specs and Contract

Status: DRAFT v0.6 for review, re-baselined with the decisions of 14 (sections 8 to 11), specs 15 and 16 and the rulings of 17, sections 4 and 5 (fix pass 4, deltas at the end of section 10). v0.6 (17, section 5, propagation pass): account role `GR_IR_WRITE_OFF`, document type `GR_IR_WRITE_OFF` and the owning reversal command `gr-ir:reverse-write-off` (3.3, 6.1, 6.8, 7.5, 7.5a, Q26 closed), order lifecycle outbox mapping (4.7), lock order of section 9 renumbered to the seven classes of 04, 6.3, keys of `ic_order_link` and CHARGE columns of `purchase_invoice_line` (6.9). v0.5: one open-quantity definition per side (4.3, D4), THREE_WAY cap received minus returned (D1), ORDERED goods lines invoiceable before delivery in both modes with delivery reference protection (D2), `gr-ir:write-off` (7.5, D3) and the receipt reference on supplier invoice lines, migrated valuation basis and `dropship_confirmation_id` (D5), `sales-returns:reverse` and `purchase-returns:reverse` (5.6, B5), `ORDER.DELIVERY_RETURNED` (B6), order-built documents identified by `journal_document.origin` = ORDER (X5), intercompany pair coverage (6.7), `ORDER.CREDIT_EXCEEDS_INVOICED`, `confirmed_date` rejected on create (F12), column specs of the invoice and link tables (6.9), field group ORDER_ADDRESS. v0.4 (14, section 11): `purchase_receipt_line` gets `stock_document_line_no` and `stock_document_line_count` (Q23), APP_SUPPLIES goods lines always need a delivery reference (Q24), an invoice with credit notes cannot be reversed and a credit note on a reversed invoice is rejected (Q25), `ORDER.CREDIT_NOTE_*` and `ORDER.INVOICE_HAS_CREDIT_NOTES` adopted, the mirror type rule is aligned with 02, the settlement reversal command is `vat-returns:reverse-settlement`, Q22 to Q25 closed. v0.3 added `sales-credit-notes:reverse` and `purchase-credit-notes:reverse` (D1 follow-up), closes the APP_SUPPLIES price and reference rules (S2), the split of partly invoice-covered receipts (D6), the counter position in the lock order (D3), `scope_key` anchoring (K1), the approved IC permissions (D10) and corrects the lock class of `order_address_snapshot.retention_until`. Builds on 00-design-principles, ADR-001 to ADR-174 and specs 02 to 16. Main references: P1 and P12, ADR-013 (no drafts), ADR-016 (intercompany), ADR-026 to ADR-028 and 08 (VAT), ADR-040 (GL open items, GR/IR), ADR-094 and 10 (atomic stock coupling), 03 (clearing). Amended by: ADR-145 (reversal through the owning command), ADR-147 (lock order), ADR-149 (role table), ADR-150 (cost source), ADR-155 (thin matching, replaces the tolerance parts of ADR-107 and ADR-116), ADR-156 (invoice mode), ADR-157 (auto-reservation), ADR-158 (`chain_head`), ADR-159 (outbox events), ADR-141 (line dates).
Scope: sales orders, deliveries, returns, sales invoices and credit notes; purchase orders, receipts, returns, supplier invoice matching and credit notes; dropship; intercompany orders; order settings; commands and reads.
Out of scope: quotes and drafts, approval workflows, price lists and discount rules (apps), credit limit checks (apps), acceptance of price deviations (apps, S1), reservation and promising logic beyond the optional auto-reservation setting (apps, S3), advance payments and prepayment invoices (cleared later through the clearing domain), customs declarations (ADR-085), production, e-invoice transport. Retention, export, anonymisation and the restriction of partners are specified in 12.
Conventions, lock classes (L1, L2, L3, S), types and sign convention as in 02, section 1. Pattern letters as in 07, section 2. Command envelope, idempotency, error model as in 06, reads as in 09, stock as in 10.

## 1. Decisions taken

| Topic | Decision |
|---|---|
| Model | **Separate sales and purchase tables**, mirrored in structure. Only the address snapshot and the order change log are shared (Q7) |
| Documents in the core | Confirmed orders with a lifecycle. No drafts, quotes or approval states (ADR-013). Apps send an order when it is binding |
| Progress | Delivered, received, invoiced and returned quantities are **computed** from append-only fulfilment, invoice and return lines, under the order's lock. No cached quantities |
| Invoicing | One command `sales-invoices:create` with a per-company, versioned **invoice mode** (S2, ADR-156): CORE_BUILDS (the core selects lines by `invoice_policy` and prices them from the order) or APP_SUPPLIES (the app sends complete lines, each with its order line reference, a delivery reference on goods lines with policy DELIVERED, and its own price and discount). ORDERED goods lines may be invoiced before delivery in both modes (D2, 6.4). New companies start with CORE_BUILDS. The same validation rules (the references required by the mode, 6.4), tax determination and posting in both modes. Supplier invoices are **supplied by the caller and matched** to order and receipt lines |
| Matching | **Thin** (S1, ADR-155): the core enforces valid references, quantity not above ordered plus the line tolerance (TWO_WAY) or not above received minus returned (THREE_WAY, D1), the control total and the GR/IR split. Acceptance of price deviations is an app decision |
| Price differences | A supplier invoice price difference is **always posted** as price-difference lines and recorded on the match record. It is split like a landed cost: the part for goods on hand adjusts inventory value, the part for goods already issued goes to COGS. Items without stock expense at the invoice price |
| Pricing | Callers supply prices and discounts on order lines. No price lists in the core |
| Amendments | An order and its lines are one current row each, changed only by commands, every change logged field by field |
| Tolerance | Over-fulfilment and under-fulfilment tolerance (percent) per order line, L2, default zero. It is the only quantity tolerance of the core (S1) |
| Reservation | Reservation records stay in the core (10, 4.5). Auto-reservation at order creation is an optional company setting, default off, BEST_EFFORT only (S3, ADR-157). Otherwise apps reserve explicitly |
| Line dates | `requested_date` and `confirmed_date` are L2 on sales and purchase lines, changed by `change-line-dates` (G4, ADR-141, 16, 5) |
| Events | Every command writes its outbox events in the same transaction (15, 5.1, ADR-159) |
| Reversal | Derived documents (fulfilment documents including returns, order-built invoices and credit notes, `DROPSHIP_COST`, GR/IR clearings, derived stock documents) are reversed only through the command that owns them. `journal-documents:reverse` and `stock-documents:reverse` reject them (6.8, D1, ADR-145). Mirror documents carry `reversal_of_document_id` and a reason code. Types that have their own mirror type use it (REVERSAL, STOCK_REVERSAL and the others of 02), types without one keep the original (6.8) |
| Dropship | A dropship confirmation acts as a virtual receipt and delivery: cost of goods sold against GR/IR at the purchase value, no stock movement |
| Returns | Return documents linked to the original delivery or receipt lines, returned stock valued at the original cost |
| Intercompany | Linked sales and purchase order pair created by one command, with atomic fulfilment and invoicing across both companies |
| Tax inputs | Origin and destination country on order lines from the warehouse and the ship-to or ship-from country, overridable |
| Advance payments | Not in release 1 |

## 2. Overview

### 2.1 Tables

| # | Table | Pattern | Role |
|---|---|---|---|
| 1 | `company_order_setting_version` | V | Company settings: invoice mode, auto-reservation, defaults |
| 2 | `sales_order`, 3 `sales_order_line` | M with change log | Sales order header and lines |
| 4 | `purchase_order`, 5 `purchase_order_line` | M with change log | Purchase order header and lines |
| 6 | `order_address_snapshot` | E + PII | Ship-to, bill-to and ship-from snapshots of orders (shared) |
| 7 | `order_change_log` | E | Field-level change log of orders and lines (shared) |
| 8 | `order_event` | E | Lifecycle events: PLACED, CHANGED, LINE_ADDED, LINE_CLOSED (short-close), LINE_CANCELLED, CANCELLED, CLOSED (4.7) |
| 9 | `sales_delivery`, 10 `sales_delivery_line` | E + S | Deliveries against sales orders |
| 11 | `purchase_receipt`, 12 `purchase_receipt_line` | E + S | Goods receipts against purchase orders |
| 13 | `dropship_confirmation` | E + S | Combined virtual receipt and delivery |
| 14 | `sales_return`, 15 `sales_return_line` | E + S | Customer returns |
| 16 | `purchase_return`, 17 `purchase_return_line` | E + S | Returns to suppliers |
| 18 | `sales_invoice`, 19 `sales_invoice_line` | E | Link records for order-built sales invoices, credit notes and their reversals (columns in 6.9) |
| 20 | `purchase_invoice`, 21 `purchase_invoice_line` | E | Link and match records for supplier invoices, credit notes and their reversals (columns in 6.9) |
| 22 | `ic_order_link` | E | Pairs a sales and a purchase order of two companies (6.9) |

Fulfilment, return and invoice tables are insert-only. A reversal inserts mirror rows with negative quantities (as ADR-048), so progress sums stay correct without changing rows. S columns are limited to document status fields.

### 2.2 Layering on the core

- Every command that moves stock or posts value calls the inventory and finance commands **in the same transaction** (ADR-094). The order tables never write stock or finance tables directly.
- The finance document of an invoice is a normal `AR_INVOICE`, `AR_CREDIT`, `AP_INVOICE` or `AP_CREDIT` journal document. **Documents built by order management are identified by `journal_document.origin` = ORDER** (X5, 17 section 4): an L1 column set by the core, never by the caller (02, EXTERNAL, ORDER, STOCK, VAT, MIGRATION, SYSTEM). `source_system` (ORDER_MGMT for these documents) is only a free tracing field and decides nothing. A caller of `journal-documents:post` cannot create or reverse a document with origin ORDER. Its number comes from the journal numbering of 04. Callers can still post AR and AP documents without orders through `journal-documents:post`.
- Documents that are not accounting documents (orders, deliveries, receipts, returns, dropship confirmations) get numbers from number series like any document (04), assigned by document type. Non-legal series are allowed (`is_legal` false).

## 3. Configuration

### 3.1 `company_order_setting_version` (V)

PK: (`tenant_id`, `company_id`, `effective_from`). Resolution as `company_setting_version` (07, 5.5).

| Column | Type | Null | Notes |
|---|---|---|---|
| match_mode_default | CODE | N | THREE_WAY (invoice quantity limited by received minus returned, 7.2) or TWO_WAY (limited by ordered plus the line tolerance). Default for new purchase order lines |
| default_over_tolerance_pct, default_under_tolerance_pct | RATE | N | Defaults for the line tolerances of new order lines (initially 0) |
| auto_reserve | BOOL | N | Default **false** (S3, Q18 decided, ADR-157). If true, `sales-orders:create` reserves best effort for STOCK lines of stocked items (6.2). There is no stricter mode. Apps that need promising reserve explicitly with `reservations:create` (10, 4.5) |
| sales_invoice_mode | CODE | N | CORE_BUILDS or APP_SUPPLIES (S2, Q19 decided, ADR-156). **New companies start with CORE_BUILDS** (the default of the first version row). See 6.4. Versioned like every field of this table: a new row with a later `effective_from`. The mode in force at the command's time applies, so a switch affects **new invoices only** and never changes existing invoices, link rows or reversals |
| allow_invoice_before_receipt | BOOL | N | Allows TWO_WAY purchase lines for stock items, so a supplier invoice may arrive before the goods (7.2, Q2 decided). If false, stock items are always THREE_WAY. Non-stock and service items are always allowed to be two-way |

The price tolerance fields (`price_tolerance_pct`, `price_tolerance_amount_lc`), the company `quantity_tolerance_pct` and the table `purchase_tolerance_override` of v0.1 are removed (S1, ADR-155). `match_mode_default` and `allow_invoice_before_receipt` stay in this table (14, section 10, S1). The decision whether a price deviation is acceptable belongs to apps. `default_reservation_mode` is replaced by `auto_reserve`.

### 3.2 Document types and number series

Order management adds document types to the series assignment of 04, section 5.2: `SALES_ORDER`, `SALES_DELIVERY`, `SALES_RETURN`, `PURCHASE_ORDER`, `PURCHASE_RECEIPT`, `PURCHASE_RETURN`, `DROPSHIP_CONFIRMATION`. Invoices use the existing finance types.

### 3.3 Account determination additions

The `account_determination` table (10, 3.9) gains the key column `partner_id` (nullable, null = any) and the roles below. Matching and overlap rules are unchanged.

| Role | Used for |
|---|---|
| `AR_CONTROL` | Receivables control account of sales invoices (account with open-item mode CONTROL). Keys: partner, company default |
| `AP_CONTROL` | Payables control account of purchase invoices. Keys: partner, company default |
| `REVENUE` | Revenue of invoice lines. Keys: item group, item, warehouse, partner |
| `PURCHASE_EXPENSE` | Expense account for invoice lines of non-stock items and services. Keys: item group, item |
| `DROPSHIP_CLEARING` | Optional separate GR/IR style account for dropship cost. **Role default `GR_IR`:** when no dedicated `DROPSHIP_CLEARING` rule exists, the `GR_IR` rules apply (10, 3.9). `INVENTORY.ACCOUNT_DETERMINATION_MISSING` is returned only if no `GR_IR` rule matches either |
| `GR_IR_WRITE_OFF` | Counter account of `gr-ir:write-off` (7.5, 17 section 5). Open-item mode `NONE`. Keys: item group, item, warehouse. **No role default:** the `GR_IR` and `PRICE_VARIANCE` rules do not apply to it, so a price difference and a write-off stay separate in the ledger. `INVENTORY.ACCOUNT_DETERMINATION_MISSING` if no rule matches |

Required open-item mode per role (D5, ADR-149, authoritative table in 07, 6.3; the inventory roles are in 10, 3.9):

| Role | Required open-item mode |
|---|---|
| `GR_IR`, `DROPSHIP_CLEARING` | `GL_OPEN_ITEM` |
| `AR_CONTROL`, `AP_CONTROL` | `CONTROL` |
| `REVENUE`, `PURCHASE_EXPENSE`, `GR_IR_WRITE_OFF` and all other roles | `NONE` |

## 4. Orders

### 4.1 `sales_order` (M)

PK: (`tenant_id`, `company_id`, `sales_order_id`). Unique (`tenant_id`, `company_id`, `number_series_id`, `fiscal_year`, `sequence_no`).

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| order_number, sequence_no, fiscal_year, number_series_id | | N | L1 | Numbered at placement (04) |
| order_date | DATE | N | L1 | |
| customer_partner_id | ID | N | L1 | Must have an ACTIVE customer relation in the company (07, 9.3) |
| order_currency | CODE | N | L1 | Invoices are in this currency |
| payment_terms_id | ID | Y | L2 | Default from the partner relation |
| customer_reference | TEXT(100) | Y | L2 | The customer's own order number |
| requested_delivery_date | DATE | Y | L2 | Header default for the line `requested_date` (4.2). Informational on the header |
| incoterms_code | CODE | Y | L2 | Informational (customs handled in inventory and order domains, ADR-085) |
| buyer_reference, contract_reference | TEXT(100) | Y | L2 | Buyer routing reference and contract reference for e-invoices (EN 16931, 12, 9.2) |
| header_text | TEXT(200) | Y | L2 | |
| origin | CODE | N | L1 | LIVE or MIGRATED (13, 6.5). Migrated orders carry the open quantity and the legacy references |
| migration_batch_id | ID | Y | L1 | Set for migrated orders, deliveries, receipts and dropship confirmations (13) |
| legacy_order_ref | TEXT(100) | Y | L1 | The order number of the legacy system, set for migrated orders (13, 5, 6.5). Also on purchase orders |
| lifecycle_status | CODE | N | S | OPEN, CLOSED, CANCELLED. Written by commands, the truth is `order_event` |
| source_system, source_document_ref | CODE, TEXT(100) | N, Y | L1 | P13 |
| placed_at, placed_by, correlation_id, idempotency_key | | N | L1 | |
| ic_transaction_id | ID | Y | L1 | Set for intercompany orders (6.7) |
| row_version | INT | N | | Optimistic concurrency |

Derived (never stored): `fulfilment_state` (NONE, PARTIAL, COMPLETE) and `invoice_state` (NONE, PARTIAL, COMPLETE) from line sums (4.3).

### 4.2 `sales_order_line` (M)

PK: (`tenant_id`, `company_id`, `sales_order_id`, `line_no`).

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| item_id | ID | N | L1 | Stocked, non-stock or service item (10, 3.3) |
| description | TEXT(200) | Y | L2 | Overrides the item name on documents |
| quantity | DECIMAL | N | L2 | In `unit_code`. Lower limit: the larger of delivered and invoiced quantity |
| unit_code, base_quantity | | N | L1, L2 | `base_quantity` follows `quantity` |
| unit_price | AMT | N | L2 | In order currency per `price_unit_quantity`. Locked once any invoice line exists (4.4) |
| price_unit_quantity | DECIMAL | N | L1 | Price per this many units (1, 100, 1000) |
| discount_pct | RATE | N | L2 | Line discount. Locked with the price |
| net_amount | AMT | N | L2 | `quantity` × `unit_price` / `price_unit_quantity` × (1 − discount) rounded at the currency scale. Computed by the core |
| tax_class_id | ID | Y | L1 | Default the item's class |
| warehouse_id | ID | Y | L2 | Delivering warehouse. Required for stocked items. Changeable until delivered |
| origin_country, destination_country | CODE | N | L2 | Tax geography (4.5) |
| invoice_policy | CODE | N | L1 | DELIVERED (invoice what was delivered) or ORDERED (invoice the ordered quantity, for services) |
| over_tolerance_pct, under_tolerance_pct | RATE | N | L2 | Line tolerances in percent, default from the settings |
| requested_date | DATE | Y | L2 | The customer's requested delivery date (G4, 16, 5.1). Defaults from the header `requested_delivery_date`. Changed by `sales-orders:change-line-dates` (4.4) |
| confirmed_date | DATE | Y | L2 | The date promised to the customer. **Rejected on create and add-line** (`ORDER.CONFIRMED_DATE_ON_CREATE`, F12): it is set only by `sales-orders:change-line-dates` (4.4) with `order.promise`, for example by an ATP app. The only other writer is the migration (13, 5, controlled exception for migrated lines). Stored as a fact, never compared with `requested_date` by a rule |
| legacy_line_ref | TEXT(100) | Y | L1 | The legacy line reference of a migrated order line (13, Q3). Also on purchase lines |
| fulfilment_type | CODE | N | L1 | STOCK, SERVICE, DROPSHIP |
| dropship_purchase_order_id, dropship_purchase_line_no | ID, INT | Y | L2 | Linked purchase line for DROPSHIP lines (6.6). Set at placement or later until the first confirmation |
| line_status | CODE | N | S | OPEN, CLOSED, CANCELLED (short-close or cancel, with event) |
| line_text | TEXT(200) | Y | L2 | |

`item_id`, `fulfilment_type`, `invoice_policy`, `price_unit_quantity` cannot change after placement. A different item means cancelling the line and adding one (`add-line` is a change command).

### 4.3 Progress (computed)

For a sales order line, with the order row locked:

| Measure | Definition |
|---|---|
| ordered | `base_quantity` |
| delivered | Sum of `sales_delivery_line.base_quantity` and `dropship_confirmation.base_quantity` for the line, mirror rows included (negative) |
| returned | Sum of `sales_return_line.base_quantity` for the line, mirror rows included (negative) |
| invoiced | Sum of `sales_invoice_line` quantities of kind INVOICE for the line, minus mirror rows of reversals |
| credited | Sum of quantity credit notes for the line |
| **open quantity** (sales) | `max(0, ordered − delivered)` for a line that is OPEN, 0 for a CLOSED or CANCELLED line. **Over tolerance does not count as demand.** This is the one definition of open quantity to deliver. It is used by `open-orders:query` (8), by `supply-demand:query` and ATP (16, 4), by the exposure read `credit-exposures:query` (8.1) and by availability planning (09). |
| delivery limit | `ordered` + over tolerance − `delivered` (never below zero). A **limit** for posting a delivery or a dropship confirmation (`ORDER.OVER_DELIVERY`), not a demand figure |
| open to invoice | DELIVERED policy: `delivered` − `invoiced`. ORDERED policy: `ordered` + over tolerance − `invoiced` (also for goods lines, which may be invoiced before delivery, D2) |

The purchase side has the mirror measures (4.6): `open to receive` = `max(0, ordered − received)` for an OPEN line, and a `receipt limit` = `ordered` + over tolerance − `received`. `received` includes mirror rows of reversals and dropship confirmations.

Returns do not reopen delivery: a returned quantity is credited, and a replacement is a new order line. The order `fulfilment_state` is COMPLETE when every line is delivered to at least `ordered` − under tolerance or closed. `invoice_state` likewise. A line closes when it is fulfilled and invoiced, or by short-close.

### 4.4 Amendments

An order is changed only by commands (`sales-orders:change`, `:add-line`, `:close-line`, `:change-line-dates`, `:cancel`). The command locks the order row, checks `expected_version` and the rules below, applies the change and writes `order_change_log` rows and an `order_event`.

| Change | Rule |
|---|---|
| Quantity | Not below the larger of delivered and invoiced. A reservation follows the change |
| Unit price, discount | Only while no invoice line exists for the line (`ORDER.PRICE_LOCKED_INVOICED`). Net amount is recomputed |
| Line dates (`requested_date`, `confirmed_date`) | `sales-orders:change-line-dates`, with `expected_version` (compare-and-set), until the line is closed, cancelled or fully fulfilled. `requested_date` needs `sales.order`, `confirmed_date` needs `order.promise`. The change is logged in `order_change_log` and writes the outbox event `order.date_changed` (15, 6). `ORDER.DATE_INVALID` for a date outside 1900 to 2200 |
| Text, terms, reference | Any time before the order is CLOSED or CANCELLED |
| Warehouse | Until the first delivery of the line |
| Tax geography | Until the first invoice of the line |
| Cancel line or order | Only the undelivered, uninvoiced part. A line with deliveries or invoices can only be short-closed (`close-line`), which sets `line_status` CLOSED and releases the reservation |

Closed periods do not restrict orders, orders are not ledger documents. Fulfilment and invoicing follow the finance calendar through the derived stock and journal documents.

### 4.5 Tax geography

Per order line, `origin_country` and `destination_country` are set at placement (07 Q1, 08):
- **Sales, stock lines:** origin = the country of the delivering `warehouse`, destination = the ship-to country of the order's ship-to snapshot.
- **Sales, service lines:** origin = the company country, destination = the ship-to country (or the customer's country if there is no ship-to).
- **Dropship lines:** origin = the supplier's ship-from country on the linked purchase line, destination = the ship-to country.
- **Purchases:** origin = the ship-from country on the purchase order (supplier), destination = the receiving warehouse's country.
A caller may override per line. The values are the inputs of the tax determination at invoicing and are stored on the invoice's tax lines (02).

### 4.6 Purchase orders

`purchase_order` and `purchase_order_line` mirror 4.1 and 4.2 with these differences:

| Aspect | Purchase side |
|---|---|
| Counterparty | `supplier_partner_id`, an ACTIVE supplier relation in the company |
| Header | `supplier_reference`, `expected_receipt_date` (L2, the default for the line `requested_date`), `incoterms_code`, `legacy_order_ref` (migrated orders) |
| Line | `warehouse_id` is the **receiving** warehouse (stocked items). `match_mode` (THREE_WAY, TWO_WAY, L2 until the first invoice line), `over_tolerance_pct`, `under_tolerance_pct` (percent, L2, the line tolerance of S1). `requested_date` (L2, defaults from the header `expected_receipt_date`), `confirmed_date` (L2, the date confirmed by the supplier, rejected on `purchase-orders:create` and `:add-line` with `ORDER.CONFIRMED_DATE_ON_CREATE` and set only by `change-line-dates`, F12, except by the migration), `legacy_line_ref`. Dates are changed by `purchase-orders:change-line-dates` (`purchase.order` for `requested_date`, `order.promise` for `confirmed_date`, 4.4 rules). No `invoice_policy` and no reservation |
| Line origin | `origin_country` = supplier ship-from country (header default `ship_from_country`), `destination_country` = receiving warehouse country (4.5) |
| Dropship | A purchase line linked from a sales line (`dropship_sales_order_id`, `dropship_sales_line_no`). Its `warehouse_id` is null and its destination is the customer's country |
| Progress | `received`, `returned`, `invoiced`, `credited` as in 4.3 with receipts, purchase returns and purchase invoice lines. **Open quantity (purchase), "open to receive"** = `max(0, ordered − received)` for an OPEN line, over tolerance not counted, and the `receipt limit` = `ordered` + over tolerance − `received` (limit for posting a receipt, dropship confirmations on the linked purchase line included). These are the purchase side of the one open-quantity definition of 4.3, used by `open-orders:query`, `supply-demand:query` (16, 4) and receipt checks. **Open to invoice (D1):** THREE_WAY: (`received` − `returned`) − (`invoiced` − `credited quantity`), never below zero. TWO_WAY: `ordered` + over tolerance − (`invoiced` − `credited quantity`) |

### 4.7 Shared tables

#### `order_address_snapshot` (E, personal data)

PK: (`tenant_id`, `company_id`, `order_kind`, `order_id`, `address_role`). `order_kind` SALES or PURCHASE, `address_role` SHIP_TO, BILL_TO, SHIP_FROM. Columns `name`, `address_line_1`, `address_line_2`, `postal_code`, `city`, `country_code` (the country is also on the lines), `contact_reference`, `copied_from_partner` BOOL, `retention_until` (DATE, **S**, outside the hash, D2: set from the retention rule of class ADDRESS_SNAPSHOT at placement and recomputed by the retention batch job without `change_log`, 12, 4.2). Copied from the partner's master data at placement unless the caller supplies a different delivery address (a ship-to address is order data, not partner master data). The snapshot is personal data in the sense of 07 Q3: it is anonymised by the retention job and its search columns cleared. Reads of it are logged as read access to the **field group `ORDER_ADDRESS`** (09, 5.2: `LEGAL_SNAPSHOT` is the field group of the document legal snapshots of invoices and is not used for order addresses, 09 lists the new group). The country code stays for tax evidence.

#### `order_change_log` (E)

PK: (`tenant_id`, `change_id`). Columns as `change_log` (02, section 8): `change_set_id`, `entity_type` (`ORDER` for header and lines, `ORDER_ADDRESS_SNAPSHOT` for the SYSTEM overwrite of `order_address_snapshot` by the retention job, 12, 4.3), `order_kind`, `order_id`, `address_role` (null unless `entity_type` is `ORDER_ADDRESS_SNAPSHOT`: the `address_role` of the snapshot row, which is part of its key), `line_no` (null for header and snapshots), `field_name`, `value_type`, `old_value`, `new_value`, `reason_code`, `changed_by`, `changed_at`, `correlation_id`, `idempotency_key`. Insert only. The key of an `ORDER_ADDRESS_SNAPSHOT` entry is (`order_kind`, `order_id`, `address_role`). Its `reason_code` is `SYSTEM` (retention overwrite, 12, 4.3); `change_log` (02) uses `SYSTEM` only for `document_legal_snapshot` (entity type LEGAL_SNAPSHOT). Personal-data fields (addresses) are logged as changed without values (07, 10.1).

#### `order_event` (E)

PK: (`tenant_id`, `company_id`, `order_kind`, `order_id`, `event_no`). `event_type` (PLACED, CHANGED, LINE_ADDED, LINE_CLOSED, LINE_CANCELLED, CANCELLED, CLOSED), `line_no`, `event_at`, `event_by`, `reason_code`, `correlation_id`. The truth for `lifecycle_status` and `line_status`.

**Outbox mapping (15, 6, order lifecycle mapping).** PLACED writes `order.created`. CHANGED, LINE_ADDED and LINE_CANCELLED write `order.changed` (`status` and `line_no` in the summary say what happened). CLOSED (the order closes after its last line) writes `order.changed` with `status` = CLOSED. LINE_CLOSED writes `order.line_closed`. CANCELLED writes `order.cancelled`. A change of dates writes `order.date_changed` (4.4).

## 5. Fulfilment, returns and dropship

### 5.1 `sales_delivery` and `sales_delivery_line` (E + S)

`sales_delivery`: PK (`tenant_id`, `company_id`, `delivery_id`). Numbering as in 4.1. Columns: `delivery_number`, `delivery_date` (the posting date of the derived stock document), `customer_partner_id`, `warehouse_id` (default for lines), `carrier_reference`, `tracking_reference`, `stock_document_id` (L1, the derived issue document, null if the delivery has only service lines), `status` (S: posted, reversed), `reversal_of_delivery_id` (on mirror rows, with `reason_code`), `origin` (L1: LIVE or MIGRATED, 13, 6.5), `migration_batch_id` (L1, set when migrated), `source_system`, `source_document_ref`, audit columns (`posted_by`, `correlation_id`, `idempotency_key`).

`sales_delivery_line`: PK (`tenant_id`, `company_id`, `delivery_id`, `line_no`). `sales_order_id`, `sales_order_line_no`, `item_id`, `quantity`, `unit_code`, `base_quantity` (signed, negative on reversal), `warehouse_id`, `location_id`, `tracking_id` (lot or serial), `stock_status`, `stock_document_line_no` (the issue movement), `value_lc` (L1: copied from the issue movement for live deliveries. For a **migrated** delivery, which has no stock document, `value_lc` is the cost value of the legacy record and is the **valuation basis** for returns of that delivery line, 5.4, D5, 13, 5).

### 5.2 `purchase_receipt` and `purchase_receipt_line` (E + S)

Mirror of 5.1: `receipt_number`, `receipt_date`, `supplier_partner_id`, `delivery_note_reference` (supplier's), `stock_document_id` (the derived receipt document), `origin` (L1: LIVE or MIGRATED) and `migration_batch_id` (L1), the lines carry `purchase_order_id`, `purchase_order_line_no`, quantity, `unit_price` and `currency` (from the order line at receipt, in effect the receipt value), `tracking` data (lot and serial numbers with expiry, creating `item_tracking` rows, 10, 3.8), `stock_document_line_no` (the **first** movement line of the line in the derived stock document) and `stock_document_line_count` (the number of movement lines: 2 when the line is split into an INVOICE and a COMPUTED line, 7.1, 10, 5.6.1, otherwise 1. The movement lines are consecutive and carry the same `source_line_ref`, Q23), `value_lc` (L1, the sum over the movement lines for live receipts. For a **migrated** receipt the legacy GR/IR value, which is the **valuation basis** for returns of that receipt line, 5.4, D5), and `gr_ir_journal_line` (L1, set only on migrated receipts: the line of the opening document that holds the GR/IR open item, 13, 6.5). The journal of the derived stock document debits inventory and credits GR/IR (10, section 7). The GR/IR credit line is a GL open item that supplier invoice matching clears.

### 5.3 `dropship_confirmation` (E + S)

PK: (`tenant_id`, `company_id`, `dropship_confirmation_id`). Numbering as above. Columns: `confirmation_date`, `sales_order_id`, `sales_order_line_no`, `purchase_order_id`, `purchase_order_line_no`, `quantity`, `unit_code`, `base_quantity` (signed), `supplier_shipment_reference`, `carrier_reference`, `journal_document_id` (L1, derived), `value_lc` (L1: purchase value of the quantity), `origin` (L1: LIVE or MIGRATED) and `migration_batch_id` (L1), `gr_ir_journal_line` (L1, set only on migrated confirmations, the opening line that holds the GR/IR open item, 13, 6.5), `status` (S), `reversal_of_id` (with `reason_code`), audit columns.

The confirmation counts as delivered for the sales line and as received for the purchase line (progress sums include it). No stock movement exists. The derived journal (document type `DROPSHIP_COST`, system) debits `COGS` and credits `DROPSHIP_CLEARING` (default `GR_IR`) at the purchase line value for the quantity, converted at the confirmation date (ADR-019). The credit line is a GL open item matched later by the supplier invoice (7.2).

### 5.4 Returns

`sales_return` and `sales_return_line` (E + S): header like a delivery with `return_number`, `return_date`, `customer_partner_id`, `stock_document_id`, `status`. Lines reference the **original delivery line** (`delivery_id`, `delivery_line_no`) and the sales order line: `base_quantity`, `reason_code`, `disposition` (stock status of the returned goods: AVAILABLE, QUARANTINE, BLOCKED; or SCRAP for no stock re-entry), `tracking_id` (must equal the original's for tracked items). Returned stock re-enters at the **original issue cost**: the derived stock document is of type `STOCK_RETURN` and each movement refers to the original issue movement for valuation (10 delta, Q6). The movements carry `cost_source` COMPUTED (D6, 10, 5.6): the core derives the value from the referenced movement, the caller supplies no value. **Migrated deliveries (D5):** a delivery line with `origin` MIGRATED has no issue movement, so the valuation basis is the `value_lc` of the delivery line (5.1), taken proportionally to the returned quantity, and the derived movement refers to that delivery line as valuation source (10, 4.2). Cost of goods sold is reversed by the derived journal. A return can exceed neither the delivered quantity nor the already returned quantity (`ORDER.RETURN_EXCEEDS_DELIVERED`). Dropship lines: a return is a return to the supplier plus a credit, handled through a purchase return linked to the sales return (no stock). **The link (D5):** `sales_return_line` and `purchase_return_line` carry `dropship_confirmation_id` (ID, L1, null for non-dropship lines): the confirmation both returns refer to. The value of both returns is the `value_lc` of that confirmation (proportional to the returned quantity), so a dropship return needs no stock movement for its valuation. The caller names the confirmation on the sales return line and on the purchase return line. Reversal of one side of a linked pair reverses the pair (5.6).

`purchase_return` and `purchase_return_line`: the mirror. Lines reference the original **receipt line**, goods leave stock at the **original receipt value** (not the current average, for a migrated receipt the `value_lc` of its receipt line, D5) as a `STOCK_RETURN` document with referenced valuation (FIFO: the movement consumes the layer of the referenced receipt, 10, 5.3), debiting GR/IR (or AP side through the credit note) and crediting inventory. The journal debits `GR_IR` for the returned value, which clears against the original receipt credit if not yet invoiced, or becomes a debit open item that the supplier credit note clears if the receipt was already invoiced (7.4).

### 5.5 Fulfilment chain (Q1 decided)

Deliveries, receipts, returns (both sides) and dropship confirmations, including their reversals, are nodes of **one hash chain per company**. Each of these headers has the L1 columns `chain_seq` (gapless per company), `prev_hash`, `row_hash`, `hash_version`, and `line_digest` (SHA-256 over its lines' L1 columns in line order). `row_hash` covers all L1 columns of the header, `line_digest`, and the ids of the derived stock or journal documents. The chain head is the row (`tenant_id`, `company_id`, `chain_type` = FULFILMENT, empty scope key) of the single `chain_head` table (K1, ADR-158, 02, 9). It is locked by every fulfilment command at its place in the lock order of section 9: after the LEDGER, CLEARING, PERIOD_EVENT, VAT_RETURN and STOCK heads. It is anchored with the other chains in `chain_anchor` (`chain_type` = FULFILMENT, `scope_key` empty, 02, 9.5). The fulfilment document's `row_hash` is bound into the derived document: the derived stock document's new L1 column `detail_digest` (10 delta) for deliveries, receipts and returns, and the derived journal document's `detail_digest` for dropship confirmations (and for service-only fulfilments with no stock document, the chain alone is the evidence). Orders and invoice link records are not chained: orders keep the change log and events, invoices are covered by their journal document.

### 5.6 Return reversal (B5, 17 section 4)

`sales-returns:reverse` and `purchase-returns:reverse` are the **owning commands** of a return, its `STOCK_RETURN` stock document and its derived journal (6.8). `stock-documents:reverse` and `journal-documents:reverse` reject them. Permissions as the original commands: `sales.return` and `purchase.return`.

| Field | Req | Notes |
|---|---|---|
| return_id | Req | The `sales_return` or `purchase_return` to reverse |
| reason_code | Req | Closed list AUTO_REVERSAL, RERUN, REOPEN, CORRECTION, ERROR, RETURN (06, 5.3). External callers use CORRECTION or ERROR. Missing: `REVERSAL.REASON_REQUIRED` |
| posting_date | Opt | Default the return date. Explicit, no shifting (06 Q8). Must be postable in finance |

Effect, in **one transaction** (all or nothing, ADR-094):
- **Mirror return records:** a mirror return header with `reversal_of_return_id` and `reason_code`, and mirror lines with negative `base_quantity`, so the `returned` progress of the order lines is restored (4.3). The original `status` (S) becomes `reversed`. The mirror header is a node of the **fulfilment chain** with its own `chain_seq`, `prev_hash`, `row_hash` and `line_digest` (5.5), and its `row_hash` is bound into the mirror stock document through `detail_digest`.
- **Stock and journal:** a mirror stock document of type `STOCK_REVERSAL` for the `STOCK_RETURN` document (10, 5.5, original values, FIFO layers returned to their state) and its derived journal reversed by the mirror rule (ADR-048, mirror type `STOCK_REVERSAL`, `reversal_of_document_id`, the reason code). A return without stock (SCRAP disposition, dropship) has no stock document, then only the fulfilment records and a journal reversal if a journal exists.
- **Dropship pair:** a return carrying `dropship_confirmation_id` (5.4, D5) is reversed **together with its linked return** of the other side in the same transaction. Either command then reverses both and requires both permissions, `sales.return` and `purchase.return`.
- Outbox events: the reversal of `return.posted` (`return.reversed`, 15, 6), `ledger.document_reversed`, `stock.document_reversed` and `stock.balance_changed` where stock moved.

Rejections (area `ORDER`, to be entered in 06): `ORDER.RETURN_NOT_FOUND`, `ORDER.RETURN_ALREADY_REVERSED`, `ORDER.RETURN_CREDITED` (a return credit note of a sales return line, or a supplier credit note of a purchase return, that is not reversed: the credit note is reversed first with its own command, 6.5, 7.3). A purchase return whose debit open item has been cleared otherwise is rejected with `REVERSAL.HAS_CLEARED_LINES` (06). The stock check of 10, 5.5 applies: returned goods that are already consumed again reject the reversal with `INVENTORY.RECEIPT_CONSUMED`. A restricted partner does not block the reversal (6, common rule 1). Locks: the order rows, item rows, counters, `chain_head` rows (including FULFILMENT) and open items in the order of section 9.

## 6. Commands

All commands: synchronous, atomic, idempotent, VALIDATE mode, error areas `ORDER` and `MATCH` (listed with each command) in addition to the areas of 06 and 10. Derived stock and finance errors reject the whole command with their own codes.

**Phase model (X7, one model with 06 and 10).** P1 to P6 run on unlocked reads and are non-binding for EXECUTE. **P7 takes all locks first** in the order of section 9, then recomputes the progress checks and tolerance checks of P4, the derivation of P5 and the consistency checks of P6 under the locks, and then writes. There is no separate lock phase.

**Common rules.** (1) Every command that names a customer or supplier rejects a **restricted** partner for new orders, deliveries, receipts, returns, invoices and credit notes with `MASTER.PARTNER_RESTRICTED` (12, 5.3, P2 of each command). Payments, clearings, reversals and credit notes that settle existing items stay allowed. (2) Every command writes its outbox events in the same transaction (15, 5.1, ADR-159), event types per 15, section 6, with the outbox position allocated last. (3) All `:reverse` commands take a required `reason_code` (6.8).

### 6.1 Catalogue

| Command | Purpose | Permission |
|---|---|---|
| `sales-orders:create`, `:change`, `:add-line`, `:close-line`, `:cancel` | Place and amend a sales order | `sales.order` |
| `sales-orders:change-line-dates` | Change `requested_date` and/or `confirmed_date` of lines (4.4) | `sales.order` for `requested_date`, `order.promise` for `confirmed_date` |
| `sales-deliveries:post`, `:reverse` | Deliver against order lines (stock issue) | `sales.deliver` |
| `sales-returns:post`, `:reverse` | Receive returned goods. `:reverse` is the owning reversal command of the return (5.6, 6.8) | `sales.return` |
| `sales-invoices:create`, `:reverse` | Invoice delivered or ordered quantities (mode CORE_BUILDS) or the lines supplied by the app (mode APP_SUPPLIES), post AR | `sales.invoice` |
| `sales-credit-notes:create`, `:reverse` | Credit against invoice lines by return or amount. `:reverse` is the owning reversal command of the credit note (6.5, 6.8) | `sales.invoice` |
| `purchase-orders:create`, `:change`, `:add-line`, `:close-line`, `:cancel` | Place and amend a purchase order | `purchase.order` |
| `purchase-orders:change-line-dates` | Change `requested_date` and/or `confirmed_date` of lines (4.4) | `purchase.order` for `requested_date`, `order.promise` for `confirmed_date` |
| `purchase-receipts:post`, `:reverse` | Receive against order lines (stock receipt) | `purchase.receive` |
| `purchase-returns:post`, `:reverse` | Return goods to the supplier. `:reverse` is the owning reversal command of the return (5.6, 6.8) | `purchase.return` |
| `purchase-invoices:post`, `:reverse` | Match and post a supplier invoice | `purchase.invoice` |
| `purchase-credit-notes:post`, `:reverse` | Match and post a supplier credit note. `:reverse` is the owning reversal command of the credit note (7.3, 6.8) | `purchase.invoice` |
| `gr-ir:write-off`, `gr-ir:reverse-write-off` | Clear a GR/IR residual to the account of role `GR_IR_WRITE_OFF` (7.5, D3). `:reverse-write-off` is the owning reversal command of the write-off document (7.5, 6.8) | `purchase.invoice` |
| `dropship-confirmations:post`, `:reverse` | Confirm a dropship delivery | `dropship.confirm` |
| `ic-orders:create`, `ic-orders:change` | Place and change an intercompany order pair (6.7) | `ic.order` |
| `ic-deliveries:post`, `:reverse` | Delivery in the seller and receipt in the buyer, one atomic command (6.7) | `ic.deliver` |
| `ic-invoices:create`, `:reverse` | AR invoice in the seller and AP invoice in the buyer, one atomic pair (6.7) | `ic.invoice` |

The permissions `ic.order`, `ic.deliver` and `ic.invoice` are approved (D10, 14 section 10) and are added with `order.promise` to the permission table of 06. The `purchase.match_override` permission of v0.1 is removed (S1). The reversal commands use the permission of the original command (6.8).

### 6.2 `sales-orders:create`

| Field | Req | Notes |
|---|---|---|
| order_date, order_currency, customer_partner_id | Req | |
| payment_terms_id, customer_reference, requested_delivery_date (header default for line dates), incoterms_code, header_text, source_document_ref | Opt | |
| ship_to | Opt | Address snapshot data, else copied from the partner |
| lines[] | Req | At least one |

`lines[]`: `line_ref`, `item_id`, `quantity`, `unit_code`, `unit_price`, `price_unit_quantity` (default 1), `discount_pct` (default 0), `warehouse_id`, `fulfilment_type` (default by item type), `invoice_policy`, `tax_class_id`, `origin_country`, `destination_country` (default 4.5), `over_tolerance_pct`, `under_tolerance_pct` (line tolerances in percent), `requested_date` (default the header date), `dropship_purchase_line` (optional), `line_text`. **`confirmed_date` is not accepted** on create or `:add-line`: a request that carries it is rejected with `ORDER.CONFIRMED_DATE_ON_CREATE` (F12). It is set afterwards by `sales-orders:change-line-dates` with `order.promise` (4.4), so `sales-orders:create` needs only `sales.order`.

Pipeline: P2 customer relation active, not blocked and not restricted (`MASTER.PARTNER_RESTRICTED`), items active and activated in the company (`item_company`), units convertible, warehouse of the company, currencies. P3 none (orders are not ledger documents), but the order date must not be in the future beyond a setting. P4 prices non-negative, discount 0 to 100, tolerances, DROPSHIP lines need a linked purchase line and no warehouse, STOCK lines need a warehouse. P5 net amounts, tax geography, base quantities. P6 reservations (below). P7 locks in the order of section 9 (the new order row, the item rows when auto-reservation is on), number the order, insert header, lines, address snapshot, event, `command_log`, outbox events (`order.created`, `reservation.created`).

**Reservations (S3, ADR-157).** Reservations are **off by default**. If the company setting `auto_reserve` (3.1) is true, `sales-orders:create` reserves for STOCK lines of stocked items **best effort**: it reserves what is available (possibly zero, TRANSIT and QUARANTINE stock never counts) and marks the shortfall in the response, which is the backorder planned by apps. There is no mode that rejects the order for lack of stock (the former REQUIRED mode and the per-line `reservation_mode` are removed). Without the setting, apps reserve explicitly after placement with `reservations:create` (10, 4.5). A reservation uses `demand_ref_type` ORDER_LINE and `demand_ref` the order line key. Deliveries consume it, changes adjust it, cancellation releases it, whoever created it.

Result: the order with computed fields, reservation results, and warnings.

### 6.3 `sales-deliveries:post`

| Field | Req | Notes |
|---|---|---|
| delivery_date | Req | Posting date of the stock issue |
| warehouse_id | Opt | Default per line |
| carrier_reference, tracking_reference, source_document_ref | Opt | |
| lines[] | Req | `line_ref`, `sales_order_id`, `sales_order_line_no`, `quantity`, `unit_code`, `warehouse_id`, `location_id`, `tracking` (lot or serial numbers, per 10, 3.8.1), `stock_status` (default AVAILABLE) |

Pipeline: P2 customer not restricted (`MASTER.PARTNER_RESTRICTED`), order lines exist and are OPEN, same company, items; stock to be issued is not in a TRANSIT or QUARANTINE location (`INVENTORY.LOCATION_NOT_AVAILABLE`, 10, 3.6). P4 (progress checks, repeated under the locks of P7, section 9) `delivered` + quantity ≤ ordered + over tolerance (`ORDER.OVER_DELIVERY`), line not CLOSED or CANCELLED, order lifecycle OPEN, DROPSHIP lines rejected (use dropship confirmation), warehouse matches or is explicitly changed. P5 the derived stock document of type STOCK_ISSUE: base quantities, tracking, and the stock pipeline of 10 (valuation, running balances, journal Dr COGS Cr Inventory). P6 the stock and finance consistency checks of 10 and 06. P7 takes the locks first, in the single order of section 9 (order rows, item rows, number counters, `chain_head` rows by (chain type, company, scope_key), open items by id, `outbox_head`), and then recomputes the progress checks of P4, the derivation of P5 and the checks of P6 on locked state (D3, ADR-147, the phase model of 6). Insert delivery and lines, link `stock_document_id`, consume reservations (`CONSUME` events), fulfilment chain node with `origin` LIVE, `command_log`, outbox events (`delivery.posted`, `stock.*`, `reservation.consumed`).

Service lines (`fulfilment_type` SERVICE) record delivery without a stock movement. A delivery with only service lines has no stock document. Result: the delivery with each line's stock movement reference and cost, the derived stock and journal documents, the updated progress of the order lines.

`sales-deliveries:reverse`: the owning command of the delivery, its stock document and the derived journal (6.8). Mirror rows with negative quantities and a reversal of the stock document (10, 5.5). Rejected with `ORDER.DELIVERY_INVOICED` (the invoice must be reversed or credited first) if (a) the delivered quantity would fall below the invoiced quantity of a DELIVERED-policy line, or (b) an invoice line that is not reversed **names a line of this delivery** in its delivery reference (`sales_invoice_line.delivery_id` and `delivery_line_no`, 6.9): a given delivery reference protects the delivery line from reversal in both invoice modes and for both invoice policies (D2). Rejected with `ORDER.DELIVERY_RETURNED` (B6) if a return that is not reversed refers to a line of this delivery: the return is reversed first with `sales-returns:reverse` (5.6).

### 6.4 `sales-invoices:create`

One command for both invoice modes (S2, ADR-156). The mode is the company's `sales_invoice_mode` (3.1) in force when the command runs.

| Field | Req | Notes |
|---|---|---|
| invoice_date | Req | Becomes `document_date` and `posting_date` of the AR document unless `posting_date` is given |
| posting_date | Opt | Default the invoice date |
| due_date | Opt | Default computed from the payment terms of the order and the invoice date (07, 9.6) |
| lines[] | Req | `line_ref`, `sales_order_id`, `sales_order_line_no`, `quantity`, `unit_code`, `tax_code_id` (optional override as in 06). Mode CORE_BUILDS: optional `delivery_line_refs` (specific delivery lines). Mode APP_SUPPLIES: `sales_order_id` and `sales_order_line_no` required on every line, `delivery_line_refs` required on every goods line with policy DELIVERED and optional on ORDERED lines (see below), and the price fields `unit_price`, `discount_pct` |

| Mode | Who builds the lines | Rules |
|---|---|---|
| CORE_BUILDS | The caller states what to invoice, the core selects and prices it | Quantities and prices come from the order lines, **not** from the caller. The core selects the invoiceable quantity by `invoice_policy` (DELIVERED or ORDERED, 4.3, "open to invoice"). A goods line with policy ORDERED is invoiced at the ordered quantity **without a delivery** (D2). The caller cannot change prices at invoicing, price changes are order changes (4.4) |
| APP_SUPPLIES | The app sends complete lines | **Each line must carry its order line reference** (`sales_order_id`, `sales_order_line_no`). **A goods line with `invoice_policy` DELIVERED also carries a delivery reference** (`delivery_line_refs`). **Lines with `invoice_policy` ORDERED, goods or service, are validated against `open to invoice` of the order line (4.3) and may carry a delivery reference but need none** (D2, Q24 amended): an ORDERED goods line may be invoiced before delivery in both modes. A missing required reference is rejected with `ORDER.INVOICE_LINE_REFERENCE_MISSING` (`params`: `line_ref`, the missing field). The app supplies quantity, unit price and discount. The core does not select by `invoice_policy` and has **no price rule**: the supplied price and discount are posted as sent and are **stored on the `sales_invoice_line` beside the order line price and discount** in force at invoicing (the order price is a fact, never compared by a rule). Price and discount of the order line are not changed and stay locked by the invoice (4.4) |

Both modes apply the **same validation rules**: references valid, quantity within `open to invoice`, and, where delivery lines are referenced, within those delivery lines. The modes differ only in **which references are required** (table above) and in the origin of the line quantities and prices. Tax determination and posting are the same. A delivery reference that is given, in either mode and with either policy, is stored on the invoice link line and **protects that delivery line from reversal** while the invoice stands (`ORDER.DELIVERY_INVOICED`, 6.3). A mode switch (a new `company_order_setting_version` row) affects **new invoices only**: existing invoices, link rows and their reversals are unchanged.

Invoice lines may come from several orders if they share customer, currency and payment terms (Q13).

Pipeline: P2 customer not restricted (`MASTER.PARTNER_RESTRICTED`), order lines, deliveries. P4 (progress checks, repeated under the locks of P7, section 9) invoiceable quantity: `open to invoice` (4.3) covers the quantity, deliveries referenced must belong to the line. Tax determination per line with the order line's tax class, origin, destination, the customer's VAT status and the tax date. P5 builds the finance document: `AR_INVOICE`, one revenue line per invoice line (account by `REVENUE` determination), tax lines and rounding by the finance pipeline, and the **gross AR control line** (`AR_CONTROL` determination) with the open-item attributes (partner, due date, terms, baseline). P6 the finance balance and account rules. P7 as 06, with the locks in the order of section 9, plus `sales_invoice` and `sales_invoice_line` rows (each line: order line, delivery line (null when no delivery reference is given, for example an ORDERED line invoiced before delivery), quantity, unit price, discount, net amount, in APP_SUPPLIES the order line unit price and discount beside the supplied ones, `journal_line_no` of the revenue line, tax code and version of its tax line, the mode used), `detail_digest` of the journal document (ADR-052), and outbox events (`invoice.posted`, `ledger.document_posted`, `open_item.created`).

Tax point: the line's `tax_date` is the delivery date for goods lines invoiced from a delivery, and the invoice date otherwise. This default is a rule to verify per country (Q4).

Result: the invoice link record and the journal document in the shape of 06, 5.2.

`sales-invoices:reverse`: the owning command of the invoice (6.8). Reverses the journal document (ADR-048, `reversal_of_document_id` with the reason code) and inserts mirror link rows with negative quantities. Rejected if the AR open item has been cleared (`REVERSAL.HAS_CLEARED_LINES`, 06). Rejected with `ORDER.INVOICE_HAS_CREDIT_NOTES` while a credit note of the invoice exists that is not reversed: the credit notes are reversed first with `sales-credit-notes:reverse` (6.5, Q25). The same applies in both modes.

### 6.5 `sales-credit-notes:create`

Credits invoice lines. Two kinds, stated in the request:

| Kind | Payload | Effect |
|---|---|---|
| Return credit | `sales_return_line` references and quantities | Credit at the **original invoice unit price and tax codes** for the returned quantity |
| Amount credit | `sales_invoice_line` reference, `credit_amount_net`, `reason_code` | Price correction or goodwill, no quantity effect. Tax reversed proportionally with the original tax codes (Q11) |

**Credit caps.** A return credit line is capped by the **returned quantity of the return line minus the quantity already credited against it**, and by the **invoiced quantity of the invoice line minus the quantity already credited** (reversed credit notes do not count). An amount credit is capped by the **net amount of the invoice line minus the net amounts already credited** on it. A credit above a cap is rejected with `ORDER.CREDIT_EXCEEDS_INVOICED` (`params`: `line_ref`, `cap`, `requested`, `cap_source` RETURN_LINE or INVOICE_LINE). The same code and caps apply to supplier credit notes (7.3).

Document type `AR_CREDIT`, mirrored structure of an invoice (debit revenue and tax, credit the AR control line with open-item attributes for the credit). The credit may be cleared against the original invoice through the clearing domain by the caller. Result and permissions as for invoices.

**`sales-credit-notes:reverse`** (D1 follow-up, ADR-145). The owning command of a credit note built by `sales-credit-notes:create` and its `AR_CREDIT` journal document. Permission `sales.invoice` (as the original command).

| Field | Req | Notes |
|---|---|---|
| credit_note_id | Req | The credit link record to reverse |
| reason_code | Req | Closed list AUTO_REVERSAL, RERUN, REOPEN, CORRECTION, ERROR, RETURN (06, 5.3). External callers use CORRECTION or ERROR. Missing: `REVERSAL.REASON_REQUIRED` |
| posting_date | Opt | Default the credit note posting date. Explicit, no shifting (06 Q8). Must be postable |

Effect, in one transaction: a mirror `AR_CREDIT` journal document (ADR-048, the mirror keeps the `document_type` `AR_CREDIT`, since it has no mirror type of its own, `reversal_of_document_id` and reason code set, own number and chain node) and mirror link rows with negative quantities and amounts, so the `credited` progress of the invoice and order lines (4.3) and the creditable amount of the invoice line are restored. A return credit does not change the return: the returned quantity stays returned and can be credited again. Rejected if the AR open item of the credit has been cleared (`REVERSAL.HAS_CLEARED_LINES`, 06): the clearing must be reset first. Outbox events: `ledger.document_reversed`, `invoice.reversed` (15, 6, the reversal of `invoice.posted`), `open_item.*` as for the open item. Lock order of section 9, progress computed after the locks.

**Credit note and invoice reversal (14, section 11).** `sales-credit-notes:create` rejects a credit against an invoice that is reversed (the invoice link record has a mirror, 6.4). The rejection code is `ORDER.INVOICE_REVERSED` (14, section 12). Reversing the credit note of a reversed invoice cannot occur, because the invoice cannot be reversed before its credit notes (6.4, `ORDER.INVOICE_HAS_CREDIT_NOTES`).

Error codes (area `ORDER`, adopted in 14, section 11, entered in 06): `ORDER.CREDIT_NOTE_NOT_FOUND`, `ORDER.CREDIT_NOTE_ALREADY_REVERSED`, `ORDER.INVOICE_HAS_CREDIT_NOTES`, `ORDER.INVOICE_REVERSED` (credit note on a reversed invoice), `ORDER.CREDIT_EXCEEDS_INVOICED` (credit above the return line or invoice line cap, new, to be entered in 06). A restricted partner does not block the reversal (6, common rule 1).

### 6.6 Dropship

**Placement.** A DROPSHIP sales line is linked to a purchase line of an existing purchase order for the same item and quantity (the link is set at `sales-orders:create` or by `:change` before the first confirmation). Both lines show the link. The purchase line has no receiving warehouse and its destination country is the customer's.

**`dropship-confirmations:post`.** Payload: `confirmation_date`, `supplier_shipment_reference`, `carrier_reference`, `lines[]` with `sales_order_id`, `sales_order_line_no`, `quantity`. Checks: the sales line is DROPSHIP and linked, the quantity ≤ the **delivery limit** on the sales line and ≤ the **receipt limit** on the linked purchase line (4.3, 4.6: open quantity plus over tolerance, `ORDER.OVER_DELIVERY`). The command inserts the confirmation, counts it as delivered and received, and posts the derived `DROPSHIP_COST` journal (5.3). **Revenue** is recognised by the sales invoice (6.4, DELIVERED policy counts the confirmation as delivered). **Supplier cost** is matched by the supplier invoice (7.2) against the GR/IR credit. Reversal (`dropship-confirmations:reverse`, the owning command of the confirmation and its `DROPSHIP_COST` journal, 6.8): mirror rows and a journal reversal, rejected if the quantity is already invoiced on either side, and rejected with `ORDER.DELIVERY_RETURNED` (B6) if a return that is not reversed refers to the confirmation (`dropship_confirmation_id` on a sales or purchase return line, 5.4): the linked returns are reversed first (5.6). An invoice line that names the confirmation as its delivery reference protects it with `ORDER.DELIVERY_INVOICED` (D2).

### 6.7 Intercompany

`ic-orders:create` places a sales order in company A and a purchase order in company B in one transaction, sharing `ic_transaction_id` and one set of lines (items, quantities, prices, currency). Rules:
- A is the seller, B the buyer. The counterpart partners are the intercompany partners (`ic_company_id`, 07, 9.1). Both orders carry `ic_transaction_id` and a row in `ic_order_link` (`sales_order_id`, `purchase_order_id`).
- Currency: one order currency for both sides. Local amounts use each company's own rates (ADR-016).
- Delivery in A and receipt in B are posted by **one** command `ic-deliveries:post` (permission `ic.deliver`: a sales delivery in A and a purchase receipt in B, the stock issue and the stock receipt, atomically, same date). The receipt value in B is the transfer price (the order price). Both periods must be postable (ADR-016, 04, 7.1). The IC GR/IR of B and the COGS of A follow the normal templates. **Progress:** the same transaction inserts the fulfilment documents of both sides (each a node of its own company's fulfilment chain) and updates the progress of both orders (`delivered` on the sales order lines, `received` on the purchase order lines, 4.3, 4.6) under both order locks. The tolerance checks of 6.3 and 7.1 apply on each side.
- Invoicing: `ic-invoices:create` (permission `ic.invoice`) posts the AR invoice in A and the AP invoice in B as one atomic IC document pair (ADR-016) with the shared `ic_transaction_id`. The pair consists of ordinary `AR_INVOICE` and `AP_INVOICE` documents carrying `ic_transaction_id`: `IC` is not a separate document type (06, 5.2). The invoice mode of A (3.1) does not apply to the pair: the buyer's invoice equals the seller's by construction and no price deviation arises. **The AR side** writes the `sales_invoice` and `sales_invoice_line` link rows and updates `invoiced` on the sales order lines as in 6.4. **The AP side runs the matching of 7.2 in the same transaction**: the GR/IR clearing of 7.2 (the receipt's GR/IR credit of the IC receipt against the invoice's GR/IR debit at the same value R, the clearing domain, 03, no difference line), the `purchase_invoice` and `purchase_invoice_line` link rows with their match fields (price difference zero) and the updated `invoiced` progress of the purchase order lines. Both journals have `origin` = ORDER and carry `ic_transaction_id`.
- **Intercompany marker (ADR-226, 02, 5.4).** `ic-invoices:create` sets `ic_counterpart_company_id` on the control lines (AR and AP) and the BASE lines of both documents (revenue on the seller side, expense, inventory or asset lines on the buyer side), and on the IC account lines where the posting uses them. It never sets it on tax lines. `ic-deliveries:post` posts stock and cost journals at the transfer price (the receipt value in B is the order price): these journals get **no marker** from this rule. Data for the unrealised intercompany profit in stock stays with the order link (`ic_order_link`) and the stock documents, and is computed by the consolidation app (23, O1).
- Changes and cancellations of either order are rejected while the pair exists unchanged on the other side. `ic-orders:change` (permission `ic.order`) changes both. Reversals (`ic-deliveries:reverse`, `ic-invoices:reverse`) are atomic across both (ADR-016) and are the owning commands of the pair's documents (6.8). They take the required `reason_code` of the closed list of 6.8 (AUTO_REVERSAL, RERUN, REOPEN, CORRECTION, ERROR, RETURN, `REVERSAL.REASON_REQUIRED` if missing, external callers use CORRECTION or ERROR), and **both sides carry the same reason code**. The rejections of the single-company commands apply to both sides (`ORDER.DELIVERY_INVOICED`, `ORDER.DELIVERY_RETURNED`, `ORDER.INVOICE_HAS_CREDIT_NOTES`, `REVERSAL.HAS_CLEARED_LINES`), and the AP side of an invoice reversal resets the GR/IR clearing it created (7.4).
- Locks of both companies are taken in the single order of section 9, sorted by company within each position. Partners of both sides must not be restricted.

### 6.8 Reversal rules (D1, ADR-145)

A document derived from another document is reversible **only through the command that owns it**. `journal-documents:reverse` and `stock-documents:reverse` reject these documents.

| Derived document | Owning reversal command |
|---|---|
| Delivery, its stock document and journal | `sales-deliveries:reverse` |
| Receipt, its stock document, journal and the GR/IR clearings it created | `purchase-receipts:reverse` |
| Sales return, its `STOCK_RETURN` document and journal (B5) | `sales-returns:reverse` (5.6) |
| Purchase return, its `STOCK_RETURN` document and journal (B5) | `purchase-returns:reverse` (5.6) |
| Sales invoice (`AR_INVOICE`) with `origin` ORDER | `sales-invoices:reverse` |
| Sales credit note (`AR_CREDIT`) with `origin` ORDER | `sales-credit-notes:reverse` (6.5) |
| Supplier invoice (`AP_INVOICE`) with its `STOCK_COST_ADJUSTMENT` document and clearings | `purchase-invoices:reverse` (7.4) |
| Supplier credit note (`AP_CREDIT`) with its stock cost adjustment and clearings | `purchase-credit-notes:reverse` (7.3) |
| `DROPSHIP_COST` journal | `dropship-confirmations:reverse` |
| IC pairs (`AR_INVOICE` and `AP_INVOICE` with `ic_transaction_id` and `origin` ORDER, no separate IC type) | `ic-deliveries:reverse`, `ic-invoices:reverse` |
| GR/IR write-off document (`GR_IR_WRITE_OFF`, `origin` ORDER) and its clearing | `gr-ir:reverse-write-off` (7.5). `journal-documents:reverse` rejects it |

The owning commands defined in other specs, for completeness: `landed-costs:reverse` (10, 6.8), `vat-returns:reverse-settlement` (08, before filing only, permission `vat.settle`), `year-end:reopen` (05), `clearings:reset` (03), `migration-batches:rollback` (13, a mirror-producing command with reason code CORRECTION).

Every reversal command takes a required `reason_code` from the closed list **AUTO_REVERSAL, RERUN, REOPEN, CORRECTION, ERROR, RETURN** (06, 5.3), and `REVERSAL.REASON_REQUIRED` if it is missing (external callers of `journal-documents:reverse` get `REVERSAL.REASON_NOT_ALLOWED` for AUTO_REVERSAL, RERUN, REOPEN and RETURN, 06, 5.3). Every mirror document sets `reversal_of_document_id` (or `reversal_of_stock_document_id`, the fulfilment `reversal_of_*_id`), carries the reason code and follows the **mirror type rule** (14, section 11): the four mirror types of 02 stay (REVERSAL, REVAL_REVERSAL, CLEARING_RESET, STOCK_REVERSAL, used where 02 and 10 apply them, for example `STOCK_REVERSAL` for stock documents), and a document type without a mirror type of its own **keeps its `document_type`**. That covers the order-built invoices and credit notes (documents with `origin` ORDER): a mirror of an `AR_INVOICE`, `AP_INVOICE`, `AR_CREDIT` or `AP_CREDIT` keeps that type, a mirror of an IC `AR_INVOICE` is an `AR_INVOICE` with the same `ic_transaction_id`. `DROPSHIP_COST` likewise. Reversal commands use the permission of the original command.
- The group currency amount of the IC invoice pair is computed once and copied (ADR-016), so IC balances eliminate to zero at group level.

### 6.9 Invoice and link tables: columns

Column specs derived from the ERD page 13 and the rules of 6.4, 6.5 and 7. All four tables are **E** (insert only, no S column, ADR-048 mirror rows). Lock class L1 for every column unless stated. Audit columns `created_at`, `created_by`, `correlation_id`, `idempotency_key` on every header.

**`sales_invoice`** (link record of an order-built sales document). PK (`tenant_id`, `company_id`, `sales_invoice_id`).

| Column | Type | Null | Notes |
|---|---|---|---|
| invoice_kind | CODE | N | INVOICE, CREDIT_NOTE or REVERSAL (a mirror row set of an invoice or credit note, 6.4, 6.5) |
| customer_partner_id | ID | N | |
| invoice_date, due_date | DATE | N, Y | |
| payment_terms_id | ID | Y | |
| order_currency | CODE | N | The invoice currency (A2) |
| journal_document_id | ID | N | The `AR_INVOICE`, `AR_CREDIT` or the mirror document of a REVERSAL, `origin` ORDER |
| credits_invoice_id | ID | Y | CREDIT_NOTE only: the `sales_invoice` credited |
| credit_kind | CODE | Y | CREDIT_NOTE only: RETURN or AMOUNT (6.5) |
| reversal_of_sales_invoice_id | ID | Y | REVERSAL only: the invoice or credit note record that is mirrored. (The ERD page 13 shows no such column, it is added with this pass) |
| reason_code | CODE | Y | Required for CREDIT_NOTE (amount credit) and REVERSAL (closed list of 6.8) |
| invoice_mode | CODE | N | The `sales_invoice_mode` in force (CORE_BUILDS or APP_SUPPLIES, 6.4). Null on a REVERSAL |
| ic_transaction_id | ID | Y | Set for the AR side of an IC invoice (6.7) |
| source_system, source_document_ref | CODE, TEXT(100) | N, Y | P13, tracing only |

**`sales_invoice_line`**. PK (`tenant_id`, `company_id`, `sales_invoice_id`, `line_no`).

| Column | Type | Null | Notes |
|---|---|---|---|
| sales_order_id, sales_order_line_no | ID, INT | N | |
| delivery_id, delivery_line_no | ID, INT | Y | The delivery line named by the delivery reference (one link line per delivery line when several are named). Null when no delivery reference was given (ORDERED lines invoiced before delivery, D2). A given reference protects the delivery line from reversal (6.3) |
| dropship_confirmation_id | ID | Y | The confirmation named as delivery reference for a DROPSHIP line, instead of `delivery_id` |
| quantity, unit_code, base_quantity | DECIMAL, CODE, DECIMAL | N | Signed: negative on credit note and reversal rows |
| unit_price, discount_pct, net_amount | AMT, RATE, AMT | N | As invoiced, in the invoice currency. CORE_BUILDS: from the order line. APP_SUPPLIES: as supplied by the app |
| order_unit_price, order_discount_pct | AMT, RATE | Y | **APP_SUPPLIES only (null in CORE_BUILDS):** the order line price and discount in force at invoicing, stored beside the supplied ones. A fact, never compared by a rule (6.4) |
| tax_code_id, tax_code_version_id, tax_date | ID, ID, DATE | N | Of the line's tax line (6.4) |
| journal_line_no | INT | N | The revenue line of the journal document |
| credits_invoice_line_no | INT | Y | CREDIT_NOTE: the credited line of `credits_invoice_id` |
| return_id, return_line_no | ID, INT | Y | RETURN credit: the `sales_return_line` credited (6.5) |

**`purchase_invoice`** (supplied by the caller, matched). PK (`tenant_id`, `company_id`, `purchase_invoice_id`). Unique (`tenant_id`, `company_id`, `supplier_partner_id`, `supplier_invoice_number`) for INVOICE and CREDIT_NOTE rows (`ORDER.DUPLICATE_SUPPLIER_INVOICE`).

| Column | Type | Null | Notes |
|---|---|---|---|
| invoice_kind | CODE | N | INVOICE, CREDIT_NOTE or REVERSAL |
| supplier_partner_id | ID | N | |
| supplier_invoice_number | TEXT(100) | Y | Null on a REVERSAL row |
| invoice_date, due_date | DATE | N, Y | |
| currency | CODE | N | Equals the order currency of all matched lines (7.2) |
| gross_amount | AMT | N | The control total of the request (7.2). Negated on credit note and reversal rows |
| journal_document_id | ID | N | The `AP_INVOICE`, `AP_CREDIT` or mirror document, `origin` ORDER |
| stock_document_id | ID | Y | The derived `STOCK_COST_ADJUSTMENT` document, or its mirror, if any (external journal mode, 10) |
| credits_invoice_id, credit_kind | ID, CODE | Y | CREDIT_NOTE only (7.3): the invoice credited, and RETURN or AMOUNT |
| reversal_of_purchase_invoice_id | ID | Y | REVERSAL only (added with this pass, not in the ERD) |
| reason_code | CODE | Y | Required for REVERSAL |
| ic_transaction_id | ID | Y | Set for the AP side of an IC invoice (6.7) |
| source_system, source_document_ref | CODE, TEXT(100) | N, Y | |

**`purchase_invoice_line`** (the per-line match record). PK (`tenant_id`, `company_id`, `purchase_invoice_id`, `line_no`).

| Column | Type | Null | Notes |
|---|---|---|---|
| line_kind | CODE | N | MATCHED (an order or receipt line) or CHARGE (a `charges[]` entry of 7.2). A CHARGE row has `description`, `charge_treatment` (EXPENSE or LANDED_COST), no order or receipt columns |
| description, charge_treatment | TEXT(200), CODE | Y | CHARGE rows only: the charge text and EXPENSE or LANDED_COST. Null on MATCHED rows |
| purchase_order_id, purchase_order_line_no | ID, INT | Y | Null for a line matched to a direct receipt (`stock_document_id` set, D3) and for charges |
| receipt_id, receipt_line_no | ID, INT | Y | The receipt line of the allocation (7.2): one link line per allocated receipt line |
| stock_document_id, stock_document_line_no | ID, INT | Y | **Receipt reference for a direct receipt without an order (D3):** the receipt movement line of a `stock-documents:post` RECEIPT that this invoice line matches |
| base_quantity | DECIMAL | N | Signed: negative on credit note and reversal rows |
| unit_price, net_amount, tax_amount | AMT | N | As supplied |
| order_unit_price | AMT | Y | The order line net unit price at matching |
| match_mode_used | CODE | Y | THREE_WAY or TWO_WAY |
| tolerance_used | RATE | Y | The line tolerance in percent applied at matching (TWO_WAY), informational |
| price_difference_lc | AMT | N | Always posted, never rejected (7.2): invoice value minus receipt value of the matched quantity |
| on_hand_part_lc, consumed_part_lc | AMT | N | Split to inventory and to COGS (10, 6.6) |
| dropship_part_lc | AMT | N | The whole difference of a dropship line to COGS (7.2). Zero otherwise |
| unreceived_gr_ir_lc | AMT | N | The GR/IR debit for the part not yet received on a TWO_WAY line (7.2). Zero otherwise |
| gr_ir_clearing_id | ID | Y | The clearing of 7.2 that cleared the receipt's GR/IR credit against this line's GR/IR debit (03) |
| tax_code_id, tax_code_version_id, tax_date | ID, ID, DATE | N | |
| journal_line_no | INT | N | The line of the journal document |
| credits_invoice_line_no | INT | Y | CREDIT_NOTE: the credited line |
| return_id, return_line_no | ID, INT | Y | RETURN credit: the `purchase_return_line` credited (7.3) |

**`ic_order_link`** (one row per intercompany order pair, 6.7). PK (`tenant_id`, `ic_transaction_id`). Columns: `sales_company_id`, `sales_order_id`, `purchase_company_id`, `purchase_order_id` (all N, L1, FK to the two orders, one pair across two companies), `created_at`, `created_by`, `correlation_id`, `idempotency_key`. Keys: unique (`tenant_id`, `sales_company_id`, `sales_order_id`) and unique (`tenant_id`, `purchase_company_id`, `purchase_order_id`), so an order belongs to at most one pair. FK (`tenant_id`, `sales_company_id`, `sales_order_id`) to `sales_order` and (`tenant_id`, `purchase_company_id`, `purchase_order_id`) to `purchase_order`. Insert only: `ic-orders:change` changes the orders, not the link.

**`purchase_receipt_line`** and **`sales_delivery_line`** are specified in 5.1 and 5.2. For `purchase_receipt_line`: `stock_document_line_no` (INT, L1, the **first** movement line of the receipt line in the derived stock document, null for service lines and migrated receipts) and `stock_document_line_count` (SMALLINT, L1, N, 1 or 2: 2 when the line is split into an INVOICE and a COMPUTED movement line, 7.1, 10, 5.6.1, and 0 for lines without stock document).

## 7. Purchasing: receipts and invoice matching

### 7.1 `purchase-receipts:post`

Mirror of `sales-deliveries:post`: lines reference purchase order lines, quantity limits with over tolerance, tracking data for tracked items (new lots and serials create `item_tracking` rows in the same transaction), the derived stock document of type STOCK_RECEIPT, and the derived journal Dr inventory Cr GR/IR.

**Valuation.** A receipt line is valued at the order line **net unit price**, converted at the receipt date (10, 4.2), except for the part that is **covered by an earlier supplier invoice** (two-way invoicing, 7.2). Let U be the quantity invoiced on the order line and not yet covered by receipts. The first min(received quantity, U) units are valued at the invoice values already posted (the LC amounts of the invoice's GR/IR debit lines, proportional, in invoice order). Any remaining quantity is valued at the order price. The receipt's GR/IR credit for the covered part therefore equals the invoice's GR/IR debit exactly, and the two are **cleared in the same transaction** through the clearing domain (03), without difference lines. No price difference arises at receipt for the covered part: the stock enters at the actual invoiced cost. Inventory value and the movement's `value_tc` use the invoice price for that part. Because `cost_source` is per movement line (D6, ADR-150), a receipt line that is **only partly covered** is **split into two movement lines** of the derived stock document: an INVOICE line for the covered quantity (`cost_source` INVOICE) and a COMPUTED line for the remainder at the order price (`cost_source` COMPUTED). A fully covered receipt line is one INVOICE line, an uncovered one a COMPUTED line (10, 5.6.1). Locks: the GR/IR open items of the invoice debits to be cleared are locked in id order with the other open items (section 9). Receipts reference the supplier's delivery note. Migrated receipts (`origin` = MIGRATED, 13, 6.5) have no stock document, and their lines carry `gr_ir_journal_line` (L1), the line of the opening document that holds the GR/IR open item. Service lines record receipt without stock and without journal (cost follows the invoice). `purchase-receipts:reverse` (the owning command, 6.8) inserts mirror rows, reverses the stock document (10, 5.5) and **resets the clearings the receipt created** against invoice GR/IR debits (`clearings:reset`, 03) in the same transaction, so those invoice debits become open again. It is rejected if the receipt quantity is already returned or the reversal would make received less than the quantity matched three-way (`ORDER.RECEIPT_INVOICED`).

### 7.2 `purchase-invoices:post`

The supplier's invoice is supplied by the caller and matched. Matching is **thin** (S1, ADR-155): the core enforces integrity, apps decide acceptance. The core checks valid references, the quantity limit, the control total and the GR/IR split, and **always posts** a price deviation as price-difference lines and records it on the match record. It does not reject a deviation, and it has no price or quantity tolerance setting and no override permission.

| Field | Req | Notes |
|---|---|---|
| supplier_partner_id | Req | Must not be restricted (`MASTER.PARTNER_RESTRICTED`) |
| supplier_invoice_number | Req | Unique per supplier and company: `ORDER.DUPLICATE_SUPPLIER_INVOICE` |
| invoice_date, posting_date | Req, Opt | Posting date default the invoice date. `tax_date` default as for sales |
| currency | Req | Must equal the order currency of all matched lines |
| due_date | Opt | Default from the order terms |
| gross_amount | Req | The invoice total including tax. Control total |
| lines[] | Req | `line_ref`, then **either** `purchase_order_id` and `purchase_order_line_no` with optional `receipt_line_refs`, **or** `receipt_ref` = `{stock_document_id, line_no}` (D3: the receipt line of a **direct receipt without an order**, posted by `stock-documents:post`, 10, section 7), then `quantity`, `unit_price`, `net_amount`, `tax_code_id` (optional), `tax_amount` (the supplier's tax for the line, used with the tolerance of ADR-027). A line has exactly one of the two reference forms, else `ORDER.INVOICE_LINE_REFERENCE_MISSING` |
| charges[] | Opt | Additional invoice charges without order: `{description, net_amount, tax_code_id, tax_amount, treatment}` with `treatment` EXPENSE (to `PURCHASE_EXPENSE`) or LANDED_COST (to `LANDED_COST_CLEARING`, later allocated through `landed-costs:allocate`) |

**Matching per line.**
- References: order line, named receipt lines and the supplier must exist, belong together and be in the same company (`MATCH.NO_RECEIPT` where a receipt is required).
- **Direct receipt (D3).** A line with `receipt_ref` is matched against that receipt movement line (same company and supplier, a stock `RECEIPT` with a GR/IR credit, `MATCH.NO_RECEIPT` otherwise). It is always THREE_WAY-like: the invoiced quantity of the receipt line (all invoice lines that name it, minus credited quantity) plus this quantity must not exceed the received quantity of the line (`MATCH.QUANTITY_EXCEEDS_RECEIVED`), and TWO_WAY does not exist for it. The difference between invoice value and receipt value is posted as a price difference like for an order line (on-hand and consumed parts, 10, 6.6), and the GR/IR clearing against the receipt's GR/IR credit runs as below. The link line stores `stock_document_id` and `stock_document_line_no` (6.9). Items of such a line need not have an order, so no order progress changes.
- Quantity (D1): THREE_WAY: (invoiced − credited quantity) + this quantity ≤ **received − returned** (`MATCH.QUANTITY_EXCEEDS_RECEIVED`, the returned quantity of the line reduces what may be invoiced, 4.6). TWO_WAY: ≤ ordered + the line over tolerance (percent, L2 on the purchase order line, 14 Q13). There is no company quantity tolerance. A stock item line may be TWO_WAY only if the company setting `allow_invoice_before_receipt` is true (Q2 decided), otherwise `MATCH.TWO_WAY_NOT_ALLOWED`. Allocation to receipt lines for THREE_WAY lines: the named ones, otherwise the oldest receipt lines with uninvoiced quantity (receipt order). For a TWO_WAY line, the part of the invoice quantity already received is matched like THREE_WAY (with the price difference split below), and the part **not yet received** is posted as a GR/IR debit.
- Price: no price rule. The difference between the invoice value I and the receipt value R of the matched quantity is posted as price-difference lines in every case (table below), and for dropship lines the whole difference to `COGS`. It is recorded on the match record per line: order unit price, invoice unit price, difference in LC and the on-hand, consumed and dropship parts. For the not yet received part of a TWO_WAY line no difference arises at invoice time, because the later receipt is valued at the invoice value (below). An app that wants to hold a deviating invoice does so before it sends the invoice.
- Header: the sum of net amounts, charges and tax equals `gross_amount` within the ADR-027 tax tolerance, else `MATCH.GROSS_MISMATCH`.
- Tax: determined by rules (ADR-026) per line from the order line's tax class and geography and the supplier's VAT status. The supplier's `tax_amount` must match within tolerance. Reverse charge codes follow 08. **Partial deduction (ADR-206, 19, 10):** a line for a stock or dropship item, or a charge with treatment LANDED_COST, whose determined or supplied tax code has `deductible_pct` below 100, a `deduction_key` or a `deduction_cap` is rejected with `ORDER.PARTIAL_DEDUCTION_NOT_SUPPORTED` in P4. The non-deductible part would have to become part of the stock or landed cost, which release 1 does not model. Lines for non-stock and service items and charges with treatment EXPENSE are allowed: the non-deductible part is posted as `TAX_NONDEDUCTIBLE` cost on their expense account (`PURCHASE_EXPENSE`).

**Derived finance document** (`AP_INVOICE`) per matched stock or dropship line, with receipt value R (LC) of the matched quantity and invoice net value I (LC):

| Line | Debit | Credit | Notes |
|---|---|---|---|
| Clear GR/IR | `GR_IR` | | R, an open item with no partner, matched to the receipt credit by a clearing in the same transaction (03). R is the receipt value of the matched quantity, so it nets to zero against the receipt line |
| Price difference, on-hand part | `INVENTORY` | | (I − R) × on-hand part. A `COST_ADJUST` movement (`cost_source` INVOICE, 10, 5.6) raises inventory value (10, 6.6 method) |
| Price difference, consumed part | `COGS` | | (I − R) × consumed part. Negative differences reverse the signs |
| Price difference, dropship | `COGS` | | The whole difference, since no stock exists |
| Tax | `INPUT VAT` | | Tax lines by the finance pipeline |
| Supplier | | `AP_CONTROL` | Gross, an open item of the supplier with due date and terms |

**Invoice before receipt (TWO_WAY stock lines, the part not yet received).** The line posts: Dr `GR_IR` at the invoice net value I of the unreceived quantity (an open item with no partner, waiting for the receipt), tax lines as above, and the supplier credit in the `AP_CONTROL` line. There is no inventory or variance posting yet. The later receipt is valued at I for the covered quantity and clears this GR/IR debit (7.1).

On-hand and consumed parts are determined as in 10, 6.6 (FIFO by layer, moving average by cost key quantity). Lines for non-stock and service items: Dr `PURCHASE_EXPENSE` at the invoice net value, no GR/IR unless the item has a receipt (service receipt) in which case the stock-free GR/IR rule applies like dropship. The derived `STOCK_COST_ADJUSTMENT` stock document (with the `COST_ADJUST` movements) uses the invoice journal as its derived journal (10: external journal mode, the stock document stores the invoice journal document id and the journal binds the combined digest of both through `detail_digest`, ADR-052 extended to a digest of digests). Charges of treatment LANDED_COST debit `LANDED_COST_CLEARING`.

The clearing of GR/IR items happens in the same transaction through the clearing domain (03): for each matched receipt line, the receipt's GR/IR credit item and the invoice's GR/IR debit line (partial amounts proportional to the matched quantity). No difference line arises because both sides carry the same value R.

**Locks (D3, ADR-147).** The GR/IR open items of the receipts to be cleared are determined by a read, then locked in id order together with the other open items at their place in the lock order of section 9, and re-checked under the lock before the derivation. The match derivation (invoiced quantities, price differences, on-hand and consumed parts) runs only after all locks are held.

Result: the `purchase_invoice` record with per-line match details (receipt allocation, order and invoice unit price, price difference parts), the journal document and the stock cost adjustment document if any, and the clearings made.

### 7.3 `purchase-credit-notes:post`

Supplier credit note, supplied and matched against purchase returns or invoice lines. Tax codes with partial deduction follow the same rejection and the same allowed lines as in 7.2 (`ORDER.PARTIAL_DEDUCTION_NOT_SUPPORTED`), and an allowed line takes the key rate of the credit note's own tax date (19, 7). A credit note against an invoice that is reversed is rejected with `ORDER.INVOICE_REVERSED` (as 6.5). Mirror of 7.2 with opposite signs: credits expense or inventory adjustment for price corrections, debits the AP control line. A credit for returned goods references the purchase return line, clears the debit open item of the return (5.4) and relieves GR/IR or the earlier price difference parts at the original values. **The credit note step still finds the return open item (D1):** the cap of 7.2 (received minus returned) limits what may be invoiced, it does not remove the debit open item that the purchase return created. The credit note locates that open item through the purchase return line reference (`return_id`, `return_line_no`, 6.9) and not through the invoice cap, and the credited quantity reduces the invoiced side of the cap (`invoiced − credited quantity`, 4.6), so an invoice that was already complete before the return can be credited down to the cap. **Caps:** a credit line is capped by the returned quantity of the return line minus the quantity already credited, and by the invoiced quantity of the invoice line minus the quantity already credited, amount credits by the net amount of the invoice line minus the net amounts already credited, else `ORDER.CREDIT_EXCEEDS_INVOICED` (6.5).

**`purchase-credit-notes:reverse`** (D1 follow-up, ADR-145). The owning command of a supplier credit note (`AP_CREDIT`), its derived `STOCK_COST_ADJUSTMENT` document (price corrections on stock items) and the clearings it created (for example against the debit open item of a purchase return, 5.4). Permission `purchase.invoice`. Fields: `credit_note_id` (Req), `reason_code` (Req, closed list AUTO_REVERSAL, RERUN, REOPEN, CORRECTION, ERROR, RETURN, `REVERSAL.REASON_REQUIRED` if missing), `posting_date` (Opt, default the credit note posting date, explicit, no shifting). In one transaction: mirror `AP_CREDIT` journal document (keeps the `document_type` `AP_CREDIT`, ADR-048, `reversal_of_document_id`, reason code), mirror stock cost adjustment (10, 5.5, `cost_source` unchanged at the original value), reset of the clearings (`clearings:reset`, 03) so the return's debit open item and the earlier GR/IR or price difference parts become open again, mirror link rows with negative quantities and amounts. Rejected if the AP open item of the credit note has been cleared by a payment (`REVERSAL.HAS_CLEARED_LINES`). Outbox events: `ledger.document_reversed`, `stock.document_reversed` and `stock.balance_changed` where stock moved, `invoice.reversed` (15, 6), `open_item.*`. Lock order of section 9, including the open items to be reset in id order. `purchase-credit-notes:post` rejects a credit against an invoice that is reversed with `ORDER.INVOICE_REVERSED` (adopted, as 6.5), and the invoice cannot be reversed before its credit notes (7.4). Error codes (area `ORDER`, adopted in 14, section 11, entered in 06): `ORDER.CREDIT_NOTE_NOT_FOUND`, `ORDER.CREDIT_NOTE_ALREADY_REVERSED`, `ORDER.INVOICE_REVERSED`. New, to be entered in 06: `ORDER.CREDIT_EXCEEDS_INVOICED`.

### 7.4 Reversal

`purchase-invoices:reverse` (the owning command of the invoice, its `STOCK_COST_ADJUSTMENT` document and its clearings, 6.8; takes a required `reason_code`): reverses the journal document and the stock cost adjustment (mirror, ADR-048, 10, 5.5) and the clearings (`clearings:reset`, 03) in one transaction, and inserts mirror link rows with negative quantities. Rejected if the AP open item has been cleared by a payment (`REVERSAL.HAS_CLEARED_LINES`). Rejected with `ORDER.INVOICE_HAS_CREDIT_NOTES` while a credit note of the invoice exists that is not reversed (reverse the credit notes first with `purchase-credit-notes:reverse`, 7.3, Q25). Rejected with `INVENTORY.ALLOCATION_EXISTS` (10, 6.8, adopted) while a `LANDED_COST` charge line of the invoice has a landed cost allocation that is not reversed: `landed-costs:reverse` first, then the invoice.

### 7.5 `gr-ir:write-off` (D3, 17 sections 4 and 5)

Clears a **GR/IR residual** that no supplier invoice will match: a receipt that is never invoiced, an invoice debit with no further receipt, an orphan item of a migrated receipt or of a direct receipt without an order. Permission `purchase.invoice`. The command posts a derived journal document and clears the selected GR/IR open items against it, so the GR/IR account (open-item mode GL_OPEN_ITEM, no partner) is reconciled by items and `gr-ir:query` (8) shows no residual. A write-off is undone only by `gr-ir:reverse-write-off` (7.5a).

| Field | Req | Notes |
|---|---|---|
| posting_date | Req | Must be postable in finance. Explicit, no shifting |
| reason_code | Req | Closed list of 6.8 does not apply (not a reversal): free `CODE` from the tenant list of write-off reasons plus `reason_text` (TEXT(200), Req) |
| items[] | Req | Per entry exactly one selector: `open_item_ids` (the GR/IR open items), or `purchase_order_id` and `purchase_order_line_no` (all open GR/IR items of the line), or `receipt_ref` = `{stock_document_id, line_no}` (a direct receipt line), and `expected_residual_lc` (AMT, signed: the residual the caller saw, compare-and-set) |

Effect, in one transaction:
- A derived journal document (type **`GR_IR_WRITE_OFF`**, system generated, `origin` = ORDER, `source_system` = ORDER_MGMT) with one `GR_IR` line per cleared residual (the opposite sign of the residual, with the GL open-item attributes) and one counter line on the account of the account determination role **`GR_IR_WRITE_OFF`** (3.3, open-item mode NONE, so the counter line is no open item; keys item group, item, warehouse of the line, the `GR_IR` and `PRICE_VARIANCE` rules do not apply to it). Price difference and write-off stay separate in the ledger. The `GR_IR` residual items and the new `GR_IR` lines are cleared against each other in the same transaction through the clearing domain (03), without difference line.
- No stock movement, no change of the order progress (`received`, `invoiced`). The write-off is recorded on the order line only through `gr-ir:query` (state WRITTEN_OFF).
- Outbox events: `ledger.document_posted`, `clearing.posted`, `open_item.*` (15, 6).

Rejections (area `ORDER`, new, to be entered in 06): `ORDER.GRIR_ITEM_INVALID` (an item is not an open GR/IR item of the company, or is already cleared), `ORDER.GRIR_RESIDUAL_CHANGED` (the current residual differs from `expected_residual_lc`, `params`: the current value). `INVENTORY.ACCOUNT_DETERMINATION_MISSING` if no `GR_IR_WRITE_OFF` rule matches. Locks in the order of section 9: the order rows of the selected lines, counters, the LEDGER and CLEARING heads, the `fiscal_period` row of the posting date in share mode (status re-checked), the open items in id order (read first, locked, re-checked under the lock), `outbox_head`. The derivation runs after the locks.

### 7.5a `gr-ir:reverse-write-off` (17 section 5)

The **owning reversal command** of a `GR_IR_WRITE_OFF` document and of the clearing it created. `journal-documents:reverse` rejects the document (6.8). Permission `purchase.invoice`.

| Field | Req | Notes |
|---|---|---|
| journal_document_id | Req | The `GR_IR_WRITE_OFF` document to reverse |
| reason_code | Req | **CORRECTION or ERROR.** Missing: `REVERSAL.REASON_REQUIRED`. Any other value of the closed list of 6.8: `REVERSAL.REASON_NOT_ALLOWED` |
| reason_text | Opt | TEXT(200) |
| posting_date | Opt | Default the write-off posting date. Explicit, no shifting (06 Q8). Must be postable in finance |

Effect, **atomic in one transaction** (all or nothing, ADR-094): the clearing of the write-off is reset (`clearings:reset`, 03), so the GR/IR residual items are open again. A mirror journal document of the write-off is posted (ADR-048: the type has no mirror type of its own and keeps `GR_IR_WRITE_OFF`, `reversal_of_document_id`, the reason code, `origin` ORDER). The mirror open items of the write-off's `GR_IR` lines are cleared against the originals automatically with a clearing record and no document (B3, 17 section 4). The residual state of `gr-ir:query` returns from WRITTEN_OFF to open. No stock movement and no change of order progress.

Outbox events: `gr_ir.write_off_reversed` (15, 6: entered with this pass), `ledger.document_reversed`, `clearing.reset`, `open_item.*`.

Rejections (area `ORDER`, new, to be entered in 06): `ORDER.GRIR_WRITE_OFF_NOT_FOUND` (not a `GR_IR_WRITE_OFF` document of the company), `ORDER.GRIR_WRITE_OFF_REVERSED` (already reversed). Locks as 7.5, and the open items to be reset in id order (section 9).

## 8. Reads (09 conventions, permission `read.orders`)

| Operation | Notes |
|---|---|
| `sales-orders:get`, `:list`, `purchase-orders:get`, `:list` | Filters: partner, dates, status, item, warehouse, `ic_transaction_id`, source reference. Includes `lines`, `progress` (computed measures), `events`, `changes`, `address` (logged), `fulfilments`, `invoices` |
| `sales-deliveries`, `purchase-receipts`, `dropship-confirmations`, `sales-returns`, `purchase-returns`, `sales-invoices`, `purchase-invoices` | `get` and `list` with the filters of their fields. Invoices include the journal document. `sales-invoices` and `purchase-invoices` also filter by `issue_state` (09, 3, 22, delta 6) and include `issue_records` |
| `open-orders:query` | Order lines with open quantity (sales: **open quantity** of 4.3, purchase: **open to receive** of 4.6, over tolerance not counted), with both line dates (`requested_date`, `confirmed_date`), by item, warehouse, partner. Supports availability planning with `availability:query` (10). `supply-demand:query` (16, 4) uses the same open-quantity measures, so both reads agree |
| `price-differences` on match records | Included in `purchase-invoices:get` per line (7.2) |
| `uninvoiced:query` | Delivered not invoiced (sales) and received not invoiced (purchase) per order line with values. The purchase side is the GR/IR detail |
| `gr-ir:query` | GR/IR open items by purchase order, receipt (including direct receipts without an order) and supplier invoice, with age and value, and the items cleared by `gr-ir:write-off` (7.5). Reconciles to the GL balance of the GR/IR account |
| `price-variances:query` | Invoice price differences by item, supplier, period, with the on-hand and consumed split |
| `order-changes:query` | The change log of an order or of all orders in a period |
| `credit-exposures:query` | Credit limit versus exposure per customer (8.1). Needs `read.orders` and `read.ledger` |

### 8.1 `credit-exposures:query` (Q12 decided)

The core does not enforce credit limits. It answers the question "what is this customer's exposure against the limit on the customer master data" so that apps decide.

| Parameter | Req | Notes |
|---|---|---|
| company_id | Req | |
| partner_id | Req, or a list | One or several customers. Several need `read.cross_company` only if they span companies |
| as_of | Opt | Default now. Exposure is as of this moment (receivables as of the date, orders and deliveries as currently recorded) |
| proposed_amount | Opt | An additional amount (for example a new order being placed) in the limit currency. The result shows headroom after it |
| convert | Opt | `LIMIT_CURRENCY` (default): all exposure components converted to the limit currency with the SPOT rate of the date (derivation of 07, 7.2). `NONE`: components per currency, no conversion |
| tax_basis | Opt | `ESTIMATED_GROSS` (default, Q16 decided): delivered-not-invoiced and open-order amounts include tax estimated through the tax determination (order line tax class, geography, customer VAT status), labelled an estimate. `NET`: net amounts only |
| scope | Opt | `COMPANY` (default): the limit on the company relation against that company's exposure. `TENANT` (Q17 decided): the tenant-level limit on `business_partner` against the sum of the exposure of the customer in all companies of the tenant, each converted to `tenant_credit_limit_currency`. Needs `read.cross_company` and company access to every company with exposure. The result then also lists the exposure per company, so a caller sees where it sits |

Result per partner: `credit_limit` and its currency (null if none), and the exposure grouped as:

| Component | Content |
|---|---|
| `ar_open_items` | Remaining open items of the customer on CONTROL accounts as of the date, from `open-items:query` (09, 4.4). Credit open items (credit notes, unallocated payments) reduce it. Of which `overdue` (due date before the date) |
| `delivered_not_invoiced` | For DELIVERED-policy lines (and dropship confirmations): delivered quantity minus invoiced quantity, valued at the order line net price (net of discount) |
| `open_orders` | For OPEN orders: the **open quantity** of 4.3 (ordered minus delivered, not below zero, lines not CLOSED or CANCELLED, over tolerance not counted) at the order line net price. ORDERED-policy lines: ordered minus invoiced. The same definition as `open-orders:query` and `supply-demand:query` |
| `total_exposure` | Sum of the three |
| `headroom` | `credit_limit` − `total_exposure` (− `proposed_amount`). Null without a limit |
| `over_limit` | True if headroom is negative |

Each component carries the per-currency breakdown (the source currencies and amounts) so a caller sees what was converted. The response also states the rate dates used. The query reads the open-item cache path of 09 and the computed order progress (4.3) under one consistent snapshot (09, 2.2). It never blocks or changes anything. A customer without a limit returns the exposure and no headroom.

## 9. Concurrency and lock order

**One authoritative lock order (D3, ADR-147; defined in 04, 6.3 and repeated in 10, 6.4):**

| Position | Lock | Sorted by |
|---|---|---|
| 1 | Order rows (`sales_order`, `purchase_order`) of the command | (company, kind, id). Intercompany commands lock both orders in this order |
| 2 | Stock `item_company` rows | (company, item) |
| 3 | Number range counters of the documents the command creates (D3 follow-up: **before** the chain heads) | order of 04, 6.3 |
| 4 | `chain_head` rows | The tuple (**chain type, company, scope_key**) (X4, as 02 and 04), the chain types in the fixed order LEDGER, CLEARING, PERIOD_EVENT, VAT_RETURN, STOCK, FULFILMENT (the VAT_RETURN head is one per company, empty scope key, K1). An intercompany command, which locks the heads of two companies, locks them in this tuple order. A fulfilment command takes the FULFILMENT head last among the heads |
| 5 | `fiscal_year` and `fiscal_period` rows (04, 6.3, new class). Period-dependent commands take them in **share** mode after their head locks and re-check the status | (company, fiscal_year, period_no) |
| 6 | Open items (for example the GR/IR open items that a receipt or invoice clears) | id |
| 7 | `outbox_head` | last (15, 8) |

A clearing inside a command that can produce an exchange difference (03) pre-locks the LEDGER head and the counter of its difference document at their positions before the open items are cleared. `business_partner.retention_until` is not written per posting (batch job, 12) and has no position.

**Derivation after the locks.** All progress sums, quantity and tolerance checks, balances, match results and tax determination that depend on locked state are computed **after** the locks of this order are held, never before. Static checks (schema, references, permissions) may run earlier. Where the set of open items to lock depends on a derivation (GR/IR clearing at receipt reversal and invoice matching), the candidate set is read first, locked in id order, and re-checked under the lock. Progress is always computed under the order lock.

## 10. Deltas to earlier specs

| Spec | Change |
|---|---|
| 10 `account_determination` | Key column `partner_id`, roles `AR_CONTROL`, `AP_CONTROL`, `REVENUE`, `PURCHASE_EXPENSE`, `DROPSHIP_CLEARING`, `GR_IR_WRITE_OFF` (3.3) |
| 10 stock movements and documents | Document type `STOCK_RETURN`, movement types `RETURN_IN` and `RETURN_OUT` with `valuation_ref_document_id` and `valuation_ref_line_no` (L1): the value comes from the referenced original movement (`cost_source` COMPUTED, listed in 10, 5.6). `cost_source` INVOICE for receipts valued at an earlier invoice and for invoice-derived `COST_ADJUST`. External journal mode for `STOCK_COST_ADJUSTMENT` documents derived from purchase invoices. TRANSIT and QUARANTINE locations are not deliverable or reservable |
| 02 | New system document types `DROPSHIP_COST` and `GR_IR_WRITE_OFF` (no mirror type of their own, 7.5a). `detail_digest` may be a digest of several component digests (stock document, order records). AR and AP types stay open to external posting, but documents with `origin` ORDER (set by the core, X5) and all derived journals are not reversible through `journal-documents:reverse` (6.8, D1). New L1 column `journal_document.origin` (EXTERNAL, ORDER, STOCK, VAT, MIGRATION, SYSTEM). The fulfilment chain head is a `chain_head` row (K1), anchored in `chain_anchor` by `chain_type` and `scope_key` (empty). Mirror types: REVERSAL, REVAL_REVERSAL, CLEARING_RESET and STOCK_REVERSAL stay, types without a mirror type of their own (order-built invoices and credit notes, `DROPSHIP_COST`, `VAT_SETTLE`, `YEAR_END`) keep the original type |
| 04 | Series assignments for the order document types (3.2). Lock order of section 9 (D3), as the seven classes of 04, 6.3: order rows, item rows, counters, `chain_head` rows, `fiscal_year` and `fiscal_period` rows, open items, `outbox_head` |
| 06 | Error areas `ORDER` (`OVER_DELIVERY`, `PRICE_LOCKED_INVOICED`, `DELIVERY_INVOICED`, `RETURN_EXCEEDS_DELIVERED`, `DUPLICATE_SUPPLIER_INVOICE`, `LINE_CLOSED`, `INVOICE_NOT_ALLOWED`, `DROPSHIP_LINK_INVALID`, `DATE_INVALID`, `INVOICE_LINE_REFERENCE_MISSING`, and others) and `MATCH` (`QUANTITY_EXCEEDS_RECEIVED`, `GROSS_MISMATCH`, `TAX_OUT_OF_TOLERANCE`, `NO_RECEIPT`, `TWO_WAY_NOT_ALLOWED`). `MATCH.PRICE_OUT_OF_TOLERANCE` is **removed** (S1). Adopted `ORDER` codes `CREDIT_NOTE_NOT_FOUND`, `CREDIT_NOTE_ALREADY_REVERSED`, `INVOICE_HAS_CREDIT_NOTES` and `INVOICE_REVERSED` (6.4, 6.5, 7.3, 7.4, 14 section 11). `INVENTORY.ALLOCATION_EXISTS` (10, 6.8) is adopted and entered in 06 and is returned by `purchase-invoices:reverse` (7.4). `INVOICE_LINE_REFERENCE_MISSING` also covers the APP_SUPPLIES rule of 6.4 (order line reference always, delivery reference on goods lines with policy DELIVERED, none required for ORDERED lines) and the two reference forms of supplier invoice lines (7.2). New `ORDER` codes of this pass, to be entered in 06: `CONFIRMED_DATE_ON_CREATE`, `DELIVERY_RETURNED`, `CREDIT_EXCEEDS_INVOICED`, `RETURN_NOT_FOUND`, `RETURN_ALREADY_REVERSED`, `RETURN_CREDITED`, `GRIR_ITEM_INVALID`, `GRIR_RESIDUAL_CHANGED`, `GRIR_WRITE_OFF_NOT_FOUND`, `GRIR_WRITE_OFF_REVERSED`. New commands `sales-returns:reverse`, `purchase-returns:reverse`, `gr-ir:write-off`, `gr-ir:reverse-write-off` with their permissions (6.1). Permissions of 6.1 including `ic.order`, `ic.deliver`, `ic.invoice` (approved, D10), `order.promise`, and `read.orders`. The reversal commands `sales-credit-notes:reverse` and `purchase-credit-notes:reverse` use `sales.invoice` and `purchase.invoice`. The reason code list and the owning-command table of `journal-documents:reverse` (06, 5.3) are as in 6.8, and IC is not a document type. `purchase.match_override` is **removed**. Command catalogue gets the IC commands and `change-line-dates`. `MASTER.PARTNER_RESTRICTED` applies in the P2 of the order commands (6) |
| 07 | `company_order_setting_version` (3.1: `sales_invoice_mode`, `auto_reserve`) in the organisation tables, document types in readiness (11.3). `purchase_tolerance_override` is **removed** from the table overview. `partner_company_relation` gains `credit_limit` and `credit_limit_currency` (8.1). Role table with open-item mode per role (3.3) |
| 09 | Reads of section 8, audit events add orders and fulfilment documents. `open-orders:query` returns both line dates. `include=extensions` on orders and open items (15, 4.4) |
| 12 | The restriction of a partner (`restriction_status`) is checked in P2 of the commands of 6 |
| 13 | `legacy_order_ref`, `legacy_line_ref`, `origin`, `migration_batch_id`, `gr_ir_journal_line` (4.1, 4.2, 5.1 to 5.3) |
| 15 | Order and fulfilment commands write the outbox events of 15, section 6 in their transaction. `extension_definition` entity types `SALES_ORDER`, `SALES_ORDER_LINE`, `PURCHASE_ORDER`, `PURCHASE_ORDER_LINE`, `OPEN_ITEM` |
| 16 | Line dates and `change-line-dates` (4.2, 4.4, 4.6), `order.promise`, `order_change_log` records the date fields. Header `expected_receipt_date` is the default of the line `requested_date`. `supply-demand:query` reads orders |

Added in v0.4 (14, section 11): receipt line `stock_document_line_no` and `stock_document_line_count` (5.2), APP_SUPPLIES goods lines need a delivery reference (6.4), invoice reversal rules with credit notes (6.4, 6.5, 7.3, 7.4), `ORDER.CREDIT_NOTE_*` and `ORDER.INVOICE_HAS_CREDIT_NOTES` adopted, `ORDER.INVOICE_REVERSED` adopted (14 section 12), mirror type rule (1, 6.8), `vat-returns:reverse-settlement` (6.8). 10 delta: `purchase-invoices:reverse` returns `INVENTORY.ALLOCATION_EXISTS` while charge-line allocations stand (7.4). 08 delta: the settlement reversal command is named `vat-returns:reverse-settlement`.

**Added in v0.5 (17, section 4, fix pass 4).** X3: the names `sales_invoice_mode`, `auto_reserve` and the validity column `effective_from` were verified in this spec and are consistent (3.1). X5: order-built documents are identified by `journal_document.origin` = ORDER (2.2, 6.8). X7: one phase model, locks at P7 (6). X4: lock tuple (9). F12: `confirmed_date` rejected on create (4.2, 4.6, 6.2). D1: THREE_WAY cap received minus returned, credit note step finds the return open item (4.6, 7.2, 7.3). D2: ORDERED goods lines invoiceable before delivery in both modes, delivery reference protects the delivery line (`ORDER.DELIVERY_INVOICED`), the "same validation" wording of 6.4 corrected. D3: `gr-ir:write-off` (7.5) and `receipt_ref` on supplier invoice lines (7.2, 6.9). D4: open quantity per side (4.3, 4.6, 8, 8.1). D5: migrated `value_lc` as valuation basis, `dropship_confirmation_id` (5.1, 5.2, 5.4). B5: return reversal commands (5.6, 6.8). B6: `ORDER.DELIVERY_RETURNED` (6.3, 6.6). IC: AP side matching and link rows, progress, reversal reason codes (6.7). `ORDER.CREDIT_EXCEEDS_INVOICED` (6.5, 7.3). Column specs of the invoice and link tables (6.9). Address snapshot field group ORDER_ADDRESS (4.7). Stale text of 7.3 (`ORDER.INVOICE_REVERSED`) fixed. 02 delta: `journal_document.origin`. 09 delta: field group ORDER_ADDRESS, open-quantity reference. 10 delta: valuation reference to a fulfilment line for migrated deliveries (4.2), `DROPSHIP_CLEARING` role default (10, 3.9). 13 delta: `value_lc` on `DELIVERED_NOT_INVOICED` records. 15 delta: `return.reversed`. 16 delta: open-quantity definition of 4.3 and 4.6.

**Added in v0.6 (17, section 5, propagation pass).** `gr-ir:write-off` posts its counter line on the new role `GR_IR_WRITE_OFF` (3.3, open-item mode NONE) in a `GR_IR_WRITE_OFF` document (origin ORDER), `gr-ir:reverse-write-off` is its owning reversal command (reason CORRECTION or ERROR, atomic mirror, codes `ORDER.GRIR_WRITE_OFF_NOT_FOUND` and `ORDER.GRIR_WRITE_OFF_REVERSED`, event `gr_ir.write_off_reversed`), Q26 closed (7.5, 7.5a, 6.8). The outbox mapping of `order_event` is stated in 4.7 as in 15, 6. The lock order of section 9 has seven positions as 04, 6.3 (`fiscal_year` and `fiscal_period` in share mode at position 5, open items 6, `outbox_head` 7). 15 delta: `gr_ir.write_off_reversed` to be listed in 15, 6. 06 and 07 delta: the new command and codes, permission `purchase.invoice`, role `GR_IR_WRITE_OFF` in the role table of 07, 6.3. 10 delta: role `GR_IR_WRITE_OFF` (10, 3.9).

These deltas are applied in v0.2 (re-baseline of 14, sections 8 and 9). Added in v0.3 (14, section 10): the two credit note reversal commands (6.5, 7.3, 6.8), APP_SUPPLIES price and reference rules (6.4), `cost_source` split of partly covered receipts (7.1), counters before chain heads (9), `scope_key` anchoring (5.5), approved IC permissions (6.1), IC documents are `AR_INVOICE`/`AP_INVOICE` (6.7), `order_address_snapshot.retention_until` is S (4.7).

**Added 2026-10-14 (ADR-206, 19).** `ORDER.PARTIAL_DEDUCTION_NOT_SUPPORTED` on supplier invoice and credit note lines for stock and dropship items and LANDED_COST charges whose tax code has partial deduction, a key or a cap (7.2, 7.3). Entered in 06, 2.4. The derived `AP_INVOICE` journal of 7.2 gets `TAX_NONDEDUCTIBLE` lines on allowed expense lines through the finance pipeline (02, 5.1) and needs no change in the derivation table.

**Added 2026-10-19 (ADR-226, 23).** The intercompany marker rule of 6.7: control lines and BASE lines of an IC invoice pair carry `ic_counterpart_company_id`, tax lines and the journals of IC deliveries do not.

## 11. Assumptions

| Ref | Item |
|---|---|
| A1 | Revenue is recognised at the sales invoice. There is no delivered-not-invoiced accrual in R1 (Q3). Cost of goods sold is recognised at delivery |
| A2 | An invoice is in the order currency. The exchange rate of the invoice date applies through the finance pipeline (ADR-019) |
| A3 | Orders and invoice link records are not hash-chained (change log, events, journal chain). Fulfilment documents are chained per company (5.5, Q1 decided) |
| A4 | The core enforces only `partner_company_relation.status`. Credit limits are stored and compared with exposure by a read API (8.1) but never enforced. Holds and approval states are app concerns |
| A5 | One order has one customer or supplier and one currency |
| A6 | Pricing, discounts and rebates are computed by apps. The core multiplies and rounds |

## 12. Open questions

| # | Question |
|---|---|
| Q1 | **Decided:** fulfilment documents (deliveries, receipts, returns, dropship confirmations) form one hash chain per company, bound to their stock or journal documents through `detail_digest` (5.5). Orders are not chained. |
| Q2 | **Decided (differs from the proposal):** invoice before receipt is allowed for stock items when the company setting `allow_invoice_before_receipt` is true. The invoice posts a GR/IR debit, the later receipt is valued at the invoiced value for the covered quantity and clears it (7.1, 7.2). |
| Q3 | **Decided:** revenue follows the sales invoice, no delivered-not-invoiced accrual in R1. |
| Q4 | **Decided:** the default tax point is the delivery date for goods invoiced from a delivery and the invoice date otherwise. To verify per country before use (P15), the caller can override per line. |
| Q5 | **Decided:** control accounts, revenue and purchase expense are roles of the versioned `account_determination` table with a partner key (3.3). |
| Q6 | **Decided:** returns are valued from the original movement through a validated reference, a controlled exception to computed costs (10 Q5). |
| Q7 | **Decided:** the address snapshot and the order change log are shared with an `order_kind` discriminator. |
| Q8 | **Superseded by Q18 (S3):** the default reservation mode was BEST_EFFORT with REQUIRED and NONE per line. Now auto-reservation is a company setting, default off, BEST_EFFORT only. A backorder is still the unreserved open quantity of a line, planned by apps. |
| Q9 | **Decided:** `lifecycle_status` and `line_status` are S columns, `order_event` is the truth and `chains:verify` recomputes them. |
| Q10 | **Superseded by Q17a (S1, ADR-155):** company tolerances with versioned overrides per supplier and item group (v0.1, 3.1a) are removed. The core keeps only the line tolerance in percent. |
| Q11 | **Decided:** amount credit notes are allowed with a reason, tax reversed proportionally on the original codes. Country rules for corrections are to be verified and refined in country packages. |
| Q12 | **Decided (extended by you):** the core enforces only the relation status. A credit limit on the customer relation is stored, and `credit-exposures:query` (8.1) compares it with exposure grouped by AR open items, delivered goods not yet invoiced and open orders. Apps decide, nothing is blocked. |
| Q13 | **Decided:** one sales invoice may cover lines of several orders of one customer when currency and payment terms agree (6.4). |
| Q14 | **Decided:** no automatic close. A line closes by fulfilment and invoicing, or by explicit `close-line`. The under-tolerance only tells apps when a line may be treated as complete. |
| Q15 | **Decided:** delivery and receipt numbers use number series that can be flagged `is_legal` (gapless). The company decides per series. Country needs are verified per package. |
| Q16 | **Decided:** exposure uses estimated gross by default for delivered-not-invoiced goods and open orders (8.1), net selectable. |
| Q17 | **Decided (differs from the proposal):** a tenant-level credit limit on `business_partner` is added beside the company-level limit. Exposure with scope TENANT sums the customer's exposure over all companies, converted to the limit currency (8.1). For persons, credit limits are personal data (07). |
| Q17a | **Decided (14 S1, Q13):** thin matching. The core enforces references, quantity ≤ ordered + line tolerance (percent, L2), control total and the GR/IR split. A price deviation is always posted as price-difference lines and recorded on the match record, never rejected. `purchase_tolerance_override`, `purchase.match_override`, the price tolerance fields and `MATCH.PRICE_OUT_OF_TOLERANCE` are removed (ADR-155). `match_mode_default` and `allow_invoice_before_receipt` stay in `company_order_setting_version` (14, section 10, S1, 3.1). |
| Q18 | **Decided (14 S3, Q12):** auto-reservation is the company setting `auto_reserve` in `company_order_setting_version`, default off, BEST_EFFORT only. The stricter modes and the per-line `reservation_mode` are removed (ADR-157). |
| Q19 | **Decided (14 S2, Q11):** one command `sales-invoices:create`. The per-company versioned setting `sales_invoice_mode` selects CORE_BUILDS or APP_SUPPLIES. APP_SUPPLIES requires each line to carry its order line reference, and a delivery reference except on ORDERED service lines (amended by 14 section 10). New companies default to CORE_BUILDS. A switch affects new invoices only (ADR-156). |
| Q20 | **Decided (14 section 10, D1 follow-up):** `sales-credit-notes:reverse` (6.5) and `purchase-credit-notes:reverse` (7.3) are the owning commands of credit notes. Permissions as the original commands, `reason_code` required (AUTO_REVERSAL, RERUN, REOPEN, CORRECTION, ERROR, RETURN), atomic reversal of finance and stock effects and clearings, outbox events, mirror documents keep the original `document_type`. The 6.8 table is updated. |
| Q21 | **Decided (14 section 10, S2):** in APP_SUPPLIES the app sends price and discount per line. The core stores them beside the order price on the invoice link row and has no price rule (6.4). ORDERED service lines require the order line reference instead of a delivery reference, `ORDER.INVOICE_LINE_REFERENCE_MISSING` is the rule for missing references. |
| Q22 | **Decided (14 section 11):** `ORDER.CREDIT_NOTE_NOT_FOUND` and `ORDER.CREDIT_NOTE_ALREADY_REVERSED` are adopted and entered in 06, with `ORDER.INVOICE_HAS_CREDIT_NOTES` (6.4, 6.5). |
| Q23 | **Decided (14 section 11):** `purchase_receipt_line` holds `stock_document_line_no` (the first movement line) and `stock_document_line_count` (5.2). |
| Q24 | **Decided (14 section 11), amended by 17 D2:** APP_SUPPLIES goods lines with policy DELIVERED require a delivery reference. ORDERED goods lines may be invoiced before delivery in both modes and need none. A delivery reference that is given protects the delivery line from reversal (`ORDER.DELIVERY_INVOICED`, 6.3, 6.4). |
| Q25 | **Decided (14 section 11):** an invoice with credit notes cannot be reversed until the credit notes are reversed (`ORDER.INVOICE_HAS_CREDIT_NOTES`), and a credit note on a reversed invoice is rejected (6.4, 6.5, 7.3, 7.4). The second rule is rejected with `ORDER.INVOICE_REVERSED` (14, section 12). |
| Q26 | **Decided (17, section 5):** `gr-ir:write-off` (7.5) posts its counter line on the new account role `GR_IR_WRITE_OFF` (open-item mode NONE, 3.3), its document type is `GR_IR_WRITE_OFF`, and `gr-ir:reverse-write-off` (7.5) is the owning reversal command. Price difference and write-off stay separate in the ledger. |

## 13. ADRs raised by this spec (now in 01-adr-log, which is authoritative)

**ADR-104: Separate sales and purchase order tables.** Mirrored structure, shared address snapshot and order change log with an order kind.

**ADR-105: Confirmed orders only, with command-controlled amendments.** No drafts or quotes. Orders and lines are current rows changed only by commands, with a field-level change log and an event log.

**ADR-106: Computed order progress.** Delivered, received, invoiced and returned quantities are sums of append-only rows computed under the order lock, mirror rows for reversals.

**ADR-107: Core-built sales invoices, matched supplier invoices.** Sales invoices price from order lines and are built by the core. Supplier invoices are supplied and matched in two- or three-way mode with price and quantity tolerances.

**ADR-108: Price differences split like landed costs.** On-hand part to inventory, consumed part to COGS, expense at invoice price for non-stock items.

**ADR-109: GR/IR clearing at invoice matching.** The receipt's GR/IR credit and the invoice's GR/IR debit carry the same value and are cleared by the clearing domain in the same transaction.

**ADR-110: Dropship confirmation as virtual receipt and delivery.** Cost of goods sold against GR/IR at purchase value, no stock movement, revenue at the sales invoice.

**ADR-111: Returns valued at the original movement.** `STOCK_RETURN` documents refer to the original movement for valuation. Credits use the original invoice prices and tax codes.

**ADR-112: Atomic intercompany order flow.** An intercompany order pair, delivery, receipt and invoices are posted atomically across both companies.

**ADR-113: Tax geography on order lines.** Origin and destination default from warehouse and ship-to country, per line, overridable, and flow into invoice tax determination.

**ADR-114 to ADR-117** (raised while deciding the open questions): the fulfilment chain (5.5), invoice before receipt for stock items (7.1, 7.2), matching tolerances with supplier and item group overrides (v0.1, 3.1a), and credit limits with the exposure read API (8.1). All are in the log.

Amended at the re-baseline (14, sections 8, 9 and 10; the credit note reversal commands extend ADR-145, ADR numbers for the follow-up decisions are assigned in 01-adr-log): **ADR-116** (tolerances with overrides) and the tolerance parts of **ADR-107** are superseded by **ADR-155** (thin matching). ADR-107 is further amended by **ADR-156** (invoice mode: sales invoices are built by the core in mode CORE_BUILDS, supplied by the app in mode APP_SUPPLIES). Reservation defaults (Q8) are superseded by **ADR-157**. ADR-114 (fulfilment chain) is amended by **ADR-158** (`chain_head`). The lock order of section 9 is **ADR-147**, the reversal rules of 6.8 are **ADR-145**, the role table of 3.3 is **ADR-149**, `cost_source` in 7.1 and 7.2 is **ADR-150**, the outbox events are **ADR-159**. Line dates are **ADR-141** (raised in 16).
