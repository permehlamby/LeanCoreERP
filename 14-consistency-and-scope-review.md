# Review: Consistency and Scope of the Core

Status: REVIEW for decision. Nothing in specs 00 to 13 or the ADR log has been changed by this review. Findings come from four parallel read-only checks (specs 02 to 05, 06 to 09, 10 to 13, ADR log with principles and ERD) plus my own scope analysis. Items marked **V** I verified against the files myself. Items marked **R** are reported by a check and not yet verified, I verify them when I fix them.

Purpose of the core (restated): a strong core of **data and transactions that several processes use** (credit checks, ATP checks, MRP runs and so on) and that must stay consistent and provable. Process-specific data, policy and logic is built in apps on top.

## 1. Summary

| Area | Result |
|---|---|
| Numbering and ADR references | Clean. ADR-001 to ADR-136 are unique and ascending, every referenced ADR exists |
| Core mechanics (chains, clearing model, periods, numbering) | Coherent in 02 to 05 |
| Back-porting of later "deltas" | **Incomplete.** Many deltas claimed "applied" are not (section 3) |
| Enumerations (chain types, document types, error areas, permissions, entity types, audit sources) | Widely stale in 02, 04, 06, 07, 09 |
| Real design defects | 10, listed in section 2 |
| Scope against the stated purpose | The core is mostly right, with process policy that crept in, and missing cross-process capabilities. Section 5 |

## 2. Real design defects (need a decision or a rule)

| ID | Defect | Where | Status |
|---|---|---|---|
| D1 | **Reversal rules are incomplete.** `reversal_of_document_id` is allowed only for REVERSAL and REVAL_REVERSAL, but clearing resets, stock reversals, VAT settlement reversal, rerun reversals and the year-end reopen also mirror documents and need it, with a reason code. The reversal of a YEAR_END document would be included in the closed year's P&L and picked up by a re-close. `journal-documents:reverse` lists only five non-reversible types, so derived journals (STOCK_*, DROPSHIP_COST, VAT_SETTLE, HISTORY_SUMMARY, OPENING*, order-built invoices) can be reversed directly, which breaks the atomic stock and order coupling | 02:83, 02:65, 05:213, 03:167, 06:238-242, 08:278, 10:277, 11:323, 11:406, 13:207 | **V** (02:83) and R |
| D2 | **Trigger and grant rules contradict S columns.** 02 says triggers reject any change in a hard-closed period and any change without a `change_log` row, but clearing, reversal status and the other S updates are legitimately made in hard-closed periods and are not logged in `change_log` (ADR-037). `retention_until` is L1, yet `retention:extend` updates it | 02:369, 02:266, 03:136, 03:144, 12:82-84, 12:357 | **V** (02:369) and R |
| D3 | **Lock order is not authoritative.** 04 6.3 lists three of the six chain heads, `fulfilment_chain_head` and `vat_return_chain_head` have no position, `business_partner.retention_until` (S) is written by every posting but has no lock position and becomes a hot row, GR/IR open items must be pre-locked at receipt and invoice time. 10 P7 says "stock chain head, then finance locks" while 04 says after the ledger heads. Derivation (balances, tolerance, progress) runs before the P7 lock, while 10 and 11 claim it is computed under the lock | 04:218-228, 10:334, 10:344, 11:245, 11:285, 11:450, 12:86, 06:117 | **V** (04) and R |
| D4 | **Revaluation stale check has a hole, and the old rule is still in 04.** 04:252 still says "no pending system job, e.g. a revaluation whose auto-reversal is not yet posted", while ADR-051/053 replaced it. Postings into special periods 13+ dated in period 12's range come after period 12's hard-close check and can make the active run stale with no later check. `year-end:close` hard-closes the closing period and the year but appends only YEAR_END_CLOSE, while 04's own trigger requires a matching PERIOD_HARD_CLOSE event | 04:252, 05:159-161, 05:183, 05:190, 04:118 | **V** (04:252, 05:190) and R |
| D5 | **Account role rules are inconsistent.** 10 says "GR_IR must be GL_OPEN_ITEM, others NONE", but AR_CONTROL and AP_CONTROL must be CONTROL and DROPSHIP_CLEARING must be GL_OPEN_ITEM. The unique key of `account_role_assignment` omits the VAT registration although `vat_registration_id` is declared part of the key | 10:145, 11:96-100, 11:241, 07:206 | **V** (07:206) and R |
| D6 | **"Costs are always computed" and "caller sends TC only" have unlisted exceptions.** Listed: returns and stock opening. Not listed: supplied value on adjustments (10:262), receipt valued at the earlier invoice value (ADR-115), COST_ADJUST from invoice price differences, migration supplying all four currencies | ADR-096, ADR-057, 10 Q5, 10:262, 11:358, 11:392 | R |
| D7 | **`read_access_log` does not fit the export logging of 12.** 09 has `partner_id` NOT NULL, one row per partner, no job reference or partner count, and no field groups for credit limits, search or order addresses | 09:263-279, 12:127, 12:237, 12:396 | R |
| D8 | **Migration (13) has logic gaps after later changes.** Commit order and retained earnings sequencing: the opening line for HISTORY_RESULT is "target minus current", which is zero if opening GL documents post before history summaries, and the history closing entries are created only at the seal, after the check. The two GR/IR reconciliation checks conflict. The "controlled exceptions apply only to documents of an OPEN batch" wording ignores COMMITTING and COMMITTED. "Posts atomically" is stale after the chunked commit | 13:32, 13:196, 13:221, 13:256, 13:277-279, 13:285, 13:296-299 | R |
| D9 | **VAT and rate rules contradict between specs.** VAT settlement reversal "rejected after filing unless the filing is reversed", while Q5 and ADR-080 say only before filing and no command reverses a filing. `exchange_rate.first_used_at` is "set by posting, revaluation and clearing", while 03 says no rate lookup happens at clearing | 08:278, 08:313, 01:442, 07:285, 03:210 | R |
| D10 | **No write permissions exist for configuration and master data.** 06 section 8 has only `read.config`. `config-packages:apply`, `companies:activate`, `{resource}:create/update/deactivate`, the batch variants and `fiscal-years:open-history` have no permission. VAT permissions `vat.prepare`, `vat.file`, `vat.settle` and the error area `VAT` are not in 06's tables although 08 says they were added | 06:376-380, 06:71-96, 07:520, 08:331 | R |

