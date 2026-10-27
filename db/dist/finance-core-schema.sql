-- Finance core schema, generated from db/migrations (0001_foundation.sql, 0002_platform_reference.sql, 0003_organisation.sql, 0004_calendar_and_numbers.sql, 0005_ledger.sql, 0006_security_and_integrity.sql, 0007_posting_period_lock.sql, 0008_migration_role_membership.sql, 0009_config_outbox_head.sql, 0010_document_lookup.sql, 0011_account_roles.sql, 0012_change_logs.sql, 0013_l2_guard.sql, 0014_open_items.sql, 0015_clearing.sql, 0016_open_item_status.sql, 0017_business_partner.sql, 0018_open_item_partner.sql, 0019_retention_rule.sql, 0020_document_legal_snapshot.sql, 0021_posting_reads_partner.sql).
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

-- Platform tables stay outside tenant isolation (07, A6), but RLS is on as a second line of defence and so that the
-- Supabase Security Advisor has nothing to report. Reference data is readable by everyone who may query it, a
-- tenant is readable only as the tenant of the transaction, and the migration log is not readable by command roles.
-- The migration role bypasses RLS and is the only one that writes these tables.
alter table platform.ref_currency enable row level security;
alter table platform.ref_country enable row level security;
alter table platform.tenant enable row level security;
alter table platform.schema_migration enable row level security;
drop policy if exists ref_read on platform.ref_currency;
create policy ref_read on platform.ref_currency for select using (true);
drop policy if exists ref_read on platform.ref_country;
create policy ref_read on platform.ref_country for select using (true);
drop policy if exists tenant_read on platform.tenant;
create policy tenant_read on platform.tenant for select using (tenant_id = core.current_tenant_id());
-- platform.schema_migration has no policy: no command role can read or write it.

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

-- ==================== 0007_posting_period_lock.sql ====================
-- 0007 the posting command takes a share lock on the period rows (06, 9, lock order step 3).
-- PostgreSQL only allows SELECT ... FOR SHARE for a role with UPDATE privilege on at least one column. cmd_post got no
-- UPDATE on the period tables in 0006, so the lock failed with 42501 (finding F-IMPL-9, found by the posting tests).
-- The grant is on last_event_id only, and the guard trigger of 0006 (guard_period_status) rejects every change of it that
-- does not match the latest period_event of the same period and status. The role can lock the row, it cannot change it.
-- The share lock is what makes a period close wait for a running posting, and a posting after the close see the new status.
grant update (last_event_id) on core.fiscal_period, core.fiscal_year to cmd_post;

insert into platform.schema_migration (version, description)
values ('0007', 'posting role may lock period rows for share (last_event_id guarded by trigger)')
on conflict (version) do nothing;

-- ==================== 0008_migration_role_membership.sql ====================
-- 0008 the migration role may switch to the command roles. Needed to run db/tests/001_protection.sql on Supabase, where
-- the SQL Editor runs as postgres without superuser rights (set role needs membership). Found when the tests ran there.
-- It gives the migration role nothing it does not have: it already owns every object. Skipped where no role postgres exists.
do $$ begin
    if exists (select 1 from pg_roles where rolname = 'postgres') then
        grant cmd_read, cmd_config, cmd_period, cmd_post, cmd_retention to postgres;
    end if;
end $$;

insert into platform.schema_migration (version, description)
values ('0008', 'migration role is a member of the command roles (tests on Supabase)')
on conflict (version) do nothing;

-- ==================== 0009_config_outbox_head.sql ====================
-- 0009 the configuration role may take outbox positions. Every command writes its outbox event in its own transaction
-- and moves outbox_head last (06, 6.3, class 7). 0006 gave that update to cmd_post and cmd_period only, so
-- companies:activate (a configuration command that emits company.activated) could not run (finding F-IMPL-11).
-- The guard trigger outbox_head_guard still allows only a forward move.
grant update (last_position) on core.outbox_head to cmd_config;

insert into platform.schema_migration (version, description)
values ('0009', 'configuration role may move outbox_head forward')
on conflict (version) do nothing;

-- ==================== 0010_document_lookup.sql ====================
-- 0010 look up a document by its id alone. journal-documents:reverse (06, 5.3) takes only document_id, while the primary
-- key of journal_document leads with company_id.
create index if not exists jd_document on core.journal_document (tenant_id, document_id);

insert into platform.schema_migration (version, description)
values ('0010', 'journal_document lookup by document_id')
on conflict (version) do nothing;

-- ==================== 0011_account_roles.sql ====================
-- 0011 account_role_assignment (07, 6.3): the accounts the core posts to on its own (rounding, FX, retained earnings,
-- intercompany, migration). The roles that depend on item group, warehouse, item or partner belong to
-- account_determination (10, 3.9) and are not part of this table.

create table if not exists core.account_role_assignment (
    tenant_id              uuid not null,
    company_id             uuid not null,
    assignment_id          uuid not null default gen_random_uuid(),
    role_code              core.code not null check (role_code in (
        'ROUNDING', 'TAX_ROUNDING', 'FX_REALISED', 'FX_UNREALISED_GAIN', 'FX_UNREALISED_LOSS', 'RETAINED_EARNINGS',
        'IC_RECEIVABLE', 'IC_PAYABLE', 'VAT_SETTLEMENT', 'MIGRATION_CLEARING', 'HISTORY_RESULT')),
    currency_code          char(3) references platform.ref_currency(currency_code),
    counterpart_company_id uuid,
    vat_registration_id    uuid,      -- no FK: company_vat_registration (08, 4.1) does not exist yet
    account_id             uuid not null,
    effective_from         timestamptz not null,
    primary key (tenant_id, company_id, assignment_id),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, company_id, account_id) references core.gl_account (tenant_id, company_id, account_id),
    foreign key (tenant_id, counterpart_company_id) references core.company (tenant_id, company_id),
    -- 6.3: a currency belongs to the roles that are per currency, a counterpart to the IC roles, a registration to VAT_SETTLEMENT.
    check (currency_code is null or role_code in ('ROUNDING', 'FX_UNREALISED_GAIN', 'FX_UNREALISED_LOSS')),
    check ((counterpart_company_id is not null) = (role_code in ('IC_RECEIVABLE', 'IC_PAYABLE'))),
    check (counterpart_company_id is null or counterpart_company_id <> company_id),
    check (vat_registration_id is null or role_code = 'VAT_SETTLEMENT')
);
-- The unique key of 6.3 with the nullable parts replaced by a fixed marker (ADR-149, D5).
create unique index if not exists account_role_assignment_key on core.account_role_assignment (
    tenant_id, company_id, role_code,
    coalesce(currency_code, '***'),
    coalesce(counterpart_company_id, '00000000-0000-0000-0000-000000000000'::uuid),
    coalesce(vat_registration_id, '00000000-0000-0000-0000-000000000000'::uuid),
    effective_from);
create index if not exists account_role_assignment_account on core.account_role_assignment (tenant_id, company_id, account_id);

-- ---------------------------------------------------------------------------------------------------------
-- Security: forced RLS, read for every command role, insert for the configuration role, never update or delete.
-- A change of a role is a new row with a later effective_from (V table).
-- ---------------------------------------------------------------------------------------------------------
alter table core.account_role_assignment enable row level security;
alter table core.account_role_assignment force row level security;
do $$ begin
    if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = 'account_role_assignment' and policyname = 'tenant_isolation') then
        create policy tenant_isolation on core.account_role_assignment using (tenant_id = core.current_tenant_id())
            with check (tenant_id = core.current_tenant_id());
    end if;
end $$;
revoke all on core.account_role_assignment from public;
grant select on core.account_role_assignment to cmd_read, cmd_config, cmd_period, cmd_post;
grant insert on core.account_role_assignment to cmd_config;

drop trigger if exists account_role_assignment_append_only on core.account_role_assignment;
create trigger account_role_assignment_append_only before update or delete on core.account_role_assignment
    for each row execute function core.reject_change();
drop trigger if exists account_role_assignment_no_truncate on core.account_role_assignment;
create trigger account_role_assignment_no_truncate before truncate on core.account_role_assignment
    for each statement execute function core.reject_change();

