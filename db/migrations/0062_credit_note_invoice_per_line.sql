-- 0062: a supplier credit note across several invoices (11, 7.3; F-IMPL-104).
-- A credit line names the invoice it credits: purchase_invoice_line.credits_purchase_invoice_id. The header column credits_invoice_id keeps the invoice named in the command (the first one),
-- so the check of 0046 holds. Rows written before this migration have no value: their invoice is the one of the header (coalesce in the reads).
alter table core.purchase_invoice_line add column if not exists credits_purchase_invoice_id uuid;

do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'purchase_invoice_line_credits_invoice_fk') then
        alter table core.purchase_invoice_line
            add constraint purchase_invoice_line_credits_invoice_fk foreign key (tenant_id, company_id, credits_purchase_invoice_id)
            references core.purchase_invoice (tenant_id, company_id, purchase_invoice_id);
    end if;
end $$;

insert into platform.schema_migration (version, description)
values ('0062', 'purchase credit note lines name the invoice they credit')
on conflict (version) do nothing;
