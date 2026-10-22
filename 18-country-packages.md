# Country Packages: Format, Ownership and Model Test (DK, DE, SE)

Status: DRAFT v0.2 for review (decisions of 2026-10-14 applied, section 11). Builds on 00-design-principles v0.4, ADR-001 to ADR-192 and specs 02 to 17. ADR-193 to ADR-202 are ACCEPTED (2026-10-14, see 01-adr-log). Design pending: partial input VAT deduction (ADR-201). Closes the open item "country package format" of 00, section 5, and Q10 of 07 (format and maintenance process).
Scope: what country content is data in the core and what is a package, who owns what, the package format, apply and update semantics, the verification gate, and a test of the model against DK, DE and NO.
Out of scope: the actual content of any package (accounts, codes, rates, boxes). Each package is authored and verified after this spec is agreed. Platform mapping (signing, storage, repository technology) is decided in 20 (ADR-208 to ADR-218, accepted 2026-10-19).

**Caution on law.** This spec states no tax, bookkeeping or retention rule of any country as fact. Every legal statement in sections 7 to 9 is an **indicative test input**, marked UNVERIFIED, taken from secondary sources (adviser and vendor pages), and must pass the verification gate of section 6 before it enters a released package (P15).

## 1. Decisions taken

| Topic | Decision | ADR |
|---|---|---|
| What a package is | A versioned bundle with **three content classes**: core content (loaded into core configuration tables), policy selections (values for a closed switch catalogue in the core), app content (artefacts the core never interprets) | 193 |
| Ownership boundary | The core owns the **mechanisms, the package format, the apply engine and the switch catalogue**. Packages own **content and its legal verification**. A package can select behaviour the core already has, never add behaviour | 193, 194 |
| Layering | Standards packages (`STD_SAFT_OECD`, `STD_EN16931`), a VAT-directive package (`EU_VAT`) and one package per country, each declaring `requires`. Tenant changes are ordinary configuration, not packages | 195 |
| Apply | Symbolic references, declared inputs, create-only for master data, new versions for versioned data, user changes are never overwritten, every created object is itemised | 196 |
| Release | A package is RELEASED only with every item VERIFIED by a named reviewer. PRODUCTION companies accept only RELEASED packages | 197 |
| Model test | DK as baseline, DE and SE as stress tests. Result: model holds, with one small core delta (retention start) and one deferred mechanism (cash VAT, not in release 1) | 198, 199 |

## 2. What is data in the core, what is a package

Existing core tables stay the only place where country content becomes data. This section fixes, per kind of content, whether it is a core table, a policy switch or app content. It is a ruling per item, not a general test (14, 5.1 was not adopted as a rule).

