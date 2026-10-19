# EU_VAT: changelog

## 1.0.0 (DRAFT, 2026-10-14)

First draft of the thin regime package decided in 18 (Q10, ADR-195). Kind REGIME, requires STD_EN16931. State DRAFT, nothing VERIFIED.

### Contents

| File | Content |
|---|---|
| package.json | Manifest. Requires STD_EN16931 ^1 for the category pairing |
| core/tax_class.json | Two tenant-level tax classes: GOODS (`tcl_goods`) and SERVICES (`tcl_services`) |
| app/eu-vat/scenario-catalogue.json | 13 scenarios (domestic, reverse charge, intra-EU goods and services, acquisitions, triangulation, OSS, import, IOSS, export, drop shipment) with determination keys, code attributes, EN 16931 pairing, evidence and return type |

### What it replaces

- The `provides_to_dk` placeholder of the DK package: `tcl_goods` and `tcl_services` now exist (DK F-DK-8 closed for this package).
- The indicative table of 08, section 3 is now also a reusable artefact. 08 stays the specification, this is its package-form catalogue. The keys now use `origin_eu` and `destination_eu` (ADR-200) where 08 said "another EU country".

### Findings

| Ref | Finding | Effect |
|---|---|---|
| V1 | The scenario catalogue shows that **three** scenarios cannot be expressed by the keys alone: triangulation (EU-07) needs a class or an alternative code, drop shipment (EU-13) needs explicit per-line countries, and a B2C sale to an EU consumer (EU-08) depends on the OSS scheme of the registration, not on a rule key. | By design (08, 3). Country packages choose alternative codes or classes |
| V2 | EU-04 (services) and EU-07 (triangulation) have no single EN 16931 category across member states. The catalogue gives the DK choice (AE) and says it differs. | Verified per country package |
| V3 | A tenant that applies DK and DE packages applies EU_VAT once: the second apply reports SKIPPED_EXISTS for the two classes (18, 7). | Works as designed |

### Before RELEASED

1. Verify the scenario rows against the VAT Directive and at least the first country package.
2. Decide whether OSS and IOSS need their own regime content (the schemes are in the registration, the return rows are keyed by country and rate, 08, 6.1).
3. Name a reviewer.