## 3. Deltas claimed as applied but missing or inconsistent

| Target | Missing or inconsistent |
|---|---|
| 02 | `STOCK_OPENING` and `STOCK_STATUS_CHANGE` in the document type list. `IC` type undefined versus how IC documents are actually typed. `is_system_generated` list incomplete. `TAX_EVIDENCE` in `change_log.entity_type`. `journal_line_tax_evidence` missing from the table overview and grants. New L1 open-item columns (`legacy_*`) not in the line hash definition. Exemptions from the partner and open-item rules for REVAL_ADJUSTMENT, CLOSING, HISTORY_PL and OPENING lines not stated in 5A. Rate and rate_type values for TC=0 system lines undefined |
| 04 | Series assignment document types still "as in `journal_document`", but stock, order and migration types are assigned too. `is_legal` text ("all journal documents") contradicts non-legal series. `fiscal-years:open-history` has no row. Hard-close rule (D4). `year-end:reopen` condition "no later year closed" missing. Single authorisation text in 7.2 and A5 not updated for two soft-close permissions |
| 06 | Error areas and codes (VAT, MIGRATION codes that no spec defines, INVENTORY codes that no spec uses and codes that are used and unlisted). Permission table (VAT, config, migration, IC delivery and invoice commands). `command_log_result.entity_type` list. Reversible-type list (D1). Q3 "four warnings" no longer true. Q11 batch limit stated "proposed" while ADR-062 treats 500 as settled. `command_log` correlation index |
| 07 | Table overview lacks `company_vat_registration`, `company_order_setting_version`, `purchase_tolerance_override`, `external_code_mapping`, `legacy_id_map` and the inventory master tables. Readiness checks (account determination catch-all, retention rules, migration roles, VAT registration) not added. `tax_class` default from the item not applied. `legacy_ref` and `upsert` only on the batch variant. `CONFIG.COST_SCOPE_CONFLICT` code missing, no membership rule in 5.6. Section 11.5 before 11.4. `master_change_log` correlation index. Page of `ref_country_eu_membership` missing in the ERD |
| 09 | Permissions `read.inventory`, `read.orders` in 2.3. `chains:status` chain types (four listed, six exist) and the duplicate `stock-chains:status`. Audit-event sources for REACTIVATE, ANONYMISE, export download, `retention:extend`, tenant deletion cancel and execute. Resources `command-log`, `master-change-log`, `applied-config-package`. `migration_batch_id` filter. Index references point at the wrong spec |
| 11 | `legacy_order_ref` and `legacy_line_ref` columns on orders. `origin` and `migration_batch_id` on deliveries, receipts and dropship confirmations, and `gr_ir_journal_line` in the receipt line column list. IC commands missing from the catalogue and permissions. Restriction (MASTER.PARTNER_RESTRICTED) not in any pipeline. Section 5.5 before 5.4 |
| 12 | Rehearsal tenant variant of the deletion workflow (13 Q10). Migration batches in the audit package. `RETENTION.CANNOT_SHORTEN` not in the code list. Inconsistent retention rule for migrated records (13 delta versus 13 Q15) |
| 13 | Setting names (`migration_commit_max_documents` versus `_chunk_documents`, undefined max lines per document), stale "atomic" wording, delta rows toward 08 and 12 |
| 08 | `source` = LEGACY wording without a column, "fifth chain" wording |

