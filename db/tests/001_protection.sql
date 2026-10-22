-- Protection and integrity tests (spec 20, section 3; 02, 10; 04, 8). Run by db/tools/run-pglite.mjs after the
-- migrations and the dev seed. They also run in any PostgreSQL where the script user may SET ROLE to the command
-- roles (a superuser, or `postgres` in Supabase after `grant cmd_post, cmd_config, cmd_period, cmd_retention to postgres`).
-- All changes are rolled back at the end, the seed data stays as it was.

begin;

create temp table test_result (seq serial, name text, ok boolean, detail text);

-- Runs a statement and expects an error whose message matches the pattern. The role is reset afterwards.
create function pg_temp.expect_error(p_name text, p_sql text, p_like text) returns void language plpgsql as $$
begin
    begin
        execute p_sql;
        execute 'reset role';
        insert into test_result (name, ok, detail) values (p_name, false, 'no error was raised');
    exception when others then
        execute 'reset role';
        insert into test_result (name, ok, detail) values (p_name, sqlerrm like p_like, sqlerrm);
    end;
end $$;

-- Runs a statement and expects it to succeed.
create function pg_temp.expect_ok(p_name text, p_sql text) returns void language plpgsql as $$
begin
    begin
        execute p_sql;
        execute 'reset role';
        insert into test_result (name, ok, detail) values (p_name, true, 'ok');
    exception when others then
        execute 'reset role';
        insert into test_result (name, ok, detail) values (p_name, false, sqlerrm);
    end;
end $$;

create function pg_temp.expect_count(p_name text, p_sql text, p_expected bigint) returns void language plpgsql as $$
declare n bigint;
begin
    execute p_sql into n;
    insert into test_result (name, ok, detail) values (p_name, n = p_expected, 'got ' || coalesce(n::text, 'null') || ', expected ' || p_expected);
end $$;

-- A document of two lines with the genesis or a given predecessor hash (the row_hash is a fixed test value: the SQL
-- layer checks links and balance, the hash itself is checked by the application and chains:verify).
create function pg_temp.insert_doc(p_seq bigint, p_prev bytea, p_hash bytea, p_doc uuid, p_number text,
                                   p_amount numeric, p_key text) returns void language plpgsql as $$
begin
    insert into core.journal_document (tenant_id, company_id, document_id, ledger_id, document_type, is_system_generated,
        number_series_id, fiscal_year, sequence_no, document_number, fiscal_period, document_date, posting_date, posted_at,
        transaction_currency, origin, source_system, idempotency_key, command_name, posted_by, correlation_id,
        chain_seq, prev_hash, row_hash, hash_version)
    values ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', p_doc,
        '44444444-4444-4444-8444-444444444401', 'JOURNAL', false, '66666666-6666-4666-8666-666666666601', 2026, p_seq,
        p_number, 10, date '2026-10-21', date '2026-10-21', timestamptz '2026-10-21 08:00:00+00', 'DKK', 'EXTERNAL', 'TEST',
        p_key, 'journal-documents:post', 'tester', 'corr-' || p_key, p_seq, p_prev, p_hash, 2);
    insert into core.journal_line (tenant_id, company_id, document_id, line_no, ledger_id, line_type, account_id, posting_date,
        fiscal_year, fiscal_period, amount_tc, amount_lc, currency_lc, rate_lc, amount_gc, currency_gc, rate_gc, rate_type, rate_date)
    values
      ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', p_doc, 1,
       '44444444-4444-4444-8444-444444444401', 'BASE', '55555555-5555-4555-8555-555555555501', date '2026-10-21', 2026, 10,
       p_amount, p_amount, 'DKK', 1, round(p_amount * 0.134, 4), 'EUR', 0.134, 'SPOT', date '2026-10-21'),
      ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', p_doc, 2,
       '44444444-4444-4444-8444-444444444401', 'BASE', '55555555-5555-4555-8555-555555555502', date '2026-10-21', 2026, 10,
       -p_amount, -p_amount, 'DKK', 1, -round(p_amount * 0.134, 4), 'EUR', 0.134, 'SPOT', date '2026-10-21');
end $$;

