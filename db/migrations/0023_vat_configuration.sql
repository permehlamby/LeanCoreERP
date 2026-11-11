-- 0023 VAT configuration (07, 4, 6.1, 8.1 to 8.3, 08, 4.1, 6.2, 6.7, 19, 3.1): the EU membership of a country, tax classes, return boxes, the
-- VAT registrations of a company, tax codes with their versions and the boxes a version maps to.
-- Written by the commands of 07, 11.1 (write.config). Not in this migration: the determination rules (8.4), the deduction keys and rates (8.6), the
-- partner registrations (9.4), the tax lines of a document (02, 7) and the return snapshots (08, 6). A tax code is not used by any posting yet.

-- ---------------------------------------------------------------------------------------------------------
-- ref_country_eu_membership (07, 4): reference data, maintained by platform releases, readable by every command role
-- ---------------------------------------------------------------------------------------------------------
create table if not exists platform.ref_country_eu_membership (
    country_code char(2) not null references platform.ref_country (country_code),
    valid_from   date not null,
    valid_to     date,
    primary key (country_code, valid_from),
    check (valid_to is null or valid_to > valid_from)
);
alter table platform.ref_country_eu_membership enable row level security;
drop policy if exists ref_read on platform.ref_country_eu_membership;
create policy ref_read on platform.ref_country_eu_membership for select using (true);
revoke all on platform.ref_country_eu_membership from public;
grant select on platform.ref_country_eu_membership to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_retention;

-- ---------------------------------------------------------------------------------------------------------
-- tax_class (07, 8.1)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.tax_class (
    tenant_id    uuid not null references platform.tenant (tenant_id),
    tax_class_id uuid not null default gen_random_uuid(),
    code         core.code not null,
    name         varchar(100) not null,
    active       boolean not null default true,
    row_version  bigint not null default 1,
    created_at   timestamptz not null default now(),
    created_by   varchar(100) not null,
    updated_at   timestamptz not null default now(),
    updated_by   varchar(100) not null,
    primary key (tenant_id, tax_class_id),
    unique (tenant_id, code)
);

do $$ begin
    if not exists (select 1 from pg_constraint where conname = 'gl_account_default_tax_class_fkey') then
        alter table core.gl_account add constraint gl_account_default_tax_class_fkey
            foreign key (tenant_id, default_tax_class_id) references core.tax_class (tenant_id, tax_class_id);
    end if;
end $$;

-- ---------------------------------------------------------------------------------------------------------
-- tax_return_box (07, 8.2, 08, 6.2): the boxes of the VAT returns of a country, per return type
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.tax_return_box (
    tenant_id    uuid not null references platform.tenant (tenant_id),
    country_code char(2) not null references platform.ref_country (country_code),
    return_type  core.code not null check (return_type in ('STANDARD', 'EC_LIST', 'OSS', 'IOSS')),
    box_code     core.code not null,
    name         varchar(100) not null,
    box_group    core.code,
    active       boolean not null default true,
    row_version  bigint not null default 1,
    created_at   timestamptz not null default now(),
    created_by   varchar(100) not null,
    updated_at   timestamptz not null default now(),
    updated_by   varchar(100) not null,
    primary key (tenant_id, country_code, return_type, box_code)
);

-- ---------------------------------------------------------------------------------------------------------
-- company_vat_registration (08, 4.1). The status ACTIVE or INACTIVE of the spec is the flag `active`.
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.company_vat_registration (
    tenant_id          uuid not null references platform.tenant (tenant_id),
    company_id         uuid not null,
    registration_id    uuid not null default gen_random_uuid(),
    country_code       char(2) not null references platform.ref_country (country_code),                                          -- IAP
    vat_id             varchar(30) not null,
    scheme             core.code not null check (scheme in ('STANDARD', 'OSS_UNION', 'OSS_NON_UNION', 'IOSS')),                  -- IAP
    filing_frequency   core.code not null check (filing_frequency in ('MONTHLY', 'QUARTERLY', 'HALF_YEARLY', 'YEARLY')),
    ec_list_frequency  core.code check (ec_list_frequency in ('MONTHLY', 'QUARTERLY')),
    correction_method  core.code not null check (correction_method in ('ADJUST_NEXT', 'SUPPLEMENTARY', 'REPLACEMENT')),
    return_currency    char(3) not null references platform.ref_currency (currency_code),
    vat_rate_type      core.code not null default 'BOOKING' check (vat_rate_type in ('BOOKING', 'VAT')),
    valid_from         date not null,
    valid_to           date,
    active             boolean not null default true,
    row_version        bigint not null default 1,
    created_at         timestamptz not null default now(),
    created_by         varchar(100) not null,
    updated_at         timestamptz not null default now(),
    updated_by         varchar(100) not null,
    primary key (tenant_id, company_id, registration_id),
    unique (tenant_id, company_id, country_code, scheme, valid_from),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    check (valid_to is null or valid_to >= valid_from)
);