## 4. Stale enumerations, text and naming (mechanical)

| Topic | Fix |
|---|---|
| **Chain types.** Three chains named in ADR-006, ADR-046, P5, 02 9.5, 05:191, 09 `chains:status`. Six exist (LEDGER, CLEARING, PERIOD_EVENT, VAT_RETURN, STOCK, FULFILMENT) | Say "all chains of the company" and list the six once |
| **Command names.** 02 to 05 and ADRs: `journal:post`, `clearing:post`, `fiscal-year:open`, `period:*`, `anchor:create`, `chain:verify`. 06 and later: `journal-documents:post`, `clearings:post`, `fiscal-years:open`, `periods:*`, `anchors:create`, `chains:verify`. 04 uses both | Normalise to the 06 names everywhere |
| **ADR text not updated** when later ADRs amended it: ADR-013 (open item `:validate` resolved, orders), ADR-019 (rate types now SPOT, CLOSING, VAT, DERIVED), ADR-021 (rerun in soft-closed), ADR-024 and ADR-049 and ADR-054 ("no opening documents" versus migration OPENING documents), ADR-028 consequences (`vat:settle`), ADR-039 (no hidden side effects versus GR/IR clearing reset), ADR-057, ADR-094 ("item rows first" versus order rows first), ADR-096, ADR-122 (refers to ADR-092, should be ADR-088), ADR-100 and ADR-103 (duplicate expired-lot rule). **ADR-035 is still PROPOSED** although everything depends on it | Add amended notes, decide ADR-035 |
| **Principles 00** (v0.3 status line, ADR ranges, open items, P1 "caller cannot bypass", P5 chains, P7 exceptions, P8 snapshots, P11 dimension types, "no hard delete once referenced", layer table "append-only", release scoping "domestic" and "inventory later") | Refresh to v0.4 |
| **P7 exception list.** ADR-069 and P7 name currencies, countries, EU membership and the tenant registry. `ref_unit` and `tenant_deletion_certificate` are also outside tenant keys | Add them |
| **Type and cross-reference slips** (section references, "P4" colliding with pipeline phase P4, `INT` versus `SMALLINT` for period numbers, table numbering gaps, "Q7" versus "Q6" references, specs 10 and 11 saying compliance is parked) | Fix |
| **ERD.** `ref_country_eu_membership` missing. `journal_document` box omits six L1 columns. Real global tables drawn as stubs | Add |

## 5. Scope review against the purpose of the core

### 5.1 The test

An element belongs in the core if at least one of these is true:
1. **Integrity or legal evidence:** immutability, chains, numbering, periods, balance, retention, tenant isolation, audit.
2. **Shared by several processes:** master data, configuration, stock, open items, orders (as demand and supply), reservations, exposure.
3. **Atomic across domains:** stock plus finance, invoice plus clearing, intercompany.
4. **Statutory derivation** that needs 1 to 3: VAT returns, audit and tenant exports.

It belongs in an app if it is workflow, pricing, planning, decision policy, document rendering, an integration format or case management that differs per customer.

### 5.2 What the core already does right

Ledger, clearing, periods, numbering, revaluation, year-end, VAT data, stock ledger and valuation, orders, fulfilment, GR/IR, reservations, lots, credit limit data and the exposure read, retention and anonymisation, tenant export and deletion, migration reconciliation. These are shared data or integrity machinery. Drafts, stocktake sessions, pricing, quotes and approvals are already in apps, which is the right line.

### 5.3 Process policy that crept into the core (candidates to thin or move)