-- ---------------------------------------------------------------------------------------------------------
-- The account rules of the role table (07, 6.3) as a backstop. The command checks the same rules first and answers with
-- parameters; this trigger stops every other writer. Error code in the message: CONFIG.ROLE_ACCOUNT_RULE_VIOLATED.
-- Not checked here, because the table does not exist yet: "no mandatory dimension rules" (account_dimension_rule, 6.2).
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.guard_account_role_assignment() returns trigger language plpgsql as $$
declare a core.gl_account%rowtype; pl boolean; bs boolean;
begin
    select * into a from core.gl_account
     where tenant_id = new.tenant_id and company_id = new.company_id and account_id = new.account_id;
    if not found then
        raise exception 'CONFIG.INVALID_REFERENCE: account % of the role % does not exist', new.account_id, new.role_code using errcode = '23503';
    end if;
    pl := new.role_code in ('ROUNDING', 'TAX_ROUNDING', 'FX_REALISED', 'FX_UNREALISED_GAIN', 'FX_UNREALISED_LOSS');
    bs := new.role_code in ('IC_RECEIVABLE', 'IC_PAYABLE', 'VAT_SETTLEMENT', 'MIGRATION_CLEARING', 'HISTORY_RESULT');
    if pl and a.account_type not in ('REVENUE', 'EXPENSE') then
        raise exception 'CONFIG.ROLE_ACCOUNT_RULE_VIOLATED: role % needs a P&L account (ACCOUNT_TYPE)', new.role_code using errcode = '23514';
    end if;
    if bs and a.account_type not in ('ASSET', 'LIABILITY', 'EQUITY') then
        raise exception 'CONFIG.ROLE_ACCOUNT_RULE_VIOLATED: role % needs a balance sheet account (ACCOUNT_TYPE)', new.role_code using errcode = '23514';
    end if;
    if new.role_code = 'RETAINED_EARNINGS' and a.account_type <> 'EQUITY' then
        raise exception 'CONFIG.ROLE_ACCOUNT_RULE_VIOLATED: role RETAINED_EARNINGS needs an EQUITY account (ACCOUNT_TYPE)' using errcode = '23514';
    end if;
    if new.role_code = 'RETAINED_EARNINGS' and a.revalue_flag then
        raise exception 'CONFIG.REVALUE_FLAG_ON_RETAINED_EARNINGS: the account has revalue_flag' using errcode = '23514';
    end if;
    if a.open_item_mode <> 'NONE' then
        raise exception 'CONFIG.ROLE_ACCOUNT_RULE_VIOLATED: role % needs open_item_mode NONE (OPEN_ITEM_MODE)', new.role_code using errcode = '23514';
    end if;
    if exists (select 1 from core.account_role_assignment r
                where r.tenant_id = new.tenant_id and r.company_id = new.company_id and r.account_id = new.account_id
                  and r.role_code <> new.role_code) then
        raise exception 'CONFIG.ROLE_ACCOUNT_RULE_VIOLATED: the account has another role (ONE_ROLE_PER_ACCOUNT)' using errcode = '23514';
    end if;
    if new.currency_code is not null and a.currency_code is not null and a.currency_code <> new.currency_code then
        raise exception 'CONFIG.ROLE_ACCOUNT_RULE_VIOLATED: the account is for % only (CURRENCY)', a.currency_code using errcode = '23514';
    end if;
    if new.currency_code is null and a.currency_code is not null then
        raise exception 'CONFIG.ROLE_ACCOUNT_RULE_VIOLATED: a default row needs an account without currency (CURRENCY)' using errcode = '23514';
    end if;
    if a.tax_account and new.role_code <> 'VAT_SETTLEMENT' then
        raise exception 'CONFIG.ROLE_ACCOUNT_RULE_VIOLATED: a tax account accepts only VAT_SETTLEMENT (TAX_ACCOUNT)' using errcode = '23514';
    end if;
    return new;
end $$;
drop trigger if exists account_role_assignment_guard on core.account_role_assignment;
create trigger account_role_assignment_guard before insert on core.account_role_assignment
    for each row execute function core.guard_account_role_assignment();

-- gl_account: what a role row fixes (07, 6.1 and the role rules F8, 1).
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
    if (new.account_type, new.open_item_mode) is distinct from (old.account_type, old.open_item_mode)
       and exists (select 1 from core.account_role_assignment r
                   where r.tenant_id = old.tenant_id and r.company_id = old.company_id and r.account_id = old.account_id) then
        raise exception 'CONFIG.REFERENCED: account type and open item mode are fixed while a role refers to the account' using errcode = '23000';
    end if;
    if new.revalue_flag and not old.revalue_flag
       and exists (select 1 from core.account_role_assignment r
                   where r.tenant_id = old.tenant_id and r.company_id = old.company_id and r.account_id = old.account_id
                     and r.role_code = 'RETAINED_EARNINGS') then
        raise exception 'CONFIG.REVALUE_FLAG_ON_RETAINED_EARNINGS: the account has the role RETAINED_EARNINGS' using errcode = '23000';
    end if;
    return new;
end $$;

insert into platform.schema_migration (version, description)
values ('0011', 'account_role_assignment with the account rules of the role table')
on conflict (version) do nothing;

-- ==================== 0012_change_logs.sql ====================
-- 0012 change_log (02, 8) and master_change_log (07, 10.1): the field-level evidence of L2 changes of journal records
-- and of changes of master data. Both are append-only. No command writes them yet: change_log waits for
-- journal-documents:change-open-fields, master_change_log for the first {resource}:update of a master table (M).
-- V tables (such as account_role_assignment) have no rows here: their rows are the history (07, 10.1).

-- ---------------------------------------------------------------------------------------------------------
-- change_log (02, 8)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.change_log (
    tenant_id       uuid not null,
    change_id       uuid not null default gen_random_uuid(),
    change_set_id   uuid not null,
    company_id      uuid not null,
    entity_type     core.code not null check (entity_type in (
        'DOCUMENT', 'LINE', 'LINE_OPEN_ITEM', 'LINE_DIMENSION', 'LEGAL_SNAPSHOT', 'CLEARING', 'TAX_EVIDENCE')),
    document_id     uuid,
    clearing_id     uuid,     -- no FK: clearing (03, 4.1) does not exist yet
    line_no         int,
    sub_key         varchar(50),
    field_name      core.code not null,
    value_type      core.code not null check (value_type in ('AMOUNT', 'DATE', 'TEXT', 'ID', 'CODE', 'BOOL')),
    old_value       varchar(500),
    new_value       varchar(500),
    lock_class      core.code not null check (lock_class in ('L2', 'L1*')),
    period_status   core.code not null check (period_status in ('OPEN', 'SOFT_CLOSED', 'NONE')),
    reason_code     core.code not null,
    reason_text     varchar(500),
    changed_by      varchar(100) not null,
    changed_at      timestamptz not null,
    correlation_id  varchar(100) not null,
    idempotency_key varchar(100) not null,
    primary key (tenant_id, change_id),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, company_id, document_id) references core.journal_document (tenant_id, company_id, document_id),
    foreign key (tenant_id, company_id, document_id, line_no) references core.journal_line (tenant_id, company_id, document_id, line_no),
    -- 02, 8: exactly one of document_id and clearing_id is set. CLEARING has the clearing and no line.
    check ((document_id is null) <> (clearing_id is null)),
    check ((entity_type = 'CLEARING') = (clearing_id is not null)),
    -- line_no: null for header changes and for CLEARING, set for the entity types that belong to a line.
    check ((entity_type in ('LINE', 'LINE_OPEN_ITEM', 'LINE_DIMENSION', 'TAX_EVIDENCE')) = (line_no is not null)),
    -- L2 changes happen in an open or soft-closed period (a hard-closed period rejects them, L3). The one logged L1
    -- exception, the retention overwrite of the legal snapshot, is not period-governed and has the reason SYSTEM.
    check ((lock_class = 'L2' and period_status in ('OPEN', 'SOFT_CLOSED') and reason_code <> 'SYSTEM')
        or (lock_class = 'L1*' and period_status = 'NONE' and reason_code = 'SYSTEM' and entity_type = 'LEGAL_SNAPSHOT')),
    check (old_value is distinct from new_value)
);
create index if not exists change_log_document on core.change_log (tenant_id, company_id, document_id, line_no);
create index if not exists change_log_clearing on core.change_log (tenant_id, company_id, clearing_id);
create index if not exists change_log_correlation on core.change_log (tenant_id, correlation_id);
create index if not exists change_log_time on core.change_log (tenant_id, changed_at);