| # | Content | Class | Lands in | Notes |
|---|---|---|---|---|
| 1 | Chart of accounts template with `standard_account_code`, account groups, open-item mode, tax code rules | Core | `gl_account`, `account_dimension_rule` (07) | Company-level, applied per company |
| 2 | Role proposals (rounding, FX, retained earnings, VAT settlement and so on) mapped to template accounts | Core | `account_role_assignment` | Needs the company's VAT registration for `VAT_SETTLEMENT` (input, 4.3) |
| 3 | Tax classes, return boxes per return type | Core | `tax_class`, `tax_return_box` (tenant level) | Applied once per tenant and country |
| 4 | Tax codes and versions, box mapping, determination rules and alternatives, `ec_list_category`, `supply_kind`, evidence requirement, EN 16931 category and exemption reason code | Core | 07 section 8, 08 section 6.2, 12 section 9.2 | Per company (07, A3) |
| 5 | VAT registration options: allowed schemes, filing frequencies, default `correction_method`, `vat_rate_type` | Core, via **inputs** | `company_vat_registration` | The VAT id and the chosen frequency are tenant facts and are package **inputs**, not package content |
| 6 | Number series templates and assignments, `is_legal` flags | Core | `number_series`, `number_series_assignment` (04) | Created at company set-up |
| 7 | Retention rules per record class, start event | Core | `retention_rule` (12) | One rule per class and country |
| 8 | External code mappings (SAF-T standard codes, EN 16931 codes, national variants) | Core | `external_code_mapping` (12, 9.1) | |
| 9 | Public holidays | Core | `business_calendar_exception` (16, 3.2) | |
| 10 | Payment terms, default company settings (rounding mode, tolerances) | Core | `payment_terms`, `company_setting_version` | |
| 11 | Behaviour switches (numbering order, clearing in legal series, tax point rule) | **Policy** | `company_policy_setting` (5.3) | Closed catalogue (section 5). Value from the package, behaviour in the core |
| 12 | National file formats: SAF-T national XML and its mapping rules, SIE, DATEV, OIOUBL, XRechnung and Peppol syntax, CIUS rules | **App** | Not stored in the core | Assembly from core data sets (12, sections 8, 9, 10) stays with apps (P15) |
| 13 | Filing channels and return form layout, derived return boxes (formulas), filing deadlines, threshold advice for the filing frequency | **App** | Not stored | The core returns box totals (08, 6.4) and records the filing (08, 7.2) |
| 14 | Mandatory invoice texts and their languages, letterhead items | **App** | Not stored | The core holds the code and one text (`exemption_reason_*`, 12, 9.2). Rendering and translation are apps |
| 15 | Payment instruments (SEPA, FIK, KID, Betalingsservice and similar), dunning and interest rules | **App, later domain** | Task 3 (invoicing and payment) | Listed here so the package format has the slot. No content now |
| 16 | EU membership, currencies, countries | Platform | `ref_*` (07, 4) | Not packages. Maintained by platform releases |

Rule for new content: if it can be expressed in a table the core already has, it is core content. If it needs a behaviour the core does not have, it needs an ADR and a core change first (ADR-194). If the core never reads it, it is app content.

## 3. Ownership

| Party | Owns | Does not own |
|---|---|---|
| **Core** (platform team) | Package format (this spec), validator and apply engine, switch catalogue, `ref_*` tables, `STD_*` packages (they track international standards, not countries) | Any country's content |
| **Country owner** (one named role per country package, for example a TTC or Implement practice lead) | Content of the country package and `EU_VAT` for the member states it covers, release cadence, change log. Appoints a **legal reviewer** | Legal sign-off of own work |
| **Legal reviewer** (tax or accounting specialist, may be external) | The verification of each item (section 6): source, date, verdict | Authoring |
| **Tenant / company** | Inputs at apply time, overlay changes through the normal config API (own accounts, own tax codes, own series) | The package itself |
| **Apps** | Class C content for their country, delivered as package artefacts, never read by the core | Writing core tables except through the API |

A package is not legal advice. The verification record is the evidence of what was checked, against which source, by whom and when.

## 4. Package format

A package is a directory (distributed as one signed archive). The format is technology-neutral JSON (ADR-001). The signing scheme, signing manifest, trust sets and repository are decided in 20, section 11 (ADR-217): the archive carries a generated `signing-manifest.json` with a SHA-256 per file, a detached ECDSA P-256 signature covers it, and the core verifies signature, digests and fields against `package.json` at apply. The core receives the archive through its API and never fetches it.

```
package.json            manifest
core/                   one file per resource, objects in the shape of {resource}:create
policy.json             policy selections
app/                    class C artefacts, opaque to the core
verification.json       per-item verification records
CHANGELOG.md            human history
```

### 4.1 Manifest (`package.json`)

| Field | Notes |
|---|---|
| `format_version` | Version of this format. The core rejects an unknown major version |
| `package_code` | `DK`, `DE`, `NO`, `EU_VAT`, `STD_SAFT_OECD`, `STD_EN16931` |
| `package_version` | `MAJOR.MINOR.PATCH`. MAJOR = format or structural break, MINOR = new content or a law change as new dated versions, PATCH = correction of text, mapping or description that touches no applied object |
| `kind` | `STANDARD`, `REGIME` (the EU VAT package), `COUNTRY` |
| `country_code` | For `COUNTRY` |
| `state` | DRAFT, REVIEWED, RELEASED, DEPRECATED (section 6) |
| `requires` | List of `package_code` with a version range. Applied first |
| `scope_map` | Which files are TENANT scope and which are COMPANY scope (section 4.3) |
| `inputs` | Schema of the values the applying user must supply (4.3) |
| `law_effective_from` | Earliest `valid_from` of any content, for orientation |
| `owner`, `legal_reviewer` | Roles, not personal data |
| `content_digest` | SHA-256 over core, policy and app files in fixed order (recorded in `applied_config_package`, 07, 10.2) |

