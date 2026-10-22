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
