# Consolidation Data

Status: DRAFT v0.2. Task 3 of the agreed work order, second spec. ADR-222 (accepted 2026-10-19): the consolidation run and its results live in an app, the core adds only the data it lacks. This spec lists that data, the reads, and what the consolidation app must read and prove. The decisions Q1 to Q6 of section 11 were taken as recommended (2026-10-19, ADR-224 to ADR-227) and the deltas of section 10 are applied. Builds on 00 v0.4, ADR-001 to ADR-223 and specs 02, 05, 07, 09, 11, 21, 22.
Scope: ownership and method per company, translation basis per group account, rate types AVERAGE and HISTORICAL, the meaning of the intercompany marker, group-level reads, the intercompany reconciliation read, readiness, the contract of the consolidation app.
Out of scope: the consolidation run (translation, elimination, minority interest, goodwill), consolidated statements and cash flow, group tax, a consolidation ledger in the core (option B of 21, not designed), UI.

**Caution.** Accounting treatments named here (current rate method, temporal method, which items use which rate) are mechanisms the data must support, not accounting advice: the choice per company belongs to the user and the auditor. Nothing in this spec was tested against a standard or a real consolidation.

## 1. What exists and what is missing

| Need of a consolidation | In the core today | Gap |
|---|---|---|
| Which companies belong to the group at a date | `company_group`, `group_membership` effective-dated (07, 5.3, 5.6, ADR-018) | Nothing. Reads take membership as of the reporting date (ADR-018) |
| Group currency amount per line | Stored per line at the rate of the posting date, IC pairs copy it (02, 5.2, ADR-016, ADR-020) | Nothing |
| Local currency amount per line, closing-rate revaluation of monetary items | Yes (05, ADR-021) | Nothing |
| Group chart of accounts and the mapping of company accounts | `group_chart_account`, `account_group_mapping` (V) (07, 6.4) | No rule ties the mapping to the group of the company. No translation rate basis. Not usable in reads (G-C3, G-C5) |
| Ownership, parent, method | Nothing | G-C1 |
| Average and historical rates | Rate types SPOT, CLOSING, VAT (07, 7.2, ADR-019: "out of scope", open item of 00, section 5) | G-C2 |
| Intercompany partner and amounts | `ic_transaction_id`, `ic_counterpart_company_id` (02, 3, 5.4), IC roles (07, 6.3) | The marker is stated "on IC lines" only. Not a key of any read (G-C4) |
| Reconciliation of IC balances | Nothing apart from raw reads | G-C6 |

## 2. Findings from reading the specs

| Ref | Finding |
|---|---|
| G-C1 | The core has no ownership percentage, parent or consolidation method. The group is a container with a membership date (07, 5.6) |
| G-C2 | Only SPOT, CLOSING and VAT rates are loaded. Profit and loss is translated at an average rate and equity at historical rates in the usual methods. The posting-date rate stored on each line is a transaction rate |
| G-C3 | `account_group_mapping` has a company, an account and a group account (07, 6.4), but nothing checks that the group account belongs to the group the company is in at that date. A company that changes group (ADR-018) keeps mappings to the old group's accounts |
| G-C4 | `ic_counterpart_company_id` is an L1 column of `journal_line` (02, 5.4) and the header (02, 3), described as set "on IC lines". The IC commands generate the IC account lines (06, 5.5). Which other lines of an IC document carry it is not stated. Without it on the revenue and expense lines, the elimination of intercompany sales and purchases cannot be found by a read, and `balances:query` has no key for it anyway (09, 4.1) |
| G-C5 | `balances:query` groups by `account_group`, which is the company's own account group, and has no key for the group chart (09, 4.1). The group chart is stored but nothing reads it |
| G-C6 | Intercompany balances sit in two models: the IC role accounts per counterpart (07, 6.3, ADR-016) and the AR and AP of the intercompany partner (`business_partner.ic_company_id`, 11, 6.7). Differences between the two sides arise from settlements in transit, manual postings to IC accounts (the IC role accounts have open item mode NONE, and I found no rule in 06 or 07 that forbids a direct posting to them) and migrated data |
| G-C7 | A rate row that no posting used is never usage-locked (07, 7.2, `first_used_at`). An average or historical rate used only by the consolidation app could be changed afterwards without a trace in the result |

## 3. Ownership and method (G-C1)

### 3.1 `company_consolidation_setting_version` (V)

PK: (`tenant_id`, `company_id`, `effective_from`). Resolution as `company_setting_version` (07, 5.5): the row with the highest `effective_from` not later than the reporting date. Written with `write.config` (07, 11.1). Organisation data, no personal data.

