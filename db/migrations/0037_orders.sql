-- 0037 order management, first slice (11): the company order settings (V), sales and purchase orders with their lines, the order change log and the order events, and the
-- order command role. Orders are pattern M with a change log: a command locks the order row, checks expected_version, changes the L2 and S columns and writes a change log row
-- and an event. Not here: address snapshots, deliveries, receipts, returns, invoices, dropship and intercompany links (the columns that point at them exist and stay empty).

do $$ begin
    if not exists (select 1 from pg_roles where rolname = 'cmd_order') then
        create role cmd_order nologin;
    end if;
end $$;
grant cmd_order to core_app;
do $$ begin
    if exists (select 1 from pg_roles where rolname = 'postgres') then
        grant cmd_order to postgres;
    end if;
end $$;
grant usage on schema platform, core to cmd_order;

-- ---------------------------------------------------------------------------------------------------------
-- company_order_setting_version (11, 3.1, V): resolved like company_setting_version, insert-only. Without a row the defaults of the column definitions apply.
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.company_order_setting_version (
    tenant_id                   uuid not null references platform.tenant (tenant_id),
    company_id                  uuid not null,
    effective_from              date not null,
    match_mode_default          core.code not null default 'THREE_WAY' check (match_mode_default in ('THREE_WAY', 'TWO_WAY')),
    default_over_tolerance_pct  core.rate not null default 0 check (default_over_tolerance_pct between 0 and 100),
    default_under_tolerance_pct core.rate not null default 0 check (default_under_tolerance_pct between 0 and 100),
    auto_reserve                boolean not null default false,
    sales_invoice_mode          core.code not null default 'CORE_BUILDS' check (sales_invoice_mode in ('CORE_BUILDS', 'APP_SUPPLIES')),
    allow_invoice_before_receipt boolean not null default false,
    created_at                  timestamptz not null default now(),
    created_by                  varchar(100) not null,
    primary key (tenant_id, company_id, effective_from),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id)
);

-- ---------------------------------------------------------------------------------------------------------
-- sales_order and sales_order_line (11, 4.1, 4.2)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.sales_order (
    tenant_id              uuid not null references platform.tenant (tenant_id),
    company_id             uuid not null,
    sales_order_id         uuid not null default gen_random_uuid(),
    order_number           varchar(50) not null,
    sequence_no            bigint not null,
    fiscal_year            int not null,
    number_series_id       uuid not null,
    order_date             date not null,
    customer_partner_id    uuid not null,
    order_currency         char(3) not null references platform.ref_currency (currency_code),
    payment_terms_id       uuid,                                                           -- L2
    customer_reference     varchar(100),                                                   -- L2
    requested_delivery_date date,                                                          -- L2
    incoterms_code         core.code,                                                      -- L2
    buyer_reference        varchar(100),                                                   -- L2
    contract_reference     varchar(100),                                                   -- L2
    header_text            varchar(200),                                                   -- L2
    origin                 core.code not null default 'LIVE' check (origin in ('LIVE', 'MIGRATED')),
    migration_batch_id     uuid,
    legacy_order_ref       varchar(100),
    lifecycle_status       core.code not null default 'OPEN' check (lifecycle_status in ('OPEN', 'CLOSED', 'CANCELLED')),   -- S
    source_system          core.code not null,
    source_document_ref    varchar(100),
    placed_at              timestamptz not null default now(),
    placed_by              varchar(100) not null,
    correlation_id         varchar(100) not null,
    idempotency_key        varchar(100) not null,
    ic_transaction_id      uuid,
    row_version            bigint not null default 1,
    primary key (tenant_id, company_id, sales_order_id),
    unique (tenant_id, company_id, number_series_id, fiscal_year, sequence_no),
    unique (tenant_id, idempotency_key),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, customer_partner_id) references core.business_partner (tenant_id, partner_id),
    foreign key (tenant_id, payment_terms_id) references core.payment_terms (tenant_id, payment_terms_id),
    check ((origin = 'MIGRATED') = (migration_batch_id is not null))
);
create index if not exists sales_order_customer on core.sales_order (tenant_id, company_id, customer_partner_id, order_date);

