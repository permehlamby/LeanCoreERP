# Finance Core: Revaluation and Year-End, Table Specs

Status: DRAFT v0.6 for review, propagation pass of 2026-10-13 applied (17, section 5: B1 four-document rerun final, D9 closing rate text aligned, open point 4 resolved; delta row in section 10), fix pass 4 of 2026-10-13 applied (decisions of 17, section 4: A1, A2, A4, A5, B1, X1, D9, retained earnings, events and codes; section 10 lists them), re-baselined on 2026-10-13, third pass applied (v0.4 decisions of 14, section 11: mirror types, migration variant of year close, history years not closable or reopenable; second pass: reason codes AUTO_REVERSAL, RERUN, REOPEN, D4 wording confirmed), for the scope review (14, sections 8 to 11): D1 (mirror documents and YEAR_END reversal, ADR-145), D3 (lock order, ADR-147), D4 (stale check including special periods, `year-end:close` events, ADR-148), D2 (S columns, ADR-146), K1 (`chain_head`, ADR-158), K3 (outbox events, ADR-159) and the mechanical fixes (section 10). Builds on 00-design-principles v0.4, ADR-001 to ADR-174 and specs 02 to 04 (re-baselined).
Scope: period-end revaluation run (ADR-021), its automatic reversal and rerun, the link to hard-close (ADR-022), year-end close and reopen (ADR-024), how balances are read without opening balances.
Out of scope: configuration tables (gain/loss accounts, retained earnings account, revalue flag, open-item mode), consolidation translation, posting-command API contracts.
Conventions, lock classes (L1, L2, S), types and sign convention are as in 02-journal-document-core, section 1.

## 1. Decisions taken

| Topic | Decision |
|---|---|
| Revaluation lines | Per partner and currency on the revalued account (line_type `REVAL_ADJUSTMENT`, no open-item row), plus unrealised gain/loss lines. The open items themselves never change. Item-level detail lives in `revaluation_run_item` |
| Reversal | The automatic reversal is posted in the same transaction as the run, future-dated to the first day of the next period. The next period must exist and be inside the posting window |
| Opening balances | **None.** No opening-balance document and no snapshot table. The balance of a balance sheet account at any date is the sum of all its lines up to that date. This amends ADR-024 |
| Closing document | Classic closing entry: each P&L account (per dimension combination) is zeroed with a `CLOSING` line, offset against retained earnings |

Consequences of the last two choices, which the spec handles in sections 5 and 6: P&L reports for a closed year must exclude `YEAR_END` documents (and `CLOSING` documents, which are the closing entries of migrated history years, X1), and the closing document needs a period to be posted into after all regular periods are hard-closed (the closing special period, Q2).

## 2. Table overview

| # | Table | Role | Mutability |
|---|---|---|---|
| 1 | `revaluation_run` | One row per revaluation run, links the posted documents | L1, plus S status columns |
| 2 | `revaluation_run_rate` | Closing rates used by the run | L1, insert only |
| 3 | `revaluation_run_item` | One row per revalued open item or account balance, with before/after amounts | L1, insert only |

Year-end needs no new table. Its facts are the closing document, `fiscal_year` (status, `year_end_document_id`) and `period_event` (04).

Changes to existing tables (applied in 02 and 04): `journal_document.detail_digest` (L1, new), line_type `REVAL_ADJUSTMENT` (exempt from the partner and open-item rule, 02, 5A), rate_type `DERIVED`, and the `year-end:close` and `year-end:reopen` rows of 04, section 7.3.

Referenced configuration (defined in the config domain, attributes required here): per GL account `revalue_flag` and open-item mode (ADR-040), per company unrealised gain and loss accounts (optionally per currency), retained earnings account.

## 3. Revaluation

### 3.1 Scope and basis

`revaluation:run` is per company and regular fiscal period (special periods 13 and up are not revalued themselves, but the stale check of 3.7 covers their postings). Valuation date = end date of the period. Two bases:

| Basis | Accounts | Unit | Basis amounts as of the valuation date |
|---|---|---|---|
| OPEN_ITEM | Accounts with open-item mode CONTROL or GL_OPEN_ITEM and `revalue_flag` | Open item in a foreign transaction currency | Remaining TC = line TC minus cleared TC. Carried LC, GC, RC = line amounts minus cleared amounts. Cleared amounts are taken from `clearing_item` with `clearing_date` on or before the valuation date, not from the current S columns |
| ACCOUNT_BALANCE | Other balance sheet accounts with `revalue_flag` | Account and transaction currency | Sum of the lines up to the valuation date: TC, LC, GC, RC |

Rules:
- P&L accounts are never revalued (ADR-021).
- Only lines posted on or before the valuation date count. Revaluation and revaluation-reversal documents are excluded from the basis, so booked amounts are revalued, never earlier adjustments.
- Items whose transaction currency equals the target currency need no revaluation in that currency. Each of LC, GC, RC is revalued only where the TC differs from it.
- Intercompany items are revalued per company at the same closing rates (ADR-016, ADR-021), so group amounts stay eliminable.
- Accounts with `revalue_flag` must not have mandatory dimension rules (config validation, Q3), because adjustment lines carry no dimensions.