-- ---------------------------------------------------------------------------------------------------------
-- Setup (as the migration role, tenant A is the session tenant set by the seed)
-- ---------------------------------------------------------------------------------------------------------
select set_config('app.tenant_id', '11111111-1111-4111-8111-111111111111', false);

insert into core.fiscal_year (tenant_id, company_id, fiscal_year, calendar_id, start_date, end_date, opened_by)
values ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', 2026,
        '33333333-3333-4333-8333-333333333301', date '2026-01-01', date '2026-12-31', 'test');
insert into core.fiscal_period (tenant_id, company_id, fiscal_year, period_no, period_type, start_date, end_date)
select '11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', 2026, m, 'REGULAR',
       make_date(2026, m, 1), (make_date(2026, m, 1) + interval '1 month - 1 day')::date
from generate_series(1, 12) m;
insert into core.number_range_counter (tenant_id, company_id, series_id, fiscal_year, last_number)
values ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', '66666666-6666-4666-8666-666666666601', 2026, 0);

-- ---------------------------------------------------------------------------------------------------------
-- 1. Row-level security. A superuser or a role with BYPASSRLS (the migration role in Supabase) is never subject
--    to it, so the checks run as the command roles, as the application does.
-- ---------------------------------------------------------------------------------------------------------
create function pg_temp.count_as(p_role text, p_sql text) returns bigint language plpgsql as $$
declare n bigint;
begin
    execute format('set local role %I', p_role);
    execute p_sql into n;
    execute 'reset role';
    return n;
end $$;
select pg_temp.expect_count('rls: tenant A sees its accounts', $t$select pg_temp.count_as('cmd_read', 'select count(*) from core.gl_account')$t$, 4);

insert into platform.tenant (tenant_id, tenant_code, name, purpose)
values ('99999999-9999-4999-8999-999999999999', 'OTHER', 'Other tenant', 'TEST');
select pg_temp.expect_count('rls: reference data is readable by a command role',
    $t$select pg_temp.count_as('cmd_read', 'select count(*) from platform.ref_currency')$t$, 6);
select pg_temp.expect_count('rls: a command role sees only the tenant of the transaction',
    $t$select pg_temp.count_as('cmd_read', 'select count(*) from platform.tenant')$t$, 1);
select pg_temp.expect_count('rls: the migration log is closed to command roles',
    $t$select pg_temp.count_as('cmd_read', 'select count(*) from platform.schema_migration')$t$, 0);
select set_config('app.tenant_id', '99999999-9999-4999-8999-999999999999', false);
select pg_temp.expect_count('rls: tenant B sees none of A''s accounts', $t$select pg_temp.count_as('cmd_read', 'select count(*) from core.gl_account')$t$, 0);
select pg_temp.expect_error('rls: tenant B cannot insert a row for tenant A',
    $t$set local role cmd_config; insert into core.tenant_currency (tenant_id, currency_code) values ('11111111-1111-4111-8111-111111111111', 'SEK')$t$,
    '%row-level security%');
select set_config('app.tenant_id', '', false);
select pg_temp.expect_count('rls: no tenant set means no rows', $t$select pg_temp.count_as('cmd_read', 'select count(*) from core.gl_account')$t$, 0);
select set_config('app.tenant_id', '11111111-1111-4111-8111-111111111111', false);

-- ---------------------------------------------------------------------------------------------------------
-- 2. Append-only tables and the period status rule
-- ---------------------------------------------------------------------------------------------------------
insert into core.period_event (tenant_id, event_id, company_id, event_seq, fiscal_year, period_no, event_type, from_status, to_status,
    event_at, event_by, correlation_id, idempotency_key, prev_hash, row_hash, hash_version)
values ('11111111-1111-4111-8111-111111111111', 'aaaaaaaa-0000-4000-8000-000000000001', '22222222-2222-4222-8222-222222222201',
        1, 2026, 10, 'PERIOD_SOFT_CLOSE', 'OPEN', 'SOFT_CLOSED', now(), 'tester', 'c1', 'pe-1', sha256('a'), sha256('b'), 2);

select pg_temp.expect_error('append-only: period_event cannot be updated',
    $t$update core.period_event set event_by = 'x'$t$, '%append-only%');
