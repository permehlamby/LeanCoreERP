# STD_EN16931: changelog

## 1.0.1 (DRAFT, 2026-10-19)

Applies ADR-223 (accepted 2026-10-19), found by the Danish payment content (DK F-DK-19 and F-DK-20). State DRAFT, nothing VERIFIED.

| Change | Detail |
|---|---|
| BT-83 Remittance information added | Optional in the standard, DERIVED from `journal_line_open_item.payment_reference` of the AR control line of the invoice (12, section 10). A country rule can make it mandatory: Denmark does for FIK (DK-R-010). The prefix such as `71#` is applied by the app from the country package. Terms: 82 (was 81), DERIVED 14 (was 13). Mandatory and conditional counts are unchanged (27 and 13) |
| BT-84 source | `company_bank_account.iban`, or `account_identifier` when `account_scheme` is not IBAN (07, 9.5) |

Still open from the Danish findings: the direct debit group (BG-19: mandate reference BT-89, bank assigned creditor identifier BT-90, debited account BT-91) and the Danish registration number of a bank (DK-R-006 and DK-R-007) are not in the mapping. They need the mandate (an app extension attribute, 22, 5.7) and a creditor identifier decision (G-ISO-2 of STD_ISO20022) before they can be mapped.

## 1.0.0 (DRAFT, 2026-10-14)

First draft of the standards package for the `EINVOICE_DATASET` export job and the read `einvoice-datasets:get` (12, 10). Kind STANDARD, owned by the platform team (18, Q4). State DRAFT, nothing VERIFIED.

### Contents

| File | Content |
|---|---|
| package.json | Manifest. Standard: EN 16931-1, baseline edition 2017+A1:2019 |
| core/external_code_mapping.json | 2 rows `EN16931` / `DOCUMENT_TYPE`: AR_INVOICE to 380, AR_CREDIT to 381 |
| app/einvoice/en16931-term-mapping.json | 82 business terms and groups (81 at 1.0.0): requirement (mandatory, conditional, optional), status, core source, the business rules that make a term required |
| app/einvoice/en16931-code-lists.json | VAT categories, exemption reason codes, invoice type codes, electronic address schemes |

Status counts of the terms at 1.0.1: 52 MAPPED, 14 DERIVED, 2 PROFILE, 4 GAP, 10 OMITTED (1.0.0: 13 DERIVED, 81 terms). 27 terms are mandatory and 13 conditional.

### Findings

| Ref | Finding | Effect |
|---|---|---|
| G1 | The seller postal address (BG-5) is mandatory but only the country code (BT-40) is. The company has the country, not the street, city and post code (BT-35 to BT-38, optional). | The data set is complete without them. ADR-207 PROPOSED adds the address for SAF-T and for readable invoices |
| G7 | The completeness check of the data set (12, 10) lists missing mandatory data. Its rule set is code in the core, this file lists the same terms with sources. | **Decided (ADR-221, 2026-10-19):** the core keeps a fixed rule set per EN 16931 edition, selected by `einvoice_rule_set` (07, 5.5). The core never reads this package. A conformance test compares the two lists. The choice of edition is still open |
| E1 | **The standard is moving.** EN 16931-1:2026 was approved on 2026-03-13, replaces the 2017+A1 edition and is not backward compatible (secondary source: BT-10 changes from 1..1 to 0..n, BT-29 and BT-46 from 1..n to 0..n). | The package targets 2017+A1:2019 until a decision. Open question below |
| E2 | The normative text of EN 16931-1 is not freely available. The term requirements here come from the business rules as published in the Peppol BIS Billing 3.0 specification. | Verification against the standard of the chosen edition is needed before RELEASED |
| E3 | Peppol BIS Billing 3.0 and the national CIUS make additional terms mandatory (for example a buyer reference or order reference). | Not in this package: they are app content of the country package or the profile |

### Open decision

Which edition should the package target: EN 16931-1:2017+A1:2019 (the edition national profiles and the validation artefacts in use refer to, UNVERIFIED), or EN 16931-1:2026? A major version of the package per edition is the natural form (1.x for 2017+A1, 2.x for 2026).

### Before RELEASED

1. Decide the edition. Verify the term list, cardinalities and the code lists against the standard and the exact list versions.
2. Decide G1 (ADR-207). G7 is decided (ADR-221). Write the conformance test of package list against the rule set of the chosen edition.
3. Write the syntax bindings (UBL, CII) as app content.
4. Name a reviewer.
