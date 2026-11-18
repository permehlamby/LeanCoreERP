-- 0032 migration batches (13): the batch, its control totals, its staged records, its reconciliation runs and its commit chunks, the migration command role and the
-- link from a document to its batch. First slice: the opening balances and the open items of the general ledger. History, stock, orders and the legacy tax summary
-- are not built and have no table here (legacy_id_map and legacy_tax_summary come with them).

do $$ begin
    if not exists (select 1 from pg_roles where rolname = 'cmd_migration') then
        create role cmd_migration nologin;
    end if;
end $$;
grant cmd_migration to core_app;
do $$ begin
    if exists (select 1 from pg_roles where rolname = 'postgres') then
        grant cmd_migration to postgres;
    end if;
end $$;
grant usage on schema platform, core to cmd_migration;

alter table core.tenant_setting add column if not exists migration_commit_chunk_documents int not null default 20000 check (migration_commit_chunk_documents >= 1);
alter table core.tenant_setting add column if not exists migration_document_max_lines int not null default 5000 check (migration_document_max_lines >= 2);

create table if not exists core.migration_batch (
    tenant_id           uuid not null,
    batch_id            uuid not null default gen_random_uuid(),
    company_id          uuid not null,
    cutover_date        date not null,
    source_system       core.code not null,
    status              core.code not null default 'OPEN'
                        check (status in ('OPEN', 'VALIDATED', 'RECONCILED', 'COMMITTING', 'COMMITTED', 'SEALED', 'ABORTED', 'ROLLED_BACK')),   -- S
    opened_by           varchar(100) not null,
    opened_at           timestamptz not null default now(),
    committed_at        timestamptz,                  -- S
    committed_by        varchar(100),                 -- S
    sealed_at           timestamptz,                  -- S
    sealed_by           varchar(100),                 -- S
    accepted_reconciliation_id uuid,                  -- S
    correlation_id      varchar(100) not null,
    idempotency_key     varchar(100) not null,
    row_version         int not null default 1,
    primary key (tenant_id, batch_id),
    unique (tenant_id, idempotency_key),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id)
);
-- one batch per company that is not ABORTED and not ROLLED_BACK (D6)
create unique index if not exists migration_batch_active on core.migration_batch (tenant_id, company_id) where status not in ('ABORTED', 'ROLLED_BACK');

create table if not exists core.migration_control_total (
    tenant_id     uuid not null,
    batch_id      uuid not null,
    control_no    int not null check (control_no >= 1),
    control_kind  core.code not null check (control_kind in ('TB_BY_ACCOUNT', 'PL_YTD_BY_ACCOUNT', 'HISTORY_PL_BY_PERIOD_ACCOUNT', 'OPEN_ITEMS_BY_ACCOUNT', 'STOCK_VALUE_BY_ACCOUNT',
                                                           'STOCK_QTY_BY_ITEM', 'OPEN_ORDER_LINES', 'DELIVERED_NOT_INVOICED', 'RECEIVED_NOT_INVOICED')),
    key_1         varchar(100),
    key_2         varchar(100),
    amount_lc     core.amt,
    amount_gc     core.amt,
    amount_rc     core.amt,
    source_report_ref varchar(200),
    declared_at   timestamptz not null default now(),
    declared_by   varchar(100) not null,
    primary key (tenant_id, batch_id, control_no),
    foreign key (tenant_id, batch_id) references core.migration_batch (tenant_id, batch_id)
);

create table if not exists core.migration_record (
    tenant_id      uuid not null,
    batch_id       uuid not null,
    record_no      bigint not null check (record_no >= 1),
    record_type    core.code not null,
    legacy_ref     varchar(100) not null,
    payload        jsonb not null,
    payload_digest core.hash32 not null,
    status         core.code not null check (status in ('RECEIVED', 'VALID', 'INVALID')),
    errors         jsonb,
    received_at    timestamptz not null default now(),
    received_by    varchar(100) not null,
    superseded     boolean not null default false,        -- S
    primary key (tenant_id, batch_id, record_no),
    foreign key (tenant_id, batch_id) references core.migration_batch (tenant_id, batch_id)
);
create index if not exists migration_record_ref on core.migration_record (tenant_id, batch_id, record_type, legacy_ref, record_no);

create table if not exists core.migration_reconciliation (
    tenant_id         uuid not null,
    batch_id          uuid not null,
    reconciliation_id uuid not null,
    check_no          int not null check (check_no >= 1),
    check_code        core.code not null,
    key               varchar(100),
    declared_lc       core.amt, declared_gc core.amt, declared_rc core.amt,
    computed_lc       core.amt, computed_gc core.amt, computed_rc core.amt,
    difference_lc     core.amt, difference_gc core.amt, difference_rc core.amt,
    result            core.code not null check (result in ('PASS', 'FAIL', 'WARNING')),
    detail            varchar(500),
    run_at            timestamptz not null default now(),
    run_by            varchar(100) not null,
    primary key (tenant_id, batch_id, reconciliation_id, check_no),
    foreign key (tenant_id, batch_id) references core.migration_batch (tenant_id, batch_id)
);

create table if not exists core.migration_commit_chunk (
    tenant_id      uuid not null,
    batch_id       uuid not null,
    chunk_no       int not null check (chunk_no >= 1),
    area           core.code not null check (area in ('HISTORY', 'OPENING_GL', 'OPEN_ITEMS', 'STOCK', 'ORDERS', 'FULFILMENT', 'RESERVATIONS', 'LEGACY_TAX')),
    document_count int not null default 0,
    status         core.code not null default 'PENDING' check (status in ('PENDING', 'DONE', 'FAILED')),   -- S
    failure_codes  varchar(500),
    started_at     timestamptz,
    completed_at   timestamptz,
    primary key (tenant_id, batch_id, chunk_no),
    foreign key (tenant_id, batch_id) references core.migration_batch (tenant_id, batch_id)
);