select pg_temp.expect_error('append-only: period_event cannot be deleted', $t$delete from core.period_event$t$, '%append-only%');
select pg_temp.expect_error('append-only: outbox_event cannot be truncated', $t$truncate core.outbox_event$t$, '%append-only%');

select pg_temp.expect_error('period status: change without an event is rejected',
    $t$update core.fiscal_period set status = 'SOFT_CLOSED' where period_no = 9$t$, '%without a period_event%');
select pg_temp.expect_error('period status: an event for another period is rejected',
    $t$update core.fiscal_period set status = 'SOFT_CLOSED', last_event_id = 'aaaaaaaa-0000-4000-8000-000000000001' where period_no = 9$t$,
    '%does not match%');
select pg_temp.expect_error('period status: the wrong target status is rejected',
    $t$update core.fiscal_period set status = 'HARD_CLOSED', last_event_id = 'aaaaaaaa-0000-4000-8000-000000000001' where period_no = 10$t$,
    '%does not match%');
select pg_temp.expect_ok('period status: the matching latest event allows the change',
    $t$update core.fiscal_period set status = 'SOFT_CLOSED', last_event_id = 'aaaaaaaa-0000-4000-8000-000000000001' where period_no = 10$t$);
select pg_temp.expect_error('period status: an L1 column cannot change',
    $t$update core.fiscal_period set start_date = date '2026-10-02' where period_no = 10$t$, '%L1 column%');

-- ---------------------------------------------------------------------------------------------------------
-- 3. Counters and chain heads move by exactly one
-- ---------------------------------------------------------------------------------------------------------
select pg_temp.expect_error('counter: a jump of 2 is rejected',
    $t$update core.number_range_counter set last_number = 2$t$, '%exactly 1%');
select pg_temp.expect_error('counter: a step back is rejected',
    $t$update core.number_range_counter set last_number = 0$t$, '%exactly 1%');
select pg_temp.expect_error('chain head: a jump of 2 is rejected',
    $t$update core.chain_head set last_seq = 2, last_hash = sha256('x') where chain_type = 'LEDGER'$t$, '%exactly 1%');
select pg_temp.expect_error('chain head: a new head must start at 0',
    $t$insert into core.chain_head (tenant_id, company_id, chain_type, scope_key, last_seq, last_hash, hash_version)
       values ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', 'STOCK', '', 5, sha256('x'), 2)$t$,
    '%starts at last_seq 0%');

-- ---------------------------------------------------------------------------------------------------------
-- 4. Documents: balance, lines, chain linkage (deferred checks are forced with SET CONSTRAINTS ... IMMEDIATE)
-- ---------------------------------------------------------------------------------------------------------
-- Document 1 follows the genesis value and moves the counter and the head in the same transaction.
create function pg_temp.post_doc1() returns void language plpgsql as $$
declare genesis bytea := sha256(convert_to('genesis|11111111-1111-4111-8111-111111111111|22222222-2222-4222-8222-222222222201|LEDGER|44444444-4444-4444-8444-444444444401', 'UTF8'));
begin
    perform pg_temp.insert_doc(1, genesis, sha256('doc1'), 'bbbbbbbb-0000-4000-8000-000000000001', 'GL-2026-000001', 100, 'k1');
    update core.number_range_counter set last_number = 1, updated_at = now();
    update core.chain_head set last_seq = 1, last_hash = sha256('doc1'), updated_at = now() where chain_type = 'LEDGER';
    set constraints all immediate;
    set constraints all deferred;
end $$;
select pg_temp.expect_ok('document: the first document, counter and head in step, commits', $t$select pg_temp.post_doc1()$t$);

select pg_temp.expect_count('document: the head follows the document',
    $t$select last_seq from core.chain_head where chain_type = 'LEDGER'$t$, 1);

