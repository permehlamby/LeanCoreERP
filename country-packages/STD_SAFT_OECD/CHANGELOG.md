# STD_SAFT_OECD: changelog

## 1.0.0 (DRAFT, 2026-10-14)

First draft of the standards package for the SAFT_DATASET variant `OECD_BASE` (12, 8). Kind STANDARD, owned by the platform team (18, Q4). State DRAFT, nothing VERIFIED.

### Contents

| File | Content |
|---|---|
| package.json | Manifest. Standard: OECD SAF-T 2.00 |
| core/external_code_mapping.json | 28 rows `SAFT_OECD` / `DOCUMENT_TYPE`: the core document types mapped to journal codes (package choice, the schema prescribes none) |
| app/saft/oecd-2.00-schema-elements.json | 423 element definitions of the OECD schema appendix (path, occurrence, type, length), extracted mechanically from the archived PDF (sha256 recorded) |
| app/saft/oecd-2.00-core-mapping.json | 190 entries mapping the elements of the covered scope to core columns, derivations, constants, job parameters, or flagging them as gaps |

### Coverage

Header, MasterFiles (GeneralLedgerAccounts, Customers, Suppliers, TaxTable, UOMTable, Products), GeneralLedgerEntries, SourceDocuments (SalesInvoices, PurchaseInvoices, Payments). All 133 mandatory elements of that scope (counting type members once) have an entry or lie below one. Status counts: 103 MAPPED, 48 DERIVED, 11 JOB, 7 CONSTANT, 11 OMITTED, 10 GAP.

Not covered: Taxonomies, AnalysisTypeTable, MovementTypeTable, PhysicalStock, Owners, Assets, MovementOfGoods, AssetTransactions. Of these, AnalysisTypeTable (dimensions) and MovementOfGoods (stock movements, 12, 8) are candidates for 1.1.0.

### Findings (gaps in the core, from the mandatory elements)

| Ref | Finding | Effect |
|---|---|---|
| G1 | The `company` has no address (07, 5.4). `Header/Company/Address` (City and PostalCode) and the seller billing address on every sales invoice are mandatory in the schema. The same data is useful for EN 16931 (seller postal address) | New ADR-207 PROPOSED: address columns on `company` |
| G2 | The company has no contact person or telephone. `Header/Company/Contact` needs `ContactPerson` (first and last name) and `Telephone`. A contact person is personal data (12, 3) | Part of ADR-207: a company contact, with the personal data treatment decided there |
| G3 | Several mandatory SAF-T texts are nullable in the core (`header_text`, `line_text`, partner address fields, `legal_registration_id`). | The data set uses defaults for the texts and lists the missing partner and company data as a completeness finding (12, 8). No core change |
| G4 | `Line/DebitAmount` and `CreditAmount` are both mandatory in the schema, the core stores one signed amount (ADR-035). | Derived: the unused side is 0. No core change |
| G5 | `TaxTableEntry/TaxCodeDetails/FlatTaxRate` is mandatory. The core has no flat-amount taxes. | Constant 0 |
| G6 | `Invoice/Line/UnitPrice` is mandatory. Directly posted AR and AP documents have no unit price, only order-built invoices do. | A completeness finding for such documents. They appear in the ledger section regardless |

### Before RELEASED

1. Compare the structure file with the current OECD schema file and record the exact version.
2. Resolve G1 and G2 (ADR-207) or document the defaults.
3. Name a reviewer. Verify the mapping against specs 02, 07, 10, 11, 12 as they stand at release.
4. Decide whether `AnalysisTypeTable` and `MovementOfGoods` belong in 1.0.
