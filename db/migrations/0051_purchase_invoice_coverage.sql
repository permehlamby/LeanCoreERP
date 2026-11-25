-- 0051 coverage of an invoice before receipt (11, 7.1, 7.2): a supplier invoice for goods that are not received yet (a TWO_WAY line) leaves a GR/IR debit that waits for the receipt. When a receipt
-- arrives, its first units cover the invoiced quantity and are valued at the invoice value (cost source INVOICE), and the receipt clears the GR/IR debit of the invoice in the same transaction.
-- One row says how much of an invoice line a receipt line covers and at what value. Insert-only: the reversal of the receipt inserts rows with the opposite sign. The receipt is posted by the stock
-- role, which writes the rows.

create table if not exists core.purchase_invoice_coverage (
    tenant_id              uuid not null references platform.tenant (tenant_id),
    company_id             uuid not null,
    coverage_id            uuid not null default gen_random_uuid(),
    purchase_invoice_id    uuid not null,
    invoice_line_no        int not null,
    receipt_id             uuid not null,
    receipt_line_no        int not null,
    covered_quantity       numeric(19,6) not null check (covered_quantity <> 0),
    covered_value_lc       core.amt not null,
    clearing_id            uuid,
    created_at             timestamptz not null default now(),
    created_by             varchar(100) not null,
    correlation_id         varchar(100) not null,
    primary key (tenant_id, company_id, coverage_id),
    foreign key (tenant_id, company_id, purchase_invoice_id, invoice_line_no) references core.purchase_invoice_line (tenant_id, company_id, purchase_invoice_id, line_no),
    foreign key (tenant_id, company_id, receipt_id, receipt_line_no) references core.purchase_receipt_line (tenant_id, company_id, receipt_id, line_no)
);
create index if not exists purchase_invoice_coverage_invoice on core.purchase_invoice_coverage (tenant_id, company_id, purchase_invoice_id, invoice_line_no);
create index if not exists purchase_invoice_coverage_receipt on core.purchase_invoice_coverage (tenant_id, company_id, receipt_id, receipt_line_no);

do $$
declare t text := 'purchase_invoice_coverage';
begin
    execute format('alter table core.%I enable row level security', t);
    execute format('alter table core.%I force row level security', t);
    if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = t and policyname = 'tenant_isolation') then
        execute format('create policy tenant_isolation on core.%I using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id())', t);
    end if;
    execute format('revoke all on core.%I from public', t);
    execute format('grant select on core.%I to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_retention, cmd_stock, cmd_order', t);
    if exists (select 1 from pg_roles where rolname = 'cmd_closing') then execute format('grant select on core.%I to cmd_closing', t); end if;
    if exists (select 1 from pg_roles where rolname = 'cmd_migration') then execute format('grant select on core.%I to cmd_migration', t); end if;
    execute format('grant insert on core.%I to cmd_stock', t);
    execute format('drop trigger if exists %I on core.%I', t || '_append_only', t);
    execute format('create trigger %I before update on core.%I for each row execute function core.reject_change()', t || '_append_only', t);
    execute format('drop trigger if exists %I on core.%I', t || '_no_delete', t);
    execute format('create trigger %I before delete on core.%I for each row execute function core.reject_delete()', t || '_no_delete', t);
    execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
    execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_delete()', t || '_no_truncate', t);
end $$;

insert into platform.schema_migration (version, description)
values ('0051', 'coverage of an invoice before receipt: which units of a receipt line cover which invoice line, at the invoice value')
on conflict (version) do nothing;
