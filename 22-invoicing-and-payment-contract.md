# Invoicing and Payment: the Contract Between Core and Apps

Status: DRAFT v0.1 for review. Task 3 of the agreed work order, first spec. Decisions D0 to D8 of 21 were taken as recommended (2026-10-19, ADR-219 to ADR-222). Builds on 00 v0.4, ADR-001 to ADR-222 and specs 02, 03, 06, 07, 09, 11, 12, 15, 18, 20, 21.
Scope: which commands, reads and events an invoicing, billing, e-invoice or treasury app uses, in which order, with which idempotency keys, and what happens on failure. Reference flows for invoicing and payment. Small deltas the flows need. A list of conformance flow tests.
Out of scope: the design of any app (D0), payment file formats and bank message mappings (`STD_ISO20022` and the DK payment content, written next), consolidation (23), UI, rendering, transport.

**Caution.** The core mechanics below come from the specs cited in each row and were checked against them. Rules about when a country allows reversal instead of a credit note, and all scheme and format names, are UNVERIFIED and belong to the country packages. Flows marked "to validate" are consequences I derived and have not run: the conformance tests of section 11 are what settles them.

## 1. Decisions taken

| # | Decision (21, section 6) | Effect in this spec |
|---|---|---|
| D0 | The project specifies the contract and reference flows, not the apps | This spec |
| D1 | Spec 22 for invoicing and payment, spec 23 for consolidation data, packages in parallel | Section 12 |
| D3 | Advance and prepayment invoices are not in the next release | Pattern only, 4.7 |
| D4 | A payment run posts at the bank statement, `payment_block` holds items meanwhile | 5.5 |
| D5 | Invoice delivery status is app data | 4.5 |
| D6 | The e-invoice completeness check stays in the core as a fixed rule set per EN 16931 edition | 10, delta 3 |
| D7 | Direct debit mandates are extension attributes with `is_pii` | 5.7 |
| D8 | Order of work: this spec, then `STD_ISO20022` and DK payment content, then 23 | 12 |

## 2. Roles

| Layer | Owns | Never does |
|---|---|---|
| Core | Posting, validation, tax determination, legal numbers, credit notes and reversal rules, buyer snapshot, issue evidence, EN 16931 data set, open items, clearing and its evidence, realised FX, retention | Render a document, send anything, read app content, choose which items to pay, match a bank line |
| Packages | Invoice particulars and texts per country, EN 16931 term mapping, payment and statement message mappings, reference scheme rules, mandatory-field lists | Execute anything. A package is data (18) |
| Apps | Billing runs and pricing, drafts and approval, rendering and syntax, delivery and status, payment proposals and files, bank connectivity, statement import and matching, mandates, dunning | Write core tables. Every core change goes through a command (00, P2) |

## 3. Conventions for every app

**Registration.** An app is a registered app with an `app_code` (15, 3.1). Commands carry `context.actor`, `context.source_system` and `context.correlation_id` (06, 2.1). `source_system` is a tracing field, never a decision input.

**Permissions.** Invoicing apps hold `sales.invoice`, `purchase.invoice`, `ic.invoice`, `journal.post` where they post directly. Treasury apps hold `journal.post`, `clearing.post`, `clearing.reset`, `journal.change` (for `change-open-fields`), `read.ledger`. The core checks presence only (06, 2.1).

**Idempotency keys.** Mandatory on every command (06, 4). The rules for an app:

| Rule | Reason |
|---|---|
| The key is derived from the **source fact**, not generated at call time: statement id and line number, billing run id and invoice reference | A crashed app that retries builds the same key and gets the stored result (06, 4.3) |
| Base key at most 90 characters | Commands that derive keys append `#n` (06, 4.3) |
| A key is never reused for a changed request | A different request with the same key is `IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST`. An app that sees it has a defect |
| Prefix by flow and app code, for example `inv:{app}:{run}:{ref}`, `pay:{bank_acct}:{stmt}:{line}`, `clr:{stmt}:{line}`, `iss:{ref}:{n}` | One namespace per flow, readable in the audit trail |
| Do not rely on replay after the `command_log` is purged (`IDEMPOTENCY.KEY_EXPIRED_REUSE`, 06, Q2). The unique keys on documents and clearings still stop a double posting | Apps keep their own record of what was sent |