| # | Item | Why it is process-specific | Proposal |
|---|---|---|---|
| S1 | Supplier invoice **matching policy**: `company_order_setting_version` (tolerances, default match mode, reservation default, invoice-before-receipt switch), `purchase_tolerance_override`, `purchase.match_override` | Tolerances and acceptance are per-customer policy and workflow (park, approve, override) | Core keeps hard integrity: references valid, quantity not above ordered plus the per-line tolerance (data), gross control total, GR/IR clearing and price-difference split. Apps decide whether a price deviation is acceptable and send the invoice. The core records the deviation on the match record. Removes two tables and one permission |
| S2 | **Invoice building** for sales (`sales-invoices:create`, `invoice_policy`, "open to invoice") | Selecting what to bill, pricing and consolidation are billing-run logic | One invoice posting command for sales and purchase: apps send complete lines with order and delivery references, the core validates references and limits, determines tax and posts. Symmetric with supplier invoices. (You decided "AR built by the core", this is a proposal to revisit) |
| S3 | `reservation_mode` (BEST_EFFORT and so on) and auto-reservation at order creation | Availability promising is ATP logic | Keep reservation records and commands as shared data. Apps reserve explicitly after order placement |
| S4 | **Landed cost allocation** tables and allocation methods (value, quantity, weight, volume) | Allocation across receipts is process logic | Core keeps the valuation primitive: a cost adjustment per receipt line with an explicit amount and the on-hand versus consumed split (valuation depends on cost state). Allocation across receipts moves to apps. Removes two tables |
| S5 | **Data subject request** case table with statuses and deadlines | Privacy case management is workflow | Core keeps the restriction flag, the anonymise command, the export and the evidence of decisions as an audit event. Request tracking moves to apps (or becomes an optional table) |
| S6 | **Open item payment attributes**: `discount_due_date`, `discount_amount_tc`, `dunning_block`, payment run flags | Payment run and dunning processes | Keep `payment_block` and `bank_account_id` and `payment_reference` (several processes). Move discount and dunning fields to extension attributes (5.4) |
| S7 | `item_warehouse.min_quantity`, `reorder_quantity` | Replenishment parameters | Move to apps (MRP) or extension attributes |
| S8 | SAF-T and e-invoice **dataset assembly** as core job types | Output formats for filing and exchange | Optional slimming: keep the audit package and tenant export (evidence) and the data and mappings, and let apps assemble national formats from read APIs. You chose these in core, so this is a trade-off to confirm |

The measure of success of these moves: fewer tables, fewer permissions, fewer decision rules in the core, with the same integrity guarantees.

### 5.4 Cross-process capabilities the core lacks

| # | Gap | Who needs it | Proposal |
|---|---|---|---|
| G1 | **Extension attributes.** No governed way for apps to attach process data to core records without schema changes | Every app | Namespaced, typed extension definitions per entity type, values stored in one generic table keyed by (entity type, id, namespace, key), changeable by command with audit, outside every hash, never used by core rules. App-owned tables with core ids stay the default for larger structures |
| G2 | **Change feed (event outbox).** Apps can only poll reads. No reliable way to react to postings, stock moves, order changes, partner or config changes | ATP caches, MRP triggers, credit holds, integrations | An `outbox_event` table written in the same transaction as the change, with a monotonic position per tenant, event type, entity reference and compact payload. Read API `events:list since position`, consumer offsets owned by apps. At-least-once, ordered per entity. Transport (webhooks, queues) stays outside |
| G3 | **Supply and demand read model.** Today `availability:query` gives current available quantity only | ATP, MRP | `supply-demand:query` by item, warehouse and time bucket: on hand by status, reserved, open sales demand, open purchase supply, in-transit, with dates |
| G4 | **Dates on orders.** Sales lines have a requested date, purchase has only a header-level expected receipt date, no confirmed or promised date on either side | ATP, MRP, credit check timing | Line-level `requested_date`, `confirmed_date` (L2) on both order sides |
| G5 | **Item-supplier master data** (supplier item number, lead time, minimum order quantity, order multiple, country of origin) | Purchasing, MRP, ATP, receiving | A small `item_partner` master table. Pricing stays in apps |
| G6 | **In-transit stock.** Transfers are immediate (two movements in one document) | ATP, MRP, logistics | A `location_type` (STANDARD, TRANSIT, QUARANTINE) so apps model two-step transfers with existing movements |
| G7 | **Business calendars** (working days) | ATP, MRP, due dates, dunning | Optional `business_calendar` configuration. Decide whether core or apps |
| G8 | **App identity and scope.** Authorisation is outside the core by design, but extension namespaces and the change feed need an app registry | G1, G2 | An `app_registration` config table (app id, namespaces, allowed event types). Permissions stay with the identity provider |

### 5.5 Structural consolidations (shrink the surface)

| # | Consolidation | Effect |
|---|---|---|
| K1 | **One `chain_head` table** keyed (tenant, company, chain type, scope) instead of six head tables, one verification routine, one lock-order rule by chain type | Removes five tables, fixes D3 structurally |
| K2 | **One `entity_change_log`** replacing `change_log`, `master_change_log` and `order_change_log` (superset of columns: entity type and key, field, old and new value, is_pii, lock class, period status, reason) | Removes two tables |
| K3 | **Outbox and audit events**: let `outbox_event` be the single append-only stream behind `audit-events:query` and the app change feed | Removes the union view logic, one source |

Estimated net effect: about 12 tables fewer (S1, S4, S5, K1, K2) and about 5 added (extension definition and values, outbox, app registration, item_partner, optional calendar), with fewer rules.

## 6. Proposed work plan

