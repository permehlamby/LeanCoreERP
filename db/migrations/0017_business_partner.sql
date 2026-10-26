-- 0017 The partner master (07, 9.1 to 9.3) and payment terms (07, 9.6): business_partner, business_partner_pii (anonymisable),
-- partner_company_relation and payment_terms, all pattern M (current state, row_version, every change in master_change_log).
-- Written by the commands of 07, 11.1 (write.master for the partner, write.config for the terms). Not in this migration: partner_tax_registration
-- (waits for tax), the bank accounts (07, 9.5), the anonymisation and the retention columns' writers (12). The foreign keys of the
-- columns that already carry a partner or terms (open items, clearings) are added with the posting of CONTROL accounts.

-- ---------------------------------------------------------------------------------------------------------
-- payment_terms (07, 9.6)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.payment_terms (
    tenant_id        uuid not null references platform.tenant (tenant_id),
    payment_terms_id uuid not null default gen_random_uuid(),
    code             core.code not null,
    description      varchar(100) not null,
    baseline_rule    core.code not null check (baseline_rule in ('DOCUMENT_DATE', 'POSTING_DATE', 'END_OF_MONTH')),
    net_days         smallint not null check (net_days >= 0),
    discount_days    smallint check (discount_days >= 0),
    discount_pct     core.rate check (discount_pct > 0 and discount_pct <= 100),
    active           boolean not null default true,
    row_version      bigint not null default 1,
    created_at       timestamptz not null default now(),
    created_by       varchar(100) not null,
    updated_at       timestamptz not null default now(),
    updated_by       varchar(100) not null,
    primary key (tenant_id, payment_terms_id),
    unique (tenant_id, code),
    check ((discount_days is null) = (discount_pct is null))
);

-- ---------------------------------------------------------------------------------------------------------
-- business_partner (07, 9.1): nothing in this table is personal data
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.business_partner (
    tenant_id                    uuid not null references platform.tenant (tenant_id),
    partner_id                   uuid not null default gen_random_uuid(),
    partner_code                 core.code not null,
    partner_kind                 core.code not null check (partner_kind in ('ORGANISATION', 'PERSON')),   -- IAP
    is_customer                  boolean not null default false,
    is_supplier                  boolean not null default false,
    ic_company_id                uuid,
    tenant_credit_limit          core.amt check (tenant_credit_limit >= 0),
    tenant_credit_limit_currency char(3) references platform.ref_currency (currency_code),
    status                       core.code not null default 'ACTIVE' check (status in ('ACTIVE', 'BLOCKED', 'ANONYMISED')),
    retention_until              date,                                 -- S, written by the retention job (12, 4.2)
    retention_floor              date,                                 -- S, written by retention:extend (12)
    restriction_status           core.code not null default 'NONE' check (restriction_status in ('NONE', 'RESTRICTED')),   -- S, data subject requests (12, 5.3)
    anonymised_at                timestamptz,                          -- S, written by the retention job
    row_version                  bigint not null default 1,
    created_at                   timestamptz not null default now(),
    created_by                   varchar(100) not null,
    updated_at                   timestamptz not null default now(),
    updated_by                   varchar(100) not null,
    primary key (tenant_id, partner_id),
    unique (tenant_id, partner_code),
    foreign key (tenant_id, ic_company_id) references core.company (tenant_id, company_id),
    check (tenant_credit_limit is null or tenant_credit_limit_currency is not null),
    check ((status = 'ANONYMISED') = (anonymised_at is not null))
);

-- ---------------------------------------------------------------------------------------------------------
-- business_partner_pii (07, 9.2): personal data, 1:1 with the partner, anonymised with it
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.business_partner_pii (
    tenant_id                uuid not null,
    partner_id               uuid not null,
    legal_name               varchar(200) not null,
    trade_name               varchar(200),
    address_line_1           varchar(200),
    address_line_2           varchar(200),
    postal_code              varchar(20),
    city                     varchar(100),
    country_code             char(2) not null references platform.ref_country (country_code),
    email                    varchar(200),
    phone                    varchar(50),
    contact_person           varchar(200),
    electronic_address       varchar(100),
    electronic_address_scheme core.code,
    legal_registration_id    varchar(50),
    row_version              bigint not null default 1,
    created_at               timestamptz not null default now(),
    created_by               varchar(100) not null,
    updated_at               timestamptz not null default now(),
    updated_by               varchar(100) not null,
    primary key (tenant_id, partner_id),
    foreign key (tenant_id, partner_id) references core.business_partner (tenant_id, partner_id)
);

