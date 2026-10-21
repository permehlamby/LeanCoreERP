-- 0002 platform tables: tenant registry and reference data (07, 4 and 5.1). Outside tenant isolation (07, A6).

create table if not exists platform.tenant (
    tenant_id   uuid primary key default gen_random_uuid(),
    tenant_code core.code not null unique,
    name        varchar(100) not null,
    status      core.code not null default 'ACTIVE'
                check (status in ('ACTIVE', 'SUSPENDED', 'EXPORTED', 'DELETION_PENDING', 'DELETED')),
    purpose     core.code not null default 'PRODUCTION'
                check (purpose in ('PRODUCTION', 'REHEARSAL', 'TEST')),
    created_at  timestamptz not null default now()
);

create table if not exists platform.ref_currency (
    currency_code char(3) primary key check (currency_code = upper(currency_code)),
    numeric_code  char(3),
    name          varchar(100) not null,
    minor_units   smallint not null check (minor_units between 0 and 4),
    valid_from    date not null default date '1900-01-01',
    valid_to      date,
    check (valid_to is null or valid_to > valid_from)
);

create table if not exists platform.ref_country (
    country_code char(2) primary key check (country_code = upper(country_code)),
    alpha3       char(3) not null,
    name         varchar(100) not null,
    valid_from   date not null default date '1900-01-01',
    valid_to     date,
    check (valid_to is null or valid_to > valid_from)
);

insert into platform.schema_migration (version, description)
values ('0002', 'platform: tenant, ref_currency, ref_country')
on conflict (version) do nothing;
