# Scoping: Consolidation, Invoicing and Payment

Status: SCOPING v0.2. Decisions D0 to D8 taken as recommended (2026-10-19, ADR-219 to ADR-222), design in 22 (invoicing and payment) and 23 (consolidation, not yet written). Task 3 of the agreed work order. No design yet: this document fixes the boundary, lists what already exists and what is missing, and proposes the decisions that shape the design specs. Builds on 00 v0.4, ADR-001 to ADR-218 and specs 02 to 20.
Scope: what each of the three areas needs from the core, from country or standard packages, and from apps.
Out of scope: the design itself (comes after the decisions of section 6), app design, UI.

**Caution.** Format and scheme names (ISO 20022 messages, FIK, Betalingsservice, KID, OCR, Peppol) are named from general knowledge and are UNVERIFIED. They are pointers for the packages, not requirements.

## 1. Finding

Most of invoicing and payment is **already in the core**. What remains is mainly **the contract the apps work against**, two **standards packages** and a few small core deltas. Consolidation is the only area with a real design question (does the core ever hold consolidation postings). The numbers in section 2 come from the specs, not from new design.

| Area | Status in the core | Remaining work |
|---|---|---|
| Invoicing | Posting, matching, credit notes, issue evidence and e-invoice data set exist (11, 12, ADR-123, ADR-125) | App contract, advance invoices (decision D3), country invoice content, G7 |
| Payment | Open items, clearing, terms, bank accounts, payment reference, block exist (02, 03, 07) | App contract, ISO 20022 standards package, DK payment content |
| Consolidation | Data only (ADR-017): group, group currency, group CoA mapping, IC marker | Data gaps (section 3.2) and the decision D2 |

## 2. What exists

| Capability | Where | Note |
|---|---|---|
| Sales invoice and credit note built by the core from order lines (`CORE_BUILDS`) or supplied by the app (`APP_SUPPLIES`), same validation and posting | 11, ADR-156 | Mode is versioned per company |
| Supplier invoice matching (two-way and three-way), price differences, GR/IR write-off | 11, 10 | |
| Invoice posted without orders (`journal-documents:post`, origin EXTERNAL) | 02, 06 | Document types AR_INVOICE, AR_CREDIT, AP_INVOICE, AP_CREDIT |
| Buyer data as on the invoice (`document_legal_snapshot`) | 02, 12, ADR-064 | |
| Issued document evidence (`invoice_issue_record`, `invoices:record-issue`): file hash, format, channel, time. The core renders and stores no file | 12, ADR-125 | |
| EN 16931 data fields for one invoice (`einvoice-datasets:get`) and as export | 12, ADR-123 | Syntax and transport are app work |
| Document type PAYMENT, open items on lines, partial and on-account payments | 02, 03 | |
| Clearing with methods MANUAL, AUTO_MATCH, PAYMENT_ALLOCATION, realised FX, residual document | 03 | Short payment and discount are ordinary documents that are then cleared (03, Q1) |
| `payment_terms`, `partner_bank_account`, `company_bank_account` (with bank GL account), `payment_block`, `payment_reference`, `bank_account_id`, informational discount fields on open items | 02, 07 | The core posts no discount (02, Q9). S6 left these fields in the core (00, section 5) |
| Idempotency key on every command, change feed of events, extension attributes for app data | 06, 15 | |
| Intercompany pairs, `ic_transaction_id`, `ic_counterpart_company_id`, group amount copied | 02, ADR-016, ADR-017 | |
| `group_chart_account` and `account_group_mapping` (effective-dated), `group_currency`, group membership | 07, 6.4, 5.x | |
| Rate types SPOT, CLOSING and VAT | 07, 8.x | AVERAGE and HISTORICAL are missing (00, section 5) |

Out of the core by principle (00, P15, 03): UI, drafts and approval, document rendering, bank connectivity, payment file generation, matching and allocation logic, dunning, e-invoice transport, consolidation run.

## 3. Consolidation

### 3.1 The question

ADR-017 stores what consolidation needs and runs nothing. Three things can go beyond that:

1. Data the run needs but the core does not have yet.
2. The result of the run (translated balances, elimination entries, minority share).
3. Whether elimination and translation entries are **postings in the core** (with numbers, hash chain and anchors) or app data.

### 3.2 Data gaps (candidates for the core, small)

