-- 0039 order fulfilment, first slice (11, 5.1, 5.2, 5.5): sales deliveries and purchase receipts with their lines, and the FULFILMENT chain they are nodes of. Insert-only: a reversal
-- (not built) inserts mirror rows. The commands run as the stock role, which already writes the stock ledger and the journal of the derived stock document in the same transaction.
-- Not here: returns, dropship confirmations, intercompany deliveries, invoice link records.

-- ---------------------------------------------------------------------------------------------------------
-- sales_delivery and sales_delivery_line (11, 5.1)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.sales_delivery (
    tenant_id              uuid not null references platform.tenant (tenant_id),
    company_id             uuid not null,
    delivery_id            uuid not null default gen_random_uuid(),
    delivery_number        varchar(50) not null,
    number_series_id       uuid not null,
    fiscal_year            int not null,
    sequence_no            bigint not null,
    delivery_date          date not null,
    customer_partner_id    uuid not null,
    warehouse_id           uuid,
    carrier_reference      varchar(100),
    tracking_reference     varchar(100),
    stock_document_id      uuid,
    status                 core.code not null default 'posted' check (status in ('posted', 'reversed')),     -- S, outside the hash
    reversal_of_delivery_id uuid,
    reason_code            core.code,
    origin                 core.code not null default 'LIVE' check (origin in ('LIVE', 'MIGRATED')),
    migration_batch_id     uuid,
    source_system          core.code not null,
    source_document_ref    varchar(100),
    posted_at              timestamptz not null,
    posted_by              varchar(100) not null,
    correlation_id         varchar(100) not null,
    idempotency_key        varchar(100) not null,
    line_count             int not null check (line_count >= 1),
    chain_seq              bigint not null check (chain_seq >= 1),
    prev_hash              core.hash32 not null,
    row_hash               core.hash32 not null,
    line_digest            core.hash32 not null,
    hash_version           smallint not null check (hash_version = 2),
    primary key (tenant_id, company_id, delivery_id),
    unique (tenant_id, company_id, number_series_id, fiscal_year, sequence_no),
    unique (tenant_id, idempotency_key),
    unique (tenant_id, company_id, chain_seq),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, customer_partner_id) references core.business_partner (tenant_id, partner_id),
    foreign key (tenant_id, company_id, stock_document_id) references core.stock_document (tenant_id, company_id, stock_document_id),
    foreign key (tenant_id, company_id, reversal_of_delivery_id) references core.sales_delivery (tenant_id, company_id, delivery_id),
    check ((origin = 'MIGRATED') = (migration_batch_id is not null))
);

create table if not exists core.sales_delivery_line (
    tenant_id              uuid not null,
    company_id             uuid not null,
    delivery_id            uuid not null,
    line_no                int not null check (line_no >= 1),
    sales_order_id         uuid not null,
    sales_order_line_no    int not null,
    item_id                uuid not null,
    quantity               numeric(19,6) not null,
    unit_code              varchar(3) not null references platform.ref_unit (unit_code),
    base_quantity          numeric(19,6) not null,
    warehouse_id           uuid,
    location_id            uuid,
    tracking_id            uuid,
    stock_status           core.code not null default 'AVAILABLE',
    stock_document_line_no int,
    value_lc               numeric(19,4),
    primary key (tenant_id, company_id, delivery_id, line_no),
    foreign key (tenant_id, company_id, delivery_id) references core.sales_delivery (tenant_id, company_id, delivery_id),
    foreign key (tenant_id, company_id, sales_order_id, sales_order_line_no) references core.sales_order_line (tenant_id, company_id, sales_order_id, line_no),
    foreign key (tenant_id, item_id) references core.item (tenant_id, item_id)
);
create index if not exists sales_delivery_line_order on core.sales_delivery_line (tenant_id, company_id, sales_order_id, sales_order_line_no);