-- ---------------------------------------------------------------------------------------------------------
-- master_change_log (07, 10.1)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.master_change_log (
    tenant_id      uuid not null,
    change_id      uuid not null default gen_random_uuid(),
    change_set_id  uuid not null,
    entity_type    core.code not null,
    entity_id      varchar(100) not null,
    action         core.code not null check (action in ('CREATE', 'UPDATE', 'DEACTIVATE', 'REACTIVATE', 'ANONYMISE', 'CLEAR')),
    app_id         uuid,      -- no FK: app registration (15, 4) does not exist yet
    field_name     core.code,
    is_pii         boolean not null default false,
    old_value      varchar(500),
    new_value      varchar(500),
    reason_code    core.code,
    changed_by     varchar(100) not null,
    changed_at     timestamptz not null,
    correlation_id varchar(100) not null,
    command_log_id uuid not null,
    primary key (tenant_id, change_id),
    foreign key (tenant_id) references platform.tenant (tenant_id),
    -- The command writes its log row in the same transaction, after the change rows, so the check runs at commit.
    foreign key (tenant_id, command_log_id) references core.command_log (tenant_id, command_log_id) deferrable initially deferred,
    -- Null for CREATE (the whole row is the new state) and for DEACTIVATE. A field change names its field.
    check (action not in ('CREATE', 'DEACTIVATE') or field_name is null),
    check (action <> 'UPDATE' or field_name is not null),
    -- The log never holds a personal value (07, 10.1). Anonymisation does not touch it, because it never held the values.
    check (not is_pii or (old_value is null and new_value is null)),
    -- A create or a deactivation carries no field values. An update changes a value.
    check (action not in ('CREATE', 'DEACTIVATE') or (old_value is null and new_value is null)),
    check (action <> 'UPDATE' or is_pii or old_value is distinct from new_value)
);
create index if not exists master_change_log_entity on core.master_change_log (tenant_id, entity_type, entity_id, changed_at);
create index if not exists master_change_log_correlation on core.master_change_log (tenant_id, correlation_id);
create index if not exists master_change_log_set on core.master_change_log (tenant_id, change_set_id);

-- ---------------------------------------------------------------------------------------------------------
-- Security: forced RLS, read for every command role, insert only for the roles that write the log, never update or delete.
-- change_log: the posting role runs the change command (02, 10 lists "the change command role"), the retention role
-- writes the L1* row of its snapshot overwrite. master_change_log: the configuration role, and the retention role for ANONYMISE.
-- ---------------------------------------------------------------------------------------------------------
do $$
declare t text;
begin
    foreach t in array array['change_log', 'master_change_log'] loop
        execute format('alter table core.%I enable row level security', t);
        execute format('alter table core.%I force row level security', t);
        if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = t and policyname = 'tenant_isolation') then
            execute format('create policy tenant_isolation on core.%I using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id())', t);
        end if;
        execute format('revoke all on core.%I from public', t);
        execute format('grant select on core.%I to cmd_read, cmd_config, cmd_period, cmd_post', t);
        execute format('drop trigger if exists %I on core.%I', t || '_append_only', t);
        execute format('create trigger %I before update or delete on core.%I for each row execute function core.reject_change()', t || '_append_only', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
        execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_change()', t || '_no_truncate', t);
    end loop;
end $$;
grant insert on core.change_log to cmd_post, cmd_retention;
grant insert on core.master_change_log to cmd_config, cmd_retention;
grant select on core.change_log, core.master_change_log to cmd_retention;

insert into platform.schema_migration (version, description)
values ('0012', 'change_log and master_change_log, append-only')
on conflict (version) do nothing;

-- ==================== 0013_l2_guard.sql ====================
-- 0013 The L2 columns open (F-IMPL-3, 02, 8 and 10): header_text and external_reference of a document and line_text of a line
-- can change, and only together with a change_log row of the same transaction, and never in a hard-closed period (L3).
-- Every other column stays locked: L1 never changes, the S columns of a document stay as 0006 allows them.
-- Not built: the typed dimensions of a line (cost_center_id and the three others), because the dimension tables that
-- validate them do not exist (06, 5.4 P4), the free dimensions, the open-item fields and the tax evidence.

-- "The same transaction" is checked by content and by time, not by transaction id, which breaks under savepoints (F-IMPL-2):
-- the log row must carry the old and the new value, the lock class L2, the status of the governing period, and the
-- timestamp of this transaction (the command writes it with now()).
create or replace function core.require_l2_log(
    p_tenant uuid, p_company uuid, p_document uuid, p_line_no int, p_field text, p_old text, p_new text,
    p_fiscal_year int, p_fiscal_period int) returns void language plpgsql as $$
declare
    v_status text;
begin
    select status into v_status from core.fiscal_period
     where tenant_id = p_tenant and company_id = p_company and fiscal_year = p_fiscal_year and period_no = p_fiscal_period;
    if v_status = 'HARD_CLOSED' then
        raise exception 'LOCK.FIELD_LOCKED_L3: % cannot change in a hard-closed period', p_field using errcode = '23000';
    end if;
    if not exists (
        select 1 from core.change_log c
         where c.tenant_id = p_tenant and c.company_id = p_company and c.document_id = p_document
           and c.line_no is not distinct from p_line_no
           and c.entity_type = case when p_line_no is null then 'DOCUMENT' else 'LINE' end
           and c.field_name = p_field
           and c.old_value is not distinct from p_old and c.new_value is not distinct from p_new
           and c.lock_class = 'L2' and c.period_status = v_status
           and c.changed_at = transaction_timestamp()) then
        raise exception 'journal record: the L2 column % cannot change without a matching change_log row in this transaction', p_field
            using errcode = '23000';
    end if;
end $$;

create or replace function core.guard_journal_document() returns trigger language plpgsql as $$
begin
    -- The S columns status and reversed_by_document_id and the L2 columns header_text and external_reference may change.
    -- Everything else is L1.
    if (to_jsonb(new) - array['status', 'reversed_by_document_id', 'header_text', 'external_reference'])
       is distinct from (to_jsonb(old) - array['status', 'reversed_by_document_id', 'header_text', 'external_reference']) then
        raise exception 'journal_document: an L1 column cannot change' using errcode = '23000';
    end if;
    if new.status is distinct from old.status and not (old.status = 'posted' and new.status = 'reversed') then
        raise exception 'journal_document: status can only change from posted to reversed' using errcode = '23000';
    end if;
    if old.reversed_by_document_id is not null and new.reversed_by_document_id is distinct from old.reversed_by_document_id then
        raise exception 'journal_document: reversed_by_document_id is never overwritten (B1)' using errcode = '23000';
    end if;
    if new.header_text is distinct from old.header_text then
        perform core.require_l2_log(new.tenant_id, new.company_id, new.document_id, null, 'header_text',
                                    old.header_text, new.header_text, new.fiscal_year, new.fiscal_period);
    end if;
    if new.external_reference is distinct from old.external_reference then
        perform core.require_l2_log(new.tenant_id, new.company_id, new.document_id, null, 'external_reference',
                                    old.external_reference, new.external_reference, new.fiscal_year, new.fiscal_period);
    end if;
    return new;
end $$;

create or replace function core.guard_journal_line() returns trigger language plpgsql as $$
begin
    -- Only line_text is open. The typed dimensions open with the dimension tables.
    if (to_jsonb(new) - 'line_text') is distinct from (to_jsonb(old) - 'line_text') then
        raise exception 'journal_line: only line_text can change, and only through the change command' using errcode = '23000';
    end if;
    if new.line_text is distinct from old.line_text then
        perform core.require_l2_log(new.tenant_id, new.company_id, new.document_id, new.line_no, 'line_text',
                                    old.line_text, new.line_text, new.fiscal_year, new.fiscal_period);
    end if;
    return new;
end $$;

-- The posting role runs the change command (02, 10). It gets UPDATE on the open columns only.
grant update (header_text, external_reference) on core.journal_document to cmd_post;
grant update (line_text) on core.journal_line to cmd_post;

insert into platform.schema_migration (version, description)
values ('0013', 'L2 columns of documents and lines open with a change_log row (F-IMPL-3)')
on conflict (version) do nothing;

-- ==================== 0014_open_items.sql ====================
-- 0014 journal_line_open_item (02, 5A, ADR-033): the open-item attributes of a line on an open-item-managed account.
-- Row existence marks an open item. This slice builds the accounts of mode GL_OPEN_ITEM (no partner). The accounts of mode
-- CONTROL need business_partner (07, 9.1), which does not exist yet: partner_id has no foreign key and the backstop below
-- rejects a CONTROL line, because a CONTROL line needs a partner. The clearing tables (03) do not exist either, so the S columns
-- (clearing_status, cleared_amount_*, last_clearing_id) cannot change yet and last_clearing_id has no foreign key.

create table if not exists core.journal_line_open_item (
    tenant_id             uuid not null,
    company_id            uuid not null,
    document_id           uuid not null,
    line_no               int  not null,
    partner_id            uuid,                 -- L1. No FK until business_partner exists
    baseline_date         date,                 -- L2
    payment_terms_id      uuid,                 -- L2. No FK until payment_terms exists (07, 9.6)
    due_date              date,                 -- L2. Mandatory for CONTROL accounts
    discount_due_date     date,                 -- L2
    discount_amount_tc    core.amt,             -- L2. Informational: the core posts no discount (Q9)
    payment_block         core.code,            -- L2
    dunning_block         core.code,            -- L2
    payment_reference     varchar(100),         -- L2
    bank_account_id       uuid,                 -- L2. No FK until company_bank_account exists (07, 9.5)
    legacy_document_date  date,                 -- L1. Migrated open items (13, 6.3)
    legacy_posting_date   date,                 -- L1
    legacy_document_ref   varchar(100),         -- L1
    clearing_status       core.code not null default 'OPEN' check (clearing_status in ('OPEN', 'PARTIAL', 'CLEARED')),  -- S
    cleared_amount_tc     core.amt not null default 0,   -- S
    cleared_amount_lc     core.amt not null default 0,   -- S
    cleared_amount_gc     core.amt not null default 0,   -- S
    cleared_amount_rc     core.amt,                      -- S. Null if the line has no RC amount
    last_clearing_id      uuid,                 -- S. No FK until clearing exists (03, 4.1)
    primary key (tenant_id, company_id, document_id, line_no),
    foreign key (tenant_id, company_id, document_id, line_no) references core.journal_line (tenant_id, company_id, document_id, line_no),
    -- a status that says nothing is cleared has nothing cleared
    check (clearing_status <> 'OPEN' or (cleared_amount_tc = 0 and cleared_amount_lc = 0 and cleared_amount_gc = 0 and coalesce(cleared_amount_rc, 0) = 0)),
    check (clearing_status = 'OPEN' or last_clearing_id is not null),
    check (discount_amount_tc is null or discount_amount_tc >= 0)
);
create index if not exists jloi_partner_status on core.journal_line_open_item (tenant_id, company_id, partner_id, clearing_status);
create index if not exists jloi_partner_due on core.journal_line_open_item (tenant_id, company_id, partner_id, due_date);

-- ---------------------------------------------------------------------------------------------------------
-- Security: forced RLS, read for every command role, insert by the posting role, UPDATE on the L2 columns for the change command
-- (the posting role runs it, 02, 10). The S columns get their own grant when the clearing role exists. No delete.
-- ---------------------------------------------------------------------------------------------------------
alter table core.journal_line_open_item enable row level security;
alter table core.journal_line_open_item force row level security;
do $$ begin
    if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = 'journal_line_open_item' and policyname = 'tenant_isolation') then
        create policy tenant_isolation on core.journal_line_open_item using (tenant_id = core.current_tenant_id())
            with check (tenant_id = core.current_tenant_id());
    end if;
