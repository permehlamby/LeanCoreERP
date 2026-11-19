-- 0041 returns (11, 5.4): sales returns and purchase returns with their lines, nodes of the FULFILMENT chain of 0039. Insert-only, a reversal (not built) inserts mirror rows. The
-- commands run as the stock role. A return line names the delivery line or receipt line it returns. Not here: dropship returns, the credit-note link records.

create table if not exists core.sales_return (
    tenant_id              uuid not null references platform.tenant (tenant_id),
    company_id             uuid not null,
    return_id              uuid not null default gen_random_uuid(),
    return_number          varchar(50) not null,
    number_series_id       uuid not null,
    fiscal_year            int not null,
    sequence_no            bigint not null,
    return_date            date not null,
    customer_partner_id    uuid not null,
    warehouse_id           uuid,
    stock_document_id      uuid,
    status                 core.code not null default 'posted' check (status in ('posted', 'reversed')),     -- S, outside the hash
    reversal_of_return_id  uuid,
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
    primary key (tenant_id, company_id, return_id),
    unique (tenant_id, company_id, number_series_id, fiscal_year, sequence_no),
    unique (tenant_id, idempotency_key),
    unique (tenant_id, company_id, chain_seq),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, customer_partner_id) references core.business_partner (tenant_id, partner_id),
    foreign key (tenant_id, company_id, stock_document_id) references core.stock_document (tenant_id, company_id, stock_document_id),
    foreign key (tenant_id, company_id, reversal_of_return_id) references core.sales_return (tenant_id, company_id, return_id),
    check ((origin = 'MIGRATED') = (migration_batch_id is not null))
);

create table if not exists core.sales_return_line (
    tenant_id              uuid not null,
    company_id             uuid not null,
    return_id              uuid not null,
    line_no                int not null check (line_no >= 1),
    delivery_id            uuid not null,
    delivery_line_no       int not null,
    sales_order_id         uuid not null,
    sales_order_line_no    int not null,
    item_id                uuid not null,
    quantity               numeric(19,6) not null,
    unit_code              varchar(3) not null references platform.ref_unit (unit_code),
    base_quantity          numeric(19,6) not null,
    reason_code            core.code not null,
    disposition            core.code not null check (disposition in ('AVAILABLE', 'QUARANTINE', 'BLOCKED', 'SCRAP')),
    warehouse_id           uuid,
    location_id            uuid,
    tracking_id            uuid,
    stock_status           core.code not null default 'AVAILABLE',
    stock_document_line_no int,
    value_lc               numeric(19,4),
    dropship_confirmation_id uuid,
    primary key (tenant_id, company_id, return_id, line_no),
    foreign key (tenant_id, company_id, return_id) references core.sales_return (tenant_id, company_id, return_id),
    foreign key (tenant_id, company_id, delivery_id, delivery_line_no) references core.sales_delivery_line (tenant_id, company_id, delivery_id, line_no),
    foreign key (tenant_id, company_id, sales_order_id, sales_order_line_no) references core.sales_order_line (tenant_id, company_id, sales_order_id, line_no),
    foreign key (tenant_id, item_id) references core.item (tenant_id, item_id)
);
create index if not exists sales_return_line_delivery on core.sales_return_line (tenant_id, company_id, delivery_id, delivery_line_no);
create index if not exists sales_return_line_order on core.sales_return_line (tenant_id, company_id, sales_order_id, sales_order_line_no);

create table if not exists core.purchase_return (
    tenant_id              uuid not null references platform.tenant (tenant_id),
    company_id             uuid not null,
    return_id              uuid not null default gen_random_uuid(),
    return_number          varchar(50) not null,
    number_series_id       uuid not null,
    fiscal_year            int not null,
    sequence_no            bigint not null,
    return_date            date not null,
    supplier_partner_id    uuid not null,
    warehouse_id           uuid,
    stock_document_id      uuid,
    status                 core.code not null default 'posted' check (status in ('posted', 'reversed')),
    reversal_of_return_id  uuid,
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
    primary key (tenant_id, company_id, return_id),
    unique (tenant_id, company_id, number_series_id, fiscal_year, sequence_no),
    unique (tenant_id, idempotency_key),
    unique (tenant_id, company_id, chain_seq),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, supplier_partner_id) references core.business_partner (tenant_id, partner_id),
    foreign key (tenant_id, company_id, stock_document_id) references core.stock_document (tenant_id, company_id, stock_document_id),
    foreign key (tenant_id, company_id, reversal_of_return_id) references core.purchase_return (tenant_id, company_id, return_id),
    check ((origin = 'MIGRATED') = (migration_batch_id is not null))
);