### 3.2 Computation

Per basis unit: `revalued_x = ROUND(remaining_tc x closing_rate(tc to x))` for x in LC, GC, RC, at the target currency's scale. `adjustment_x = revalued_x - carried_x`. Rates come from the `CLOSING` rate table for the valuation date, with the fallback rule of ADR-019. Missing rate: the run is rejected with a structured error naming the currency pair.

### 3.3 Document anatomy

The run posts two documents in one transaction.

**REVALUATION document** (`document_type` REVALUATION, `is_system_generated`, posting date = valuation date, period of the valuation date, `transaction_currency` = company currency):

| Line | line_type | Account | Amounts |
|---|---|---|---|
| Adjustment, one per (account, partner, currency) | `REVAL_ADJUSTMENT` | The revalued account | TC = 0. LC, GC, RC = sum of item adjustments |
| Result, one per (currency, direction) | `FX_UNREALISED` | Configured unrealised gain account, or loss account | TC = 0. LC, GC, RC = opposite of the adjustments it offsets |

For `GL_OPEN_ITEM` and `ACCOUNT_BALANCE` units there is no partner: one adjustment line per (account, currency). Direction of a result line follows the sign of the LC adjustment it offsets (ties go to gain). Because each currency's amounts are independent, a line may have different signs in LC and GC. `detail_digest` of the document is the `items_digest` of the run (3.5).

Worked example (company currency EUR, group currency EUR, USD open item of partner P1): remaining TC 100, carried LC 90, closing rate 0.95.

| Line | TC | LC |
|---|---|---|
| `REVAL_ADJUSTMENT` AR control account, partner P1 | 0 | +5 |
| `FX_UNREALISED` unrealised gain account | 0 | -5 |

**REVAL_REVERSAL document**: a mirror type that stays (14, section 11; the keep-original-type rule of 02, 3 does not apply to it). It mirrors the REVALUATION document 1:1 (ADR-048), opposite sign, `reversal_of_document_id` = the REVALUATION document, `reversal_reason_code` = `AUTO_REVERSAL` (D1, ADR-145, code list in 02, 3), `auto_reversal_date` on the original, and the original's `reversed_by_document_id` = this document (the first mirror, B1). REVALUATION and REVAL_REVERSAL are derived documents: `journal-documents:reverse` rejects them, only `revaluation:run` (rerun, 3.6) reverses them. Posting date = first day of the next period. Both documents get numbers in the same transaction. Numbers follow commit order, not posting date order (ADR-025).

Neither document carries an open-item row. The adjustment lines are exempt from P9 (system-generated, partner carried by `revaluation_run_item`). Partner balance at closing rate = open items plus the adjustment of the active run (Q4).

### 3.4 Rules of the run

1. Period must be OPEN. SOFT_CLOSED needs the soft-close authorisation (and sets `soft_close_override`). HARD_CLOSED rejects (ADR-021, ADR-022).
2. Valuation date and reversal date must be inside the posting window.
3. The next period must exist, be OPEN, and the reversal date must fall in it. If the period is the last of a fiscal year, the next fiscal year must have been opened (ADR-043). Otherwise the run is rejected with an instruction to open the year.
4. At most one ACTIVE run per (company, fiscal year, period).
5. A run that finds nothing to adjust is still recorded (no documents, `items_digest` of the empty set). It documents that revaluation was performed.
6. Lock order as in 04, section 6.3 (D3, ADR-147, A1, A5): number counters for the REVALUATION and REVAL_REVERSAL series in sorted order, then the `chain_head` rows LEDGER and **CLEARING** (the basis reads `clearing_item`, so the run serialises with clearings, A5), then the `fiscal_period` rows in share mode, then `outbox_head` last. The period status of rule 1 is read again after the head locks. Derivation (3.2) runs after the locks. **The locks do not make the run immune against later data.** A posting or clearing dated on or before the valuation date can still be made after the run commits, and the run is then stale. The earlier claim that the run "cannot be stale" under its locks is withdrawn: staleness is detected only by the check at hard-close (3.7).
7. The run writes its outbox events in the same transaction (K3, ADR-159, 15, section 6): `ledger.document_posted` for the REVALUATION and REVAL_REVERSAL documents, `ledger.document_reversed` for each mirror of a rerun, and the revaluation events `revaluation.run_completed` (every run, first or rerun, carrying run id, `run_no`, period, `items_digest`) and `revaluation.rerun` (additionally for a rerun, carrying the old and the new run id). Both revaluation event types are new and must be added to 15, section 6.
8. Error codes of the rules (for 06, new where marked): rule 1 `REVAL.PERIOD_NOT_OPEN`, rule 2 `REVAL.OUTSIDE_POSTING_WINDOW` (new), rule 3 `REVAL.NEXT_PERIOD_NOT_OPEN` and `REVAL.NEXT_YEAR_NOT_OPENED`, missing rate `REVAL.CLOSING_RATE_MISSING`, a SOFT_CLOSED period without authorisation `REVAL.SOFT_CLOSE_NOT_AUTHORISED` (new).

