# Finance Core: VAT, Table Specs and Contract

Status: DRAFT v0.6 for review (propagation pass of 2026-10-13, decisions of 17, sections 4 and 5, see 11.3). v0.5 (fix pass 4: X8, B9, F1, F2, unused VAT codes, stale notes, one VAT_RETURN chain per company). Builds on 00-design-principles v0.4, ADR-001 to ADR-174 and specs 02 to 07. Main references: ADR-026 (determination), ADR-027 (tax lines), ADR-028 (return data and settlement), ADR-047, ADR-072, P15. Revised on 2026-10-13 for the scope review (14, sections 8 and 9): D9 (settlement reversal only before filing, ADR-153), K1 (one `chain_head` table, ADR-158), wording fixes (section 11, deltas). Second re-baseline (14, section 10): one VAT_RETURN chain per company over all return types, new settlement reversal command (7.3a), Q13 closed. Third re-baseline (14, section 11): the command is renamed `vat-returns:reverse-settlement` (permission `vat.settle`), new event `vat.settlement_reversed`, error `VAT.NOT_SETTLED` confirmed, Q14 closed.
Scope: VAT registrations and VAT periods, tax point, the scenario model (domestic, reverse charge, intra-EU B2B, OSS/IOSS, import and export), VAT evidence references, frozen VAT return snapshots (box returns, EC Sales List, OSS and IOSS returns), filing record, VAT settlement posting.
Out of scope: return filing and file formats (apps, ADR-028), SAF-T generation (compliance domain), customs processing (declarations, duties, customs value, deferment accounts: inventory and order management, Q7), cash accounting (Q11), VAT ID validation services, capital goods adjustment, the year-end adjustment of a provisional deduction rate (19, 9). Partial and non-deductible input VAT is **in scope since 2026-10-14** (ADR-201 to ADR-206), specified in 19-partial-input-vat-deduction.
Conventions, lock classes and types as in 02, section 1. Pattern letters (V, M, E, R) as in 07, section 2.

**Caution on tax law.** This spec defines how VAT facts are stored, determined and reported. It does not state tax rules of any country. Scenario descriptions in section 3 are indicative and must be verified per member state and country package before use (P15, 07 Q10).

## 1. Decisions taken

| Topic | Decision |
|---|---|
| Scope | Domestic VAT, intra-EU B2B, OSS/IOSS, import and export, all as rules and codes on the existing determination and tax-line model, with the additions below |
| Return data | A command creates an **immutable, hashed return snapshot** per VAT registration, return type and VAT period. Apps file from it, settlement references it, corrections are new snapshots |
| VAT period | Per VAT registration, built from fiscal periods (1, 3, 6 or 12 months, aligned to the fiscal calendar) |
| Tax point | An explicit `tax_date` (L1) on each tax line, default the document date |
| Corrections | A `correction_method` per registration: ADJUST_NEXT, SUPPLEMENTARY or REPLACEMENT. Country packages choose, nothing is hard-wired |
| VAT currency | Tax lines carry separate VAT reporting amounts, valued with a configurable rate type per registration (default: the booking rate). Returns sum these amounts |
| Customs | The core stores VAT treatment and evidence references only. Customs data belongs to inventory and order management |
| VAT accounts | Accounts used by tax codes or the settlement role accept only tax lines and settlement lines (`tax_account` flag) |
| Partial deduction | An input tax line is split into the deductible part (the tax line, in the return) and the non-deductible part (`TAX_NONDEDUCTIBLE` cost lines on the base accounts). The share is a fixed percentage times an optional deduction key rate. Returns, settlement and reversal sum and mirror the tax lines unchanged (ADR-203 to ADR-205, 19) |
| EU scenarios | Determination rules can use the derived keys `origin_eu` and `destination_eu` (EU membership at the tax date), so no rule per member state is needed (ADR-200, 07, 8.4) |

## 2. Table overview

| # | Table | Pattern | Role |
|---|---|---|---|
| 1 | `company_vat_registration` | M | A company's VAT registration: country, VAT id, scheme, filing frequency |
| 2 | `journal_line_tax_evidence` | L2 rows | Evidence references for a tax line (customs declaration, proof of export, transport document) |
| 3 | `vat_return` | E + S | Header of a frozen return snapshot |
| 4 | `vat_return_line` | E | Result rows of a snapshot (box, EC list or OSS rows) |
| 5 | `vat_return_tax_line` | E | The tax lines included in a snapshot (drill-down, one report per line and return type) |
| 6 | ~~`vat_return_chain_head`~~ | | Removed (14 K1, ADR-158). The head of the VAT return chain is a row of the shared `chain_head` table (6.6) |

Changes to earlier specs (section 11): `journal_line_tax.tax_date`, `tax_code` and `tax_code_version` attributes, `tax_return_box.return_type`, `tax_code_version_box.line_side`, `account_role_assignment.vat_registration_id`, `chain_anchor` chain type VAT_RETURN.

## 3. Scenario model

Everything below is expressed with the tax determination keys (07, 8.4: transaction type, origin, destination, partner VAT status, account, tax class), code attributes and return mappings. No scenario-specific tables exist. The catalogue is indicative. The actual rules, codes and boxes come from country packages and must be verified per country.

