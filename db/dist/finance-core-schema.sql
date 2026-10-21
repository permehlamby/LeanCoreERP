-- Finance core schema, generated from db/migrations (0001_foundation.sql, 0002_platform_reference.sql, 0003_organisation.sql, 0004_calendar_and_numbers.sql, 0005_ledger.sql, 0006_security_and_integrity.sql).
-- Do not edit: change the numbered files and run db/tools/build-all.mjs. Safe to run again.

-- ==================== 0001_foundation.sql ====================
-- 0001 foundation: schemas, domains, roles, helper functions (spec 20, ADR-208, ADR-209)
-- Run order: 0001 to 0006, then optionally db/seed/dev_seed.sql. Every script can be run again.
-- Run as the migration role: `postgres` in Supabase, a superuser or the database owner elsewhere.
-- This role is the "core_owner" of 20, section 3. Nothing here is used at runtime by the application.

create extension if not exists btree_gist;

create schema if not exists platform;   -- tenant registry and reference data, outside tenant isolation (07, A6)
create schema if not exists core;       -- every tenant table, subject to row-level security (P7)

create table if not exists platform.schema_migration (
    version     text primary key,
    description text not null,
    applied_at  timestamptz not null default now()
);

-- Types of the specs (02, types line): AMT = DECIMAL(19,4), RATE = DECIMAL(19,10), BINARY(32), CODE.
do $$ begin
    if not exists (select 1 from pg_type t join pg_namespace n on n.oid = t.typnamespace
                   where n.nspname = 'core' and t.typname = 'amt') then
        create domain core.amt  as numeric(19,4);
    end if;
    if not exists (select 1 from pg_type t join pg_namespace n on n.oid = t.typnamespace
                   where n.nspname = 'core' and t.typname = 'rate') then
        create domain core.rate as numeric(19,10);
    end if;
    if not exists (select 1 from pg_type t join pg_namespace n on n.oid = t.typnamespace
                   where n.nspname = 'core' and t.typname = 'hash32') then
        create domain core.hash32 as bytea check (length(value) = 32);
    end if;
    if not exists (select 1 from pg_type t join pg_namespace n on n.oid = t.typnamespace
                   where n.nspname = 'core' and t.typname = 'code') then
        create domain core.code as varchar(50) check (value <> '');
    end if;
end $$;

-- Roles (20, section 3). The command roles are group roles without login. The login roles of the processes
-- are created here without a password and without login: set both outside this file, see db/README.md.
do $$
declare r text;
begin
    foreach r in array array['cmd_read', 'cmd_config', 'cmd_period', 'cmd_post', 'cmd_retention',
                             'core_app', 'core_job', 'core_retention']
    loop
        if not exists (select 1 from pg_roles where rolname = r) then
            execute format('create role %I nologin', r);
        end if;
    end loop;
end $$;

-- The login roles do not inherit the privileges of the command roles: every transaction names its command
-- role with SET LOCAL ROLE (db/README.md). That makes the per-command grants and policies effective.
alter role core_app noinherit;
alter role core_job noinherit;
alter role core_retention noinherit;

grant cmd_read, cmd_config, cmd_period, cmd_post to core_app;
grant cmd_read to core_job;
grant cmd_read, cmd_retention to core_retention;

-- The tenant of the current transaction. The application sets it with SET LOCAL as the first statement
-- of every transaction, from the validated token (ADR-209). Without a value no row is visible.
create or replace function core.current_tenant_id() returns uuid
language sql stable as $$
    select nullif(current_setting('app.tenant_id', true), '')::uuid
$$;

-- Nothing of ours is reachable through the Supabase data API. These revokes do nothing outside Supabase.
revoke all on schema core, platform from public;
do $$
declare r text;
begin
    foreach r in array array['anon', 'authenticated', 'service_role']
    loop
        if exists (select 1 from pg_roles where rolname = r) then
            execute format('revoke all on schema core, platform from %I', r);
            execute format('alter default privileges in schema core revoke all on tables from %I', r);
        end if;
    end loop;
end $$;

grant usage on schema platform to cmd_read, cmd_config, cmd_period, cmd_post, cmd_retention;
grant usage on schema core to cmd_read, cmd_config, cmd_period, cmd_post, cmd_retention;

insert into platform.schema_migration (version, description)
values ('0001', 'foundation: schemas, domains, roles, tenant function')
on conflict (version) do nothing;

-- ==================== 0002_platform_reference.sql ====================
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

-- ==================== 0003_organisation.sql ====================
-- 0003 organisation, currency and chart of accounts (07, sections 5 to 7). Every table leads with tenant_id (P7).

create table if not exists core.tenant_setting (
    tenant_id                  uuid primary key references platform.tenant(tenant_id),
    rate_fallback_days         smallint not null default 7 check (rate_fallback_days >= 0),
    command_log_retention_days int not null default 90 check (command_log_retention_days > 0),
    max_batch_items            int not null default 500 check (max_batch_items > 0),
    lock_timeout_ms            int not null default 5000 check (lock_timeout_ms > 0),
    pivot_currency             char(3) references platform.ref_currency(currency_code),
    read_log_retention_days    int,
    anchor_pending_deadline    smallint not null default 24 check (anchor_pending_deadline > 0)
);

create table if not exists core.tenant_currency (
    tenant_id     uuid not null references platform.tenant(tenant_id),
    currency_code char(3) not null references platform.ref_currency(currency_code),
    active        boolean not null default true,
    primary key (tenant_id, currency_code)
);

create table if not exists core.fiscal_calendar (
    tenant_id            uuid not null references platform.tenant(tenant_id),
    calendar_id          uuid not null default gen_random_uuid(),
    calendar_code        core.code not null,
    description          varchar(100),
    first_month          smallint not null check (first_month between 1 and 12),
    special_period_count smallint not null default 0 check (special_period_count between 0 and 4),
    year_label_rule      core.code not null check (year_label_rule in ('START', 'END')),
    primary key (tenant_id, calendar_id),
    unique (tenant_id, calendar_code)
);