create table if not exists core.sales_order_line (
    tenant_id              uuid not null,
    company_id             uuid not null,
    sales_order_id         uuid not null,
    line_no                int not null check (line_no >= 1),
    item_id                uuid not null,
    description            varchar(200),                                                   -- L2
    quantity               numeric(19,6) not null check (quantity > 0),                    -- L2
    unit_code              varchar(3) not null references platform.ref_unit (unit_code),
    base_quantity          numeric(19,6) not null check (base_quantity > 0),               -- L2, follows quantity
    unit_price             core.amt not null check (unit_price >= 0),                      -- L2
    price_unit_quantity    numeric(19,6) not null default 1 check (price_unit_quantity > 0),
    discount_pct           core.rate not null default 0 check (discount_pct between 0 and 100),   -- L2
    net_amount             core.amt not null,                                              -- L2, computed by the core
    tax_class_id           uuid,
    warehouse_id           uuid,                                                           -- L2
    origin_country         char(2) not null references platform.ref_country (country_code),       -- L2
    destination_country    char(2) not null references platform.ref_country (country_code),       -- L2
    invoice_policy         core.code not null check (invoice_policy in ('DELIVERED', 'ORDERED')),
    over_tolerance_pct     core.rate not null default 0 check (over_tolerance_pct between 0 and 100),    -- L2
    under_tolerance_pct    core.rate not null default 0 check (under_tolerance_pct between 0 and 100),   -- L2
    requested_date         date,                                                           -- L2
    confirmed_date         date,                                                           -- L2
    legacy_line_ref        varchar(100),
    fulfilment_type        core.code not null check (fulfilment_type in ('STOCK', 'SERVICE', 'DROPSHIP')),
    dropship_purchase_order_id uuid,                                                       -- L2
    dropship_purchase_line_no  int,
    line_status            core.code not null default 'OPEN' check (line_status in ('OPEN', 'CLOSED', 'CANCELLED')),   -- S
    line_text              varchar(200),                                                   -- L2
    primary key (tenant_id, company_id, sales_order_id, line_no),
    foreign key (tenant_id, company_id, sales_order_id) references core.sales_order (tenant_id, company_id, sales_order_id),
    foreign key (tenant_id, item_id) references core.item (tenant_id, item_id),
    foreign key (tenant_id, tax_class_id) references core.tax_class (tenant_id, tax_class_id),
    foreign key (tenant_id, company_id, warehouse_id) references core.warehouse (tenant_id, company_id, warehouse_id),
    -- a stock line is delivered from a warehouse, a dropship line from none (4.2, 6.2)
    check (fulfilment_type <> 'STOCK' or warehouse_id is not null),
    check (fulfilment_type <> 'DROPSHIP' or warehouse_id is null)
);

-- ---------------------------------------------------------------------------------------------------------
-- purchase_order and purchase_order_line (11, 4.6)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.purchase_order (
    tenant_id              uuid not null references platform.tenant (tenant_id),
    company_id             uuid not null,
    purchase_order_id      uuid not null default gen_random_uuid(),
    order_number           varchar(50) not null,
    sequence_no            bigint not null,
    fiscal_year            int not null,
    number_series_id       uuid not null,
    order_date             date not null,
    supplier_partner_id    uuid not null,
    order_currency         char(3) not null references platform.ref_currency (currency_code),
    payment_terms_id       uuid,                                                           -- L2
    supplier_reference     varchar(100),                                                   -- L2
    expected_receipt_date  date,                                                           -- L2
    ship_from_country      char(2) references platform.ref_country (country_code),         -- L2, the default origin of the lines
    incoterms_code         core.code,                                                      -- L2
    header_text            varchar(200),                                                   -- L2
    origin                 core.code not null default 'LIVE' check (origin in ('LIVE', 'MIGRATED')),
    migration_batch_id     uuid,
    legacy_order_ref       varchar(100),
    lifecycle_status       core.code not null default 'OPEN' check (lifecycle_status in ('OPEN', 'CLOSED', 'CANCELLED')),   -- S
    source_system          core.code not null,
    source_document_ref    varchar(100),
    placed_at              timestamptz not null default now(),
    placed_by              varchar(100) not null,
    correlation_id         varchar(100) not null,
    idempotency_key        varchar(100) not null,
    ic_transaction_id      uuid,
    row_version            bigint not null default 1,
    primary key (tenant_id, company_id, purchase_order_id),
    unique (tenant_id, company_id, number_series_id, fiscal_year, sequence_no),
    unique (tenant_id, idempotency_key),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, supplier_partner_id) references core.business_partner (tenant_id, partner_id),
    foreign key (tenant_id, payment_terms_id) references core.payment_terms (tenant_id, payment_terms_id),
    check ((origin = 'MIGRATED') = (migration_batch_id is not null))
);
create index if not exists purchase_order_supplier on core.purchase_order (tenant_id, company_id, supplier_partner_id, order_date);

