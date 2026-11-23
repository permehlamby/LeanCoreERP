-- 0044 the clearing role writes the tax rows of a mirror: it reverses every document with an open item (03, 5.7), and the mirror of a taxed document carries the tax rows of the original
-- (02, 7). The role had no grant on them, so a taxed AR or AP document could not be reversed. Found with sales-invoices:reverse (F-IMPL-56), which runs as the clearing role.

grant insert on core.journal_line_tax, core.journal_line_tax_base to cmd_clearing;

insert into platform.schema_migration (version, description)
values ('0044', 'the clearing role writes the tax rows of a mirror document')
on conflict (version) do nothing;