| Gap | Why | Proposal |
|---|---|---|
| Ownership percentage and consolidation method per company and period (full, proportional, equity) | Determines translation, elimination and minority. Group membership has no percentage | New versioned table `company_consolidation_setting_version` (V), effective-dated |
| Rate types AVERAGE and HISTORICAL | P&L translation at the average rate, equity at historical rates (00, section 5) | Add to `rate_type` (IAP), no new table |
| Account roles for translation difference and minority | Needed only if consolidation postings exist (option B). Not needed for option A | Add with option B |
| IC reconciliation | A run needs the unmatched IC differences between two companies | A read `intercompany-reconciliation` over `ic_transaction_id` pairs, no new table |
| Group CoA mapping by group, not only by company | Today one group account per company account | Verify in 07, 6.4 before design (a company belongs to one group at a time, ADR-014), likely sufficient |

### 3.3 Options for the result

| Option | Description | For | Against |
|---|---|---|---|
| **A. App only** (recommended) | A consolidation app reads through the read APIs (`read.cross_company`), runs translation and elimination, keeps results and adjustments in its own tables, refers to core ids and chain positions | Lean core, in line with the principle that processes live in apps. No change to company, VAT or numbering semantics | Consolidation adjustments have no core-level tamper evidence. The app must prove its inputs by chain position and anchors |
| B. Consolidation ledger in the core | A pseudo company of kind CONSOLIDATION with its own `ledger_id`. Elimination and translation entries are ordinary journal documents there | Same audit trail and hash chain as the ledger. Reporting reads one source | A company that is not a legal entity touches VAT registration, number series, retention, fiscal calendar, SAF-T and the readiness checks. Large change for a feature no client has asked for |

Recommendation A for release 2. The data of section 3.2 is cheap and keeps B open: `ledger_id` already exists in the chain key, so B can be added later without a hash change.

## 4. Invoicing

### 4.1 Boundary

| Layer | Owns |
|---|---|
| Core | Posting, validation, tax determination, legal numbers, credit notes and reversal rules, buyer snapshot, issue evidence, EN 16931 data set, retention |
| Standards and country packages | Mandatory invoice particulars per country (invoice texts, required fields, VAT category and exemption text), EN 16931 term mapping (`STD_EN16931`), country e-invoice profiles (for example Peppol and national syntaxes, UNVERIFIED) |
| Apps | Billing runs and recurring or usage-based billing, pricing and discounts, drafts and approval, rendering (PDF, XML syntax), delivery and transport, delivery status, dunning, customer portal |

### 4.2 Gaps

| # | Gap | Proposal |
|---|---|---|
| I1 | No written **app contract** for invoicing: which command to call in which situation, in which order, with which idempotency key, which events to consume | Specify it (section 7) |
| I2 | **Advance and prepayment invoices** are not in release 1 (11, section 1) and are common in project and subscription business | Decision D3 |
| I3 | **G7**: the completeness check for e-invoice fields is rule code in the core, while `STD_EN16931` lists the same terms with source | Open decision from the package work. It belongs in this design |
| I4 | **Invoice delivery status** (accepted, rejected by the receiver) has no home | App data, linked to `invoice_issue_record`. A status event could be recorded by `invoices:record-issue` (small delta) or stay in the app |
| I5 | **Self-billing** (the buyer issues the invoice) and **corrected invoices** by country rules | Check against the country packages. Probably an app flow over existing document types |
| I6 | **Billing runs for utilities and subscriptions** (consumption billing, FI-CA style) | App domain. The core receives complete invoices (APP_SUPPLIES). Volume and batch behaviour are a load-test item (20, section 13) |

Proposal: no new core tables for invoicing. A contract spec plus the decisions D3 and G7.

## 5. Payment

### 5.1 Boundary

| Layer | Owns |
|---|---|
| Core | Open items, clearing and its evidence, FX on clearing, payment terms and bank accounts as master data, payment document posting, block and reference fields |
| Standards package (new) | `STD_ISO20022`: payment and statement message mappings (credit transfer, direct debit, statement, notification, status report) to core fields, code lists, structured creditor reference rules. Same shape as `STD_SAFT_OECD` |
| Country packages | DK: payment slip and direct debit schemes, national account scheme, payment terms (exists), reference algorithms and field rules. SE and NO later (UNVERIFIED names: OCR, Bankgiro, KID) |
| Apps | Payment proposals and runs, file generation, bank connectivity, statement import, matching rules, dunning, mandates, treasury |

### 5.2 Gaps