| Scenario | Determination keys (typical) | Tax code attributes | Reported in |
|---|---|---|---|
| Domestic sale or purchase | Origin = destination = company country | OUTPUT or INPUT, rate | Standard return boxes |
| Domestic reverse charge | Domestic, partner registered, class marks the supply | `reverse_charge`, counter account | Boxes for both sides |
| Intra-EU B2B supply of goods | Sales, origin company country, destination another EU country, partner REGISTERED there | ZERO_RATED, `ec_list_category` = GOODS | Standard return box, EC Sales List |
| Intra-EU B2B supply of services | As above, class = service | ZERO_RATED or out-of-scope style code, `ec_list_category` = SERVICES. Customer side typically reverse charge | Box, EC Sales List |
| Intra-community acquisition of goods | Purchase, origin another EU country, partner registered | `reverse_charge`, INPUT/OUTPUT pair | Acquisition boxes |
| Triangulation | Intra-EU keys plus class marks a triangular supply | ZERO_RATED, `ec_list_category` = TRIANGULATION | EC Sales List |
| B2C distance sales (OSS) | Sales, destination another EU country, partner NOT_REGISTERED | OUTPUT, destination country's rate, registration scheme OSS | OSS return by consumption country and rate |
| Import from outside the EU, VAT paid at customs | Purchase, origin non-EU | INPUT. Customs authority as partner | Standard return, evidence: customs declaration |
| Import with postponed accounting | As above | `reverse_charge` pair | Standard return |
| Import of low-value consignments (IOSS) | Sales to consumers, origin non-EU, scheme IOSS | OUTPUT per destination, scheme IOSS | IOSS return |
| Export outside the EU | Sales, destination non-EU | ZERO_RATED | Standard return, evidence: proof of export |
| Drop shipment | Per line origin and destination differ from company and partner country | By the keys above, per line | By line |

Customs data (declarations, duties, customs value, deferment) is out of the finance core (Q7). The core stores references as evidence.

### 3.1 Determination rules (F1, 17 section 4)

The keys of the table above are resolved by the determination of 07, 8.4. These rules complete it for VAT (the text of 07, 8.4 and the 06 rejection codes are aligned in their own passes):

- **Tie-break.** If several rules match a line, the rule with the explicit `priority` wins first (the better `priority`; the determination rule keeps its `priority` column, 07, 8.4). If priorities are equal, the rule with the highest specificity (the number of non-null keys) wins. `rule_id` is the **last tie-break**: the lowest `rule_id` wins among equal priority and specificity (ADR-192, 17 section 5). The result is deterministic and never depends on insertion order.
- **Partner VAT status.** The status is evaluated against the destination country of the supply. A partner registration that is absent, inactive or expired at the `tax_date` in that country gives the status NOT_REGISTERED in the destination country. There is no undefined status.
- **Partnerless documents.** A document without a partner (no partner id on any line) must supply explicit origin and destination country on the header or the line. Otherwise it is rejected in P4 (new code `TAX.ORIGIN_DESTINATION_MISSING` in the error area `TAX` of 06, to be added there). The core does not guess them from the company country.
- **EU flags (ADR-200).** `origin_eu` and `destination_eu` are true when the resolved origin or destination country has a membership row in `ref_country_eu_membership` valid at the `tax_date`, else false. They are rule keys (07, 8.4) and are stored on the tax line (02, 7.1). The company's own country is a member (or not) like any other: an intra-EU rule that must not apply to domestic supplies needs a domestic rule with explicit countries and a lower `priority` value, as the DK package does (18). For a purchase from another EU country the partner VAT status is not a useful key, because it is evaluated in the destination country (the company's), where a foreign supplier is NOT_REGISTERED: such rules use `origin_eu` and the tax class.

## 4. Registrations and VAT periods

### 4.1 `company_vat_registration` (M)

PK: (`tenant_id`, `company_id`, `registration_id`). Unique (`tenant_id`, `company_id`, `country_code`, `scheme`, `valid_from`).

| Column | Type | Null | Notes |
|---|---|---|---|
| country_code | CODE | N | IAP. Country of registration. For OSS: the member state of identification |
| vat_id | TEXT(30) | N | |
| scheme | CODE | N | IAP. STANDARD, OSS_UNION, OSS_NON_UNION, IOSS |
| filing_frequency | CODE | N | MONTHLY, QUARTERLY, HALF_YEARLY, YEARLY |
| ec_list_frequency | CODE | Y | MONTHLY or QUARTERLY if the registration files an EC Sales List, else null |
| correction_method | CODE | N | ADJUST_NEXT, SUPPLEMENTARY, REPLACEMENT (7.1). Changes apply to later preparations only |
| return_currency | CODE | N | Currency of the return amounts (company local currency, or the reporting currency, e.g. EUR for OSS) |
| vat_rate_type | CODE | N | BOOKING (default) or VAT. BOOKING: the reporting amounts are derived from the posted amounts. VAT: converted with the rate type `VAT` of the rate table at the tax date (5.4) |
| valid_from, valid_to | DATE | N, Y | |
| status | CODE | N | ACTIVE, INACTIVE |

A company has one or more registrations. A tax code belongs to exactly one registration (07, tax code delta), and a tax line therefore belongs to that registration. A company in DRAFT status needs at least one registration to use VAT codes.

### 4.2 VAT period

A VAT period of a registration is a run of consecutive fiscal periods, built from `filing_frequency`: MONTHLY = 1 fiscal period, QUARTERLY = 3 (periods 1-3, 4-6, ...), HALF_YEARLY = 6, YEARLY = all regular periods. The key is (`registration_id`, `fiscal_year`, `vat_period_no`). Boundaries follow the fiscal calendar and a short first fiscal year gives a shorter first VAT period. Special periods belong to the last regular period's VAT period. The EC list period follows `ec_list_frequency` in the same way.

## 5. Tax line additions

### 5.1 `tax_date` on `journal_line_tax`

New column `tax_date` DATE, not null, L1 (delta to 02). Resolution: the line's supplied `tax_date`, else the document header's `tax_date`, else `document_date`. The VAT period of a tax line is the VAT period of its registration that contains `tax_date`. The date is stored on the tax line and part of the line hash.

### 5.1a VAT reporting amounts on `journal_line_tax`

New L1 columns (delta to 02):