-- ---------------------------------------------------------------------------------------------------------
-- partner_company_relation (07, 9.3)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.partner_company_relation (
    tenant_id                uuid not null,
    partner_id               uuid not null,
    company_id               uuid not null,
    is_customer              boolean not null default false,
    is_supplier              boolean not null default false,
    default_payment_terms_id uuid,
    credit_limit             core.amt check (credit_limit >= 0),
    credit_limit_currency    char(3) references platform.ref_currency (currency_code),
    default_currency         char(3) references platform.ref_currency (currency_code),
    payment_block            core.code,
    dunning_block            core.code,
    status                   core.code not null default 'ACTIVE' check (status in ('ACTIVE', 'BLOCKED')),
    row_version              bigint not null default 1,
    created_at               timestamptz not null default now(),
    created_by               varchar(100) not null,
    updated_at               timestamptz not null default now(),
    updated_by               varchar(100) not null,
    primary key (tenant_id, partner_id, company_id),
    foreign key (tenant_id, partner_id) references core.business_partner (tenant_id, partner_id),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, default_payment_terms_id) references core.payment_terms (tenant_id, payment_terms_id),
    check (credit_limit is null or credit_limit_currency is not null)
);
create index if not exists partner_company_relation_company on core.partner_company_relation (tenant_id, company_id);

-- ---------------------------------------------------------------------------------------------------------
-- Security: forced RLS, read for the command roles, write for the configuration role only, never delete.
-- The partner's S columns have no UPDATE grant: the retention job and the data subject commands get theirs with their commands (12).
-- ---------------------------------------------------------------------------------------------------------
do $$
declare t text;
begin
    foreach t in array array['payment_terms', 'business_partner', 'business_partner_pii', 'partner_company_relation'] loop
        execute format('alter table core.%I enable row level security', t);
        execute format('alter table core.%I force row level security', t);
        if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = t and policyname = 'tenant_isolation') then
            execute format('create policy tenant_isolation on core.%I using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id())', t);
        end if;
        execute format('revoke all on core.%I from public', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_delete', t);
        execute format('create trigger %I before delete on core.%I for each row execute function core.reject_delete()', t || '_no_delete', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
        execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_delete()', t || '_no_truncate', t);
    end loop;
end $$;
-- the personal data is read by the roles that need it: the posting roles for the legal snapshot (02, 4) come with the invoice types
grant select on core.payment_terms, core.business_partner, core.partner_company_relation
      to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_retention;
grant select on core.business_partner_pii to cmd_read, cmd_config, cmd_retention;
grant insert on core.payment_terms, core.business_partner, core.business_partner_pii, core.partner_company_relation to cmd_config;
grant update (code, description, baseline_rule, net_days, discount_days, discount_pct, active, row_version, updated_at, updated_by)
      on core.payment_terms to cmd_config;
grant update (partner_code, partner_kind, is_customer, is_supplier, ic_company_id, tenant_credit_limit, tenant_credit_limit_currency, status,
              row_version, updated_at, updated_by) on core.business_partner to cmd_config;
grant update (legal_name, trade_name, address_line_1, address_line_2, postal_code, city, country_code, email, phone, contact_person,
              electronic_address, electronic_address_scheme, legal_registration_id, row_version, updated_at, updated_by)
      on core.business_partner_pii to cmd_config;
grant update (is_customer, is_supplier, default_payment_terms_id, credit_limit, credit_limit_currency, default_currency, payment_block,
              dunning_block, status, row_version, updated_at, updated_by) on core.partner_company_relation to cmd_config;

-- ---------------------------------------------------------------------------------------------------------
-- Guards (07, 2.3, 9.1): the kind of a partner is IAP, an anonymised partner changes no more, and only the retention job anonymises
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.guard_business_partner() returns trigger language plpgsql as $$
begin
    if old.status = 'ANONYMISED' and not pg_has_role(current_user, 'cmd_retention', 'member') then
        raise exception 'MASTER.PARTNER_ANONYMISED: an anonymised partner cannot change' using errcode = '23000';
    end if;
    if new.status = 'ANONYMISED' and old.status <> 'ANONYMISED' and not pg_has_role(current_user, 'cmd_retention', 'member') then
        raise exception 'business_partner: only the retention job anonymises a partner' using errcode = '23000';
    end if;
    if new.partner_kind <> old.partner_kind and exists (
        select 1 from core.journal_line_open_item o where o.tenant_id = old.tenant_id and o.partner_id = old.partner_id) then
        raise exception 'CONFIG.IMMUTABLE_AFTER_POSTING: partner_kind of a partner that open items refer to cannot change' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists business_partner_guard on core.business_partner;
create trigger business_partner_guard before update on core.business_partner
    for each row execute function core.guard_business_partner();

create or replace function core.guard_partner_dependents() returns trigger language plpgsql as $$
begin
    if exists (select 1 from core.business_partner p where p.tenant_id = new.tenant_id and p.partner_id = new.partner_id and p.status = 'ANONYMISED')
       and not pg_has_role(current_user, 'cmd_retention', 'member') then
        raise exception 'MASTER.PARTNER_ANONYMISED: the data of an anonymised partner cannot change' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists business_partner_pii_guard on core.business_partner_pii;
create trigger business_partner_pii_guard before update on core.business_partner_pii
    for each row execute function core.guard_partner_dependents();
drop trigger if exists partner_company_relation_guard on core.partner_company_relation;
create trigger partner_company_relation_guard before update on core.partner_company_relation
    for each row execute function core.guard_partner_dependents();

insert into platform.schema_migration (version, description)
values ('0017', 'business_partner, business_partner_pii, partner_company_relation and payment_terms')
on conflict (version) do nothing;
