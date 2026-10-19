# Supply and Demand Data for ATP, MRP and Credit Checks

Status: DRAFT v0.4 for review, propagation pass of 2026-10-13 (17, sections 4 and 5, see 9.2; v0.3: second re-baseline pass and fix pass 4 of 2026-10-13 applied (17, section 4: D4, F4 to F6, F11, F12, C5, see 9.1)). Builds on 00-design-principles v0.4, ADR-001 to ADR-174 and specs 02 to 15. Decisions come from the scope review (14, sections 8 and 9). ADR-140 to ADR-144 are raised here and added to the log at the re-baseline.
Scope: the data the core must hold so that apps can run availability-to-promise (ATP), material requirements planning (MRP), credit checks and replenishment without owning a copy of core data: a supply and demand read model, line-level dates on orders, item-partner terms, location types for in-transit and quarantined stock, and business calendars.
Out of scope: the planning logic itself (netting, lot sizing, safety stock, promising, scheduling, pricing), any app-owned plan or promise tables, forecasts.
Conventions as in 02, section 1, pattern letters as in 07, section 2, envelope and errors as in 06, reads as in 09.

## 1. Decisions taken

| Topic | Decision |
|---|---|
| Principle | The core holds the **facts** several processes read (stock, reservations, open orders, dates, supplier terms, calendars). Apps hold the **decisions** (what to promise, plan, buy) |
| Supply and demand | A snapshot read `supply-demand:query` by item, warehouse and time bucket. Nothing is stored (Q6) |
| Order dates | `requested_date` and `confirmed_date` are L2 on **sales and purchase lines**, with change log and event (Q7) |
| Item-partner terms | A versioned master table `item_partner` (Q8) |
| Locations | `warehouse_location.location_type` STANDARD, TRANSIT, QUARANTINE (Q9) |
| Calendars | Versioned `business_calendar` configuration with a working-day helper. No posting rule depends on it (Q10) |
| Events | Every change of these facts emits an outbox event (15) so apps refresh without polling the read model |

## 2. Overview

| Need | Served by |
|---|---|
| ATP: what can I promise for item X at date D | `supply-demand:query` plus `availability:query` (10) plus `confirmed_date` set back by the ATP app |
| MRP: net requirements by bucket | `supply-demand:query` (buckets, past due), `item_partner` (lead time, MOQ, multiples), `business_calendar`, `item_warehouse` parameters (10 3.7) |
| Credit check: exposure of customer | `credit-exposures:query` (11, 8.1) with `open-orders:query` and the order dates |
| Replenishment trigger | `stock.balance_changed`, `order.*` events (15) and `supply-demand:query` |

## 3. Tables and column changes

| # | Object | Pattern | Role |
|---|---|---|---|
| 1 | `item_partner` | V | Supplier and customer terms per item |
| 2 | `business_calendar`, `business_calendar_version`, `business_calendar_exception` | M, V, V | Working days |
| 3 | `company_calendar_assignment` | V | Calendar per company and optionally warehouse |
| 4 | `warehouse_location.location_type` | column | Section 6 |
| 5 | `sales_order_line`, `purchase_order_line` dates | columns | Section 5 |

### 3.1 `item_partner` (V)

A **V table** (07, section 2: versioned, insert-only, `valid_from`), not a pure master table, because an order must be able to show the terms that applied when it was placed. PK: (`tenant_id`, `item_id`, `partner_id`, `company_key`, `valid_from`) (F4).

| Column | Type | Null | Notes |
|---|---|---|---|
| company_key | ID | N | 0 = tenant row (all companies of the tenant), otherwise a company id (a company override). Part of the key, like `account_role_assignment` (07). A company row overrides a tenant row for the same item and partner. Replaces the nullable `company_id` column: a nullable column cannot be part of a key, and the sentinel 0 has no foreign key |
| relation | CODE | N | SUPPLIER or CUSTOMER |
| partner_item_no | TEXT | Y | The partner's number for the item (supplier item number or customer item number) |
| partner_item_description | TEXT | Y | |
| lead_time_days | INT | Y | Calendar days from order to receipt (SUPPLIER) or to delivery (CUSTOMER) |
| min_order_qty | QTY | Y | In the item's base unit |
| order_multiple | QTY | Y | In the item's base unit |
| origin_country | CODE | Y | FK `ref_country`. Used for customs and statistics |
| is_preferred | BOOL | N | At most one preferred supplier per item and company at a time (checked at write) |
| active | BOOL | N | A version ends when a later `valid_from` row exists or `active` is set false by a new version (an end-version row, F7 of 17: insert a row, never update) |

