-- 0064 the views of the progress of an order line and a grant (11, 4.3; F-IMPL-105). 0063 was applied without them: they are here instead of in an edited 0063.

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
values ('0064', 'views of the fulfilment progress of order lines, update grant on exchange_rate for the stock role')
on conflict (version) do nothing;