create table if not exists core.company_group (
    tenant_id            uuid not null references platform.tenant(tenant_id),
    group_id             uuid not null default gen_random_uuid(),
    group_code           core.code not null,
    name                 varchar(100) not null,
    group_currency       char(3) not null references platform.ref_currency(currency_code),
    inventory_cost_scope core.code not null default 'COMPANY' check (inventory_cost_scope in ('COMPANY', 'WAREHOUSE')),
    active               boolean not null default true,
    primary key (tenant_id, group_id),
    unique (tenant_id, group_code)
);

create table if not exists core.company (
    tenant_id                 uuid not null references platform.tenant(tenant_id),
    company_id                uuid not null default gen_random_uuid(),
    company_code              core.code not null,
    legal_name                varchar(200) not null,
    legal_registration_id     varchar(50),
    legal_registration_scheme core.code,
    electronic_address        varchar(100),
    electronic_address_scheme core.code,
    country_code              char(2) not null references platform.ref_country(country_code),
    local_currency            char(3) not null references platform.ref_currency(currency_code),
    reporting_currency        char(3) references platform.ref_currency(currency_code),
    fiscal_calendar_id        uuid not null,
    status                    core.code not null default 'DRAFT' check (status in ('DRAFT', 'ACTIVE', 'INACTIVE')),
    package_code              core.code,
    package_version           varchar(20),
    row_version               bigint not null default 1,
    primary key (tenant_id, company_id),
    unique (tenant_id, company_code),
    foreign key (tenant_id, fiscal_calendar_id) references core.fiscal_calendar (tenant_id, calendar_id)
);

-- Finding F-IMPL-1: the specs use ledger_id everywhere (02, 3, 04, 5.2, 06, 5.2: "the company's single ledger
-- in R1") but no spec defines a ledger table. This minimal table is a proposal and needs a delta to 07.
create table if not exists core.ledger (
    tenant_id   uuid not null,
    company_id  uuid not null,
    ledger_id   uuid not null default gen_random_uuid(),
    ledger_code core.code not null,
    is_default  boolean not null default true,
    active      boolean not null default true,
    primary key (tenant_id, company_id, ledger_id),
    unique (tenant_id, company_id, ledger_code),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id)
);
create unique index if not exists ledger_one_default_per_company
    on core.ledger (tenant_id, company_id) where is_default;

create table if not exists core.group_membership (
    tenant_id  uuid not null,
    company_id uuid not null,
    valid_from date not null,
    group_id   uuid not null,
    valid_to   date,
    primary key (tenant_id, company_id, valid_from),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, group_id) references core.company_group (tenant_id, group_id),
    check (valid_to is null or valid_to > valid_from),
    -- A company belongs to one group at a time (ADR-014): no overlap per company.
    exclude using gist (tenant_id with =, company_id with =, daterange(valid_from, valid_to, '[)') with &&)
);

create table if not exists core.company_setting_version (
    tenant_id                   uuid not null,
    company_id                  uuid not null,
    effective_from              timestamptz not null,
    tax_rounding_mode           core.code not null default 'PER_DOCUMENT' check (tax_rounding_mode in ('PER_DOCUMENT', 'PER_LINE')),
    tax_amount_tolerance        core.amt not null default 0 check (tax_amount_tolerance >= 0),
    rate_override_tolerance_pct core.rate not null default 0 check (rate_override_tolerance_pct >= 0),
    soft_close_requires_reason  boolean not null default false,
    einvoice_rule_set           core.code,
    primary key (tenant_id, company_id, effective_from),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id)
);

create table if not exists core.exchange_rate (
    tenant_id     uuid not null references platform.tenant(tenant_id),
    rate_id       uuid not null default gen_random_uuid(),
    rate_type     core.code not null check (rate_type in ('SPOT', 'CLOSING', 'VAT', 'AVERAGE', 'HISTORICAL')),
    from_currency char(3) not null references platform.ref_currency(currency_code),
    to_currency   char(3) not null references platform.ref_currency(currency_code),
    valid_from    date not null,
    rate          core.rate not null check (rate > 0),
    source        varchar(100),
    first_used_at timestamptz,
    primary key (tenant_id, rate_id),
    unique (tenant_id, rate_type, from_currency, to_currency, valid_from),
    check (from_currency <> to_currency)
);

create table if not exists core.gl_account (
    tenant_id             uuid not null,
    company_id            uuid not null,
    account_id            uuid not null default gen_random_uuid(),
    account_no            core.code not null,
    name                  varchar(100) not null,
    account_type          core.code not null check (account_type in ('ASSET', 'LIABILITY', 'EQUITY', 'REVENUE', 'EXPENSE')),
    account_group         core.code,
    standard_account_code core.code,
    open_item_mode        core.code not null default 'NONE' check (open_item_mode in ('NONE', 'CONTROL', 'GL_OPEN_ITEM')),
    currency_code         char(3) references platform.ref_currency(currency_code),
    revalue_flag          boolean not null default false,
    tax_code_rule         core.code not null default 'OPTIONAL' check (tax_code_rule in ('MANDATORY', 'OPTIONAL', 'FORBIDDEN')),
    default_tax_class_id  uuid,
    posting_blocked       boolean not null default false,
    tax_account           boolean not null default false,   -- S column, set by the system (07, 6.1)
    valid_from            date not null default date '1900-01-01',
    valid_to              date,
    active                boolean not null default true,
    row_version           bigint not null default 1,
    primary key (tenant_id, company_id, account_id),
    unique (tenant_id, company_id, account_no),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    check (valid_to is null or valid_to >= valid_from),
    -- Balance sheet accounts only may be revalued (05).
    check (not revalue_flag or account_type in ('ASSET', 'LIABILITY', 'EQUITY'))
);

insert into platform.schema_migration (version, description)
values ('0003', 'organisation: settings, currency, calendar, group, company, ledger, rates, accounts')
on conflict (version) do nothing;

-- ==================== 0004_calendar_and_numbers.sql ====================
-- 0004 fiscal years and periods, period event log, posting window, number series and counters (04)