| Column | Type | Null | Notes |
|---|---|---|---|
| parent_company_id | ID | Y | The immediate parent in the group. Null for the top company. Must be a member of the same group at `effective_from`, not the company itself. No cycle at any date |
| ownership_pct | RATE | Y | The parent's direct share, above 0 and at most 100. Required when `parent_company_id` is set, null otherwise. The effective group share (parent chain) is computed by the app |
| consolidation_method | CODE | N | `FULL`, `PROPORTIONAL`, `EQUITY` or `NOT_CONSOLIDATED`. The core does not judge the choice against the percentage (control is a judgement) |
| translation_method | CODE | N | `NONE`, `CURRENT_RATE` or `TEMPORAL` (3.2) |

Rules and error codes (area `CONFIG`):
- `CONFIG.NOT_A_GROUP_MEMBER`: the company is not a member of a group at `effective_from`.
- `CONFIG.OWNERSHIP_INVALID`: percentage outside the range, missing with a parent, present without one, or the parent not a member of the same group.
- `CONFIG.OWNERSHIP_CYCLE`: the parent chain returns to the company at some date.
- `CONFIG.TRANSLATION_METHOD_INVALID`: `NONE` while the local currency differs from the group currency, or another value while they are equal.

A new row never restates anything (ADR-018). Membership gives the acquisition date and the exit date (`valid_from`, `valid_to` of `group_membership`). The first-time consolidation entries, goodwill and the carrying value of the investment are app data: the core stores no equity or purchase price.

### 3.2 Translation methods

| Value | Meaning for the app | Data used |
|---|---|---|
| `NONE` | Local currency equals the group currency. No translation | Group amounts equal local amounts |
| `CURRENT_RATE` | Balance sheet at the closing rate, profit and loss at the average rate of the period, equity at historical rates, the difference goes to a translation reserve | Local amounts, rates of the types CLOSING, AVERAGE and HISTORICAL from local to group currency, `translation_rate_basis` of the group account (4) |
| `TEMPORAL` | The stored group amounts are used: lines at the rate of their posting date and the monetary items revalued at the closing rate by the revaluation run (05) | Group amounts of the lines |

The stored group amount is a posting-date rate result, not a translation of the local ledger. A company that needs `CURRENT_RATE` therefore translates from local amounts and ignores the stored group amounts, except for the intercompany check of section 7. The choice is policy, not law.

## 4. The group chart and its mapping (G-C3, G-C5)

**`group_chart_account`** (07, 6.4) gets one column:

| Column | Type | Null | Notes |
|---|---|---|---|
| translation_rate_basis | CODE | Y | `CLOSING`, `AVERAGE` or `HISTORICAL`. Null means the default of the account type: ASSET and LIABILITY `CLOSING`, REVENUE and EXPENSE `AVERAGE`, EQUITY `HISTORICAL`. Read by the app only, no core command uses it |

**`account_group_mapping`** (07, 6.4) gets one rule: the group account must belong to the group that the company is a member of at `valid_from`. A violation is `CONFIG.GROUP_ACCOUNT_OTHER_GROUP`. A change of group (a new `group_membership` row) does not remap anything: the old mappings stay as history and the company needs mappings to the new group's accounts from the date of the change. The readiness check of 9 warns about it.

**Which date resolves a mapping in a read.** The mapping row valid at the **reporting date** (`as_of_date`, or `to_date` for a movement), as ADR-018 does for membership. A remapping therefore changes the presentation of earlier periods consistently. A line whose account has no mapping at that date appears in the row with `group_account_id` null.

## 5. Rate types AVERAGE and HISTORICAL (G-C2, G-C7)

`exchange_rate.rate_type` (07, 7.2) gets two values: `AVERAGE` and `HISTORICAL`. This amends ADR-019 (the open item "average and historical rate types").

