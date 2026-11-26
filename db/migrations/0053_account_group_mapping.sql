-- 0053 account_group_mapping (07, 6.4, 23, 4, ADR-227), pattern V: the mapping of a company account to a group account, effective-dated, one group account per company account at a time
-- (no overlap, like group_membership). Insert-only but for valid_to: the command closes the open row when a later mapping starts. The rule that the group account belongs to the group of the
-- company at valid_from needs two tables and is the command's (CONFIG.GROUP_ACCOUNT_OTHER_GROUP).

create table if not exists core.account_group_mapping (
    tenant_id        uuid not null references platform.tenant (tenant_id),
    company_id       uuid not null,
    account_id       uuid not null,
    valid_from       date not null,
    group_account_id uuid not null,
    valid_to         date,
    created_at       timestamptz not null default now(),
    created_by       varchar(100) not null,
    primary key (tenant_id, company_id, account_id, valid_from),
    foreign key (tenant_id, company_id, account_id) references core.gl_account (tenant_id, company_id, account_id),
    foreign key (tenant_id, group_account_id) references core.group_chart_account (tenant_id, group_account_id),
    check (valid_to is null or valid_to > valid_from),
    exclude using gist (tenant_id with =, company_id with =, account_id with =, daterange(valid_from, valid_to, '[)') with &&)
);
create index if not exists account_group_mapping_group_account on core.account_group_mapping (tenant_id, group_account_id);

create or replace function core.guard_account_group_mapping() returns trigger language plpgsql as $$
begin
    if (to_jsonb(new) - 'valid_to') is distinct from (to_jsonb(old) - 'valid_to') or old.valid_to is not null or new.valid_to is null then
        raise exception 'account_group_mapping: only an open row can get its valid_to' using errcode = '23000';
    end if;
    return new;
end $$;

do $$
declare t text := 'account_group_mapping';
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
    execute format('drop trigger if exists %I on core.%I', t || '_guard', t);
    execute format('create trigger %I before update on core.%I for each row execute function core.guard_account_group_mapping()', t || '_guard', t);
    execute format('drop trigger if exists %I on core.%I', t || '_no_delete', t);
    execute format('create trigger %I before delete on core.%I for each row execute function core.reject_delete()', t || '_no_delete', t);
    execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
    execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_delete()', t || '_no_truncate', t);
end $$;
grant update (valid_to) on core.account_group_mapping to cmd_config;

insert into platform.schema_migration (version, description)
values ('0053', 'account_group_mapping: company account to group account, effective-dated')
on conflict (version) do nothing;