### 3.5 Tables

#### `revaluation_run`

PK: (`tenant_id`, `company_id`, `run_id`)
Unique: (`tenant_id`, `company_id`, `fiscal_year`, `fiscal_period`) where `status` = ACTIVE
Unique: (`tenant_id`, `idempotency_key`)
FK: (`tenant_id`, `company_id`, `fiscal_year`, `fiscal_period`) to `fiscal_period`.

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| tenant_id, company_id, run_id | ID | N | L1 | |
| fiscal_year, fiscal_period | INT, INT | N | L1 | Regular period (period numbers are INT everywhere, 04, 3.3) |
| valuation_date | DATE | N | L1 | End date of the period |
| run_no | INT | N | L1 | 1 for the first run of the period, +1 per rerun |
| supersedes_run_id | ID | Y | L1 | Set on a rerun. FK to this table |
| status | CODE | N | S | ACTIVE or SUPERSEDED. A rerun sets the old run SUPERSEDED **before** it inserts the new ACTIVE row (3.6, step 0), so the unique index above holds at every step |
| superseded_by_run_id | ID | Y | S | Id allocated for the new run at step 0 of the rerun. The new row is inserted in the same transaction. Owning event of the S update: the new run and its document pair (6) |
| document_id | ID | Y | L1 | REVALUATION document. Null if nothing to adjust |
| reversal_document_id | ID | Y | L1 | REVAL_REVERSAL document |
| item_count | INT | N | L1 | Rows in `revaluation_run_item` |
| items_digest | BINARY(32) | N | L1 | SHA-256 over rates and items in fixed order. Equals `detail_digest` of `document_id` |
| run_at | TS | N | L1 | UTC |
| run_by | TEXT(100) | N | L1 | |
| correlation_id, idempotency_key | TEXT(100) | N | L1 | ADR-004 |

#### `revaluation_run_rate`

PK: (`tenant_id`, `company_id`, `run_id`, `from_currency`, `to_currency`)

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| tenant_id, company_id, run_id | ID | N | L1 | |
| from_currency, to_currency | CODE | N | L1 | Transaction currency to LC, GC or RC |
| rate | RATE | N | L1 | CLOSING rate used |
| rate_date | DATE | N | L1 | Date of the table rate actually used (ADR-019 fallback) |

#### `revaluation_run_item`

PK: (`tenant_id`, `company_id`, `run_id`, `item_no`)
Index: (`tenant_id`, `company_id`, `open_item_document_id`, `open_item_line_no`): item history across runs.
Index: (`tenant_id`, `company_id`, `partner_id`, `run_id`): partner balance at closing rate.

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| tenant_id, company_id, run_id | ID | N | L1 | |
| item_no | INT | N | L1 | |
| basis_type | CODE | N | L1 | OPEN_ITEM or ACCOUNT_BALANCE |
| account_id | ID | N | L1 | |
| partner_id | ID | Y | L1 | CONTROL accounts only |
| ic_counterpart_company_id | ID | Y | L1 | Intercompany items |
| currency_tc | CODE | N | L1 | |
| open_item_document_id, open_item_line_no | ID, INT | Y | L1 | OPEN_ITEM only |
| remaining_tc | AMT | N | L1 | At the valuation date |
| carried_lc, carried_gc, carried_rc | AMT | N, N, Y | L1 | Booked amounts of the remaining part |
| revalued_lc, revalued_gc, revalued_rc | AMT | N, N, Y | L1 | At closing rate |
| adjustment_lc, adjustment_gc, adjustment_rc | AMT | N, N, Y | L1 | Revalued minus carried |
| adjustment_line_no | INT | Y | L1 | Line of the REVALUATION document that this item rolls up into. Null if the adjustment is zero |

### 3.6 Rerun

`revaluation:run` for a period that already has an ACTIVE run is a rerun, allowed only while the period is OPEN or SOFT_CLOSED with authorisation (ADR-021). Let R0 be the REVALUATION document of the ACTIVE run and A0 its automatic REVAL_REVERSAL. In one transaction, after the locks of 3.4:

0. **Supersede the old run first (B1, decided in 17, section 4).** Allocate the `run_id` of the new run, set the old run SUPERSEDED with `superseded_by_run_id` = that id, and only then insert the new run row as ACTIVE. This is the step at which the unique index "one ACTIVE run per (company, fiscal year, period)" (3.5) is satisfied: the old run leaves ACTIVE before the new row exists, so the index holds after every statement and needs no deferral.
1. **Mirror R0** (REVAL_REVERSAL type, `reversal_of_document_id` = R0, `reversal_reason_code` = `RERUN` (D1), posting date = the original valuation date). R0 already has A0 as its automatic reversal, so this is the **second mirror of R0**.
2. **Mirror A0** (REVAL_REVERSAL type, `reversal_of_document_id` = A0, reason `RERUN`, posting date = the original date of A0). This is a mirror of an automatic reversal and is allowed for this command only (it neutralises the second reversal of R0 in step 1), so the old pair nets to zero in both periods.
3. **Post the new run's own pair**: the new REVALUATION R1 and its automatic REVAL_REVERSAL A1, computed from the current data. R1 and A1 are ordinary run documents and **not mirrors** of anything.
4. Link the two runs (`revaluation_run.supersedes_run_id` on the new run, set at insert).