create table if not exists core.period_event (
    tenant_id       uuid not null,
    event_id        uuid not null default gen_random_uuid(),
    company_id      uuid not null,
    event_seq       bigint not null check (event_seq >= 1),
    fiscal_year     int not null,
    period_no       int,
    event_type      core.code not null check (event_type in
                    ('YEAR_OPEN', 'PERIOD_SOFT_CLOSE', 'PERIOD_HARD_CLOSE', 'PERIOD_REOPEN', 'YEAR_END_CLOSE', 'YEAR_END_REOPEN')),
    from_status     core.code,
    to_status       core.code not null,
    reason_code     core.code,
    reason_text     varchar(500),
    authorisation   core.code,
    document_id     uuid,
    event_at        timestamptz not null,
    event_by        varchar(100) not null,
    correlation_id  varchar(100) not null,
    idempotency_key varchar(100) not null,
    prev_hash       core.hash32 not null,
    row_hash        core.hash32 not null,
    hash_version    smallint not null check (hash_version >= 1),
    primary key (tenant_id, event_id),
    unique (tenant_id, company_id, event_seq),
    unique (tenant_id, idempotency_key),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    -- The reopen events need a reason (ADR-023).
    check (event_type not in ('PERIOD_REOPEN', 'YEAR_END_REOPEN') or (reason_code is not null and reason_text is not null))
);
create index if not exists period_event_period on core.period_event (tenant_id, company_id, fiscal_year, period_no, event_seq);

create table if not exists core.fiscal_year (
    tenant_id            uuid not null,
    company_id           uuid not null,
    fiscal_year          int not null,
    calendar_id          uuid not null,
    start_date           date not null,
    end_date             date not null,
    first_period_no      int not null default 1 check (first_period_no between 1 and 12),
    special_period_count smallint not null default 0 check (special_period_count between 0 and 4),
    is_history           boolean not null default false,
    history_until_period int,
    status               core.code not null default 'OPEN' check (status in ('OPEN', 'CLOSED')),   -- S
    year_end_document_id uuid,                                                                     -- S, FK added in 0005
    opened_at            timestamptz not null default now(),
    opened_by            varchar(100) not null,
    last_event_id        uuid,                                                                     -- S
    primary key (tenant_id, company_id, fiscal_year),
    unique (tenant_id, company_id, start_date),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, calendar_id) references core.fiscal_calendar (tenant_id, calendar_id),
    foreign key (tenant_id, last_event_id) references core.period_event (tenant_id, event_id),
    check (end_date > start_date)
);

create table if not exists core.fiscal_period (
    tenant_id     uuid not null,
    company_id    uuid not null,
    fiscal_year   int not null,
    period_no     int not null check (period_no >= 1),
    period_type   core.code not null check (period_type in ('REGULAR', 'SPECIAL')),
    start_date    date not null,
    end_date      date not null,
    status        core.code not null default 'OPEN' check (status in ('OPEN', 'SOFT_CLOSED', 'HARD_CLOSED')),   -- S
    last_event_id uuid,                                                                                        -- S
    primary key (tenant_id, company_id, fiscal_year, period_no),
    foreign key (tenant_id, company_id, fiscal_year) references core.fiscal_year (tenant_id, company_id, fiscal_year),
    foreign key (tenant_id, last_event_id) references core.period_event (tenant_id, event_id),
    check (end_date >= start_date)
);
create unique index if not exists fiscal_period_regular_start
    on core.fiscal_period (tenant_id, company_id, start_date) where period_type = 'REGULAR';
-- The lookup path from a posting date (04, 3.3).
create index if not exists fiscal_period_dates on core.fiscal_period (tenant_id, company_id, start_date, end_date);

create table if not exists core.posting_window (
    tenant_id             uuid not null,
    company_id            uuid not null,
    window_id             uuid not null default gen_random_uuid(),
    effective_from        timestamptz not null,
    earliest_posting_date date not null,
    latest_posting_date   date not null,
    reason_code           core.code,
    created_by            varchar(100) not null,
    created_at            timestamptz not null default now(),
    primary key (tenant_id, company_id, window_id),
    unique (tenant_id, company_id, effective_from),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    check (latest_posting_date >= earliest_posting_date)
);

create table if not exists core.number_series (
    tenant_id           uuid not null,
    company_id          uuid not null,
    series_id           uuid not null default gen_random_uuid(),
    series_code         core.code not null,
    description         varchar(100),
    prefix              varchar(10),
    include_fiscal_year boolean not null default true,
    separator           varchar(1),
    number_length       smallint not null check (number_length between 1 and 20),
    start_number        bigint not null default 1 check (start_number >= 1),
    is_legal            boolean not null default true,
    primary key (tenant_id, company_id, series_id),
    unique (tenant_id, company_id, series_code),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    -- A legal series must carry the year, else the legal number repeats across years (04, 5.1).
    check (not is_legal or include_fiscal_year)
);

create table if not exists core.number_series_assignment (
    tenant_id       uuid not null,
    company_id      uuid not null,
    assignment_id   uuid not null default gen_random_uuid(),
    document_type   core.code not null,
    ledger_id       uuid,
    ledger_key      uuid generated always as (coalesce(ledger_id, '00000000-0000-0000-0000-000000000000'::uuid)) stored,
    series_id       uuid not null,
    effective_from  date not null,
    created_by      varchar(100) not null,
    created_at      timestamptz not null default now(),
    primary key (tenant_id, company_id, assignment_id),
    unique (tenant_id, company_id, document_type, ledger_key, effective_from),
    foreign key (tenant_id, company_id, series_id) references core.number_series (tenant_id, company_id, series_id),
    foreign key (tenant_id, company_id, ledger_id) references core.ledger (tenant_id, company_id, ledger_id)
);