-- An unbalanced document
create function pg_temp.post_unbalanced() returns void language plpgsql as $$
begin
    perform pg_temp.insert_doc(2, sha256('doc1'), sha256('doc2'), 'bbbbbbbb-0000-4000-8000-000000000002', 'GL-2026-000002', 50, 'k2');
    update core.journal_line set amount_lc = amount_lc + 1 where false;  -- lines are never updated, see below
    insert into core.journal_line (tenant_id, company_id, document_id, line_no, ledger_id, line_type, account_id, posting_date,
        fiscal_year, fiscal_period, amount_tc, amount_lc, currency_lc, rate_lc, amount_gc, currency_gc, rate_gc, rate_type, rate_date)
    values ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', 'bbbbbbbb-0000-4000-8000-000000000002', 3,
        '44444444-4444-4444-8444-444444444401', 'BASE', '55555555-5555-4555-8555-555555555503', date '2026-10-21', 2026, 10,
        1, 1, 'DKK', 1, 0.134, 'EUR', 0.134, 'SPOT', date '2026-10-21');
    update core.number_range_counter set last_number = 2, updated_at = now();
    update core.chain_head set last_seq = 2, last_hash = sha256('doc2'), updated_at = now() where chain_type = 'LEDGER';
    set constraints all immediate;
end $$;
select pg_temp.expect_error('document: an unbalanced document is rejected at commit', $t$select pg_temp.post_unbalanced()$t$, '%does not balance%');

create function pg_temp.post_no_lines() returns void language plpgsql as $$
begin
    insert into core.journal_document (tenant_id, company_id, document_id, ledger_id, document_type, is_system_generated,
        number_series_id, fiscal_year, sequence_no, document_number, fiscal_period, document_date, posting_date, posted_at,
        transaction_currency, origin, source_system, idempotency_key, command_name, posted_by, correlation_id,
        chain_seq, prev_hash, row_hash, hash_version)
    values ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', 'bbbbbbbb-0000-4000-8000-000000000003',
        '44444444-4444-4444-8444-444444444401', 'JOURNAL', false, '66666666-6666-4666-8666-666666666601', 2026, 3, 'GL-2026-000003', 10,
        date '2026-10-21', date '2026-10-21', now(), 'DKK', 'EXTERNAL', 'TEST', 'k3', 'journal-documents:post', 'tester', 'c', 2,
        sha256('doc1'), sha256('doc3'), 2);
    update core.chain_head set last_seq = 2, last_hash = sha256('doc3'), updated_at = now() where chain_type = 'LEDGER';
    set constraints all immediate;
end $$;
select pg_temp.expect_error('document: a document without lines is rejected', $t$select pg_temp.post_no_lines()$t$, '%has no lines%');

create function pg_temp.post_wrong_prev() returns void language plpgsql as $$
begin
    perform pg_temp.insert_doc(2, sha256('not the predecessor'), sha256('doc2'), 'bbbbbbbb-0000-4000-8000-000000000004', 'GL-2026-000002', 50, 'k4');
    update core.number_range_counter set last_number = 2, updated_at = now();
    update core.chain_head set last_seq = 2, last_hash = sha256('doc2'), updated_at = now() where chain_type = 'LEDGER';
    set constraints all immediate;
end $$;
select pg_temp.expect_error('chain: a wrong prev_hash is rejected', $t$select pg_temp.post_wrong_prev()$t$, '%does not link%');

create function pg_temp.post_head_not_moved() returns void language plpgsql as $$
begin
    perform pg_temp.insert_doc(2, sha256('doc1'), sha256('doc2'), 'bbbbbbbb-0000-4000-8000-000000000005', 'GL-2026-000002', 50, 'k5');
    set constraints all immediate;
end $$;
select pg_temp.expect_error('chain: a document without moving the head is rejected', $t$select pg_temp.post_head_not_moved()$t$, '%does not match the newest%');

create function pg_temp.post_doc2() returns void language plpgsql as $$
begin
    perform pg_temp.insert_doc(2, sha256('doc1'), sha256('doc2'), 'bbbbbbbb-0000-4000-8000-000000000006', 'GL-2026-000002', 50, 'k6');
    update core.number_range_counter set last_number = 2, updated_at = now();
    update core.chain_head set last_seq = 2, last_hash = sha256('doc2'), updated_at = now() where chain_type = 'LEDGER';
    set constraints all immediate;
    set constraints all deferred;
end $$;
select pg_temp.expect_ok('chain: the second document links to the first', $t$select pg_temp.post_doc2()$t$);

