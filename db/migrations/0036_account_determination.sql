-- 0036 account_determination (10, 3.9, pattern V): the accounts that the inventory and order events post to, by role and the keys item group, warehouse, partner and item.
-- A determination is a determination_id with versions, every version is a row, rows are never changed or deleted. Every key column is nullable, null means any. The version
-- holds until the next version of its determination starts (or its valid_to). Written by cmd_config. The stock commands (next slices of 10) read it through
-- AccountDeterminationResolver.

create table if not exists core.account_determination (
    tenant_id        uuid not null references platform.tenant (tenant_id),
    company_id       uuid not null,
    determination_id uuid not null,
    valid_from       date not null,
    valid_to         date,
    role_code        core.code not null check (role_code in (
        'INVENTORY', 'COGS', 'GR_IR', 'PRICE_VARIANCE', 'ADJUSTMENT_GAIN', 'ADJUSTMENT_LOSS', 'NEG_STOCK_VARIANCE', 'LANDED_COST_CLEARING', 'STOCK_TRANSFER_CLEARING',
        'AR_CONTROL', 'AP_CONTROL', 'REVENUE', 'PURCHASE_EXPENSE', 'DROPSHIP_CLEARING', 'GR_IR_WRITE_OFF')),
    item_group_id    uuid,
    warehouse_id     uuid,
    partner_id       uuid,
    item_id          uuid,
    priority         smallint not null check (priority >= 0),
    account_id       uuid not null,
    created_at       timestamptz not null default now(),
    created_by       varchar(100) not null,
    primary key (tenant_id, company_id, determination_id, valid_from),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, company_id, account_id) references core.gl_account (tenant_id, company_id, account_id),
    foreign key (tenant_id, item_group_id) references core.item_group (tenant_id, item_group_id),
    foreign key (tenant_id, company_id, warehouse_id) references core.warehouse (tenant_id, company_id, warehouse_id),
    foreign key (tenant_id, partner_id) references core.business_partner (tenant_id, partner_id),
    foreign key (tenant_id, item_id) references core.item (tenant_id, item_id),
    check (valid_to is null or valid_to >= valid_from)
);
create index if not exists account_determination_role on core.account_determination (tenant_id, company_id, role_code, valid_from);

alter table core.account_determination enable row level security;
alter table core.account_determination force row level security;
do $$ begin
    if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = 'account_determination' and policyname = 'tenant_isolation') then
        create policy tenant_isolation on core.account_determination
            using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id());
    end if;
end $$;
revoke all on core.account_determination from public;
grant select on core.account_determination to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_retention;
do $$ begin
    if exists (select 1 from pg_roles where rolname = 'cmd_closing') then grant select on core.account_determination to cmd_closing; end if;
    if exists (select 1 from pg_roles where rolname = 'cmd_migration') then grant select on core.account_determination to cmd_migration; end if;
end $$;
grant insert on core.account_determination to cmd_config;
drop trigger if exists account_determination_append_only on core.account_determination;
create trigger account_determination_append_only before update or delete on core.account_determination
    for each row execute function core.reject_change();
drop trigger if exists account_determination_no_truncate on core.account_determination;
create trigger account_determination_no_truncate before truncate on core.account_determination
    for each statement execute function core.reject_change();

insert into platform.schema_migration (version, description)
values ('0036', 'account_determination: the accounts of the inventory and order events by role and keys, versioned and insert-only')
on conflict (version) do nothing;