-- One row per series and fiscal year: the serialisation point of gapless numbering (ADR-007, 04, 6).
create table if not exists core.number_range_counter (
    tenant_id   uuid not null,
    company_id  uuid not null,
    series_id   uuid not null,
    fiscal_year int not null,
    last_number bigint not null,   -- S. Starts at start_number - 1
    updated_at  timestamptz not null default now(),
    primary key (tenant_id, company_id, series_id, fiscal_year),
    foreign key (tenant_id, company_id, series_id) references core.number_series (tenant_id, company_id, series_id),
    foreign key (tenant_id, company_id, fiscal_year) references core.fiscal_year (tenant_id, company_id, fiscal_year),
    check (last_number >= 0)
);

insert into platform.schema_migration (version, description)
values ('0004', 'calendar: fiscal year and period, period_event, posting window, number series and counters')
on conflict (version) do nothing;

-- ==================== 0005_ledger.sql ====================
-- 0005 ledger: chain heads, journal documents and lines, command log, outbox (02, 06, 15)

create table if not exists core.chain_head (
    tenant_id    uuid not null,
    company_id   uuid not null,
    chain_type   core.code not null check (chain_type in
                 ('LEDGER', 'CLEARING', 'PERIOD_EVENT', 'VAT_RETURN', 'STOCK', 'FULFILMENT')),
    scope_key    varchar(50) not null default '',   -- ledger_id for LEDGER, empty otherwise (02, 9.2)
    last_seq     bigint not null check (last_seq >= 0),
    last_hash    core.hash32 not null,
    hash_version smallint not null check (hash_version >= 1),
    updated_at   timestamptz not null default now(),
    primary key (tenant_id, company_id, chain_type, scope_key),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id)
);

create table if not exists core.journal_document (
    tenant_id                 uuid not null,
    company_id                uuid not null,
    document_id               uuid not null default gen_random_uuid(),
    ledger_id                 uuid not null,
    document_type             core.code not null check (document_type in (
        'JOURNAL', 'AP_INVOICE', 'AP_CREDIT', 'AR_INVOICE', 'AR_CREDIT', 'PAYMENT', 'REVALUATION', 'REVAL_REVERSAL',
        'YEAR_END', 'OPENING', 'VAT_SETTLE', 'REVERSAL', 'CLEARING', 'CLEARING_RESET', 'STOCK_RECEIPT', 'STOCK_ISSUE',
        'STOCK_ADJUSTMENT', 'STOCK_TRANSFER', 'STOCK_STATUS_CHANGE', 'STOCK_COST_ADJUSTMENT', 'STOCK_RETURN',
        'STOCK_OPENING', 'STOCK_REVERSAL', 'DROPSHIP_COST', 'GR_IR_WRITE_OFF', 'HISTORY_SUMMARY', 'OPENING_ITEMS',
        'CLOSING')),
    is_system_generated       boolean not null,
    number_series_id          uuid not null,
    fiscal_year               int not null,
    sequence_no               bigint not null check (sequence_no >= 1),
    document_number           core.code not null,
    fiscal_period             int not null,
    soft_close_override       boolean not null default false,
    migration_batch_id        uuid,
    detail_digest             core.hash32,
    document_date             date not null,
    posting_date              date not null,
    posted_at                 timestamptz not null,
    transaction_currency      char(3) not null references platform.ref_currency(currency_code),
    status                    core.code not null default 'posted' check (status in ('posted', 'reversed')),   -- S
    reversal_of_document_id   uuid,
    reversed_by_document_id   uuid,                                                                         -- S
    reversal_reason_code      core.code check (reversal_reason_code in
                              ('AUTO_REVERSAL', 'RERUN', 'REOPEN', 'CORRECTION', 'ERROR', 'RETURN')),
    auto_reversal_date        date,
    ic_transaction_id         uuid,
    ic_counterpart_company_id uuid,
    origin                    core.code not null check (origin in
                              ('EXTERNAL', 'ORDER', 'STOCK', 'VAT', 'MIGRATION', 'SYSTEM')),
    source_system             core.code not null,
    source_document_ref       varchar(100),
    idempotency_key           varchar(100) not null,
    command_name              core.code not null,
    posted_by                 varchar(100) not null,
    correlation_id            varchar(100) not null,
    header_text               varchar(200),     -- L2
    external_reference        varchar(100),     -- L2
    chain_seq                 bigint not null check (chain_seq >= 1),
    prev_hash                 core.hash32 not null,
    row_hash                  core.hash32 not null,
    hash_version              smallint not null check (hash_version in (1, 2)),
    primary key (tenant_id, company_id, document_id),
    unique (tenant_id, company_id, number_series_id, fiscal_year, sequence_no),
    unique (tenant_id, company_id, number_series_id, fiscal_year, document_number),
    unique (tenant_id, idempotency_key),
    unique (tenant_id, company_id, ledger_id, chain_seq),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, company_id, ledger_id) references core.ledger (tenant_id, company_id, ledger_id),
    foreign key (tenant_id, company_id, number_series_id) references core.number_series (tenant_id, company_id, series_id),
    foreign key (tenant_id, company_id, fiscal_year, fiscal_period)
        references core.fiscal_period (tenant_id, company_id, fiscal_year, period_no),
    foreign key (tenant_id, company_id, reversal_of_document_id)
        references core.journal_document (tenant_id, company_id, document_id),
    foreign key (tenant_id, company_id, reversed_by_document_id)
        references core.journal_document (tenant_id, company_id, document_id),
    -- A mirror document has a reversal reason, and only a mirror has one (02, 3, D1).
    check ((reversal_of_document_id is null) = (reversal_reason_code is null))
);
create index if not exists jd_posting on core.journal_document (tenant_id, company_id, posting_date, chain_seq);
create index if not exists jd_type_posting on core.journal_document (tenant_id, company_id, document_type, posting_date);
create index if not exists jd_source on core.journal_document (tenant_id, company_id, source_system, source_document_ref);
create index if not exists jd_external on core.journal_document (tenant_id, company_id, external_reference);
create index if not exists jd_correlation on core.journal_document (tenant_id, correlation_id);