end $$;
revoke all on core.journal_line_open_item from public;
grant select on core.journal_line_open_item to cmd_read, cmd_config, cmd_period, cmd_post;
grant insert on core.journal_line_open_item to cmd_post;
grant update (baseline_date, payment_terms_id, due_date, discount_due_date, discount_amount_tc, payment_block, dunning_block,
              payment_reference, bank_account_id) on core.journal_line_open_item to cmd_post;
do $$ begin
    execute 'drop trigger if exists journal_line_open_item_no_delete on core.journal_line_open_item';
    execute 'create trigger journal_line_open_item_no_delete before delete on core.journal_line_open_item for each row execute function core.reject_delete()';
    execute 'drop trigger if exists journal_line_open_item_no_truncate on core.journal_line_open_item';
    execute 'create trigger journal_line_open_item_no_truncate before truncate on core.journal_line_open_item for each statement execute function core.reject_delete()';
end $$;

-- ---------------------------------------------------------------------------------------------------------
-- The L2 guard knows the entity type (0013 chose DOCUMENT or LINE by the line number). The nine-argument form of 0013 stays
-- and calls the new one.
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.require_l2_log(
    p_tenant uuid, p_company uuid, p_document uuid, p_line_no int, p_field text, p_old text, p_new text,
    p_fiscal_year int, p_fiscal_period int, p_entity text) returns void language plpgsql as $$
declare
    v_status text;
begin
    select status into v_status from core.fiscal_period
     where tenant_id = p_tenant and company_id = p_company and fiscal_year = p_fiscal_year and period_no = p_fiscal_period;
    if v_status = 'HARD_CLOSED' then
        raise exception 'LOCK.FIELD_LOCKED_L3: % cannot change in a hard-closed period', p_field using errcode = '23000';
    end if;
    if not exists (
        select 1 from core.change_log c
         where c.tenant_id = p_tenant and c.company_id = p_company and c.document_id = p_document
           and c.line_no is not distinct from p_line_no
           and c.entity_type = p_entity
           and c.field_name = p_field
           and c.old_value is not distinct from p_old and c.new_value is not distinct from p_new
           and c.lock_class = 'L2' and c.period_status = v_status
           and c.changed_at = transaction_timestamp()) then
        raise exception 'journal record: the L2 column % cannot change without a matching change_log row in this transaction', p_field
            using errcode = '23000';
    end if;
end $$;

create or replace function core.require_l2_log(
    p_tenant uuid, p_company uuid, p_document uuid, p_line_no int, p_field text, p_old text, p_new text,
    p_fiscal_year int, p_fiscal_period int) returns void language sql as $$
    select core.require_l2_log(p_tenant, p_company, p_document, p_line_no, p_field, p_old, p_new, p_fiscal_year, p_fiscal_period,
                               case when p_line_no is null then 'DOCUMENT' else 'LINE' end)
$$;

-- ---------------------------------------------------------------------------------------------------------
-- Lock classes of the open item (02, 5A and 10): L1 never changes, the L2 columns change with a change_log row (entity type
-- LINE_OPEN_ITEM), the S columns change only with a clearing, which cannot exist yet.
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.guard_journal_line_open_item() returns trigger language plpgsql as $$
declare
    v_year int; v_period int;
    f text;
    l2 constant text[] := array['baseline_date', 'payment_terms_id', 'due_date', 'discount_due_date', 'discount_amount_tc',
                                'payment_block', 'dunning_block', 'payment_reference', 'bank_account_id'];
    s  constant text[] := array['clearing_status', 'cleared_amount_tc', 'cleared_amount_lc', 'cleared_amount_gc', 'cleared_amount_rc', 'last_clearing_id'];
begin
    if (to_jsonb(new) - l2 - s) is distinct from (to_jsonb(old) - l2 - s) then
        raise exception 'journal_line_open_item: an L1 column cannot change' using errcode = '23000';
    end if;
    if (to_jsonb(new) - l2) is distinct from (to_jsonb(old) - l2) then
        raise exception 'journal_line_open_item: the S columns change only with a clearing, and clearing is not built yet' using errcode = '23000';
    end if;
    select fiscal_year, fiscal_period into v_year, v_period from core.journal_line
     where tenant_id = new.tenant_id and company_id = new.company_id and document_id = new.document_id and line_no = new.line_no;
    foreach f in array l2 loop
        if (to_jsonb(new) ->> f) is distinct from (to_jsonb(old) ->> f) then
            perform core.require_l2_log(new.tenant_id, new.company_id, new.document_id, new.line_no, f,
                                        to_jsonb(old) ->> f, to_jsonb(new) ->> f, v_year, v_period, 'LINE_OPEN_ITEM');
        end if;
    end loop;
    return new;
end $$;
drop trigger if exists journal_line_open_item_guard on core.journal_line_open_item;
create trigger journal_line_open_item_guard before update on core.journal_line_open_item
    for each row execute function core.guard_journal_line_open_item();

-- ---------------------------------------------------------------------------------------------------------
-- Backstop at commit (02, 5A): a line on an open-item account has a row, a line on a NONE account has none, a CONTROL line has a
-- partner. Row existence is decided at posting, so the check reads the account once at commit. The lines of 02, 5A that are
-- exempt (REVAL_ADJUSTMENT, CLOSING, HISTORY_PL, OPENING) carry no row. With no partner table yet, a CONTROL line cannot pass.
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.check_open_item_rows() returns trigger language plpgsql as $$
declare
    v_type text; v_account uuid; v_mode text; v_partner uuid; v_has boolean;
begin
    select line_type, account_id into v_type, v_account from core.journal_line
     where tenant_id = new.tenant_id and company_id = new.company_id and document_id = new.document_id and line_no = new.line_no;
    if v_type in ('REVAL_ADJUSTMENT', 'CLOSING', 'HISTORY_PL', 'OPENING') then
        return null;
    end if;
    select open_item_mode into v_mode from core.gl_account
     where tenant_id = new.tenant_id and company_id = new.company_id and account_id = v_account;
    select true, partner_id into v_has, v_partner from core.journal_line_open_item
     where tenant_id = new.tenant_id and company_id = new.company_id and document_id = new.document_id and line_no = new.line_no;
    v_has := coalesce(v_has, false);
    if v_mode = 'NONE' and v_has then
        raise exception 'journal_line_open_item: line % of document % is on an account that is not open-item managed', new.line_no, new.document_id using errcode = '23514';
    end if;
    if v_mode <> 'NONE' and not v_has then
        raise exception 'journal_line_open_item: line % of document % is on an open-item account and has no open item row', new.line_no, new.document_id using errcode = '23514';
    end if;
    if v_mode = 'CONTROL' and v_partner is null then
        raise exception 'journal_line_open_item: line % of document % is on a control account and has no partner', new.line_no, new.document_id using errcode = '23514';
    end if;
    if v_mode = 'GL_OPEN_ITEM' and v_partner is not null then
        raise exception 'journal_line_open_item: line % of document % is on a GL open-item account, which takes no partner', new.line_no, new.document_id using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists journal_line_open_item_rows on core.journal_line;