-- ---------------------------------------------------------------------------------------------------------
-- tax_code (07, 8.3) and tax_code_version: a version is insert-only, the one with the latest valid_from not after a date is in force at it
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.tax_code (
    tenant_id           uuid not null references platform.tenant (tenant_id),
    company_id          uuid not null,
    tax_code_id         uuid not null default gen_random_uuid(),
    code                core.code not null,
    description         varchar(100) not null,
    tax_type            core.code not null check (tax_type in ('OUTPUT', 'INPUT', 'EXEMPT', 'ZERO_RATED')),   -- IAP once a version exists
    vat_registration_id uuid not null,                                                                      -- IAP once a version exists
    active              boolean not null default true,
    row_version         bigint not null default 1,
    created_at          timestamptz not null default now(),
    created_by          varchar(100) not null,
    updated_at          timestamptz not null default now(),
    updated_by          varchar(100) not null,
    primary key (tenant_id, company_id, tax_code_id),
    unique (tenant_id, company_id, code),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, company_id, vat_registration_id) references core.company_vat_registration (tenant_id, company_id, registration_id)
);

create table if not exists core.tax_code_version (
    tenant_id                 uuid not null,
    company_id                uuid not null,
    tax_code_id               uuid not null,
    valid_from                date not null,
    valid_to                  date,
    tax_code_version_id       uuid not null default gen_random_uuid(),   -- what a tax line stores (02, 7.1)
    rate                      core.rate not null check (rate >= 0 and rate <= 100),
    vat_account_id            uuid not null,
    reverse_charge            boolean not null default false,
    reverse_charge_account_id uuid,
    en16931_vat_category      core.code check (en16931_vat_category in ('S', 'Z', 'E', 'AE', 'K', 'G', 'O', 'L', 'M')),
    exemption_reason_code     core.code,
    exemption_reason_text     varchar(200),
    ec_list_category          core.code check (ec_list_category in ('GOODS', 'SERVICES', 'TRIANGULATION')),
    supply_kind               core.code check (supply_kind in ('GOODS', 'SERVICES')),
    evidence_required_type    core.code check (evidence_required_type in ('CUSTOMS_DECLARATION', 'PROOF_OF_EXPORT', 'TRANSPORT_DOCUMENT', 'PARTNER_VAT_CHECK', 'OTHER')),
    deductible_pct            core.rate not null default 100 check (deductible_pct >= 0 and deductible_pct <= 100),
    deduction_key             core.code,   -- no foreign key until the deduction keys exist (08, 8.6)
    deduction_cap             core.code not null default 'NONE' check (deduction_cap in ('NONE', 'SUPPLIED_AMOUNT')),
    created_at                timestamptz not null default now(),
    created_by                varchar(100) not null,
    primary key (tenant_id, company_id, tax_code_id, valid_from),
    unique (tenant_id, tax_code_version_id),
    foreign key (tenant_id, company_id, tax_code_id) references core.tax_code (tenant_id, company_id, tax_code_id),
    foreign key (tenant_id, company_id, vat_account_id) references core.gl_account (tenant_id, company_id, account_id),
    foreign key (tenant_id, company_id, reverse_charge_account_id) references core.gl_account (tenant_id, company_id, account_id),
    check (valid_to is null or valid_to >= valid_from),
    check (not reverse_charge or reverse_charge_account_id is not null),
    check (reverse_charge or reverse_charge_account_id is null)
);

