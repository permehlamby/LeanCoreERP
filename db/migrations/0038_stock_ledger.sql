-- 0038 the stock ledger, first slice (10, 4, 5, 6): stock_document (a chain node of the STOCK chain) and stock_movement, the stock command role, and the chain check. First slice:
-- moving average, items without tracking, receipts, issues and adjustments in the company currency. The tables are complete for the rest of 10 (layers, status changes, transfers,
-- returns, cost adjustments, reversals and the reservation of 4.5 come with their commands): columns that those need exist and stay empty. Every stock document that has value
-- posts its journal in the same transaction as the cmd_stock role, through the normal finance tables (a system document, origin STOCK, 10, 7).

do $$ begin
    if not exists (select 1 from pg_roles where rolname = 'cmd_stock') then
        create role cmd_stock nologin;
    end if;
end $$;
grant cmd_stock to core_app;
do $$ begin
    if exists (select 1 from pg_roles where rolname = 'postgres') then
        grant cmd_stock to postgres;
    end if;
end $$;
grant usage on schema platform, core to cmd_stock;

-- ---------------------------------------------------------------------------------------------------------
-- stock_document (10, 4.1)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.stock_document (
    tenant_id                       uuid not null references platform.tenant (tenant_id),
    company_id                      uuid not null,
    stock_document_id               uuid not null default gen_random_uuid(),
    document_type                   core.code not null check (document_type in (
        'STOCK_RECEIPT', 'STOCK_ISSUE', 'STOCK_TRANSFER', 'STOCK_ADJUSTMENT', 'STOCK_STATUS_CHANGE', 'STOCK_COST_ADJUSTMENT', 'STOCK_RETURN', 'STOCK_OPENING', 'STOCK_REVERSAL')),
    number_series_id                uuid not null,
    fiscal_year                     int not null,
    sequence_no                     bigint not null,
    document_number                 varchar(50) not null,
    fiscal_period                   int not null,
    document_date                   date not null,
    posting_date                    date not null,
    posted_at                       timestamptz not null,
    reason_code                     core.code,
    status                          core.code not null default 'posted' check (status in ('posted', 'reversed')),     -- S, outside the hash
    reversal_of_stock_document_id   uuid,
    reversed_by_stock_document_id   uuid,                                                                             -- S
    journal_document_id             uuid,
    soft_close_override             boolean not null,
    currency_tc                     char(3) references platform.ref_currency (currency_code),
    source_system                   core.code not null,
    source_document_ref             varchar(100),
    idempotency_key                 varchar(100) not null,
    command_name                    core.code not null,
    posted_by                       varchar(100) not null,
    correlation_id                  varchar(100) not null,
    header_text                     varchar(200),                                                                     -- L2
    movement_count                  int not null check (movement_count >= 1),
    detail_digest                   core.hash32,
    movement_digest                 core.hash32 not null,
    chain_seq                       bigint not null check (chain_seq >= 1),
    prev_hash                       core.hash32 not null,
    row_hash                        core.hash32 not null,
    hash_version                    smallint not null check (hash_version = 2),
    primary key (tenant_id, company_id, stock_document_id),
    unique (tenant_id, company_id, number_series_id, fiscal_year, sequence_no),
    unique (tenant_id, idempotency_key),
    unique (tenant_id, company_id, chain_seq),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, company_id, journal_document_id) references core.journal_document (tenant_id, company_id, document_id),
    foreign key (tenant_id, company_id, reversal_of_stock_document_id) references core.stock_document (tenant_id, company_id, stock_document_id),
    foreign key (tenant_id, company_id, reversed_by_stock_document_id) references core.stock_document (tenant_id, company_id, stock_document_id),
    check ((document_type = 'STOCK_REVERSAL') = (reversal_of_stock_document_id is not null))
);
create index if not exists stock_document_posting on core.stock_document (tenant_id, company_id, posting_date, chain_seq);

