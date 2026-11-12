-- 0028 The record of a package apply (18, 7.1, 07, 10.2): one row per apply of a package to a company and one row per object that the apply created or
-- skipped. Insert-only: the record is the audit trail from an object to the package item it came from, and what a later version may extend.
-- Written by the platform operation that applies a package (the migration role), read by every command role.

create table if not exists core.applied_config_package (
    tenant_id        uuid not null,
    applied_id       uuid not null default gen_random_uuid(),
    company_id       uuid not null,
    package_code     core.code not null,
    package_version  varchar(30) not null,
    country_code     char(2) not null references platform.ref_country (country_code),
    kind             core.code not null check (kind in ('STANDARD', 'REGIME', 'COUNTRY')),
    state            core.code not null check (state in ('DRAFT', 'REVIEWED', 'RELEASED', 'DEPRECATED')),
    manifest_digest  varchar(64) not null,       -- SHA-256 over package.json and the core files in fixed order
    inputs_digest    varchar(64) not null,       -- the inputs are recorded by digest, never as content (18, 4.3)
    requires_snapshot varchar(500),              -- the packages that were read besides this one: code and version
    scope            core.code not null default 'VAT' check (scope in ('VAT')),   -- which content was applied: the VAT content only, so far
    item_count       int not null check (item_count >= 0),
    created_count    int not null check (created_count >= 0),
    applied_at       timestamptz not null default now(),
    applied_by       varchar(100) not null,
    primary key (tenant_id, applied_id),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id)
);
create index if not exists applied_config_package_company on core.applied_config_package (tenant_id, company_id, applied_at);

create table if not exists core.applied_config_package_item (
    tenant_id    uuid not null,
    applied_id   uuid not null,
    item_no      int not null check (item_no >= 1),
    item_id      varchar(100) not null,            -- the stable id of the package item, the key of its verification record
    entity_type  core.code not null,
    entity_id    varchar(100),                      -- the id of the object, where the command returned one
    action       core.code not null check (action in ('CREATED', 'VERSIONED', 'SKIPPED_EXISTS', 'SKIPPED_MODIFIED', 'SKIPPED_NOT_SELECTED')),
    primary key (tenant_id, applied_id, item_no),
    foreign key (tenant_id, applied_id) references core.applied_config_package (tenant_id, applied_id)
);

do $$
declare t text;
begin
    foreach t in array array['applied_config_package', 'applied_config_package_item'] loop
        execute format('alter table core.%I enable row level security', t);
        execute format('alter table core.%I force row level security', t);
        if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = t and policyname = 'tenant_isolation') then
            execute format('create policy tenant_isolation on core.%I using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id())', t);
        end if;
        execute format('revoke all on core.%I from public', t);
        execute format('grant select on core.%I to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing', t);
        execute format('drop trigger if exists %I on core.%I', t || '_append_only', t);
        execute format('create trigger %I before update or delete on core.%I for each row execute function core.reject_change()', t || '_append_only', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
        execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_change()', t || '_no_truncate', t);
    end loop;
end $$;

insert into platform.schema_migration (version, description)
values ('0028', 'the record of a package apply and its items')
on conflict (version) do nothing;