create table if not exists core.tax_code_version_box (
    tenant_id    uuid not null,
    company_id   uuid not null,
    tax_code_id  uuid not null,
    valid_from   date not null,
    line_side    core.code not null check (line_side in ('PRIMARY', 'COUNTER')),
    return_type  core.code not null check (return_type in ('STANDARD', 'EC_LIST', 'OSS', 'IOSS')),
    box_code     core.code not null,
    amount_kind  core.code not null check (amount_kind in ('BASE', 'TAX')),
    sign         smallint not null check (sign in (1, -1)),
    primary key (tenant_id, company_id, tax_code_id, valid_from, line_side, return_type, box_code),
    foreign key (tenant_id, company_id, tax_code_id, valid_from) references core.tax_code_version (tenant_id, company_id, tax_code_id, valid_from)
);

-- ---------------------------------------------------------------------------------------------------------
-- Security: forced RLS, read for the command roles, write for the configuration role only, never delete
-- ---------------------------------------------------------------------------------------------------------
do $$
declare t text;
begin
    foreach t in array array['tax_class', 'tax_return_box', 'company_vat_registration', 'tax_code', 'tax_code_version', 'tax_code_version_box'] loop
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
    -- versions and their boxes are insert-only (pattern V, 07, 2.1)
    foreach t in array array['tax_code_version', 'tax_code_version_box'] loop
        execute format('drop trigger if exists %I on core.%I', t || '_append_only', t);
        execute format('create trigger %I before update on core.%I for each row execute function core.reject_change()', t || '_append_only', t);
    end loop;
end $$;
grant select on core.tax_class, core.tax_return_box, core.company_vat_registration, core.tax_code, core.tax_code_version, core.tax_code_version_box
      to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_retention;
grant insert on core.tax_class, core.tax_return_box, core.company_vat_registration, core.tax_code, core.tax_code_version, core.tax_code_version_box to cmd_config;
grant update (code, name, active, row_version, updated_at, updated_by) on core.tax_class to cmd_config;
grant update (name, box_group, active, row_version, updated_at, updated_by) on core.tax_return_box to cmd_config;
grant update (country_code, vat_id, scheme, filing_frequency, ec_list_frequency, correction_method, return_currency, vat_rate_type, valid_from, valid_to, active,
              row_version, updated_at, updated_by) on core.company_vat_registration to cmd_config;
grant update (code, description, tax_type, vat_registration_id, active, row_version, updated_at, updated_by) on core.tax_code to cmd_config;

-- ---------------------------------------------------------------------------------------------------------
-- Guards
-- ---------------------------------------------------------------------------------------------------------
-- the country and the scheme of a registration are IAP once a tax code uses it (07, 2.3)
create or replace function core.guard_company_vat_registration() returns trigger language plpgsql as $$
begin
    if (new.country_code <> old.country_code or new.scheme <> old.scheme) and exists (
        select 1 from core.tax_code c where c.tenant_id = old.tenant_id and c.company_id = old.company_id and c.vat_registration_id = old.registration_id) then
        raise exception 'CONFIG.IMMUTABLE_AFTER_POSTING: the country and the scheme of a registration that a tax code uses cannot change' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists company_vat_registration_guard on core.company_vat_registration;
create trigger company_vat_registration_guard before update on core.company_vat_registration
    for each row execute function core.guard_company_vat_registration();

-- the type and the registration of a code are fixed once it has a version: what a version may hold depends on them
create or replace function core.guard_tax_code() returns trigger language plpgsql as $$
begin
    if (new.tax_type <> old.tax_type or new.vat_registration_id <> old.vat_registration_id) and exists (
        select 1 from core.tax_code_version v where v.tenant_id = old.tenant_id and v.company_id = old.company_id and v.tax_code_id = old.tax_code_id) then
        raise exception 'CONFIG.IMMUTABLE_AFTER_POSTING: the type and the registration of a tax code that has a version cannot change' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists tax_code_guard on core.tax_code;
create trigger tax_code_guard before update on core.tax_code
    for each row execute function core.guard_tax_code();