**Mirror rules of the rerun (B1).** (a) Several REVAL_REVERSAL mirrors per original are allowed: R0 has A0 and the RERUN mirror of step 1. (b) `reversed_by_document_id` of an original is its **first** mirror and is never overwritten: R0 keeps A0, A0 gets the step 2 mirror. Other mirrors are found through `reversal_of_document_id` (02, 3). (c) Only documents of the **ACTIVE** run are mirrored. A superseded run's documents are already neutralised and are never mirrored again, so no original is reversed by the rerun twice. (d) A rerun of a period whose ACTIVE run has no documents (nothing to adjust, 3.4 rule 5) mirrors nothing and posts only the new pair.

Net effect for an original adjustment X: R0 +X and M1 -X net to zero on the valuation date, A0 -X and M2 +X net to zero on the reversal date, and R1 and A1 carry the new result. Nothing is rewritten. Four extra documents appear in the chain per rerun. Because the next period cannot be hard-closed while this one is open (predecessor rule, 04 Q5), the next period's date is always postable, except when it is SOFT_CLOSED, which also requires the authorisation.

### 3.7 Staleness and hard-close (ADR-022, Q1)

An ACTIVE run is stale when new data would change its result: a posting or clearing dated on or before the valuation date, made after the run. The check is a recomputation, not a status flag: `revaluation:preview` (read-only, same logic as the run, side-effect free) is compared with the ACTIVE run's `items_digest`.

`periods:hard-close` for a regular period with `revalue_flag` accounts calls this check. Rule (Q1 decided): reject if the recomputed digest differs from the ACTIVE run, including when no run exists but the preview is non-empty. This replaces the generic wording "no pending system job" in ADR-022 for revaluation, and also the remaining example of that wording in 04, 7.3 (D4, ADR-148). The rest of the pending-job concept stays for later system jobs.

**Serialisation (A1, A5).** `periods:hard-close` and `year-end:close` lock the LEDGER and CLEARING heads before they recompute (04, 6.3), so no posting or clearing can commit between the recomputation and the status change. This, and not any lock held by `revaluation:run`, is what makes the check reliable. The check is made **at hard-close only** (X2): there is no per-posting stale check, and ADR-148, ADR-053 (amended note), 06 and the read specs must say the same.

**Scope of the check (D4, ADR-148, user decision of 14, section 10).** The check runs at the hard-close of every relevant period, not per posting. It covers every posting in scope, including postings into special periods 13 and up, which share the date range of period 12 and so fall inside the basis of period 12's valuation (Q6). It is therefore run at the hard-close of **every period whose range contains a valuation date**: the regular period itself, and each special period when it is hard-closed, including the closing period hard-closed by `year-end:close` (5.1, before the closing document is posted). The comparison is always against the ACTIVE run of the regular period whose valuation date lies in the range. A stale result is resolved by `periods:reopen` of that regular period (allowed while no later period is hard-closed, 04, 7.3) and a rerun (3.6).

### 3.8 Reading balances at closing rate

- GL balance of a revalued account = its lines, including the active run's `REVAL_ADJUSTMENT` lines for periods up to the valuation date. After the automatic reversal date the adjustment is gone again.
- Partner balance at closing rate = open items of the partner plus the partner's adjustment from `revaluation_run_item` of the ACTIVE run (Q4).
- Unrealised FX in the P&L for a period = `FX_UNREALISED` lines of the documents of that period.

## 4. Realised versus unrealised FX

Unchanged from ADR-021 and ADR-038. Realised FX is posted at clearing from booked amounts. Revaluation never touches the booked amounts. Because the automatic reversal restores the carrying value on day 1 of the next period, a later clearing computes realised FX against booked amounts without double counting. Q2 of 03 (clearing after the run) is covered by the staleness check in 3.7.

## 5. Year-end close

### 5.0 Use by migration seal

`year-end:close` is **rejected on history years** (`is_history` = true, 04, 3.2) with `PERIOD.HISTORY_YEAR` (14, section 12). The closing entries of a history year are posted by the HISTORY chunk of the migration (13, 6.1) as one document of type **`CLOSING`** (02, 3, X1, decided in 17, section 4), not YEAR_END. The seal sets `fiscal_year.year_end_document_id` of the history year to that document and writes it as `document_id` of the YEAR_END_CLOSE event (04, 7.3). Because `CLOSING` is its own type, `year-end:close`, `year-end:reopen` and the YEAR_END exclusion of 5.1 never touch it. P&L reads exclude it like YEAR_END, balance reads include it (5.4). `migration-batches:seal` (13, 9.1) closes the history years by a **migration variant of year close**, internal to the seal: it posts no document and no number, writes PERIOD_HARD_CLOSE for the history periods and, per history year, PERIOD_HARD_CLOSE for the closing period plus YEAR_END_CLOSE (`period_no` null), and **skips the revaluation stale check of 3.7** (no revaluation runs in history years). Sealed history years cannot be reopened: `year-end:reopen` (5.3) and `periods:reopen` (04, 7.3) reject them with `PERIOD.HISTORY_YEAR_SEALED`. Corrections of history use adjustment postings.

