-- 0066 reservations in the order flows (11, 3.1, 6.2, 6.3; 10, 4.5; F-IMPL-107): sales-orders:create reserves best effort when the company setting auto_reserve is on, a delivery consumes the reservation
-- of its order line, a short-close, a cancellation, a change of warehouse or a smaller quantity release or reduce it. The order role writes the reservation records and, like every command that
-- touches an item, locks its item_company row first (10, 6.4). It has no update right on the table, so the lock is taken through a function that runs with the right of its owner and only for the
-- tenant of the session.

create or replace function core.lock_item_company(p_company uuid, p_item uuid) returns void language plpgsql security definer set search_path = core, pg_temp as $$
begin
    perform 1 from core.item_company where tenant_id = core.current_tenant_id() and company_id = p_company and item_id = p_item for update;
end $$;
revoke all on function core.lock_item_company(uuid, uuid) from public;
grant execute on function core.lock_item_company(uuid, uuid) to cmd_order;

grant insert on core.stock_reservation, core.stock_reservation_event to cmd_order;

insert into platform.schema_migration (version, description)
values ('0066', 'reservations in the order flows: item lock function and insert rights of the order role')
on conflict (version) do nothing;
