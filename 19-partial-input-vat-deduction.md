# Partial and Non-Deductible Input VAT

Status: DRAFT v0.2 for review. Design pass of ADR-201 (scope accepted 2026-10-14). Builds on 00-design-principles v0.4, ADR-001 to ADR-202 and specs 02, 06, 07, 08, 09, 18. ADR-203 to ADR-206 are ACCEPTED (2026-10-14, 01-adr-log) with the decisions Q1 to Q3 of section 12. The deltas of section 11 were applied on 2026-10-14 to 02, 06, 07, 08, 09, 10, 11 and 13 (together with ADR-200). Still open: 18 (package deltas), the ERD and the DK package 0.3.0.
Scope: how an input tax line is split into a deductible and a non-deductible part, the configuration that decides the share (fixed percentage, variable rate keys, a supplied cap), the line types, the effect on the document hash, returns, settlement, reversal and reads, and what the DK package gets from it.
Out of scope: adjustment of capital goods (investment goods) over several years, the year-end adjustment of a provisional pro rata rate (hook only, section 9), turnover calculation (the core never computes a pro rata rate), partial deduction on stock purchases built by order management (section 10), output VAT.

**Caution on law.** This spec defines mechanisms. The Danish code families in section 3 come from the official VAT code list (primary source for the code names and the deduction type) and from secondary sources for their meaning. Percentages and rules must pass the verification gate of 18, section 6 before a package is RELEASED.

## 1. Decisions taken (proposed)

| Topic | Decision | ADR |
|---|---|---|
| Share of deduction | `share = deductible_pct / 100 x key_rate / 100`, where `deductible_pct` is a fixed percentage on the tax code version (default 100) and `key_rate` comes from an optional **deduction key** (pro rata, sector, area, estimate) valued per registration and date | 203 |
| Caps | A code may require the caller to supply the deductible tax amount (leasing cars: the allowed amount comes from the leasing invoice). The core checks it against the computed tax | 203 |
| Posting shape | The deductible part is the tax line as today (VAT account, in the return). The non-deductible part is posted as **cost lines on the account and dimensions of the base lines** (new line type `TAX_NONDEDUCTIBLE`). Nothing is posted to a separate VAT cost account | 204 |
| Reverse charge | The output side is always the full tax. The split applies to the deductible (INPUT) side only. The rest becomes cost | 204 |
| Returns and settlement | Unchanged in structure: they sum tax lines. The return shows the deductible part, the output side of a reverse charge shows the full tax | 205 |
| Hash | New L1 columns on the tax detail and the allocation, a new line type. New `hash_version`. Old documents keep theirs | 204 |
| Limits in release 1 | No order-built stock purchases with partial deduction, no capital goods adjustment, no adjustment command (hook only) | 206 |

## 2. Why the old model was not enough

08, Q6 said: an input tax code is fully deductible, or a separate non-deductible code posts the tax to the expense. The official Danish code list has 41 purchase codes outside that:

| Deduction type in the list | Codes | Count |
|---|---|---|
| Fixed percentage | 50 percent (K040, K050), one third (K060, K330), 25 percent (K070, K080), two thirds (K610) | 7 |
| Pro rata (business with taxable and exempt activities) | K110, K240, K290, K440, K490, and domestic reverse charge K670. Combined with a fixed percentage: K050, K080 | 6 (+2) |
| Estimated (skøn) | K020, K220, K270, K420, K470, domestic reverse charge K680 | 6 |
| Area-based (real estate) | K130, K250, K300, K450, K500, K700 | 6 |
| Sector-based | K120 | 1 |
| Cap supplied (leasing, 25 percent of a special basis), also with pro rata | K090, K100, K310, K320 | 4 |
| No deduction, reverse charge | K230, K280, K430, K480, K690 | 5 |
| Special schemes without a claim (used goods, margin, used cars, leasing cars) and refund scheme | K620 to K650, K710, K720 | 6 |

The first six rows (30 codes) and the five no-deduction reverse-charge codes need this design. The last row stays outside (special schemes, refund), see 18 and the DK changelog.

## 3. Share model

### 3.1 New attributes of `tax_code_version` (V)

