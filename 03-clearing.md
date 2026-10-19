# Finance Core: Clearing, Table Specs

Status: DRAFT v0.6 for review, propagation pass of 2026-10-13 applied (17, section 5: reset item list final, open point 5 added; delta row in section 10), fix pass 4 of 2026-10-13 applied (decisions of 17, section 4: A1, B2, B3, change_log shape, reset lock order; section 10 lists them), re-baselined on 2026-10-13, third pass applied (v0.4 decisions of 14, section 11: pre-lock condition definitive, CLEARING_RESET stays a mirror type; second pass: D3 pre-lock rule, reason codes of 02, 3), for the scope review (14, sections 8 to 11): D1 (CLEARING_RESET as mirror document, ADR-145), D2 (S columns without `change_log`, ADR-146), D3 (lock order, ADR-147), D9 (clearing does not touch `first_used_at`, ADR-153), K1 (`chain_head`, ADR-158), K3 (outbox events, ADR-159) and the mechanical fixes (section 11). Builds on 00-design-principles v0.4, ADR-001 to ADR-174 and 02-journal-document-core v0.4.
Scope: clearing of open items (ADR-005): clearing record, cleared items, realised FX and differences at clearing, un-clearing (reset), consistency of the open-item S columns, tamper evidence of clearings.
Out of scope (apps or later): matching and allocation logic (bank statement matching, payment proposals), dunning, payment file generation, number range and period tables.
Conventions, lock classes (L1, L2, S), types and sign convention are as in 02-journal-document-core, section 1.

## 1. Decisions taken

| Topic | Decision |
|---|---|
| Model | Header plus items. One `clearing` row per clearing command, one `clearing_item` row per cleared open item. Supports partial, many-to-many and credit-against-invoice clearing |
| Document | A clearing is **not** a journal document. A complete match posts nothing and consumes no legal document number. A journal document (type CLEARING) is posted, numbered and chained only when the clearing needs lines (realised FX or difference). The clearing then points at it |
| Traceability | Every clearing has its own identity, sequence number and hash chain, and records exactly which documents (open items) it cleared and for what amounts. A clearing is traceable from the clearing to its items and from each open item to its clearings |
| Reversal | Un-clearing is a new `clearing` row of type RESET with mirrored, opposite-sign items. The original is never changed. If the original had a difference document, the reset posts a CLEARING_RESET document that mirrors it |
| Scope R1 | Same account and same partner (same or different currency), plus open-item-managed GL accounts without partner (e.g. bank clearing). Cross-account and cross-partner clearing: not in R1 |
| Matching | The core receives an explicit item list with amounts. Finding which items belong together is an app concern |

## 2. Table overview

| # | Table | Role | Mutability |
|---|---|---|---|
| 1 | `clearing` | One row per clear or reset command. Chain node | L1 + one L2 column, insert only otherwise |
| 2 | `clearing_item` | One row per cleared open item, with cleared amounts in all four currencies | L1, insert only |
| ~~3~~ | ~~`clearing_chain_head`~~ | Removed (14 K1, ADR-158). The head is a row of the shared `chain_head` (02, 9.2) with `chain_type` = CLEARING and empty `scope_key` | |

Changed in 02 (already applied): `journal_line_open_item` (partner and due date optional, cleared amounts in four currencies, FK `last_clearing_id`), `journal_document` (two document types for difference documents), `journal_line` (line_type CLEARING_DIFF).

## 3. Anatomy of a clearing

- **Clean match.** Items net to zero in every currency. Result: one `clearing` row, n `clearing_item` rows, updated S columns on the open items. No journal document, no legal number.
- **Match with difference.** The same, plus one `journal_document` of type CLEARING with the two difference lines of 3.1, in the same transaction. `clearing.document_id` points at it.

### 3.1 Difference lines

When the cleared items do not net to zero in LC, GC or RC (realised FX, ADR-021), the difference document carries exactly two lines:

| Line | line_type | Account | Amounts |
|---|---|---|---|
| FX result | `FX_REALISED` | Configured realised FX gain/loss account | `amount_tc` = 0. LC, GC, RC = residual, signed to net the document to zero |
| Offset | `CLEARING_DIFF` | The cleared account (and partner) | `amount_tc` = 0. LC, GC, RC = the opposite residual. Carries an open-item row |

The offset line is itself an open item. It is created already cleared: this clearing has a `clearing_item` row for it (an item pointing at a line of the clearing's own document). Net effect: the cleared account and partner balance to zero.

Worked example (company currency EUR, transaction currency USD): invoice +100 USD booked at 0.90 (LC +90), payment -100 USD booked at 0.92 (LC -92).

| Item | TC | LC |
|---|---|---|
| Invoice (cleared) | +100 | +90 |
| Payment (cleared) | -100 | -92 |
| Sum | 0 | -2 |
| Offset line `CLEARING_DIFF` on customer account | 0 | +2 |
| FX line `FX_REALISED` (gain) | 0 | -2 |

LC sums to zero on the document (+2 -2) and on the cleared set (+90 -92 +2). The same logic applies to GC and RC.

Differences in transaction currency (short payment, cash discount, small write-off) are not FX. The clearing command rejects a clearing whose same-currency items do not net to zero in TC (Q1 decided). A short payment clears partially and leaves the rest open. A discount or write-off is posted as an ordinary document (credit memo or journal, with tax by the usual rules) and then cleared against the invoice.

## 4. Tables

### 4.1 `clearing`

PK: (`tenant_id`, `company_id`, `clearing_id`)
Unique: (`tenant_id`, `company_id`, `chain_seq`)
Unique: (`tenant_id`, `idempotency_key`)
Unique: (`tenant_id`, `company_id`, `document_id`) where `document_id` is not null
Unique: (`tenant_id`, `company_id`, `reset_of_clearing_id`) where not null (a clearing is reset at most once)
FK: (`tenant_id`, `company_id`, `document_id`) to `journal_document`.
Index: (`tenant_id`, `company_id`, `account_id`, `partner_id`).

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| tenant_id, company_id | ID | N | L1 | |
| clearing_id | ID | N | L1 | Surrogate |
| chain_seq | BIGINT | N | L1 | Gapless per (tenant, company). This is the clearing number used in references and audit |
| clearing_type | CODE | N | L1 | `CLEAR` or `RESET` |
| clearing_date | DATE | N | L1 | Effective date. Must be inside the posting window and in a period that allows posting (ADR-022, ADR-025). Equals the posting date of `document_id` when there is one. The period status is read again in share mode after the CLEARING head lock (A1, 5.6), because `periods:hard-close` locks the CLEARING head before it changes the status |
| fiscal_year, fiscal_period | INT | N | L1 | Resolved from `clearing_date` |
| cleared_at | TS | N | L1 | System timestamp UTC |
| account_id | ID | N | L1 | All items are on this account (R1 scope) |
| partner_id | ID | Y | L1 | Null only for GL_OPEN_ITEM accounts. All items carry this partner |
| document_id | ID | Y | L1 | The difference document, if one was posted. Null on a clean match |
| reset_of_clearing_id | ID | Y | L1 | Set when clearing_type = RESET. FK to the clearing reversed |
| clearing_method | CODE | N | L1 | MANUAL, AUTO_MATCH, PAYMENT_ALLOCATION, REVERSAL (the automatic clearing of mirror open items against their originals, 5.7). Informational |
| matching_ref | TEXT(100) | Y | L1 | Caller's match or batch reference |
| source_system | CODE | N | L1 | P13 |
| idempotency_key | TEXT(100) | N | L1 | Mandatory (ADR-004) |
| command_name | CODE | N | L1 | `clearings:post`, `clearings:reset` |
| cleared_by | TEXT(100) | N | L1 | User or service identity |
| correlation_id | TEXT(100) | N | L1 | |
| note | TEXT(200) | Y | L2 | Changes logged in `change_log` with entity type CLEARING, `clearing_id` set and `document_id` null (02, 8). The period that governs the change is the clearing's own period (`fiscal_year`, `fiscal_period` above): OPEN allows, SOFT_CLOSED needs `change.soft_closed`, HARD_CLOSED rejects (L3, 04, 7.2) |
| prev_hash | BINARY(32) | N | L1 | See section 6 |
| row_hash | BINARY(32) | N | L1 | |
| hash_version | SMALLINT | N | L1 | |

Constraints:
- `reset_of_clearing_id` is not null if and only if clearing_type = RESET.
- `document_id` is null or the document type is CLEARING (for CLEAR) or CLEARING_RESET (for RESET).
- `account_id` and `partner_id` equal the account and partner of every item's line (command check).

### 4.2 `clearing_item`

PK: (`tenant_id`, `company_id`, `clearing_id`, `item_no`)  FK to `clearing`.
Unique: (`tenant_id`, `company_id`, `clearing_id`, `open_item_document_id`, `open_item_line_no`).
FK: (`tenant_id`, `company_id`, `open_item_document_id`, `open_item_line_no`) to `journal_line_open_item`.
Index: (`tenant_id`, `company_id`, `open_item_document_id`, `open_item_line_no`, `clearing_date`): the path from an open item to all its clearings, and the basis of as-of queries (09).

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| tenant_id, company_id, clearing_id | ID | N | L1 | |
| item_no | INT | N | L1 | 1..n |
| clearing_date | DATE | N | L1 | Copied from `clearing`. Lets as-of open-item and aging queries work without joining `clearing` (09, section 6) |
| open_item_document_id | ID | N | L1 | Document of the cleared line. May be the clearing's own difference document (offset line) |
| open_item_line_no | INT | N | L1 | |
| currency_tc | CODE | N | L1 | Transaction currency of the cleared line. Copied for readability |
| cleared_amount_tc | AMT | N | L1 | Signed like the line amount. CLEAR rows have the sign of the line, RESET rows the opposite, with one exception: the item of a RESET on the offset line of its own CLEARING_RESET document has the sign of that line (5.4) |
| cleared_amount_lc | AMT | N | L1 | Portion of the line's LC amount. See 5.2 |
| cleared_amount_gc | AMT | N | L1 | |
| cleared_amount_rc | AMT | Y | L1 | Null if the line has no RC amount |

Constraints (command-enforced, with a verification query in `chains:verify`):
- Cumulative cleared TC per open item (sum over its `clearing_item` rows, CLEAR and RESET) lies between 0 and the line's `amount_tc` (same sign, never over-cleared, never negative).
- One row per open item per clearing.
- A CLEAR has at least two items, or one item together with its own difference-document offset line.
- A RESET has the mirrored items of its original except the item on the original's difference-document offset line, which is not mirrored, plus, when the original has a difference document D, exactly one item on the offset line of its own CLEARING_RESET document (5.4). A RESET therefore never touches the cleared amount of D's offset line.
### 4.3 Consistency with `journal_line_open_item` (S columns)

On every clearing or reset, in the same transaction, the command recomputes for each touched open item:

| S column | Value |
|---|---|
| cleared_amount_tc/lc/gc/rc | Sum of its `clearing_item` amounts |
| clearing_status | OPEN if cleared TC = 0 and cleared LC = 0, CLEARED if equal to the line's `amount_tc` (and LC), otherwise PARTIAL |
| last_clearing_id | The clearing just posted |

The S columns are a denormalised cache of `clearing_item` (ADR-037). `chains:verify` recomputes them and reports mismatches. Per D2 (ADR-146) these S updates are allowed in hard-closed periods (the open item may be old, only `clearing_date` is checked, 5.1), are not written to `change_log`, and their event is the `clearing` and `clearing_item` rows of the same transaction, which the trigger backstop of 02, section 10 requires.

## 5. Rules

### 5.1 Eligibility

- Items must have a `journal_line_open_item` row and not be fully cleared.
- All items share account, partner (or all have none) and company.
- Items from hard-closed periods can be cleared. Only `clearing_date` must be in a period allowing posting (ADR-022, ADR-025).
- Intercompany items clear within each company only. No cross-company clearing.
- A payment block on an item does not stop the core from clearing it. Blocks are an app-level control.

### 5.2 Amount allocation

The caller supplies, per item, `cleared_amount_tc` (full or partial). The core derives the other three:
- Full clearing of the remaining amount: LC, GC, RC = the remaining carried amounts (line amount minus already cleared), so the partials always sum to the original exactly.
- Partial clearing: `cleared_lc = ROUND(line_lc x cleared_tc / line_tc)`, same for GC and RC, at the currency's scale.
- A line whose TC amount is zero (a difference-document offset line) is cleared as a whole.

### 5.3 Balance and residual

After allocation:
1. If all items share one `currency_tc`, TC must net to zero, else the clearing is rejected.
2. LC, GC and RC: a non-zero net becomes the two lines of 3.1 in a new CLEARING document. If the net is zero in all three, no document is posted.
3. The difference document balances in all currencies (P4), and the cleared set including its offset line nets to zero.

### 5.4 Reset (un-clear)

`clearings:reset` on clearing C:
- Allowed only if C is a CLEAR, not already reset, not a REVERSAL clearing (5.7), and the reset's `clearing_date` is in a period allowing posting. The reset carries its own, later, date and sequence number.
- Without a difference document on C: the reset has mirrored items (opposite sign) and posts nothing.
- With a difference document D on C: the reset also posts a CLEARING_RESET document that mirrors D (opposite sign, `reversed_line_no` set, same method as a normal reversal, 02 Q8). It is a mirror document in the sense of 02, section 3 (D1, ADR-145) and keeps its own mirror type CLEARING_RESET (the keep-original-type rule of 02, 3 does not apply to it, 14 section 11): `reversal_of_document_id` = D, `reversal_reason_code` = the `reason_code` of the reset command (limited to `CORRECTION` and `ERROR`, 02, 3), and D gets `reversed_by_document_id`. **Item list of the reset (B2, decided in 17, section 4, replaces the earlier two-item rule, which could not be satisfied because D's offset line is already fully cleared by C):**
  - **No item on D's offset line.** It stays CLEARED through the item of C, which the reset does not mirror. The cumulative rule of 4.2 therefore holds for it.
  - **One item on the reset's own offset line** (the `CLEARING_DIFF` line of the CLEARING_RESET document), cleared as a whole like any zero-TC offset line (5.2). The line is an open item created already cleared, with `clearing_status` CLEARED and `last_clearing_id` = the reset.
  - **All other items of C are mirrored** (opposite sign) and reopen.

  Worked example, continuing 3.1. C cleared: invoice (TC +100, LC +90), payment (TC -100, LC -92) and D's offset line (TC 0, LC +2). D has FX_REALISED LC -2 and offset LC +2. The reset posts D' (CLEARING_RESET, mirror of D): FX_REALISED LC +2, `CLEARING_DIFF` offset on the customer account LC -2. Items of the reset:

  | Item | TC | LC | Effect |
  |---|---|---|---|
  | Invoice (mirror of C) | -100 | -90 | Invoice reopens to OPEN |
  | Payment (mirror of C) | +100 | +92 | Payment reopens to OPEN |
  | D' offset line (own offset) | 0 | -2 | D' offset is CLEARED by this reset |
  | D offset line | none | none | Stays CLEARED by C |
  | Sum | 0 | 0 | |

  The items net to zero in LC (-90 +92 -2), the pair D offset (+2) and D' offset (-2) are both fully cleared and net to zero on the customer account, and D' balances (+2 -2). The same logic applies to GC and RC.
- Items return to OPEN or PARTIAL through the S columns.
- Not allowed if it would leave an item with negative cleared TC.

### 5.5 Interaction with other commands

| Command | Rule |
|---|---|
| `journal-documents:reverse` of a document with cleared or partially cleared lines | Rejected until the clearing is reset. The difference document of a clearing (type CLEARING, and CLEARING_RESET) is a derived document: `journal-documents:reverse` rejects it (D1), it can only be reversed through `clearings:reset` (Q4 decided). A document whose open items are all uncleared is reversed, and its mirror open items are then cleared against the originals automatically (5.7, B3) |
| `revaluation:run` (ADR-021) | Values only the remaining open amount: remaining TC = line TC - cleared TC, remaining carried LC = line LC - cleared LC. Fully cleared items are excluded. The run locks the CLEARING head (A5, 05, 3.4), so it serialises with clearings. A clearing dated on or before the valuation date and made after the run is possible and is caught by the stale check at hard-close only (05, 3.7) |
| `year-end:close` (ADR-024) | Open items are not recreated. The same lines and `clearing_item` history carry on across years (A2, Q3 decided) |
| `chains:verify` | Also verifies the clearing chain, recomputes S columns of every open item from `clearing_item`, and checks that every clearing's items plus difference lines net to zero in LC, GC and RC |

### 5.6 Concurrency and lock order

Two clearings on the same open item must serialise. The authoritative lock order is in 04, section 6.3 (D3, ADR-147). For a clearing command it gives:
1. number range row of the difference-document series (pre-locked when a difference document can be needed, see the rule below),
2. `chain_head` rows sorted by chain type: LEDGER (pre-locked under the same condition), then CLEARING,
3. `journal_line_open_item` rows of all items, sorted by key,
4. `outbox_head`, always last (K3, ADR-159).

Allocation, residual and S column values (4.3, 5.2, 5.3) are derived **after** the locks, from the locked rows. This replaces the earlier order (open items first, chain heads last).

**Pre-lock rule (D3, 14, sections 10 and 11, decided).** Number counters lock before chain heads, and chain heads before open items. A clearing that can produce an exchange difference therefore locks the number counter of the CLEARING difference-document series and the LEDGER head **before** it locks the open items. The command pre-locks when the candidate items do not net to zero in LC, GC and RC at their full amounts (booked LC, GC, RC, immutable L1 data), or when any of them is partially cleared. The condition is evaluated before the locks and **re-checked after the open-item locks** against the locked rows. After the locks the command derives the real residual (5.3). If the re-check finds that the condition holds although no pre-lock was taken, the command cannot take the counter and the LEDGER head any more without breaking the lock order: it releases its locks and starts again with the pre-lock (see the open points). If no difference document turns out to be needed, the pre-locked counter is not consumed (no number is used, the lock is only released) and no document is posted. A clearing whose items clearly net to zero in all currencies locks only the CLEARING head and the open items. The pre-check is conservative: an unneeded pre-lock costs contention, never correctness.

**Period re-check (A1, decided in 17, section 4).** `periods:hard-close` and `year-end:close` lock the LEDGER and CLEARING heads before the PERIOD_EVENT head (04, 6.3). A clearing therefore reads the status of the period of `clearing_date` in share mode again after it holds its CLEARING head lock (and its LEDGER head lock when it pre-locked one) and rejects if the period no longer allows posting. A pre-read before the locks is allowed for an early rejection only.

**`clearings:reset` (B2 follow-up, decided in 17, section 4).** The same pre-lock rule applies, with its own condition. A reset of a clearing C that has a difference document (`document_id` is not null, an L1 fact known before the locks) always needs a CLEARING_RESET document, so it pre-locks the number counter of the CLEARING_RESET series and the LEDGER head, then the CLEARING head, then the open items of C (sorted by key), then `outbox_head`. A reset of a clearing without a difference document locks only the CLEARING head and the open items. Items return to OPEN or PARTIAL after the locks, from the locked rows. The counter series of the difference document of a CLEAR and of a RESET are resolved from the document types CLEARING and CLEARING_RESET (04, 5.2).

### 5.7 Automatic clearing at reversal (B3, decided in 17, section 4)

When a mirror document carries open-item lines (02, 3, any mirror-producing command, including `journal-documents:reverse`, the owning commands of derived documents and `migration-batches:rollback`), the command clears each mirror open item against its original **in the same transaction**, as a clearing without a difference document:
- One `clearing` row per (account, partner) group of the open-item lines of the document (R1 scope, 1), `clearing_type` CLEAR, `clearing_method` REVERSAL, `clearing_date` = posting date of the mirror, own `chain_seq`. No journal document and no legal number. `document_id` is null.
- One `clearing_item` per original line and one per mirror line, `cleared_amount_*` = the full line amounts. Because the mirror reverses the lines 1:1 (02, Q8), the items net to zero in TC, LC, GC and RC, so no difference can arise and no pre-lock of a counter is needed for the clearing itself.
- The original open items must be completely uncleared. Otherwise the reversal is rejected before posting (`REVERSAL.HAS_CLEARED_LINES`).
- Lock order of the reversal command: number counter of the mirror, LEDGER head, CLEARING head, the original open items (sorted by key), `outbox_head` last (04, 6.3). The mirror lines are new rows and need no lock.
- Events: `clearing.posted` and `open_item.cleared` per cleared line, in the same transaction (6). Revaluation sees the cleared items from `clearing_date` on (05, 3.1).
- A later `clearings:reset` of such a REVERSAL clearing is rejected (`CLEARING.NOT_RESETTABLE`, new): the originals stay reversed.

## 6. Tamper evidence

Clearings have their own chain per (`tenant_id`, `company_id`), same scheme as the ledger chain (02, section 9): `chain_seq` starts at 1, `prev_hash` links to the previous clearing.

Chain head: the row of `chain_head` (02, 9.2) with key (`tenant_id`, `company_id`, `chain_type` = CLEARING, `scope_key` = empty), columns `last_seq`, `last_hash`, `hash_version`, `updated_at` (all S). Locked by the clearing command at the position given by 04, 6.3 (K1, ADR-158). The table `clearing_chain_head` no longer exists.

`row_hash = H( hash_version | tenant_id | company_id | chain_seq | prev_hash | clearing_L1_fields | items_digest | linked_document_hash )`
- `clearing_L1_fields`: all L1 columns of `clearing` except `chain_seq`-derived hash columns, in fixed order.
- `items_digest = H( concat of clearing_item L1 columns ordered by item_no )`.
- `linked_document_hash`: the `row_hash` of `document_id`, or a fixed NULL marker. This binds the clearing to its difference document in the ledger chain, so the two chains cannot be separated or swapped unnoticed.
- `note` (L2) and the S columns of open items are outside the hash.

Anchoring uses the shared `chain_anchor` table (02, 9.5) with `chain_type` = CLEARING.

DB protection: `clearing` INSERT, plus UPDATE on `note` only. `clearing_item` INSERT only. The CLEARING row of `chain_head` is updated by the clearing command role only. No DELETE anywhere. Same trigger backstop, per column class, as in 02, section 10.

Events (K3, ADR-159): `clearings:post` and `clearings:reset` write `clearing.posted` or `clearing.reset`, `open_item.cleared` or `open_item.reset` per touched item, and `ledger.document_posted` for a difference document, all in the command transaction (15, 5.1 and 6). The clearing event carries `chain_ref` only, the evidence stays in the clearing chain.

## 7. Resolved by this spec, and assumptions

| Ref | Item |
|---|---|
| A1 | Clearing identity and sequence come from `clearing_id` and `chain_seq`. No legal number range is consumed (resolves earlier Q5 and Q6) |
| A2 | Open items persist across year-end: the lines and their clearing history are not re-posted into the new year. Consistent with ADR-024 wording "carry forward with original dates and rates". Confirmed (Q3) |
| A3 | Realised FX is computed from the booked LC, GC and RC amounts of the cleared items. No rate lookup happens at clearing, so a clearing never sets `exchange_rate.first_used_at` (D9, ADR-153). Consistent with ADR-021 |
| A4 | Matching and allocation are app concerns. The core validates and records |
| A5 | A clean match leaves no entry in the GL, so the ledger chain and legal numbering are unaffected. Audit trail for clearings is the clearing chain |

## 8. Open questions

| # | Question |
|---|---|
| Q1 | **Decided:** reject TC differences. Discounts and write-offs are ordinary documents that are then cleared. No new line types, no tolerance config. |
| Q2 | **Decided:** accept. A clearing after the revaluation run is allowed. The period cannot be hard-closed until the revaluation is rerun (revaluation stale check at hard-close, ADR-051 and ADR-053, 05, 3.7). |
| Q3 | **Decided:** open-item rows persist across year-end. Opening-balance documents carry balances only. |
| Q4 | **Decided:** reject reversal of a document with cleared lines until `clearings:reset` has been run. No auto-reset. |
| Q5 | **Decided:** accepted. Exports read the clearing chain for the "which payment settled which invoice" view. **Follow-up:** add to the P15 per-country regulatory review, to catch any country that requires clearings in the legal number series. |
| Q6 | **Decided:** accept ADR-040. Account open-item mode NONE, CONTROL, GL_OPEN_ITEM. P9 wording is amended to cover control accounts only. |
| Q7 | **Decided:** defer cross-account and cross-partner clearing (AR/AP netting). No hooks in R1. |
| Q8 | **Decided:** one clearing chain per company. A scope key can be added later if contention requires (`chain_head.scope_key` is empty for CLEARING, K1). |

## 9. ADRs raised by this spec (now in 01-adr-log, which is authoritative)

**ADR-036: Clearing as its own chained record, not a document.** A `clearing` row with `clearing_item` rows. A journal document (type CLEARING) is posted only when a difference needs lines. Own gapless sequence and hash chain, linked to the difference document by hash. Refines ADR-005 and ADR-007 (no legal number for clean matches).

**ADR-037: Clearing items are the log of open-item status.** `clearing_status` and cleared amounts on `journal_line_open_item` are a denormalised cache (lock class S) of `clearing_item`, recomputed and verified. They are not written to `change_log`. Refines ADR-031.

**ADR-038: Realised FX at clearing via residual lines.** Two lines (`FX_REALISED` and `CLEARING_DIFF`), computed from booked amounts of the cleared items, no rate lookup. Refines ADR-021.

**ADR-039: Un-clearing by reset record.** A `RESET` clearing mirrors the original with opposite sign, own sequence number and later date. A clearing can be reset once. A difference document is mirrored by a CLEARING_RESET document.

**ADR-040: Open-item-managed GL accounts.** Open-item mode per account (NONE, CONTROL, GL_OPEN_ITEM). Partner optional only for GL_OPEN_ITEM. Amends P9. Depends on Q6.

## 10. Re-baseline of 2026-10-13: changes in this spec and deltas toward other specs

| Ref | Change in 03 |
|---|---|
| D1, ADR-145 | CLEARING_RESET is a mirror document with `reversal_of_document_id` and reason code (5.4). CLEARING and CLEARING_RESET are derived documents, rejected by `journal-documents:reverse` (5.5) |
| D2, ADR-146 | S column updates of open items allowed in hard-closed periods, no `change_log`, event is the clearing record (4.3) |
| D3, ADR-147 | Lock order of 5.6 replaced by the authoritative order of 04, 6.3. Derivation after the locks. A clearing that can produce an exchange difference pre-locks the counter and the LEDGER head. Third pass: the pre-lock condition (items not netting to zero in LC, GC and RC at full amounts, or partially cleared, re-checked after the open-item locks) is decided, no longer a proposal (5.6) |
| D1 follow-up | Reset reason: `reversal_reason_code` of the CLEARING_RESET document is one of the codes of 02, 3 (the reset command passes it). The rule that mirrors keep the original `document_type` (02, 3) applies only to types without own mirror type. CLEARING_RESET is one of the four mirror types that stay (14, section 11), so it remains the type defined here |
| D9, ADR-153 | Clearing does not touch `exchange_rate.first_used_at` (A3) |
| K1, ADR-158 | `clearing_chain_head` removed, row of `chain_head` with chain type CLEARING (2, 6) |
| K3, ADR-159 | Clearing commands write outbox events in their transaction, `outbox_head` last (5.6, 6) |
| 15, section 10 | Applied: outbox events (6), lock order (5.6). No hash change |
| 16, section 9 | No delta toward 03 |
| 14, sections 3 and 4 | Command names normalised (`clearings:post`, `clearings:reset`, `journal-documents:reverse`, `chains:verify`). Q2 reference to the ADR-022 pending-job check replaced by the stale check of ADR-051 and ADR-053 |
| Fix pass 4 (17, section 4, v0.5) | **B2:** reset item list with amounts, no item on D's offset line, one item on the reset's own offset line (5.4, 4.2). **Reset lock order:** pre-lock of counter and LEDGER head whenever C has a difference document (5.6). **A1:** period re-check in share mode after the CLEARING head lock (5.6). **B3:** automatic clearing of mirror open items at reversal, `clearing_method` REVERSAL (4.1, 5.7). **`change_log` shape:** clearing notes use `clearing_id`, entity type CLEARING, governing period is the clearing's own (4.1, 02, 8). **A5:** the revaluation run locks the CLEARING head (5.5). **Stale text:** "or reversed" in Q2 removed (8). New code: `CLEARING.NOT_RESETTABLE` (reset of a REVERSAL clearing, for 06) |
| Propagation pass (17, section 5, v0.6) | The reset item list of 5.4 (no item on D's offset line, one item on the reset's own offset line, all other items mirrored, worked example with amounts) is final. Open point 5 added as resolved. The B1 rerun form (four documents) and the D9 rate rule are final in 05 and 02. Clearing differences use rate 1 and `DERIVED` (02, 5.2, D9). Open point 4 stays open (not covered by a decision of 17) |
| Delta toward other specs | 04, 6.3 holds the lock order. 02, 9.2 defines `chain_head`. 06 command contract for `clearings:reset` already has `reason_code`, limited to `CORRECTION` and `ERROR` (verify in 06). 06: new code `CLEARING.NOT_RESETTABLE`. 15: `clearing.posted` is also emitted by reversal commands (5.7) |

## 11. Open points

| # | Point |
|---|---|
| 1 | **Resolved (second pass, D3):** pre-lock of counter and LEDGER head when a difference can arise, pre-check from L1 amounts, re-check after the locks (5.6) |
| 2 | **Resolved (14, section 11):** pre-lock condition is: items do not net to zero in LC, GC and RC at full amounts, or are partially cleared, re-checked after the open-item locks (5.6) |
| 3 | **Resolved (14, section 11):** CLEARING_RESET stays as a mirror type. The keep-original-type rule applies only to types without own mirror type (02, 3) |
| 4 | **Resolved (user approval 2026-10-14):** when the re-check after the open-item locks finds the pre-lock condition true and no pre-lock was taken, the command releases its locks and starts again with the pre-lock (5.6) |
| 5 | **Resolved (17, section 4, B2, final in section 5):** the item list of a reset with a difference document is the one of 5.4: no item on D's offset line, one item on the reset's own offset line, every other item of C mirrored. The earlier two-item rule is withdrawn |