-- what a version holds (07, 8.3, 19, 3.1): the rate of an exempt or zero-rated code is 0, a share of the deduction belongs to input codes, the accounts are
-- balance sheet accounts, a version starts after the latest one. The system marks the accounts as tax accounts (07, 6.1, 08, 6.7): from then on only
-- tax lines post to them. The trigger runs with the rights of the migration role, because no command role may update that column.
create or replace function core.guard_tax_code_version() returns trigger language plpgsql security definer set search_path = pg_catalog, core as $$
declare
    v_type text;
    v_latest date;
    a uuid;
begin
    select tax_type into v_type from core.tax_code where tenant_id = new.tenant_id and company_id = new.company_id and tax_code_id = new.tax_code_id;
    if v_type in ('EXEMPT', 'ZERO_RATED') and new.rate <> 0 then
        raise exception 'SCHEMA.INVALID_VALUE: the rate of an exempt or zero-rated code is 0' using errcode = '23514';
    end if;
    if v_type <> 'INPUT' and (new.deductible_pct <> 100 or new.deduction_key is not null or new.deduction_cap <> 'NONE') then
        raise exception 'CONFIG.DEDUCTION_ON_NON_INPUT_CODE: only an input code has a deductible share, a deduction key or a cap' using errcode = '23514';
    end if;
    select max(valid_from) into v_latest from core.tax_code_version
     where tenant_id = new.tenant_id and company_id = new.company_id and tax_code_id = new.tax_code_id;
    if v_latest is not null and new.valid_from <= v_latest then
        raise exception 'CONFIG.VALID_FROM_NOT_LATER: a version starts after the latest one (%)', v_latest using errcode = '23514';
    end if;
    foreach a in array array[new.vat_account_id, new.reverse_charge_account_id] loop
        continue when a is null;
        if not exists (select 1 from core.gl_account g where g.tenant_id = new.tenant_id and g.company_id = new.company_id and g.account_id = a
                          and g.account_type in ('ASSET', 'LIABILITY', 'EQUITY')) then
            raise exception 'CONFIG.INVALID_REFERENCE: a VAT account is a balance sheet account' using errcode = '23514';
        end if;
        update core.gl_account set tax_account = true
         where tenant_id = new.tenant_id and company_id = new.company_id and account_id = a and not tax_account;
    end loop;
    return new;
end $$;
drop trigger if exists tax_code_version_guard on core.tax_code_version;
create trigger tax_code_version_guard before insert on core.tax_code_version
    for each row execute function core.guard_tax_code_version();

-- a box of a version: the box exists for the country of the registration, and the counter side belongs to a reverse-charge version (08, 6.2)
create or replace function core.guard_tax_code_version_box() returns trigger language plpgsql as $$
declare
    v_country char(2);
    v_type text;
    v_reverse boolean;
begin
    select r.country_code, c.tax_type into v_country, v_type
      from core.tax_code c join core.company_vat_registration r
        on r.tenant_id = c.tenant_id and r.company_id = c.company_id and r.registration_id = c.vat_registration_id
     where c.tenant_id = new.tenant_id and c.company_id = new.company_id and c.tax_code_id = new.tax_code_id;
    select reverse_charge into v_reverse from core.tax_code_version
     where tenant_id = new.tenant_id and company_id = new.company_id and tax_code_id = new.tax_code_id and valid_from = new.valid_from;
    if not exists (select 1 from core.tax_return_box b where b.tenant_id = new.tenant_id and b.country_code = v_country
                      and b.return_type = new.return_type and b.box_code = new.box_code) then
        raise exception 'CONFIG.INVALID_REFERENCE: the box % does not exist for the country of the registration', new.box_code using errcode = '23514';
    end if;
    if new.line_side = 'COUNTER' and not (v_reverse and v_type = 'INPUT') then
        raise exception 'CONFIG.INVALID_REFERENCE: the counter side belongs to a reverse-charge version of an input code' using errcode = '23514';
    end if;
    return new;
end $$;
drop trigger if exists tax_code_version_box_guard on core.tax_code_version_box;
create trigger tax_code_version_box_guard before insert on core.tax_code_version_box
    for each row execute function core.guard_tax_code_version_box();

insert into platform.schema_migration (version, description)
values ('0023', 'VAT configuration: EU membership, tax classes, return boxes, VAT registrations, tax codes with versions and boxes')
on conflict (version) do nothing;
