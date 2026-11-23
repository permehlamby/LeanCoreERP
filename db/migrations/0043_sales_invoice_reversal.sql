-- 0043 reversal of sales invoices (11, 6.4): an invoice has an AR open item, so its reversal runs as the clearing role (the automatic clearing of 03, 5.7 is written by that role, as for
-- journal-documents:reverse). The role gets the two link tables to write the mirror rows and the one updatable column that locks an order row.

grant insert on core.sales_invoice, core.sales_invoice_line to cmd_clearing;
grant update (row_version) on core.sales_order, core.purchase_order to cmd_clearing;

insert into platform.schema_migration (version, description)
values ('0043', 'reversal of sales invoices: the clearing role writes the mirror link rows')
on conflict (version) do nothing;