- **AVERAGE:** the average rate of a period for a currency pair. `valid_from` is the first day of the period, the row applies until the next row, as for every rate. Computed by the app or taken from a rate provider, never by the core.
- **HISTORICAL:** the rate at an event date (acquisition, capital contribution, dividend) for a pair. `valid_from` is the event date. One row per event. The app reads the row with that exact `valid_from`, there is no fallback.
- **No core command uses them.** No posting, clearing or revaluation looks up an AVERAGE or HISTORICAL rate: no fallback window, no override tolerance, no inverse or cross derivation (07, 7.2, Q9) and no `first_used_at`. The pair is the one the app needs, normally local to group currency, loaded explicitly.
- **Reading:** the generic list of the exchange rate resource with a `rate_type` filter (the config and master resources of 07, 11.1 are listed generically, 09, 3). No new read.
- **Locking (accepted, ADR-225, 07, 7.3):** a new command `exchange-rates:mark-used`, payload `rate_ids[]` (up to the batch limit of `master_batch_max_items`) and `purpose_ref` (the consolidation run reference, 100 characters). It sets `first_used_at` on every named row that has none, for any rate type. The usage lock of 07, 7.2 then applies: the row can no longer change or be deactivated. It is idempotent per key. It writes a `master_change_log` row per rate (field `first_used_at`) and the event `master.changed`. Permission `write.config`. A run that used locked rates can therefore be repeated with the same inputs. Without the command, the app must keep its own snapshot of the rates and digest them (8).

## 6. The intercompany marker (G-C4)

Before ADR-226 the marker `ic_counterpart_company_id` was stated "on IC lines" (02, 5.4). Meaning now (ADR-226): the IC commands (`intercompany-documents:post`, `ic-deliveries:post`, `ic-invoices:create`) set the marker, on the lines of the document **other than tax lines**:

| Line | Marker |
|---|---|
| Generated IC account lines (IC role accounts) | Set (as now) |
| Control line (AR or AP, partner of the counterpart) | Set |
| BASE lines (revenue, expense, inventory, asset) | Set |
| TAX, TAX_NONDEDUCTIBLE, ROUNDING, TAX_ROUNDING | **Not set**. Tax is owed to the tax authority and is not eliminated. The non-deductible tax is posted as cost on the base line account but is a tax cost, not an intercompany cost |

- **Hash.** The marker is already an L1 column and part of the line hash (02, 9.3). Which lines carry it changes for documents posted after the delta. No `hash_version` is needed: verification recomputes from stored values. No document is rewritten.
- **Migrated and manual postings.** A line posted with `journal-documents:post` has no marker (the field is not in the request, 06, 5.2). Such a posting to an IC account is possible: it is the case G-C6 describes and the reconciliation of section 7 must show it.
- **Reads.** `ic_counterpart_company_id` becomes a group key and a filter of `balances:query` (7.1) and a filter of `journal-lines`, and `is_intercompany` (boolean, derived: marker not null) a filter.

## 7. Reads

### 7.1 `balances:query`, three additions (09, 4.1)

| Addition | Meaning |
|---|---|
| `group_id` instead of `company_ids` | The member companies of the group as of the reporting date (membership of ADR-018). Needs `read.cross_company`. The response lists the companies and the membership date used |
| `group_account_id` as group key and filter | Resolved through `account_group_mapping` at the reporting date (4). The row carries the code, name, account type and `translation_rate_basis` of the group account. Unmapped lines form the row with null |
| `ic_counterpart_company_id` as group key and filter, `is_intercompany` as filter | 6 |

Amounts are never summed across currencies (09, 4.1.2): with several companies the rows split by currency combination, as today. Each company's response position is returned as `ledger_seq` per company, as the consistency token of 09, section 2 requires.

### 7.2 `ic-reconciliations:query` (new, read-only)

Needs `read.ledger` and `read.cross_company`.

| Parameter | Req | Notes |
|---|---|---|
| company_ids or group_id | Req | At least two companies |
| as_of_date | Req | |
| view | Opt | `BALANCES` (default) or `DOCUMENTS` |
| tolerance_gc | Opt | Differences with an absolute group amount up to this value are not flagged. Default 0 |

**View BALANCES.** One row per unordered pair of companies {A, B}:
- `a_to_b`: the net of the marked balance sheet lines (account type ASSET or LIABILITY) of company A with counterpart B at the date, as a block of LC, GC and RC. `b_to_a`: the same for B with counterpart A.
- `difference_gc`: `a_to_b` GC plus `b_to_a` GC, which is zero when the positions agree. The same by `transaction_currency` for the lines of one currency (`difference_tc`).
- `pnl_a_to_b_gc`, `pnl_b_to_a_gc` and `pnl_difference_gc`: the net of the marked BASE lines of REVENUE and EXPENSE accounts of the fiscal year to date (the year of `as_of_date`, excluding YEAR_END and CLOSING documents as in 09, 4.1.1).
- `flags`: `BALANCE_DIFFERENCE`, `PNL_DIFFERENCE`. A balance difference is expected for a settlement in transit (one side has booked the payment, the other not yet) and for manual postings. The read shows it, the app explains and adjusts.