create table if not exists core.purchase_return_line (
    tenant_id              uuid not null,
    company_id             uuid not null,
    return_id              uuid not null,
    line_no                int not null check (line_no >= 1),
    receipt_id             uuid not null,
    receipt_line_no        int not null,
    purchase_order_id      uuid not null,
    purchase_order_line_no int not null,
    item_id                uuid not null,
    quantity               numeric(19,6) not null,
    unit_code              varchar(3) not null references platform.ref_unit (unit_code),
    base_quantity          numeric(19,6) not null,
    reason_code            core.code not null,
    disposition            core.code not null default 'AVAILABLE' check (disposition in ('AVAILABLE', 'QUARANTINE', 'BLOCKED', 'SCRAP')),
    warehouse_id           uuid,
    location_id            uuid,
    tracking_id            uuid,
    stock_status           core.code not null default 'AVAILABLE',
    stock_document_line_no int,
    value_lc               numeric(19,4),
    dropship_confirmation_id uuid,
    primary key (tenant_id, company_id, return_id, line_no),
    foreign key (tenant_id, company_id, return_id) references core.purchase_return (tenant_id, company_id, return_id),
    foreign key (tenant_id, company_id, receipt_id, receipt_line_no) references core.purchase_receipt_line (tenant_id, company_id, receipt_id, line_no),
    foreign key (tenant_id, company_id, purchase_order_id, purchase_order_line_no) references core.purchase_order_line (tenant_id, company_id, purchase_order_id, line_no),
    foreign key (tenant_id, item_id) references core.item (tenant_id, item_id)
);
create index if not exists purchase_return_line_receipt on core.purchase_return_line (tenant_id, company_id, receipt_id, receipt_line_no);
create index if not exists purchase_return_line_order on core.purchase_return_line (tenant_id, company_id, purchase_order_id, purchase_order_line_no);

-- ---------------------------------------------------------------------------------------------------------
-- The FULFILMENT chain now runs over the six header tables
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.fulfilment_nodes(p_tenant uuid, p_company uuid) returns table (chain_seq bigint, row_hash bytea) language sql stable as $$
    select chain_seq, row_hash from core.sales_delivery where tenant_id = p_tenant and company_id = p_company
    union all select chain_seq, row_hash from core.purchase_receipt where tenant_id = p_tenant and company_id = p_company
    union all select chain_seq, row_hash from core.sales_return where tenant_id = p_tenant and company_id = p_company
    union all select chain_seq, row_hash from core.purchase_return where tenant_id = p_tenant and company_id = p_company
$$;

create or replace function core.check_fulfilment_chain() returns trigger language plpgsql as $$
declare h core.chain_head%rowtype; max_seq bigint; max_hash bytea; expected_prev bytea;
begin
    select * into h from core.chain_head
     where tenant_id = new.tenant_id and company_id = new.company_id and chain_type = 'FULFILMENT' and scope_key = '';
    if not found then
        raise exception 'no FULFILMENT chain head for company %', new.company_id using errcode = '23514';
    end if;
    select n.chain_seq, n.row_hash into max_seq, max_hash from core.fulfilment_nodes(new.tenant_id, new.company_id) n order by n.chain_seq desc limit 1;
    if h.last_seq <> max_seq or h.last_hash <> max_hash then
        raise exception 'FULFILMENT chain head (seq %) does not match the newest node (seq %)', h.last_seq, max_seq using errcode = '23514';
    end if;
    if new.chain_seq = 1 then
        expected_prev := sha256(convert_to('genesis|' || new.tenant_id::text || '|' || new.company_id::text || '|FULFILMENT|', 'UTF8'));
    else
        select n.row_hash into expected_prev from core.fulfilment_nodes(new.tenant_id, new.company_id) n where n.chain_seq = new.chain_seq - 1;
    end if;
    if expected_prev is distinct from new.prev_hash then
        raise exception 'fulfilment document does not link to its predecessor in the chain (seq %)', new.chain_seq using errcode = '23514';
    end if;
    return null;
end $$;

create or replace function core.check_fulfilment_line_count() returns trigger language plpgsql as $$
declare n bigint;
begin
    if tg_table_name = 'sales_delivery' then
        select count(*) into n from core.sales_delivery_line where tenant_id = new.tenant_id and company_id = new.company_id and delivery_id = new.delivery_id;
    elsif tg_table_name = 'purchase_receipt' then
        select count(*) into n from core.purchase_receipt_line where tenant_id = new.tenant_id and company_id = new.company_id and receipt_id = new.receipt_id;
    elsif tg_table_name = 'sales_return' then
        select count(*) into n from core.sales_return_line where tenant_id = new.tenant_id and company_id = new.company_id and return_id = new.return_id;
    else
        select count(*) into n from core.purchase_return_line where tenant_id = new.tenant_id and company_id = new.company_id and return_id = new.return_id;
    end if;
    if n <> new.line_count then
        raise exception '% has not the line count it says', tg_table_name using errcode = '23514';
    end if;
    return null;
end $$;

do $$
declare t text;
begin
    foreach t in array array['sales_return', 'purchase_return'] loop
        execute format('drop trigger if exists %I on core.%I', t || '_chain', t);
        execute format('create constraint trigger %I after insert on core.%I deferrable initially deferred for each row execute function core.check_fulfilment_chain()', t || '_chain', t);
        execute format('drop trigger if exists %I on core.%I', t || '_lines', t);
        execute format('create constraint trigger %I after insert on core.%I deferrable initially deferred for each row execute function core.check_fulfilment_line_count()', t || '_lines', t);
        execute format('drop trigger if exists %I on core.%I', t || '_guard', t);
        execute format('create trigger %I before update on core.%I for each row execute function core.guard_fulfilment_header()', t || '_guard', t);
    end loop;
    foreach t in array array['sales_return', 'sales_return_line', 'purchase_return', 'purchase_return_line'] loop
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
    foreach t in array array['sales_return_line', 'purchase_return_line'] loop
        execute format('drop trigger if exists %I on core.%I', t || '_append_only', t);
        execute format('create trigger %I before update on core.%I for each row execute function core.reject_change()', t || '_append_only', t);
    end loop;
end $$;
grant update (status) on core.sales_return, core.purchase_return to cmd_stock;

insert into platform.schema_migration (version, description)
values ('0041', 'returns: sales and purchase returns with lines, nodes of the FULFILMENT chain')
on conflict (version) do nothing;
