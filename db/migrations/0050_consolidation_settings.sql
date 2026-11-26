-- 0050 consolidation settings (23, 3.1, ADR-224): ownership and method per company, versioned by effective_from, insert-only, written by the configuration role.
-- The rules that need other tables (group membership, parent in the same group, no cycle, translation method against the currencies) are checked by the command.

create table if not exists core.company_consolidation_setting_version (
    tenant_id             uuid not null references platform.tenant (tenant_id),
    company_id            uuid not null,
    effective_from        date not null,
    parent_company_id     uuid,
    ownership_pct         core.rate,
    consolidation_method  core.code not null check (consolidation_method in ('FULL', 'PROPORTIONAL', 'EQUITY', 'NOT_CONSOLIDATED')),
    translation_method    core.code not null check (translation_method in ('NONE', 'CURRENT_RATE', 'TEMPORAL')),
    created_at            timestamptz not null default now(),
    created_by            varchar(100) not null,
    primary key (tenant_id, company_id, effective_from),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, parent_company_id) references core.company (tenant_id, company_id),
    check (parent_company_id is null or parent_company_id <> company_id),
    check ((parent_company_id is null) = (ownership_pct is null)),
    check (ownership_pct is null or (ownership_pct > 0 and ownership_pct <= 100))
);

do $$
declare t text := 'company_consolidation_setting_version';
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
    execute format('drop trigger if exists %I on core.%I', t || '_append_only', t);
    execute format('create trigger %I before update on core.%I for each row execute function core.reject_change()', t || '_append_only', t);
    execute format('drop trigger if exists %I on core.%I', t || '_no_delete', t);
    execute format('create trigger %I before delete on core.%I for each row execute function core.reject_delete()', t || '_no_delete', t);
    execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
    execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_delete()', t || '_no_truncate', t);
end $$;

insert into platform.schema_migration (version, description)
values ('0050', 'consolidation settings: company_consolidation_setting_version')
on conflict (version) do nothing;
