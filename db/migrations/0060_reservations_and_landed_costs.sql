-- 0060 reservations and landed costs (10, 4.5, 4.7, 6.5, 6.6, 6.8): stock_reservation with its events (the reserved quantity is the sum of the events), landed_cost_allocation with its lines
-- (an allocation is the STOCK_COST_ADJUSTMENT document that raises the cost of receipt lines by a cost on LANDED_COST_CLEARING, a mirror allocation reverses it). All insert-only but the
-- status of an allocation (POSTED to REVERSED), which is an S column. The reservation and landed cost commands run as the stock role, under the item locks of 6.4.

create table if not exists core.stock_reservation (
    tenant_id       uuid not null references platform.tenant (tenant_id),
    company_id      uuid not null,
    reservation_id  uuid not null default gen_random_uuid(),
    item_id         uuid not null,
    warehouse_id    uuid not null,
    tracking_id     uuid,
    demand_ref_type core.code not null check (demand_ref_type in ('ORDER_LINE', 'MANUAL')),
    demand_ref      varchar(100) not null,
    unit_code       varchar(3) not null references platform.ref_unit (unit_code),
    created_at      timestamptz not null default now(),
    created_by      varchar(100) not null,
    correlation_id  varchar(100) not null,
    idempotency_key varchar(100) not null,
    primary key (tenant_id, company_id, reservation_id),
    unique (tenant_id, idempotency_key),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, item_id) references core.item (tenant_id, item_id),
    foreign key (tenant_id, company_id, warehouse_id) references core.warehouse (tenant_id, company_id, warehouse_id),
    foreign key (tenant_id, item_id, tracking_id) references core.item_tracking (tenant_id, item_id, tracking_id)
);
create index if not exists stock_reservation_key on core.stock_reservation (tenant_id, company_id, item_id, warehouse_id);

create table if not exists core.stock_reservation_event (
    tenant_id         uuid not null,
    company_id        uuid not null,
    reservation_id    uuid not null,
    event_no          int not null check (event_no >= 1),
    event_type        core.code not null check (event_type in ('CREATE', 'INCREASE', 'DECREASE', 'RELEASE', 'CONSUME')),
    base_quantity     numeric(19,6) not null check (base_quantity <> 0),
    event_at          timestamptz not null default now(),
    event_by          varchar(100) not null,
    reason_code       core.code,
    stock_document_id uuid,
    primary key (tenant_id, company_id, reservation_id, event_no),
    foreign key (tenant_id, company_id, reservation_id) references core.stock_reservation (tenant_id, company_id, reservation_id),
    foreign key (tenant_id, company_id, stock_document_id) references core.stock_document (tenant_id, company_id, stock_document_id),
    check ((event_type = 'CONSUME') = (stock_document_id is not null)),
    check ((event_type in ('CREATE', 'INCREASE')) = (base_quantity > 0))
);

create table if not exists core.landed_cost_allocation (
    tenant_id                 uuid not null,
    company_id                uuid not null,
    allocation_id             uuid not null default gen_random_uuid(),
    stock_document_id         uuid not null,
    cost_source_document_id   uuid not null,
    cost_source_line_no       int not null,
    allocation_method         core.code not null check (allocation_method in ('VALUE', 'QUANTITY', 'WEIGHT', 'VOLUME')),
    total_cost_lc             numeric(19,4) not null check (total_cost_lc <> 0),
    reversal_of_allocation_id uuid,
    reason_code               core.code,
    status                    core.code not null default 'POSTED' check (status in ('POSTED', 'REVERSED', 'MIRROR')),   -- S
    created_at                timestamptz not null default now(),
    created_by                varchar(100) not null,
    primary key (tenant_id, company_id, allocation_id),
    foreign key (tenant_id, company_id, stock_document_id) references core.stock_document (tenant_id, company_id, stock_document_id),
    foreign key (tenant_id, company_id, cost_source_document_id, cost_source_line_no) references core.journal_line (tenant_id, company_id, document_id, line_no),
    foreign key (tenant_id, company_id, reversal_of_allocation_id) references core.landed_cost_allocation (tenant_id, company_id, allocation_id),
    check ((status = 'MIRROR') = (reversal_of_allocation_id is not null))
);
create index if not exists landed_cost_allocation_source on core.landed_cost_allocation (tenant_id, company_id, cost_source_document_id, cost_source_line_no);

create table if not exists core.landed_cost_allocation_line (
    tenant_id          uuid not null,
    company_id         uuid not null,
    allocation_id      uuid not null,
    line_no            int not null check (line_no >= 1),
    receipt_document_id uuid not null,
    receipt_line_no    int not null,
    allocated_cost_lc  numeric(19,4) not null,
    on_hand_part_lc    numeric(19,4) not null,
    consumed_part_lc   numeric(19,4) not null,
    primary key (tenant_id, company_id, allocation_id, line_no),
    foreign key (tenant_id, company_id, allocation_id) references core.landed_cost_allocation (tenant_id, company_id, allocation_id),
    foreign key (tenant_id, company_id, receipt_document_id, receipt_line_no) references core.stock_movement (tenant_id, company_id, stock_document_id, line_no),
    check (on_hand_part_lc + consumed_part_lc = allocated_cost_lc)
);

create or replace function core.guard_landed_cost_allocation() returns trigger language plpgsql as $$
begin
    if (to_jsonb(new) - 'status') is distinct from (to_jsonb(old) - 'status') then
        raise exception 'landed_cost_allocation: only the status changes' using errcode = '23000';
    end if;
    if not (old.status = 'POSTED' and new.status = 'REVERSED') then
        raise exception 'landed_cost_allocation: the status only changes from POSTED to REVERSED' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists landed_cost_allocation_guard on core.landed_cost_allocation;
create trigger landed_cost_allocation_guard before update on core.landed_cost_allocation for each row execute function core.guard_landed_cost_allocation();

do $$
declare t text;
begin
    foreach t in array array['stock_reservation', 'stock_reservation_event', 'landed_cost_allocation', 'landed_cost_allocation_line'] loop
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
    foreach t in array array['stock_reservation', 'stock_reservation_event', 'landed_cost_allocation_line'] loop
        execute format('drop trigger if exists %I on core.%I', t || '_append_only', t);
        execute format('create trigger %I before update on core.%I for each row execute function core.reject_change()', t || '_append_only', t);
    end loop;
end $$;
grant update (status) on core.landed_cost_allocation to cmd_stock;

insert into platform.schema_migration (version, description)
values ('0060', 'stock reservations with events, landed cost allocations with lines')
on conflict (version) do nothing;