| Column | Type | Null | Notes |
|---|---|---|---|
| vat_currency | CODE | N | `return_currency` of the registration at posting |
| vat_base_amount | AMT | N | Base amount of the tax line in `vat_currency` |
| vat_tax_amount | AMT | N | Tax amount in `vat_currency` |
| vat_rate | RATE | N | Conversion rate from the transaction currency to `vat_currency` used for the two amounts. 1 if the currencies equal |
| vat_rate_date | DATE | Y | Date of the rate row used. Null for BOOKING |

Valuation (Q4 decided):
- `vat_rate_type` = BOOKING: the amounts equal the posted LC amounts of the tax line when `vat_currency` is the local currency, or the RC amounts when it is the reporting currency. `vat_rate` is the booking rate stored on the line.
- `vat_rate_type` = VAT: the rate is looked up in the tenant rate table with `rate_type` = VAT, at `tax_date`, with the derivation and fallback rules of 07, 7.2. Amounts are `ROUND(base_tc x vat_rate)` and `ROUND(tax_amount_tc x vat_rate)` at the scale of `vat_currency`. A missing rate rejects the posting with `RATE.RATE_NOT_FOUND`.
- Reporting amounts are not ledger amounts and need not balance. The difference to the ledger is visible by comparing them with the posted LC amounts. A new rate type `VAT` is added to ADR-019's list (SPOT, CLOSING, VAT).
- Returns (6.4, 6.5) always sum the reporting amounts, never the ledger amounts.
- The reporting amounts of a tax line are valued on the line's own amount: with partial deduction the INPUT side line carries the deductible part, and the non-deductible part is cost and has no reporting amount (ADR-205).

### 5.2 Reverse charge

A tax code version with `reverse_charge` = true produces **two** tax lines for each base line group, both linked to the same base lines (`journal_line_tax_base`):

| Line | tax_type | Account | Amounts |
|---|---|---|---|
| Output side | OUTPUT | `vat_account_id` of the version | Credit (VAT payable) |
| Deductible side | INPUT | `reverse_charge_account_id` of the version | Debit (VAT receivable) |

Both have the same rate, base and tax amounts with opposite signs, so the document stays balanced and the partner's control account line is unaffected (the partner invoice carries no VAT). The version's box mappings carry a `line_side` (PRIMARY or COUNTER) so each side maps to its own boxes.

**With partial deduction (ADR-204, 19, 4.3).** The output side is always the full tax. The split applies to the deductible (INPUT) side only: its tax line carries the deductible part, the rest is posted as `TAX_NONDEDUCTIBLE` cost lines on the base line accounts. With no deduction the deductible side is a zero tax line (ADR-047) and the full tax becomes cost. The return shows the full output side and the deductible input side.

### 5.3 `journal_line_tax_evidence` (L2 rows)

Evidence arrives after posting (proof of export, customs declaration number), so it is changeable data attached to a tax line.
PK: (`tenant_id`, `company_id`, `document_id`, `line_no`, `evidence_no`)  FK to `journal_line_tax`. `line_no` is the line number of the tax line and is part of the evidence key everywhere evidence is addressed (the `TAX_EVIDENCE` entity key of `change-open-fields` is (`document_id`, `line_no`, `evidence_no`)).

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| evidence_no | INT | N | L1 | |
| evidence_type | CODE | N | Append-only | CUSTOMS_DECLARATION, PROOF_OF_EXPORT, TRANSPORT_DOCUMENT, PARTNER_VAT_CHECK, OTHER |
| reference | TEXT(100) | N | Append-only | Declaration or document number |
| evidence_date | DATE | Y | Append-only | |
| note | TEXT(200) | Y | Append-only | |

The four evidence fields (`evidence_type`, `reference`, `evidence_date`, `note`) and `line_no` are added to the `TAX_EVIDENCE` entity type of `change-open-fields` (06 delta). **Evidence is append-only (F2).** An evidence row is added through `change-open-fields` and is never changed or deleted afterwards. A correction is a further row (next `evidence_no`) that refers to the corrected fact in its `note`, all rows stay visible. Adding evidence is allowed in every period status including HARD_CLOSED, as a **controlled exception** to the hard-close rule (L3): it never changes a posted amount, a hash or a return snapshot. Each addition writes its own audit event `vat.evidence_added` (15, section 6, `is_audit` = true, references only: `document_id`, `line_no`, `evidence_no`) and a `change_log` row (entity type `TAX_EVIDENCE`, old value null). Compare-and-set is on the document `expected_version` as for other L2 entities. The evidence is not part of the hash. Tax code versions may mark evidence as required for a scenario (`evidence_required_type`, section 6.2): `vat-returns:prepare` reports lines lacking it as warnings (Q10).

## 6. Return snapshots

### 6.1 Return types

| return_type | Registration scheme | Structure of `vat_return_line` rows |
|---|---|---|
| `STANDARD` | STANDARD | One row per return box (`BOX`): base and tax totals |
| `EC_LIST` | STANDARD with `ec_list_frequency` | One row per (partner, country, category) (`EC_LIST`): total base |
| `OSS` | OSS_UNION, OSS_NON_UNION | One row per (consumption country, rate, supply kind) (`OSS`) |
| `IOSS` | IOSS | As OSS |

### 6.2 Configuration additions (deltas to 07)

| Table | Change |
|---|---|
| `tax_code` | New column `vat_registration_id` (FK `company_vat_registration`, IAP) |
| `tax_code_version` | New columns `reverse_charge_account_id` (required when `reverse_charge`), `ec_list_category` (GOODS, SERVICES, TRIANGULATION, null), `supply_kind` (GOODS, SERVICES, null: for OSS rows), `evidence_required_type` (null or an evidence type) |
| `tax_return_box` | New key column `return_type`: PK (`tenant_id`, `country_code`, `return_type`, `box_code`) |
| `tax_code_version_box` | New key column `line_side` (PRIMARY or COUNTER) |
| `account_role_assignment` | New optional key `vat_registration_id`. Role `VAT_SETTLEMENT` per registration |