### 4.2 Core content files

Each object is the payload of the `{resource}:create` or `{resource}:create-version` command of 07, 11.1, plus:

- `ref`: a symbolic key unique in the package. References between objects use `ref`, never ids, because ids are generated per tenant. The engine resolves `ref` to ids during apply, in dependency order.
- `valid_from` (V objects): the date from which the content applies under law, so a future change ships early without effect.
- `item_id`: stable id of the item, the key of its verification record.

### 4.3 Inputs and scope

Facts that belong to the tenant, not the country, are inputs: the VAT id, the chosen filing frequency (limited to the values the package allows), the fiscal year start, the account for each role the package leaves open. The package declares the schema, the engine validates the values, and the values are recorded by digest (`inputs_digest`), not stored as package content.

Scope: tenant-level objects (tax classes, return boxes, external code mappings that are tenant-wide) are applied once per tenant and country. Company-level objects (accounts, tax codes, rules, series, roles, retention rules, registration) are applied per company, which is why a tenant with several companies duplicates codes (07, A3). Applying the same package to a second company is a normal repeat of the apply, not a copy.

### 4.4 Policy file

Key and value pairs for the catalogue of section 5, each with `valid_from`. Unknown keys reject the package.

### 4.5 App content

Files under `app/` are listed in the manifest with name, media type and digest, grouped by app role (for example `saft-export`, `einvoice-syntax`, `filing`, `invoice-text`). The core stores only the digest list in the apply record. Apps fetch the artefacts from the repository by digest. The core never parses them.

## 5. Policy catalogue (closed)

A policy key selects a behaviour that exists in the core. The catalogue is closed: a new key needs an ADR and a core change, never only a package.

| Key | Values | Core behaviour | State |
|---|---|---|---|
| `NUMBERING_ORDER` | `POSTING_ORDER` (default), `DATE_ORDER` | ADR-025: numbers follow posting order. `DATE_ORDER` restricts the posting window to one open period so numbers follow date order | `DATE_ORDER` is an **option in ADR-025, not built**. No tested country needs it (section 9). Built only when a package needs it |
| `CLEARING_IN_LEGAL_SERIES` | `NO` (default), `YES` | ADR-036: a clean clearing consumes no legal number | `YES` not built. Closes the follow-up of ADR-036 per country (section 9) |
| `TAX_POINT_RULE` | `INVOICE_DATE`, `DELIVERY_DATE` (default for goods from a delivery, 11, Q4), `PAYMENT_DATE` | Default of `tax_date` (08, 5.1). `PAYMENT_DATE` needs the tax-point event of ADR-199 | `PAYMENT_DATE` **not available** until ADR-199 is decided |

### 5.1 Tables

`company_policy_setting` (V, insert only): PK (`tenant_id`, `company_id`, `policy_key`, `valid_from`). Columns: `value`, `applied_id` (the package apply that created it, null for a manual setting), `created_by`, `created_at`. Permission `write.config`. A manual row can override a package row from a later date. Reads by the posting pipeline use the row valid at the posting date.

## 6. Verification gate and lifecycle

| State | Meaning | Applies to |
|---|---|---|
| DRAFT | Content authored, nothing checked | TEST and REHEARSAL tenants, DRAFT companies |
| REVIEWED | Every item has a verification record, at least one is not VERIFIED | Same as DRAFT |
| RELEASED | Every item is VERIFIED or NOT_APPLICABLE, signed off by owner and legal reviewer | All, including PRODUCTION companies |
| DEPRECATED | Replaced. Still readable and re-appliable to companies that already use it | Existing companies |