### 5.1 Command

`year-end:close` per company and fiscal year. Preconditions:
- Year status OPEN and not a history year (5.0).
- All regular periods of the year HARD_CLOSED (their predecessor rule and revaluation checks have therefore passed).
- The closing period, the highest special period of the year, exists and is OPEN. A calendar used with year-end close needs `special_period_count` of at least 1 (Q2).
- **Every special period of the year other than the closing period is HARD_CLOSED** (A4, decided in 17, section 4). Otherwise the command is rejected with `CLOSE.SPECIAL_PERIOD_NOT_CLOSED` (new). A closing period that exists but is not OPEN is rejected with `CLOSE.CLOSING_PERIOD_NOT_OPEN` (new), a year that is not OPEN with `CLOSE.YEAR_NOT_OPEN` (new), a missing closing period with `CLOSE.CLOSING_PERIOD_MISSING`.
- The revaluation stale check of 3.7 passes for the closing period (D4, ADR-148), run after the locks and before the closing document is posted. It also covers postings into the special periods 13 and up of the year.
- Retained earnings account configured, active, without mandatory dimension rules and **without `revalue_flag`** (Q3, decided in 17, section 4: a revalue-flagged retained earnings account is forbidden, config validation in 07 rejects it, and the close checks it again). Otherwise `CLOSE.RETAINED_EARNINGS_INVALID` (new).

**Locks (A1, 04, 6.3):** number counter of the YEAR_END series, LEDGER head, CLEARING head, PERIOD_EVENT head, then the VAT_RETURN, STOCK and FULFILMENT heads in share mode (anchors), then the `fiscal_period` and `fiscal_year` rows exclusively, `outbox_head` last. All preconditions above are checked after the locks, so no posting or clearing can commit into the year during the close.

Steps in one transaction:
1. Select all lines of the company with `fiscal_year` = Y on P&L accounts, excluding documents of type YEAR_END (the mirror created by `year-end:reopen` keeps the document type YEAR_END and is recognised by `reversal_of_document_id`, D1), so a re-close after a reopen does not pick up the reversed closing. Group by account and dimension combination (cost center, project, profit center, business unit, free dimension values).
2. Post one `YEAR_END` document (system-generated, numbered, chained): per group that is **non-zero in at least one currency** (LC, GC or RC, D9, 02, 5.2: a group that is zero in LC but not in GC or RC is still closed) one `CLOSING` line on the P&L account with the opposite sign of the group's sum, carrying the same dimension values, plus one `CLOSING` line on the retained earnings account with the net result. Posting date = last day of the fiscal year, `fiscal_period` = closing period (04, A3: the date lies in period 12's range).
3. Append the `PERIOD_HARD_CLOSE` event for the closing period and the `YEAR_END_CLOSE` event for the year (D4, ADR-148, 04, 3.4, so that the status trigger finds a matching event), set the closing period HARD_CLOSED, set `fiscal_year.status` = CLOSED and `year_end_document_id`.
4. Write the **anchor requests** (`anchors:create`) for all chains of the company, the six chain types LEDGER, CLEARING, PERIOD_EVENT, VAT_RETURN, STOCK and FULFILMENT of 02, 9.5 (ADR-046, ADR-158), with state PENDING and the digest, in this transaction (A2, decided in 17, section 4). The timestamp tokens are fetched after commit and the anchors become STAMPED then. The close requires the request, not the response, so an outage of the timestamp service does not block the year-end. `chains:verify` warns about PENDING anchors beyond the deadline (02, 9.5).
5. Write the outbox events (`ledger.document_posted`, `period.status_changed`, `year.closed`, 15, section 6) in the same transaction. Lock order as in the Locks paragraph above and 04, 6.3 (D3, K3, ADR-159).

If all P&L groups are zero in every currency there is no document: `year_end_document_id` stays null and the year closes anyway.

### 5.2 Closing document anatomy

