-- 0065 order address snapshots (11, 4.5, 4.7; F-IMPL-106): the addresses of an order as they were at placement, copied from the partner or given by the caller.
-- Personal data (07, Q3). One row per order and role, written with the order and never changed except by the retention job (retention_until is an S column, outside any hash; the job that
-- anonymises the snapshot is not built). The country stays for tax evidence. Reads of it are logged as read access to the field group ORDER_ADDRESS (09, 5.2, 0033).

create table if not exists core.order_address_snapshot (
    tenant_id           uuid not null references platform.tenant (tenant_id),
    company_id          uuid not null,
    order_kind          core.code not null check (order_kind in ('SALES', 'PURCHASE')),
    order_id            uuid not null,
    address_role        core.code not null check (address_role in ('SHIP_TO', 'BILL_TO', 'SHIP_FROM')),
    name                varchar(200),
    address_line_1      varchar(200),
    address_line_2      varchar(200),
    postal_code         varchar(20),
    city                varchar(100),
    country_code        char(2) not null references platform.ref_country (country_code),
    contact_reference   varchar(200),
    copied_from_partner boolean not null,
    retention_until     date,                                  -- S
    primary key (tenant_id, company_id, order_kind, order_id, address_role),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id)
);

alter table core.order_address_snapshot enable row level security;
alter table core.order_address_snapshot force row level security;
do $$ begin
    if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = 'order_address_snapshot' and policyname = 'tenant_isolation') then
        create policy tenant_isolation on core.order_address_snapshot using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id());
    end if;
end $$;
revoke all on core.order_address_snapshot from public;
grant select on core.order_address_snapshot to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_retention, cmd_stock, cmd_order;
grant insert on core.order_address_snapshot to cmd_order;
grant update (retention_until) on core.order_address_snapshot to cmd_retention;
-- the order command copies the address of the partner at placement (11, 4.7)
grant select (partner_id, tenant_id, legal_name, address_line_1, address_line_2, postal_code, city, country_code, contact_person) on core.business_partner_pii to cmd_order;

-- the address columns are frozen once written: only the retention date changes (the retention job anonymises through its own function, not built)
create or replace function core.guard_order_address_snapshot() returns trigger language plpgsql as $$
begin
    if (to_jsonb(new) - 'retention_until') is distinct from (to_jsonb(old) - 'retention_until') then
        raise exception 'order_address_snapshot: an address cannot change after placement' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists order_address_snapshot_guard on core.order_address_snapshot;
create trigger order_address_snapshot_guard before update on core.order_address_snapshot for each row execute function core.guard_order_address_snapshot();
drop trigger if exists order_address_snapshot_no_delete on core.order_address_snapshot;
create trigger order_address_snapshot_no_delete before delete on core.order_address_snapshot for each row execute function core.reject_delete();
drop trigger if exists order_address_snapshot_no_truncate on core.order_address_snapshot;
create trigger order_address_snapshot_no_truncate before truncate on core.order_address_snapshot for each statement execute function core.reject_delete();

insert into platform.schema_migration (version, description)
values ('0065', 'order address snapshots')
on conflict (version) do nothing;