Verification record (in `verification.json`, per `item_id`, for several `item_ids`, or for all items with an `item_prefixes` entry, when they share a source): `status` (UNVERIFIED, VERIFIED, NOT_APPLICABLE), `source_ref` (law, regulation or authority guidance with section and version), `source_date`, `verified_by` (role), `verified_on`, `note`. An item that depends on a standard version (SAF-T schema, EN 16931 code list) also records that version.

Gate: `config-packages:apply` on a PRODUCTION tenant rejects a package whose `state` is not RELEASED (`PACKAGE.NOT_RELEASED`), whose signature is not valid for the trust set of the tenant `purpose` (`PACKAGE.SIGNATURE_INVALID`, 20, section 11) or whose digest does not match the manifest (`PACKAGE.DIGEST_MISMATCH`). The engine does not judge the legal content, it checks that the record exists and is complete. Items marked UNVERIFIED in a released package are not possible by construction.

A law change is a new MINOR version with new dated versions (V objects) and a new release. An error in an already applied object that postings have used cannot be edited (IAP, 07, 2.3): it is corrected by a new version from today and flagged `CORRECTION` in the changelog. Repairing past postings is a data repair outside the package.

## 7. Apply and update semantics

Refines `config-packages:apply` of 07, 11.4. Items are the objects the apply creates.

| Rule | Text |
|---|---|
| Order | `requires` first, in dependency order, then the package. Each package is its own apply record |
| Create-only (M objects) | An existing object (same natural key) is **never changed**. The result lists it as `SKIPPED_EXISTS` |
| New versions (V objects) | A later package version may add a version row (`create-version`) to a V object **that the package created or adopted**. It never edits or ends a version created by a user |
| User changes win | If a package-created M object has a later `master_change_log` UPDATE, or a V object has a user-created version, the item is `SKIPPED_MODIFIED` and listed in the result. The package never overwrites |
| Idempotent | Re-applying the same package version creates nothing (07, 11.4 unchanged) |
| Whole-package validation | VALIDATE mode checks the whole package: references resolve, role accounts satisfy the role table (07, 6.3), no overlapping rules, every tax code has determination (07, 11.3), inputs complete, `requires` satisfied. Apply is one transaction (ADR-056) |
| Newer version | `config-packages:diff` (read) shows, per company, what a newer version would create or skip, without writing |
| Rollback | None. Configuration is never deleted (ADR-073). A wrong apply on a company that has not posted is corrected by deactivation and a new company, on a company that has posted by a correcting version |

### 7.1 Tables

`applied_config_package` (E, existing, 07, 10.2) gets: `country_code`, `kind`, `state`, `manifest_digest` (= `content_digest`), `inputs_digest`, `requires_snapshot` (list of package codes and versions applied), `verification_summary` (counts per status), `app_content_digests`.

`applied_config_package_item` (E, new): PK (`tenant_id`, `applied_id`, `item_no`). Columns: `item_id`, `entity_type`, `entity_id` (canonical key text), `action` (CREATED, VERSIONED, SKIPPED_EXISTS, SKIPPED_MODIFIED), `valid_from`. Insert only. It is what lets a later version know which objects it may extend, and gives the audit trail from an object to the verification record of its source item.

### 7.2 Commands

| Command | Permission | Notes |
|---|---|---|
| `config-packages:apply` | `config.apply` | As 07, 11.4, plus `package_ref` (code and version), `inputs`, VALIDATE mode. Writes `applied_config_package` and its items, the policy rows and the outbox event `package.applied` (09, 4.8 PACKAGE_APPLIED) |
| `config-packages:diff`, `:list-applied`, `:get` | `read.config` | Read only |

New error area `PACKAGE`: `NOT_RELEASED`, `DIGEST_MISMATCH`, `UNKNOWN_FORMAT_VERSION`, `REQUIRES_NOT_SATISFIED`, `INPUT_INVALID`, `UNKNOWN_POLICY_KEY`, `POLICY_VALUE_NOT_AVAILABLE` (for example `PAYMENT_DATE` before ADR-199), `REFERENCE_UNRESOLVED`. To be added to 06, 2.4.

## 8. Worked package: DK (baseline)