select pg_temp.expect_error('document: the same sequence number cannot be issued twice',
    $t$select pg_temp.insert_doc(2, sha256('doc2'), sha256('doc3'), 'bbbbbbbb-0000-4000-8000-000000000007', 'GL-2026-000099', 10, 'k7')$t$,
    '%duplicate key%');
select pg_temp.expect_error('document: the idempotency key is unique per tenant',
    $t$select pg_temp.insert_doc(3, sha256('doc2'), sha256('doc3'), 'bbbbbbbb-0000-4000-8000-000000000008', 'GL-2026-000003', 10, 'k1')$t$,
    '%duplicate key%');

-- ---------------------------------------------------------------------------------------------------------
-- 5. Lock classes: L1 never changes, S columns change in the allowed way
-- ---------------------------------------------------------------------------------------------------------
select pg_temp.expect_error('lock class: an L1 column of a document cannot change',
    $t$update core.journal_document set document_type = 'AR_INVOICE' where document_id = 'bbbbbbbb-0000-4000-8000-000000000001'$t$, '%cannot change%');
select pg_temp.expect_error('lock class: an L2 column is locked until the change command exists',
    $t$update core.journal_document set header_text = 'x' where document_id = 'bbbbbbbb-0000-4000-8000-000000000001'$t$, '%cannot change%');
select pg_temp.expect_ok('lock class: status posted to reversed is allowed',
    $t$update core.journal_document set status = 'reversed' where document_id = 'bbbbbbbb-0000-4000-8000-000000000001'$t$);
select pg_temp.expect_error('lock class: status cannot go back',
    $t$update core.journal_document set status = 'posted' where document_id = 'bbbbbbbb-0000-4000-8000-000000000001'$t$, '%only change from posted to reversed%');
select pg_temp.expect_error('lock class: lines are never updated',
    $t$update core.journal_line set amount_tc = 1$t$, '%never updated%');
select pg_temp.expect_error('no delete: documents', $t$delete from core.journal_document$t$, '%no hard delete%');
select pg_temp.expect_error('no delete: lines', $t$delete from core.journal_line$t$, '%no hard delete%');
select pg_temp.expect_error('no delete: accounts', $t$delete from core.gl_account$t$, '%no hard delete%');

select pg_temp.expect_error('account: type is fixed once the account has postings',
    $t$update core.gl_account set account_type = 'LIABILITY' where account_no = '1000'$t$, '%fixed once the account has postings%');
select pg_temp.expect_ok('account: type may change before the first posting',
    $t$update core.gl_account set account_type = 'LIABILITY' where account_no = '8000'$t$);
select pg_temp.expect_error('account: tax_account is never reset',
    $t$update core.gl_account set tax_account = true where account_no = '4000'; update core.gl_account set tax_account = false where account_no = '4000'$t$,
    '%never reset%');
select pg_temp.expect_error('company: currency is fixed once the company has postings',
    $t$update core.company set local_currency = 'EUR'$t$, '%fixed once the company has postings%');

-- ---------------------------------------------------------------------------------------------------------
-- 6. Exchange rates are usage-locked
-- ---------------------------------------------------------------------------------------------------------
insert into core.exchange_rate (tenant_id, rate_id, rate_type, from_currency, to_currency, valid_from, rate)
values ('11111111-1111-4111-8111-111111111111', 'cccccccc-0000-4000-8000-000000000001', 'SPOT', 'DKK', 'EUR', date '2026-10-21', 0.1340000000);
select pg_temp.expect_ok('rate: an unused rate can change',
    $t$update core.exchange_rate set rate = 0.1341 where rate_id = 'cccccccc-0000-4000-8000-000000000001'$t$);
select pg_temp.expect_error('rate: the identity never changes',
    $t$update core.exchange_rate set valid_from = date '2026-10-22'$t$, '%cannot change%');
select pg_temp.expect_ok('rate: marking it used is allowed',
    $t$update core.exchange_rate set first_used_at = now() where rate_id = 'cccccccc-0000-4000-8000-000000000001'$t$);
select pg_temp.expect_error('rate: a used rate cannot change',
    $t$update core.exchange_rate set rate = 0.2$t$, '%used rate cannot change%');

