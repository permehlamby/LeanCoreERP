-- 0059 tracking and FIFO layers (10, 3.8, 4.3, 5.3): item_tracking (lot and serial identities, created by the stock command that first receives them), stock_cost_layer (the layer of a
-- receiving movement line of a FIFO item, with the S column exhausted), stock_layer_consumption (the issue line that consumed a layer; a mirror line of a reversal carries the negated
-- quantity and value) and stock_layer_adjustment (an increase of the value of a layer by a landed cost, 6.6). All insert-only but for the tracking status and exhausted.
-- The remaining quantity of a layer is its quantity less the sum of its consumption (computed, the truth); exhausted is the cache that finds the oldest open layers through one index.

create table if not exists core.item_tracking (
    tenant_id       uuid not null references platform.tenant (tenant_id),
    item_id         uuid not null,
    tracking_id     uuid not null default gen_random_uuid(),
    tracking_no     varchar(50) not null,
    kind            core.code not null check (kind in ('LOT', 'SERIAL')),
    expiry_date     date,
    production_date date,
    supplier_ref    varchar(100),
    status          core.code not null default 'ACTIVE' check (status in ('ACTIVE', 'RECALLED')),
    row_version     bigint not null default 1,
    created_at      timestamptz not null default now(),
    created_by      varchar(100) not null,
    updated_at      timestamptz not null default now(),
    updated_by      varchar(100) not null,
    primary key (tenant_id, item_id, tracking_id),
    unique (tenant_id, item_id, tracking_no),
    foreign key (tenant_id, item_id) references core.item (tenant_id, item_id)
);

create or replace function core.guard_item_tracking() returns trigger language plpgsql as $$
begin
    if (new.tracking_no, new.kind, new.item_id, new.created_by) is distinct from (old.tracking_no, old.kind, old.item_id, old.created_by) then
        raise exception 'item_tracking: the number and the kind of a lot or serial never change' using errcode = '23000';
    end if;
    if new.expiry_date is distinct from old.expiry_date and exists (
        select 1 from core.stock_movement m where m.tenant_id = old.tenant_id and m.item_id = old.item_id and m.tracking_id = old.tracking_id) then
        raise exception 'item_tracking: the expiry date cannot change once a movement uses the lot' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists item_tracking_guard on core.item_tracking;
create trigger item_tracking_guard before update on core.item_tracking for each row execute function core.guard_item_tracking();

-- a movement may name a tracking row
do $$ begin
    if not exists (select 1 from pg_constraint where conname = 'stock_movement_tracking_fk') then
        alter table core.stock_movement add constraint stock_movement_tracking_fk foreign key (tenant_id, item_id, tracking_id) references core.item_tracking (tenant_id, item_id, tracking_id);
    end if;
end $$;
create index if not exists stock_movement_tracking on core.stock_movement (tenant_id, item_id, tracking_id) where tracking_id is not null;

create table if not exists core.stock_cost_layer (
    tenant_id         uuid not null,
    company_id        uuid not null,
    stock_document_id uuid not null,
    line_no           int not null,
    item_id           uuid not null,
    cost_warehouse_id uuid,
    tracking_id       uuid,
    layer_seq         bigint not null check (layer_seq >= 1),
    layer_date        date not null,
    quantity_base     numeric(19,6) not null check (quantity_base > 0),
    unit_cost_lc      numeric(19,10) not null check (unit_cost_lc >= 0),
    value_lc          numeric(19,4) not null check (value_lc >= 0),
    exhausted         boolean not null default false,                                                   -- S
    receipt_legacy_ref varchar(100),
    primary key (tenant_id, company_id, stock_document_id, line_no),
    foreign key (tenant_id, company_id, stock_document_id, line_no) references core.stock_movement (tenant_id, company_id, stock_document_id, line_no)
);
create unique index if not exists stock_cost_layer_seq on core.stock_cost_layer
    (tenant_id, company_id, item_id, coalesce(cost_warehouse_id, '00000000-0000-0000-0000-000000000000'::uuid), coalesce(tracking_id, '00000000-0000-0000-0000-000000000000'::uuid), layer_seq);
create index if not exists stock_cost_layer_open on core.stock_cost_layer (tenant_id, company_id, item_id, cost_warehouse_id, tracking_id, exhausted, layer_seq);

create table if not exists core.stock_layer_consumption (
    tenant_id         uuid not null,
    company_id        uuid not null,
    stock_document_id uuid not null,
    line_no           int not null,
    layer_doc_id      uuid not null,
    layer_line_no     int not null,
    quantity_base     numeric(19,6) not null check (quantity_base <> 0),
    value_lc          numeric(19,4) not null,
    primary key (tenant_id, company_id, stock_document_id, line_no, layer_doc_id, layer_line_no),
    foreign key (tenant_id, company_id, stock_document_id, line_no) references core.stock_movement (tenant_id, company_id, stock_document_id, line_no),
    foreign key (tenant_id, company_id, layer_doc_id, layer_line_no) references core.stock_cost_layer (tenant_id, company_id, stock_document_id, line_no),
    check ((quantity_base > 0) = (value_lc >= 0) or value_lc = 0)
);
create index if not exists stock_layer_consumption_layer on core.stock_layer_consumption (tenant_id, company_id, layer_doc_id, layer_line_no);

-- a landed cost raises the value of a layer (6.6): the COST_ADJUST line of the allocation names the layer and the amount
create table if not exists core.stock_layer_adjustment (
    tenant_id         uuid not null,
    company_id        uuid not null,
    stock_document_id uuid not null,
    line_no           int not null,
    layer_doc_id      uuid not null,
    layer_line_no     int not null,
    value_lc          numeric(19,4) not null check (value_lc <> 0),
    primary key (tenant_id, company_id, stock_document_id, line_no, layer_doc_id, layer_line_no),
    foreign key (tenant_id, company_id, stock_document_id, line_no) references core.stock_movement (tenant_id, company_id, stock_document_id, line_no),
    foreign key (tenant_id, company_id, layer_doc_id, layer_line_no) references core.stock_cost_layer (tenant_id, company_id, stock_document_id, line_no)
);

create or replace function core.guard_stock_cost_layer() returns trigger language plpgsql as $$
begin
    if (to_jsonb(new) - 'exhausted') is distinct from (to_jsonb(old) - 'exhausted') then
        raise exception 'stock_cost_layer: only the exhausted flag changes' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists stock_cost_layer_guard on core.stock_cost_layer;
create trigger stock_cost_layer_guard before update on core.stock_cost_layer for each row execute function core.guard_stock_cost_layer();

do $$
declare t text;
begin
    foreach t in array array['item_tracking', 'stock_cost_layer', 'stock_layer_consumption', 'stock_layer_adjustment'] loop
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
    foreach t in array array['stock_layer_consumption', 'stock_layer_adjustment'] loop
        execute format('drop trigger if exists %I on core.%I', t || '_append_only', t);
        execute format('create trigger %I before update on core.%I for each row execute function core.reject_change()', t || '_append_only', t);
    end loop;
end $$;

grant insert on core.item_tracking, core.stock_cost_layer, core.stock_layer_consumption, core.stock_layer_adjustment to cmd_stock;
grant update (exhausted) on core.stock_cost_layer to cmd_stock;
-- the status of a lot or serial (a recall) is configuration of the tenant
grant update (status, expiry_date, production_date, supplier_ref, row_version, updated_at, updated_by) on core.item_tracking to cmd_config;

insert into platform.schema_migration (version, description)
values ('0059', 'item tracking, FIFO cost layers, layer consumption and layer adjustments')
on conflict (version) do nothing;