Rules: the item and partner must exist and the partner must have the matching role (supplier or customer). `min_order_qty` and `order_multiple` are **information for apps**. The core never rejects an order for violating them (consistent with A6 of 11: policy is not enforced). Prices are not stored here (apps). Lookup by order: `item_partners:get` returns the version valid at a date.

### 3.2 `business_calendar` (M), `business_calendar_version` (V), `business_calendar_exception` (V)

`business_calendar`: PK (`tenant_id`, `calendar_id`), `code`, `name`, `country_code` (Y), `active`.
`business_calendar_version`: PK (`tenant_id`, `calendar_id`, `valid_from`), `working_weekdays` (a seven-character mask, Monday first, for example `1111100`).
`business_calendar_exception`: PK (`tenant_id`, `calendar_id`, `exception_date`, `created_at`) (F5), `is_working_day` BOOL, `description`. A holiday is an exception with `is_working_day` = false, an extra working Saturday is true. Exceptions are insert-only. A correction is a new row for the same date with a later `created_at`: **the latest row wins**.

`company_calendar_assignment` (V): PK (`tenant_id`, `company_id`, `warehouse_key`, `valid_from`) (F6), `calendar_id`. `warehouse_key` is the warehouse id, or the sentinel **0 for the company level** (no nullable column in the key). A warehouse row overrides the company row.

Country packages may ship public-holiday data as `config-packages` (07) that create exceptions. The core does not compute holidays itself.

### 3.3 Helper

`calendar:add-working-days` and `calendar:working-days-between` (read-only operations, permission `read.config`): `{calendar_id | company_id + warehouse_id, date, days}` returns the date. Apps use it for lead times, due dates and promise dates. No posting, clearing, tax or period rule uses a calendar. Due dates on journal lines are always supplied by the caller or derived from payment terms in calendar days as before (07).

## 4. `supply-demand:query`

Read-only snapshot (09 conventions, permission `read.inventory` and `read.orders`).

| Parameter | Req | Notes |
|---|---|---|
| company_id | Req | |
| item_id | Opt list | Default: all items with activity. Maximum 500 per call |
| warehouse_id | Opt list | Default: all |
| bucket | Req | DAY, WEEK (ISO, Monday start), MONTH |
| from_date, to_date | Req | The bucket window. Maximum 366 buckets |
| min_outbox_position | Opt | Consistency token (F11, 09, 2.2). The response carries `snapshot.outbox_position`. If the serving database has not yet applied this position, the read is rejected with `READ.STALE_SNAPSHOT` (retryable). **There is no time-based `as_of`**: orders and reservations keep no time series, so the state at an earlier time cannot be rebuilt. The former `as_of` parameter is removed |
| include_status | Opt | Stock statuses counted as on hand. Default AVAILABLE |
| group_by | Opt | List of `location_type` (splits the stock part by location type) and/or `ic` (splits the order part into intercompany and other lines). Default: no extra grouping |

Result, per item and warehouse (warehouse **null** for lines without a warehouse, see below), per location type when `group_by` has `location_type`, and per `ic` value when `group_by` has `ic`:

| Field | Meaning |
|---|---|
| on_hand_by_status | Quantities by `stock_status` in the snapshot, **excluding** TRANSIT locations |
| ic | Present when `group_by` has `ic`: true for lines of intercompany order pairs (11), false otherwise. Without the grouping, intercompany lines are included in the totals and flagged in `open-orders:query` only |
| in_transit_qty | Quantity in TRANSIT locations of the warehouse (6) |
| reserved_qty | Open stock reservations (10, 4.5) |
| buckets[] | One entry per bucket plus `PAST_DUE` (date before `from_date`) and `UNDATED` |
| buckets[].sales_demand_qty | Open quantity (D4, below) of open sales order lines, dated by `confirmed_date`, else `requested_date`, else `UNDATED` |
| buckets[].purchase_supply_qty | Open quantity (D4, below) of open purchase order lines ("open to receive"), dated by `confirmed_date`, else `requested_date`, else the header `expected_receipt_date`, else `UNDATED` |
| buckets[].reserved_demand_qty | The part of `sales_demand_qty` already reserved |
| buckets[].dropship_qty | Open dropship lines are **excluded** from supply and demand (they never touch stock) and reported separately for information |

