# Database scripts

PostgreSQL schema of the finance core, first slice: organisation, currency and chart of accounts, fiscal calendar, number series, the ledger (documents, lines, chain heads), the command log and the outbox. It implements spec 20 (ADR-208, ADR-209, ADR-210) and the tables of specs 02, 04, 06, 07 and 15.

Status: DRAFT, 2026-10-21. Tested against PGlite (PostgreSQL in WebAssembly), not yet against Supabase or a server PostgreSQL. Nothing here is verified against production behaviour.

## Files

| File | Content |
|---|---|
| `migrations/0001_foundation.sql` | Schemas `platform` and `core`, domains, roles, `core.current_tenant_id()` |
| `migrations/0002_platform_reference.sql` | `platform.tenant`, `ref_currency`, `ref_country` |
| `migrations/0003_organisation.sql` | Settings, currency, calendar, group, company, ledger, group membership, exchange rate, chart of accounts |
| `migrations/0004_calendar_and_numbers.sql` | Fiscal year and period, `period_event`, posting window, number series and counters |
| `migrations/0005_ledger.sql` | `chain_head`, `journal_document`, `journal_line`, `command_log`, outbox |
| `migrations/0006_security_and_integrity.sql` | Forced row-level security, grants, append-only and lock-class triggers, deferred integrity checks |
| `migrations/0011_account_roles.sql` | `account_role_assignment` (07, 6.3) with the account rules of the role table, and the `gl_account` guard for what a role fixes |
| `migrations/0012_change_logs.sql` | `change_log` (02, 8) and `master_change_log` (07, 10.1): append-only, shape checks, insert grants. No command writes them yet |
| `migrations/0013_l2_guard.sql` | The L2 columns `header_text`, `external_reference` and `line_text` open, with a matching `change_log` row of the same transaction and never in a hard-closed period (F-IMPL-3) |
| `migrations/0014_open_items.sql` | `journal_line_open_item` (02, 5A) for GL_OPEN_ITEM accounts: L1, L2 and S guards, the commit-time backstop for the rows, and the entity type in the L2 guard |
| `migrations/0015_clearing.sql` | `clearing` and `clearing_item` (03, 4), the clearing chain checks, the role `cmd_clearing`, and the commit-time rule that the S columns of an open item are what the clearing items say. No command writes them yet |
| `migrations/0016_open_item_status.sql` | The status of an open item looks at every currency (F-IMPL-20) |
| `migrations/0017_business_partner.sql` | `business_partner`, `business_partner_pii`, `partner_company_relation` and `payment_terms` (07, 9.1 to 9.3 and 9.6), with the guards for anonymisation, the kind of a referenced partner and the S columns. The first master tables written by commands |
| `migrations/0018_open_item_partner.sql` | The partner and the payment terms of an open item, and the partner of a clearing, refer to their tables. A CONTROL item needs a due date, at posting and for good |
| `migrations/0019_retention_rule.sql` | `retention_rule`, versioned and insert-only: the class and the country of a rule never change between its versions. Written by `cmd_config`, read by every command role |
| `migrations/0020_document_legal_snapshot.sql` | `document_legal_snapshot`, one per document, insert-only. A deferred trigger rejects a document without one at commit |
| `migrations/0021_posting_reads_partner.sql` | The posting role reads the snapshot columns of `business_partner_pii` (column grant), for the legal snapshot of an invoice |
| `migrations/0022_dimensions.sql` | `dimension_type` (the four standard types of every tenant come from a trigger on `platform.tenant`), `dimension_value`, `account_dimension_rule`, `journal_line_dimension`, the keys of the typed columns of `journal_line`, the checks of what a line may carry, and the L2 guard of the typed columns and of the free value |
| `migrations/0023_vat_configuration.sql` | `platform.ref_country_eu_membership`, `tax_class`, `tax_return_box`, `company_vat_registration`, `tax_code`, `tax_code_version` and `tax_code_version_box` (07, 8.1 to 8.3, 08, 4.1), the key of `gl_account.default_tax_class_id`, the guards of what a version may hold, and the marking of the VAT accounts as tax accounts. No posting uses a tax code yet |
| `migrations/0024_vat_determination_and_tax_lines.sql` | `core.partner_tax_registration`, `tax_determination_rule` and `tax_determination_alternative` (07, 8.4, 9.4), `journal_line_tax` and `journal_line_tax_base` (02, 7.1, 7.2), the guards of a rule version, and the rule that a tax account accepts only tax lines (08, 6.7) |
| `migrations/0025_vat_deduction_keys.sql` | `core.vat_deduction_key` and `vat_deduction_rate` (07, 8.6, 19, 3.2), the key of the non-deductible line in `journal_line_tax_base`, and the check that a tax code version names a key of its registration |
| `migrations/0026_vat_returns_and_settlement.sql` | `core.vat_return`, `vat_return_line` and `vat_return_tax_line` (08, 6.3 to 6.5) with their guards and the VAT_RETURN chain check, the line type `VAT_SETTLEMENT` and its guards, the registration of the role VAT_SETTLEMENT |
| `migrations/0027_tax_evidence.sql` | `core.journal_line_tax_evidence` (08, 5.3), added by `change-open-fields` in every period status, append-only; `change_log` takes the period status HARD_CLOSED for TAX_EVIDENCE |
| `migrations/0028_applied_config_package.sql` | `core.applied_config_package` and `applied_config_package_item` (18, 7.1): the record of a package apply, insert-only |
| `seed/dev_seed.sql` | A TEST tenant with a Danish company, reference data, ten accounts and the six roles that activation needs. No fiscal year |
| `tests/001_protection.sql` | 117 checks of isolation, grants, triggers and integrity, including the two change logs and the L2 guard |
| `dist/finance-core-schema.sql` | The migrations in one file, generated by `tools/build-all.mjs` |
| `tools/run-pglite.mjs` | Applies everything twice and runs the tests in-process |