### 6.3 `vat_return` (E, with S columns)

PK: (`tenant_id`, `company_id`, `return_id`)
Unique: (`tenant_id`, `company_id`, `registration_id`, `return_type`, `fiscal_year`, `vat_period_no`, `return_seq`)
Unique: (`tenant_id`, `idempotency_key`)
Unique: (`tenant_id`, `company_id`, `chain_seq`) (the chain is one per company over all return types, 6.6, 14 section 10)

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| tenant_id, company_id, return_id | ID | N | L1 | |
| registration_id | ID | N | L1 | |
| return_type | CODE | N | L1 | |
| fiscal_year, vat_period_no | INT, SMALLINT | N | L1 | |
| period_start_date, period_end_date | DATE | N | L1 | |
| return_kind | CODE | N | L1 | ORIGINAL, SUPPLEMENTARY or REPLACEMENT (7.1) |
| return_seq | INT | N | L1 | 1 for the original, +1 per further return of the same period |
| replaces_return_id | ID | Y | L1 | REPLACEMENT only: the return being replaced |
| superseded_by_return_id | ID | Y | S | Set when a REPLACEMENT supersedes this return. Outside the hash |
| prepared_at, prepared_by | TS, TEXT(100) | N | L1 | |
| correlation_id, idempotency_key | TEXT(100) | N | L1 | |
| tax_line_count | INT | N | L1 | Rows in `vat_return_tax_line` |
| late_line_count | INT | N | L1 | Lines with `tax_date` before the period start (late) |
| snapshot_digest | BINARY(32) | N | L1 | SHA-256 over `vat_return_line` and `vat_return_tax_line` in fixed order |
| chain_seq | BIGINT | N | L1 | Gapless per (tenant, company), one chain over all return types and registrations |
| prev_hash, row_hash, hash_version | BINARY(32), BINARY(32), SMALLINT | N | L1 | Chain, same scheme as 02, section 9 (Q3) |
| filing_reference | TEXT(100) | Y | S | Set once by `vat-returns:record-filing` |
| filed_at, filed_by | TS, TEXT(100) | Y | S | |
| settled_document_id | ID | Y | S | The VAT settlement document, FK `journal_document` |

### 6.4 `vat_return_line` (E)

PK: (`tenant_id`, `company_id`, `return_id`, `line_no`).

| Column | Type | Null | Notes |
|---|---|---|---|
| row_type | CODE | N | BOX, EC_LIST, OSS |
| box_code | CODE | Y | BOX |
| partner_id | ID | Y | EC_LIST |
| partner_vat_id | TEXT(30) | Y | EC_LIST. Copied from the partner's registration at preparation (Q9) |
| country_code | CODE | Y | EC_LIST: partner country. OSS: consumption country |
| category | CODE | Y | EC_LIST: GOODS, SERVICES, TRIANGULATION |
| rate | RATE | Y | OSS |
| supply_kind | CODE | Y | OSS |
| currency_code | CODE | N | `return_currency` of the registration |
| base_amount, tax_amount | AMT | N | Sums of the VAT reporting amounts of the included tax lines (5.1a), signed by the box mapping |
| line_count | INT | N | Number of tax lines in the row |
| legacy_base_amount, legacy_tax_amount | AMT | N | Part of `base_amount` and `tax_amount` that comes from migrated legacy tax summary rows (13), else 0. The row's drill-down covers only the core part |

Check constraints by `row_type` make the key columns of the other types null.

### 6.5 `vat_return_tax_line` (E)

PK: (`tenant_id`, `company_id`, `return_id`, `document_id`, `line_no`)  (`line_no` = the tax line)
Unique (partial): (`tenant_id`, `company_id`, `registration_id`, `return_type`, `document_id`, `line_no`) where `is_current` = true: a tax line is currently reported at most once per registration and return type.

| Column | Type | Null | Lock | Notes |
|---|---|---|---|---|
| registration_id, return_type | ID, CODE | N | L1 | Copied for the unique constraint |
| tax_date | DATE | N | L1 | Copy |
| is_late | BOOL | N | L1 | `tax_date` before the period start |
| tax_code_version_id | ID | N | L1 | Copy, the mapping applied |
| vat_base_amount, vat_tax_amount | AMT | N | L1 | Copy of the VAT reporting amounts of the tax line (5.1a), signed as posted |
| is_current | BOOL | N | S | True while the return is not superseded. Set to false for all rows of a return when a REPLACEMENT supersedes it. Outside the hash |

### 6.6 Chain head (`chain_head`, K1)

The head of the VAT return chain is a row of the shared `chain_head` table (ADR-158): key (`tenant_id`, `company_id`, `chain_type` = VAT_RETURN, `scope_key` empty), columns `last_seq`, `last_hash`, `hash_version`, `updated_at` (all S). The table is defined with the other chain heads (02, 9.2). There is one VAT_RETURN chain per company over all return types and registrations (14, section 10): `scope_key` is empty and `chain_seq` is gapless per company. `vat-returns:prepare` locks the row in the order of 04, 6.3 as amended (counters before heads, chain heads sorted by the tuple chain type, company, scope key; X4 of 17, D3, ADR-147). Anchored through `chain_anchor` with `chain_type` = VAT_RETURN and `scope_key` (02, 9.5). The table `vat_return_chain_head` no longer exists.

### 6.7 VAT accounts accept only tax logic (Q12 decided)

New column on `gl_account` (S, delta to 07): `tax_account` BOOL. The system sets it to true when an account becomes the `vat_account_id` or `reverse_charge_account_id` of a tax code version, or the account of a `VAT_SETTLEMENT` role. It is never set back to false. A line on a `tax_account` account is accepted only if it is a TAX line with tax detail, a line of a VAT_SETTLE document, or a mirror line of a reversal of those. Anything else is rejected in P4 with `ACCOUNT_RULE.TAX_ACCOUNT_DIRECT_POSTING`. Manual VAT corrections are posted as documents with tax lines and the right code. (Opening-balance migration is a separate topic and will need a controlled exception.)