| Column | Type | Null | Notes |
|---|---|---|---|
| deductible_pct | RATE | N | Fixed share in percent, 0 to 100. Default 100. 0 means no deduction. Only on input codes: `tax_type` INPUT, including reverse-charge codes. An OUTPUT, EXEMPT or ZERO_RATED version with a value other than 100 is rejected (`CONFIG.DEDUCTION_ON_NON_INPUT_CODE`) |
| deduction_key | CODE | Y | Names a deduction key (3.2). Null: no variable rate |
| deduction_cap | CODE | N | NONE (default) or SUPPLIED_AMOUNT: the caller must send the deductible tax amount of the line (6.2) |

### 3.2 `vat_deduction_key` (M) and `vat_deduction_rate` (V)

A **key** is a named variable rate of a VAT registration. The core does not compute it, it is loaded like an exchange rate.

`vat_deduction_key`: PK (`tenant_id`, `company_id`, `registration_id`, `key_code`). Columns `kind` (PRO_RATA, ESTIMATE, SECTOR, AREA), `description`, `active`, `row_version`. Created by the country package (the key definitions) or by the company (the sector and area keys it needs). A key is IAP once a rate of it has been used.

`vat_deduction_rate` (V, insert only): PK (`tenant_id`, `company_id`, `registration_id`, `key_code`, `valid_from`). Columns `valid_to`, `rate_pct` (RATE, 0 to 100), `rate_status` (PROVISIONAL, FINAL), `basis_note` (TEXT 200, for example the period of the turnover used), `created_by`, `created_at`. Resolution: the row valid at the **tax date** of the line (08, 5.1). No row: the posting is rejected with `TAX.DEDUCTION_RATE_NOT_FOUND`. A rate used in a posted line cannot change: a correction is a new version from a later date. Retroactive corrections are an adjustment (section 9), not a rate edit.

### 3.3 Computation

For an input tax line (or the deductible side of a reverse-charge pair) with computed tax `T` (as today, ADR-027, in transaction currency, rounded per `tax_rounding_mode`):

1. `share = deductible_pct / 100 x key_rate / 100` (key_rate = 100 without a key). Intermediate precision 10 decimals, no rounding before step 3.
2. If `deduction_cap` = SUPPLIED_AMOUNT (leasing cars, where the allowed amount comes from the leasing invoice): let `S` be the supplied amount. It must satisfy `0 <= S <= ROUND(T x deductible_pct / 100)`, else `TAX.DEDUCTION_AMOUNT_EXCEEDS_LIMIT`. Then `D = ROUND(S x key_rate / 100)` (key_rate = 100 without a key, so a code with a cap and a pro rata key deducts the supplied amount times the pro rata rate). The stored `deduction_share` is the derived ratio `D / T` (informational). Otherwise: `D = ROUND(T x share)` at the scale of the transaction currency. (Corrected in the DK 0.3.0 work: the first draft let the cap bound the share, which cannot express "invoice amount times pro rata".)
3. `N = T - D`.

`D` is the amount of the tax line. `N` is posted as non-deductible cost (section 4). The amounts in LC, GC and RC are derived from the transaction currency amounts as for any line (ADR-057). The balance check runs in all four currencies as before.

## 4. Posting shape (ADR-204)

### 4.1 New line type `TAX_NONDEDUCTIBLE`

Added to the `line_type` list of 02, 5.1. A line of this type:

- is generated by the core, never sent by the caller;
- has the **account, the dimensions (typed and free) and the line text of the base line it belongs to**. It is exempt from the tax code rule of the account and carries no `tax_code_id`;
- has the sign of the base line (a cost is a debit);
- is linked to its tax line and base line through `journal_line_tax_base` (4.3);
- is mirrored by reversal like every line (ADR-048).

It satisfies the account rules the base line already satisfied (active, posting allowed, dimensions valid) because it uses the same account and values. A mandatory dimension rule therefore holds without extra input.

### 4.2 Without reverse charge

Purchase of 1,000.00 net, 25 percent, share 40 percent (50 percent x pro rata 80 percent), supplier invoice 1,250.00:

| Line | Type | Account | Amount |
|---|---|---|---|
| 1 | BASE | Expense | 1,000.00 |
| 2 | TAX (INPUT) | Input VAT | 100.00 (D) |
| 3 | TAX_NONDEDUCTIBLE | Expense (as line 1) | 150.00 (N) |
| 4 | BASE (control) | Payable | -1,250.00 |

The document balances (1,250.00 on each side). The return shows 100.00.

### 4.3 With reverse charge