**Atomicity.** One command is atomic. A flow of several commands is not. Every flow in this spec states the **intermediate states** and how the app resumes from each. No flow depends on two commands succeeding together.

**Failure handling** (06, 2.2): `REJECTED` means nothing was written, fix and retry (same key allowed). `FAILED` with `retryable` means retry with the same key. `CONFLICT.*` and lock timeouts are retryable. A `SYSTEM.*` failure after validation is safe to retry with the same key.

**Consistency.** After a command, the response is complete: apps do not need a follow-up read (06, 2). A later read on a replica passes `min_ledger_seq` from the posting response (`chain_seq` of the document, 09, section 2). An app consuming events keeps its own position with `events:ack` (15, 5.3).

**Personal data.** Events carry references and no personal data (15, 6). Extension attributes with personal data use `is_pii`.

## 4. Invoicing

### 4.1 Which posting path

| Path | Command | Lines | Order progress and matching | Link record | E-invoice data set | Issue evidence |
|---|---|---|---|---|---|---|
| A. Order-built, core selects | `sales-invoices:create` (mode CORE_BUILDS) | From the order lines by `invoice_policy` | Yes | `sales_invoice` | Yes | Yes |
| B. Order-built, app supplies | `sales-invoices:create` (mode APP_SUPPLIES) | Sent by the app with order and delivery references | Yes | `sales_invoice` | Yes | Yes |
| C. Direct | `journal-documents:post` with `AR_INVOICE` or `AR_CREDIT` | Any accounts | No | None | **No** | Yes, by `document_id` (delta 1) |
| D. Intercompany | `ic-invoices:create` | From the IC order | Both sides, with matching on the AP side | Both | Seller side | Yes |
| Supplier, ordered | `purchase-invoices:post`, `purchase-credit-notes:post` | The supplier invoice lines matched to receipts | Yes, GR/IR | `purchase_invoice` | n/a | Yes (purchase) |
| Supplier, direct | `journal-documents:post` with `AP_INVOICE` or `AP_CREDIT` | Any accounts | No | None | n/a | Yes, by `document_id` (delta 1) |

**Finding F1.** `invoices:record-issue` referred to the link record only (`invoice_id`, 12, section 11) and `einvoice-datasets:get` reads `sales_invoice` lines (12, section 10). A direct journal invoice (path C) has neither a link record nor invoice lines, so it could not carry issue evidence and has **no e-invoice data**. This matters for apps that bill without orders. Two answers:
- Issue evidence for a direct invoice: delta 1 (a `document_id` in place of `invoice_id`, applied).
- E-invoice data for a billing app without orders: the **billing-via-orders pattern** of 4.6. A direct invoice carries no line data the core could map to EN 16931.

### 4.2 Sales invoice, reference flow (paths A and B)

| Step | Who | Action | Intermediate state |
|---|---|---|---|
| 1 | App | Decides what to bill (billing run, approval, pricing). Pricing is app logic: in mode A the core takes the order price | Nothing in the core |
| 2 | App | `sales-invoices:create`, key `inv:{app}:{run}:{ref}`. VALIDATE mode first when the app wants the rejections before the commit (06, 6) | |
| 3 | Core | Posts `AR_INVOICE` in one transaction: revenue lines, tax, AR control line with open-item attributes, link rows, outbox events (`invoice.posted`, `ledger.document_posted`, `open_item.created`) | **POSTED, not issued.** The legal number exists now (00, P6) |
| 4 | App | `einvoice-datasets:get` for the e-invoice data and, if it renders, its own PDF or syntax | |
| 5 | App | Sends to the customer through its channel | Sent, not recorded |
| 6 | App | `invoices:record-issue` with file hash, format, channel, key `iss:{ref}:{n}` | **ISSUED** (`invoice.issued`) |
| 7 | App | Tracks delivery status and answers in its own tables with the `issue_id` | App data |