do $$ begin
    if not exists (select 1 from pg_constraint where conname = 'fiscal_year_year_end_document_fk') then
        alter table core.fiscal_year add constraint fiscal_year_year_end_document_fk
            foreign key (tenant_id, company_id, year_end_document_id)
            references core.journal_document (tenant_id, company_id, document_id);
    end if;
end $$;

create table if not exists core.journal_line (
    tenant_id                 uuid not null,
    company_id                uuid not null,
    document_id               uuid not null,
    line_no                   int not null check (line_no >= 1),
    ledger_id                 uuid not null,
    line_type                 core.code not null check (line_type in (
        'BASE', 'TAX', 'TAX_NONDEDUCTIBLE', 'ROUNDING', 'FX_REALISED', 'FX_UNREALISED', 'REVAL_ADJUSTMENT',
        'TAX_ROUNDING', 'CLOSING', 'OPENING', 'CLEARING_DIFF', 'HISTORY_PL')),
    account_id                uuid not null,
    posting_date              date not null,
    fiscal_year               int not null,
    fiscal_period             int not null,
    line_text                 varchar(200),     -- L2
    reversed_line_no          int,
    tax_code_id               uuid,
    amount_tc                 core.amt not null,
    amount_lc                 core.amt not null,
    currency_lc               char(3) not null references platform.ref_currency(currency_code),
    rate_lc                   core.rate not null check (rate_lc > 0),
    rate_lc_table             core.rate check (rate_lc_table > 0),
    amount_gc                 core.amt not null,
    currency_gc               char(3) not null references platform.ref_currency(currency_code),
    rate_gc                   core.rate not null check (rate_gc > 0),
    rate_gc_table             core.rate check (rate_gc_table > 0),
    amount_rc                 core.amt,
    currency_rc               char(3) references platform.ref_currency(currency_code),
    rate_rc                   core.rate check (rate_rc > 0),
    rate_rc_table             core.rate check (rate_rc_table > 0),
    rate_type                 core.code not null check (rate_type in ('SPOT', 'CLOSING', 'VAT', 'DERIVED')),
    rate_date                 date not null,
    ic_counterpart_company_id uuid,
    cost_center_id            uuid,             -- L2 typed dimensions (02, 5.3), no FK until the dimension tables exist
    project_id                uuid,
    profit_center_id          uuid,
    business_unit_id          uuid,
    primary key (tenant_id, company_id, document_id, line_no),
    foreign key (tenant_id, company_id, document_id)
        references core.journal_document (tenant_id, company_id, document_id),
    foreign key (tenant_id, company_id, account_id) references core.gl_account (tenant_id, company_id, account_id),
    -- The reporting currency amount, currency and rate are all present or all absent (02, 5.2).
    check ((amount_rc is null) = (currency_rc is null) and (currency_rc is null) = (rate_rc is null))
);
create index if not exists jl_account_date on core.journal_line (tenant_id, company_id, account_id, posting_date);
create index if not exists jl_period_account on core.journal_line (tenant_id, company_id, fiscal_year, fiscal_period, account_id);
create index if not exists jl_date_account on core.journal_line (tenant_id, company_id, posting_date, account_id);

create table if not exists core.command_log (
    tenant_id           uuid not null,
    command_log_id      uuid not null default gen_random_uuid(),
    company_id          uuid,
    idempotency_key     varchar(100) not null,
    command_name        core.code not null,
    request_fingerprint core.hash32 not null,
    actor               varchar(100) not null,
    correlation_id      varchar(100) not null,
    executed_at         timestamptz not null default now(),
    primary key (tenant_id, command_log_id),
    unique (tenant_id, idempotency_key),
    foreign key (tenant_id) references platform.tenant (tenant_id)
);
create index if not exists command_log_correlation on core.command_log (tenant_id, correlation_id);

create table if not exists core.command_log_result (
    tenant_id      uuid not null,
    command_log_id uuid not null,
    result_seq     int not null check (result_seq >= 1),
    entity_type    core.code not null,
    entity_id      uuid not null,
    primary key (tenant_id, command_log_id, result_seq),
    foreign key (tenant_id, command_log_id) references core.command_log (tenant_id, command_log_id)
);

-- Outbox (15, 3.7, 3.8): positions are gapless as seen by a reader, taken under the outbox_head lock, last.
create table if not exists core.outbox_head (
    tenant_id     uuid primary key references platform.tenant (tenant_id),
    last_position bigint not null default 0 check (last_position >= 0)
);

create table if not exists core.outbox_event (
    tenant_id      uuid not null references platform.tenant (tenant_id),
    position       bigint not null check (position >= 1),
    event_type     core.code not null,
    event_version  smallint not null default 1 check (event_version >= 1),
    company_id     uuid,
    entity_type    core.code not null,
    entity_key     text not null,
    entity_version bigint,
    change         core.code not null,
    occurred_at    timestamptz not null,
    actor_ref      text not null,
    app_id         uuid,
    correlation_id varchar(100) not null,
    is_audit       boolean not null default false,
    chain_ref      jsonb,
    summary        jsonb,
    primary key (tenant_id, position)
);
create index if not exists outbox_audit_time on core.outbox_event (tenant_id, is_audit, occurred_at);
create index if not exists outbox_correlation on core.outbox_event (tenant_id, correlation_id);
create index if not exists outbox_entity on core.outbox_event (tenant_id, entity_type, entity_key, position);

insert into platform.schema_migration (version, description)
values ('0005', 'ledger: chain_head, journal_document, journal_line, command_log, outbox')
on conflict (version) do nothing;

-- ==================== 0006_security_and_integrity.sql ====================
-- 0006 row-level security, grants, protection and integrity triggers (spec 20, section 3, ADR-209; 02, 10; 04, 8)
--
-- Model: the application connects as a login role (core_app, core_job, core_retention, NOINHERIT) and starts
-- every transaction with
--     SET LOCAL ROLE cmd_post;                     -- the command role of the command, never more
--     SET LOCAL app.tenant_id = '<tenant uuid>';   -- from the validated token (ADR-209)
-- Three layers protect the ledger: grants, these triggers, and the hash chain with its anchors (02, 9).
-- A privileged user who disables a trigger is still caught by `chains:verify`.