-- ---------------------------------------------------------------------------------------------------------
-- purchase_receipt and purchase_receipt_line (11, 5.2)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.purchase_receipt (
    tenant_id              uuid not null references platform.tenant (tenant_id),
    company_id             uuid not null,
    receipt_id             uuid not null default gen_random_uuid(),
    receipt_number         varchar(50) not null,
    number_series_id       uuid not null,
    fiscal_year            int not null,
    sequence_no            bigint not null,
    receipt_date           date not null,
    supplier_partner_id    uuid not null,
    warehouse_id           uuid,
    delivery_note_reference varchar(100),
    stock_document_id      uuid,
    status                 core.code not null default 'posted' check (status in ('posted', 'reversed')),
    reversal_of_receipt_id uuid,
    reason_code            core.code,
    origin                 core.code not null default 'LIVE' check (origin in ('LIVE', 'MIGRATED')),
    migration_batch_id     uuid,
    source_system          core.code not null,
    source_document_ref    varchar(100),
    posted_at              timestamptz not null,
    posted_by              varchar(100) not null,
    correlation_id         varchar(100) not null,
    idempotency_key        varchar(100) not null,
    line_count             int not null check (line_count >= 1),
    chain_seq              bigint not null check (chain_seq >= 1),
    prev_hash              core.hash32 not null,
    row_hash               core.hash32 not null,
    line_digest            core.hash32 not null,
    hash_version           smallint not null check (hash_version = 2),
    primary key (tenant_id, company_id, receipt_id),
    unique (tenant_id, company_id, number_series_id, fiscal_year, sequence_no),
    unique (tenant_id, idempotency_key),
    unique (tenant_id, company_id, chain_seq),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, supplier_partner_id) references core.business_partner (tenant_id, partner_id),
    foreign key (tenant_id, company_id, stock_document_id) references core.stock_document (tenant_id, company_id, stock_document_id),
    foreign key (tenant_id, company_id, reversal_of_receipt_id) references core.purchase_receipt (tenant_id, company_id, receipt_id),
    check ((origin = 'MIGRATED') = (migration_batch_id is not null))
);

create table if not exists core.purchase_receipt_line (
    tenant_id              uuid not null,
    company_id             uuid not null,
    receipt_id             uuid not null,
    line_no                int not null check (line_no >= 1),
    purchase_order_id      uuid not null,
    purchase_order_line_no int not null,
    item_id                uuid not null,
    quantity               numeric(19,6) not null,
    unit_code              varchar(3) not null references platform.ref_unit (unit_code),
    base_quantity          numeric(19,6) not null,
    unit_price             core.amt,
    currency               char(3) references platform.ref_currency (currency_code),
    warehouse_id           uuid,
    location_id            uuid,
    tracking_id            uuid,
    stock_status           core.code not null default 'AVAILABLE',
    stock_document_line_no int,
    stock_document_line_count int not null default 1 check (stock_document_line_count >= 0),
    value_lc               numeric(19,4),
    gr_ir_journal_line     int,
    primary key (tenant_id, company_id, receipt_id, line_no),
    foreign key (tenant_id, company_id, receipt_id) references core.purchase_receipt (tenant_id, company_id, receipt_id),
    foreign key (tenant_id, company_id, purchase_order_id, purchase_order_line_no) references core.purchase_order_line (tenant_id, company_id, purchase_order_id, line_no),
    foreign key (tenant_id, item_id) references core.item (tenant_id, item_id)
);
create index if not exists purchase_receipt_line_order on core.purchase_receipt_line (tenant_id, company_id, purchase_order_id, purchase_order_line_no);

-- ---------------------------------------------------------------------------------------------------------
-- The fulfilment chain (11, 5.5): one chain per company over both tables. The head equals the newest node, a node links to its predecessor, sequence 1 starts at the genesis value.
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.check_fulfilment_chain() returns trigger language plpgsql as $$
declare h core.chain_head%rowtype; max_seq bigint; max_hash bytea; expected_prev bytea;
begin
    select * into h from core.chain_head
     where tenant_id = new.tenant_id and company_id = new.company_id and chain_type = 'FULFILMENT' and scope_key = '';
    if not found then
        raise exception 'no FULFILMENT chain head for company %', new.company_id using errcode = '23514';
    end if;
    select chain_seq, row_hash into max_seq, max_hash from (
        select chain_seq, row_hash from core.sales_delivery where tenant_id = new.tenant_id and company_id = new.company_id
        union all
        select chain_seq, row_hash from core.purchase_receipt where tenant_id = new.tenant_id and company_id = new.company_id) n
     order by chain_seq desc limit 1;
    if h.last_seq <> max_seq or h.last_hash <> max_hash then
        raise exception 'FULFILMENT chain head (seq %) does not match the newest node (seq %)', h.last_seq, max_seq using errcode = '23514';
    end if;
    if new.chain_seq = 1 then
        expected_prev := sha256(convert_to('genesis|' || new.tenant_id::text || '|' || new.company_id::text || '|FULFILMENT|', 'UTF8'));
    else
        select row_hash into expected_prev from (
            select chain_seq, row_hash from core.sales_delivery where tenant_id = new.tenant_id and company_id = new.company_id
            union all
            select chain_seq, row_hash from core.purchase_receipt where tenant_id = new.tenant_id and company_id = new.company_id) n
         where chain_seq = new.chain_seq - 1;
    end if;
    if expected_prev is distinct from new.prev_hash then
        raise exception 'fulfilment document does not link to its predecessor in the chain (seq %)', new.chain_seq using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists sales_delivery_chain on core.sales_delivery;