| Aspect | Rule |
|---|---|
| Transaction currency | Company currency. `amount_tc` = `amount_lc`, `rate_lc` = 1 |
| GC, RC | Sums of the posted group and reporting amounts of the grouped lines. `rate_type` = DERIVED, rates are the implied ratios (`amount_gc / amount_lc`), table rates null. **Rate rules (D9, rule table in 02, 5.2):** "non-zero" means non-zero in at least one currency. When `amount_lc` of a line is 0 but GC or RC is not (D9, final in 17, section 5), the ratio cannot be formed from LC, so the rates are the **ratio to the first non-zero amount** (GC, else RC), the rate of that currency itself is 1, and `rate_lc` is 1, as in the last row of the table of 02, 5.2. The amounts are authoritative and the rates are derived from them. The retained earnings line is derived from the net result in each currency and follows the same rules. A line whose amounts are zero in all currencies is not created |
| Dimensions | Closing lines copy the dimension values of the lines they zero. Dimension validity periods and account active flags are not checked, because the lines mirror posted data. Dimension rules of the retained earnings account are not applied, see Q3 |
| Balance | Closing lines net to the retained earnings line in each currency by construction (P4) |
| Size | One line per P&L account and dimension combination. Possibly thousands of lines. Chained and hashed like any document |
| Hash | The document is a normal chain node. No `detail_digest` needed |

Unrealised FX already posted by the December revaluation is part of the P&L closed here. Its automatic reversal is dated in the next fiscal year and so appears in that year's P&L, which is the intended effect.

### 5.3 Reopen

`year-end:reopen` (ADR-024), privileged, reason code and text mandatory:
- Year status CLOSED and not a sealed history year (5.0). No later fiscal year of the company is CLOSED.
- The next year has no posting, except the automatic reversal documents (REVAL_REVERSAL with `auto_reversal_date` set) of the closed year's last revaluation. Other postings, including clearings, block the reopen. This amends the check wording of ADR-024.
- **Locks (A1):** as for `year-end:close` (5.1). The checks run after the locks.
- **Steps in this order (A4, decided in 17, section 4), one transaction:** (1) reopen the closing period HARD_CLOSED to OPEN and append `PERIOD_REOPEN`, (2) set the year status to OPEN and append `YEAR_END_REOPEN` with `document_id` = the closing document being reversed, (3) post the mirror of the closing document: a mirror document of type YEAR_END with `reversal_of_document_id` and `reversal_reason_code` = `REOPEN`, posting date = the closing date, in the closing period. The mirror is posted last because it needs an OPEN closing period in an OPEN year (the earlier order posted into a CLOSED year). It is the only way to reverse a YEAR_END document, D1, ADR-145: `journal-documents:reverse` rejects it. The regular periods stay HARD_CLOSED. Further changes use `periods:reopen` per ADR-023.
- **Reason codes:** the payload `reason_code` and `reason_text` of `year-end:reopen` are written to the two events (`period_event.reason_code`, 04, 3.4). The mirror always gets `reversal_reason_code` = `REOPEN`.
- Error codes: a later closed year `CLOSE.REOPEN_LATER_YEAR_CLOSED` (new), postings in the next year `CLOSE.REOPEN_BLOCKED_BY_POSTINGS`, a year that is not CLOSED `CLOSE.YEAR_NOT_CLOSED` (new).
- The reopen writes the outbox events `ledger.document_reversed`, `period.status_changed` and `year.reopened` (K3).
- A later `year-end:close` posts a new closing document. The earlier one stays, reversed.

### 5.4 Balances without opening balances

| Question | Answer |
|---|---|
| Balance sheet account balance at date d | Sum of all lines with `posting_date` <= d, over all fiscal years. Uses the `(account_id, posting_date)` index (02) |
| P&L account, fiscal year Y, to date d | Sum of lines with `fiscal_year` = Y and `posting_date` <= d, **excluding** document types YEAR_END and CLOSING. YEAR_END includes its mirror (the mirror keeps the type, D1). **CLOSING** is the closing entry of a migrated history year (X1, 02, 3), also excluded, so the P&L comparatives of history years are preserved and do not net to zero. The mirror of a CLOSING document in a migration rollback keeps the type CLOSING (02, 3) and is excluded as well. After the close, the cumulative P&L balance of a closed year is zero |
| Balance sheet and retained earnings reads | Include YEAR_END, CLOSING and their mirrors: the closing results move to retained earnings in the balances |
| Retained earnings | Sum of its lines. Includes the closing results of all closed years |
| Open items | Unaffected by year-end (ADR-049) |

A materialised balance cache can be added later as a pure optimisation (derived, rebuildable, not a source of truth, never part of the hash chain). It is not part of R1.

## 6. DB protection

| Table | App role grants |
|---|---|
| `revaluation_run` | INSERT. UPDATE of `status`, `superseded_by_run_id` by the revaluation command role only |
| `revaluation_run_rate`, `revaluation_run_item` | INSERT only |

The S update of `revaluation_run.status` on a rerun is allowed in any period status (D2, ADR-146). Its chained event is the new REVALUATION and REVAL_REVERSAL pair of the same transaction, and no `change_log` row is written.

Tamper evidence: `items_digest` is part of the REVALUATION document hash through `detail_digest` (02), so run items and rates are covered by the ledger chain. `chains:verify` recomputes the digest from `revaluation_run_rate` and `revaluation_run_item` and compares it with the document. Triggers backstop as in 02, section 10.

## 7. Resolved by this spec, and assumptions