-- ---------------------------------------------------------------------------------------------------------
-- 1. Row-level security on every tenant table, forced also for the table owner
-- ---------------------------------------------------------------------------------------------------------
do $$
declare t text;
begin
    for t in select tablename from pg_tables where schemaname = 'core' order by tablename loop
        execute format('alter table core.%I enable row level security', t);
        execute format('alter table core.%I force row level security', t);
        if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = t and policyname = 'tenant_isolation') then
            execute format('create policy tenant_isolation on core.%I using (tenant_id = core.current_tenant_id()) '
                           'with check (tenant_id = core.current_tenant_id())', t);
        end if;
    end loop;
end $$;

-- A command role may move only its own chain (02, 9.2: the owning command role updates the head).
drop policy if exists chain_head_period on core.chain_head;
create policy chain_head_period on core.chain_head as restrictive for update to cmd_period
    using (chain_type = 'PERIOD_EVENT') with check (chain_type = 'PERIOD_EVENT');
drop policy if exists chain_head_post on core.chain_head;
create policy chain_head_post on core.chain_head as restrictive for update to cmd_post
    using (chain_type = 'LEDGER') with check (chain_type = 'LEDGER');

-- ---------------------------------------------------------------------------------------------------------
-- 2. Grants. No role has DELETE or TRUNCATE on the transaction layer (ADR-001). Only cmd_retention may purge
--    app events of the outbox (15, 5.4).
-- ---------------------------------------------------------------------------------------------------------
revoke all on all tables in schema core from public;
grant select on all tables in schema core to cmd_read, cmd_config, cmd_period, cmd_post;
grant select on all tables in schema platform to cmd_read, cmd_config, cmd_period, cmd_post, cmd_retention;
grant select, delete on core.outbox_event to cmd_retention;

-- Configuration role (write.config, write.master, period.admin parts): create and change configuration.
grant insert on core.tenant_setting, core.tenant_currency, core.fiscal_calendar, core.company_group, core.company,
      core.ledger, core.group_membership, core.company_setting_version, core.exchange_rate, core.gl_account,
      core.number_series, core.number_series_assignment, core.posting_window,
      core.chain_head, core.outbox_head, core.command_log, core.command_log_result, core.outbox_event
      to cmd_config;
grant update on core.tenant_setting, core.tenant_currency, core.fiscal_calendar, core.company_group, core.company,
      core.ledger, core.gl_account, core.number_series to cmd_config;
grant update (valid_to) on core.group_membership to cmd_config;
grant update (rate, source, first_used_at) on core.exchange_rate to cmd_config;

-- Period role (period.admin, period.hard_close, year_end.*): open years, change period status, own chain.
grant insert on core.fiscal_year, core.fiscal_period, core.period_event, core.number_range_counter,
      core.chain_head, core.command_log, core.command_log_result, core.outbox_event to cmd_period;
grant update (status, year_end_document_id, last_event_id) on core.fiscal_year to cmd_period;
grant update (status, last_event_id) on core.fiscal_period to cmd_period;
grant update (last_seq, last_hash, hash_version, updated_at) on core.chain_head to cmd_period;
grant update (last_position) on core.outbox_head to cmd_period;
grant update (first_used_at) on core.exchange_rate to cmd_period;

-- Posting role (journal.post): documents and lines, counter, ledger chain head, outbox.
grant insert on core.journal_document, core.journal_line, core.command_log, core.command_log_result,
      core.outbox_event, core.chain_head to cmd_post;
grant update (status, reversed_by_document_id) on core.journal_document to cmd_post;
grant update (last_number, updated_at) on core.number_range_counter to cmd_post;
grant update (last_seq, last_hash, hash_version, updated_at) on core.chain_head to cmd_post;
grant update (last_position) on core.outbox_head to cmd_post;
grant update (first_used_at) on core.exchange_rate to cmd_post;

-- ---------------------------------------------------------------------------------------------------------
-- 3. Append-only and no-delete triggers
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.reject_change() returns trigger language plpgsql as $$
begin
    if tg_op = 'DELETE' and tg_table_name = 'outbox_event' and pg_has_role(current_user, 'cmd_retention', 'member') then
        return old;
    end if;
    raise exception 'core.% is append-only: % is not allowed', tg_table_name, tg_op using errcode = '23000';
end $$;

create or replace function core.reject_delete() returns trigger language plpgsql as $$
begin
    raise exception 'core.% has no hard delete (ADR-073): deactivate instead', tg_table_name using errcode = '23000';
end $$;

do $$
declare t text;
begin
    foreach t in array array['period_event', 'command_log', 'command_log_result', 'outbox_event'] loop
        execute format('drop trigger if exists %I on core.%I', t || '_append_only', t);
        execute format('create trigger %I before update or delete on core.%I for each row execute function core.reject_change()',
                       t || '_append_only', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
        execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_change()',
                       t || '_no_truncate', t);
    end loop;
    foreach t in array array['journal_document', 'journal_line', 'chain_head', 'number_range_counter', 'fiscal_year',
                             'fiscal_period', 'company', 'gl_account', 'exchange_rate', 'number_series',
                             'number_series_assignment', 'posting_window', 'company_setting_version', 'ledger',
                             'group_membership', 'outbox_head'] loop
        execute format('drop trigger if exists %I on core.%I', t || '_no_delete', t);
        execute format('create trigger %I before delete on core.%I for each row execute function core.reject_delete()',
                       t || '_no_delete', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
        execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_delete()',
                       t || '_no_truncate', t);
    end loop;
end $$;