## Run it in Supabase (or any PostgreSQL 15 or newer)

1. Create a project in an EU region. Use the **direct** connection for the application, not the pooler.
2. Open the SQL Editor and run `dist/finance-core-schema.sql` once, **in an empty database only** (F-IMPL-32). A database that exists is brought up to date with `financecore-platform db migrate` (see below), never by running the file again. Then, for development only, `dist/finance-core-dev-seed.sql`.
3. Give the login roles a password and the right to log in. Choose the passwords yourself and keep them out of the repository:
   ```sql
   alter role core_app login password '<choose>';
   alter role core_job login password '<choose>';
   alter role core_retention login password '<choose>';
   ```
4. Check **Settings, API, Exposed schemas**: `core` and `platform` must not be listed. The scripts revoke the Supabase roles `anon`, `authenticated` and `service_role`, but the data API (PostgREST) would bypass the command pipeline if it were pointed at these schemas (P2).
5. The application connects as `core_app`, `core_job` or `core_retention` and **starts every transaction with**
   ```sql
   set local role cmd_post;                          -- the command role of this command, one of cmd_read, cmd_config, cmd_period, cmd_post
   set local app.tenant_id = '<tenant uuid>';        -- from the validated token
   ```
   The login roles are `NOINHERIT`, so a transaction that does not name a command role can do nothing.

To run the tests in Supabase, grant the command roles to the SQL Editor user first (`grant cmd_read, cmd_config, cmd_period, cmd_post, cmd_retention to postgres;`) and run `tests/001_protection.sql`. It ends with a rollback.

## Things you need to know