Package `DK` 0.1.0 (country owner: Per Amby), kind COUNTRY, requires `STD_SAFT_OECD`, `STD_EN16931`, `EU_VAT`. State DRAFT. Contents and their test status:

| Content | Class | Expected shape | Test result |
|---|---|---|---|
| Public standard chart of accounts (version 20260101), used directly | Core | One account set per company, `account_no` = `standard_account_code`. The standard allows a suffix after the number (horizontal dimensioning) and forbids new numbers. Headings and sums go to app content | Holds. The core cannot enforce "no new numbers" (F-DK-12) |
| Tax class, return boxes STANDARD and EC_LIST | Core | Box codes are symbolic (`DK_B_SALES_VAT` style), labels in app content | Holds |
| Tax codes: the official public VAT code list (72 codes), 55 included | Core | Codes, rates, return text and business-type profiles from the list | Holds, including partial deduction since ADR-201 to ADR-206 (the first test showed it did not, F-DK-11). 17 special-scheme, refund, OSS and local-VAT codes are left out |
| Registration inputs | Core via inputs | Filing frequency half-yearly, quarterly or monthly by turnover bands (UNVERIFIED). The core does not compute turnover | Holds. The band advice is app content (row 13 of section 2) |
| EC list | Core | `ec_list_frequency` MONTHLY or QUARTERLY | Holds |
| Number series | Core | Unique, sequential. Gapless `is_legal` is stricter than the EU rule (UNVERIFIED) and acceptable | Holds. `NUMBERING_ORDER` = POSTING_ORDER, `CLEARING_IN_LEGAL_SERIES` = NO (UNVERIFIED) |
| Retention | Core | 5 years, start point differs between sources: end of financial year or document date (UNVERIFIED, source of the doubt below) | Holds with `FISCAL_YEAR_END` or `RECORD_DATE`, both exist (12, 4.1) |
| SAF-T | Core mapping, app file | OECD base data set from the core, Danish national file by an app. The exact Danish requirement is not confirmed in the sources found | Holds. A gap analysis of the Danish file against the OECD base is a package task before RELEASED |
| E-invoicing | App | Public-sector exchange by Peppol BIS Billing 3 (UNVERIFIED). Endpoint ids as `electronic_address_scheme` in the EN 16931 mapping | Holds |
| Payment instruments | App, task 3 | FIK and Betalingsservice style identifiers | Slot only |

The draft package is in `country-packages/DK/` (v0.3.0, state DRAFT, 638 items, none VERIFIED, 55 of the 72 official VAT codes including partial deduction). It uses the official public standard chart (version 20260101) and the official VAT code list directly, so the chart rows of the table above are no longer a template. Findings from writing it are in its CHANGELOG, among them the missing EU key in tax determination (ADR-200) and the need for partial VAT deduction (F-DK-11). Manifest example (shortened):

```json
{
  "format_version": "1.0",
  "package_code": "DK",
  "package_version": "0.1.0",
  "kind": "COUNTRY",
  "country_code": "DK",
  "state": "DRAFT",
  "requires": [
    {"package_code": "STD_SAFT_OECD", "range": "^1"},
    {"package_code": "STD_EN16931",   "range": "^1"},
    {"package_code": "EU_VAT",        "range": "^1"}
  ],
  "scope_map": {"tenant": ["tax_class", "tax_return_box"], "company": ["gl_account", "tax_code", "number_series", "retention_rule"]},
  "inputs": [
    {"name": "vat_id", "type": "text"},
    {"name": "filing_frequency", "type": "code", "allowed": ["MONTHLY", "QUARTERLY", "HALF_YEARLY"]},
    {"name": "role_vat_settlement_account", "type": "account_ref", "default": "acct_vat_settlement"}
  ],
  "owner": "DK country owner",
  "legal_reviewer": "to be named"
}
```

A core object (shortened). Note `ref`, `valid_from` and the `item_id` that points to the verification record:

```json
{
  "item_id": "DK-TAX-0001",
  "ref": "tc_dk_out_std",
  "resource": "tax-codes",
  "code": "DK_OUT_STD",
  "type": "OUTPUT",
  "version": {"valid_from": "2000-01-01", "rate": "25.0000", "box_map": [{"box": "bx_sales_vat", "side": "PRIMARY"}]}
}
```