**Order matters.** The legal number comes from posting. The invoice is rendered after the post. A rendering or transport failure leaves the invoice posted and not issued. The app **retries from step 4 or 5**, never from step 2. A re-send is a new `invoices:record-issue` with a new key (several records per invoice are allowed, 12, 11).

**Payment reference (the Danish FIK case, DK 0.4.0, F-DK-21).** An invoice that carries a FIK payment id needs the invoice number in the id, and the number exists only after the post. `sales-invoices:create` has no `payment_reference` field. The app therefore posts first and then sets `payment_reference` on the AR open item with `journal-documents:change-open-fields` (L2, expected value null, key `ref:{ref}`) before it reads the e-invoice data set in step 4. The step is idempotent. If the data set is read before the reference is set, the invoice is issued without it: the app must not skip this step for a country that needs the reference.

**Finding the posted-not-issued set.** The app consumes `invoice.posted` and `invoice.issued` events and holds the set difference. It can also list them directly: `issue_state` = `NOT_ISSUED` with `status` = `posted` on `sales-invoices` or on `journal-documents` (09, 3, delta 6). The events are the push path, the filter is the reconciliation path.

### 4.3 Credit notes and corrections

| Case | Flow |
|---|---|
| Price correction or goodwill | `sales-credit-notes:create`, kind amount credit, `sales_invoice_line`, `credit_amount_net`, `reason_code`. Capped by the net amount of the line (`ORDER.CREDIT_EXCEEDS_INVOICED`) |
| Returned goods | Post the return (`sales-returns:post`), then `sales-credit-notes:create`, kind return credit |
| Wrong invoice, not cleared, no credit notes | `sales-invoices:reverse` with `reason_code` CORRECTION or ERROR. Rejected with `REVERSAL.HAS_CLEARED_LINES` or `ORDER.INVOICE_HAS_CREDIT_NOTES` otherwise |
| Wrong invoice, cleared or already issued | Credit note for the full amount, then a new invoice. Whether a country allows a reversal of an issued invoice instead is a country rule (UNVERIFIED) |
| Reverse a credit note | `sales-credit-notes:reverse`. Rejected while the credit open item is cleared |

The core has **no rule that stops a reversal of an issued invoice** (11 and 12 state none). Whether it should is open point OQ1. Until it is decided, the rule belongs to the app and the country package.

A credit note can be cleared against its invoice by the caller (11, 6.5). That is a clearing, section 5.

### 4.4 Supplier invoices

| Case | Flow |
|---|---|
| Invoice with order and receipt | `purchase-invoices:post` (match, GR/IR clearing, price difference lines, stock cost adjustment) |
| Invoice without order (rent, services) | `journal-documents:post`, type `AP_INVOICE`, control line with partner, due date and open-item attributes, base lines with accounts, dimensions and optional tax code. The core adds tax lines and, for partial deduction (19), the non-deductible lines |
| Partial deduction with a supplied cap | `deductible_tax_amount_tc` on the line for the codes that need it (19, 3.3, 06, 5.2) |
| Duplicate supplier invoice | `purchase_invoice` has a unique key on supplier and supplier invoice number (11, 6.9). Direct journal invoices have **no** such key: the app checks `external_reference` itself (OQ6) |
| Supplier credit note | `purchase-credit-notes:post` or direct `AP_CREDIT` |

Approval and three-way matching tolerance acceptance happen in the app before the post (11, S1).

### 4.5 Issue and delivery evidence

