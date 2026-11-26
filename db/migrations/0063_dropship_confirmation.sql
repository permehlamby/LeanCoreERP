-- 0063 dropship confirmations (11, 5.3, 6.6; F-IMPL-105): the supplier ships to the customer, the confirmation counts as delivered on the sales line and as received on the linked purchase line.
-- One row per confirmed line, a node of the FULFILMENT chain of 0039 (the chain now runs over the seven tables). The row carries the derived DROPSHIP_COST journal (Dr COGS, Cr DROPSHIP_CLEARING) in
-- journal_document_id, and its row_hash is the detail_digest of that journal. Insert-only: a reversal inserts a mirror row with a negative quantity and sets the status of the original (S).
-- Not here: the invoicing of a dropship line (sales invoice policy DELIVERED, supplier invoice matching), returns of a confirmation.

create table if not exists core.dropship_confirmation (
    tenant_id                    uuid not null references platform.tenant (tenant_id),
    company_id                   uuid not null,
    dropship_confirmation_id     uuid not null default gen_random_uuid(),
    confirmation_number          varchar(50) not null,
    number_series_id             uuid not null,
    fiscal_year                  int not null,
    sequence_no                  bigint not null,
    confirmation_date            date not null,
    customer_partner_id          uuid not null,
    supplier_partner_id          uuid not null,
    sales_order_id               uuid not null,
    sales_order_line_no          int not null,
    purchase_order_id            uuid not null,
    purchase_order_line_no       int not null,
    item_id                      uuid not null,
    quantity                     numeric(19,6) not null,
    unit_code                    varchar(3) not null references platform.ref_unit (unit_code),
    base_quantity                numeric(19,6) not null,
    supplier_shipment_reference  varchar(100),
    carrier_reference            varchar(100),
    journal_document_id          uuid not null,
    value_lc                     numeric(19,4) not null,
    status                       core.code not null default 'posted' check (status in ('posted', 'reversed')),     -- S, outside the hash
    reversal_of_confirmation_id  uuid,
    reason_code                  core.code,
    origin                       core.code not null default 'LIVE' check (origin in ('LIVE', 'MIGRATED')),
    migration_batch_id           uuid,
    source_system                core.code not null,
    source_document_ref          varchar(100),
    posted_at                    timestamptz not null,
    posted_by                    varchar(100) not null,
    correlation_id               varchar(100) not null,
    idempotency_key              varchar(100) not null,
    chain_seq                    bigint not null check (chain_seq >= 1),
    prev_hash                    core.hash32 not null,
    row_hash                     core.hash32 not null,
    line_digest                  core.hash32 not null,
    hash_version                 smallint not null check (hash_version = 2),
    primary key (tenant_id, company_id, dropship_confirmation_id),
    unique (tenant_id, company_id, number_series_id, fiscal_year, sequence_no),
    unique (tenant_id, idempotency_key),
    unique (tenant_id, company_id, chain_seq),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, customer_partner_id) references core.business_partner (tenant_id, partner_id),
    foreign key (tenant_id, supplier_partner_id) references core.business_partner (tenant_id, partner_id),
    foreign key (tenant_id, company_id, sales_order_id, sales_order_line_no) references core.sales_order_line (tenant_id, company_id, sales_order_id, line_no),
    foreign key (tenant_id, company_id, purchase_order_id, purchase_order_line_no) references core.purchase_order_line (tenant_id, company_id, purchase_order_id, line_no),
    foreign key (tenant_id, company_id, journal_document_id) references core.journal_document (tenant_id, company_id, document_id),
    foreign key (tenant_id, company_id, reversal_of_confirmation_id) references core.dropship_confirmation (tenant_id, company_id, dropship_confirmation_id),
    check ((origin = 'MIGRATED') = (migration_batch_id is not null))
);
create index if not exists dropship_confirmation_sales_line on core.dropship_confirmation (tenant_id, company_id, sales_order_id, sales_order_line_no);
create index if not exists dropship_confirmation_purchase_line on core.dropship_confirmation (tenant_id, company_id, purchase_order_id, purchase_order_line_no);

-- the returns of a confirmation (0041) refer to it
do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'sales_return_line_dropship_fk') then
        alter table core.sales_return_line add constraint sales_return_line_dropship_fk foreign key (tenant_id, company_id, dropship_confirmation_id)
            references core.dropship_confirmation (tenant_id, company_id, dropship_confirmation_id);
    end if;
    if not exists (select 1 from pg_constraint where conname = 'purchase_return_line_dropship_fk') then
        alter table core.purchase_return_line add constraint purchase_return_line_dropship_fk foreign key (tenant_id, company_id, dropship_confirmation_id)
            references core.dropship_confirmation (tenant_id, company_id, dropship_confirmation_id);
    end if;
end $$;

-- the FULFILMENT chain over the seven tables
create or replace function core.fulfilment_nodes(p_tenant uuid, p_company uuid) returns table (chain_seq bigint, row_hash bytea) language sql stable as $$
    select chain_seq, row_hash from core.sales_delivery where tenant_id = p_tenant and company_id = p_company
    union all select chain_seq, row_hash from core.purchase_receipt where tenant_id = p_tenant and company_id = p_company
    union all select chain_seq, row_hash from core.sales_return where tenant_id = p_tenant and company_id = p_company
    union all select chain_seq, row_hash from core.purchase_return where tenant_id = p_tenant and company_id = p_company
    union all select chain_seq, row_hash from core.dropship_confirmation where tenant_id = p_tenant and company_id = p_company
$$;

do $$
declare t text := 'dropship_confirmation';
begin
    execute format('drop trigger if exists %I on core.%I', t || '_chain', t);
    execute format('create constraint trigger %I after insert on core.%I deferrable initially deferred for each row execute function core.check_fulfilment_chain()', t || '_chain', t);
    execute format('drop trigger if exists %I on core.%I', t || '_guard', t);
    execute format('create trigger %I before update on core.%I for each row execute function core.guard_fulfilment_header()', t || '_guard', t);
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
end $$;
grant update (status) on core.dropship_confirmation to cmd_stock;

-- what a side has done of an order line (11, 4.3): deliveries and confirmations on the sales side, receipts and confirmations on the purchase side, mirror rows with their negative quantities included
create or replace view core.sales_fulfilment_line with (security_invoker = true) as
    select tenant_id, company_id, sales_order_id, sales_order_line_no, base_quantity from core.sales_delivery_line
    union all select tenant_id, company_id, sales_order_id, sales_order_line_no, base_quantity from core.dropship_confirmation;
create or replace view core.purchase_fulfilment_line with (security_invoker = true) as
    select tenant_id, company_id, purchase_order_id, purchase_order_line_no, base_quantity from core.purchase_receipt_line
    union all select tenant_id, company_id, purchase_order_id, purchase_order_line_no, base_quantity from core.dropship_confirmation;
grant select on core.sales_fulfilment_line, core.purchase_fulfilment_line to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_retention, cmd_stock, cmd_order;
-- the confirmation posts its journal through the general poster, which marks the rates it used (ADR-019)
grant update (first_used_at) on core.exchange_rate to cmd_stock;


insert into platform.schema_migration (version, description)
values ('0063', 'dropship confirmations, a node of the FULFILMENT chain')
on conflict (version) do nothing;