## 9. Model test against DE and SE

Test countries (decided 2026-10-14): **DK** baseline, **DE** and **SE**. DE stresses rule density, a cash VAT scheme and a retention start on the calendar year. SE stresses a second cash scheme with a year-end rule, a different retention anchor and a country without mandatory SAF-T (the national file is SIE). NO was dropped from this round and is the natural next test for what DK, DE and SE do not cover (section 9.1).

All legal inputs are UNVERIFIED indicative (sources at the end).

| # | Test input | DK | DE | SE | Model result |
|---|---|---|---|---|---|
| T1 | VAT period length | 1, 3 or 6 months | Monthly, quarterly | Monthly, quarterly or yearly | Holds. 08, 4.2 covers 1, 3, 6 and 12 fiscal periods. A two-month period (NO, not in this round) is **not** added now |
| T2 | Cash VAT (tax point at payment) | Not assumed | **Yes**, optional scheme with a turnover limit (800,000 EUR prior year in one source) | **Yes**, the cash method up to 3 million SEK annual turnover, with all open receivables and payables recorded at year-end closing (one source) | **Gap in both.** `tax_date` is L1 and never updated (08, Q11, ADR-077). Two countries now need the mechanism. Decided: not in release 1 (ADR-199). The SE year-end rule shows that the future tax-point event must also allow a **forced point at year-end** for unpaid items |
| T3 | Retention start | Fiscal year end or document date (sources differ) | End of the **calendar year of the document** | End of the **calendar year in which the fiscal year ended**, 7 years (one source) | **Gap, and the two countries differ.** `start_event` has FISCAL_YEAR_END and RECORD_DATE. DE counts from the calendar year of the record, SE from the calendar year of the fiscal year end. Needs two values, see ADR-198 |
| T4 | Retention period by class | 5 years | 8 years vouchers and invoices, 10 books, 6 commercial letters | 7 years for accounting records | Holds: `retention_rule` is per record class. The mapping of legal categories to record classes is a package decision to verify |
| T5 | VAT return structure | Boxes | Boxes | Boxes (rutor, UNVERIFIED name) | Holds: `tax_return_box` with symbolic codes, labels in app content |
| T6 | EC list | Monthly or quarterly | Yes | Monthly for goods, quarterly if only services (one source) | Holds: `ec_list_frequency` is one value per registration. A company that sells goods and services chooses MONTHLY |
| T7 | National SAF-T | A Danish file is not confirmed | Not required in the sources found | **Not introduced** (one vendor source). SIE is the national exchange format | Holds. The OECD base data set exists in the core. SIE and DATEV are app content (row 12 of section 2). Nothing in the core depends on a country having SAF-T |
| T8 | E-invoice | Public sector | B2B receipt since 2025, issue from 2027 and 2028 in phases | Public sector (Peppol BIS Billing 3), B2B optional | Holds: EN 16931 data set is neutral. Syntax, CIUS and mandate dates are app content |
| T9 | Numbering | Sequential unique | Sequential unique | Not checked | Holds. **No tested country needs DATE_ORDER or CLEARING_IN_LEGAL_SERIES**, which is evidence, not proof |
| T10 | Mandatory invoice texts | Yes | Yes | Yes | Holds as code plus one text in the core. Languages are app content |
| T11 | Chart of accounts template | Public standard chart | SKR03 or SKR04 (common, not mandatory) | BAS (common, UNVERIFIED) | Holds: template plus `standard_account_code`. Several templates per country are package options chosen by input |
| T12 | National export formats | | DATEV | SIE | Out of the core, app content |
| T13 | Three-country tenant | | | | Holds: one tenant, three companies, each applies its package. Shared `EU_VAT` and standards packages are applied per company scope. Intra-EU between the companies uses the determination keys of 08 |

Findings in one line: the model holds for all three countries with **one enum extension (T3, two values), one deferred mechanism that DE and SE both need (T2, decided: not in release 1), and no new tables for country logic**. New tables for package administration: `company_policy_setting`, `applied_config_package_item`, extra columns on `applied_config_package`.