| Phase | Content | Needs your decision |
|---|---|---|
| A. Mechanical fixes | Section 3 deltas, section 4 enumerations, names, ADR amended notes, principles v0.4, ERD corrections. Verify each R item while fixing | No. I apply and report |
| B. Defect rulings | D1 to D10 | Yes, a short question round |
| C. Scope decisions | S1 to S8 and G1 to G8, K1 to K3 | Yes, the main decisions of this review |
| D. Re-baseline | Update specs, ADR log, principles and ERD for phases B and C | After the decisions |

## 7. Decisions needed from you

1. Do you adopt the **test** of 5.1 as the governing rule for what belongs in the core?
2. **Phase A** (mechanical fixes): may I apply them now? They change wording, enumerations and names but no design decision.
3. **Phase B**: D1 to D10, each has a proposed ruling I can put to you as questions.
4. **Phase C**: which of S1 to S8 do you want to thin or move (S1, S2, S4 and S5 are the largest), which of G1 to G8 do you want added (G1, G2 and G3 are the most important for apps), and do you want K1 to K3?

## 8. Decisions taken (2026-10-13)

| Ref | Decision |
|---|---|
| 5.1 test | **Not adopted** as a fixed rule. Each item judged on its own |
| Phase A | Apply mechanical fixes **after** phases B and C, in one re-baseline |
| D1 | Derived documents (STOCK_*, DROPSHIP_COST, VAT_SETTLE, HISTORY_SUMMARY, OPENING*, order-built invoices, clearing reset, YEAR_END) reversible only through the owning command. `journal-documents:reverse` rejects them. `reversal_of_document_id` allowed for all mirror documents with reason code. YEAR_END only via `year-end:reopen` |
| D2 | Trigger rules per column class: L1 and L2 rejected in hard-closed periods and require `change_log`. S columns changed by system commands without `change_log`, each with its own chained event. `retention_until` reclassified to S |
| D3 | One authoritative lock order: order, stock (`item_company`), chain heads sorted by chain type (LEDGER, CLEARING, PERIOD_EVENT, VAT_RETURN, STOCK, FULFILMENT), open items sorted by id. Derivation runs after the locks. `retention_until` updated by batch job, not per posting |
| D4 | Stale check on every posting in scope including special periods 13+. ADR-051/053 rule replaces 04:252. `year-end:close` writes PERIOD_HARD_CLOSE for closing period and year in addition to YEAR_END_CLOSE |
| D5 | Role table in 07 with required open-item mode per role (GR_IR, DROPSHIP_CLEARING = GL_OPEN_ITEM, AR/AP_CONTROL = CONTROL, others NONE). Unique key of `account_role_assignment` extended with nullable `vat_registration_id` |
| D6 | Closed list of cost exceptions in ADR-096, marked on the document with `cost_source` (COMPUTED, SUPPLIED, INVOICE, MIGRATION) |
| D7 | `read_access_log` extended: `partner_id` nullable, `export_job_id`, `partner_count`, wider field-group list. Exports log one row per job |
| D8 | Migration chunk order: HISTORY (incl. closing entries), OPENING_GL, OPEN_ITEMS, STOCK, ORDERS, FULFILMENT, RESERVATIONS, LEGACY_TAX. One GR/IR check against TB. Exceptions apply to OPEN, COMMITTING, COMMITTED until seal. "Atomic per chunk" |
| D9 | VAT settlement reversal only before filing (ADR-080). Clearing does not touch `first_used_at` |
| D10 | Fixed permissions per area: `write.config`, `write.master`, `config.apply`, `company.activate`, `history.open`, `vat.prepare/file/settle`, `migration.*`. Full table in 06 |
| S1 | Thin to line tolerance. Core enforces references, quantity <= ordered + line tolerance, control total, GR/IR split. Core records price deviation. Remove `purchase_tolerance_override` and `purchase.match_override` |
| S2 | Per-tenant setting, versioned configuration with valid_from: CORE_BUILDS (core selects lines by `invoice_policy`) or APP_SUPPLIES (apps send complete lines with order and delivery references). Same validation, tax determination and posting |
| S3 | Keep reservation records. Auto-reservation becomes an optional tenant setting, default off |
| S4, S5, S6, S7, S8 | Unchanged |
| G1 | Add extension attributes (namespaced, typed definitions, generic value table, audited, outside hashes, never used by core rules) |
| G2 | Add `outbox_event` change feed |
| G3 | Add `supply-demand:query` read model |
| G4 | Add `requested_date`, `confirmed_date` (L2) on sales and purchase order lines |
| G5 | Add `item_partner` master table |
| G6 | Add `location_type` (STANDARD, TRANSIT, QUARANTINE) |
| G7 | Add `business_calendar` configuration |
| G8 | Add `app_registration` configuration |
| K1 | One `chain_head` table replaces six head tables |
| K2 | Not adopted, three change logs stay separate |
| K3 | `outbox_event` is the single append-only stream behind `audit-events:query` and the app feed |