**View DOCUMENTS.** One row per intercompany document pair or single document with a finding:
- `ONE_SIDED`: a document with `ic_transaction_id` whose counterpart is not in the tenant or not visible to the caller (`COUNTERPART_NOT_VISIBLE` when the caller lacks access to that company).
- `GC_DIFFERENT`, `TC_DIFFERENT`, `DATE_DIFFERENT`: the two sides differ although ADR-016 copies them. Cannot occur through the IC commands. A finding points to migrated data or a defect.
- `ONE_SIDE_REVERSED`: only one side of the pair is reversed.

The read is a control over the ledger, not a posting aid. Sums use `journal_line` amounts (L1, four currencies) and need no new table.

## 8. What the consolidation app must read and prove

The run itself is not specified. Its data contract is:

| Step | Read or command | Purpose |
|---|---|---|
| 1 | `group_membership`, `company_consolidation_setting_version` at the reporting date | Scope, parent chain, method, translation method |
| 2 | `fiscal-periods` and `revaluation-runs` per company (09, 3) | Every company's period is at least soft-closed and has an active revaluation run (05). The app refuses to run otherwise. Hard-close is the strong form |
| 3 | `exchange-rates` list by type CLOSING, AVERAGE, HISTORICAL, then `exchange-rates:mark-used` (if accepted) | Rates of the run, frozen |
| 4 | `balances:query` per company by `group_account_id`, `account_type` and `ic_counterpart_company_id` | Local and group amounts per group account |
| 5 | `ic-reconciliations:query` both views | Intercompany differences and anomalies |
| 6 | `chains:status` (09, 4.9) and the `anchors` list (09, 3) per company | Proof of input |

**Proof record.** For every run the app stores, per company: the LEDGER `chain_seq` and `row_hash` at the cut-off (the `chain_head`, 02, 9.2), the newest STAMPED anchor that covers it (02, 9.5) or a note that it is still PENDING, the digest of the `balances:query` result, the `ledger_seq` of the response, the digest of the rate rows used (and whether they are locked), and the valid-from dates of the mapping rows and the setting rows read. With these a later verifier recomputes the chains (`chains:verify`), checks the anchors, and shows that the inputs of the run were the ledger at that time. After a restore of the core (20, section 12) the app compares its stored `chain_seq` values with the restored ones: a lower value means postings were lost and the run must be repeated.

**Adjustments and results.** Elimination entries, translation differences, minority interest and the consolidated statements are app data, with references to core ids and the proof record. They are not postings (ADR-222). The consolidation-ledger option stays open: `ledger_id` is already part of the chain key (02, 9.2).

## 9. Readiness (07, 11.3)

Three warnings (not errors) are added to `companies:check-readiness`:

| Check | Source |
|---|---|
| A company with a `company_consolidation_setting_version` whose method is not `NOT_CONSOLIDATED` is a member of a group at today's date | 3.1 |
| A company that is a member of a group with group accounts has at least one `account_group_mapping` row valid today | 4 |
| After a change of group, the company has mappings to accounts of its current group | 4 |

## 10. Deltas (applied 2026-10-19)

Applied in 07 (3.2, 5.6a, 6.4, 7.2, 7.3, 11.1, 11.2, 11.3, 13.2f), 02 (5.4, 14), 06 (2.4, 5.5, 7.4, 8.2, 13.7), 09 (3, 4.1, 4.1.3, 4.14, 6, 9.5), 11 (6.7, 10), 15 (6) and 01 (ADR-224 to ADR-227, amendments of ADR-017 and ADR-019). The ERD was updated on 2026-10-19 (page 5: consolidation settings, group chart column, policy settings, `einvoice_rule_set`; page 6: rate types).

| Spec | Delta |
|---|---|
| 07 | New table `company_consolidation_setting_version` (5.x) and its four error codes. `translation_rate_basis` on `group_chart_account` and the mapping rule (6.4, `CONFIG.GROUP_ACCOUNT_OTHER_GROUP`). Rate types AVERAGE and HISTORICAL and the command `exchange-rates:mark-used` (7.2). Three readiness warnings (11.3). Table list (2) and write permission `write.config` (11.1) |
| 02 | 5.4: the meaning of the marker (6). No hash change |
| 06 | 5.5, 11 (6.7): the IC commands set the marker as in 6. Error codes. `exchange-rates:mark-used` in 7.4 and the permission map |
| 09 | 4.1: `group_id`, `group_account_id`, `ic_counterpart_company_id`, `is_intercompany`. New 4.14 `ic-reconciliations:query`. 3: `exchange-rates` list filter `rate_type` |
| 11 | 6.7: the marker on base and control lines |
| 15 | Event `master.changed` for `exchange-rates:mark-used` (no new event type) |
| 01 | ADR-224 to ADR-227 (section 13) and the amendment of ADR-019 |

