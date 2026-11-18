-- 0029 revaluation, year-end and anchors (05, 02 9.5): the closing command role, the lock function for chain heads (F-IMPL-13), chain_anchor and the three
-- tables of a revaluation run. The closing role posts the REVALUATION, REVAL_REVERSAL and YEAR_END documents and, for year-end, changes the status of the
-- closing period and the year with the period events. Hashes are computed by the application (ADR-211), SQL checks the links.

-- ---------------------------------------------------------------------------------------------------------
-- The closing command role. Like the clearing role it is not a member of cmd_post or cmd_period (their restrictive policies of 0006 would both apply).
-- It moves the LEDGER head (its documents) and the PERIOD_EVENT head (year-end), and locks the other heads through core.lock_chain_heads.
-- ---------------------------------------------------------------------------------------------------------
do $$ begin
    if not exists (select 1 from pg_roles where rolname = 'cmd_closing') then
        create role cmd_closing nologin;
    end if;
end $$;
grant cmd_closing to core_app;
do $$ begin
    if exists (select 1 from pg_roles where rolname = 'postgres') then
        grant cmd_closing to postgres;
    end if;
end $$;
grant usage on schema platform, core to cmd_closing;

-- ---------------------------------------------------------------------------------------------------------
-- The status guard of 0006 read new.period_no also for fiscal_year, which has no such column, so no status change of a year could ever pass
-- (found when year-end:close first changed one). Same checks, with the period_no test only on fiscal_period.
-- ---------------------------------------------------------------------------------------------------------
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
        if ev.company_id <> new.company_id or ev.fiscal_year <> new.fiscal_year or ev.to_status <> new.status or ev.event_seq <> latest then
            raise exception '%: the period_event does not match the status change', tg_table_name using errcode = '23000';
        end if;
        if tg_table_name = 'fiscal_period' then
            if ev.period_no is distinct from (to_jsonb(new) ->> 'period_no')::int then
                raise exception 'fiscal_period: the period_event does not match the status change' using errcode = '23000';
            end if;
        end if;
    end if;
    return new;
end $$;

-- ---------------------------------------------------------------------------------------------------------
-- chain_anchor (02, 9.5)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.chain_anchor (
    tenant_id        uuid not null,
    anchor_id        uuid not null default gen_random_uuid(),
    company_id       uuid not null,
    chain_type       core.code not null check (chain_type in ('LEDGER', 'CLEARING', 'PERIOD_EVENT', 'VAT_RETURN', 'STOCK', 'FULFILMENT')),
    scope_key        varchar(50) not null,
    chain_seq        bigint not null check (chain_seq >= 0),
    row_hash         core.hash32 not null,
    anchored_at      timestamptz not null,
    anchor_digest    core.hash32 not null,
    anchor_state     core.code not null default 'PENDING' check (anchor_state in ('PENDING', 'STAMPED')),   -- S
    timestamp_token  bytea,                                                                                  -- S
    tsa_id           varchar(100),                                                                           -- S
    stamped_at       timestamptz,                                                                            -- S
    anchor_ref       varchar(200),                                                                           -- S
    primary key (tenant_id, anchor_id),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    check ((anchor_state = 'STAMPED') = (timestamp_token is not null and tsa_id is not null and stamped_at is not null))
);
create index if not exists chain_anchor_chain on core.chain_anchor (tenant_id, company_id, chain_type, scope_key, chain_seq);

-- The S columns change once, PENDING to STAMPED, and nothing else changes (02, 10). No role has the update grant yet: the job that stamps comes later.
create or replace function core.guard_chain_anchor() returns trigger language plpgsql as $$
begin
    if tg_op = 'DELETE' then
        raise exception 'core.chain_anchor has no hard delete (ADR-073)' using errcode = '23000';
    end if;
    if (to_jsonb(new) - array['anchor_state', 'timestamp_token', 'tsa_id', 'stamped_at', 'anchor_ref'])
       is distinct from (to_jsonb(old) - array['anchor_state', 'timestamp_token', 'tsa_id', 'stamped_at', 'anchor_ref']) then
        raise exception 'chain_anchor: an L1 column cannot change' using errcode = '23000';
    end if;
    if not (old.anchor_state = 'PENDING' and new.anchor_state = 'STAMPED') then
        raise exception 'chain_anchor: the state changes once, from PENDING to STAMPED' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists chain_anchor_guard on core.chain_anchor;
create trigger chain_anchor_guard before update or delete on core.chain_anchor
    for each row execute function core.guard_chain_anchor();
drop trigger if exists chain_anchor_no_truncate on core.chain_anchor;
create trigger chain_anchor_no_truncate before truncate on core.chain_anchor
    for each statement execute function core.reject_delete();