### 9.1 What the test does not cover

- Whether the retention, numbering, tax point and SAF-T statements above are correct. That is the verification gate.
- **Not tested because all three are EU members:** a non-EU country (EU membership, import and export without the intra-EU scenarios, the choice not to require `EU_VAT`), a two-month VAT period, and a mandatory national SAF-T variant with schema versions. NO (EEA) tests all three and is the next candidate. The corresponding design choices (ADR-195 requires-lists, `variant` of `SAFT_DATASET`) are therefore untested.
- A second VAT registration in another country of one company (08, 4.1 supports several, not tested here).
- A country with real-time reporting or clearance: needs an acknowledgement flow that is an app concern and may need a core status. Deferred.

## 10. Assumptions

| Ref | Item |
|---|---|
| A1 | Packages are authored and released outside the core. The core stores only apply records, digests and items |
| A2 | The legal reviewer may be outside Implement. Their record is the evidence |
| A3 | A package never contains personal data and never contains a tenant's own facts (inputs are not stored as content) |
| A4 | The standards packages track the published standard versions and record the version they were verified against |
| A5 | One country package per company at a time. Switching a company to another package version is the apply of a newer version (7) |

## 11. Decisions of 2026-10-14 and open points

| # | Question | Decision |
|---|---|---|
| Q1 | Test countries | **DK, DE and SE** (NO later, section 9.1) |
| Q2 | Cash VAT in release 1 | **No.** The hook is fixed (ADR-199), `PAYMENT_DATE` is unavailable, DE and SE packages declare the cash scheme unsupported |
| Q3 | Country owner and legal reviewer | **DK: Per Amby** (country owner). Legal reviewer for DK still to be named. DE and SE owners to be named when their packages start |
| Q4 | Owner of `STD_SAFT_OECD` and `STD_EN16931` | **Platform team** |
| Q5 | Signing and repository | **Decided in 20, section 11 (ADR-217, accepted 2026-10-19):** ECDSA P-256 signature over a generated signing manifest, key in KMS or HSM, two trust sets (development: TEST and REHEARSAL tenants, release: PRODUCTION), immutable versioned repository, archive uploaded to the core API |
| Q6 | Policy catalogue | **Three keys** as in section 5 |
| Q7 | ADR-200: derived EU keys `origin_eu`, `destination_eu` | **Yes.** ACCEPTED |
| Q8 | Partial input VAT deduction (08 Q6, DK F-DK-11) | **In release 1 for DK** (ADR-201). The design pass is the next work item |
| Q9 | ADR-193, 196, 197, 198 | **ACCEPTED** |
| Q10 | `EU_VAT` | **Own thin package** with `tcl_goods`, `tcl_services` and the scenario description. To be written |
| Q11 | DK legal reviewer | **To be named later.** The package stays DRAFT |
| Q12 | DK account choices (RETAINED_EARNINGS 6940, realised FX netted on 3610-1, rounding on suffix accounts of 2720) | **Approved** |
| Q13 | Where the standard-chart number rule is enforced (F-DK-12) | **Outside the core** (ADR-202) |

Written 2026-10-14 as DRAFT: `EU_VAT` 1.0.0, `STD_EN16931` 1.0.0 and `STD_SAFT_OECD` 1.0.0 (country-packages/). Their findings: G1 and G2 (no company address and contact, ADR-207 PROPOSED), the EN 16931 edition decision (the 2026 edition is not backward compatible), G7 (should the e-invoice completeness check read the package). Open: (a) named legal reviewer for DK, (b) design of partial deduction (ADR-201), (c) before the DE and SE packages start: country owners and the gap analysis of the national files, (d) NO as a later test round, (e) the sign convention of `tax_code_version_box.sign` (the package convention is proposed for 08, 6.4 and 07, 8.3 unless you object).

## 12. ADRs raised by this spec (ACCEPTED 2026-10-14, full text in 01-adr-log)