-- A migration document knows its batch, and only a migration document does (02, 3, X5).
do $$ begin
    if not exists (select 1 from pg_constraint where conname = 'journal_document_migration_batch_fk') then
        alter table core.journal_document add constraint journal_document_migration_batch_fk
            foreign key (tenant_id, migration_batch_id) references core.migration_batch (tenant_id, batch_id);
        alter table core.journal_document add constraint journal_document_migration_origin_check
            check ((origin = 'MIGRATION') = (migration_batch_id is not null));
    end if;
end $$;

-- The S columns of a batch move forward along the statuses of 13, 3.1. Nothing else changes.
create or replace function core.guard_migration_batch() returns trigger language plpgsql as $$
begin
    if (to_jsonb(new) - array['status', 'committed_at', 'committed_by', 'sealed_at', 'sealed_by', 'accepted_reconciliation_id', 'row_version'])
       is distinct from (to_jsonb(old) - array['status', 'committed_at', 'committed_by', 'sealed_at', 'sealed_by', 'accepted_reconciliation_id', 'row_version']) then
        raise exception 'migration_batch: an L1 column cannot change' using errcode = '23000';
    end if;
    if new.status is distinct from old.status and not (
        (old.status = 'OPEN' and new.status in ('VALIDATED', 'RECONCILED', 'ABORTED')) or
        (old.status = 'VALIDATED' and new.status in ('OPEN', 'RECONCILED', 'ABORTED')) or
        (old.status = 'RECONCILED' and new.status in ('OPEN', 'VALIDATED', 'COMMITTING', 'ABORTED')) or
        (old.status = 'COMMITTING' and new.status in ('COMMITTED', 'ABORTED', 'ROLLED_BACK')) or
        (old.status = 'COMMITTED' and new.status in ('SEALED', 'ROLLED_BACK'))) then
        raise exception 'migration_batch: the status cannot change from % to %', old.status, new.status using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists migration_batch_guard on core.migration_batch;
create trigger migration_batch_guard before update on core.migration_batch
    for each row execute function core.guard_migration_batch();

-- A staged record is replaced by a later one of the same type and legacy_ref: only the S column superseded changes.
create or replace function core.guard_migration_record() returns trigger language plpgsql as $$
begin
    if (to_jsonb(new) - 'superseded') is distinct from (to_jsonb(old) - 'superseded') or (old.superseded and not new.superseded) then
        raise exception 'migration_record: only superseded changes, and only to true' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists migration_record_guard on core.migration_record;
create trigger migration_record_guard before update on core.migration_record
    for each row execute function core.guard_migration_record();

do $$
declare t text;
begin
    foreach t in array array['migration_batch', 'migration_control_total', 'migration_record', 'migration_reconciliation', 'migration_commit_chunk'] loop
        execute format('alter table core.%I enable row level security', t);
        execute format('alter table core.%I force row level security', t);
        if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = t and policyname = 'tenant_isolation') then
            execute format('create policy tenant_isolation on core.%I using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id())', t);
        end if;
        execute format('revoke all on core.%I from public', t);
        execute format('grant select on core.%I to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_closing, cmd_retention, cmd_migration', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_delete', t);
        execute format('create trigger %I before delete on core.%I for each row execute function core.reject_delete()', t || '_no_delete', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
        execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_delete()', t || '_no_truncate', t);
    end loop;
    foreach t in array array['migration_control_total', 'migration_reconciliation'] loop
        execute format('drop trigger if exists %I on core.%I', t || '_append_only', t);
        execute format('create trigger %I before update on core.%I for each row execute function core.reject_change()', t || '_append_only', t);
    end loop;
end $$;

-- The migration role: its own tables, documents with their lines and open item rows, the number, the LEDGER head, the outbox and the anchor requests.
grant select on all tables in schema core to cmd_migration;
grant select on all tables in schema platform to cmd_migration;
revoke all on core.business_partner_pii from cmd_migration;
grant insert on core.migration_batch, core.migration_control_total, core.migration_record, core.migration_reconciliation, core.migration_commit_chunk,
      core.journal_document, core.journal_line, core.journal_line_open_item, core.document_legal_snapshot, core.command_log, core.command_log_result,
      core.outbox_event, core.chain_head, core.chain_anchor to cmd_migration;
grant update (status, committed_at, committed_by, sealed_at, sealed_by, accepted_reconciliation_id, row_version) on core.migration_batch to cmd_migration;
grant update (superseded) on core.migration_record to cmd_migration;
grant update (status, failure_codes, document_count, started_at, completed_at) on core.migration_commit_chunk to cmd_migration;
grant update (last_number, updated_at) on core.number_range_counter to cmd_migration;
grant update (last_seq, last_hash, hash_version, updated_at) on core.chain_head to cmd_migration;
grant update (last_position) on core.outbox_head to cmd_migration;
grant update (last_event_id) on core.fiscal_period, core.fiscal_year to cmd_migration;   -- a share lock on the period rows needs one column (0007)

drop policy if exists chain_head_migration on core.chain_head;
create policy chain_head_migration on core.chain_head as restrictive for update to cmd_migration
    using (chain_type = 'LEDGER') with check (chain_type = 'LEDGER');

insert into platform.schema_migration (version, description)
values ('0032', 'migration batches: batch, control totals, staged records, reconciliation, commit chunks, the migration role')
on conflict (version) do nothing;
