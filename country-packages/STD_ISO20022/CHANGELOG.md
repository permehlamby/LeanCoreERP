# STD_ISO20022: changelog

## 1.0.0 (DRAFT, 2026-10-19)

First draft of the standards package for the payment, collection and statement apps (22, section 12). Kind STANDARD, owned by the platform team (18, Q4). State DRAFT, nothing VERIFIED.

### Contents

| File | Content |
|---|---|
| package.json | Manifest. Six messages, five profiles, four sources, seven findings |
| core/external_code_mapping.json | 4 rows `ISO20022` / `PAYMENT_MEANS`: the payment means codes 30, 58, 49 and 59 mapped to a composite of ISO 20022 method, service level and local instrument |
| app/iso20022/sepa-ct-pain001-elements.json | 195 elements of pain.001.001.09 in the SEPA Core credit transfer profile, parsed from the EPC guideline (sha256 recorded). The root-level SupplementaryData row is left out |
| app/iso20022/sepa-ct-pain002-elements.json | 74 elements of pain.002.001.10 in the SEPA reject dataset, same rule |
| app/iso20022/iso20022-core-mapping.json | 140 entries over five profiles (credit transfer, reject, direct debit, statement, notification). Status counts: APP 51, MAPPED 43, DERIVED 26, CONSTANT 8, GAP 6, EXTENSION 4, OMITTED 2 |
| app/iso20022/iso20022-code-lists.json | Reject reasons (16, from the guideline), status, sequence type, balance type, entry status and bank transaction code subsets, character set, address rule, IBAN and RF algorithms with test vectors |
| app/iso20022/message-catalogue.json | The messages, direction, profile and basis |

### How well each part is sourced

| Part | Basis | Strength |
|---|---|---|
| pain.001 and pain.002 | EPC132-08 2025 V1.0, the data dictionary parsed from the PDF. All mandatory elements of both dictionaries have an entry (34 and 6) | Primary source for SEPA credit transfer. Not yet compared with the XSD |
| camt.053 | mBank camt.053.001.08 description and Danske Bank camt.053.001.02 (CGI). Both are bank subsets of the ISO message | Secondary. The ISO message definition was not read |
| pain.008, camt.054 | Authored from general knowledge. The EPC direct debit guideline URL returned 404 and the Danske pain.008 PDF had no extractable text | **Weakest.** Every such entry has basis KNOWLEDGE |
| IBAN and RF check algorithms | Computed in the build against well-known examples (both IBAN vectors valid, the RF vector gives 18) | Algorithm and vectors agree. The standard text was not read |

### Findings (gaps in the core and in the sources)

| Ref | Finding | Effect |
|---|---|---|
| G-ISO-1 | The company has no address (as G1 of STD_SAFT_OECD). The guideline says that from 22 November 2026 only structured and hybrid addresses are allowed. The debtor and creditor address are optional in the profile | Supports ADR-207 (PROPOSED). The data set uses no address when it is missing |
| G-ISO-2 | No creditor identifier on the company for SEPA direct debits | Extension attribute of the company by the collection app, or a field if a package needs it. Decision with the first debit client |
| G-ISO-3 | Partner addresses are free lines. Enough for a hybrid address. No street name or building number apart | No core change. Hybrid address from the existing columns |
| G-ISO-4 | `partner_bank_account` has only an IBAN | No change for SEPA. A table extension if non-IBAN payees are needed |
| G-ISO-5 | No core field for the end-to-end id | By design (22, 5.5): the app keeps the link. A convention, not a gap |
| G-ISO-6 | UNCL4461 has no code for SEPA business-to-business debit | The package maps 59 to CORE. A B2B code is added by a country package or the company |
| G-ISO-7 | The SEPA credit transfer profile allows EUR only | Other currencies need a bank profile outside this package |
| G-ISO-8 | ADR-223 (accepted 2026-10-19) lets `company_bank_account` hold accounts without an IBAN (FIK creditor numbers, giro accounts) as rows of their own. Payment files (debtor and creditor account) and the statement lookup by IBAN use IBAN rows only | The payment and statement apps filter `account_scheme` = IBAN. No change in the mapping entries |
| S-1 | The address rule date is 22 November 2026 in the guideline. A search result (secondary) says an EPC update moves it to 15 November 2026 | Check the EPC update before the date is used in a country package |
| S-2 | The camt version differs by bank: Danske Bank documents camt.053.001.02 for Nordic accounts, mBank camt.053.001.08. The entries used here exist with the same tags in both, the status element differs | The statement import app must read both. A conformance test against real files of the pilot bank decides |

### Before RELEASED

1. Compare the pain.001 and pain.002 dictionaries with the EPC XSDs.
2. Read the EPC direct debit guideline (Core and B2B) and replace the KNOWLEDGE entries of pain.008.
3. Read the ISO 20022 message definition reports for camt.053 and camt.054 and replace the bank-subset basis.
4. Verify the code lists against the ISO 20022 external code lists.
5. Decide ADR-207 (G-ISO-1) and G-ISO-2.
6. Name a reviewer.