-- ---------------------------------------------------------------------------------------------------------
-- stock_movement (10, 4.2). chain_seq is the chain_seq of the document, copied (C1: the position of a movement is (chain_seq, line_no)).
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.stock_movement (
    tenant_id                  uuid not null,
    company_id                 uuid not null,
    stock_document_id          uuid not null,
    line_no                    int not null check (line_no >= 1),
    chain_seq                  bigint not null check (chain_seq >= 1),
    movement_type              core.code not null check (movement_type in (
        'RECEIPT', 'ISSUE', 'TRANSFER_OUT', 'TRANSFER_IN', 'ADJUST_IN', 'ADJUST_OUT', 'STATUS_OUT', 'STATUS_IN', 'COST_ADJUST', 'RETURN_IN', 'RETURN_OUT', 'OPENING_IN')),
    cost_source                core.code not null check (cost_source in ('COMPUTED', 'SUPPLIED', 'INVOICE', 'MIGRATION')),
    valuation_ref_document_id  uuid,
    valuation_ref_line_no      int,
    posting_date               date not null,
    item_id                    uuid not null,
    warehouse_id               uuid not null,
    location_id                uuid,
    tracking_id                uuid,
    stock_status               core.code not null default 'AVAILABLE' check (stock_status in ('AVAILABLE', 'QUARANTINE', 'BLOCKED')),
    quantity                   numeric(19,6) not null,
    unit_code                  varchar(3) not null references platform.ref_unit (unit_code),
    base_quantity              numeric(19,6) not null,
    value_lc                   numeric(19,4) not null,
    unit_cost_lc               core.rate,
    value_tc                   numeric(19,4),
    currency_tc                char(3) references platform.ref_currency (currency_code),
    cost_warehouse_id          uuid,
    cost_qty_after             numeric(19,6) not null,
    cost_value_after_lc        numeric(19,4) not null,
    stock_qty_after            numeric(19,6) not null,
    lot_expired                boolean not null default false,
    count_ref                  varchar(100),
    expected_book_quantity     numeric(19,6),
    source_line_ref            varchar(100),
    reversal_of_line_no        int,
    line_text                  varchar(200),                                                                          -- L2
    primary key (tenant_id, company_id, stock_document_id, line_no),
    foreign key (tenant_id, company_id, stock_document_id) references core.stock_document (tenant_id, company_id, stock_document_id),
    foreign key (tenant_id, item_id) references core.item (tenant_id, item_id),
    foreign key (tenant_id, company_id, warehouse_id) references core.warehouse (tenant_id, company_id, warehouse_id),
    foreign key (tenant_id, company_id, warehouse_id, location_id) references core.warehouse_location (tenant_id, company_id, warehouse_id, location_id),
    check ((value_tc is null) = (currency_tc is null))
);
-- the balance lookups of 6.3: the highest position of the cost key and of the stock key
create index if not exists stock_movement_cost_key on core.stock_movement (tenant_id, company_id, item_id, cost_warehouse_id, tracking_id, chain_seq desc, line_no desc);
create index if not exists stock_movement_stock_key on core.stock_movement
    (tenant_id, company_id, item_id, warehouse_id, location_id, tracking_id, stock_status, chain_seq desc, line_no desc);
create index if not exists stock_movement_ledger on core.stock_movement (tenant_id, company_id, posting_date, item_id);

-- ---------------------------------------------------------------------------------------------------------
-- Guards: a movement never changes, a stock document changes only its S columns and the header text
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.guard_stock_document() returns trigger language plpgsql as $$
begin
    if (to_jsonb(new) - array['status', 'reversed_by_stock_document_id', 'header_text'])
       is distinct from (to_jsonb(old) - array['status', 'reversed_by_stock_document_id', 'header_text']) then
        raise exception 'stock_document: an L1 column cannot change' using errcode = '23000';
    end if;
    if new.status is distinct from old.status and not (old.status = 'posted' and new.status = 'reversed') then
        raise exception 'stock_document: status can only change from posted to reversed' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists stock_document_guard on core.stock_document;
create trigger stock_document_guard before update on core.stock_document for each row execute function core.guard_stock_document();

create or replace function core.guard_stock_movement() returns trigger language plpgsql as $$
begin
    if (to_jsonb(new) - 'line_text') is distinct from (to_jsonb(old) - 'line_text') then
        raise exception 'stock_movement: an L1 column cannot change' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists stock_movement_guard on core.stock_movement;
create trigger stock_movement_guard before update on core.stock_movement for each row execute function core.guard_stock_movement();

