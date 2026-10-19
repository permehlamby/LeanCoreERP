# DK country package: changelog

## 0.4.0 (DRAFT, 2026-10-19)

Adds the Danish payment content as app content (21, D8, 22, section 12). No core item changes: still 638 core items, no change to the core files. State DRAFT, nothing VERIFIED.

### Contents

| File | Content |
|---|---|
| app/payment/dk-payment-schemes.json | FIK card types 71, 73 and 75, giro (01, 04, 15), domestic credit transfers, NemKonto, Betalingsservice and SEPA: identifiers, how each is represented in a slip, in Peppol and in pain.001, which core field holds it, facts with the basis of each |
| app/payment/dk-reference-algorithms.json | FIK modulus 10 check digit (type 71 checked against the worked example of the PBS handbook), Danish BBAN and IBAN structure (the Nordea example IBAN checked), Peppol PaymentID form |
| app/payment/dk-peppol-payment-rules.json | Danish Peppol BIS Billing 3 rules DK-R-005 to DK-R-011 with what each needs from the core |
| app/payment/dk-pain001-profile.json | Codes of the domestic payment types in pain.001 as Nordea describes them, account and address rules, the 2027-04-01 change |

### How well each part is sourced

| Part | Source | Strength |
|---|---|---|
| FIK type 71 structure and modulus 10 | PBS handbook for the card +71< (August 2009) | Primary for the slip. Check digit example reproduced. The handbook is old: check for a current version |
| FIK 73 and 75, giro | Danske Bank terms (December 2002), Nordea appendix, Peppol rules | Secondary. The 15 digit weights of types 75, 04 and 15 are ASSUMED |
| Danish accounts and domestic payment types | Nordea Corporate Access Payables appendix Denmark (2026-10-08) | One bank's guide, pain.001.001.03 codes. Example IBAN reproduced |
| Betalingsservice | Spar Nord general rules for debtors (2019) | A bank's debtor terms. **The creditor side (agreements, collection files) was not found** |
| Danish Peppol payment rules | docs.peppol.eu through a page fetch that summarises | Paraphrased, ids consistent with a search result. Check the page for exact wording |

### Findings