| # | Gap | Proposal |
|---|---|---|
| P1 | No written **app contract** for payment flows | Specify it (section 7): proposal, run, bank confirmation, statement line to payment document and clearing |
| P2 | **Exactly-once from a statement line to a posting** | Convention, no schema: idempotency key derived from statement id and line, the source reference field carries the statement line reference |
| P3 | **Payment in transit** (post at file creation) versus posting at the bank statement | Decision D4. Default: post at the statement, mark open items with `payment_block` while a run holds them. The block is an L2 field, so no schema change. Option "in transit" uses an ordinary GL account chosen by the app |
| P4 | **Direct debit mandates** are personal data with consent and validity | Extension attributes with `is_pii` on the partner bank account (15, 3), no core table. Revisit if a country package needs more |
| P5 | `STD_ISO20022` and DK payment content do not exist | Write them as DRAFT packages after the decisions, same method as the SAF-T mapping |
| P6 | **Cash discount** is informational only (02, Q9): the app computes the discounted payment and posts the difference as a document | Keep. The contract states the pattern |
| P7 | Structured references (check digits) are app and package logic | The core keeps `payment_reference` as free text of 100 characters. No validation in the core |

## 6. Decisions to take

| # | Decision | Recommendation | Why |
|---|---|---|---|
| D0 | Does this project specify only the **contract** the apps use plus reference flows, or also design the apps? | Contract and reference flows only | Apps are outside the core by principle. A design spec per app would double the scope |
| D1 | Shape of the work | One spec `22-invoicing-and-payment-contract` (app contract, events, idempotency conventions, reference flows, small deltas) and one spec `23-consolidation-data` (data gaps and the read for IC reconciliation). Packages `STD_ISO20022` and DK 0.4.0 payment content as parallel items | The core changes are small. The contract is the large part |
| D2 | Consolidation result | Option A, app only. Data of 3.2 now, option B only when a client needs consolidation postings | Keeps the company concept clean |
| D3 | Advance and prepayment invoices | Not in the next release. The contract describes the pattern as a clearing of an on-account payment against the final invoice. Bring it into the core only with a first client | VAT point-of-supply rules for prepayments differ by country, so it needs package support |
| D4 | Payment run posting | Default: posting at the bank statement, `payment_block` while a run holds an item. "In transit" is the app's option on an ordinary account | No schema change, fewer accounts |
| D5 | Delivery status of invoices | App data, no core delta | Transport is outside the core |
| D6 | G7: where the e-invoice completeness rule lives | Keep the check in the core as a **fixed rule set per EN 16931 edition** (a code version, selected per company), and keep `STD_EN16931` as the documented list, with a conformance test that both lists agree | The core must not read app content (18, 4.5) |
| D7 | Direct debit mandates | Extension attributes with `is_pii`, no core table | Lean, uses the existing mechanism |
| D8 | Order of work | 1. Payment and invoicing contract (22). 2. `STD_ISO20022` and DK payment content. 3. Consolidation data (23) | DK pilot needs payment and invoicing first |

## 7. Proposed content of the design specs

**22-invoicing-and-payment-contract** (outline).
1. Roles and ownership table (as sections 4.1 and 5.1, made binding).
2. Reference flows with the exact commands, idempotency keys and events: sales invoice (CORE_BUILDS and APP_SUPPLIES), supplier invoice and matching, credit note, issue and delivery evidence, payment received (statement line to payment document to clearing), short payment and discount, payment run (proposal, hold, bank confirmation), foreign currency payment, return of a payment, intercompany settlement.
3. Idempotency key conventions per flow.
4. Event consumption: which events an invoicing or treasury app subscribes to, and the consistency token (`ledger_seq`, outbox position).
5. Failure and retry behaviour per command (retryable errors of 06, restore reconciliation of 20, section 12).
6. Small deltas: the items in sections 4.2 and 5.2 that the decisions accept.
7. Verification tests: a flow test list that a conformance suite can run against the API.

**23-consolidation-data** (outline). `company_consolidation_setting_version`, rate types AVERAGE and HISTORICAL, `intercompany-reconciliation` read, readiness checks, deltas to 07 and 09, and what the consolidation app must read and prove.

## 8. Assumptions and open points

| # | Item |
|---|---|
| A1 | The first pilot is DK (the DK package exists). SE and NO test the payment model later |
| A2 | An app layer exists or will exist. Which apps are built in this project is decision D0 |
| A3 | Volume of billing runs (utilities or subscriptions) is unknown. It decides whether the post-batch limit of 500 and the single chain per ledger hold (20, sections 4 and 13) |
| A4 | Country payment and e-invoice scheme names and rules are unverified. They get verification records like the other packages |
| A5 | Check in 07, 6.4 whether the group chart mapping per company is enough, before 23 is written |