The read does **not** net, project or promise. It returns the facts per bucket. Netting, safety stock, lot sizes and promising belong to apps. Closed, cancelled and fully delivered lines are never included. Lines of intercompany order pairs are included on both sides, and flagged `ic` (the `group_by` value `ic`, above) so apps can net them.

**Lines without a warehouse (F11).** Dropship lines and service lines have no warehouse. They are reported under **warehouse null**, never attributed to a warehouse of the company: dropship lines only in `dropship_qty` (open dropship lines are excluded from supply and demand, they never touch stock), service lines with the open quantity of D4 in the sales and purchase measures of the null row. A `warehouse_id` filter excludes the null row. Stock figures (`on_hand_by_status`, `in_transit_qty`, `reserved_qty`) are empty on the null row.

**Open quantity (D4).** One definition per side, defined in 11 and referenced here, in 09 and in the credit exposure (11, 8.1): for a sales line, **open = ordered base quantity minus delivered base quantity, not below zero**. For a purchase line, **open to receive = ordered base quantity minus received base quantity, not below zero**. A quantity delivered or received above the ordered quantity (within or over the tolerance) never produces negative demand or supply, and the over-tolerance part is not counted as demand or supply. Lines that are closed or cancelled are not open lines and are excluded as a whole. The measure is computed from the order lines and fulfilment documents and is not stored (S columns of 11, Q9). 11 carries the text of this definition in its section 8 (to be added there, see 9.1).

Units are the item's base unit. The read uses the same snapshot rules as `availability:query` (10, 6.3) and the open-quantity measure of `open-orders:query` (11, 8), so the three reads always agree.

## 5. Order line dates (G4)

### 5.1 Columns

| Table | Column | Type | Null | Lock | Notes |
|---|---|---|---|---|---|
| `sales_order_line` | requested_date | DATE | Y | L2 | Existing sales field, now documented as the customer's requested delivery date. Defaults from the header |
| `sales_order_line` | confirmed_date | DATE | Y | L2 | The date promised to the customer. Null at creation. Set only by `change-line-dates` (5.2), by an ATP app or by a user with `order.promise` |
| `purchase_order_line` | requested_date | DATE | Y | L2 | Defaults from the header `expected_receipt_date` |
| `purchase_order_line` | confirmed_date | DATE | Y | L2 | Date confirmed by the supplier. Null at creation, set only by `change-line-dates` |

The header `expected_receipt_date` of purchase orders stays as the default for lines (11, 4.x). A line date overrides it.

### 5.2 Commands

`requested_date` is set on creation (`sales-orders:create`, `purchase-orders:create`, `:add-line`) and changed by `:change-line-dates`. **`confirmed_date` is rejected on creation and on `:add-line`** (F12): the creation commands map to `sales.order` / `purchase.order` only, and setting a promise needs `order.promise`, so `confirmed_date` is set only by `change-line-dates`. A create or add-line payload with a `confirmed_date` is rejected with the new code `ORDER.CONFIRMED_DATE_ON_CREATE` (to be added to 06):

| Command | Purpose | Permission |
|---|---|---|
| `sales-orders:change-line-dates`, `purchase-orders:change-line-dates` | Change `requested_date` and/or `confirmed_date` of one or more lines. `expected_version` per order, compare-and-set. A reason code is optional | `sales.order` / `purchase.order` for `requested_date`, `order.promise` for `confirmed_date` |

Rules: dates are L2, so they can change until the line is closed or fully fulfilled. A change appends to `order_change_log` (field, old, new, actor, app) and writes an `order.date_changed` outbox event with the new dates, so an ATP or MRP app reacts without polling. `confirmed_date` may be earlier or later than `requested_date`. The core stores the facts and never compares them as a rule. Posting-date and tax-point rules (11, 4.x) do not use these dates.

New permission `order.promise` lets an ATP app set `confirmed_date` without the right to change order content. Error codes `ORDER.LINE_CLOSED` (existing) and `ORDER.DATE_INVALID` (a date outside 1900 to 2200).

## 6. Location types (G6)

### 6.1 Column

