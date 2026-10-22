-- Removes the tenants that the database tests create, with all their rows. DESTRUCTIVE: run it only as the migration role
-- on a database you may clean, and read the list of tenants first (the select below).
--
-- Which tenants: purpose TEST and a code of the form T + 8 hex characters, which is what the tests generate
-- (tests/FinanceCore.Data.Tests/TestTenant.cs). The dev tenant (code DEV) and every other tenant are never touched.
-- To remove one tenant only, run first:  select set_config('cleanup.only_tenant', '<tenant id>', false);
--
-- The command roles have no DELETE, and the guards of 0006 reject changes and deletes for every role. This script
-- switches the user triggers of the tables off inside one transaction and on again before it ends. If anything fails,
-- the whole script is rolled back, the triggers included. Foreign key checks stay on, so the delete order matters.
--
-- In the Supabase SQL Editor: run it without selecting any text, and answer "Run without RLS" if asked.

-- 1. What will be removed
select tenant_id, tenant_code, name
  from platform.tenant
 where purpose = 'TEST' and tenant_code ~ '^T[0-9a-f]{8}$'
   and (nullif(current_setting('cleanup.only_tenant', true), '') is null
        or tenant_id = nullif(current_setting('cleanup.only_tenant', true), '')::uuid)
 order by tenant_code;

-- 2. Remove it
do $cleanup$
declare
    t record;
    tbl text;
    -- children before parents
    tables text[] := array[
        'outbox_event', 'command_log_result', 'command_log', 'journal_line', 'journal_document', 'number_range_counter',
        'fiscal_period', 'fiscal_year', 'chain_head', 'period_event', 'posting_window', 'number_series_assignment',
        'number_series', 'gl_account', 'exchange_rate', 'company_setting_version', 'group_membership', 'ledger', 'company',
        'company_group', 'fiscal_calendar', 'tenant_currency', 'tenant_setting', 'outbox_head'];
    only_id uuid := nullif(current_setting('cleanup.only_tenant', true), '')::uuid;
    removed int := 0;
begin
    foreach tbl in array tables loop
        execute format('alter table core.%I disable trigger user', tbl);
    end loop;
    alter table platform.tenant disable trigger user;

    for t in
        select tenant_id, tenant_code from platform.tenant
         where purpose = 'TEST' and tenant_code ~ '^T[0-9a-f]{8}$' and (only_id is null or tenant_id = only_id)
    loop
        perform set_config('app.tenant_id', t.tenant_id::text, true);
        update core.fiscal_year set year_end_document_id = null where tenant_id = t.tenant_id;
        foreach tbl in array tables loop
            execute format('delete from core.%I where tenant_id = $1', tbl) using t.tenant_id;
        end loop;
        delete from platform.tenant where tenant_id = t.tenant_id;
        removed := removed + 1;
    end loop;

    foreach tbl in array tables loop
        execute format('alter table core.%I enable trigger user', tbl);
    end loop;
    alter table platform.tenant enable trigger user;
    raise notice 'removed % test tenants', removed;
end
$cleanup$;

-- 3. What is left
select count(*) filter (where purpose = 'TEST' and tenant_code ~ '^T[0-9a-f]{8}$') as test_tenants_left,
       count(*) as tenants_total
  from platform.tenant;