- **Core:** `invoice_issue_record` (insert only): `invoice_kind`, `invoice_id`, `format`, `document_reference`, `document_sha256`, `issued_at`, `delivery_channel`. It proves **which file went out and when**.
- **App:** delivery status (accepted, rejected, read, paid by the receiver's system) stays in app tables with the `issue_id` (D5). The core does not model it. A status that changes what must be posted (a rejection that needs a credit note) is a new flow started by the app.
- **E-invoice rules:** the core's completeness check lists missing mandatory data and the data set is still produced with the gaps listed (12, section 10). The check is a fixed rule set per EN 16931 edition (D6, delta 3).

### 4.6 Billing without orders (pattern)

Subscription, utility and project billing often have no sales order. For these, the **billing-via-orders pattern** gives the e-invoice data and issue evidence:
1. A contract is represented by a sales order with service lines and `invoice_policy` ORDERED (11, 4.2).
2. Each billing event is `sales-invoices:create` in mode APP_SUPPLIES with the app's quantity, price and discount (11, 6.4). The core validates the order line reference and `open to invoice`.
3. The order stays open for the contract term. Each invoice is a normal path B invoice.

Cost: one order per contract, order lines per billable item. Benefit: all invoice features of 4.1 apply. An app with very high volume may prefer path C and live without the e-invoice data set. The volume decides, and the load test of 20, section 13 should include a billing-run profile (A3 of 21).

### 4.7 Advance and prepayment invoices (D3, pattern only)

Not in the next release. The pattern for an app that needs it before the core supports it:
1. Post the advance invoice as `AR_INVOICE` (path C) to a customer prepayment account.
2. When the payment arrives, treat it as a normal payment (5.1).
3. The final invoice posts the full amount. The app clears the prepayment item against the final invoice (a clearing, section 5).

The VAT point of supply of prepayments differs by country and is not handled by the core (08, 3). A country package must state it before this pattern is used in production.

### 4.8 Intercompany invoices

`ic-invoices:create` posts the AR invoice of the seller and the AP invoice of the buyer atomically with a shared `ic_transaction_id` (11, 6.7, ADR-016). The app calls it once. The settlement of an IC balance is two ordinary payments in two companies (5.8).

## 5. Payment and clearing

### 5.1 Incoming payment from a bank statement line

| Step | Who | Action | Intermediate state |
|---|---|---|---|
| 1 | App | Imports the statement (camt or national format, a package mapping), stores the line in its own tables | Nothing in the core |
| 2 | App | Matches the line to open items (rules are the app's): reference, amount, partner. Reads `open-items:query` | |
| 3 | App | `journal-documents:post`, type `PAYMENT`, key `pay:{bank_acct}:{stmt}:{line}`. Lines: the bank GL account of the company bank account (07, 9.5), and the control line of the customer with open-item attributes. `source_document_ref` carries the statement line reference | **Payment posted, not cleared.** The credit sits as an open item on the customer (on account) |
| 4 | App | `clearings:post`, key `clr:{stmt}:{line}`, method `PAYMENT_ALLOCATION`, items: the invoice open item and the payment open item, each with `cleared_amount_tc` (06, 7.1) | **Cleared** |
| 5 | App | Marks the statement line as processed in its own tables | |

**Resume rules.** A crash after step 3 leaves an on-account payment. The app resumes at step 4 and reads the payment document by its key (replay returns it). A crash after step 4 repeats step 4 with the same key and gets the stored clearing.

**Rules of the clearing** (03, 06, 7.1): all items on one account and one partner, none fully cleared, cleared amount within the line amount, same-currency items net to zero in transaction currency. A competing clearing on the same item makes the loser fail with `CONFLICT.ITEM_ALREADY_CLEARED` (retryable): the app re-reads and re-matches.

**To validate (OQ3).** The payment control line is an open item. Whether the open-item rules of 06, 5.2 (`due_date` required on CONTROL accounts) need a due date on a payment line, and which, is not stated for `PAYMENT` in the specs read. The test FT-05 settles it. The expected answer is the payment date.

### 5.2 Short payment, overpayment, discount

| Case | Flow | Basis |
|---|---|---|
| Short payment | Clear the payment amount against the invoice (`cleared_amount_tc` equal on both items). The rest of the invoice stays open | 03, Q1 |
| Overpayment | Clear the invoice amount against the payment. The rest of the payment stays on account | 03 |
| Cash discount taken | The core posts no discount (02, Q9). The app posts a credit memo (`AR_CREDIT`, with the tax effect by the usual rules) for the discount, then clears invoice, payment and credit memo in one `clearings:post` | 03, Q1, 02, Q9 |
| Small write-off | An ordinary journal or credit memo, then clear | 03, Q1 |
| Several invoices, one payment | One `clearings:post` with all items. Multiple partners in one clearing are rejected (`CLEARING.ITEMS_NOT_SAME_PARTNER`): one clearing per customer | 06, 7.1 |

### 5.3 Foreign currency

The payment document has **one transaction currency** (ADR-035). A payment in another currency than the invoice clears with realised FX computed by the clearing (03, ADR-038, role `FX_REALISED`). A bank fee in another currency is a **separate document** (02, A2). The clearing performs no rate lookup of its own (06, 7.1, ADR-153): the rates come from the documents.

### 5.4 Reading open items for proposals

`open-items:query` (09, 4.4): `company_id`, `as_of_date`, filters (`account_id`, `partner_id`, due date range, `payment_block`, `currency`). Result per item: four-currency blocks, `remaining`, `days_past_due`, `payment_block`, `payment_reference`, `bank_account_id`. `aging:query` and `partner-balances:query` (09, 4.5, 4.6) serve dunning and credit review. Valuation is at booked amounts (09, Q3).

### 5.5 Outgoing payments: proposal, hold, confirmation (D4)

| Step | Who | Action | Intermediate state |
|---|---|---|---|
| 1 | App | Proposal: reads `open-items:query` (AP, due, no block). Terms, discount timing and bank choice are app logic | In the app only |
| 2 | App | **Hold:** `journal-documents:change-open-fields` sets `payment_block` to a code that the app owns (L2, with the expected value so a competing change is caught: `CONFLICT.EXPECTED_VALUE_MISMATCH`, 06, 5.4), key `blk:{run}:{item}` | Items held. The block is audited in `change_log` |
| 3 | App | Builds the file (package mapping) and sends it to the bank | In the app. Nothing posted |
| 4 | App | Statement line arrives (the bank has paid): posts `PAYMENT` and clears as in 5.1, mirrored for AP | Posted at the statement (D4) |
| 5 | App | Cancelled run or rejected item: removes the block with the same command | Block released |

**Why post at the statement.** The default needs no in-transit account and no extra postings. An app that wants payments in transit posts the `PAYMENT` at file creation to an ordinary GL account of its own choice and clears that account against the statement. The core does not model it (D4).

**The block code.** `payment_block` is a free CODE on the open item (02). The list of values and who may set them is not defined in the specs read (OQ4). The proposal is a small configured list: a run block owned by the treasury app and a manual block owned by users.

### 5.6 Returned and rejected payments (to validate)

A direct debit that the bank returns, or a customer credit transfer that is reversed, is a **business event**, not a correction. The reason code list for external reversals is CORRECTION or ERROR only (06, 5.3), so the flow avoids reversing a payment document:
1. `clearings:reset` of the clearing that used the payment (reason CORRECTION). The invoice becomes open again and the payment credit is on account.
2. Post a `PAYMENT` of the opposite sign (bank credit, control debit) with `source_document_ref` naming the original payment and the bank's return code in `header_text`.
3. Clear the original payment credit against the new debit with `clearings:post` (`PAYMENT_ALLOCATION`).

The invoice stays open and the trail shows both movements. Open point OQ2 asks whether a dedicated reason code or a document type for returned payments is better. FT-09 settles whether the flow works as written.

### 5.7 Direct debit mandates (D7)

A mandate is personal data (consent, validity, signatory) and is not a core table. The owning app defines extension attributes (15, 4.3):

| Attribute | Type | Note |
|---|---|---|
| `{app}.mandate_id` | text | `is_pii` true |
| `{app}.mandate_signed_on` | date | `is_pii` true |
| `{app}.mandate_scheme` | code | Package-defined value list |
| `{app}.mandate_status` | code | ACTIVE, REVOKED |

Set with `extensions:set` on the partner bank account record (entity type per 15, 3.5, to confirm, OQ7). The retention job clears `is_pii` values with the partner (12, 4.3). Revisit with a core table only if a country package needs more.

### 5.8 Intercompany settlement

An IC balance is settled by an ordinary `PAYMENT` in each company and a clearing on each side against the IC control accounts of the invoice pair. Netting several IC balances in one document is **not** covered by the core: an app can post it as a journal in each company (type `JOURNAL`) and clear. Group amounts follow ADR-016 (copied from the pair).

### 5.9 Payment references and instruments

`payment_reference` is free text of 100 characters on the open item and carries no validation in the core (21, P7). Check digits and scheme rules (national payment slips, creditor references) are package content (`STD_ISO20022` and country packages) applied by the app. `bank_account_id` points to a `partner_bank_account` (personal data, anonymised with the partner, 07, 9.5).

### 5.10 Dunning

Dunning is an app domain (00, section 4). It uses `aging:query` and `open-items:query`, owns the dunning cases in its tables (15, 1) and sets `dunning_block` through `change-open-fields` where needed. A dunning fee or interest is an ordinary `AR_INVOICE` (path C or an order-built invoice). The core posts neither.

## 6. Reads for apps

| Need | Read | Spec |
|---|---|---|
| Open items at a date | `open-items:query` | 09, 4.4 |
| Aging, partner exposure | `aging:query`, `partner-balances:query` | 09, 4.5, 4.6 |
| Credit exposure of a customer | `credit-exposures:query` | 11, 8.1 |
| One document with its context and links | `documents:get-context` | 09, 4.7 |
| E-invoice data for one invoice | `einvoice-datasets:get` | 12, 10 |
| Any entity | generic `get` and `list` with typed filters | 09, ADR-086 |
| Events since a position | `events:list`, `events:head` | 15, 5.3 |

Reads on replicas pass `min_ledger_seq` (20, section 8).

## 7. Events by app type

| App type | Subscribes to | Used for |
|---|---|---|
| Invoicing, e-invoice | `invoice.posted`, `invoice.reversed`, `invoice.issued`, `ledger.document_posted` | Find posted-not-issued, react to reversals |
| Treasury | `open_item.created`, `open_item.cleared`, `open_item.reset`, `open_item.changed`, `clearing.posted`, `clearing.reset` | Refresh proposals, detect competing clearings |
| Dunning, credit | `open_item.*`, `ledger.document_posted`, `master.*` (partner changes) | Refresh cases and holds |
| Reconciliation | `ledger.document_posted` with `ledger_seq` | Check that the statement side and the ledger side agree |

Events carry references only, no personal data. Delivery is at least once: the app is idempotent on `outbox` positions (15, 5.2).

## 8. Failure and recovery

| Situation | Behaviour |
|---|---|
| App crashes between commands of a flow | Resume from the intermediate state of the flow table, same keys |
| `FAILED` or lock timeout | Retry with the same key (safe, 06, 2.2) |
| `CONFLICT.ITEM_ALREADY_CLEARED` | Re-read the item, re-match, retry with a **new** key if the request changes |
| Key replay after `command_log` purge | The unique keys of documents and clearings stop a double posting. The app must not depend on a replayed response |
| Restore of the core to an earlier point (20, section 12) | The app compares its consumer position with `events:head` and the last posted document of its flows. A statement line that the app marked processed but the core no longer has is re-posted with its original key (it is a new posting after the restore) |
| Period closed | `PERIOD.*` rejection. The app asks a user or uses the posting date rule of its flow. No date shifting by the core (06, Q8) |

## 9. Deltas

ADR-223 (accepted 2026-10-19) adds a seventh delta from the Danish payment content: `company_bank_account` gets `account_scheme` and `account_identifier`, and the payment instructions of the e-invoice data set read the identifier and the invoice `payment_reference` (07, 9.5, 12, 10, 06, 13.6). Applied.

| # | Spec | Delta | Reason | State |
|---|---|---|---|---|
| 1 | 12, section 11, 06, 15 | `invoices:record-issue` accepts `document_id` (the journal document) as an alternative to `invoice_id`, for direct invoices and credit notes and the supplier side. Exactly one of the two. Three `ORDER.ISSUE_*` codes, two indexes | F1 | **Applied 2026-10-19** |
| 2 | 11, 12 | Decide OQ1 first: an optional company setting that rejects the reversal of an invoice with an issue record (`ORDER.INVOICE_ISSUED`). Default off | OQ1 | Open |
| 3 | 07, 12 | New company setting `einvoice_rule_set` (a closed code per EN 16931 edition, in `company_setting_version`, resolved at the `document_date` of the invoice). 12, section 10 reads it. `CONFIG.EINVOICE_RULE_SET_UNKNOWN`. Readiness warning. `STD_EN16931` keeps the list and gets a conformance test | D6, G7 | **Applied 2026-10-19** |
| 4 | 06 | Section 13.5 records the deltas | Single source | **Applied 2026-10-19** |
| 5 | 00 | Status line: invoicing and payment contract specified | | Open |
| 6 | 09, 11 | Filter `issue_state` on `journal-documents` (invoice types), `sales-invoices` and `purchase-invoices`, include `issue_records`, list `invoice-issue-records` (OQ5 closed) | 4.2 | **Applied 2026-10-19** |

Deltas 1, 3 and 6 were the only schema or contract changes. The ERD was updated on 2026-10-19 (page 15 shows the `document_id` of the issue record, page 7 the new bank account columns).

## 10. E-invoice completeness rule set (D6, ADR-221)

The core keeps the check that lists missing mandatory EN 16931 data for an invoice (12, section 10). Rules:
1. The check is a **fixed rule set per edition of the standard**, shipped with the core and selected by the company setting `einvoice_rule_set` (delta 3).
2. A new edition is a new rule set, never a silent change of the old one. Both can exist, so a company moves when its country does.
3. `STD_EN16931` lists the same terms with sources for review (18, 4.5). The package is documentation to the core and is never read by it. A conformance test compares them: a difference is a defect of one of the two.
4. The choice of edition (2017+A1:2019 or the 2026 edition, which is not backward compatible, UNVERIFIED) is still open and belongs to the user. Until it is made, `EN16931_2017_A1` is the only rule set code defined (07, 5.5), and the core release that ships it must also ship its rules. The rule set itself is not written yet: it comes with the conformance test of `STD_EN16931`.

## 11. Conformance flow tests

Each test runs against the API in a rehearsal tenant (13, ADR-134). A test passes only if the response, the stored records and the outbox events match.

| # | Flow | Checks |
|---|---|---|
| FT-01 | Post an order-built sales invoice twice with the same key | One document. Second response `idempotent_replay` |
| FT-02 | Same key, changed payload | `IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST`, nothing posted |
| FT-03 | Invoice, e-invoice data, record issue, record a second issue | Two issue records, one invoice, events `invoice.issued` |
| FT-04 | Direct `AR_INVOICE` then `invoices:record-issue` with `document_id`; with both targets; with a `JOURNAL` document; with a mirror document | Record written for the first. `ORDER.ISSUE_TARGET_INVALID`, `ORDER.ISSUE_DOCUMENT_TYPE_INVALID` for the others (delta 1) |
| FT-04b | `issue_state` filter on `sales-invoices` and `journal-documents` before and after an issue record | NOT_ISSUED, then ISSUED. `READ.INVALID_FILTER` on a `JOURNAL` list (delta 6) |
| FT-05 | `PAYMENT` posted on a customer without invoice, no due date, then with the payment date | Settles OQ3 |
| FT-06 | Payment equal to invoice, clear | Both items CLEARED, `clearing.posted` |
| FT-07 | Short payment, overpayment, discount via credit memo, three invoices one payment | Residuals as in 5.2 |
| FT-08 | USD payment on an EUR-reporting company, invoice in USD at other rate | Realised FX line on `FX_REALISED`, TC nets to zero |
| FT-09 | Returned payment flow of 5.6 | Invoice open again, both movements visible, no reversal |
| FT-10 | Two apps clear the same invoice concurrently | One wins, the other `CONFLICT.ITEM_ALREADY_CLEARED` retryable |
| FT-11 | Block, expected value mismatch, release | Audit rows in `change_log`, `CONFLICT.EXPECTED_VALUE_MISMATCH` on a stale write |
| FT-12 | Credit note above cap | `ORDER.CREDIT_EXCEEDS_INVOICED` |
| FT-13 | Reverse invoice with clearing, then with credit note | `REVERSAL.HAS_CLEARED_LINES`, `ORDER.INVOICE_HAS_CREDIT_NOTES` |
| FT-14 | IC invoice pair and reversal | Both sides, same reason code |
| FT-15 | Direct `AP_INVOICE` with partial deduction cap | `TAX.DEDUCTION_AMOUNT_*` rules (19) |
| FT-16 | App crash between payment post and clearing | Resume at step 4 with the same keys, one clearing |
| FT-17 | Restore drill, app consumer ahead of the core | Reconciliation of 8, no duplicate posting |
| FT-18 | Billing-via-orders: 1,000 invoices of one contract | Order progress, per-invoice time, lock waits |
| FT-19 | Posting into a hard-closed period | `PERIOD.HARD_CLOSED`, no date shifting |
| FT-20 | Completeness check with a rule set of an older edition | Same result as before the new edition exists |

## 12. Next items

| Item | Content |
|---|---|
| `STD_ISO20022` | **Written 2026-10-19 as DRAFT** (country-packages/STD_ISO20022): 140 mapping entries over SEPA credit transfer, reject, direct debit, statement and notification, code lists, check algorithms. Findings G-ISO-1 to G-ISO-7 in its CHANGELOG. The pain.001 and pain.002 parts are read from the EPC guideline, camt.053 from two bank guides, pain.008 and camt.054 are authored without a source |
| DK 0.4.0 payment content | **Written 2026-10-19 as DRAFT** (country-packages/DK/app/payment): FIK 71, 73 and 75, giro, domestic payment types, NemKonto, Betalingsservice, check algorithms, the Danish Peppol payment rules, one bank's pain.001 profile. Findings F-DK-19 to F-DK-24 in the DK changelog, ADR-223 PROPOSED. The creditor side of Betalingsservice and the pain.001 placement of the FIK reference are not read |
| 23 | Consolidation data (21, section 3) |
| Deltas | The list of section 9, after you accept them |

## 13. Open points

| # | Point | Proposal |
|---|---|---|
| OQ1 | Should the core reject the reversal of an issued invoice | Optional company setting, default off (delta 2). Country packages say whether on |
| OQ2 | Reason for returned payments | The flow of 5.6 needs no new code. Revisit if FT-09 fails or the audit trail is unclear |
| OQ3 | Due date of the open item on a `PAYMENT` line | Payment date. Settled by FT-05 |
| OQ4 | List and ownership of `payment_block` values | Configured list, run block owned by treasury, manual block owned by users |
| OQ5 | Filter for link records without issue record | **Closed:** `issue_state` filter (delta 6) |
| OQ6 | Duplicate check of direct supplier invoices | App check on `external_reference`. A unique key in the core only if apps cannot do it reliably |
| OQ7 | Entity type for extensions on a partner bank account | Confirm in 15, 3.5. `STD_ISO20022` also needs extension attributes on the company (creditor identifier, G-ISO-2) and on the mandate |
| OQ8 | Creditor identifier for SEPA direct debits (G-ISO-2) | Extension attribute of the company, or a field of `company_bank_account`. Decide with the first debit client |
| OQ9 | Company address for payment files (G-ISO-1) | Part of ADR-207 (PROPOSED). The address rule of the EPC guideline makes it more useful |
| A1 | Volume of billing runs | FT-18 and the load test |

## 14. ADRs raised by this spec

ACCEPTED 2026-10-19, full text in 01-adr-log: ADR-219 (invoicing and payment are app domains over a documented core contract: D0, D1, D5, D7), ADR-220 (advance invoices not in the next release, payment runs post at the bank statement: D3, D4), ADR-221 (e-invoice completeness check stays in the core as a fixed rule set per EN 16931 edition: D6), ADR-222 (consolidation results stay in apps, data gaps only: D2, text in 23).