ADR-193 package classes and ownership boundary. ADR-194 closed policy catalogue. ADR-195 layered packages and standards packages. ADR-196 apply and update semantics with item records. ADR-197 verification gate and lifecycle. ADR-198 retention start values found by the country test (two calendar-year values). ADR-199 tax-point event for cash VAT. ADR-200 derived EU keys in tax determination (found by the DK draft). ADR-201 partial input VAT deduction in release 1 (scope, design pending). ADR-202 the standard-chart number rule is enforced outside the core.

## 13. Deltas to earlier specs (the ADRs are accepted. **All rows are applied** as of 2026-10-19, the ERD row included)

| Spec | Change |
|---|---|
| 07, 11.4 | **Applied 2026-10-19** (07, 3.2, 10.2, 10.3, 11.4, 13.2g). Replace "The format is a later spec (Q10)" by a reference to this spec. Add `package_ref`, `inputs`, items, user-changes-win. 10.2 gets the new columns and `applied_config_package_item`. New table `company_policy_setting` in 3.2 (V, `write.config`). Q10 closed |
| 12, 4.1 | **Applied 2026-10-19.** `retention_rule.start_event` adds CALENDAR_YEAR_END_OF_RECORD (DE) and CALENDAR_YEAR_END_OF_FISCAL_YEAR (SE) |
| 08, 7.1 and Q6, 02, 09 | ADR-201 to ADR-206: partial and non-deductible input VAT in release 1. **Applied 2026-10-14** in 02, 06, 07, 08, 09, 10, 11, 13 (design in 19) |
| 07, 8.4 and 02 | ADR-200: nullable `origin_eu` and `destination_eu` on `tax_determination_rule`, resolved values stored on `journal_line_tax`, matching algorithm updated. **Applied 2026-10-14** in 02, 06, 07, 08, 09 |
| 06 | **Applied 2026-10-19** (2.4, 7.4, 8.2, 13.8). Error area `PACKAGE`, commands `config-packages:diff`, `:list-applied`, `:get` |
| 09, 3 and 4.8 | **Applied 2026-10-19** (3, 6, 9.6). Resources `applied-config-package-item`, `company-policy-setting`. Event `package.applied` already maps to PACKAGE_APPLIED |
| 00, section 5 | "Country package format" moves from not specified to this spec (DRAFT) |
| ERD | **Applied 2026-10-19:** `company_policy_setting` on page 5, `applied_config_package_item` and the new columns of `applied_config_package` on page 7 |

## Sources (secondary, UNVERIFIED, used only as test inputs)

- DK bookkeeping act and digital systems: [KPMG DK](https://kpmg.com/dk/da/indsigter/moms-told-og-punktafgifter/nye-krav-om-digital-bogfoering.html), [Lovguiden, bogføringsloven kapitel 5](https://www.lovguiden.dk/loven/bogføringsloven/kapitel-5)
- DK VAT periods (ML § 57): [tax.dk, Juridisk Vejledning A.B.4.2.1.2](https://tax.dk/jv-2025-1/ab/A_B_4_2_1_2.htm)
- DE cash VAT, e-invoicing and retention: [Forvis Mazars on thresholds](https://www.forvismazars.com/at/en/insights/tax-news/newsletter-september-2025/umsatzgrenze-fuer-kleinunternehmer-innen), [e-invoicing timeline](https://www.advisori.de/en/blog/e-invoicing-mandate-germany), [retention periods](https://kostenlose-erechnung.de/ratgeber/e-rechnung-archivierung-pflicht/)
- SE retention, cash method and VAT periods: [Spiris on archiving](https://www.spiris.se/blogg/foretagsredovisning/arkivering-bokforing), [Spiris on the cash method](https://www.spiris.se/blogg/foretagsredovisning/vad-ar-kontantmetoden), [Skatteverket on VAT periods](https://www.skatteverket.se/foretag/moms/deklareramoms/narskajagdeklareramoms.html)
- SE SIE, e-invoicing and EC list: [Fonoa on Sweden](https://fonoa.com/countries/sweden/e-invoicing-and-digital-reporting), [Stripe on the EC list](https://stripe.com/se/resources/more/ec-sales-list-in-sweden)