## 9. Detail decisions for the new and changed parts (2026-10-13)

| Ref | Decision |
|---|---|
| Q1 G1 | Extension attributes allowed on master data, orders and open items. Not on posted journal or stock documents |
| Q2 G1 | Typed definitions (TEXT, DECIMAL, DATE, BOOLEAN, REF), one value column per type, core validates type and allowed values, filterable in read API |
| Q3 G2 | Outbox event holds event type, entity type, id, version or ledger_seq, time, company, correlation_id. No PII. Apps read data through the read API |
| Q4 G2 | Monotonic position per tenant. Audit events kept per retention rules, pure app events purged after configured days once all registered consumers have read past |
| Q5 G8 | `app_registration`: app_id, extension namespaces (only owner writes, all read), allowed event types, consumer offset stored in the core. Authentication stays with the identity provider |
| Q6 G3 | `supply-demand:query` by item x warehouse x DAY/WEEK/MONTH buckets. Sources: stock by status, reservations, open sales lines (confirmed_date else requested_date), open purchase lines, TRANSIT stock. Snapshot read, nothing stored |
| Q7 G4 | `requested_date` and `confirmed_date` are L2 on sales and purchase lines, changed with compare-and-set, logged in `order_change_log`, emit outbox event. New permission for setting confirmed_date |
| Q8 G5 | `item_partner` versioned master, key (item, partner, valid_from): partner_item_no, lead_time_days, min_order_qty, order_multiple, origin_country, is_preferred. Used for suppliers and customers, no prices |
| Q9 G6 | `location_type` STANDARD, TRANSIT, QUARANTINE, L1 and fixed after first movement. TRANSIT is not available stock and is shown as in transit. QUARANTINE not reservable or sellable |
| Q10 G7 | Versioned `business_calendar` per country or company (weekdays, exception dates), assignable to company and warehouse. Helper `calendar:add-working-days`. No posting validation depends on it |
| Q11 S2 | One command `sales-invoices:create`. Mode (CORE_BUILDS or APP_SUPPLIES) is versioned config per company with valid_from, switch affects new invoices only |
| Q12 S3 | Auto-reservation is a company setting in `company_order_setting_version`, default off, BEST_EFFORT only. Stricter modes removed |
| Q13 S1 | Price deviation always posted as price-difference line (account determination) beside the GR/IR clearing, recorded on the match record. Quantity limited by order + line tolerance (percent, L2) |
| Q14 K1 | `chain_head` key (tenant, company, chain_type, scope_key). scope_key = ledger_id for LEDGER, return type for VAT_RETURN, empty otherwise. Columns last_seq, last_hash, hash_version, updated_at. Event tables stay. Lock order sorted by chain_type then scope_key |
| Q15 K3 | `outbox_event.is_audit`. Audit events (config, master, DSR, retention, export, tenant, access) retained per retention. Chained events referenced, not duplicated. `audit-events:query` reads the outbox |
| Q16 | Two new specs: 15 (extensions, outbox, app registration), 16 (inventory and order extensions G3 to G7) |

## 10. Decisions from the re-baseline pass (2026-10-13)

| Ref | Decision |
|---|---|
| Q15 (15) | `extensions:set` on OPEN_ITEM allowed in hard-closed periods. Events filtered at read time only. 50 definitions per entity type and namespace (setting) |
| Q (16) | Supply-demand returns totals only. QUARANTINE location type stays as guard accepting only status QUARANTINE or BLOCKED. `item_partner` pure master table, extendable |
| D4 | Stale check runs at hard-close of every relevant period (incl. special 13+ and the closing period). Stale is fixed by `periods:reopen` of period 12 and a rerun. `year-end:close` writes PERIOD_HARD_CLOSE for the closing period plus YEAR_END_CLOSE at year level (`period_no` null) |
| D1 follow-up | New owning commands `sales-credit-notes:reverse`, `purchase-credit-notes:reverse`, `landed-costs:reverse`, `vat-settlements:reverse` (before filing only). Reason codes AUTO_REVERSAL, RERUN, REOPEN, CORRECTION, ERROR, RETURN. Mirror documents keep the `document_type` of the original. `IC` is not a separate document type in 06 (IC documents are AR/AP_INVOICE with `ic_transaction_id`) |
| K1 / 08 | VAT_RETURN chain is one per company over all return types (`chain_head` scope_key empty). `chain_anchor` uses `scope_key` in 06, 10, 11 |
| D3 | Number counters lock before chain heads. A clearing that can produce an exchange difference pre-locks the LEDGER head and the counter |
| D10 | Config/master permission split as in 07 11.1. IC permissions `ic.order`, `ic.deliver`, `ic.invoice`. `outbox:verify` needs `read.audit` |
| S1 | `match_mode` and `allow_invoice_before_receipt` stay in `company_order_setting_version` |
| S2 | APP_SUPPLIES: app sends price and discount per line, stored beside the order price with no core price rule. ORDERED service lines require an order line reference instead of a delivery reference. New companies default to CORE_BUILDS |
| D6 | `cost_source` per movement line. Returns COMPUTED, partly invoice-covered receipt split into an INVOICE line and a COMPUTED line |
| 13 Q16 | `migration-batches:seal` runs a migration variant of year close for history years (writes PERIOD_HARD_CLOSE and YEAR_END_CLOSE for the already posted closing entries). `year-end:close` cannot be used on history years |
| 13 Q17 | Correction of a migration document between commit and seal is by rollback and recommit |
| 09 Q13 | Add outbox event types `ledger.line_changed`, `revaluation.run_completed`, `revaluation.rerun`, `dsr.received`, `dsr.decided`, `invoice.issued`, `master.reactivated` (all audit, no PII in summary) |
| 12/09 | Anonymisation job recomputes partner `retention_until` before acting. Export log one row per job and field group, never per partner. Field groups PII_EXTENSION, ORDER_ADDRESS, PII_CREDIT, SEARCH approved |

