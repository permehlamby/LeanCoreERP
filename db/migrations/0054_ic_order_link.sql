-- 0054 ic_order_link (11, 6.7, ADR-016): one row per intercompany order pair, the sales order of the seller and the purchase order of the buyer, linked by the ic_transaction_id that both orders
-- carry. Insert-only: ic-orders:change changes the orders, not the link. An order belongs to at most one pair (the two unique keys). Written by the order role.

create table if not exists core.ic_order_link (
    tenant_id           uuid not null references platform.tenant (tenant_id),
    ic_transaction_id   uuid not null,
    sales_company_id    uuid not null,
    sales_order_id      uuid not null,
    purchase_company_id uuid not null,
    purchase_order_id   uuid not null,
    created_at          timestamptz not null default now(),
    created_by          varchar(100) not null,
    correlation_id      varchar(100) not null,
    idempotency_key     varchar(100) not null,
    primary key (tenant_id, ic_transaction_id),
    unique (tenant_id, sales_company_id, sales_order_id),
    unique (tenant_id, purchase_company_id, purchase_order_id),
    unique (tenant_id, idempotency_key),
    foreign key (tenant_id, sales_company_id, sales_order_id) references core.sales_order (tenant_id, company_id, sales_order_id),
    foreign key (tenant_id, purchase_company_id, purchase_order_id) references core.purchase_order (tenant_id, company_id, purchase_order_id),
    check (sales_company_id <> purchase_company_id)
);

do $$
declare t text := 'ic_order_link';
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
    execute format('grant insert on core.%I to cmd_order', t);
    execute format('drop trigger if exists %I on core.%I', t || '_append_only', t);
    execute format('create trigger %I before update on core.%I for each row execute function core.reject_change()', t || '_append_only', t);
    execute format('drop trigger if exists %I on core.%I', t || '_no_delete', t);
    execute format('create trigger %I before delete on core.%I for each row execute function core.reject_delete()', t || '_no_delete', t);
    execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
    execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_delete()', t || '_no_truncate', t);
end $$;

insert into platform.schema_migration (version, description)
values ('0054', 'ic_order_link: the link of an intercompany order pair')
on conflict (version) do nothing;