create constraint trigger journal_line_open_item_rows after insert on core.journal_line
    deferrable initially deferred for each row execute function core.check_open_item_rows();
drop trigger if exists journal_line_open_item_rows_oi on core.journal_line_open_item;
create constraint trigger journal_line_open_item_rows_oi after insert on core.journal_line_open_item
    deferrable initially deferred for each row execute function core.check_open_item_rows();

insert into platform.schema_migration (version, description)
values ('0014', 'journal_line_open_item for GL_OPEN_ITEM accounts, with its guards')
on conflict (version) do nothing;

-- ==================== 0015_clearing.sql ====================
-- 0015 clearing (03): the clearing record, its items, the clearing chain, the clearing command role and the rule that the
-- S columns of an open item are a cache of the clearing items (03, 4.3). No command writes these tables yet: clearings:post
-- (03, 5) and clearings:reset come next. The hash is computed by the application (ADR-211, 03, 6.1), SQL checks the links.

-- ---------------------------------------------------------------------------------------------------------
-- The clearing command role (02, 10: "the clearing command role"). It is not a member of cmd_post: a restrictive policy
-- of 0006 limits cmd_post to the LEDGER head, and membership would make both policies apply. It gets the grants it needs
-- itself: its own chain, a CLEARING difference document with its number and its LEDGER head, and the S columns of open items.
-- ---------------------------------------------------------------------------------------------------------
do $$ begin
    if not exists (select 1 from pg_roles where rolname = 'cmd_clearing') then
        create role cmd_clearing nologin;
    end if;
end $$;
grant cmd_clearing to core_app;
do $$ begin
    if exists (select 1 from pg_roles where rolname = 'postgres') then
        grant cmd_clearing to postgres;
    end if;
end $$;

grant usage on schema platform, core to cmd_clearing;

-- ---------------------------------------------------------------------------------------------------------
-- clearing (03, 4.1)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.clearing (
    tenant_id            uuid not null,
    company_id           uuid not null,
    clearing_id          uuid not null default gen_random_uuid(),
    chain_seq            bigint not null check (chain_seq >= 1),
    clearing_type        core.code not null check (clearing_type in ('CLEAR', 'RESET')),
    clearing_date        date not null,
    fiscal_year          int not null,
    fiscal_period        int not null,
    cleared_at           timestamptz not null,
    account_id           uuid not null,
    partner_id           uuid,                 -- no FK until business_partner exists. Null only for GL_OPEN_ITEM accounts
    document_id          uuid,                 -- the difference document, if one was posted
    reset_of_clearing_id uuid,
    clearing_method      core.code not null check (clearing_method in ('MANUAL', 'AUTO_MATCH', 'PAYMENT_ALLOCATION', 'REVERSAL')),
    matching_ref         varchar(100),
    source_system        core.code not null,
    idempotency_key      varchar(100) not null,
    command_name         core.code not null,
    cleared_by           varchar(100) not null,
    correlation_id       varchar(100) not null,
    note                 varchar(200),         -- L2. Changes will be logged with entity type CLEARING (02, 8). Not changeable yet
    prev_hash            core.hash32 not null,
    row_hash             core.hash32 not null,
    hash_version         smallint not null check (hash_version in (1, 2)),
    primary key (tenant_id, company_id, clearing_id),
    unique (tenant_id, company_id, chain_seq),
    unique (tenant_id, idempotency_key),
    unique (tenant_id, company_id, clearing_id, clearing_date),    -- the target of the date copy of clearing_item
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, company_id, account_id) references core.gl_account (tenant_id, company_id, account_id),
    foreign key (tenant_id, company_id, document_id) references core.journal_document (tenant_id, company_id, document_id),
    foreign key (tenant_id, company_id, reset_of_clearing_id) references core.clearing (tenant_id, company_id, clearing_id),
    foreign key (tenant_id, company_id, fiscal_year, fiscal_period) references core.fiscal_period (tenant_id, company_id, fiscal_year, period_no),
    check ((reset_of_clearing_id is not null) = (clearing_type = 'RESET')),
    check (reset_of_clearing_id is distinct from clearing_id)
);
create unique index if not exists clearing_document on core.clearing (tenant_id, company_id, document_id) where document_id is not null;
create unique index if not exists clearing_reset_of on core.clearing (tenant_id, company_id, reset_of_clearing_id) where reset_of_clearing_id is not null;
create index if not exists clearing_account_partner on core.clearing (tenant_id, company_id, account_id, partner_id);

-- ---------------------------------------------------------------------------------------------------------
-- clearing_item (03, 4.2)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.clearing_item (
    tenant_id             uuid not null,
    company_id            uuid not null,
    clearing_id           uuid not null,
    item_no               int not null check (item_no >= 1),
    clearing_date         date not null,
    open_item_document_id uuid not null,
    open_item_line_no     int not null,
    currency_tc           char(3) not null references platform.ref_currency (currency_code),
    cleared_amount_tc     core.amt not null,
    cleared_amount_lc     core.amt not null,
    cleared_amount_gc     core.amt not null,
    cleared_amount_rc     core.amt,
    primary key (tenant_id, company_id, clearing_id, item_no),
    unique (tenant_id, company_id, clearing_id, open_item_document_id, open_item_line_no),
    foreign key (tenant_id, company_id, clearing_id, clearing_date) references core.clearing (tenant_id, company_id, clearing_id, clearing_date),
    foreign key (tenant_id, company_id, open_item_document_id, open_item_line_no)
        references core.journal_line_open_item (tenant_id, company_id, document_id, line_no)
);
create index if not exists clearing_item_open_item on core.clearing_item
    (tenant_id, company_id, open_item_document_id, open_item_line_no, clearing_date);

-- The open item points at the clearing that touched it last (S column of 0014).
do $$ begin
    if not exists (select 1 from pg_constraint where conname = 'journal_line_open_item_last_clearing_fk') then
        alter table core.journal_line_open_item add constraint journal_line_open_item_last_clearing_fk
            foreign key (tenant_id, company_id, last_clearing_id) references core.clearing (tenant_id, company_id, clearing_id);
    end if;
end $$;

-- ---------------------------------------------------------------------------------------------------------
-- Security: forced RLS, read for every command role, append-only, insert for the clearing role only.
-- ---------------------------------------------------------------------------------------------------------
do $$
declare t text;
begin
    foreach t in array array['clearing', 'clearing_item'] loop
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
grant insert on core.clearing, core.clearing_item to cmd_clearing;

-- What the clearing role needs besides its own tables. A difference document (type CLEARING) is a journal document, so the role
-- inserts documents, lines and open item rows, takes a number, moves the LEDGER head and, for a reset, marks the mirrored
-- document as reversed. It locks the period rows in share mode (A1) and writes the command log and the outbox.
grant select on all tables in schema core to cmd_clearing;
grant select on all tables in schema platform to cmd_clearing;
grant insert on core.journal_document, core.journal_line, core.journal_line_open_item, core.command_log, core.command_log_result,
      core.outbox_event, core.chain_head to cmd_clearing;
grant update (status, reversed_by_document_id) on core.journal_document to cmd_clearing;
grant update (last_number, updated_at) on core.number_range_counter to cmd_clearing;
grant update (last_seq, last_hash, hash_version, updated_at) on core.chain_head to cmd_clearing;
grant update (last_position) on core.outbox_head to cmd_clearing;
grant update (last_event_id) on core.fiscal_period, core.fiscal_year to cmd_clearing;
-- The S columns of an open item (02, 5A, 10). Only the clearing role changes them.
grant update (clearing_status, cleared_amount_tc, cleared_amount_lc, cleared_amount_gc, cleared_amount_rc, last_clearing_id)
      on core.journal_line_open_item to cmd_clearing;

-- A command role moves only its own chain (02, 9.2). The clearing role moves CLEARING, and LEDGER for its difference documents.
drop policy if exists chain_head_clearing on core.chain_head;
create policy chain_head_clearing on core.chain_head as restrictive for update to cmd_clearing
    using (chain_type in ('LEDGER', 'CLEARING')) with check (chain_type in ('LEDGER', 'CLEARING'));