## 11. Decisions (taken 2026-10-19: Q1 to Q6 as recommended)

| # | Decision | Recommendation | Alternative |
|---|---|---|---|
| Q1 | Ownership and method per company: the table of 3.1 | Accept. Four columns, no percentage logic | Keep it in the app. Then ownership is outside the core's audit trail and the readiness checks |
| Q2 | Rate types AVERAGE and HISTORICAL, and `exchange-rates:mark-used` | Accept both: the lock makes a consolidated figure reproducible | Accept the rate types only and let the app snapshot the rates (8) |
| Q3 | The IC marker on base and control lines, not on tax lines | Accept. It is the only way to find intercompany revenue and expense by a read | Marker as today and the app pairs documents by `ic_transaction_id` (heavier, and manual postings stay invisible) |
| Q4 | `balances:query` additions and the new `ic-reconciliations:query` | Accept both | Additions only: the app reconciles itself from `balances:query` by counterpart |
| Q5 | `translation_rate_basis` on the group account and the group consistency rule of the mapping | Accept | No column: the app holds the basis table |
| Q6 | Unmapped accounts: readiness warning and the null row in reads | Accept | Block a company from the group until all accounts with postings are mapped (too hard for a rollout) |

## 12. Test list

| # | Scenario | Checks |
|---|---|---|
| CT-01 | Company moves from group G1 to G2 mid-year | Reads as of dates before and after use the right members and mappings. Old mappings stay. Readiness warns until the new mapping exists |
| CT-02 | Mapping to a group account of another group | `CONFIG.GROUP_ACCOUNT_OTHER_GROUP` |
| CT-03 | Ownership cycle, percentage 0 and 101, parent outside the group | `CONFIG.OWNERSHIP_CYCLE`, `CONFIG.OWNERSHIP_INVALID` |
| CT-04 | Company in EUR, group in EUR, method CURRENT_RATE; company in DKK, group EUR, method NONE | `CONFIG.TRANSLATION_METHOD_INVALID` |
| CT-05 | `balances:query` by `group_account_id` with one unmapped account | Null row with the amount, other rows by group account |
| CT-06 | IC invoice pair (11, 6.7) | Marker on the IC lines and base lines of both sides, not on tax lines. Both sides reconcile to zero in group amounts |
| CT-07 | IC settlement in transit (payment booked in A only) | `BALANCE_DIFFERENCE` with the difference equal to the payment in group amount |
| CT-08 | Manual posting to an IC role account | Visible as a balance difference. Not marked, therefore listed in the document view as a finding if it lacks a pair |
| CT-09 | IC revaluation at the closing rate on both sides (05) | No difference in group amounts |
| CT-10 | `exchange-rates:mark-used` then an attempt to change the rate | The usage lock rejects the change. A second call with the same key replays |
| CT-11 | Restore of the core to an earlier point | The proof record shows lower `chain_seq`, the app repeats the run |
| CT-12 | A company sold mid-year | It stays in reads up to its exit date (ADR-018). Its IC balances at that date are external balances by the app's rule |

## 13. ADRs raised by this spec

ACCEPTED 2026-10-19, full text in 01-adr-log: ADR-224 (ownership and method per company), ADR-225 (rate types AVERAGE and HISTORICAL and the mark-used command, amends ADR-019), ADR-226 (the meaning of the intercompany marker, refines ADR-016 and ADR-017), ADR-227 (group-level reads: group chart, group and counterpart keys, `ic-reconciliations:query`, and the consistency rule of the group chart mapping).

## 14. Open points

| # | Point |
|---|---|
| O1 | **Settled in the delta:** the stock and cost journals of an order-built IC delivery (`ic-deliveries:post`, at the transfer price) get no marker (11, 6.7, 02, 5.4). The unrealised intercompany profit in stock is data of the order link and the stock documents, computed by the consolidation app. Check at the first client whether a read over IC stock is needed |
| O2 | Calendars may differ between companies of a group. The app aligns by posting date ranges. Whether the core should offer a group calendar is not decided |
| O3 | Cash flow statement and segment reporting need classifications the group chart does not carry. Not in this spec |
| O4 | The effective group share through several parents is an app calculation. If apps differ in how they compute it, a core read could standardise it |
| O5 | Minority interest needs the equity of subsidiaries by owner. The core stores no owner structure of equity |
