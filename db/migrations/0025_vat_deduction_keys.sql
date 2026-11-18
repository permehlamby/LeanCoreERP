-- 0025 Deduction keys and rates (07, 8.6, 19, 3.2): a deduction key is a named variable rate of a VAT registration (pro rata, estimate, sector, area), a rate is
-- a version of it. The core never computes a rate, it is loaded like an exchange rate. A tax code version that names a key needs a key of its registration.
-- The columns of the split of an input tax line exist since 0024 (journal_line_tax, journal_line_tax_base) and are written from this migration on.

-- ---------------------------------------------------------------------------------------------------------
-- vat_deduction_key (M). A surrogate id is added to the key of the spec, so that the commands of a master table address a row by one id.
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.vat_deduction_key (
    tenant_id          uuid not null references platform.tenant (tenant_id),
    company_id         uuid not null,
    registration_id    uuid not null,
    key_code           core.code not null,
    deduction_key_id   uuid not null default gen_random_uuid(),
    kind               core.code not null check (kind in ('PRO_RATA', 'ESTIMATE', 'SECTOR', 'AREA')),   -- IAP once a rate of the key has been used
    description        varchar(200),
    active             boolean not null default true,
    row_version        bigint not null default 1,
    created_at         timestamptz not null default now(),
    created_by         varchar(100) not null,
    updated_at         timestamptz not null default now(),
    updated_by         varchar(100) not null,
    primary key (tenant_id, company_id, registration_id, key_code),
    unique (tenant_id, deduction_key_id),
    foreign key (tenant_id, company_id, registration_id) references core.company_vat_registration (tenant_id, company_id, registration_id)
);

-- ---------------------------------------------------------------------------------------------------------
-- vat_deduction_rate (V, insert only)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.vat_deduction_rate (
    tenant_id        uuid not null,
    company_id       uuid not null,
    registration_id  uuid not null,
    key_code         core.code not null,
    valid_from       date not null,
    valid_to         date,
    rate_pct         core.rate not null check (rate_pct >= 0 and rate_pct <= 100),
    rate_status      core.code not null check (rate_status in ('PROVISIONAL', 'FINAL')),
    basis_note       varchar(200),
    created_at       timestamptz not null default now(),
    created_by       varchar(100) not null,
    primary key (tenant_id, company_id, registration_id, key_code, valid_from),
    foreign key (tenant_id, company_id, registration_id, key_code) references core.vat_deduction_key (tenant_id, company_id, registration_id, key_code),
    check (valid_to is null or valid_to >= valid_from)
);

-- the non-deductible part of a base line is posted as a line of its own: the link is a key (02, 7.2)
do $$ begin
    if not exists (select 1 from pg_constraint where conname = 'journal_line_tax_base_nondeductible_fkey') then
        alter table core.journal_line_tax_base add constraint journal_line_tax_base_nondeductible_fkey
            foreign key (tenant_id, company_id, document_id, nondeductible_line_no) references core.journal_line (tenant_id, company_id, document_id, line_no);
    end if;
end $$;

-- ---------------------------------------------------------------------------------------------------------
-- Security
-- ---------------------------------------------------------------------------------------------------------
do $$
declare t text;
begin
    foreach t in array array['vat_deduction_key', 'vat_deduction_rate'] loop
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
drop trigger if exists vat_deduction_rate_append_only on core.vat_deduction_rate;
create trigger vat_deduction_rate_append_only before update on core.vat_deduction_rate for each row execute function core.reject_change();
grant select on core.vat_deduction_key, core.vat_deduction_rate to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_retention;
grant insert on core.vat_deduction_key, core.vat_deduction_rate to cmd_config;
grant update (kind, description, active, row_version, updated_at, updated_by) on core.vat_deduction_key to cmd_config;

-- ---------------------------------------------------------------------------------------------------------
-- Guards
-- ---------------------------------------------------------------------------------------------------------
-- the kind of a key is fixed once a rate of it exists (07, 8.6)
create or replace function core.guard_vat_deduction_key() returns trigger language plpgsql as $$
begin
    if new.kind <> old.kind and exists (select 1 from core.vat_deduction_rate r where r.tenant_id = old.tenant_id and r.company_id = old.company_id
                                           and r.registration_id = old.registration_id and r.key_code = old.key_code) then
        raise exception 'CONFIG.DEDUCTION_KEY_REFERENCED: the kind of a key that has a rate cannot change' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists vat_deduction_key_guard on core.vat_deduction_key;
create trigger vat_deduction_key_guard before update on core.vat_deduction_key for each row execute function core.guard_vat_deduction_key();

-- a rate version starts after the latest one of its key
create or replace function core.guard_vat_deduction_rate() returns trigger language plpgsql as $$
declare v_latest date;
begin
    select max(valid_from) into v_latest from core.vat_deduction_rate
     where tenant_id = new.tenant_id and company_id = new.company_id and registration_id = new.registration_id and key_code = new.key_code;
    if v_latest is not null and new.valid_from <= v_latest then
        raise exception 'CONFIG.VALID_FROM_NOT_LATER: a rate starts after the latest one (%)', v_latest using errcode = '23514';
    end if;
    return new;
end $$;
drop trigger if exists vat_deduction_rate_guard on core.vat_deduction_rate;
create trigger vat_deduction_rate_guard before insert on core.vat_deduction_rate for each row execute function core.guard_vat_deduction_rate();

-- a version of a tax code that names a key needs the key of the registration of the code (07, 8.3, 8.6). The rest of the guard is that of 0023.
create or replace function core.guard_tax_code_version() returns trigger language plpgsql security definer set search_path = pg_catalog, core as $$
declare
    v_type text;
    v_registration uuid;
    v_latest date;
    a uuid;
begin
    select tax_type, vat_registration_id into v_type, v_registration from core.tax_code where tenant_id = new.tenant_id and company_id = new.company_id and tax_code_id = new.tax_code_id;
    if v_type in ('EXEMPT', 'ZERO_RATED') and new.rate <> 0 then
        raise exception 'SCHEMA.INVALID_VALUE: the rate of an exempt or zero-rated code is 0' using errcode = '23514';
    end if;
    if v_type <> 'INPUT' and (new.deductible_pct <> 100 or new.deduction_key is not null or new.deduction_cap <> 'NONE') then
        raise exception 'CONFIG.DEDUCTION_ON_NON_INPUT_CODE: only an input code has a deductible share, a deduction key or a cap' using errcode = '23514';
    end if;
    if new.deduction_key is not null and not exists (select 1 from core.vat_deduction_key k where k.tenant_id = new.tenant_id and k.company_id = new.company_id
                                                        and k.registration_id = v_registration and k.key_code = new.deduction_key and k.active) then
        raise exception 'CONFIG.DEDUCTION_KEY_UNKNOWN: the registration of the code has no active key %', new.deduction_key using errcode = '23514';
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

insert into platform.schema_migration (version, description)
values ('0025', 'VAT deduction keys and rates')
on conflict (version) do nothing;