`warehouse_location.location_type` CODE, N, L1 (cannot change once any stock movement refers to the location): `STANDARD`, `TRANSIT`, `QUARANTINE`. Stock without a location is stock of the warehouse and counts as STANDARD (10, 3.6).

### 6.2 Rules

| Type | Rule |
|---|---|
| STANDARD | As today |
| TRANSIT | Stock here is **not available**: excluded from `availability:query` and `on_hand_by_status`, reported as `in_transit_qty`. Not reservable and not deliverable by sales deliveries. It is valued normally (it is the company's inventory) and stays in the item balance and the inventory account |
| QUARANTINE | Not reservable and not deliverable. Accepts only stock status QUARANTINE or BLOCKED (see Q2) |

**Movement rules (C5, aligned with 10, 3.6).** TRANSIT and QUARANTINE locations **accept** the movement types `TRANSFER_IN`, `STATUS_CHANGE` (`STATUS_IN`) and `ADJUSTMENT` (`ADJUST_IN`, `ADJUST_OUT`), and **reject** `RECEIPT` and `ISSUE` (`INVENTORY.LOCATION_NOT_AVAILABLE`). Stock therefore enters these locations by transfer, status change or adjustment, never by a receipt or issue. In a QUARANTINE location the resulting stock status must be QUARANTINE or BLOCKED (`INVENTORY.LOCATION_STATUS_MISMATCH`). The movement type names are those of 10 (`TRANSFER_IN`, `STATUS_IN` and `STATUS_OUT`, `ADJUST_IN` and `ADJUST_OUT`).

A two-step transfer is modelled with existing commands: transfer to a TRANSIT location of the receiving warehouse (or a transit warehouse), then transfer from there into the target location when the goods arrive. Each step is the existing `STOCK_TRANSFER` document (two movements, no value effect), with the stock chain and atomic coupling unchanged. The core adds no separate in-transit document or process. Apps decide when a transfer is "received".

Error codes: `INVENTORY.LOCATION_NOT_AVAILABLE` (reserve, deliver, receive or issue on a TRANSIT or QUARANTINE location), `INVENTORY.LOCATION_STATUS_MISMATCH` (non-quarantine status into a QUARANTINE location), `MASTER.LOCATION_TYPE_LOCKED`.

## 7. Credit checks and other readers

No new table is needed for credit checks. The data set a credit check reads is complete after this spec:

| Data | Source |
|---|---|
| Limit | `partner_company_relation.credit_limit`, `business_partner.credit_limit` (11, 8.1) |
| AR exposure | Open items of AR control accounts (`open-items:query`, 09) |
| Delivered, not invoiced | `uninvoiced:query` (11, 8) |
| Open orders | `open-orders:query` (11, 8) with `confirmed_date` and `requested_date` |
| Payment behaviour | Aging and open items (09) |
| Events to react to | `order.*`, `delivery.posted`, `invoice.posted`, `open_item.*` (15) |

The decision to hold or release is an app decision and is stored by the app (A4 of 11). If an app wants to show the hold on the order, it uses an extension attribute (15).

## 8. Permissions

| Permission | Allows |
|---|---|
| `read.inventory` + `read.orders` | `supply-demand:query` |
| `order.promise` | Set `confirmed_date` |
| `write.master` | Create item-partner versions (`item_partner` is in the `write.master` resource list of 07, 11.1). There is no separate permission `master.item_partner` |
| `write.config` | Calendars, exceptions and assignments |
| `read.config` | Calendar helper operations |

## 9. Deltas to earlier specs

Deltas (second pass): 06, 07, 09, 10, 11 and 13 now carry the changes listed below. The fix pass 4 deltas are in 9.1.

| Spec | Change |
|---|---|
| 07 | Table overview: `business_calendar`, `business_calendar_version`, `business_calendar_exception`, `company_calendar_assignment`, `item_partner`. Config package content may include holidays |
| 09 | `supply-demand:query` and the calendar helpers join the read catalogue |
| 10 | `warehouse_location.location_type` (3.6). Availability excludes TRANSIT and QUARANTINE (6.3, 4.5). Reservations reject those locations. `item_warehouse.min_quantity` and `reorder_quantity` stay (14, S7 unchanged) |
| 11 | Line dates on purchase and sales lines, command `change-line-dates`, `open-orders:query` returns both dates, header `expected_receipt_date` becomes the default. `order_change_log` fields extended. Permission `order.promise` |
| 06 | Permission `order.promise`, error codes of 5.2 and 6.2, the date-change commands in the catalogue |
| 15 | Event type `order.date_changed` (already listed). `extension_definition` entity types include `ITEM_PARTNER` (added to the list of extendable types) |
| 13 | Migrated open orders carry `requested_date` and `confirmed_date` when known. Open-order staging records (13, 5) get the two fields. In-transit stock is migrated as `STOCK_OPENING` lines into a TRANSIT location |

### 9.1 Deltas of fix pass 4 (17, section 4)

| Ref | Change in this spec | Effect on other specs |
|---|---|---|
| D4 | Open quantity: ordered minus delivered (received), not below zero, over-tolerance not counted, one definition per side, purchase "open to receive" (4) | 11, section 8: carries the definition (not yet there). 09 refers to it |
| F11 | Warehouse null for dropship and service lines, `group_by` (`location_type`, `ic`), `ic` flag in the result, time `as_of` replaced by `min_outbox_position` (4) | 09, 4.11 and 2.2 are aligned |
| C5 | TRANSIT and QUARANTINE accept `TRANSFER_IN`, `STATUS_CHANGE`, `ADJUSTMENT`, reject `RECEIPT` and `ISSUE` (6.2) | 10 already says the same |
| F12 | `confirmed_date` rejected on create and add-line, set only by `change-line-dates` (5), new code `ORDER.CONFIRMED_DATE_ON_CREATE` | 06, 11 |
| F4 to F6 | `item_partner` PK with `company_key` (3.1). `business_calendar_exception` PK with `created_at`, latest wins (3.2). `company_calendar_assignment` PK with `warehouse_key`, 0 = company level (3.2) | 07 (table specs of the calendar tables, if any), 15, 4.1 key |
| Wording | `item_partner` is a V table (3.1, Q3). Permission `write.master`, no separate `master.item_partner` (8) | 15, 4.1 |

### 9.2 Deltas of the propagation pass v0.4 (17, sections 4 and 5)

| Ref | Change in this spec | Effect on other specs |
|---|---|---|
| Propagation pass | Checked 6.2 against 10, 3.6: TRANSIT and QUARANTINE accept `TRANSFER_IN`, `STATUS_CHANGE`, `ADJUSTMENT` and reject `RECEIPT` and `ISSUE`, no change needed. `item_partner` PK (`tenant_id`, `item_id`, `partner_id`, `company_key`, `valid_from`) confirmed (3.1). Extension values on `item_partner` attach to the version row key (15, 4.1) | 15, 4.1 (aligned) |

## 10. Assumptions

| Ref | Item |
|---|---|
| A1 | The core provides facts per bucket. Netting, safety stock and promising are app logic |
| A2 | `min_order_qty`, `order_multiple` and `lead_time_days` are information and never enforced |
| A3 | Lead time is in calendar days, an app converts with the working-day helper |
| A4 | In-transit stock is valued in the item balance and the inventory account like any other stock |

## 11. Open questions

| # | Question |
|---|---|
| Q1 | **Decided:** totals only. `open-orders:query` gives the lines behind the buckets. |
| Q2 | **Decided:** QUARANTINE stays as a location guard that accepts only stock status QUARANTINE or BLOCKED. |
| Q3 | **Decided:** `item_partner` is a V table (versioned master data, 3.1), extendable by apps (15, 4.1). |

## 12. ADRs raised by this spec (to be added to 01-adr-log)

- **ADR-140: Supply and demand as a snapshot read.** `supply-demand:query` returns facts per item, warehouse and bucket. No stored plan, no netting, no promising in the core.
- **ADR-141: Line-level dates on orders.** `requested_date` and `confirmed_date` are L2 on sales and purchase lines, changed by `change-line-dates` with log and event. `confirmed_date` has its own permission.
- **ADR-142: `item_partner` terms.** Versioned master table for supplier and customer item terms. Information only, never enforced.
- **ADR-143: Location types.** STANDARD, TRANSIT, QUARANTINE. In-transit is modelled with existing transfers and TRANSIT locations. TRANSIT stock is valued but not available.
- **ADR-144: Business calendars.** Versioned working-day calendars with exceptions and a read helper. No core posting rule depends on them.
