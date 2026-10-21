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