| Ref | Item |
|---|---|
| A1 | Revaluation never changes open items or the S columns. Valuation uses `clearing_item` as of the valuation date, so a backdated clearing in an open period is visible to a rerun |
| A2 | Revaluation and revaluation-reversal documents are excluded from the basis of later runs. Their net effect is part of the GL balance only |
| A3 | Gain and loss are determined per line pair by the LC sign of the adjustment. Group and reporting amounts follow, even if their sign differs |
| A4 | Year-end closes P&L by account and dimension, in LC, GC and RC. The transaction-currency view of P&L accounts is lost for the closing document, which is acceptable because P&L is reported in LC |
| A5 | `year-end:close` posts into a closing special period, a period that is OPEN while all regular periods are hard-closed. This replaces ADR-024's "all periods hard-closed" (Q2 decided) |
| A6 | Applied to 02: `detail_digest`, line_type `REVAL_ADJUSTMENT`, rate_type `DERIVED`. Applied to 04: year-end rows of 7.3 |

## 8. Open questions

| # | Question |
|---|---|
| Q1 | **Decided:** hard-close recomputes the revaluation and rejects if it differs from the ACTIVE run, including "no run but non-empty preview". Changes ADR-022 wording for revaluation. |
| Q2 | **Decided:** the closing document is posted into the closing special period (the highest one), kept OPEN until `year-end:close`, which hard-closes it. Calendars used with year-end close need `special_period_count` of at least 1. |
| Q3 | **Decided:** closing lines copy dimensions from the zeroed lines. The retained earnings line has none. Config rejects retained earnings or revalue-flagged accounts with mandatory dimension rules. **Added (17, section 4):** config also rejects `revalue_flag` on the retained earnings account (5.1). |
| Q4 | **Decided:** adjustment lines carry no partner on the ledger. The partner link is in `revaluation_run_item`, and partner balance at closing rate joins it. |
| Q5 | **Decided:** no balance cache in R1. A derived, rebuildable cache can be added later as a pure optimisation. |
| Q6 | **Decided:** revaluation runs for regular periods only. Special-period postings are covered by recomputing period 12's valuation and the stale check at hard-close, which since D4 (ADR-148) also runs at the hard-close of special periods 13 and up (3.7). |
| Q7 | **Decided:** one gain and one loss account per company, with optional per-currency overrides. |

## 9. ADRs raised by this spec (now in 01-adr-log, which is authoritative)

**ADR-050: Revaluation postings and run records.** `revaluation_run`, `revaluation_run_rate` and `revaluation_run_item` record rates and item-level detail. The ledger carries one `REVAL_ADJUSTMENT` line per account, partner and currency plus unrealised gain/loss lines. Open items are never changed. Refines ADR-021.

**ADR-051: Automatic reversal in the same transaction.** The REVAL_REVERSAL is posted with the run, dated the first day of the next period, which must exist and be open. Rerun reverses both old documents and posts a new pair. Refines ADR-021.

**ADR-052: Revaluation detail covered by the chain.** `journal_document.detail_digest` ties non-line detail records into the document hash.

**ADR-053: Revaluation staleness check at hard-close.** Hard-close recomputes the revaluation and compares it with the ACTIVE run. Replaces the generic pending-job wording of ADR-022 for revaluation. *Amended by ADR-148 (D4): also run at the hard-close of special periods 13 and up and of the closing period.*

**ADR-054: No opening balances.** Balances are cumulative sums of lines. P&L of a closed year is read by excluding YEAR_END documents. Amends ADR-024. *Amended by ADR-145 (D1): the mirror of a YEAR_END document, created by `year-end:reopen`, is excluded as well.*

**ADR-055: Year-end closing entry per P&L account and dimension.** Closing lines zero each P&L account and dimension combination against retained earnings, posted into a closing special period that is hard-closed by the command. Reopen reverses the closing document. Amends ADR-024.

## 10. Re-baseline of 2026-10-13: changes in this spec and deltas toward other specs