-- ---------------------------------------------------------------------------------------------------------
-- The S columns of an open item (03, 4.3). The guard of 0014 rejected every change. Now the clearing role may change them, and
-- the check at commit below makes sure that they are what the clearing items say. A change of an L1 column is still rejected.
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.guard_journal_line_open_item() returns trigger language plpgsql as $$
declare
    v_year int; v_period int;
    f text;
    l2 constant text[] := array['baseline_date', 'payment_terms_id', 'due_date', 'discount_due_date', 'discount_amount_tc',
                                'payment_block', 'dunning_block', 'payment_reference', 'bank_account_id'];
    s  constant text[] := array['clearing_status', 'cleared_amount_tc', 'cleared_amount_lc', 'cleared_amount_gc', 'cleared_amount_rc', 'last_clearing_id'];
begin
    if (to_jsonb(new) - l2 - s) is distinct from (to_jsonb(old) - l2 - s) then
        raise exception 'journal_line_open_item: an L1 column cannot change' using errcode = '23000';
    end if;
    if (to_jsonb(new) - l2) is distinct from (to_jsonb(old) - l2) and not pg_has_role(current_user, 'cmd_clearing', 'member') then
        raise exception 'journal_line_open_item: the S columns change only by the clearing role' using errcode = '23000';
    end if;
    select fiscal_year, fiscal_period into v_year, v_period from core.journal_line
     where tenant_id = new.tenant_id and company_id = new.company_id and document_id = new.document_id and line_no = new.line_no;
    foreach f in array l2 loop
        if (to_jsonb(new) ->> f) is distinct from (to_jsonb(old) ->> f) then
            perform core.require_l2_log(new.tenant_id, new.company_id, new.document_id, new.line_no, f,
                                        to_jsonb(old) ->> f, to_jsonb(new) ->> f, v_year, v_period, 'LINE_OPEN_ITEM');
        end if;
    end loop;
    return new;
end $$;

-- The cache of an open item is the sum of its clearing items (ADR-037, 03, 4.3): the cleared amounts, the status and the most
-- recent clearing. The cleared amount stays between 0 and the line amount, in transaction and in local currency, with the sign of
-- the line (never over-cleared, never negative, 4.2). Checked at commit, for the open item that a row of either table names.
create or replace function core.check_open_item_cache() returns trigger language plpgsql as $$
declare
    v_t uuid := new.tenant_id; v_c uuid := new.company_id; v_doc uuid; v_line int;
    l core.journal_line%rowtype; o core.journal_line_open_item%rowtype;
    s_tc numeric; s_lc numeric; s_gc numeric; s_rc numeric; n int; v_last uuid; v_status text;
begin
    if tg_table_name = 'clearing_item' then
        v_doc := new.open_item_document_id; v_line := new.open_item_line_no;
    else
        v_doc := new.document_id; v_line := new.line_no;
    end if;
    select * into o from core.journal_line_open_item where tenant_id = v_t and company_id = v_c and document_id = v_doc and line_no = v_line;
    select * into l from core.journal_line where tenant_id = v_t and company_id = v_c and document_id = v_doc and line_no = v_line;
    select coalesce(sum(cleared_amount_tc), 0), coalesce(sum(cleared_amount_lc), 0), coalesce(sum(cleared_amount_gc), 0),
           sum(cleared_amount_rc), count(*)
      into s_tc, s_lc, s_gc, s_rc, n
      from core.clearing_item
     where tenant_id = v_t and company_id = v_c and open_item_document_id = v_doc and open_item_line_no = v_line;
    select c.clearing_id into v_last from core.clearing c
      join core.clearing_item i on i.tenant_id = c.tenant_id and i.company_id = c.company_id and i.clearing_id = c.clearing_id
     where i.tenant_id = v_t and i.company_id = v_c and i.open_item_document_id = v_doc and i.open_item_line_no = v_line
     order by c.chain_seq desc limit 1;

    if s_tc not between least(0, l.amount_tc) and greatest(0, l.amount_tc)
       or s_lc not between least(0, l.amount_lc) and greatest(0, l.amount_lc) then
        raise exception 'open item % line %: cleared amounts (tc %, lc %) are outside the line amount (tc %, lc %)',
            v_doc, v_line, s_tc, s_lc, l.amount_tc, l.amount_lc using errcode = '23514';
    end if;
    if o.cleared_amount_tc <> s_tc or o.cleared_amount_lc <> s_lc or o.cleared_amount_gc <> s_gc
       or coalesce(o.cleared_amount_rc, 0) <> coalesce(s_rc, 0) then
        raise exception 'open item % line %: the cleared amounts differ from the sum of the clearing items', v_doc, v_line using errcode = '23514';
    end if;
    v_status := case when s_tc = 0 and s_lc = 0 then 'OPEN'
                     when s_tc = l.amount_tc and s_lc = l.amount_lc then 'CLEARED' else 'PARTIAL' end;
    if o.clearing_status <> v_status then
        raise exception 'open item % line %: clearing_status is % but the clearing items give %', v_doc, v_line, o.clearing_status, v_status using errcode = '23514';
    end if;
    if o.last_clearing_id is distinct from v_last then
        raise exception 'open item % line %: last_clearing_id is not the most recent clearing of the item', v_doc, v_line using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists journal_line_open_item_cache on core.journal_line_open_item;
create constraint trigger journal_line_open_item_cache after insert or update on core.journal_line_open_item
    deferrable initially deferred for each row execute function core.check_open_item_cache();
drop trigger if exists clearing_item_cache on core.clearing_item;
create constraint trigger clearing_item_cache after insert on core.clearing_item
    deferrable initially deferred for each row execute function core.check_open_item_cache();

-- ---------------------------------------------------------------------------------------------------------
-- A clearing at commit (03, 4.1, 4.2, 5.3): its items are on its account and partner, in the currency of their document, with a
-- reporting amount where the line has one. They net to zero in local and group currency and in reporting currency, and in transaction
-- currency when they share one. A CLEAR has at least two items (the offset line of a difference document is one of them), a
-- reset at least one. The difference document is of the type that belongs to the clearing type and has the date of the clearing.
-- A reset undoes a CLEAR of the same account and partner (5.4).
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.check_clearing_content() returns trigger language plpgsql as $$
declare
    c core.clearing%rowtype; n int; ccy int; s_tc numeric; s_lc numeric; s_gc numeric; s_rc numeric; bad int; d record; r record; v_type text;
begin
    select * into c from core.clearing where tenant_id = new.tenant_id and company_id = new.company_id and clearing_id = new.clearing_id;
    select count(*), count(distinct currency_tc), sum(cleared_amount_tc), sum(cleared_amount_lc), sum(cleared_amount_gc), sum(cleared_amount_rc)
      into n, ccy, s_tc, s_lc, s_gc, s_rc
      from core.clearing_item where tenant_id = c.tenant_id and company_id = c.company_id and clearing_id = c.clearing_id;
    if n < 1 or (c.clearing_type = 'CLEAR' and n < 2) then
        raise exception 'clearing %: a % has % item(s)', c.clearing_id, c.clearing_type, n using errcode = '23514';
    end if;
    if s_lc <> 0 or s_gc <> 0 or coalesce(s_rc, 0) <> 0 or (ccy = 1 and s_tc <> 0) then
        raise exception 'clearing %: the items do not net to zero (tc %, lc %, gc %, rc %)', c.clearing_id, s_tc, s_lc, s_gc, s_rc using errcode = '23514';
    end if;
    select count(*) into bad
      from core.clearing_item i
      join core.journal_line l on l.tenant_id = i.tenant_id and l.company_id = i.company_id and l.document_id = i.open_item_document_id and l.line_no = i.open_item_line_no
      join core.journal_document d2 on d2.tenant_id = l.tenant_id and d2.company_id = l.company_id and d2.document_id = l.document_id
      join core.journal_line_open_item o on o.tenant_id = l.tenant_id and o.company_id = l.company_id and o.document_id = l.document_id and o.line_no = l.line_no
     where i.tenant_id = c.tenant_id and i.company_id = c.company_id and i.clearing_id = c.clearing_id
       and (l.account_id <> c.account_id
            or o.partner_id is distinct from c.partner_id
            or i.currency_tc <> d2.transaction_currency
            or (i.cleared_amount_rc is null) <> (l.amount_rc is null));
    if bad > 0 then
        raise exception 'clearing %: % item(s) are not on the account and partner of the clearing, or differ in currency or reporting amount from their line', c.clearing_id, bad using errcode = '23514';
    end if;
    if c.document_id is not null then
        select document_type, posting_date into d from core.journal_document
         where tenant_id = c.tenant_id and company_id = c.company_id and document_id = c.document_id;
        v_type := case when c.clearing_type = 'CLEAR' then 'CLEARING' else 'CLEARING_RESET' end;
        if d.document_type <> v_type or d.posting_date <> c.clearing_date then
            raise exception 'clearing %: the difference document must be of type % and dated %', c.clearing_id, v_type, c.clearing_date using errcode = '23514';
        end if;
    end if;
    if c.clearing_type = 'RESET' then
        select clearing_type, account_id, partner_id into r from core.clearing
         where tenant_id = c.tenant_id and company_id = c.company_id and clearing_id = c.reset_of_clearing_id;
        if r.clearing_type <> 'CLEAR' or r.account_id <> c.account_id or r.partner_id is distinct from c.partner_id then
            raise exception 'clearing %: a reset undoes a CLEAR of the same account and partner', c.clearing_id using errcode = '23514';
        end if;
    end if;
    return null;
