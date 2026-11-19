-- 0035 inventory, first slice (10, 3): the master data of items and warehouses. platform.ref_unit (3.1), item_group, item, item_company, warehouse, warehouse_location,
-- item_warehouse, item_unit_conversion and unit_conversion, all pattern M (current state, row_version, every change in master_change_log), written by cmd_config.
-- Not here: item_tracking (written by the stock commands), account_determination, and everything of section 4 (the stock ledger). Where 10 keys a table by the business
-- keys of its parents (item_company, item_warehouse, item_unit_conversion) the table has a surrogate id as well, because the generic master commands address a row by one id.

-- ---------------------------------------------------------------------------------------------------------
-- platform.ref_unit (10, 3.1): UN/ECE Recommendation 20 codes, platform-maintained like ref_currency
-- ---------------------------------------------------------------------------------------------------------
create table if not exists platform.ref_unit (
    unit_code  varchar(3) primary key check (unit_code = upper(unit_code)),
    name       varchar(100) not null,
    dimension  text not null check (dimension in ('COUNT', 'MASS', 'VOLUME', 'LENGTH', 'AREA', 'TIME', 'OTHER')),
    valid_from date not null default date '1900-01-01',
    valid_to   date,
    check (valid_to is null or valid_to > valid_from)
);
insert into platform.ref_unit (unit_code, name, dimension) values
    ('C62', 'Piece (one)', 'COUNT'), ('PR', 'Pair', 'COUNT'), ('DZN', 'Dozen', 'COUNT'), ('SET', 'Set', 'COUNT'),
    ('BX', 'Box', 'COUNT'), ('CT', 'Carton', 'COUNT'), ('PF', 'Pallet', 'COUNT'), ('PK', 'Pack', 'COUNT'),
    ('KGM', 'Kilogram', 'MASS'), ('GRM', 'Gram', 'MASS'), ('TNE', 'Tonne', 'MASS'), ('LBR', 'Pound', 'MASS'),
    ('LTR', 'Litre', 'VOLUME'), ('MLT', 'Millilitre', 'VOLUME'), ('MTQ', 'Cubic metre', 'VOLUME'),
    ('MTR', 'Metre', 'LENGTH'), ('CMT', 'Centimetre', 'LENGTH'), ('MMT', 'Millimetre', 'LENGTH'), ('KMT', 'Kilometre', 'LENGTH'),
    ('MTK', 'Square metre', 'AREA'), ('HUR', 'Hour', 'TIME'), ('DAY', 'Day', 'TIME')
on conflict (unit_code) do nothing;
revoke all on platform.ref_unit from public;
grant select on platform.ref_unit to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_retention;
do $$ begin
    if exists (select 1 from pg_roles where rolname = 'cmd_closing') then grant select on platform.ref_unit to cmd_closing; end if;
    if exists (select 1 from pg_roles where rolname = 'cmd_migration') then grant select on platform.ref_unit to cmd_migration; end if;
end $$;

-- ---------------------------------------------------------------------------------------------------------
-- item_group (10, 3.2)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.item_group (
    tenant_id                  uuid not null references platform.tenant (tenant_id),
    item_group_id              uuid not null default gen_random_uuid(),
    code                       core.code not null,
    name                       varchar(100) not null,
    default_cost_center_id     uuid,
    default_profit_center_id   uuid,
    default_business_unit_id   uuid,
    active                     boolean not null default true,
    row_version                bigint not null default 1,
    created_at                 timestamptz not null default now(),
    created_by                 varchar(100) not null,
    updated_at                 timestamptz not null default now(),
    updated_by                 varchar(100) not null,
    primary key (tenant_id, item_group_id),
    unique (tenant_id, code),
    foreign key (tenant_id, default_cost_center_id) references core.dimension_value (tenant_id, dimension_value_id),
    foreign key (tenant_id, default_profit_center_id) references core.dimension_value (tenant_id, dimension_value_id),
    foreign key (tenant_id, default_business_unit_id) references core.dimension_value (tenant_id, dimension_value_id)
);