## 11. Decisions from pass 2 (2026-10-13, all proposals approved)

| Ref | Decision |
|---|---|
| Mirror types | The four existing mirror types stay: REVERSAL, REVAL_REVERSAL, CLEARING_RESET, STOCK_REVERSAL. "Mirror keeps original type" applies to types without own mirror type (YEAR_END, VAT_SETTLE, DROPSHIP_COST, order-built invoices and credit notes). All mirrors carry `reversal_of_document_id` |
| Clearing pre-lock | Pre-lock LEDGER head and counter when items do not net to zero in LC, GC and RC at full amounts, or are partially cleared. Re-checked after the open-item locks |
| VAT | New event `vat.settlement_reversed`, error `VAT.NOT_SETTLED`. Settlement reversal command is `vat-returns:reverse-settlement` (permission `vat.settle`), replacing `vat-settlements:reverse` everywhere |
| Retention | Outbox purge reported as `outbox_events_purged` in the anonymisation run record. SYSTEM log of `order_address_snapshot` goes to `order_change_log`. Correction of partner maximum emits `retention.recomputed` (new event type). Both revaluation event types map to view type REVALUATION_RUN |
| Reversal codes | `journal-documents:reverse` rejects AUTO_REVERSAL, RERUN, REOPEN, RETURN from external callers (CORRECTION and ERROR only). The six-code list is closed |
| Upsert | Upsert on an existing row requires `expected_version`. Without a legacy match it inserts. Only the batch variant is exempt. Upsert without `legacy_ref` is rejected |
| Error codes | MIGRATION.*, INVENTORY.INSUFFICIENT_STOCK, INVENTORY.ALLOCATION_*, ORDER.CREDIT_NOTE_*, and a rejection code for `year-end:close` on history years are adopted and entered in 06 |
| Landed cost | `landed-costs:reverse` rejected with `INVENTORY.ALLOCATION_NOT_REVERSIBLE` if the on-hand part is consumed. Correction by new allocation |
| Receipt lines | `purchase_receipt_line` gets `stock_document_line_no` (first) and `stock_document_line_count` |
| APP_SUPPLIES | Goods lines (ORDERED too) still require a delivery reference. Only service lines use the order line reference |
| Credit notes | An invoice with credit notes cannot be reversed before the credit notes are reversed (`ORDER.INVOICE_HAS_CREDIT_NOTES`). A credit note on a reversed invoice is rejected |
| Migration | The seal variant of year close skips the D4 stale check (no revaluation runs in history). Sealed history years cannot be reopened. Corrections use adjustment postings |

## 12. Cleanup pass

Final consistency scan after pass 2 (2026-10-13). Specs 02 to 13, 15, 16, 00 and 01 were edited. 14 sections 1 to 11 are unchanged. The ERD files were not touched.

**Decided names, entered in 06 (2.4, 5.3, 7.3) and used in 02, 04, 05, 10, 11, 13 and the ADR log (ADR-171, ADR-173, ADR-174):**

| Code | Use |
|---|---|
| `PERIOD.HISTORY_YEAR` | `year-end:close`, and manual `periods:soft-close` and `periods:hard-close`, on `is_history` periods before the seal |
| `PERIOD.HISTORY_YEAR_SEALED` | `periods:reopen` and `year-end:reopen` on sealed history years |
| `ORDER.INVOICE_REVERSED` | A credit note (`sales-credit-notes:create`, `purchase-credit-notes:post`) on a reversed invoice |
| `REVERSAL.REASON_NOT_ALLOWED` | `journal-documents:reverse` called by an external caller with AUTO_REVERSAL, RERUN, REOPEN or RETURN. Replaces the `SCHEMA.INVALID_VALUE` placeholder in 06 |
| `INVENTORY.ALLOCATION_EXISTS` | Now adopted and entered in 06 (was proposed in 10 and 11) |