-- ---------------------------------------------------------------------------------------------------------
-- revaluation_run, revaluation_run_rate, revaluation_run_item (05, 3.5)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.revaluation_run (
    tenant_id            uuid not null,
    company_id           uuid not null,
    run_id               uuid not null default gen_random_uuid(),
    fiscal_year          int not null,
    fiscal_period        int not null,
    valuation_date       date not null,
    run_no               int not null check (run_no >= 1),
    supersedes_run_id    uuid,
    status               core.code not null default 'ACTIVE' check (status in ('ACTIVE', 'SUPERSEDED')),   -- S
    superseded_by_run_id uuid,                                                                             -- S
    document_id          uuid,
    reversal_document_id uuid,
    item_count           int not null check (item_count >= 0),
    items_digest         core.hash32 not null,
    run_at               timestamptz not null,
    run_by               varchar(100) not null,
    correlation_id       varchar(100) not null,
    idempotency_key      varchar(100) not null,
    primary key (tenant_id, company_id, run_id),
    unique (tenant_id, idempotency_key),
    foreign key (tenant_id, company_id, fiscal_year, fiscal_period) references core.fiscal_period (tenant_id, company_id, fiscal_year, period_no),
    foreign key (tenant_id, company_id, document_id) references core.journal_document (tenant_id, company_id, document_id),
    foreign key (tenant_id, company_id, reversal_document_id) references core.journal_document (tenant_id, company_id, document_id),
    foreign key (tenant_id, company_id, supersedes_run_id) references core.revaluation_run (tenant_id, company_id, run_id),
    -- The old run is set SUPERSEDED before the new row exists (05, 3.6, step 0), so this key is checked at commit
    foreign key (tenant_id, company_id, superseded_by_run_id) references core.revaluation_run (tenant_id, company_id, run_id) deferrable initially deferred,
    check ((status = 'SUPERSEDED') = (superseded_by_run_id is not null)),
    check ((document_id is null) = (reversal_document_id is null))
);
create unique index if not exists revaluation_run_active on core.revaluation_run (tenant_id, company_id, fiscal_year, fiscal_period) where status = 'ACTIVE';

create table if not exists core.revaluation_run_rate (
    tenant_id     uuid not null,
    company_id    uuid not null,
    run_id        uuid not null,
    from_currency char(3) not null references platform.ref_currency (currency_code),
    to_currency   char(3) not null references platform.ref_currency (currency_code),
    rate          core.rate not null check (rate > 0),
    rate_date     date not null,
    primary key (tenant_id, company_id, run_id, from_currency, to_currency),
    foreign key (tenant_id, company_id, run_id) references core.revaluation_run (tenant_id, company_id, run_id)
);

create table if not exists core.revaluation_run_item (
    tenant_id              uuid not null,
    company_id             uuid not null,
    run_id                 uuid not null,
    item_no                int not null check (item_no >= 1),
    basis_type             core.code not null check (basis_type in ('OPEN_ITEM', 'ACCOUNT_BALANCE')),
    account_id             uuid not null,
    partner_id             uuid,
    ic_counterpart_company_id uuid,
    currency_tc            char(3) not null references platform.ref_currency (currency_code),
    open_item_document_id  uuid,
    open_item_line_no      int,
    remaining_tc           core.amt not null,
    carried_lc             core.amt not null,
    carried_gc             core.amt not null,
    carried_rc             core.amt,
    revalued_lc            core.amt not null,
    revalued_gc            core.amt not null,
    revalued_rc            core.amt,
    adjustment_lc          core.amt not null,
    adjustment_gc          core.amt not null,
    adjustment_rc          core.amt,
    adjustment_line_no     int,
    primary key (tenant_id, company_id, run_id, item_no),
    foreign key (tenant_id, company_id, run_id) references core.revaluation_run (tenant_id, company_id, run_id),
    foreign key (tenant_id, company_id, account_id) references core.gl_account (tenant_id, company_id, account_id),
    check ((basis_type = 'OPEN_ITEM') = (open_item_document_id is not null)),
    check ((open_item_document_id is null) = (open_item_line_no is null))
);
create index if not exists revaluation_run_item_open_item on core.revaluation_run_item (tenant_id, company_id, open_item_document_id, open_item_line_no);
create index if not exists revaluation_run_item_partner on core.revaluation_run_item (tenant_id, company_id, partner_id, run_id);

-- S columns of a run (05, 6): ACTIVE to SUPERSEDED once, with the id of the new run. Nothing else changes.
create or replace function core.guard_revaluation_run() returns trigger language plpgsql as $$
begin
    if (to_jsonb(new) - array['status', 'superseded_by_run_id']) is distinct from (to_jsonb(old) - array['status', 'superseded_by_run_id']) then
        raise exception 'revaluation_run: an L1 column cannot change' using errcode = '23000';
    end if;
    if not (old.status = 'ACTIVE' and new.status = 'SUPERSEDED') then
        raise exception 'revaluation_run: the status changes once, from ACTIVE to SUPERSEDED' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists revaluation_run_guard on core.revaluation_run;
create trigger revaluation_run_guard before update on core.revaluation_run
    for each row execute function core.guard_revaluation_run();