create table if not exists core.purchase_order_line (
    tenant_id              uuid not null,
    company_id             uuid not null,
    purchase_order_id      uuid not null,
    line_no                int not null check (line_no >= 1),
    item_id                uuid not null,
    description            varchar(200),
    quantity               numeric(19,6) not null check (quantity > 0),
    unit_code              varchar(3) not null references platform.ref_unit (unit_code),
    base_quantity          numeric(19,6) not null check (base_quantity > 0),
    unit_price             core.amt not null check (unit_price >= 0),
    price_unit_quantity    numeric(19,6) not null default 1 check (price_unit_quantity > 0),
    discount_pct           core.rate not null default 0 check (discount_pct between 0 and 100),
    net_amount             core.amt not null,
    tax_class_id           uuid,
    warehouse_id           uuid,                                                           -- L2, the receiving warehouse
    origin_country         char(2) not null references platform.ref_country (country_code),
    destination_country    char(2) not null references platform.ref_country (country_code),
    match_mode             core.code not null check (match_mode in ('THREE_WAY', 'TWO_WAY')),   -- L2 until the first invoice line
    over_tolerance_pct     core.rate not null default 0 check (over_tolerance_pct between 0 and 100),
    under_tolerance_pct    core.rate not null default 0 check (under_tolerance_pct between 0 and 100),
    requested_date         date,
    confirmed_date         date,
    legacy_line_ref        varchar(100),
    dropship_sales_order_id uuid,
    dropship_sales_line_no  int,
    line_status            core.code not null default 'OPEN' check (line_status in ('OPEN', 'CLOSED', 'CANCELLED')),   -- S
    line_text              varchar(200),
    primary key (tenant_id, company_id, purchase_order_id, line_no),
    foreign key (tenant_id, company_id, purchase_order_id) references core.purchase_order (tenant_id, company_id, purchase_order_id),
    foreign key (tenant_id, item_id) references core.item (tenant_id, item_id),
    foreign key (tenant_id, tax_class_id) references core.tax_class (tenant_id, tax_class_id),
    foreign key (tenant_id, company_id, warehouse_id) references core.warehouse (tenant_id, company_id, warehouse_id)
);

-- ---------------------------------------------------------------------------------------------------------
-- order_change_log and order_event (11, 4.7): shared by both kinds, insert-only
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.order_change_log (
    tenant_id       uuid not null references platform.tenant (tenant_id),
    change_id       uuid not null default gen_random_uuid(),
    change_set_id   uuid not null,
    company_id      uuid not null,
    entity_type     core.code not null default 'ORDER' check (entity_type in ('ORDER', 'ORDER_ADDRESS_SNAPSHOT')),
    order_kind      core.code not null check (order_kind in ('SALES', 'PURCHASE')),
    order_id        uuid not null,
    address_role    core.code,
    line_no         int,
    field_name      core.code not null,
    value_type      core.code not null check (value_type in ('AMOUNT', 'DATE', 'TEXT', 'ID', 'CODE', 'BOOL', 'QUANTITY')),
    old_value       varchar(500),
    new_value       varchar(500),
    reason_code     core.code,
    changed_by      varchar(100) not null,
    changed_at      timestamptz not null,
    correlation_id  varchar(100) not null,
    idempotency_key varchar(100) not null,
    primary key (tenant_id, change_id),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    check (old_value is distinct from new_value)
);
create index if not exists order_change_log_order on core.order_change_log (tenant_id, company_id, order_kind, order_id, changed_at);

create table if not exists core.order_event (
    tenant_id      uuid not null references platform.tenant (tenant_id),
    company_id     uuid not null,
    order_kind     core.code not null check (order_kind in ('SALES', 'PURCHASE')),
    order_id       uuid not null,
    event_no       int not null check (event_no >= 1),
    event_type     core.code not null check (event_type in ('PLACED', 'CHANGED', 'LINE_ADDED', 'LINE_CLOSED', 'LINE_CANCELLED', 'CANCELLED', 'CLOSED', 'DATE_CHANGED')),
    line_no        int,
    event_at       timestamptz not null,
    event_by       varchar(100) not null,
    reason_code    core.code,
    correlation_id varchar(100) not null,
    primary key (tenant_id, company_id, order_kind, order_id, event_no),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id)
);

-- ---------------------------------------------------------------------------------------------------------
-- Guards. The L1 columns of an order and of a line never change, the status moves from OPEN to CLOSED or CANCELLED and not back.
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.guard_order_header() returns trigger language plpgsql as $$
declare
    mutable constant text[] := array['payment_terms_id', 'customer_reference', 'requested_delivery_date', 'incoterms_code', 'buyer_reference', 'contract_reference',
                                     'header_text', 'supplier_reference', 'expected_receipt_date', 'ship_from_country', 'lifecycle_status', 'row_version'];
begin
    if (to_jsonb(new) - mutable) is distinct from (to_jsonb(old) - mutable) then
        raise exception '%: an L1 column cannot change', tg_table_name using errcode = '23000';
    end if;
    if new.lifecycle_status is distinct from old.lifecycle_status and old.lifecycle_status <> 'OPEN' then
        raise exception '%: a closed or cancelled order stays so', tg_table_name using errcode = '23000';
    end if;
    return new;
end $$;

