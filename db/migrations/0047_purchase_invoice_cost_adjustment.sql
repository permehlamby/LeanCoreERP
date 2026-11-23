-- 0047 cost adjustment of a purchase invoice (11, 7.2; 10, 5.6, 6.6): a supplier invoice whose value differs from the receipt value posts the difference to inventory and cost of goods sold, and the
-- part on hand is a COST_ADJUST movement of a STOCK_COST_ADJUSTMENT document that uses the journal document of the invoice as its derived journal (external journal mode, ADR-052). The invoice
-- runs as the posting role, so that role gets what the stock role has for a document: the two stock tables and the STOCK head (the CHAIN head policy is re-created, copy of 0046 with one more type).

grant insert on core.stock_document, core.stock_movement to cmd_post;

drop policy if exists chain_head_post on core.chain_head;
create policy chain_head_post on core.chain_head as restrictive for update to cmd_post
    using (chain_type in ('LEDGER', 'VAT_RETURN', 'CLEARING', 'STOCK')) with check (chain_type in ('LEDGER', 'VAT_RETURN', 'CLEARING', 'STOCK'));

insert into platform.schema_migration (version, description)
values ('0047', 'purchase invoice cost adjustment: the posting role writes stock documents and moves the STOCK head')
on conflict (version) do nothing;