end $$;
drop trigger if exists clearing_content on core.clearing;
create constraint trigger clearing_content after insert on core.clearing
    deferrable initially deferred for each row execute function core.check_clearing_content();
drop trigger if exists clearing_item_content on core.clearing_item;
create constraint trigger clearing_item_content after insert on core.clearing_item
    deferrable initially deferred for each row execute function core.check_clearing_content();

-- The clearing chain (03, 6, 02, 9.2): the head equals the newest clearing, the clearing links to its predecessor, sequence 1
-- starts at the genesis value (from the same text as the application's library).
create or replace function core.check_clearing_chain() returns trigger language plpgsql as $$
declare h core.chain_head%rowtype; max_seq bigint; max_hash bytea; expected_prev bytea;
begin
    select * into h from core.chain_head
     where tenant_id = new.tenant_id and company_id = new.company_id and chain_type = 'CLEARING' and scope_key = '';
    if not found then
        raise exception 'no CLEARING chain head for company %', new.company_id using errcode = '23514';
    end if;
    select chain_seq, row_hash into max_seq, max_hash from core.clearing
     where tenant_id = new.tenant_id and company_id = new.company_id order by chain_seq desc limit 1;
    if h.last_seq <> max_seq or h.last_hash <> max_hash then
        raise exception 'CLEARING chain head (seq %) does not match the newest clearing (seq %)', h.last_seq, max_seq using errcode = '23514';
    end if;
    if new.chain_seq = 1 then
        expected_prev := sha256(convert_to('genesis|' || new.tenant_id::text || '|' || new.company_id::text || '|CLEARING|', 'UTF8'));
    else
        select row_hash into expected_prev from core.clearing
         where tenant_id = new.tenant_id and company_id = new.company_id and chain_seq = new.chain_seq - 1;
    end if;
    if expected_prev is distinct from new.prev_hash then
        raise exception 'clearing % does not link to its predecessor in the chain', new.clearing_id using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists clearing_chain on core.clearing;
create constraint trigger clearing_chain after insert on core.clearing
    deferrable initially deferred for each row execute function core.check_clearing_chain();

insert into platform.schema_migration (version, description)
values ('0015', 'clearing and clearing_item, the clearing chain and role, and the cache rule of the open item S columns')
on conflict (version) do nothing;

-- ==================== 0016_open_item_status.sql ====================
-- 0016 The status of an open item looks at every currency (03, 4.3, finding F-IMPL-20). 0015 used TC and LC: OPEN at TC 0 and LC 0,
-- CLEARED at the line's TC and LC. The offset line of a difference document that arises only in group or reporting currency has TC 0 and
-- LC 0 and could never be CLEARED. Now OPEN means nothing is cleared in any currency, CLEARED that every currency of the line is
-- cleared, and the cleared group amount stays between 0 and the line amount like TC and LC.

create or replace function core.check_open_item_cache() returns trigger language plpgsql as $$
declare
    v_t uuid := new.tenant_id; v_c uuid := new.company_id; v_doc uuid; v_line int;
    l core.journal_line%rowtype; o core.journal_line_open_item%rowtype;
    s_tc numeric; s_lc numeric; s_gc numeric; s_rc numeric; n int; v_last uuid; v_status text;
begin
    if tg_table_name = 'clearing_item' then
        v_doc := new.open_item_document_id; v_line := new.open_item_line_no;
    else
        v_doc := new.document_id; v_line := new.line_no;
    end if;
    select * into o from core.journal_line_open_item where tenant_id = v_t and company_id = v_c and document_id = v_doc and line_no = v_line;
    select * into l from core.journal_line where tenant_id = v_t and company_id = v_c and document_id = v_doc and line_no = v_line;
    select coalesce(sum(cleared_amount_tc), 0), coalesce(sum(cleared_amount_lc), 0), coalesce(sum(cleared_amount_gc), 0),
           sum(cleared_amount_rc), count(*)
      into s_tc, s_lc, s_gc, s_rc, n
      from core.clearing_item
     where tenant_id = v_t and company_id = v_c and open_item_document_id = v_doc and open_item_line_no = v_line;
    select c.clearing_id into v_last from core.clearing c
      join core.clearing_item i on i.tenant_id = c.tenant_id and i.company_id = c.company_id and i.clearing_id = c.clearing_id
     where i.tenant_id = v_t and i.company_id = v_c and i.open_item_document_id = v_doc and i.open_item_line_no = v_line
     order by c.chain_seq desc limit 1;

    if s_tc not between least(0, l.amount_tc) and greatest(0, l.amount_tc)
       or s_lc not between least(0, l.amount_lc) and greatest(0, l.amount_lc)
       or s_gc not between least(0, l.amount_gc) and greatest(0, l.amount_gc) then
        raise exception 'open item % line %: cleared amounts (tc %, lc %, gc %) are outside the line amount (tc %, lc %, gc %)',
            v_doc, v_line, s_tc, s_lc, s_gc, l.amount_tc, l.amount_lc, l.amount_gc using errcode = '23514';
    end if;
    if o.cleared_amount_tc <> s_tc or o.cleared_amount_lc <> s_lc or o.cleared_amount_gc <> s_gc
       or coalesce(o.cleared_amount_rc, 0) <> coalesce(s_rc, 0) then
        raise exception 'open item % line %: the cleared amounts differ from the sum of the clearing items', v_doc, v_line using errcode = '23514';
    end if;
    -- OPEN when nothing is cleared in any currency, CLEARED when every currency of the line is cleared, else PARTIAL. TC and LC alone
    -- would not do: the offset line of a difference document that arises only in group or reporting currency has TC 0 and LC 0.
    v_status := case when s_tc = 0 and s_lc = 0 and s_gc = 0 and coalesce(s_rc, 0) = 0 then 'OPEN'
                     when s_tc = l.amount_tc and s_lc = l.amount_lc and s_gc = l.amount_gc and coalesce(s_rc, 0) = coalesce(l.amount_rc, 0) then 'CLEARED'
                     else 'PARTIAL' end;
    if o.clearing_status <> v_status then
        raise exception 'open item % line %: clearing_status is % but the clearing items give %', v_doc, v_line, o.clearing_status, v_status using errcode = '23514';
    end if;
    if o.last_clearing_id is distinct from v_last then
        raise exception 'open item % line %: last_clearing_id is not the most recent clearing of the item', v_doc, v_line using errcode = '23514';
    end if;
    return null;
end $$;

insert into platform.schema_migration (version, description)
values ('0016', 'open item status looks at every currency')
on conflict (version) do nothing;

-- ==================== 0017_business_partner.sql ====================
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

-- ==================== 0018_open_item_partner.sql ====================
-- 0018 Partners on open items (02, 5A, 07, 9). The partner and the payment terms of an open item, and the partner of a clearing, now refer to
-- their tables. A CONTROL line needs a partner (0014) and now also a due date, at posting and for good: the due date of such an open item cannot
-- be cleared by a change (06, 5.4). Whether a partner may be posted to (status, relation, restriction) is a rule of the command, not of a key.

do $$ begin
    if not exists (select 1 from pg_constraint where conname = 'journal_line_open_item_partner_fk') then
        alter table core.journal_line_open_item add constraint journal_line_open_item_partner_fk
            foreign key (tenant_id, partner_id) references core.business_partner (tenant_id, partner_id);
    end if;
    if not exists (select 1 from pg_constraint where conname = 'journal_line_open_item_terms_fk') then
        alter table core.journal_line_open_item add constraint journal_line_open_item_terms_fk
            foreign key (tenant_id, payment_terms_id) references core.payment_terms (tenant_id, payment_terms_id);
    end if;
    if not exists (select 1 from pg_constraint where conname = 'clearing_partner_fk') then
        alter table core.clearing add constraint clearing_partner_fk
            foreign key (tenant_id, partner_id) references core.business_partner (tenant_id, partner_id);
    end if;
end $$;

create or replace function core.guard_journal_line_open_item() returns trigger language plpgsql as $$
declare
    v_year int; v_period int;
    f text;
    l2 constant text[] := array['baseline_date', 'payment_terms_id', 'due_date', 'discount_due_date', 'discount_amount_tc',
                                'payment_block', 'dunning_block', 'payment_reference', 'bank_account_id'];
    s  constant text[] := array['clearing_status', 'cleared_amount_tc', 'cleared_amount_lc', 'cleared_amount_gc', 'cleared_amount_rc', 'last_clearing_id'];
begin
    if (to_jsonb(new) - l2 - s) is distinct from (to_jsonb(old) - l2 - s) then
        raise exception 'journal_line_open_item: an L1 column cannot change' using errcode = '23000';
    end if;
    if (to_jsonb(new) - l2) is distinct from (to_jsonb(old) - l2) and not pg_has_role(current_user, 'cmd_clearing', 'member') then
        raise exception 'journal_line_open_item: the S columns change only by the clearing role' using errcode = '23000';
    end if;
    -- the due date of an open item on a control account stays mandatory (02, 5A, 06, 5.4)
    if new.due_date is null and old.due_date is not null and exists (
        select 1 from core.journal_line l join core.gl_account a on a.tenant_id = l.tenant_id and a.company_id = l.company_id and a.account_id = l.account_id
         where l.tenant_id = new.tenant_id and l.company_id = new.company_id and l.document_id = new.document_id and l.line_no = new.line_no
           and a.open_item_mode = 'CONTROL') then
        raise exception 'ACCOUNT_RULE.DUE_DATE_REQUIRED: the due date of an open item on a control account cannot be cleared' using errcode = '23000';
    end if;
    select fiscal_year, fiscal_period into v_year, v_period from core.journal_line
     where tenant_id = new.tenant_id and company_id = new.company_id and document_id = new.document_id and line_no = new.line_no;
    foreach f in array l2 loop
        if (to_jsonb(new) ->> f) is distinct from (to_jsonb(old) ->> f) then
            perform core.require_l2_log(new.tenant_id, new.company_id, new.document_id, new.line_no, f,
                                        to_jsonb(old) ->> f, to_jsonb(new) ->> f, v_year, v_period, 'LINE_OPEN_ITEM');
        end if;
    end loop;
    return new;
end $$;

create or replace function core.check_open_item_rows() returns trigger language plpgsql as $$
declare
    v_type text; v_account uuid; v_mode text; v_partner uuid; v_due date; v_has boolean;
begin
    select line_type, account_id into v_type, v_account from core.journal_line
     where tenant_id = new.tenant_id and company_id = new.company_id and document_id = new.document_id and line_no = new.line_no;
    if v_type in ('REVAL_ADJUSTMENT', 'CLOSING', 'HISTORY_PL', 'OPENING') then
        return null;
    end if;
    select open_item_mode into v_mode from core.gl_account
     where tenant_id = new.tenant_id and company_id = new.company_id and account_id = v_account;
    select true, partner_id, due_date into v_has, v_partner, v_due from core.journal_line_open_item
     where tenant_id = new.tenant_id and company_id = new.company_id and document_id = new.document_id and line_no = new.line_no;
    v_has := coalesce(v_has, false);
    if v_mode = 'NONE' and v_has then
        raise exception 'journal_line_open_item: line % of document % is on an account that is not open-item managed', new.line_no, new.document_id using errcode = '23514';
    end if;
    if v_mode <> 'NONE' and not v_has then
        raise exception 'journal_line_open_item: line % of document % is on an open-item account and has no open item row', new.line_no, new.document_id using errcode = '23514';
    end if;
    if v_mode = 'CONTROL' and v_partner is null then
        raise exception 'journal_line_open_item: line % of document % is on a control account and has no partner', new.line_no, new.document_id using errcode = '23514';
    end if;
    if v_mode = 'CONTROL' and v_due is null then
        raise exception 'journal_line_open_item: line % of document % is on a control account and has no due date', new.line_no, new.document_id using errcode = '23514';
    end if;
    if v_mode = 'GL_OPEN_ITEM' and v_partner is not null then
        raise exception 'journal_line_open_item: line % of document % is on a GL open-item account, which takes no partner', new.line_no, new.document_id using errcode = '23514';
    end if;
    return null;
end $$;

insert into platform.schema_migration (version, description)
values ('0018', 'partner and payment terms of an open item refer to their tables, the due date of a control account item stays')
on conflict (version) do nothing;

-- ==================== 0019_retention_rule.sql ====================
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

-- ==================== 0020_document_legal_snapshot.sql ====================
-- 0020 document_legal_snapshot (02, 4): the legal name, address and retention date of a document, 1:1 with journal_document and outside the hash
-- (P8, ADR-008). Every document has one, whoever writes it (posting, reversal, clearing difference document, reset mirror): the deferred trigger
-- below is the backstop. The personal columns stay empty until the header partner exists (step 3): the snapshot of a document without a partner
-- carries only its retention date. retention_until comes from the retention rules at creation (12, 4.1, consistency rule). The table is insert-only
-- here: the retention job (overwrite of the L1* columns, anonymised_at) and retention:extend (retention_until) come with their own commands.

create table if not exists core.document_legal_snapshot (
    tenant_id                 uuid not null,
    company_id                uuid not null,
    document_id               uuid not null,
    partner_id                uuid,
    legal_name                varchar(200),
    address_line_1            varchar(200),
    address_line_2            varchar(200),
    postal_code               varchar(20),
    city                      varchar(100),
    country_code              char(2) references platform.ref_country (country_code),
    partner_vat_id            varchar(30),
    electronic_address        varchar(100),
    electronic_address_scheme core.code,
    legal_registration_id     varchar(50),
    anonymised_at             timestamptz,
    retention_until           date not null,
    primary key (tenant_id, company_id, document_id),
    foreign key (tenant_id, company_id, document_id) references core.journal_document (tenant_id, company_id, document_id),
    foreign key (tenant_id, partner_id) references core.business_partner (tenant_id, partner_id)
);

alter table core.document_legal_snapshot enable row level security;
alter table core.document_legal_snapshot force row level security;
do $$ begin
    if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = 'document_legal_snapshot' and policyname = 'tenant_isolation') then
        create policy tenant_isolation on core.document_legal_snapshot
            using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id());
    end if;