create or replace function core.guard_order_line() returns trigger language plpgsql as $$
declare
    mutable constant text[] := array['description', 'quantity', 'base_quantity', 'unit_price', 'discount_pct', 'net_amount', 'warehouse_id', 'origin_country', 'destination_country',
                                     'over_tolerance_pct', 'under_tolerance_pct', 'requested_date', 'confirmed_date', 'dropship_purchase_order_id', 'dropship_purchase_line_no',
                                     'dropship_sales_order_id', 'dropship_sales_line_no', 'match_mode', 'line_status', 'line_text'];
begin
    if (to_jsonb(new) - mutable) is distinct from (to_jsonb(old) - mutable) then
        raise exception '%: an L1 column cannot change', tg_table_name using errcode = '23000';
    end if;
    if new.line_status is distinct from old.line_status and old.line_status <> 'OPEN' then
        raise exception '%: a closed or cancelled line stays so', tg_table_name using errcode = '23000';
    end if;
    return new;
end $$;

do $$
declare t text;
begin
    foreach t in array array['company_order_setting_version', 'sales_order', 'sales_order_line', 'purchase_order', 'purchase_order_line', 'order_change_log', 'order_event'] loop
        execute format('alter table core.%I enable row level security', t);
        execute format('alter table core.%I force row level security', t);
        if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = t and policyname = 'tenant_isolation') then
            execute format('create policy tenant_isolation on core.%I using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id())', t);
        end if;
        execute format('revoke all on core.%I from public', t);
        execute format('grant select on core.%I to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_retention, cmd_order', t);
        if exists (select 1 from pg_roles where rolname = 'cmd_closing') then execute format('grant select on core.%I to cmd_closing', t); end if;
        if exists (select 1 from pg_roles where rolname = 'cmd_migration') then execute format('grant select on core.%I to cmd_migration', t); end if;
        execute format('drop trigger if exists %I on core.%I', t || '_no_delete', t);
        execute format('create trigger %I before delete on core.%I for each row execute function core.reject_delete()', t || '_no_delete', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
        execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_delete()', t || '_no_truncate', t);
    end loop;
    foreach t in array array['company_order_setting_version', 'order_change_log', 'order_event'] loop
        execute format('drop trigger if exists %I on core.%I', t || '_append_only', t);
        execute format('create trigger %I before update on core.%I for each row execute function core.reject_change()', t || '_append_only', t);
    end loop;
    foreach t in array array['sales_order', 'purchase_order'] loop
        execute format('drop trigger if exists %I on core.%I', t || '_guard', t);
        execute format('create trigger %I before update on core.%I for each row execute function core.guard_order_header()', t || '_guard', t);
    end loop;
    foreach t in array array['sales_order_line', 'purchase_order_line'] loop
        execute format('drop trigger if exists %I on core.%I', t || '_guard', t);
        execute format('create trigger %I before update on core.%I for each row execute function core.guard_order_line()', t || '_guard', t);
    end loop;
end $$;

-- The configuration role writes the settings. The order role: the orders, their lines, the log and the events, the number counters, the command log and the outbox.
-- It reads the master data and the partner (not the personal data of the partner) through the grants on all tables.
grant insert on core.company_order_setting_version to cmd_config;
grant select on all tables in schema core to cmd_order;
grant select on all tables in schema platform to cmd_order;
revoke all on core.business_partner_pii from cmd_order;
grant insert on core.sales_order, core.sales_order_line, core.purchase_order, core.purchase_order_line, core.order_change_log, core.order_event,
      core.command_log, core.command_log_result, core.outbox_event to cmd_order;
grant update (payment_terms_id, customer_reference, requested_delivery_date, incoterms_code, buyer_reference, contract_reference, header_text, lifecycle_status, row_version)
      on core.sales_order to cmd_order;
grant update (payment_terms_id, supplier_reference, expected_receipt_date, ship_from_country, incoterms_code, header_text, lifecycle_status, row_version) on core.purchase_order to cmd_order;
grant update (description, quantity, base_quantity, unit_price, discount_pct, net_amount, warehouse_id, origin_country, destination_country, over_tolerance_pct, under_tolerance_pct,
              requested_date, confirmed_date, dropship_purchase_order_id, dropship_purchase_line_no, line_status, line_text) on core.sales_order_line to cmd_order;
grant update (description, quantity, base_quantity, unit_price, discount_pct, net_amount, warehouse_id, origin_country, destination_country, match_mode, over_tolerance_pct,
              under_tolerance_pct, requested_date, confirmed_date, dropship_sales_order_id, dropship_sales_line_no, line_status, line_text) on core.purchase_order_line to cmd_order;
grant update (last_number, updated_at) on core.number_range_counter to cmd_order;
grant update (last_position) on core.outbox_head to cmd_order;

insert into platform.schema_migration (version, description)
values ('0037', 'orders: settings, sales and purchase orders and lines, change log, events, the order role')
on conflict (version) do nothing;