-- ---------------------------------------------------------------------------------------------------------
-- 7. Command roles (they only work with the roles in place: the application uses SET LOCAL ROLE)
-- ---------------------------------------------------------------------------------------------------------
select pg_temp.expect_error('role: cmd_post cannot create accounts',
    $t$set local role cmd_post; insert into core.gl_account (tenant_id, company_id, account_no, name, account_type) values ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', '9999', 'x', 'ASSET')$t$,
    '%permission denied%');
select pg_temp.expect_error('role: cmd_post cannot delete documents',
    $t$set local role cmd_post; delete from core.journal_document$t$, '%permission denied%');
select pg_temp.expect_error('role: cmd_config cannot post documents',
    $t$set local role cmd_config; select pg_temp.insert_doc(3, sha256('doc2'), sha256('doc3'), 'bbbbbbbb-0000-4000-8000-000000000009', 'GL-2026-000003', 5, 'k9')$t$,
    '%permission denied%');
select pg_temp.expect_error('role: cmd_post cannot update other columns of a counter',
    $t$set local role cmd_post; update core.number_range_counter set fiscal_year = 2027$t$, '%permission denied%');

create function pg_temp.head_update_count(p_role text, p_chain text) returns bigint language plpgsql as $$
declare n bigint;
begin
    execute format('set local role %I', p_role);
    execute format('update core.chain_head set last_seq = last_seq + 1, last_hash = sha256(''x''), updated_at = now() where chain_type = %L', p_chain);
    get diagnostics n = row_count;
    reset role;
    return n;
end $$;
select pg_temp.expect_count('role: cmd_post can move the LEDGER chain head', $t$select pg_temp.head_update_count('cmd_post', 'LEDGER')$t$, 1);
select pg_temp.expect_count('role: cmd_post cannot move the PERIOD_EVENT chain head (0 rows)',
    $t$select pg_temp.head_update_count('cmd_post', 'PERIOD_EVENT')$t$, 0);
select pg_temp.expect_count('role: cmd_period can move the PERIOD_EVENT chain head', $t$select pg_temp.head_update_count('cmd_period', 'PERIOD_EVENT')$t$, 1);
select pg_temp.expect_count('role: cmd_period cannot move the LEDGER chain head (0 rows)',
    $t$select pg_temp.head_update_count('cmd_period', 'LEDGER')$t$, 0);

-- ---------------------------------------------------------------------------------------------------------
-- 8. Outbox
-- ---------------------------------------------------------------------------------------------------------
create function pg_temp.post_event(p_position bigint, p_move_head boolean) returns void language plpgsql as $$
begin
    if p_move_head then
        update core.outbox_head set last_position = p_position;
    end if;
    insert into core.outbox_event (tenant_id, position, event_type, entity_type, entity_key, change, occurred_at, actor_ref, correlation_id)
    values ('11111111-1111-4111-8111-111111111111', p_position, 'ledger.document_posted', 'DOCUMENT', 'x', 'POSTED', now(), 'tester', 'c');
    set constraints all immediate;
    set constraints all deferred;
end $$;
select pg_temp.expect_error('outbox: an event beyond the head is rejected', $t$select pg_temp.post_event(1, false)$t$, '%beyond outbox_head%');
select pg_temp.expect_ok('outbox: an event within the head is accepted', $t$select pg_temp.post_event(1, true)$t$);
select pg_temp.expect_error('outbox: the head only moves forward',
    $t$update core.outbox_head set last_position = 1$t$, '%only moves forward%');
select pg_temp.expect_error('outbox: events cannot be updated', $t$update core.outbox_event set summary = '{}'::jsonb$t$, '%append-only%');
select pg_temp.expect_error('outbox: the posting role cannot purge',
    $t$set local role cmd_post; delete from core.outbox_event$t$, '%permission denied%');
select pg_temp.expect_ok('outbox: the retention role can purge', $t$set local role cmd_retention; delete from core.outbox_event$t$);

-- ---------------------------------------------------------------------------------------------------------
-- Result
-- ---------------------------------------------------------------------------------------------------------
select name, case when ok then 'PASS' else 'FAIL' end as result, detail from test_result order by seq;
rollback;