do $$
declare t text;
begin
    foreach t in array array['chain_anchor', 'revaluation_run', 'revaluation_run_rate', 'revaluation_run_item'] loop
        execute format('alter table core.%I enable row level security', t);
        execute format('alter table core.%I force row level security', t);
        if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = t and policyname = 'tenant_isolation') then
            execute format('create policy tenant_isolation on core.%I using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id())', t);
        end if;
        execute format('revoke all on core.%I from public', t);
        execute format('grant select on core.%I to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_closing', t);
    end loop;
    foreach t in array array['revaluation_run', 'revaluation_run_rate', 'revaluation_run_item'] loop
        execute format('drop trigger if exists %I on core.%I', t || '_no_delete', t);
        execute format('create trigger %I before delete on core.%I for each row execute function core.reject_delete()', t || '_no_delete', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
        execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_delete()', t || '_no_truncate', t);
    end loop;
    foreach t in array array['revaluation_run_rate', 'revaluation_run_item'] loop
        execute format('drop trigger if exists %I on core.%I', t || '_append_only', t);
        execute format('create trigger %I before update on core.%I for each row execute function core.reject_change()', t || '_append_only', t);
    end loop;
end $$;

-- ---------------------------------------------------------------------------------------------------------
-- Grants. The closing role: documents, lines, dimensions, snapshot, number, LEDGER and PERIOD_EVENT heads, period events, the period and year
-- status, anchors, the run tables. The period role writes anchor requests at a hard-close.
-- ---------------------------------------------------------------------------------------------------------
grant select on all tables in schema core to cmd_closing;
grant select on all tables in schema platform to cmd_closing;
revoke all on core.business_partner_pii from cmd_closing;   -- the closing role posts no partner document (F-IMPL-32)
grant insert on core.journal_document, core.journal_line, core.journal_line_dimension, core.document_legal_snapshot, core.command_log,
      core.command_log_result, core.outbox_event, core.chain_head, core.period_event, core.chain_anchor,
      core.revaluation_run, core.revaluation_run_rate, core.revaluation_run_item to cmd_closing;
grant update (status, reversed_by_document_id) on core.journal_document to cmd_closing;
grant update (last_number, updated_at) on core.number_range_counter to cmd_closing;
grant update (last_seq, last_hash, hash_version, updated_at) on core.chain_head to cmd_closing;
grant update (last_position) on core.outbox_head to cmd_closing;
grant update (status, last_event_id) on core.fiscal_period to cmd_closing;
grant update (status, year_end_document_id, last_event_id) on core.fiscal_year to cmd_closing;
grant update (status, superseded_by_run_id) on core.revaluation_run to cmd_closing;
grant update (first_used_at) on core.exchange_rate to cmd_closing;   -- a closing rate that a run used never changes (ADR-019)
grant insert on core.chain_anchor to cmd_period;

drop policy if exists chain_head_closing on core.chain_head;
create policy chain_head_closing on core.chain_head as restrictive for update to cmd_closing
    using (chain_type in ('LEDGER', 'PERIOD_EVENT')) with check (chain_type in ('LEDGER', 'PERIOD_EVENT'));

-- ---------------------------------------------------------------------------------------------------------
-- Locking the heads that a command does not move (F-IMPL-13, decided 2026-11-11: a read-only lock function). A command role may lock
-- only its own chain (0006), so hard-close and year-end take the LEDGER and CLEARING heads through these functions. They lock and return
-- nothing and are owned by the migration role, so the restrictive policies do not apply inside them. They read the tenant of the transaction.
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.lock_chain_heads(p_company_id uuid) returns void language plpgsql security definer set search_path = core, pg_temp as $$
begin
    -- 04, 6.3: LEDGER, then CLEARING. A company without a head has nothing to lock.
    perform 1 from core.chain_head h
     where h.tenant_id = core.current_tenant_id() and h.company_id = p_company_id and h.chain_type = 'LEDGER'
     order by h.scope_key for update;
    perform 1 from core.chain_head h
     where h.tenant_id = core.current_tenant_id() and h.company_id = p_company_id and h.chain_type = 'CLEARING'
     order by h.scope_key for update;
end $$;

create or replace function core.lock_anchor_heads(p_company_id uuid) returns void language plpgsql security definer set search_path = core, pg_temp as $$
begin
    -- after the PERIOD_EVENT head: the heads that anchors read, in share mode (02, 9.5)
    perform 1 from core.chain_head h
     where h.tenant_id = core.current_tenant_id() and h.company_id = p_company_id and h.chain_type in ('VAT_RETURN', 'STOCK', 'FULFILMENT')
     order by h.chain_type, h.scope_key for share;
end $$;

revoke all on function core.lock_chain_heads(uuid), core.lock_anchor_heads(uuid) from public;
grant execute on function core.lock_chain_heads(uuid), core.lock_anchor_heads(uuid) to cmd_period, cmd_closing;

insert into platform.schema_migration (version, description)
values ('0029', 'revaluation, year-end and anchors: the closing role, the head lock functions, chain_anchor and the revaluation run tables')
on conflict (version) do nothing;