-- The stock chain (10, 4.4): the head equals the newest document, a document links to its predecessor, sequence 1 starts at the genesis value
create or replace function core.check_stock_chain() returns trigger language plpgsql as $$
declare h core.chain_head%rowtype; max_seq bigint; max_hash bytea; expected_prev bytea;
begin
    select * into h from core.chain_head
     where tenant_id = new.tenant_id and company_id = new.company_id and chain_type = 'STOCK' and scope_key = '';
    if not found then
        raise exception 'no STOCK chain head for company %', new.company_id using errcode = '23514';
    end if;
    select chain_seq, row_hash into max_seq, max_hash from core.stock_document
     where tenant_id = new.tenant_id and company_id = new.company_id order by chain_seq desc limit 1;
    if h.last_seq <> max_seq or h.last_hash <> max_hash then
        raise exception 'STOCK chain head (seq %) does not match the newest stock document (seq %)', h.last_seq, max_seq using errcode = '23514';
    end if;
    if new.chain_seq = 1 then
        expected_prev := sha256(convert_to('genesis|' || new.tenant_id::text || '|' || new.company_id::text || '|STOCK|', 'UTF8'));
    else
        select row_hash into expected_prev from core.stock_document
         where tenant_id = new.tenant_id and company_id = new.company_id and chain_seq = new.chain_seq - 1;
    end if;
    if expected_prev is distinct from new.prev_hash then
        raise exception 'stock document % does not link to its predecessor in the chain', new.stock_document_id using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists stock_document_chain on core.stock_document;
create constraint trigger stock_document_chain after insert on core.stock_document
    deferrable initially deferred for each row execute function core.check_stock_chain();

-- the movements of a document are as many as it says (checked at commit)
create or replace function core.check_stock_movement_count() returns trigger language plpgsql as $$
begin
    if (select count(*) from core.stock_movement where tenant_id = new.tenant_id and company_id = new.company_id and stock_document_id = new.stock_document_id) <> new.movement_count then
        raise exception 'stock document % has not the movement count it says', new.stock_document_id using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists stock_document_movements on core.stock_document;
create constraint trigger stock_document_movements after insert on core.stock_document
    deferrable initially deferred for each row execute function core.check_stock_movement_count();

do $$
declare t text;
begin
    foreach t in array array['stock_document', 'stock_movement'] loop
        execute format('alter table core.%I enable row level security', t);
        execute format('alter table core.%I force row level security', t);
        if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = t and policyname = 'tenant_isolation') then
            execute format('create policy tenant_isolation on core.%I using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id())', t);
        end if;
        execute format('revoke all on core.%I from public', t);
        execute format('grant select on core.%I to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_retention, cmd_stock, cmd_order', t);
        if exists (select 1 from pg_roles where rolname = 'cmd_closing') then execute format('grant select on core.%I to cmd_closing', t); end if;
        if exists (select 1 from pg_roles where rolname = 'cmd_migration') then execute format('grant select on core.%I to cmd_migration', t); end if;
        execute format('drop trigger if exists %I on core.%I', t || '_no_delete', t);
        execute format('create trigger %I before delete on core.%I for each row execute function core.reject_delete()', t || '_no_delete', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
        execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_delete()', t || '_no_truncate', t);
    end loop;
end $$;

-- ---------------------------------------------------------------------------------------------------------
-- The stock role: its two tables, a journal document with its lines, the open item row of a GR/IR line, the snapshot, the number, the LEDGER and STOCK heads, the command log and
-- the outbox. It locks the item rows of a company (10, 6.4: the lock row of an item is item_company), which needs one updatable column, and the period rows in share mode.
-- ---------------------------------------------------------------------------------------------------------
grant select on all tables in schema core to cmd_stock;
grant select on all tables in schema platform to cmd_stock;
revoke all on core.business_partner_pii from cmd_stock;
grant insert on core.stock_document, core.stock_movement, core.journal_document, core.journal_line, core.journal_line_dimension, core.journal_line_open_item, core.document_legal_snapshot,
      core.command_log, core.command_log_result, core.outbox_event, core.chain_head to cmd_stock;
grant update (status, reversed_by_stock_document_id, header_text) on core.stock_document to cmd_stock;
grant update (updated_at) on core.item_company to cmd_stock;
grant update (last_number, updated_at) on core.number_range_counter to cmd_stock;
grant update (last_seq, last_hash, hash_version, updated_at) on core.chain_head to cmd_stock;
grant update (last_position) on core.outbox_head to cmd_stock;
grant update (last_event_id) on core.fiscal_period, core.fiscal_year to cmd_stock;

drop policy if exists chain_head_stock on core.chain_head;
create policy chain_head_stock on core.chain_head as restrictive for update to cmd_stock
    using (chain_type in ('LEDGER', 'STOCK')) with check (chain_type in ('LEDGER', 'STOCK'));

insert into platform.schema_migration (version, description)
values ('0038', 'the stock ledger: stock_document, stock_movement, the STOCK chain check, the stock role')
on conflict (version) do nothing;