create constraint trigger sales_delivery_chain after insert on core.sales_delivery deferrable initially deferred for each row execute function core.check_fulfilment_chain();
drop trigger if exists purchase_receipt_chain on core.purchase_receipt;
create constraint trigger purchase_receipt_chain after insert on core.purchase_receipt deferrable initially deferred for each row execute function core.check_fulfilment_chain();

-- the lines of a document are as many as it says (checked at commit)
create or replace function core.check_fulfilment_line_count() returns trigger language plpgsql as $$
declare n bigint;
begin
    if tg_table_name = 'sales_delivery' then
        select count(*) into n from core.sales_delivery_line where tenant_id = new.tenant_id and company_id = new.company_id and delivery_id = new.delivery_id;
    else
        select count(*) into n from core.purchase_receipt_line where tenant_id = new.tenant_id and company_id = new.company_id and receipt_id = new.receipt_id;
    end if;
    if n <> new.line_count then
        raise exception '% has not the line count it says', tg_table_name using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists sales_delivery_lines on core.sales_delivery;
create constraint trigger sales_delivery_lines after insert on core.sales_delivery deferrable initially deferred for each row execute function core.check_fulfilment_line_count();
drop trigger if exists purchase_receipt_lines on core.purchase_receipt;
create constraint trigger purchase_receipt_lines after insert on core.purchase_receipt deferrable initially deferred for each row execute function core.check_fulfilment_line_count();

-- a document is frozen: only its S columns change (the status, set by the reversal commands)
create or replace function core.guard_fulfilment_header() returns trigger language plpgsql as $$
begin
    if (to_jsonb(new) - 'status') is distinct from (to_jsonb(old) - 'status') then
        raise exception '%: an L1 column cannot change', tg_table_name using errcode = '23000';
    end if;
    if new.status is distinct from old.status and not (old.status = 'posted' and new.status = 'reversed') then
        raise exception '%: status can only change from posted to reversed', tg_table_name using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists sales_delivery_guard on core.sales_delivery;
create trigger sales_delivery_guard before update on core.sales_delivery for each row execute function core.guard_fulfilment_header();
drop trigger if exists purchase_receipt_guard on core.purchase_receipt;
create trigger purchase_receipt_guard before update on core.purchase_receipt for each row execute function core.guard_fulfilment_header();

do $$
declare t text;
begin
    foreach t in array array['sales_delivery', 'sales_delivery_line', 'purchase_receipt', 'purchase_receipt_line'] loop
        execute format('alter table core.%I enable row level security', t);
        execute format('alter table core.%I force row level security', t);
        if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = t and policyname = 'tenant_isolation') then
            execute format('create policy tenant_isolation on core.%I using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id())', t);
        end if;
        execute format('revoke all on core.%I from public', t);
        execute format('grant select on core.%I to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_retention, cmd_stock, cmd_order', t);
        if exists (select 1 from pg_roles where rolname = 'cmd_closing') then execute format('grant select on core.%I to cmd_closing', t); end if;
        if exists (select 1 from pg_roles where rolname = 'cmd_migration') then execute format('grant select on core.%I to cmd_migration', t); end if;
        execute format('grant insert on core.%I to cmd_stock', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_delete', t);
        execute format('create trigger %I before delete on core.%I for each row execute function core.reject_delete()', t || '_no_delete', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
        execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_delete()', t || '_no_truncate', t);
    end loop;
    foreach t in array array['sales_delivery_line', 'purchase_receipt_line'] loop
        execute format('drop trigger if exists %I on core.%I', t || '_append_only', t);
        execute format('create trigger %I before update on core.%I for each row execute function core.reject_change()', t || '_append_only', t);
    end loop;
end $$;

-- The stock role also writes the fulfilment documents, locks the order rows of the command (one updatable column each, 11, 9) and moves the FULFILMENT head.
grant update (status) on core.sales_delivery, core.purchase_receipt to cmd_stock;
grant update (row_version) on core.sales_order, core.purchase_order to cmd_stock;
drop policy if exists chain_head_stock on core.chain_head;
create policy chain_head_stock on core.chain_head as restrictive for update to cmd_stock
    using (chain_type in ('LEDGER', 'STOCK', 'FULFILMENT')) with check (chain_type in ('LEDGER', 'STOCK', 'FULFILMENT'));

insert into platform.schema_migration (version, description)
values ('0039', 'fulfilment: sales deliveries and purchase receipts with lines, the FULFILMENT chain')
on conflict (version) do nothing;
