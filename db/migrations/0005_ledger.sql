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