EU services 1,000.00 net, 25 percent, share 60 percent, supplier invoice 1,000.00 without VAT:

| Line | Type | Account | Amount |
|---|---|---|---|
| 1 | BASE | Expense | 1,000.00 |
| 2 | TAX (OUTPUT side) | VAT on services bought abroad | -250.00 (full tax) |
| 3 | TAX (INPUT side) | Input VAT | 150.00 (D) |
| 4 | TAX_NONDEDUCTIBLE | Expense (as line 1) | 100.00 (N) |
| 5 | BASE (control) | Payable | -1,000.00 |

Debit 1,250.00, credit 1,250.00. The return shows 250.00 as output side and 150.00 as input VAT (08, 5.2 with the box mapping `line_side`). With no deduction (K230 style, `deductible_pct` 0) line 3 is a zero tax line (ADR-047) and line 4 carries the full 250.00.

### 4.4 Per-document rounding and several base lines

With `tax_rounding_mode` PER_DOCUMENT one input tax line covers several base lines (ADR-032). `T`, `D` and `N` are computed once per tax line. `N` is then **allocated** to the base lines by their `base_amount_tc` (largest remainder, ties to the lowest `line_no`), and one `TAX_NONDEDUCTIBLE` line is created per base line that receives a non-zero amount. With PER_LINE each base line has its own tax line and the allocation is trivial. A line with `N` = 0 gets no `TAX_NONDEDUCTIBLE` line.

### 4.5 Supplied expected tax

`tax_amount_tc` of a request line (06) stays the **full** tax of the supplier document (T). The existing check and the difference to the configured tax rounding account (ADR-027) apply to T, before the split. `D` and `N` are then computed from the posted T.

## 5. Table changes (deltas to 02)

### 5.1 `journal_line_tax`, new L1 columns

| Column | Type | Null | Notes |
|---|---|---|---|
| deductible_pct | RATE | N | From the version at posting. 100 for all other tax lines |
| deduction_key | CODE | Y | Key used, null without |
| deduction_key_rate | RATE | Y | Rate of the key valid at the tax date, copied |
| deduction_share | RATE | N | Effective share in percent (10 decimals), 100 when no partial deduction. On the OUTPUT side of a reverse-charge pair always 100 |
| deduction_cap_amount_tc | AMT | Y | The supplied amount when `deduction_cap` = SUPPLIED_AMOUNT |
| gross_tax_amount_tc | AMT | N | `T` in transaction currency. Equals the line amount when `deduction_share` is 100 |

### 5.2 `journal_line_tax_base`, new L1 columns

| Column | Type | Null | Notes |
|---|---|---|---|
| nondeductible_tax_tc | AMT | N | Part of `N` allocated to this base line. 0 when nothing is allocated |
| nondeductible_line_no | INT | Y | The `TAX_NONDEDUCTIBLE` line of this allocation, null when `nondeductible_tax_tc` is 0 |

The sum of `nondeductible_tax_tc` per tax line equals `gross_tax_amount_tc` minus the tax line amount in transaction currency (checked at P6).

### 5.3 Hash

The line hash already covers all L1 columns of `journal_line`, `journal_line_tax` and `journal_line_tax_base` (02, section 9). The new columns and the new line type are therefore covered once they are in the canonical field list. This needs a new `hash_version`: documents posted before keep their version and verify as before, `chains:verify` selects the field list by `hash_version`. No document is rewritten.

### 5.4 Other tables

`vat_return_tax_line` copies the deductible part (08, 6.5) and needs no change. The VAT reporting amounts of a tax line (08, 5.1a) are valued on the line's own amount, so for the INPUT side they are the deductible part. `gl_account`: the `tax_account` rule (08, 6.7) is unaffected because `TAX_NONDEDUCTIBLE` lines go to expense accounts.

## 6. Contract (deltas to 06)

### 6.1 Pipeline

| Phase | Addition |
|---|---|
| P4 | Resolve the share: load `deductible_pct`, `deduction_key`, `deduction_cap` of the version, look up the key rate at the tax date. Check `deductible_tax_amount_tc` rules |
| P5 | Compute `T`, `D`, `N`, the tax line, the `TAX_NONDEDUCTIBLE` lines and the allocation (3.3, 4.4) |
| P6 | Balance check in four currencies. Check of 5.2 |
| P7 | Insert lines and the new columns. Hash with the new `hash_version` |