| Ref | Finding | Effect |
|---|---|---|
| F-DK-19 | FIK creditor number and giro account have no core home. DK-R-008 and DK-R-010 need them as the payee account id | **Closed by ADR-223** (accepted 2026-10-19, applied in 07, 9.5): account scheme and identifier on `company_bank_account`. A company row per creditor number comes with the first Danish client. No package item is added |
| F-DK-20 | BT-83 (the PaymentID, for example 71#...) is missing from the STD_EN16931 term mapping and from the data set of 12, section 10. DK-R-006 and DK-R-007 also name registration number, mandate id and payer account | **Partly closed by ADR-223:** BT-83 is in STD_EN16931 1.0.1 and the data set reads `payment_reference`. Open: registration number of the bank, mandate id and payer account (BG-19, DK-R-006 and DK-R-007) |
| F-DK-21 | The FIK payment id comes from the invoice number, known after posting, and `sales-invoices:create` has no payment_reference field | No change. The app sets payment_reference with change-open-fields after the post (22, 4.2) |
| F-DK-22 | A Betalingsservice debtor can reverse a payment until the 7th of the payment month | The collection app treats the receipt as reversible until then (returned payment flow, 22, 5.6). An app policy |
| F-DK-23 | Nordea names 2027-04-01 as the date from which brief advice and 41 lines of 35 characters end, structured invoice information (CINV) is allowed and domestic name and address rules change | Whether it is a Denmark-wide date is not confirmed. Check before the date is used |
| F-DK-24 | The pain.001 placement of the FIK OCR number and card type code is not readable in the appendix (the text is cut) | Read the bank's user guide and message flow before a payment file generator is built |

### Left out

- The creditor side of Betalingsservice (agreement, collection file formats, the payment overview). Not found in public sources.
- Domestic debit and credit card schemes, MobilePay, and instant payments.
- `STD_ISO20022` covers the SEPA profiles and the statement messages. Danish camt specifics (bank transaction codes, the CGI-MP country recommendation) are not mapped.

### Before RELEASED (additions)

7. Read the current FI +71<, +73<, +75< and giro handbooks and the Nets Betalingsservice creditor documentation.
8. Read the bank user guide for the pain.001 placement of FIK references.
9. Decide the remaining part of F-DK-20 (registration number of the bank, mandate and payer account in the e-invoice data). ADR-223 itself is accepted.

## Required packages (2026-10-14)

`EU_VAT`, `STD_EN16931` and `STD_SAFT_OECD` now exist as DRAFT packages (country-packages/). F-DK-8 is closed: `tcl_goods` and `tcl_services` come from `EU_VAT`. The DK SAF-T gap analysis (F-DK-9) can now start from the OECD element mapping of `STD_SAFT_OECD` and its gaps G1 and G2 (no company address, no company contact).

## 0.3.0 (DRAFT, 2026-10-14)

Adds partial and non-deductible input VAT (ADR-201 to ADR-206, design in spec 19) on the same official sources as 0.2.0. State is still DRAFT, no item is VERIFIED. 638 items (0.2.0: 595).

### What changed against 0.2.0

| Area | Change |
|---|---|
| Tax codes | 55 of the 72 official codes (0.2.0: 20). 35 new purchase codes: fixed percentage (K040 50, K060 one third, K070 25, K610 two thirds), pro rata (K110, K670, K240, K290, K440, K490), estimated (K020, K220, K270, K420, K470, K680), area (K130, K250, K300, K450, K500, K700), sector (K120), supplied amount (K090, K100, K310, K320), combinations (K050 and K080: percentage times pro rata, K100 and K320: supplied amount times pro rata, K330 one third) and reverse charge without deduction (K230, K280, K430, K480, K690) |
| Attributes | Each code carries `deductible_pct`, `deduction_key` and `deduction_cap` where it differs from full deduction (07, 8.3). The mapping of the official deduction text to the attributes is a package choice (spec 19, section 2) |
| Keys | New `core/vat_deduction_key.json`: PRO_RATA, ESTIMATE, SECTOR, AREA per registration. No rates |
| Rates | New `core/vat_deduction_rate.json` and four optional inputs (`pro_rata_rate_pct`, `estimate_rate_pct`, `sector_rate_pct`, `area_rate_pct`): a PROVISIONAL rate from the first fiscal year start is created only when the input is given |
| Determination | The new codes are permitted alternatives of the scenario rules (domestic, EU goods, EU services, import goods, import services). No new rule |
| Return mapping | A reverse-charge code with deduction maps the full tax to the output box and the deductible part to the Købsmoms box (COUNTER line). A code without deduction maps only the output box, as the official return text says |

### Findings from writing the codes

| Ref | Finding | Effect |
|---|---|---|
| F-DK-14 | **Design correction in spec 19.** K100 and K320 are "invoice amount times pro rata". The first draft of 19, 3.3 let a supplied amount be bounded by the share, which cannot express that. | Corrected: `D = ROUND(supplied amount x key rate)`, the supplied amount is limited by `tax x deductible_pct`. 19, 3.3, 6.2, 06 (field and error text) and ADR-203 are corrected |
| F-DK-15 | Business types 2 and 4 (mixed activities) have no obvious default purchase code: common costs are pro rata (K110), costs of taxable activities are fully deductible (K010). That is a judgement per line or account, not a package fact. | The default of every purchase scenario stays K010, the pro rata and other codes are permitted alternatives. Open: a company rule by account (an account-keyed rule) for common cost accounts, to decide with the first mixed client |
| F-DK-16 | One third (K060, K330) and two thirds (K610) are stored as 33.3333 and 66.6667, the official list shows 0.333 and 66,6 percent. | Confirm the statutory fractions in the verification. The attribute is four decimals, so a fraction is rounded |
| F-DK-17 | A reverse-charge code without deduction has a zero counter tax line (ADR-047) with no box. The official text has no Købsmoms entry for it. | Consistent with 19, 4.3. Nothing to do |
| F-DK-18 | Special-profile codes K610, K670, K680, K690, K700 are only available with `include_special_codes`. K610 is the 2/3 deduction on roads (agriculture). | As the official list |

### Left out of the official list (17 of 72)

Special schemes: S83 to S87 (used goods, margin, used cars, leasing cars, artists), K620 to K650 (the purchase side). Local VAT sales abroad: S88. Refund scheme: K710, K720. OSS: S61 to S65.

### Correction of an earlier note

The 0.2.0 changelog said 35 partial codes "plus the 5 reverse charge codes without deduction". The 35 already include those 5 (30 partial, estimated, area, sector and supplied-amount codes and 5 without deduction). 41 official purchase codes were left out at 0.2.0: 35 are in now, 6 stay out (K620 to K650, K710, K720).

### Before RELEASED (updated)

1. Name a legal reviewer.
2. Implement ADR-200 and ADR-203 to ADR-206 in the core, otherwise 8 rules and 35 codes do not validate.
3. Reconcile boxes, signs and codes with the current return form (F-DK-2) and confirm the fractions (F-DK-16).
4. Verify every UNVERIFIED record in verification.json against primary sources.
5. Write `EU_VAT` and the two standards packages (F-DK-8).
6. Decide the common-cost default for business types 2 and 4 (F-DK-15).

## Decisions of 2026-10-14 on the findings of 0.2.0

| Ref | Decision |
|---|---|
| F-DK-1 | ADR-200 accepted: derived EU keys. The delta is specified in 02, 06, 07, 08 and 09 (2026-10-14). The 8 rules validate when the core implements it |
| F-DK-11 | ADR-201: partial and non-deductible input VAT is in release 1 for DK. The design is in 19 (ADR-203 to ADR-206) and its deltas are applied. The left-out codes (35 purchase codes of the families in 19, section 2, among them the 5 reverse charge codes without deduction) were added in version 0.3.0 |
| F-DK-12 | ADR-202: enforced outside the core (package lint and apps) |
| F-DK-13 | Approved: RETAINED_EARNINGS 6940, realised FX netted on 3610-1, rounding on suffix accounts of 2720 |
| Reviewer | To be named later. The package stays DRAFT |
| EU_VAT | Own thin package, to be written |
| F-DK-2 | Open: the package sign convention is proposed for 08, unless the user objects |

## 0.2.0 (DRAFT, 2026-10-14)

Rebuilt on the official **Fællesoffentlig standardkontoplan, version 20260101** (file `2026-01-01 Standardkontoplan.xlsx`, sha256 `0dbebecf…7fc96`, recorded in package.json). The file has three sheets: guidance, the chart (722 rows) and the gross list of VAT codes (72 codes). State is still DRAFT, no item is VERIFIED.

### What changed against 0.1.0

| Area | Change |
|---|---|
| Chart of accounts | The starter chart is replaced by the public chart used directly: 485 numbered accounts with `account_no` = `standard_account_code`. Headings and sums (237 rows) are not accounts, they are in `app/reporting/dk-standard-chart-structure.json`. Seven system accounts are horizontal dimensions (suffix) of standard accounts, which the standard allows. 29 accounts marked "kun klasse A" are created inactive. **F-DK-5 is closed** |
| VAT codes | The package-invented codes are replaced by the official codes (new code column, for example S01, K010), with legacy code, name key, rate, official return text and business-type profiles copied from the list. 20 of 72 codes are in. See F-DK-11 for the 52 that are not |
| Profiles | New inputs `vat_profile` (1 to 4, the business types of the official list) and `include_special_codes` select the codes. Each code carries its `profiles` |
| Determination | One default rule per scenario, the other official codes of the scenario are permitted alternatives. 10 rules, 8 of them need ADR-200 |
| Return boxes | `DK_EU_GOODS_ACQ_VAT` is `DK_GOODS_ABROAD_VAT` (the official return has one box for VAT on goods bought abroad, EU and non-EU) and `DK_RC_SERVICES_VAT` is `DK_SERVICES_ABROAD_VAT`. `DK_RUBRIK_B_GOODS_OTHER` is now used by S22 (F-DK-10 closed) |
| Roles | Accounts of the standard: AR 6190, AP 7440, VAT 7680, 7700, 7720, 7740, settlement 7840, retained earnings 6940, history result 6935, bank 6480 |

### Corrections of 0.1.0 (errors found by the official list)

1. **Import is a reverse-charge pair, not input VAT paid at customs.** The official return text for K410 puts the VAT in "Moms af varekøb i udlandet (både EU og lande uden for EU)" and in Købsmoms. The 0.1.0 code `DK_P_IMPORT` was wrong for DK.
2. **Exempt sales are not in the return.** 0.1.0 mapped them to Rubrik C. In the official list S82 and S24 and S43 are "skal ikke medtages i angivelsen". Only S02 (0 percent, domestic), S41 and S42 go to Rubrik C.
3. The 0.1.0 account numbers (1000 to 6910) were a template without legal basis and are gone.

### Findings

| Ref | Finding | Effect |
|---|---|---|
| F-DK-1 | **Gap in the core.** No rule key for "any EU member state". | ADR-200 (accepted 2026-10-14). 8 of 10 rules depend on it |
| F-DK-2 | The meaning of `tax_code_version_box.sign` against signed ledger amounts is not stated in 08. The package states its convention in tax_code.json. | Clarify in 08, 6.4 and 07, 8.3 before RELEASED |
| F-DK-3 | Account `tax_code_rule` is a package choice: MANDATORY on 1010 to 1250 (7 accounts), OPTIONAL on other P&L accounts 1300 to 2829 and 3160 to 3175, fixed assets (5010 to 5770) and inventories (6080 to 6179), FORBIDDEN elsewhere. | Review with the first Danish client chart |
| F-DK-4 | Retention start differs between sources (end of financial year or document date). | Verify, then fix the `start_event` of the nine legal records |
| F-DK-6 | Public holidays are not included. | Later |
| F-DK-7 | Not in: orders and inventory (`account_determination`, order and fulfilment series, 10 and 11), OSS and IOSS, special schemes, energy taxes, SAF-T gap analysis, Peppol profile, invoice texts, payment instruments (task 3). | Planned list in app/app-content.json and the scope field |
| F-DK-8 | `EU_VAT`, `STD_SAFT_OECD` and `STD_EN16931` are required but not yet written. DK needs `tcl_goods` and `tcl_services` from `EU_VAT`. | Write them next, or move the two classes into DK |
| F-DK-9 | Sources disagree on whether Danish bookkeeping law requires SAF-T support. | Settle in the SAF-T gap analysis |
| F-DK-11 | **Partial deduction is needed in Denmark.** Of the 52 official codes left out, at least 30 are for partial, estimated, area-based or pro rata deduction and 4 are reverse charge without deduction. Business types 2 and 4 (mixed taxable and exempt activities) cannot be served without the pro rata code K110 and its relatives. 08, Q6 deferred partial deduction, and its replacement (a separate non-deductible code posting the tax to the expense) has no stated mechanism for a reverse-charge code. | Decide whether partial and non-deductible deduction enters release 1 for DK. Until then profiles 2 and 4 are only partly supported |
| F-DK-12 | The standard allows horizontal dimensioning (suffix) and forbids new account numbers (reserved for authorities). The core cannot enforce this: it accepts any `account_no`. | A check in the package tooling or an app, not in the core. Open |
| F-DK-13 | The standard names separate accounts for realised and unrealised gains and losses, the role table needs one account per role. | Realised differences are netted on 3610-1, unrealised have own suffix accounts. Confirm |

### Excluded official codes (52)

- Partial, estimated, area and pro rata deduction, sector deduction, 50 percent, 2/3 and 1/3 and the special leasing and representation rules: at least 30 (F-DK-11).
- Reverse charge without deduction: K230, K280, K430, K480 (F-DK-11).
- Special schemes (used goods, margin, used cars, leasing cars, artists): S83 to S87, K610 to K650.
- Domestic reverse charge except K660: K670, K680, K690, K700.
- Local VAT abroad, refund scheme: S88, K710, K720.
- OSS: S61 to S65 (scheme not in v0.2).

### Before RELEASED

1. Name a legal reviewer.
2. Write the ADR-200 core delta (07, 8.4 and 02).
3. Design partial deduction (ADR-201), then add the 52 left-out codes in 0.3.0.
4. Reconcile boxes, signs and codes with the current return form (F-DK-2).
5. Verify every UNVERIFIED record in verification.json against primary sources. The accounts and the code list are primary, so the check of those is a reviewer confirmation of completeness.
6. Write `EU_VAT` and the two standards packages (F-DK-8).

## 0.1.0 (DRAFT, 2026-10-14)

First draft with a starter chart and package-invented codes. Superseded by 0.2.0.