-- ---------------------------------------------------------------------------------------------------------
-- item (10, 3.3). The status INACTIVE of the spec is the flag `active`: status is ACTIVE or BLOCKED.
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.item (
    tenant_id                  uuid not null references platform.tenant (tenant_id),
    item_id                    uuid not null default gen_random_uuid(),
    item_no                    core.code not null,
    name                       varchar(200) not null,
    description                varchar(1000),
    item_type                  core.code not null check (item_type in ('GOODS', 'SERVICE', 'NON_STOCK')),                      -- IAP
    base_unit                  varchar(3) not null references platform.ref_unit (unit_code),                                    -- IAP
    quantity_scale             smallint not null check (quantity_scale between 0 and 6),                                        -- IAP
    item_group_id              uuid not null,
    tax_class_id               uuid,
    tracking_mode              core.code not null default 'NONE' check (tracking_mode in ('NONE', 'LOT', 'SERIAL')),            -- IAP
    expiry_tracked             boolean not null default false,                                                                  -- IAP
    weight_kg                  core.amt check (weight_kg >= 0),
    volume_m3                  core.amt check (volume_m3 >= 0),
    standard_identifier        varchar(50),
    standard_identifier_scheme core.code,
    commodity_code             varchar(20),
    status                     core.code not null default 'ACTIVE' check (status in ('ACTIVE', 'BLOCKED')),
    active                     boolean not null default true,
    row_version                bigint not null default 1,
    created_at                 timestamptz not null default now(),
    created_by                 varchar(100) not null,
    updated_at                 timestamptz not null default now(),
    updated_by                 varchar(100) not null,
    primary key (tenant_id, item_id),
    unique (tenant_id, item_no),
    foreign key (tenant_id, item_group_id) references core.item_group (tenant_id, item_group_id),
    foreign key (tenant_id, tax_class_id) references core.tax_class (tenant_id, tax_class_id),
    -- a service and a non-stock item are never tracked, and only a lot has an expiry date (3.3, 3.8.1)
    check (item_type = 'GOODS' or (tracking_mode = 'NONE' and not expiry_tracked)),
    check (not expiry_tracked or tracking_mode = 'LOT'),
    check (standard_identifier_scheme is null or standard_identifier is not null)
);

-- ---------------------------------------------------------------------------------------------------------
-- item_unit_conversion and unit_conversion (10, 3.4)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.item_unit_conversion (
    tenant_id                  uuid not null references platform.tenant (tenant_id),
    item_unit_conversion_id    uuid not null default gen_random_uuid(),
    item_id                    uuid not null,
    unit_code                  varchar(3) not null references platform.ref_unit (unit_code),
    factor_to_base             core.rate not null check (factor_to_base > 0),
    active                     boolean not null default true,
    row_version                bigint not null default 1,
    created_at                 timestamptz not null default now(),
    created_by                 varchar(100) not null,
    updated_at                 timestamptz not null default now(),
    updated_by                 varchar(100) not null,
    primary key (tenant_id, item_unit_conversion_id),
    unique (tenant_id, item_id, unit_code),
    foreign key (tenant_id, item_id) references core.item (tenant_id, item_id)
);

create table if not exists core.unit_conversion (
    tenant_id                  uuid not null references platform.tenant (tenant_id),
    unit_conversion_id         uuid not null default gen_random_uuid(),
    from_unit                  varchar(3) not null references platform.ref_unit (unit_code),
    to_unit                    varchar(3) not null references platform.ref_unit (unit_code),
    factor                     core.rate not null check (factor > 0),
    active                     boolean not null default true,
    row_version                bigint not null default 1,
    created_at                 timestamptz not null default now(),
    created_by                 varchar(100) not null,
    updated_at                 timestamptz not null default now(),
    updated_by                 varchar(100) not null,
    primary key (tenant_id, unit_conversion_id),
    unique (tenant_id, from_unit, to_unit),
    check (from_unit <> to_unit)
);

-- ---------------------------------------------------------------------------------------------------------
-- warehouse, warehouse_location (10, 3.6)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.warehouse (
    tenant_id                  uuid not null references platform.tenant (tenant_id),
    company_id                 uuid not null,
    warehouse_id               uuid not null default gen_random_uuid(),
    code                       core.code not null,
    name                       varchar(100) not null,
    country_code               char(2) not null references platform.ref_country (country_code),
    address                    varchar(500),
    default_cost_center_id     uuid,
    default_profit_center_id   uuid,
    default_business_unit_id   uuid,
    active                     boolean not null default true,
    row_version                bigint not null default 1,
    created_at                 timestamptz not null default now(),
    created_by                 varchar(100) not null,
    updated_at                 timestamptz not null default now(),
    updated_by                 varchar(100) not null,
    primary key (tenant_id, company_id, warehouse_id),
    unique (tenant_id, warehouse_id),
    unique (tenant_id, company_id, code),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, default_cost_center_id) references core.dimension_value (tenant_id, dimension_value_id),
    foreign key (tenant_id, default_profit_center_id) references core.dimension_value (tenant_id, dimension_value_id),
    foreign key (tenant_id, default_business_unit_id) references core.dimension_value (tenant_id, dimension_value_id)
);

create table if not exists core.warehouse_location (
    tenant_id                  uuid not null references platform.tenant (tenant_id),
    company_id                 uuid not null,
    warehouse_id               uuid not null,
    location_id                uuid not null default gen_random_uuid(),
    location_code              core.code not null,
    name                       varchar(100) not null,
    location_type              core.code not null default 'STANDARD' check (location_type in ('STANDARD', 'TRANSIT', 'QUARANTINE')),   -- L1 once a movement refers to it
    active                     boolean not null default true,
    row_version                bigint not null default 1,
    created_at                 timestamptz not null default now(),
    created_by                 varchar(100) not null,
    updated_at                 timestamptz not null default now(),
    updated_by                 varchar(100) not null,
    primary key (tenant_id, company_id, warehouse_id, location_id),
    unique (tenant_id, location_id),
    unique (tenant_id, company_id, warehouse_id, location_code),
    foreign key (tenant_id, company_id, warehouse_id) references core.warehouse (tenant_id, company_id, warehouse_id)
);