**Changes**

- "Proposed" and "open" wording removed for the adopted codes: 04 (7.3 rows, section 12 and 13 point 4 and 5), 05 (5.0, 5.1, 5.3, point 3), 06 Q16, 10 (header, 6.8, Q16, Q17, P4 now names `INVENTORY.INSUFFICIENT_STOCK`), 11 (6.4, 6.5, 7.3, 7.4, 06 delta row, Q25), 12 (header), 13 (header, 7.1, 9.1 rows, Q19, Q20).
- Old name `vat-settlements:reverse` remains only in marked history notes (06 header and 13.1, 07 delta, 08 delta and Q13, ADR-160 amendment and ADR-169). No other uses.
- `order_address_snapshot` SYSTEM entries: 11 `order_change_log` gets entity type `ORDER_ADDRESS_SNAPSHOT` with key (`order_kind`, `order_id`, `snapshot_id`). 02 states that `change_log` uses `SYSTEM` only for LEGAL_SNAPSHOT.
- Upsert: 13 (3.5) now states the 07 rule. 07 (11.1) had a sentence that still said "no `expected_version`" for a known legacy id on single create and update, corrected.
- Mirror types and `IC`: 02 and 03 already carried the rule (four mirror types stay, others keep the original type, no `IC` type). Wording check found no conflict. 06 catalogue (5.1) got a pointer line to the credit-note and landed-cost reverse commands in 5.3, 7.4 and 8.2.
- 00 line 13 range for the re-baseline ADRs now ADR-160 to ADR-174.
- Version headers: 02, 04, 05 and 15 raised to v0.4 (third pass is in their delta sections), 16 to v0.2 (second pass), spec references to 02 and 03 in the headers of 03 and 04 updated.
- 06 cross reference "10, 6.4 P4" corrected to 10, 6.2 P4 (the P4 phase is in 6.2).

**Scan results (no action needed)**

- Old head tables, `purchase_tolerance_override`, `purchase.match_override`, `default_reservation_mode`, `reservation_mode`, old command names, `stock-chains:status`, "three chains" style statements: found only in notes marked as removed or historical.
- All spec headers cite ADR-001 to ADR-174 (14 keeps its own historical wording). `chain_anchor` uses `scope_key`. `retention_until` is S on snapshots and partner. 07 has no table-count statement, its overview table list matches the section list. Cross-reference check over all specs found no dead section references (the hits were false positives from lists such as "04, 05").

**Remains open / needs a user decision**

1. 11 `order_address_snapshot` has primary key (`order_kind`, `order_id`, `address_role`). The decided log key (`order_kind`, `order_id`, `snapshot_id`) is mapped by letting `snapshot_id` carry the `address_role`. Confirm, or introduce a real `snapshot_id`.
2. The ERD files (02 drawio, SVG previews) were not edited. They need the new `entity_type` and `snapshot_id` columns of `order_change_log` and any new error-code-neutral changes from the earlier passes.
3. 13 still cites `periods:soft-close` and `periods:hard-close` rejection only through the 04 and 06 rows. The migration commit must therefore never call them, the seal does the closing (9.1). No decision needed unless the seal should be allowed to be run manually per period.
4. `extension_max_definitions` (07, 5.2) is still a proposed column name, 15 calls it "a setting". Secondary indexes of `outbox_event` in 09 are proposed and not yet in 15.
5. ADR-035 is still PROPOSED (00).

## 13. Final decisions (2026-10-13)

| Ref | Decision |
|---|---|
| ADR-035 | **ACCEPTED.** One signed amount per currency on each line (debit positive, credit negative). One transaction currency per document. A payment with a fee in another currency is two documents. Debit and credit for SAF-T and reports are derived in the read API |
| Address snapshot log key | `order_change_log` uses `address_role` (part of the `order_address_snapshot` key) for `ORDER_ADDRESS_SNAPSHOT` entries. The earlier `snapshot_id` name is dropped (11 updated, ERD updated) |
| `extension_max_definitions` | Column name confirmed in 07 |
| `outbox_event` secondary indexes | Confirmed in 09. To be added to 15, section 3.7 |
| History periods before seal | `periods:soft-close` and `periods:hard-close` are rejected with `PERIOD.HISTORY_YEAR` (applied in 04) |
| Status | Re-baseline of specs 00 to 13, 15, 16, the ADR log (ADR-001 to ADR-174) and the ERD (18 pages) is complete. No review item remains open |