end $$;
revoke all on core.document_legal_snapshot from public;
grant select on core.document_legal_snapshot to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_retention;
grant insert on core.document_legal_snapshot to cmd_post, cmd_clearing;
drop trigger if exists document_legal_snapshot_append_only on core.document_legal_snapshot;
create trigger document_legal_snapshot_append_only before update or delete on core.document_legal_snapshot
    for each row execute function core.reject_change();
drop trigger if exists document_legal_snapshot_no_truncate on core.document_legal_snapshot;
create trigger document_legal_snapshot_no_truncate before truncate on core.document_legal_snapshot
    for each statement execute function core.reject_change();

-- Backstop: a document without a snapshot cannot be committed (02, 4: 1:1)
create or replace function core.check_document_has_snapshot() returns trigger language plpgsql as $$
begin
    if not exists (select 1 from core.document_legal_snapshot where tenant_id = new.tenant_id and company_id = new.company_id
                   and document_id = new.document_id) then
        raise exception 'document % has no legal snapshot', new.document_id using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists journal_document_has_snapshot on core.journal_document;
create constraint trigger journal_document_has_snapshot after insert on core.journal_document
    deferrable initially deferred for each row execute function core.check_document_has_snapshot();

insert into platform.schema_migration (version, description)
values ('0020', 'document_legal_snapshot, one per document')
on conflict (version) do nothing;

-- ==================== 0021_posting_reads_partner.sql ====================
-- 0021 The posting role reads the partner for the legal snapshot of a document (02, 4, 06, 5.2): the name, address, country, electronic address and
-- registration id of business_partner_pii. Column grants: the contact data (email, phone, contact person) stays out of reach of the posting role.

-- 0006 and 0015 grant select on all tables of the schema to these roles. They did not reach this table on a fresh database (it came later in 0017),
-- but they do when the schema file is applied again over an existing database, and a table grant would make the column grant below meaningless.
-- Found on Supabase (F-IMPL-32): the posting role could read the contact data.
revoke all on core.business_partner_pii from cmd_period, cmd_post, cmd_clearing;

grant select (tenant_id, partner_id, legal_name, address_line_1, address_line_2, postal_code, city, country_code,
              electronic_address, electronic_address_scheme, legal_registration_id)
    on core.business_partner_pii to cmd_post;

insert into platform.schema_migration (version, description)
values ('0021', 'the posting role reads the partner for the legal snapshot')
on conflict (version) do nothing;