| Ref | Change in 05 |
|---|---|
| D1, ADR-145 | REVAL_REVERSAL and the rerun reversals carry `reversal_reason_code` (3.3, 3.6). REVALUATION and REVAL_REVERSAL are derived, rejected by `journal-documents:reverse`. The YEAR_END mirror of `year-end:reopen` has `reversal_of_document_id` and the reopen reason code. P&L reads and the re-close exclude YEAR_END and its mirror (5.1, 5.3, 5.4) |
| D2, ADR-146 | S update of `revaluation_run.status` allowed in any period status, no `change_log` (6) |
| D3, ADR-147 | Run and year-end lock order refer to 04, 6.3, derivation after the locks (3.4, 5.1) |
| D4, ADR-148 | Stale check also at the hard-close of special periods 13 and up and of the closing period (3.7, 5.1). `year-end:close` appends PERIOD_HARD_CLOSE for the closing period and YEAR_END_CLOSE (5.1). The old generic wording in 04 is replaced (04, 7.3) |
| K1, ADR-158 | Anchors at year-end cover all six chain types (5.1) |
| K3, ADR-159 | Outbox events for the run, the rerun, year-end close and reopen (3.4, 5.1, 5.3) |
| 15, section 10 | Applied: outbox events, lock order. No change to hash definitions |
| 16, section 9 | No delta toward 05 |
| Third pass (14, section 11) | Mirror types: REVAL_REVERSAL stays as mirror type of REVALUATION, including the rerun mirrors (3.3, 3.6, section 11 resolved). Only YEAR_END (and VAT_SETTLE and the like) keep the original type (02, 3). `year-end:close` rejected on history years, migration variant of year close skips the D4 stale check, sealed history years cannot be reopened, codes `PERIOD.HISTORY_YEAR` and `PERIOD.HISTORY_YEAR_SEALED` (5.0, 5.1, 5.3, 14 section 12) |
| Second pass (14, section 10) | D4 confirmed: stale check at the hard-close of every relevant period (3.7), `year-end:close` writes PERIOD_HARD_CLOSE for the closing period plus YEAR_END_CLOSE with `period_no` null (5.1, 04, 3.4). Reason codes AUTO_REVERSAL, RERUN, REOPEN (3.3, 3.6, 5.3, code list in 02, 3). YEAR_END mirror keeps type YEAR_END (5.1, 5.3, 5.4) |
| 14, sections 3 and 4 | `three chains` replaced by all six chain types (5.1). Command names normalised (`periods:hard-close`, `periods:reopen`, `chains:verify`) |
| Fix pass 4 (17, section 4, v0.5) | **A1:** `year-end:close` and `year-end:reopen` lock LEDGER and CLEARING heads before PERIOD_EVENT, checks after the locks (5.1, 5.3, 3.7). **A2:** anchor requests PENDING in the close transaction (5.1). **A4:** every non-closing special period HARD_CLOSED before close, reopen order period, year, mirror, new codes (5.1, 5.3). **A5:** `revaluation:run` locks the CLEARING head, claim that the run cannot be stale removed (3.4, 3.7). **B1:** rerun mirror rules, supersede before insert, `reversed_by` = first mirror, ACTIVE-run documents only (3.3, 3.6, 3.5). **X1:** `CLOSING` excluded from P&L reads like YEAR_END, `year_end_document_id` of history years (5.0, 5.4). **X2:** stale check at hard-close only, stated (3.7). **D9:** "non-zero in any currency" and rate rules of the closing document (5.1, 5.2). **Retained earnings:** `revalue_flag` forbidden (5.1, Q3). **Types:** `fiscal_period` INT (3.5). **Events:** `revaluation.run_completed` and `revaluation.rerun` (3.4). **Reason vocabularies:** reopen payload code to `period_event`, REOPEN to the mirror (5.3). New codes for 06: `CLOSE.YEAR_NOT_OPEN`, `CLOSE.YEAR_NOT_CLOSED`, `CLOSE.SPECIAL_PERIOD_NOT_CLOSED`, `CLOSE.CLOSING_PERIOD_NOT_OPEN`, `CLOSE.RETAINED_EARNINGS_INVALID`, `CLOSE.REOPEN_LATER_YEAR_CLOSED`, `REVAL.OUTSIDE_POSTING_WINDOW`, `REVAL.SOFT_CLOSE_NOT_AUTHORISED` |
| Propagation pass (17, section 5, v0.6) | **B1:** rerun with four documents is final, open point 4 resolved (3.6, 11). **D9:** closing-document rate text aligned with the rule table of 02, 5.2: ratio to the first non-zero amount, amounts authoritative, no "confirm" wording left (5.2). The `revalue_flag` ban on the retained earnings account (5.1, Q3) is carried in 07 as `CONFIG.REVALUE_FLAG_ON_RETAINED_EARNINGS` |
| Deltas toward other specs | 04, 7.3 (hard-close, year-end rows) carries the D4 text. 02, 5A has the REVAL_ADJUSTMENT exemption. 06: `year-end:reopen` error and reason code, system reason codes for automatic reversal and rerun |

## 11. Open points

| # | Point |
|---|---|
| 1 | **Resolved (second pass):** reason codes AUTO_REVERSAL, RERUN, REOPEN are defined in 02, 3 |
| 2 | **Resolved (14, section 11):** the mirror of YEAR_END keeps type YEAR_END. REVAL_REVERSAL stays as the mirror type of REVALUATION. In a rerun the mirror of an automatic REVAL_REVERSAL also has type REVAL_REVERSAL (3.6, 02, 3 states the rule once) |
| 3 | **Resolved (14, section 12):** `PERIOD.HISTORY_YEAR` (close on history years before seal) and `PERIOD.HISTORY_YEAR_SEALED` (reopen of sealed history years), entered in 06 (see 04, section 13, point 4) |
| 4 | **Resolved (17, section 5, B1):** the four-document form of 3.6 is final: mirror of R0, mirror of A0, the new run's own pair (R1, A1), with several mirrors per original, `reversed_by` = first, the old run superseded before the new run is inserted, and only the ACTIVE run's documents mirrored. The literal wording of 17, section 4, B1 (step 1 mirrors only an original without an existing auto reversal) is replaced by this form, because R0 always has A0 |