- **The migration role bypasses RLS.** In Supabase `postgres` has `BYPASSRLS`, in a plain PostgreSQL a superuser does. RLS is forced for everyone else, which is why the tests check isolation as `cmd_read` and `cmd_config`. The application must never connect as `postgres`.
- **Hashes are not computed in SQL** (ADR-211). The row hash is stored as supplied by the application library. SQL checks the links: the document links to its predecessor, sequence 1 starts at the genesis value of 02, 9.3a (computed here with `sha256`, from the same text as the C# library), and the chain head equals the newest document at commit.
- **Deferred checks run at commit**: a document balances in transaction, local and group currency (and reporting currency where present), has at least one line, and its chain link and head are in step. Inside one transaction `set constraints all immediate` forces them earlier.
- **No hard delete** anywhere in the transaction layer (ADR-073). `cmd_retention` may purge the outbox. Tenant deletion and anonymisation get their own role and scripts later.
- **Not in these scripts**: tax, dimensions, partners and open items on CONTROL accounts, clearing, revaluation, year-end, VAT, stock, orders, anchors, change log, extension attributes, exports. `journal_line_open_item` and the tax tables are specified in 02 but not created yet.

## Findings from writing the SQL

| Ref | Finding |
|---|---|
| F-IMPL-1 | `ledger_id` is used throughout the specs and "the company's single ledger" is mentioned (06, 5.2), but no spec defines a ledger table. `core.ledger` is a minimal proposal (one default ledger per company) and needs a delta to 07 |
| F-IMPL-2 | The period status rule of 04, 8 says the matching `period_event` must be in the same transaction. A check on the row's transaction id breaks under savepoints, so the guard requires instead that `last_event_id` names the latest event of the company with the same target status, year and period. The same-transaction part stays a rule of the command |
| F-IMPL-3 (resolved for the text fields, see below) | The L2 columns (`header_text`, `external_reference`, `line_text`, typed dimensions) cannot be updated at all yet. 02, 8 allows an L2 change with a `change_log` row in the same transaction. The guard opens when `change_log` and the change command exist |
| F-IMPL-4 | A reporting-currency amount, its currency and its rate are all present or all absent on a line, and a document uses it on all lines or none. 02, 5.2 says only "null if not configured". Stated here as a check, to be confirmed in the spec |
| F-IMPL-5 | The deduction and tax tables, `journal_line_open_item` and `change_log` are not created. The line hash already reserves their blocks (02, 9.3a) |
| F-IMPL-6 | `ROUND` is used in 03, 05, 08 and 19 without a rule for the half case. The posting command rounds half away from zero (symmetric: a line and its mirror always give opposite amounts). Needs a sentence in 03 |
| F-IMPL-7 | Rounding lines need configured rounding accounts per currency (design principle 4 of 00), The role table exists since 0011: a document whose local, group or reporting amounts do not balance after rounding gets one ROUNDING line (see F-IMPL-16), or is rejected with `BALANCE.ROUNDING_ACCOUNT_MISSING` when the role has no row. `LIMIT.ROUNDING_DIFFERENCE` is gone. The error codes `AUTH.PERMISSION_DENIED`, `COMPANY.NOT_ACTIVE`, `LEDGER.NOT_FOUND`, `CURRENCY.NOT_ACTIVE`, `CURRENCY.UNKNOWN`, `ACCOUNT.*`, `DOCUMENT.NOT_BALANCED`, `DOCUMENT.TYPE_NOT_ALLOWED`, `SCHEMA.DUPLICATE_LINE_REF`, `CONFIG.GROUP_MEMBERSHIP_MISSING`, `POSTING.SOFT_CLOSE_OVERRIDE`, `RATE.FALLBACK_USED`, `SYSTEM.*` and `LIMIT.*` are not named in the specs. They are placeholders to be settled in 06, 2.3 |
| F-IMPL-8 | 04, 5.2 does not say which assignment wins when one exists for the ledger and one for all ledgers. The slice takes the ledger-specific one first, then the latest `effective_from` |
| F-IMPL-10 | Two gaps in 02, 9.2 and 04, 3.5. (a) The byte layout of the `period_event` row hash is not given. 04, 3.5a now defines it, with a second implementation and test vectors. (b) Nothing says who creates a chain head. `fiscal-years:open` creates the PERIOD_EVENT head and `companies:activate` the LEDGER and PERIOD_EVENT heads, if missing, at sequence 0 with the genesis value. This follows the grants of 0006 (the config, period and post roles may insert a head) |
| F-IMPL-11 | `cmd_config` could not move `outbox_head`, so a configuration command could not emit its event (06, 6.3 class 7). Migration 0009 grants `update (last_position)`. The guard trigger keeps it forward-only. Supabase needs `0009` run once |
| F-IMPL-12 | 07, 11.3 requires a number series "for each document type in use" and does not say how a type becomes "in use". The slice checks only JOURNAL. Besides the rows of 11.3 the slice checks what the posting command needs: a group membership valid today, an active default ledger. The rows that need tables not yet built (account determination, VAT registration, migration roles, retention, e-invoicing, consolidation) are returned as the warning `LIMIT.READINESS_CHECKS_NOT_IMPLEMENTED`, never as passed. `fiscal-years:open` does not write an event per period: the YEAR_OPEN event is year-level and the periods take its status (04, 3.4 allows `period_no` null) |
| F-IMPL-13 | `periods:hard-close` is specified to lock the LEDGER and CLEARING heads before the PERIOD_EVENT head (04, 6.3, A1). The period role may lock only its own chain: the restrictive policy of 0006 hides other heads from its `FOR UPDATE`, which is deliberate (02, 9.2: the owning role moves the head). The command therefore locks the PERIOD_EVENT head and then the year and period rows exclusively. That still keeps a posting out of a closing period: the posting holds the period row in share mode after its own head locks, and the close needs it exclusively, so either the close waits for the posting or the posting sees HARD_CLOSED after the close. Two parts of the command are not implemented because their tables do not exist: the anchor request (A2, `chain_anchor`) and the revaluation stale check (D4, 05). When anchors are built, the head locks need a decision (a read-only lock function, or a wider policy for the period role) |
| F-IMPL-14 | Placeholders in 06, 2.4: `PERIOD.STATUS_INVALID` (a status command from a status it does not allow). The vocabulary of the reopen `reason_code` is not given (06 says "vocabulary in 06"): any code up to 50 characters is accepted. The event of `periods:reopen` is PERIOD_REOPEN also for SOFT_CLOSED to OPEN, and the rule "no later period hard-closed" is applied to that case too, as the row in 7.3 states it for the command. `period.status_changed` uses the entity type FISCAL_PERIOD with the key `company_id/fiscal_year/period_no`. Documents use the entity type DOCUMENT as 06, 2.4 lists it |
| F-IMPL-15 | `journal-documents:reverse` (06, 5.3) leaves some points open. (a) `origin` of the mirror is not stated: EXTERNAL is used, as for the command family of 5.2. (b) `document_date` of the mirror is not stated: it is the reversal date, as is the posting date. (c) The mirror needs a number series for the type REVERSAL (04, 5.2): without an assignment the command answers `SERIES.NO_ASSIGNMENT`. The readiness check of 11.3 does not yet ask for it. (d) `ledger.document_reversed` (15, 6) names no entity: the event refers to the original (`entity_key` company/document of the original, change REVERSED), and `chain_ref` and `ledger_seq` are those of the mirror. (e) The text columns `header_text` and `line_text` (L2) are not copied. (f) `REVERSAL.HAS_CLEARED_LINES` (ADR-039) and the mirror open items with automatic clearing (B3) need the open item and clearing tables. Until they exist, a document with open item lines cannot be posted either, so no reversal can meet one. Migration 0010 adds the index that finds a document by its id alone; Supabase needs `0010` run once |
| F-IMPL-9 | The posting role takes `FOR SHARE` on the period and year rows (06, 9). PostgreSQL requires UPDATE privilege on one column for that, and 0006 gave `cmd_post` none. Migration 0007 grants `update (last_event_id)` on both tables. The guard trigger rejects every change of it that is not the matching latest `period_event`, so the role can lock the row but not change it. Tests: `PostingRoleCanLockPeriodRowsButNotChangeThem`, `PostingRoleCannotPointAPeriodAtAnotherEvent`. Supabase needs `0007` run once |
| F-IMPL-16 | Rounding and role tables (07, 6.3). (a) The rate table of 02, 5.2 (D9) puts ROUNDING under "TC non-zero", but a rounding line exists because TC balances and the derived currencies do not. The slice posts one ROUNDING line per document: `amount_tc` 0, the three differences as its LC, GC and RC amounts, rates 1, `rate_type` DERIVED, no `line_ref`, the line after the base lines (06, A2). The spec says "rounding lines per currency": one line carries all three currencies here. Needs a row in D9. (b) The ROUNDING account is resolved by the transaction currency, then the default row. 07 does not say which currency is meant. (c) Roles resolve at `now()`, as 07, 6.3 says, not at the posting date: a back-dated posting uses the role in force today. (d) `account-role-assignments:create` is a name by the pattern of 07, 11.1 (`{resource}:create-version` for V tables, a first row is a version too). Its event is `config.version_created` with `is_audit` true as 15, 7 says, and a V table has no `master_change_log` row (07, 10.1), so the event refers to the version row. (e) A new version must have an `effective_from` later than the newest version of the same key (`CONFIG.VALID_FROM_NOT_LATER`). `CONFIG.VALID_FROM_IN_CLOSED_PERIOD` is not checked. (f) Not built: `VAT_SETTLEMENT` (answers `LIMIT.VAT_REGISTRATION_NOT_BUILT`, `vat_registration_id` has no FK and `tax_account` is not set by the system), "no mandatory dimension rules" for `RETAINED_EARNINGS` and the migration roles (`account_dimension_rule` does not exist), deactivation of a referenced role account. (g) Readiness: ROUNDING and the two FX_UNREALISED roles need a default row or a row for each of the local, group and reporting currencies. 07, 11.3 says "each currency in use", which the core cannot know. A role account that is inactive or posting-blocked fails `ROLE_ACCOUNT_RULES` (`ACCOUNT_USABLE`), which 07 does not say. Supabase needs `0011` run once |
| F-IMPL-17 | 03, 6 gives the formula of the clearing row hash but not the byte layout, nor that of `items_digest`. 03, 6.1 defines both, in the form of the ledger and period event chains, and the vectors are reproduced by a Node and a C# implementation |
| F-IMPL-39 | 18 specifies the package format and the apply (create-only, inputs, the gate) but the first slice had only the in-code package without tax. Built: the VAT content of `country-packages/DK` applied through the command handlers by the platform CLI (07, 13.2p). Found: the package lacked the EN 16931 category that the core requires for reverse-charge codes (F-DK-25, fixed in 0.4.1). The apply record is built in 0028 and the VAT_SETTLE series is assigned by the apply (a first apply left `vat-returns:settle` without a series). The signature and the other core content are not built |
| F-IMPL-38 | 08 specifies the EC list, OSS, the evidence and the preview but not: where the partner of a tax line comes from (the open items of its document), the sign of the amounts of an EC list and an OSS return, the frequency of an EC list (`ec_list_frequency`), what a missing partner or supply kind is, how a row of evidence is added with the fields of `change-open-fields` (the sub_key), that a hard-closed period needs a status in the `change_log`, and which preparation checks a preview makes. The built choices are in 07, 13.2o |
| F-IMPL-37 | 08 specifies the return, its settlement and the reversal but not: a read permission and the codes for a missing return, a missing VAT period and a settlement with nothing to settle, the line type and the rates of a settlement line, the posting date of a settlement without a given date, which snapshot a replacement replaces when there are several, how a registration is found without its company, and that the reversal of a settlement reuses the reversal of a document. The built choices are in 07, 13.2n |
| F-IMPL-36 | 19 and 07 specify the partial deduction but not: how a key and its rates are addressed by a command (surrogate id), what a supplied deductible amount of another sign means, how the supplied amounts of a group add up, the order of the generated lines, and a cost line on an account with open items. The built choices are in 07, 13.2m |
| F-IMPL-35 | 06, 07 and 08 specify the determination and the tax lines but not: how a registration of a partner is written (own commands, with a surrogate id), what makes a line taxed when no code is supplied (a code, or MANDATORY on the account), whether the determination runs for a supplied code (it does), what a determination rule overlaps with, how the supplied tax is checked per group, which date reads what, and what a mirror of a tax document carries. The built choices are in 07, 13.2l |
| F-IMPL-34 | 07, 8.3 and 08, 4.1 specify the VAT configuration but not: how the status of a registration is stored (the flag `active`), when the type of a code is fixed (once it has a version, because no posting uses codes yet), whether `reverse_charge` belongs to input codes only (a reading), which versions need an EN 16931 category, how a return box is addressed by a command (its key), and who sets `tax_account` (the database, at the first version that names the account). The built choices are in 07, 13.2k |
| F-IMPL-33 | 07 and 02 specify the dimension tables and the rules but not: how the standard types come to exist (a trigger on `platform.tenant` creates them), what deactivating a standard type answers, which codes a bad dimension value gets at posting (`ACCOUNT_RULE.DIMENSION_VALUE_INVALID` with a reason), whether the rules reach the generated lines (they do not), what a reversal does with the dimensions (it copies them and checks nothing), and whether a role may be given to an account with a mandatory rule (it may not). The built choices are in 07, 13.2j and 06, 13.9 |
| F-IMPL-32 | Found by running the tests against Supabase, not by any spec: applying `dist/finance-core-schema.sql` again over an existing database re-runs `grant select on all tables in schema core` (0006, 0015), which gave the posting, period and clearing roles table-wide read on `business_partner_pii` and made the column grant of 0021 meaningless (the posting role could read the contact data). The schema file is built for an empty database. 0021 now revokes the table grant first, and 001_protection.sql checks it |
| F-IMPL-31 | 20, 7 fixes the style of the API but not the claims of the token, the HTTP status of a refusal, the order of the payload check and P0, the shape of the OpenAPI document, or what the result of `chains:verify` is. 06, 7.4 names the kinds of break but the slice's verification named its breaks by code. The built choices are in 06, 13.9 and 20, 7 |
| F-IMPL-30 | No spec says how a tenant, a company or an exchange rate comes into being: 07 has no create command for them, 12, 13 only names provisioning as the platform operator's, and 07, 11.4 describes a signed package format that is not built. The slice has the platform CLI (below) with the operations `tenant create`, `company create`, `company setup` (a country package in code, `dk-minimal`), `company activate` and `rate add`. Rates are looked up per day with a fallback of `rate_fallback_days`, so a package cannot carry them and the operator adds the days that are posted to. The CLI uses the migration role, not a command role, for these operations, and the command handlers for roles, retention rules, the fiscal year and the activation |
| F-IMPL-29 | 02, 9 and 06, 7.4 verify the hash chains, but the legal snapshot is outside the hash (02, 4), so a removed or shortened snapshot is invisible to them. The slice adds three break kinds to the LEDGER verification. The built choices are in 06, 13.9 |
| F-IMPL-28 | 06, 5.2 and 02, 4 require a header partner for the invoice types and a snapshot "as of the posting date", but the partner master has no versions, the partner has no VAT registration yet, and the specs do not say which role of the relation an AR or AP type needs, whether a CONTROL line's partner must equal the header partner, or what a mirror's snapshot takes. The built choices are in 02, 4 and 06, 13.9 |
| F-IMPL-27 | 02, 4 says that the core builds the snapshot but not which commands write one (the difference document and the mirrors included), which dates the retention rule counts from for a document, what a snapshot holds before the header partner exists, in which phase a missing rule is found, and whether readiness fails or warns. The built choices are in 02, 4 |
| F-IMPL-26 | 12, 4.1 and 13 give the table and the command but leave open how the first version of a rule differs from a later one, what a later version may change, which time a new version starts at and whether it may be back-dated, and how a country is checked. The built choices are in 12, 4.1 |
| F-IMPL-25 | 03 says that clearing is per account and partner (R1 scope) but not whether the status of the partner matters for a clearing, nor which due date the offset line of a difference document has on a control account, where an open item must have one (02, 5A). The built choices are in 03, 5.1: only the existence of the partner is checked, the offset line takes the date of the clearing, and the mirror of it in a reset copies that |
| F-IMPL-24 | The specs say that a CONTROL line needs a partner and a due date (02, 5A, 06, 5.2) but not what happens to the due date of the mirror of such an item (a mirror open item has no due date of its own, and the backstop would reject a row without one), whether a restricted partner takes a new open item through a plain journal, which defaults of the relation reach a new item, or how a partner that is blocked, has no relation or has no role is told apart. The built choices are in 06, 5.2, and 02, 5A |
| F-IMPL-23 | 07, 11.1 gives the generic operations but leaves open what one command for a partner with its personal data and relations means: which version `expected_version` names, how a patch is read, what deactivation is for a partner and for terms, how the log treats personal data and the credit limits of a person, and what a relation change for a missing company does. The built choices are in 07, 13.2i |
| F-IMPL-22 | 03, 5.7 leaves open how a reversal that writes several clearings keeps their idempotency keys unique, in which order the items are written, whether the mirror's open items copy the legacy columns, and which role runs the reversal. `journal-documents:reverse` also had to switch to the clearing role for documents with open items. The built choices are in 03, 5.7, the break kinds of `chains:verify` for the clearing chain in 06, 13.9 |
| F-IMPL-21 | 03, 5.4 leaves open the date of a reset ("later"), the `clearing_method` of a reset, where the reason text goes, which number series the mirror document takes, and which events a reset writes. The built choices are in 03, 5.4 |
| F-IMPL-20 | 03, 3.1 does not say which transaction currency the difference document has when the items are in several currencies, nor the signs and order of its lines. 03, 4.3 gives the status of an open item by TC and LC, which cannot express the offset line of a difference that arises only in group or reporting currency (TC 0 and LC 0). The status now looks at every currency (migration 0016). The built choices are in 03, 3.1 |
| F-IMPL-19 | 03, 5.2 does not say what happens when a rounded partial amount is more than what remains of a line in a currency. The allocation takes the remaining amount, so that the partials of a line always add up to the line. 06, 7.1 gets the points settled while building `clearings:post` |
| F-IMPL-18 | 02, 10 names "the clearing command role" but no spec says what it may do. A difference document needs the LEDGER head, which a restrictive policy of 0006 gives to `cmd_post` only, so `cmd_clearing` cannot be a member of `cmd_post`: both policies would apply and no head could move. It has its own grants and a policy that allows CLEARING and LEDGER. The S columns of an open item change only by this role, and the database checks at commit that they equal the sum of the clearing items |

### Where the findings went in the specs (2026-10-26)

The deltas are PROPOSED, not accepted: they write down what the slice does, so that spec and code agree, and the owner of the specs confirms or changes them. ADR-228 to ADR-230 are in `01-adr-log.md.txt`.

| Finding | Spec delta | Code owes |
|---|---|---|
| F-IMPL-1 | 07, 3.1 and 5.4a (table `ledger`), 06, 5.2, ADR-228 | Nothing |
| F-IMPL-2 | 04, 8 ("Form of the same-transaction check") | Nothing |
| F-IMPL-3 | 02, 8 (the guard of an L2 column) and 06, 5.4 (points settled). Resolved for `header_text`, `external_reference` and `line_text` by migration 0013 and `DocumentChanger`. The typed dimensions, the free dimensions, the open-item fields and the tax evidence stay locked until their tables exist | The L2 change of a line dimension needs the dimension tables |
| F-IMPL-4 | 02, 5.2 (`amount_rc` row) | Nothing |
| F-IMPL-5 | Not changed. `change_log` exists since 0012 and `journal_line_open_item` since 0014. The deduction and tax tables are still missing | |
| F-IMPL-6 | 02, 5.2 "Rounding of derived amounts", 03, 5.2, ADR-229 | Nothing |
| F-IMPL-7 | 06, 2.4 (new rows) and 13.9, ADR-230 | Done 2026-10-26: `AUTH.PERMISSION_MISSING` in every command, `BALANCE.UNBALANCED_TC` in the posting command. The chain verification still names two breaks like command errors (`DOCUMENT.NOT_BALANCED`, `PERIOD.STATUS_MISMATCH`), see 06, 13.9 |
| F-IMPL-8 | 04, 5.2 ("Precedence"). The spec already had the rule, now stated for the case of a row that does not yet apply | Nothing |
| F-IMPL-9 | 04, 8 ("Row locks of the posting role") | Nothing |
| F-IMPL-10 | 04, 3.5 ("Who creates a chain head"). (a) was already in 04, 3.5a | Nothing |
| F-IMPL-11 | Not changed. 0009 is a grant, the rule of 06, 6.3 class 7 stands | |
| F-IMPL-12 | 07, 11.3 ("in use" for number series, ledger and group membership rows) | Nothing |
| F-IMPL-13 | 04, 13 open points 6 and 7. **Needs a decision before `chain_anchor` is built** | |
| F-IMPL-14 | 06, 7.3 (`PERIOD.STATUS_INVALID`, reopen reason vocabulary and event) | Nothing |
| F-IMPL-15 (f: closed by the automatic clearing at reversal, F-IMPL-22) | 06, 5.3 ("Points settled while implementing"), 07, 11.3 | The readiness check does not yet ask for a number series of type `REVERSAL` (11.3 now requires it) |
| F-IMPL-16 | 02, 5.2 (D9 row of ROUNDING), 07, 6.3 items 1 to 5 and 11.3, ADR-230. A limit for the size of a rounding line is open (ADR-230) | Nothing: the readiness check already covers the three currencies and usable role accounts |
| F-IMPL-17 | 03, 6.1 (the canonical form of the clearing row hash and `items_digest`), `test-vectors/clearing-hash-v2.json`, `verify-clearing-hash-v2.mjs`, `ClearingHash2` | Nothing |
| F-IMPL-39 | 07, 13.2p | The signature (ADR-217), VALIDATE mode, `config-packages:diff`, the policy file, the rest of the DK core content |
| F-IMPL-38 | 07, 13.2o | `vat-positions:get`, the legacy tax summary |
| F-IMPL-37 | 07, 13.2n | Closed by F-IMPL-38 except `vat-positions:get` and the legacy tax summary |
| F-IMPL-36 | 07, 13.2m | The year-end adjustment of a provisional rate, capital goods, the order management rejection, the readiness warning for a key without a rate, the reads of 19, 7, the DK package codes (18) |
| F-IMPL-35 | 07, 13.2l | The VAT rate type VAT, the evidence rows, the deduction keys, the L2 change of tax data, the return snapshots and the settlement. The data of `ref_country_eu_membership` is UNVERIFIED |
| F-IMPL-34 | 07, 13.2k | The determination rules (8.4), the deduction keys and rates (8.6), the partner registrations (9.4) and the use of a tax code in a posting: the tax lines of 02, 7, the TAX_ACCOUNT_DIRECT_POSTING rule, the hash blocks, reverse charge and partial deduction. The readiness checks of 11.3 for VAT. Loading return boxes and codes from a country package. The data of `ref_country_eu_membership` is UNVERIFIED |
| F-IMPL-33 | 07, 13.2j (commands and master choices), 06, 13.9 (posting), 02, 5.3 | Reads of the dimensions of a line (09). The batch variants and `upsert` of the dimension commands (07, 11.5). The dimension defaults of warehouses and item groups (10) |
| F-IMPL-32 | Nothing in the specs. **Decided 2026-11-11 and built:** the schema file is for an empty database only, an existing database is updated with `financecore-platform db migrate` (`MigrationRunner`, checksums in `platform.schema_migration`). The apply-twice test of `run-pglite.mjs` stays as a safety net |
| F-IMPL-31 | 06, 13.9 (API layer, `chains:verify`), 20, 7 (built), `api/openapi.json` | The index of 06, 2.4 lacks `SYSTEM.FISCAL_YEAR_MISSING`, the four codes of the API layer, and the check names `NUMBER.GAP` and `CHAIN.DOCUMENT_WITHOUT_LINES`. **Decided 2026-11-11 and built:** P0 is split. The endpoint checks the permission of the command (P0a) before the idempotency header and the payload, the company access (P0b, not built) follows P1. 06, 3 and 13.9 are changed, `api/openapi.json` carries `x-permission` |
| F-IMPL-30 | Nothing yet. 07 needs a delta for tenant and company provisioning, rates and the package format when they are specified | **Needs a decision:** the format and the signing of country packages (18) replace `CountryPackages.cs`; the legal basis of `dk-minimal` is UNVERIFIED |
| F-IMPL-29 | 06, 13.9 (break kinds `SNAPSHOT.*`) | Nothing. The names of the breaks follow 06, 7.4 when that command is built |
| F-IMPL-28 | 02, 4 (built choices), 06, 5.2 and 13.9 | Decided 2026-10-26: a CONTROL line's partner equals the header partner (`ACCOUNT_RULE.PARTNER_MISMATCH`), and the snapshot takes the master as of today. 06, 5.2 should say it in its own table when the next delta pass is made |
| F-IMPL-27 | 02, 4 (built choices), 06, 13.9, 07, 11.3 (check `RETENTION_RULES`), 12, 4.1 | Nothing |
| F-IMPL-26 | 12, 4.1 (built choices), 06, 13.9 | Nothing |
| F-IMPL-25 | 03, 5.1, 06, 7.1 (control accounts) | Nothing |
| F-IMPL-24 | 06, 5.2 (control accounts), 5.4 (a), 02, 5A | Nothing |
| F-IMPL-23 | 07, 13.2i (built choices), 06, 13.9 | Nothing |
| F-IMPL-22 | 03, 5.7 (built choices), 06, 5.3 (f) and 13.9 | Nothing |
| F-IMPL-21 | 03, 5.4 (built choices), 06, 7.1 | Nothing |
| F-IMPL-20 | 03, 3.1, 4.3 and 5.6, 06, 7.1 (g) | Nothing |
| F-IMPL-19 | 03, 5.2 (never more than remains), 06, 7.1 (points settled for the clean match) | Nothing |
| F-IMPL-18 | 03, 4.3 (database rules of the clearing tables, the role `cmd_clearing`) | `clearings:post` and `clearings:reset` write through the role `CommandRole.Clearing` (exists) |

## Migrating a database that exists (F-IMPL-32)

`dist/finance-core-schema.sql` is for an empty database. Every later change is a new numbered file in `migrations/` (forward only, 20, 12), and `financecore-platform db migrate` applies the files the database has not recorded, once each and in order, each in its own transaction, with the migration role (`FINANCECORE_ADMIN_DB`):

```
financecore-platform db status  [--dir db/migrations]
financecore-platform db migrate [--dir db/migrations] [--dry-run]
```

The state is `platform.schema_migration`, which every migration already writes its own row to. The runner adds a `checksum` column and stores the SHA-256 of each file (line endings ignored). A database that was set up before the runner takes the checksum of its files at the first `db migrate` and is held to it from then on. The runner refuses, with a stable code: `MIGRATION.CHANGED` (an applied file was edited: make it a new file), `MIGRATION.FILE_MISSING`, `MIGRATION.OUT_OF_ORDER` (a pending file numbered below an applied one), `MIGRATION.NAME_INVALID` (`<number>_<name>.sql`), `MIGRATION.VERSION_DUPLICATE`, `MIGRATION.DIRECTORY_MISSING` and `MIGRATION.FAILED` (the script failed, nothing of that file is kept). It does not apply the seed. `node tools/serve-pglite.mjs 5434 --empty` starts a local database without migrations to try it on.

## Re-running

Every script uses `if not exists`, `on conflict do nothing` or `create or replace`, and `tools/run-pglite.mjs` applies all of them twice, so a script that is run again by hand does no harm. That is a safety net, not the way to update a database: use `db migrate`. Changing a table that exists needs a new numbered migration (forward only, 20, 12), never an edit of an applied one.

## The platform CLI

`src/FinanceCore.Platform.Cli` builds `financecore-platform`, the operator's tool for what is not a tenant permission: creating a tenant and a company, applying a country package, adding exchange rates and activating (F-IMPL-30). It works on the database with the migration role from `FINANCECORE_ADMIN_DB` and is not part of the API. Exit code 0 is done, 1 is a refusal with a stable code, 2 is a usage error.

```
financecore-platform tenant create --code ACME --name "Acme" --currencies DKK,EUR
financecore-platform company create --tenant ACME --code DK01 --name "Acme ApS" --country DK --currency DKK --reporting EUR
financecore-platform company setup --tenant ACME --company DK01 --package dk-minimal --year 2026
financecore-platform rate add --tenant ACME --from DKK --to EUR --date 2026-03-15 --rate 0.134
financecore-platform company activate --tenant ACME --company DK01
financecore-platform package apply-vat --tenant ACME --company DK01 --dir country-packages --package DK --vat-id DK12345678 \
    --filing-frequency QUARTERLY --vat-profile 1 --first-fiscal-year-start 2026-01-01 [--include-special-codes] [--pro-rata-rate 80]
```

`package apply-vat` applies the VAT content of the DK package (tax codes of the chosen business type with boxes, keys, determination rules, the settlement series and role, 07, 13.2p), records the apply and prints what it created and what it skipped. `package list-applied --tenant ACME --company DK01` lists the applies of a company. It refuses a package that is not RELEASED for a PRODUCTION tenant.
```

`company setup --activate` does the last step in one go. Run `financecore-platform` without arguments for the full list. Rates are per day (the fallback of `rate_fallback_days` applies), so add the days you post to. The package `dk-minimal` has no tax and its retention basis is UNVERIFIED.

## The API

`src/FinanceCore.Api` serves the commands of the slice as `POST /v1/{resource}:{verb}` (20, 7). It needs a database (`FINANCECORE_DB`, role `core_app`) and an identity provider:

```
Auth__Authority=https://login.example.com/   an OpenID Connect authority (the keys come from its discovery document)
Auth__Audience=financecore-api               the audience of the token (default)
Auth__TenantClaim=tenant_id                  the claim with the tenant (default)
```

For local development `Auth__DevSigningKey` (at least 32 characters) accepts tokens signed with that key and issuer `financecore-dev`. The API refuses to start with it in the Production environment. A request carries `Authorization: Bearer`, `Idempotency-Key` (mandatory) and optionally `X-Correlation-Id` and `X-Source-System`. The document `api/openapi.json` is generated from the commands, and a test fails if it is out of date: run the tests with `UPDATE_OPENAPI=1` to regenerate it. It is also served at `GET /v1/openapi.json`. Creating tenants and companies is not in the API, it is the platform CLI's.

## The Supabase dialog "This query creates tables without enabling Row Level Security"

The SQL Editor shows it for every script that creates tables. Choose **Run without RLS** for 0001 to 0005. The schemas `core` and `platform` are not exposed through the data API and the Supabase roles have no rights on them (0001). 0006 then turns row-level security on for every `core` table, forced, with the tenant policy, and on the `platform` tables with read-only policies. "Run and enable RLS" would enable RLS without policies and hide the reference data from the command roles.

## Cleaning up test tenants

Every database test creates its own TEST tenant (code `T` and eight hex characters) and leaves it behind. On a hosted
database they add up. `db/tools/cleanup-test-tenants.sql` removes them with all their rows. It only touches tenants with
purpose TEST and that code pattern, never the dev tenant. It lists the tenants first, switches the protection triggers off
inside one transaction, deletes in foreign key order, switches them on again and shows what is left. `CleanupTests` proves it
on a tenant with documents, a reversal, period events and outbox events, and that another tenant stays intact. In the Supabase
SQL Editor run it without selecting text, and answer "Run without RLS". To remove one tenant only, run
`select set_config('cleanup.only_tenant', '<tenant id>', false);` in the same session first.