-- ---------------------------------------------------------------------------------------------------------
-- 4. Lock-class guards (02, 10, D2): L1 never changes, S columns only as allowed
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.guard_journal_document() returns trigger language plpgsql as $$
begin
    -- Only the S columns status and reversed_by_document_id may change. The L2 columns header_text and
    -- external_reference stay locked until the change command exists (it needs a change_log row, 02, 8).
    if (to_jsonb(new) - array['status', 'reversed_by_document_id'])
       is distinct from (to_jsonb(old) - array['status', 'reversed_by_document_id']) then
        raise exception 'journal_document: an L1 or L2 column cannot change' using errcode = '23000';
    end if;
    if new.status is distinct from old.status and not (old.status = 'posted' and new.status = 'reversed') then
        raise exception 'journal_document: status can only change from posted to reversed' using errcode = '23000';
    end if;
    if old.reversed_by_document_id is not null and new.reversed_by_document_id is distinct from old.reversed_by_document_id then
        raise exception 'journal_document: reversed_by_document_id is never overwritten (B1)' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists journal_document_guard on core.journal_document;
create trigger journal_document_guard before update on core.journal_document
    for each row execute function core.guard_journal_document();

create or replace function core.guard_journal_line() returns trigger language plpgsql as $$
begin
    -- No column of a line changes yet. The L2 columns (text, dimensions) open with the change command.
    raise exception 'journal_line: lines are never updated' using errcode = '23000';
end $$;
drop trigger if exists journal_line_guard on core.journal_line;
create trigger journal_line_guard before update on core.journal_line
    for each row execute function core.guard_journal_line();

-- Gapless numbering (ADR-007): a counter moves by exactly one, and only forward.
create or replace function core.guard_number_counter() returns trigger language plpgsql as $$
begin
    if (to_jsonb(new) - array['last_number', 'updated_at']) is distinct from (to_jsonb(old) - array['last_number', 'updated_at']) then
        raise exception 'number_range_counter: key columns cannot change' using errcode = '23000';
    end if;
    if new.last_number <> old.last_number + 1 then
        raise exception 'number_range_counter: last_number must increase by exactly 1 (% to %)', old.last_number, new.last_number
            using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists number_counter_guard on core.number_range_counter;
create trigger number_counter_guard before update on core.number_range_counter
    for each row execute function core.guard_number_counter();

-- Chain heads: created at sequence 0, then moved by exactly one (02, 9.2).
create or replace function core.guard_chain_head() returns trigger language plpgsql as $$
begin
    if tg_op = 'INSERT' then
        if new.last_seq <> 0 then
            raise exception 'chain_head: a new head starts at last_seq 0 with the genesis hash' using errcode = '23000';
        end if;
        return new;
    end if;
    if (to_jsonb(new) - array['last_seq', 'last_hash', 'hash_version', 'updated_at'])
       is distinct from (to_jsonb(old) - array['last_seq', 'last_hash', 'hash_version', 'updated_at']) then
        raise exception 'chain_head: key columns cannot change' using errcode = '23000';
    end if;
    if new.last_seq <> old.last_seq + 1 then
        raise exception 'chain_head: last_seq must increase by exactly 1 (% to %)', old.last_seq, new.last_seq using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists chain_head_guard on core.chain_head;
create trigger chain_head_guard before insert or update on core.chain_head
    for each row execute function core.guard_chain_head();

create or replace function core.guard_outbox_head() returns trigger language plpgsql as $$
begin
    if new.last_position <= old.last_position then
        raise exception 'outbox_head: last_position only moves forward' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists outbox_head_guard on core.outbox_head;
create trigger outbox_head_guard before update on core.outbox_head
    for each row execute function core.guard_outbox_head();

-- Period status is an S column: it changes only together with the matching, latest period_event (04, 8, ADR-041).
create or replace function core.guard_period_status() returns trigger language plpgsql as $$
declare ev core.period_event%rowtype; latest bigint;
begin
    if tg_table_name = 'fiscal_period' then
        if (to_jsonb(new) - array['status', 'last_event_id']) is distinct from (to_jsonb(old) - array['status', 'last_event_id']) then
            raise exception 'fiscal_period: an L1 column cannot change' using errcode = '23000';
        end if;
    else
        if (to_jsonb(new) - array['status', 'last_event_id', 'year_end_document_id'])
           is distinct from (to_jsonb(old) - array['status', 'last_event_id', 'year_end_document_id']) then
            raise exception 'fiscal_year: an L1 column cannot change' using errcode = '23000';
        end if;
    end if;
    if new.status is distinct from old.status or new.last_event_id is distinct from old.last_event_id then
        select * into ev from core.period_event where tenant_id = new.tenant_id and event_id = new.last_event_id;
        if not found then
            raise exception '%: status change without a period_event', tg_table_name using errcode = '23000';
        end if;
        select max(event_seq) into latest from core.period_event where tenant_id = new.tenant_id and company_id = new.company_id;
        if ev.company_id <> new.company_id or ev.fiscal_year <> new.fiscal_year or ev.to_status <> new.status
           or ev.event_seq <> latest
           or (tg_table_name = 'fiscal_period' and ev.period_no is distinct from new.period_no) then
            raise exception '%: the period_event does not match the status change', tg_table_name using errcode = '23000';
        end if;
    end if;
    return new;
end $$;
drop trigger if exists fiscal_period_guard on core.fiscal_period;
create trigger fiscal_period_guard before update on core.fiscal_period
    for each row execute function core.guard_period_status();
drop trigger if exists fiscal_year_guard on core.fiscal_year;
create trigger fiscal_year_guard before update on core.fiscal_year
    for each row execute function core.guard_period_status();

-- Exchange rates are usage-locked (ADR-019, 07, 7.2): a used rate never changes, the identity never changes.
create or replace function core.guard_exchange_rate() returns trigger language plpgsql as $$
begin
    if (to_jsonb(new) - array['rate', 'source', 'first_used_at']) is distinct from (to_jsonb(old) - array['rate', 'source', 'first_used_at']) then
        raise exception 'exchange_rate: type, currencies and valid_from cannot change' using errcode = '23000';
    end if;
    if old.first_used_at is not null and (new.rate is distinct from old.rate or new.source is distinct from old.source
                                         or new.first_used_at is distinct from old.first_used_at) then
        raise exception 'exchange_rate: a used rate cannot change' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists exchange_rate_guard on core.exchange_rate;
