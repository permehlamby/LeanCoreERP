-- 0055 legacy_tax_summary (13, 3.6, Q1): the legacy VAT of the part of a VAT period before a migration cutover. A row is one result row of a return (a box, an EC list row or an OSS row)
-- with its base and tax amounts and the legacy ledger amounts of the VAT account. Created by the commit of a migration batch (area LEGACY_TAX), reported by the first return of the
-- period (reported_in_return_id, S), deleted only by the rollback of its batch while it is not reported.

create table if not exists core.legacy_tax_summary (
    tenant_id             uuid not null references platform.tenant (tenant_id),
    company_id            uuid not null,
    summary_id            uuid not null default gen_random_uuid(),
    batch_id              uuid not null,
    registration_id       uuid not null,
    fiscal_year           int not null,
    vat_period_no         smallint not null check (vat_period_no >= 1),
    return_type           core.code not null check (return_type in ('STANDARD', 'EC_LIST', 'OSS', 'IOSS')),
    row_type              core.code not null check (row_type in ('BOX', 'EC_LIST', 'OSS')),
    box_code              core.code,
    partner_id            uuid,
    partner_vat_id        varchar(30),
    country_code          char(2),
    category              core.code check (category in ('GOODS', 'SERVICES', 'TRIANGULATION')),
    rate                  core.rate,
    supply_kind           core.code check (supply_kind in ('GOODS', 'SERVICES')),
    currency_code         char(3) not null references platform.ref_currency (currency_code),
    base_amount           core.amt not null,
    tax_amount            core.amt not null,
    vat_account_id        uuid,
    ledger_lc             core.amt not null default 0,
    ledger_gc             core.amt not null default 0,
    ledger_rc             core.amt,
    legacy_ref            varchar(100) not null,
    reported_in_return_id uuid,                                    -- S
    created_at            timestamptz not null default now(),
    created_by            varchar(100) not null,
    primary key (tenant_id, company_id, summary_id),
    unique (tenant_id, batch_id, legacy_ref),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, batch_id) references core.migration_batch (tenant_id, batch_id),
    foreign key (tenant_id, company_id, registration_id) references core.company_vat_registration (tenant_id, company_id, registration_id),
    foreign key (tenant_id, company_id, vat_account_id) references core.gl_account (tenant_id, company_id, account_id),
    foreign key (tenant_id, company_id, reported_in_return_id) references core.vat_return (tenant_id, company_id, return_id),
    check ((return_type = 'STANDARD' and row_type = 'BOX') or (return_type = 'EC_LIST' and row_type = 'EC_LIST') or (return_type in ('OSS', 'IOSS') and row_type = 'OSS')),
    check (row_type <> 'BOX' or (box_code is not null and partner_id is null and partner_vat_id is null and country_code is null and category is null and rate is null and supply_kind is null)),
    check (row_type <> 'EC_LIST' or (box_code is null and partner_id is not null and country_code is not null and category is not null and rate is null and supply_kind is null)),
    check (row_type <> 'OSS' or (box_code is null and partner_id is null and partner_vat_id is null and country_code is not null and category is null and rate is not null and supply_kind is not null))
);
create index if not exists legacy_tax_summary_period on core.legacy_tax_summary (tenant_id, company_id, registration_id, return_type, fiscal_year, vat_period_no);
create index if not exists legacy_tax_summary_batch on core.legacy_tax_summary (tenant_id, batch_id);

-- only the report marker changes, once; a reported row is never deleted
create or replace function core.guard_legacy_tax_summary() returns trigger language plpgsql as $$
begin
    if tg_op = 'DELETE' then
        if old.reported_in_return_id is not null then
            raise exception 'a legacy tax summary row that a return reported is not deleted' using errcode = '23514';
        end if;
        return old;
    end if;
    if old.reported_in_return_id is not null then
        raise exception 'a legacy tax summary row is reported once' using errcode = '23514';
    end if;
    if (to_jsonb(new) - 'reported_in_return_id') is distinct from (to_jsonb(old) - 'reported_in_return_id') then
        raise exception 'only reported_in_return_id of a legacy tax summary row changes' using errcode = '23514';
    end if;
    return new;
end $$;

do $$
declare t text := 'legacy_tax_summary';
begin
    execute format('alter table core.%I enable row level security', t);
    execute format('alter table core.%I force row level security', t);
    if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = t and policyname = 'tenant_isolation') then
        execute format('create policy tenant_isolation on core.%I using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id())', t);
    end if;
    execute format('revoke all on core.%I from public', t);
    execute format('grant select on core.%I to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_retention, cmd_stock, cmd_order', t);
    if exists (select 1 from pg_roles where rolname = 'cmd_closing') then execute format('grant select on core.%I to cmd_closing', t); end if;
    execute format('grant select, insert, delete on core.%I to cmd_migration', t);
    execute format('grant update (reported_in_return_id) on core.%I to cmd_post', t);
    execute format('drop trigger if exists %I on core.%I', t || '_guard', t);
    execute format('create trigger %I before update or delete on core.%I for each row execute function core.guard_legacy_tax_summary()', t || '_guard', t);
    execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
    execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_delete()', t || '_no_truncate', t);
end $$;

insert into platform.schema_migration (version, description)
values ('0055', 'legacy_tax_summary: the legacy VAT of a partial VAT period at a migration cutover')
on conflict (version) do nothing;
