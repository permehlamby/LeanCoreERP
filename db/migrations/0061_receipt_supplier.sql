-- 0061: the supplier of a receipt without an order (11, 7.2, D3; F-IMPL-100).
-- stock-documents:post takes an optional supplier_partner_id on a RECEIPT. It is a reference, not an L1 column: it is outside the row hash like header_text, and it is written
-- with the insert and never changed. purchase-invoices:post checks it against the supplier of the invoice (MATCH.NO_RECEIPT). A receipt posted without a supplier cannot be checked.
alter table core.stock_document add column if not exists supplier_partner_id uuid;

do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'stock_document_supplier_fk') then
        alter table core.stock_document
            add constraint stock_document_supplier_fk foreign key (tenant_id, supplier_partner_id) references core.business_partner (tenant_id, partner_id);
    end if;
    if not exists (select 1 from pg_constraint where conname = 'stock_document_supplier_receipt_only') then
        alter table core.stock_document
            add constraint stock_document_supplier_receipt_only check (supplier_partner_id is null or document_type = 'STOCK_RECEIPT');
    end if;
end $$;

insert into platform.schema_migration (version, description)
values ('0061', 'supplier of a receipt without an order on stock_document')
on conflict (version) do nothing;