create trigger exchange_rate_guard before update on core.exchange_rate
    for each row execute function core.guard_exchange_rate();

-- Immutable-after-posting columns (IAP, 07, 2.3).
create or replace function core.guard_gl_account() returns trigger language plpgsql as $$
begin
    if new.tax_account is distinct from old.tax_account and not (new.tax_account and not old.tax_account) then
        raise exception 'gl_account: tax_account is never reset' using errcode = '23000';
    end if;
    if (new.account_type, new.open_item_mode, new.currency_code, new.account_no)
       is distinct from (old.account_type, old.open_item_mode, old.currency_code, old.account_no)
       and exists (select 1 from core.journal_line l
                   where l.tenant_id = old.tenant_id and l.company_id = old.company_id and l.account_id = old.account_id) then
        raise exception 'gl_account: account type, open item mode, currency and number are fixed once the account has postings'
            using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists gl_account_guard on core.gl_account;
create trigger gl_account_guard before update on core.gl_account
    for each row execute function core.guard_gl_account();

create or replace function core.guard_company() returns trigger language plpgsql as $$
begin
    if (new.country_code, new.local_currency, new.reporting_currency) is distinct from
       (old.country_code, old.local_currency, old.reporting_currency)
       and exists (select 1 from core.journal_document d where d.tenant_id = old.tenant_id and d.company_id = old.company_id) then
        raise exception 'company: country and currencies are fixed once the company has postings' using errcode = '23000';
    end if;
    if new.fiscal_calendar_id is distinct from old.fiscal_calendar_id
       and exists (select 1 from core.fiscal_year y where y.tenant_id = old.tenant_id and y.company_id = old.company_id) then
        raise exception 'company: the calendar is fixed once a fiscal year is open' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists company_guard on core.company;
create trigger company_guard before update on core.company
    for each row execute function core.guard_company();

-- ---------------------------------------------------------------------------------------------------------
-- 5. Integrity checks at commit (deferred constraint triggers): backstops for the posting command (02, 3)
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.check_document_balance() returns trigger language plpgsql as $$
declare s_tc numeric; s_lc numeric; s_gc numeric; s_rc numeric; n int; n_rc int;
begin
    select sum(amount_tc), sum(amount_lc), sum(amount_gc), sum(amount_rc), count(*), count(amount_rc)
      into s_tc, s_lc, s_gc, s_rc, n, n_rc
      from core.journal_line
     where tenant_id = new.tenant_id and company_id = new.company_id and document_id = new.document_id;
    if s_tc <> 0 or s_lc <> 0 or s_gc <> 0 then
        raise exception 'document % does not balance: tc %, lc %, gc %', new.document_id, s_tc, s_lc, s_gc using errcode = '23514';
    end if;
    if n_rc > 0 and (n_rc <> n or s_rc <> 0) then
        raise exception 'document % has an unbalanced or partial reporting currency', new.document_id using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists journal_line_balance on core.journal_line;
create constraint trigger journal_line_balance after insert on core.journal_line
    deferrable initially deferred for each row execute function core.check_document_balance();

create or replace function core.check_document_has_lines() returns trigger language plpgsql as $$
begin
    if not exists (select 1 from core.journal_line where tenant_id = new.tenant_id and company_id = new.company_id
                   and document_id = new.document_id) then
        raise exception 'document % has no lines', new.document_id using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists journal_document_has_lines on core.journal_document;
create constraint trigger journal_document_has_lines after insert on core.journal_document
    deferrable initially deferred for each row execute function core.check_document_has_lines();

-- The ledger chain: the head equals the newest document, the document links to its predecessor, and sequence 1
-- starts from the genesis value of 02, 9.3a (computed here in SQL from the same text as the C# library).
create or replace function core.check_ledger_chain() returns trigger language plpgsql as $$
declare h core.chain_head%rowtype; max_seq bigint; max_hash bytea; expected_prev bytea;
begin
    select * into h from core.chain_head
     where tenant_id = new.tenant_id and company_id = new.company_id and chain_type = 'LEDGER' and scope_key = new.ledger_id::text;
    if not found then
        raise exception 'no LEDGER chain head for ledger %', new.ledger_id using errcode = '23514';
    end if;
    select chain_seq, row_hash into max_seq, max_hash from core.journal_document
     where tenant_id = new.tenant_id and company_id = new.company_id and ledger_id = new.ledger_id
     order by chain_seq desc limit 1;
    if h.last_seq <> max_seq or h.last_hash <> max_hash then
        raise exception 'LEDGER chain head (seq %) does not match the newest document (seq %)', h.last_seq, max_seq using errcode = '23514';
    end if;
    if new.chain_seq = 1 then
        expected_prev := sha256(convert_to('genesis|' || new.tenant_id::text || '|' || new.company_id::text || '|LEDGER|' || new.ledger_id::text, 'UTF8'));
    else
        select row_hash into expected_prev from core.journal_document
         where tenant_id = new.tenant_id and company_id = new.company_id and ledger_id = new.ledger_id and chain_seq = new.chain_seq - 1;
    end if;
    if expected_prev is distinct from new.prev_hash then
        raise exception 'document % does not link to its predecessor in the chain', new.document_id using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists journal_document_chain on core.journal_document;
create constraint trigger journal_document_chain after insert on core.journal_document
    deferrable initially deferred for each row execute function core.check_ledger_chain();

-- Outbox positions never run past the head (15, 5.2).
create or replace function core.check_outbox_position() returns trigger language plpgsql as $$
declare head bigint;
begin
    select last_position into head from core.outbox_head where tenant_id = new.tenant_id;
    if head is null or new.position > head then
        raise exception 'outbox_event position % is beyond outbox_head %', new.position, head using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists outbox_event_position on core.outbox_event;
create constraint trigger outbox_event_position after insert on core.outbox_event
    deferrable initially deferred for each row execute function core.check_outbox_position();

insert into platform.schema_migration (version, description)
values ('0006', 'security: RLS, grants, protection and integrity triggers')
on conflict (version) do nothing;