-- ---------------------------------------------------------------------------------------------------------
-- item_company (10, 3.5, the lock row of an item in a company) and item_warehouse (10, 3.7)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.item_company (
    tenant_id                  uuid not null references platform.tenant (tenant_id),
    company_id                 uuid not null,
    item_id                    uuid not null,
    item_company_id            uuid not null default gen_random_uuid(),
    valuation_method           core.code not null check (valuation_method in ('MOVING_AVERAGE', 'FIFO')),                       -- IAP once a stock document exists
    default_warehouse_id       uuid,
    active                     boolean not null default true,
    row_version                bigint not null default 1,
    created_at                 timestamptz not null default now(),
    created_by                 varchar(100) not null,
    updated_at                 timestamptz not null default now(),
    updated_by                 varchar(100) not null,
    primary key (tenant_id, company_id, item_id),
    unique (tenant_id, item_company_id),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, item_id) references core.item (tenant_id, item_id),
    foreign key (tenant_id, company_id, default_warehouse_id) references core.warehouse (tenant_id, company_id, warehouse_id)
);

create table if not exists core.item_warehouse (
    tenant_id                  uuid not null references platform.tenant (tenant_id),
    company_id                 uuid not null,
    warehouse_id               uuid not null,
    item_id                    uuid not null,
    item_warehouse_id          uuid not null default gen_random_uuid(),
    allow_negative_stock       boolean not null default false,
    min_quantity               core.amt check (min_quantity >= 0),
    reorder_quantity           core.amt check (reorder_quantity >= 0),
    active                     boolean not null default true,
    row_version                bigint not null default 1,
    created_at                 timestamptz not null default now(),
    created_by                 varchar(100) not null,
    updated_at                 timestamptz not null default now(),
    updated_by                 varchar(100) not null,
    primary key (tenant_id, company_id, warehouse_id, item_id),
    unique (tenant_id, item_warehouse_id),
    foreign key (tenant_id, company_id, warehouse_id) references core.warehouse (tenant_id, company_id, warehouse_id),
    foreign key (tenant_id, item_id) references core.item (tenant_id, item_id)
);

-- ---------------------------------------------------------------------------------------------------------
-- Security: forced RLS, read for the command roles, write for the configuration role only, never delete
-- ---------------------------------------------------------------------------------------------------------
do $$
declare t text;
begin
    foreach t in array array['item_group', 'item', 'item_unit_conversion', 'unit_conversion', 'warehouse', 'warehouse_location', 'item_company', 'item_warehouse'] loop
        execute format('alter table core.%I enable row level security', t);
        execute format('alter table core.%I force row level security', t);
        if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = t and policyname = 'tenant_isolation') then
            execute format('create policy tenant_isolation on core.%I using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id())', t);
        end if;
        execute format('revoke all on core.%I from public', t);
        execute format('grant select on core.%I to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_retention', t);
        if exists (select 1 from pg_roles where rolname = 'cmd_closing') then execute format('grant select on core.%I to cmd_closing', t); end if;
        if exists (select 1 from pg_roles where rolname = 'cmd_migration') then execute format('grant select on core.%I to cmd_migration', t); end if;
        execute format('grant insert on core.%I to cmd_config', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_delete', t);
        execute format('create trigger %I before delete on core.%I for each row execute function core.reject_delete()', t || '_no_delete', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
        execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_delete()', t || '_no_truncate', t);
    end loop;
end $$;

grant update (code, name, default_cost_center_id, default_profit_center_id, default_business_unit_id, active, row_version, updated_at, updated_by) on core.item_group to cmd_config;
grant update (item_no, name, description, item_type, base_unit, quantity_scale, item_group_id, tax_class_id, tracking_mode, expiry_tracked, weight_kg, volume_m3,
              standard_identifier, standard_identifier_scheme, commodity_code, status, active, row_version, updated_at, updated_by) on core.item to cmd_config;
grant update (factor_to_base, active, row_version, updated_at, updated_by) on core.item_unit_conversion to cmd_config;
grant update (factor, active, row_version, updated_at, updated_by) on core.unit_conversion to cmd_config;
grant update (code, name, country_code, address, default_cost_center_id, default_profit_center_id, default_business_unit_id, active, row_version, updated_at, updated_by) on core.warehouse to cmd_config;
grant update (location_code, name, location_type, active, row_version, updated_at, updated_by) on core.warehouse_location to cmd_config;
grant update (valuation_method, default_warehouse_id, active, row_version, updated_at, updated_by) on core.item_company to cmd_config;
grant update (allow_negative_stock, min_quantity, reorder_quantity, active, row_version, updated_at, updated_by) on core.item_warehouse to cmd_config;

insert into platform.schema_migration (version, description)
values ('0035', 'inventory master data: ref_unit, item group, item, unit conversions, warehouse, location, item company and item warehouse')
on conflict (version) do nothing;
