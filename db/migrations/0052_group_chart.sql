-- 0052 group chart of accounts (07, 6.4, 23, 4, ADR-017, ADR-227): group_chart_account, pattern M (current state, row_version, every change in master_change_log), written by cmd_config.
-- translation_rate_basis is read by the consolidation app only; null means the default of the account type. The mapping of company accounts (account_group_mapping, V) is a later slice.
-- The generic master commands address a row by one id, so the table has group_account_id as key with the group in a unique pair (tenant_id, group_id, code).

create table if not exists core.group_chart_account (
    tenant_id              uuid not null references platform.tenant (tenant_id),
    group_account_id       uuid not null default gen_random_uuid(),
    group_id               uuid not null,
    code                   core.code not null,
    name                   varchar(100) not null,
    account_type           core.code not null check (account_type in ('ASSET', 'LIABILITY', 'EQUITY', 'REVENUE', 'EXPENSE')),
    translation_rate_basis core.code check (translation_rate_basis in ('CLOSING', 'AVERAGE', 'HISTORICAL')),
    active                 boolean not null default true,
    row_version            bigint not null default 1,
    created_at             timestamptz not null default now(),
    created_by             varchar(100) not null,
    updated_at             timestamptz not null default now(),
    updated_by             varchar(100) not null,
    primary key (tenant_id, group_account_id),
    unique (tenant_id, group_id, code),
    unique (tenant_id, group_id, group_account_id),
    foreign key (tenant_id, group_id) references core.company_group (tenant_id, group_id)
);

do $$
declare t text := 'group_chart_account';
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
    execute format('grant insert on core.%I to cmd_config', t);
    execute format('drop trigger if exists %I on core.%I', t || '_no_delete', t);
    execute format('create trigger %I before delete on core.%I for each row execute function core.reject_delete()', t || '_no_delete', t);
    execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
    execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_delete()', t || '_no_truncate', t);
end $$;
grant update (code, name, translation_rate_basis, active, row_version, updated_at, updated_by) on core.group_chart_account to cmd_config;

insert into platform.schema_migration (version, description)
values ('0052', 'group chart of accounts: group_chart_account')
on conflict (version) do nothing;