### 6.2 New request field

`lines[].deductible_tax_amount_tc` (Opt): the deductible tax of the line, signed like `amount_tc`. **Required** for a code with `deduction_cap` = SUPPLIED_AMOUNT, **not allowed** for any other code. It is checked at P4 against the limit `ROUND(T x deductible_pct / 100)` of 3.3, and the deductible tax is `ROUND(value x key rate / 100)`.

### 6.3 Error codes

New in the area `TAX`: `DEDUCTION_RATE_NOT_FOUND` (no key rate at the tax date), `DEDUCTION_AMOUNT_REQUIRED`, `DEDUCTION_AMOUNT_NOT_ALLOWED`, `DEDUCTION_AMOUNT_EXCEEDS_LIMIT`. New in the area `CONFIG`: `DEDUCTION_ON_NON_INPUT_CODE`, `DEDUCTION_KEY_UNKNOWN`, `DEDUCTION_KEY_REFERENCED` (change of a used key). New in `ORDER`: `PARTIAL_DEDUCTION_NOT_SUPPORTED` (section 10).

Commands and permissions: `vat-deduction-keys:create`, `:update`, `:deactivate`, `vat-deduction-rates:create-version` with `write.config` (07, 11.1). VALIDATE mode shows the split lines.

## 7. Returns, settlement, reversal and reads (deltas to 08 and 09)

| Topic | Rule |
|---|---|
| Return boxes | Unchanged. A box total sums the VAT reporting amounts of the tax lines mapped to it. The input VAT box therefore shows the deductible part, the output side of a reverse-charge pair shows the full tax |
| Return snapshot | No new table. `vat_return_tax_line` copies the deductible tax line |
| Settlement | Unchanged (08, 7.3): it posts the opposite of the included tax line amounts on each VAT account. Non-deductible lines are cost and are not in the settlement |
| Replacement and supplementary returns | Unchanged |
| Reversal | The mirror copies every line including `TAX_NONDEDUCTIBLE` lines and the new columns 1:1 (ADR-048). A reversal never recomputes a share |
| Credit notes | A credit note reuses the code and posts its own split with the key rate valid at its own tax date. The share of the original is not inherited. If the rates differ, the difference is real and correct (the credit follows the rate of its period). Whether a country requires the original share is a package question (open, 18 verification) |
| EC list and OSS | Unaffected: they use bases of sales codes |
| Reads (09) | `line_type` gets the new value as a filter. `vat-positions:get` returns deductible and non-deductible tax per code and key. `journal-lines:query` can show the allocation through `journal_line_tax_base`. The SAF-T data set (12, 8) exports `TAX_NONDEDUCTIBLE` lines as ordinary expense lines |

## 8. Interaction with other domains

- **Clearing, open items:** the control account line carries the gross invoice amount, so clearing is unaffected.
- **Revaluation, year-end:** non-deductible lines are P&L lines, never revalued. Year-end closes them like any P&L line.
- **Intercompany:** the split is per company. An intercompany document has no input VAT by definition (ADR-016).
- **Migration (13):** migration documents carry legacy booked values (ADR-131) and no tax lines (VAT history is the `legacy_tax_summary`, 13, 3.6). `TAX_NONDEDUCTIBLE` lines therefore do not occur in them and no share is recomputed.
- **Retention, GDPR:** no personal data is added.
- **Account tax code rule (ADR-029):** a `TAX_NONDEDUCTIBLE` line is exempt, as system lines are.

## 9. Year-end adjustment of a provisional rate (hook only)

A pro rata rate is typically set provisionally and corrected when the final turnover is known (UNVERIFIED for DK, to be checked in the package verification). Release 1 does not include a command. The design keeps the door open without a schema change:

- every input tax line stores `gross_tax_amount_tc`, the applied key and key rate and the share, so a later command can recompute the deduction for a year;
- `vat_deduction_rate.rate_status` marks a FINAL rate;
- a later command `vat-deductions:adjust` (registration, key, fiscal year) would post a numbered, chained document with an adjustment tax line and cost lines, bind a run record through `detail_digest` (as revaluation, ADR-052) and report in the next return.

Until it exists, the adjustment is a manual correction document posted by the caller through `journal-documents:post` with explicit tax lines, and the package documents the procedure.

## 10. Order management and inventory (ADR-206)

