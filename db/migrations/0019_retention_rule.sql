-- 0019 retention_rule (12, 4.1): how long a record class is kept, per company and country, versioned. A rule change affects only records created
-- afterwards (12, Q13), so the table is insert-only (pattern V). The records that store a date take it from here at creation: the legal snapshot of
-- a document comes with the next step. Written by retention-rules:create-version (permission retention.manage), read by the posting commands.

create table if not exists core.retention_rule (
    tenant_id       uuid not null,
    company_id      uuid not null,
    rule_id         uuid not null,
    effective_from  timestamptz not null,
    record_class    core.code not null check (record_class in (
        'LEDGER', 'VAT_RETURN', 'STOCK', 'ORDER', 'FULFILMENT', 'INVOICE_ISSUE', 'LEGAL_SNAPSHOT', 'ADDRESS_SNAPSHOT', 'AUDIT_LOG', 'READ_LOG')),
    country_code    char(2) not null references platform.ref_country (country_code),
    retention_years smallint not null check (retention_years between 1 and 100),
    start_event     core.code not null check (start_event in (
        'FISCAL_YEAR_END', 'RECORD_DATE', 'CALENDAR_YEAR_END_OF_RECORD', 'CALENDAR_YEAR_END_OF_FISCAL_YEAR')),
    legal_basis     varchar(200),
    created_at      timestamptz not null default now(),
    created_by      varchar(100) not null,
    primary key (tenant_id, company_id, rule_id, effective_from),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    -- one version of a rule at a time: a rule is a (company, class, country)
    unique (tenant_id, company_id, record_class, country_code, effective_from)
);
create index if not exists retention_rule_class on core.retention_rule (tenant_id, company_id, record_class, effective_from);

-- All versions of a rule_id are versions of the same rule: the class and the country never change (12, 4.1)
create or replace function core.guard_retention_rule() returns trigger language plpgsql as $$
begin
    if exists (select 1 from core.retention_rule r
                where r.tenant_id = new.tenant_id and r.company_id = new.company_id and r.rule_id = new.rule_id
                  and (r.record_class <> new.record_class or r.country_code <> new.country_code)) then
        raise exception 'retention_rule: the class and the country of a rule do not change between its versions' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists retention_rule_guard on core.retention_rule;
create trigger retention_rule_guard before insert on core.retention_rule
    for each row execute function core.guard_retention_rule();

alter table core.retention_rule enable row level security;
alter table core.retention_rule force row level security;
do $$ begin
    if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = 'retention_rule' and policyname = 'tenant_isolation') then
        create policy tenant_isolation on core.retention_rule using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id());
    end if;
end $$;
revoke all on core.retention_rule from public;
grant select on core.retention_rule to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_retention;
grant insert on core.retention_rule to cmd_config;
drop trigger if exists retention_rule_append_only on core.retention_rule;
create trigger retention_rule_append_only before update or delete on core.retention_rule
    for each row execute function core.reject_change();
drop trigger if exists retention_rule_no_truncate on core.retention_rule;
create trigger retention_rule_no_truncate before truncate on core.retention_rule
    for each statement execute function core.reject_change();

insert into platform.schema_migration (version, description)
values ('0019', 'retention_rule, versioned and insert-only')
on conflict (version) do nothing;