### 6.8 Hash

`row_hash = H( hash_version | tenant_id | company_id | chain_seq | prev_hash | L1 columns of vat_return | snapshot_digest )`. S columns (`filing_reference`, `filed_*`, `settled_document_id`) are outside the hash. Snapshot lines are covered through `snapshot_digest`.

## 7. Commands

All commands use the envelope, idempotency, error model and VALIDATE mode of 06. New error area `VAT`: `NO_REGISTRATION`, `REGISTRATION_INACTIVE`, `PERIOD_NOT_COMPLETE`, `PREVIOUS_PERIOD_NOT_PREPARED`, `ALREADY_PREPARED`, `SUPERSEDED`, `NOTHING_TO_REPORT`, `ALREADY_FILED`, `ALREADY_SETTLED`, `NOT_SETTLED`, `SETTLEMENT_DATE_NOT_POSTABLE`, `EVIDENCE_MISSING` (warning).

Use of the codes (17, section 2, unused VAT codes): `VAT.NO_REGISTRATION`: `vat-returns:prepare`, `:preview` and `vat-positions:get` name a registration that does not exist for the company, or none is valid in the VAT period. `VAT.REGISTRATION_INACTIVE`: the same commands and `vat-returns:settle` name a registration whose `status` is INACTIVE (reads of existing snapshots stay allowed). `VAT.NOT_FILED` is **dropped**: no command requires a filed return (settlement does not depend on filing, A5, and the reversal requires the opposite, 7.3a). The code is removed from this list and from the `VAT` list of 06 (06 is aligned in its own pass).

### 7.1 `vat-returns:prepare`

| Field | Req | Notes |
|---|---|---|
| registration_id | Req | |
| return_type | Req | Must match the registration's scheme (6.1) |
| fiscal_year, vat_period_no | Req | |

Selection of tax lines: all tax lines of tax codes of the registration that (a) have `tax_date` on or before the period end date, (b) are not currently in a snapshot of the same registration and return type (`is_current` = true), (c) for `EC_LIST`, have a tax code version with `ec_list_category`. Lines with `tax_date` before the period start are included and marked late. `OSS` and `IOSS` select by scheme.

**Legacy tax summary rows (migration, 13, 3.6).** For the VAT period that contains a migration cutover date, `legacy_tax_summary` rows of the registration and return type that are not yet reported (`reported_in_return_id` null) are included in the snapshot: their base and tax amounts are added to the matching result rows and shown separately in `legacy_base_amount` and `legacy_tax_amount` of those rows (6.4), they are covered by `snapshot_digest`, and `reported_in_return_id` is set in the same transaction. They have no drill-down to tax lines. Settlement (7.3) includes the legacy ledger amounts per VAT account (`ledger_lc/gc/rc`) with the settlement of the tax lines, which clears the migrated opening balances on the VAT accounts.

The registration's `correction_method` decides what a second preparation for an already prepared period means (Q2 decided):

| correction_method | Preparing the same period again | Late and corrected lines |
|---|---|---|
| ADJUST_NEXT | Rejected with `VAT.ALREADY_PREPARED` | Reported as late lines in the next ordinary return |
| SUPPLEMENTARY | Creates a SUPPLEMENTARY snapshot with only the lines not yet reported (including late ones for that period) | Reported in the supplementary snapshot, or as late lines of the next period if no supplementary is prepared |
| REPLACEMENT | Creates a REPLACEMENT snapshot of the whole period: all tax lines of the latest current snapshot plus all not yet reported lines with `tax_date` on or before the period end. The replaced snapshot gets `superseded_by_return_id` and its tax line rows `is_current` = false | Included in the replacement. Lines already reported in a later period's return stay there |

The first preparation of a period is always an ORIGINAL. A REPLACEMENT replaces the latest current snapshot of the period, so a replaced snapshot is never replaced twice.

Pipeline content: P2 registration and period resolve. P3 every fiscal period in the VAT period is at least SOFT_CLOSED (Q1, `VAT.PERIOD_NOT_COMPLETE`). P4 the previous VAT period of the same registration and return type has an ORIGINAL snapshot (`VAT.PREVIOUS_PERIOD_NOT_PREPARED`), unless this is the first. P5 aggregation by the version's box mapping (`tax_code_version_box`, side and sign), by EC list key, or by OSS key. P6 `VAT.NOTHING_TO_REPORT` if nothing is selected and the kind is SUPPLEMENTARY. An empty ORIGINAL is allowed (a nil return). Warnings: `VAT.EVIDENCE_MISSING` per line lacking required evidence.

P7: lock the `chain_head` row (VAT_RETURN, `scope_key` empty) in the lock order of 04, 6.3 as amended (sort tuple chain type, company, scope key), insert header, lines, tax lines, update the head and `command_log`, write the outbox events (15, 5.1: `vat.return_prepared`). Result: the full snapshot (header, lines, counts) in one response.

### 7.2 `vat-returns:record-filing`

Payload: `return_id`, `filing_reference`, `filed_at`. Sets the S columns once. A second call is `VAT.ALREADY_FILED`. Filing itself happens outside the core. This only records the receipt. No command reverses or undoes a filing (ADR-080, ADR-153). Permission `vat.file`. Emits `vat.return_filed` (15).

### 7.3 `vat-returns:settle`

Payload: `return_id`, `posting_date` (optional, default the first postable date on or after the period end). Replaces the `vat:settle` of ADR-028 (same behaviour, same blocking rule). Only for return types with amounts (`STANDARD`, `OSS`, `IOSS`).

