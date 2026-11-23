-- 0045 sales credit notes (11, 6.5, 6.9): the credit note is a row of sales_invoice with the kind CREDIT_NOTE (the columns exist since 0042); its lines name the invoice line they credit and,
-- for a return credit, the return line. Signed: the rows of a credit note are negative, the rows of its reversal positive. Not here: purchase credit notes.

alter table core.sales_invoice_line add column if not exists credits_invoice_line_no int;
alter table core.sales_invoice_line add column if not exists return_id uuid;
alter table core.sales_invoice_line add column if not exists return_line_no int;

do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'sales_invoice_line_return_fk') then
        alter table core.sales_invoice_line add constraint sales_invoice_line_return_fk
            foreign key (tenant_id, company_id, return_id, return_line_no) references core.sales_return_line (tenant_id, company_id, return_id, line_no);
    end if;
    if not exists (select 1 from pg_constraint where conname = 'sales_invoice_line_return_pair') then
        alter table core.sales_invoice_line add constraint sales_invoice_line_return_pair check ((return_id is null) = (return_line_no is null));
    end if;
end $$;
create index if not exists sales_invoice_line_return on core.sales_invoice_line (tenant_id, company_id, return_id, return_line_no) where return_id is not null;

insert into platform.schema_migration (version, description)
values ('0045', 'sales credit notes: the credited invoice line and the return line on the link lines')
on conflict (version) do nothing;
