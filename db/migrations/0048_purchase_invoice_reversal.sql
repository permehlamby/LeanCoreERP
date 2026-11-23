-- 0048 reversal of purchase invoices (11, 7.4): a supplier invoice has an AP open item and GR/IR items that its own clearing cleared, so its reversal runs as the clearing role (the reset of that
-- clearing and the automatic clearing of 03, 5.7 are written by that role). The role gets the two link tables for the mirror rows and, for the mirror of the cost adjustment (a STOCK_REVERSAL
-- document with COST_ADJUST lines, 10, 5.5), the two stock tables, the status of the original stock document and the STOCK head.

grant insert on core.purchase_invoice, core.purchase_invoice_line, core.stock_document, core.stock_movement to cmd_clearing;
grant update (status, reversed_by_stock_document_id) on core.stock_document to cmd_clearing;

drop policy if exists chain_head_clearing on core.chain_head;
create policy chain_head_clearing on core.chain_head as restrictive for update to cmd_clearing
    using (chain_type in ('LEDGER', 'CLEARING', 'STOCK')) with check (chain_type in ('LEDGER', 'CLEARING', 'STOCK'));

insert into platform.schema_migration (version, description)
values ('0048', 'reversal of purchase invoices: the clearing role writes the mirror link rows and the mirror of the cost adjustment')
on conflict (version) do nothing;