Posts one VAT_SETTLE document (system-generated, numbered, chained, `detail_digest` = the snapshot's `snapshot_digest`):

| Line | Account | Amounts |
|---|---|---|
| One per VAT account used in the snapshot (and side) | The version's `vat_account_id` or `reverse_charge_account_id` | Opposite of the sum of the included tax line ledger amounts on that account, in LC, GC, RC |
| Balancing line | Role `VAT_SETTLEMENT` of the registration | Net payable or receivable |

Settlement uses the posted ledger amounts of the tax lines, so the VAT accounts clear exactly. The non-deductible cost lines of partial deduction (`TAX_NONDEDUCTIBLE`) are not tax lines and are not part of the settlement (ADR-205). Where reporting amounts differ from ledger amounts (`vat_rate_type` = VAT), the difference stays visible as the difference between the return and the settled balance, and is not posted.

Settlement of a REPLACEMENT (B9, 17 section 4): the settlement posts the per-account **difference** between the replacement and the **last settled snapshot along the `replaces_return_id` chain**, that is the nearest return reached by following `replaces_return_id` backwards that has `settled_document_id` set (a return whose settlement was reversed has it cleared and does not count). The net effect on the VAT accounts equals one settlement of the replacement, however many replacements and reversals lie in between, so nothing is settled twice. If no return of the chain is settled, the replacement settles in full. An unsettled superseded snapshot can no longer be settled (`VAT.SUPERSEDED`). A SUPPLEMENTARY settles its own lines.

Rules: not already settled (`VAT.ALREADY_SETTLED`). The posting date must be postable (period open or soft-closed with permission), `VAT.SETTLEMENT_DATE_NOT_POSTABLE` otherwise (ADR-028: blocked for hard-closed periods). The document goes through the normal posting pipeline P3 to P7. Sets `vat_return.settled_document_id` (S).

**Reversal of a settlement (D9, D1).** A VAT_SETTLE document is a derived document: `journal-documents:reverse` rejects it (ADR-145). It is reversed only through `vat-returns:reverse-settlement` (7.3a) and only **before** `record-filing`. No command reverses a filing, so a filed settlement is never reversed. After filing, corrections go through the registration's correction method and produce a new settlement or a settlement difference (Q5, ADR-080, ADR-153).

### 7.3a `vat-returns:reverse-settlement`

**This section is the single contract** of the command (X8, 17 section 4). 06 refers to it and carries no payload of its own.

| Field | Req | Notes |
|---|---|---|
| return_id | Req | The return whose settlement is reversed. The settlement document is found through `vat_return.settled_document_id` |
| reversal_date | Req | The posting date of the mirror document. **No date shifting**: the date is used as given. If it is not postable (period closed for posting or hard-closed), the command is rejected with `VAT.SETTLEMENT_DATE_NOT_POSTABLE` and the caller supplies another date |
| reason_code | Req | `CORRECTION` or `ERROR` only (the closed list for external callers, ADR-145). The system reason codes (AUTO_REVERSAL, RERUN, REOPEN, RETURN) are not available here |
| reason_text | Req | Free text, no personal data |

Permission `vat.settle`. Envelope, idempotency and VALIDATE mode as in 06.

- Rejected with `VAT.ALREADY_FILED` if the return is filed (`filed_at` set). Rejected with `VAT.NOT_SETTLED` (confirmed, 14 section 11) if `settled_document_id` is null.
- **Superseded return (B9).** Rejected with `VAT.SUPERSEDED` if the return is superseded (`superseded_by_return_id` set) and a successor along the `replaces_return_id` chain has an active settlement (`settled_document_id` set). The successor's settlement is reversed first. A superseded return whose successors are not settled can be reversed.
- Posts the mirror of the VAT_SETTLE document through the normal posting pipeline on `reversal_date`, with `reversal_of_document_id` and the reason code (D1). The mirror keeps the `document_type` VAT_SETTLE.
- Clears `vat_return.settled_document_id` (S) in the same transaction, so the snapshot is no longer settled and can be settled again with `vat-returns:settle` (the `VAT.ALREADY_SETTLED` check then passes). This releases the snapshot's settlement.
- Writes the outbox event `vat.settlement_reversed` (15, section 6, `is_audit` = true, audit view type VAT_SETTLEMENT_REVERSED in 09, 4.8). It is a new event type, not `vat.settled` with a flag.

### 7.4 Reads

| Operation | Notes |
|---|---|
| `vat-returns:get`, `:list` | Header, lines, tax lines. Drill-down from a row to documents through `vat_return_tax_line` |
| `vat-returns:preview` | Same as prepare, VALIDATE mode. Not stored |
| `vat-positions:get` | Live totals for a registration and period from unreported tax lines. Read API detail belongs to the read-API domain |

## 8. Interaction with other domains

- **Reversal:** a reversed document's tax lines mirror 1:1 (ADR-048) with `tax_date` copied. If the original was already reported, the reversal is an unreported line and reaches the next return as a late negative line. If neither is reported, both appear in the same return and net to zero.
- **Clearing, revaluation, year-end:** not affected. Tax lines carry no open-item row and are not revalued. The year-end close does not move VAT accounts (they are balance sheet accounts).
- **Retention (P8):** snapshots are legal records, kept for the statutory period of the company and country, and anonymised with the other legal records. EC list rows hold `partner_vat_id` copies for that reason (Q9).
- **Hash chain:** `vat_return` is one of the six chains of a company (LEDGER, CLEARING, PERIOD_EVENT, VAT_RETURN, STOCK, FULFILMENT), one chain per company over all return types, head in `chain_head` (6.6). Anchors cover all chains.

## 9. Assumptions

| Ref | Item |
|---|---|
| A1 | Reported amounts are the VAT reporting amounts of the tax lines (5.1a), valued per registration with the booking rate or the rate type VAT. Which rate a country prescribes is configuration, to verify per country (P15) |
| A2 | Tax codes belong to one registration. A company with two registrations has separate codes per registration |
| A3 | One return type per snapshot. A registration that files both a standard return and an EC list prepares two snapshots, each with its own `return_seq` per period. Both are in the company's single VAT_RETURN chain |
| A4 | The core never computes VAT liability from account balances. The return is built from tax lines only, so postings made directly to a VAT account without tax detail would be in no return. Direct postings are therefore rejected by the `tax_account` rule (section 6.7, Q12 decided) |
| A5 | Settlement does not depend on filing: it may happen before or after `record-filing` |

## 10. Open questions

| # | Question |
|---|---|
| Q1 | **Decided:** every fiscal period of the VAT period must be at least SOFT_CLOSED (`VAT.PERIOD_NOT_COMPLETE` otherwise). |
| Q2 | **Decided:** per-registration `correction_method`: ADJUST_NEXT, SUPPLEMENTARY or REPLACEMENT (7.1), with return kinds ORIGINAL, SUPPLEMENTARY and REPLACEMENT. Replacement supersedes through `superseded_by_return_id` and `is_current` (S columns). Country packages choose the method. |
| Q3 | **Decided:** `vat_return` has its own hash chain per company, anchored like the other chains (6.3, 6.6). |
| Q4 | **Decided:** tax lines carry separate VAT reporting amounts (5.1a) valued per registration with the booking rate or a new rate type VAT at the tax date. Returns sum these amounts. Which rate a country prescribes is configuration, to verify per country. Closes the open item of the principles on cross-currency VAT. |
| Q5 | **Decided (confirmed by 14 D9, ADR-153):** a VAT_SETTLE document can be reversed only before `record-filing`, through its owning command and not through `journal-documents:reverse` (D1). No command reverses a filing. After filing, corrections go through the registration's correction method and produce a new settlement or a settlement difference. |
| Q6 | **Decided:** partial deduction is deferred. In R1 an input tax code is fully deductible, or a separate non-deductible code posts the tax to the expense. Later: `deductible_pct` on the code version and a split of the input tax line. **Amended (ADR-201, 2026-10-14):** partial and non-deductible input VAT, including reverse charge without deduction, is in release 1 for the DK package. The design is in 19-partial-input-vat-deduction (DRAFT, ADR-203 to ADR-206 proposed), this deferral is lifted. |
| Q7 | **Decided:** the finance core stores the VAT treatment and evidence references only. Declarations, customs value, duties, deferment accounts and the postponed-accounting trigger belong to inventory and order management, which post to finance through the posting rules (ADR-012). |
| Q8 | **Decided:** `tax_date` on the document (default) and on the line (override). |
| Q9 | **Decided:** VAT return snapshots follow the company's retention rule for legal documents and are anonymised by the same job. No separate retention class. |
| Q10 | **Decided:** missing required evidence produces a warning (`VAT.EVIDENCE_MISSING`) at preparation. The return is still prepared. |
| Q11 | **Decided:** cash accounting is deferred. `tax_date` stays L1 and is never updated. If needed later, a separate append-only tax-point event (created when a payment clears the invoice) would give the date from which the line enters a return, and return selection would use that date instead of `tax_date`. Existing columns and snapshots need no change. |
| Q12 | **Decided:** VAT accounts accept only tax lines and settlement lines. The `tax_account` flag (S on `gl_account`) is set automatically (6.7). |
| Q13 | **Decided (14, section 10):** the command is `vat-settlements:reverse` (7.3a), before filing only, permission `vat.settle`. Renamed in 14 section 11 to `vat-returns:reverse-settlement`. The contract is 7.3a (single contract, 17 X8). The 06 catalogue entry refers to it. |
| Q14 | **Decided (14, section 11):** the reversal emits the new event type `vat.settlement_reversed` (not `vat.settled` with a flag). Error `VAT.NOT_SETTLED` is confirmed. |

## 11. Deltas to earlier specs

| Spec | Change |
|---|---|
| 02 `journal_line_tax` | New L1 columns `tax_date` and `vat_currency`, `vat_base_amount`, `vat_tax_amount`, `vat_rate`, `vat_rate_date` (5.1a) |
| 02 and 06 | New rate type `VAT` (ADR-019 list). New error code `ACCOUNT_RULE.TAX_ACCOUNT_DIRECT_POSTING` (6.7) |
| 07 `gl_account` | New S column `tax_account` (6.7). `exchange_rate.rate_type` adds VAT |
| 02 `chain_anchor` | `chain_type` adds VAT_RETURN |
| 02 line_type | `TAX` lines of a reverse-charge pair carry tax_type OUTPUT and INPUT. No new line type |
| 06 `post` | New optional header `tax_date`, optional line `tax_date`. New change entity type `TAX_EVIDENCE`. New error area `VAT`. Catalogue gains the commands of section 7 (including `vat-returns:reverse-settlement`) and the permissions `vat.prepare`, `vat.file`, `vat.settle` |
| 07 | Deltas in 6.2: `tax_code.vat_registration_id`, `tax_code_version` additions, `tax_return_box.return_type`, `tax_code_version_box.line_side`, `account_role_assignment.vat_registration_id` |
| 07 role list | `VAT_SETTLEMENT` per registration |

These deltas are applied with this spec.

### 11.1 Deltas of the 2026-10-13 re-baseline (14, sections 8 and 9)

| Source | Change in this spec |
|---|---|
| D9, ADR-153 | Settlement reversal only before filing, no command reverses a filing (7.2, 7.3, Q5). The second half of D9 (clearing does not set `exchange_rate.first_used_at`) concerns 07 and 03 and does not appear here |
| D1, ADR-145 | VAT_SETTLE is rejected by `journal-documents:reverse` and reversed only through its owning command (7.3, Q13) |
| K1, ADR-158 | `vat_return_chain_head` replaced by a `chain_head` row per (company, VAT_RETURN), `scope_key` empty, one chain over all return types (2, 6.6). `chain_seq` is gapless per company (6.3). Corrected in the second re-baseline (14, section 10) |
| D3, ADR-147 | `vat-returns:prepare` locks the chain head in the authoritative lock order (7.1) |
| 15, ADR-138 | `vat-returns:prepare`, `:record-filing` and `:settle` write the outbox events `vat.return_prepared`, `vat.return_filed`, `vat.settled`, and `vat.settlement_reversed` from `vat-returns:reverse-settlement` (15, section 6). No change to the return hash |
| 14 section 10 | VAT_RETURN chain one per company, `scope_key` empty (6.3, 6.6, 7.1, 8, A3). New command `vat-settlements:reverse` replaces the use of `journal-documents:reverse` in 7.3 (7.3a). Q13 closed |
| 14 section 11 | `vat-settlements:reverse` renamed `vat-returns:reverse-settlement` (7.3, 7.3a, Q13). New event `vat.settlement_reversed` and error `VAT.NOT_SETTLED` confirmed (7.3a). Q14 closed |
| Wording | `source` = LEGACY replaced by the existing columns `legacy_base_amount` and `legacy_tax_amount` (7.1). "Fifth chain" replaced by the list of six chains (8) |

### 11.2 Deltas of fix pass 4 (17, section 4)

| Ref | Change in this spec | Effect on other specs |
|---|---|---|
| X8 | 7.3a is the single contract of `vat-returns:reverse-settlement`: `return_id`, `reversal_date` (no date shifting), `reason_code` CORRECTION or ERROR, `reason_text` | 06 catalogue row (still shows `settlement_document_id`) and 06 line 268 refer to 08, 7.3a |
| B9 | Replacement settlement is the difference against the last settled snapshot along `replaces_return_id` (7.3). Reverse-settlement on a superseded return is rejected with `VAT.SUPERSEDED` while a successor settlement is active (7.3a) | none |
| F1 | Determination tie-break, NOT_REGISTERED in the destination country, explicit origin and destination for partnerless documents (3.1). New code `TAX.ORIGIN_DESTINATION_MISSING` | 07, 8.4 and the 06 `TAX` error list |
| F2 | `TAX_EVIDENCE`: `line_no` in the key, append-only evidence, allowed in hard-closed periods as a controlled exception with the audit event `vat.evidence_added` (5.3) | 06 (`change-open-fields` entity type), 09, 4.8 and 15, section 6 (new event type) |
| Codes | `VAT.NO_REGISTRATION` and `VAT.REGISTRATION_INACTIVE` defined (7), `VAT.NOT_FILED` dropped | 06 `VAT` error list |
| X4 | Lock sort tuple (chain type, company, scope key), counters before heads (6.6, 7.1) | none |
| Stale | Q13 and the 11.1 row no longer say that the 06 entry is pending | none |

### 11.3 Deltas of the propagation pass (17, section 5, v0.6)

The pass grepped this spec for the superseded names (`vat-settlements:reverse` outside the history rows 11.1 and Q13, `ledger_id` on `chain_anchor`, `read.ext`, `chain.verified`, `access.read_logged`), for event names that differ from 15, section 6, and for `VAT.SUCCESSOR_SETTLEMENT_ACTIVE`. None was found beyond the history rows. The event names `vat.return_prepared`, `vat.return_filed`, `vat.settled`, `vat.settlement_reversed` and `vat.evidence_added` match 15. The tie-break of 3.1 now names `priority` before `rule_id` (17 section 5, Momsbestemmelse keeps `priority`). No effect on other specs.

**Propagation pass 2.** The tie-break of 3.1 is corrected to the binding ADR-192 order: explicit `priority` first, then specificity (count of non-null keys), then lowest `rule_id`. It had said specificity, then priority, then `rule_id`. 07, 8.4 still states the old order (specificity, then `priority`) and must be aligned.

### 11.4 Deltas of ADR-200 and ADR-203 to ADR-206 (2026-10-14)

| Source | Change in this spec |
|---|---|
| ADR-200 | EU flags as determination keys, stored on the tax line (3.1, decisions table) |
| ADR-201, ADR-203 to ADR-205 | Partial and non-deductible input VAT in scope (out-of-scope line, decisions table). The reverse-charge pair splits the deductible side only (5.2). Reporting amounts follow the line's own amount (5.1a). Settlement excludes the non-deductible cost lines (7.3). Q6 is replaced by 19. Returns, snapshots, replacement, correction methods and reversal are unchanged in structure (19, section 7) |
| ADR-206 | Not in release 1: order-built stock purchases with partial deduction, capital goods adjustment, the adjustment command (19, sections 9 and 10) |

## 12. ADRs raised by this spec (now in 01-adr-log, which is authoritative)

**ADR-075: VAT scenarios as rules and codes.** Domestic, reverse charge, intra-EU, OSS/IOSS, import and export are expressed with the determination keys, code attributes (`reverse_charge`, `ec_list_category`, `supply_kind`, evidence requirement) and return mappings. No scenario-specific tables. Refines ADR-026.

**ADR-076: VAT registrations and periods from fiscal periods.** `company_vat_registration` defines scheme and filing frequency, VAT periods are runs of fiscal periods.

**ADR-077: Tax date on tax lines.** `tax_date` (L1), default document date, decides the VAT period.

**ADR-078: Reverse charge as a pair of tax lines.** One output and one deductible tax line with opposite signs, mapped to separate boxes.

**ADR-079: Frozen return snapshots.** Immutable, hashed and chained snapshots per registration, return type and period, with a one-report-per-line rule through `vat_return_tax_line`. Corrections are late lines in later snapshots. Refines ADR-028.

**ADR-080: VAT settlement from a snapshot.** `vat-returns:settle` posts the settlement document from the snapshot's tax lines and binds `snapshot_digest` through `detail_digest`. Replaces `vat:settle` of ADR-028.

**ADR-081: VAT evidence rows.** Changeable (L2) references attached to tax lines, outside the hash.