An order-built purchase invoice for stock items matches the invoice to the receipt on the GR/IR account (11, 7). A non-deductible tax part posted on the base account would land on GR/IR and leave a residual, while the correct treatment is that it becomes part of the stock cost. Release 1 therefore **rejects** codes with `deductible_pct` below 100, a key, or a cap on order-built purchase invoice and credit note lines for stock and dropship items and on charges with treatment LANDED_COST (`ORDER.PARTIAL_DEDUCTION_NOT_SUPPORTED`, 11, 7.2 and 7.3). They are allowed on direct supplier invoices (`AP_INVOICE` posted through the journal API), on service and expense lines, on charges with treatment EXPENSE and on non-stock order lines. A later version decides whether the non-deductible part enters the cost layer.

## 11. Deltas to earlier specs (to apply after Q1 to Q3)

| Spec | Change |
|---|---|
| 02, 5.1 | `line_type` adds TAX_NONDEDUCTIBLE. Rate rule row for the new type (as BASE: TC non-zero, ADR-019 rates) |
| 02, 7.1 and 7.2 | New columns (section 5). Hash canonical field list and `hash_version` increment (02, 9) |
| 06, 3.1 and 8 | Request field `deductible_tax_amount_tc`, pipeline additions, error codes (6.3), permission map entries |
| 07 | `tax_code_version` new columns (3.1). New tables `vat_deduction_key` and `vat_deduction_rate` in 3.2 and 8.x, permission map (`write.config`), readiness check: a code with a key needs a rate at the first tax date (warning) |
| 08, Q6 | Replaced by this spec. 5.2 gets a cross-reference. 6.5 and 7.3 unchanged, noted |
| 09 | Filter and read additions (section 7). Resources `vat-deduction-keys`, `vat-deduction-rates` |
| 10, 11 | `ORDER.PARTIAL_DEDUCTION_NOT_SUPPORTED` on order-built stock invoice lines |
| 13 | No change (migration documents carry no tax lines). Note on `hash_version` of migrated documents |
| 18 | The DK package gains the codes of section 2 (0.3.0): attributes `deductible_pct`, `deduction_key`, `deduction_cap` per code, the keys PRO_RATA, ESTIMATE, SECTOR and AREA as package content, no rates (company input) |
| ERD | `vat_deduction_key`, `vat_deduction_rate`, new columns, new line type. **Applied 2026-10-19** (pages 1 and 6) |

## 12. Open questions

| # | Question | Decision (2026-10-14) |
|---|---|---|
| Q1 | Year-end adjustment command in release 1 (section 9)? | **No.** Hook only, manual correction document until the command is built |
| Q2 | Order-built stock purchases with partial deduction (section 10)? | **Not in release 1.** Reject with `ORDER.PARTIAL_DEDUCTION_NOT_SUPPORTED` |
| Q3 | Where does the non-deductible part go? | **On the account and dimensions of the base line** (section 4.1), not on a separate account |
| Q4 | Capital goods adjustment over several years | Out of scope, later |
| Q5 | Does a country require a credit note to follow the original share (7)? | Open. Verify in the DK package. Default: its own date |

## 13. Assumptions

| Ref | Item |
|---|---|
| A1 | The core never computes a key rate. Apps or the company load it, like exchange rates (ADR-019) |
| A2 | A key rate is valued at the tax date, not the posting date, so a late posting follows the VAT period it belongs to |
| A3 | The non-deductible part is cost on the base account for all input codes. No country-specific alternative is modelled |
| A4 | Rounding: D is rounded, N is the remainder, so T is always preserved |

## 14. ADRs raised by this spec (PROPOSED, full text in 01-adr-log)

**ADR-203: Deduction share from a fixed percentage and a variable key.** `deductible_pct`, `deduction_key` and `deduction_cap` on the code version, keys and key rates per registration.
**ADR-204: Non-deductible input VAT is cost on the base line account.** New line type `TAX_NONDEDUCTIBLE`, allocation to base lines, new L1 columns, new `hash_version`, reverse charge splits the deductible side only.
**ADR-205: Returns and settlement sum tax lines unchanged.** The deductible part is the tax line, the full output side of a reverse charge stays.
**ADR-206: Release 1 limits.** No order-built stock purchases with partial deduction, no capital goods adjustment, no adjustment command (hook through stored share, key and gross tax).
