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

-- The legal snapshot every document needs (0020)
create function pg_temp.insert_snapshot(p_doc uuid) returns void language plpgsql as $$
begin
    insert into core.document_legal_snapshot (tenant_id, company_id, document_id, retention_until)
    values ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', p_doc, date '2036-12-31');
end $$;

-- A document of two lines with the genesis or a given predecessor hash (the row_hash is a fixed test value: the SQL
-- layer checks links and balance, the hash itself is checked by the application and chains:verify).
create function pg_temp.insert_doc(p_seq bigint, p_prev bytea, p_hash bytea, p_doc uuid, p_number text,
                                   p_amount numeric, p_key text, p_snapshot boolean default true) returns void language plpgsql as $$
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
    if p_snapshot then perform pg_temp.insert_snapshot(p_doc); end if;
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
select pg_temp.expect_count('rls: tenant A sees its accounts', $t$select pg_temp.count_as('cmd_read', 'select count(*) from core.gl_account')$t$, 10);

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
    $t$update core.period_event set event_by = 'x' where tenant_id = '11111111-1111-4111-8111-111111111111'$t$, '%append-only%');
select pg_temp.expect_error('append-only: period_event cannot be deleted', $t$delete from core.period_event$t$, '%append-only%');
select pg_temp.expect_error('append-only: outbox_event cannot be truncated', $t$truncate core.outbox_event$t$, '%append-only%');

select pg_temp.expect_error('period status: change without an event is rejected',
    $t$update core.fiscal_period set status = 'SOFT_CLOSED' where tenant_id = '11111111-1111-4111-8111-111111111111' and period_no = 9$t$, '%without a period_event%');
select pg_temp.expect_error('period status: an event for another period is rejected',
    $t$update core.fiscal_period set status = 'SOFT_CLOSED', last_event_id = 'aaaaaaaa-0000-4000-8000-000000000001' where tenant_id = '11111111-1111-4111-8111-111111111111' and period_no = 9$t$,
    '%does not match%');
select pg_temp.expect_error('period status: the wrong target status is rejected',
    $t$update core.fiscal_period set status = 'HARD_CLOSED', last_event_id = 'aaaaaaaa-0000-4000-8000-000000000001' where tenant_id = '11111111-1111-4111-8111-111111111111' and period_no = 10$t$,
    '%does not match%');
select pg_temp.expect_ok('period status: the matching latest event allows the change',
    $t$update core.fiscal_period set status = 'SOFT_CLOSED', last_event_id = 'aaaaaaaa-0000-4000-8000-000000000001' where tenant_id = '11111111-1111-4111-8111-111111111111' and period_no = 10$t$);
select pg_temp.expect_error('period status: an L1 column cannot change',
    $t$update core.fiscal_period set start_date = date '2026-10-02' where tenant_id = '11111111-1111-4111-8111-111111111111' and period_no = 10$t$, '%L1 column%');

-- ---------------------------------------------------------------------------------------------------------
-- 3. Counters and chain heads move by exactly one
-- ---------------------------------------------------------------------------------------------------------
select pg_temp.expect_error('counter: a jump of 2 is rejected',
    $t$update core.number_range_counter set last_number = 2 where tenant_id = '11111111-1111-4111-8111-111111111111'$t$, '%exactly 1%');
select pg_temp.expect_error('counter: a step back is rejected',
    $t$update core.number_range_counter set last_number = 0 where tenant_id = '11111111-1111-4111-8111-111111111111'$t$, '%exactly 1%');
select pg_temp.expect_error('chain head: a jump of 2 is rejected',
    $t$update core.chain_head set last_seq = 2, last_hash = sha256('x') where tenant_id = '11111111-1111-4111-8111-111111111111' and chain_type = 'LEDGER'$t$, '%exactly 1%');
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
    update core.number_range_counter set last_number = 1, updated_at = now() where tenant_id = '11111111-1111-4111-8111-111111111111';
    update core.chain_head set last_seq = 1, last_hash = sha256('doc1'), updated_at = now() where tenant_id = '11111111-1111-4111-8111-111111111111' and chain_type = 'LEDGER';
    set constraints all immediate;
    set constraints all deferred;
end $$;
select pg_temp.expect_ok('document: the first document, counter and head in step, commits', $t$select pg_temp.post_doc1()$t$);

select pg_temp.expect_count('document: the head follows the document',
    $t$select last_seq from core.chain_head where tenant_id = '11111111-1111-4111-8111-111111111111' and chain_type = 'LEDGER'$t$, 1);

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
    update core.number_range_counter set last_number = 2, updated_at = now() where tenant_id = '11111111-1111-4111-8111-111111111111';
    update core.chain_head set last_seq = 2, last_hash = sha256('doc2'), updated_at = now() where tenant_id = '11111111-1111-4111-8111-111111111111' and chain_type = 'LEDGER';
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
    perform pg_temp.insert_snapshot('bbbbbbbb-0000-4000-8000-000000000003');
    update core.chain_head set last_seq = 2, last_hash = sha256('doc3'), updated_at = now() where tenant_id = '11111111-1111-4111-8111-111111111111' and chain_type = 'LEDGER';
    set constraints all immediate;
end $$;
select pg_temp.expect_error('document: a document without lines is rejected', $t$select pg_temp.post_no_lines()$t$, '%has no lines%');

create function pg_temp.post_no_snapshot() returns void language plpgsql as $$
begin
    perform pg_temp.insert_doc(2, sha256('doc1'), sha256('doc2'), 'bbbbbbbb-0000-4000-8000-000000000006', 'GL-2026-000002', 50, 'k6', false);
    update core.number_range_counter set last_number = 2, updated_at = now() where tenant_id = '11111111-1111-4111-8111-111111111111';
    update core.chain_head set last_seq = 2, last_hash = sha256('doc2'), updated_at = now() where tenant_id = '11111111-1111-4111-8111-111111111111' and chain_type = 'LEDGER';
    set constraints all immediate;
end $$;
select pg_temp.expect_error('snapshot: a document without a legal snapshot is rejected at commit', $t$select pg_temp.post_no_snapshot()$t$, '%has no legal snapshot%');
select pg_temp.expect_error('snapshot: a snapshot cannot be updated', $t$update core.document_legal_snapshot set retention_until = date '2099-01-01' where tenant_id = '11111111-1111-4111-8111-111111111111'$t$, '%append-only%');
select pg_temp.expect_error('snapshot: a snapshot cannot be deleted', $t$delete from core.document_legal_snapshot$t$, '%append-only%');
select pg_temp.expect_error('retention rule: a version cannot be updated', $t$update core.retention_rule set retention_years = 99 where tenant_id = '11111111-1111-4111-8111-111111111111'$t$, '%append-only%');
select pg_temp.expect_error('retention rule: a version cannot be deleted', $t$delete from core.retention_rule$t$, '%append-only%');

create function pg_temp.post_wrong_prev() returns void language plpgsql as $$
begin
    perform pg_temp.insert_doc(2, sha256('not the predecessor'), sha256('doc2'), 'bbbbbbbb-0000-4000-8000-000000000004', 'GL-2026-000002', 50, 'k4');
    update core.number_range_counter set last_number = 2, updated_at = now() where tenant_id = '11111111-1111-4111-8111-111111111111';
    update core.chain_head set last_seq = 2, last_hash = sha256('doc2'), updated_at = now() where tenant_id = '11111111-1111-4111-8111-111111111111' and chain_type = 'LEDGER';
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
    update core.number_range_counter set last_number = 2, updated_at = now() where tenant_id = '11111111-1111-4111-8111-111111111111';
    update core.chain_head set last_seq = 2, last_hash = sha256('doc2'), updated_at = now() where tenant_id = '11111111-1111-4111-8111-111111111111' and chain_type = 'LEDGER';
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
    $t$update core.journal_document set document_type = 'AR_INVOICE' where tenant_id = '11111111-1111-4111-8111-111111111111' and document_id = 'bbbbbbbb-0000-4000-8000-000000000001'$t$, '%cannot change%');
select pg_temp.expect_error('lock class: an L2 column needs a change_log row of the same transaction',
    $t$update core.journal_document set header_text = 'x' where tenant_id = '11111111-1111-4111-8111-111111111111' and document_id = 'bbbbbbbb-0000-4000-8000-000000000001'$t$, '%without a matching change_log row%');
select pg_temp.expect_ok('lock class: status posted to reversed is allowed',
    $t$update core.journal_document set status = 'reversed' where tenant_id = '11111111-1111-4111-8111-111111111111' and document_id = 'bbbbbbbb-0000-4000-8000-000000000001'$t$);
select pg_temp.expect_error('lock class: status cannot go back',
    $t$update core.journal_document set status = 'posted' where tenant_id = '11111111-1111-4111-8111-111111111111' and document_id = 'bbbbbbbb-0000-4000-8000-000000000001'$t$, '%only change from posted to reversed%');
select pg_temp.expect_error('lock class: lines are never updated',
    $t$update core.journal_line set amount_tc = 1 where tenant_id = '11111111-1111-4111-8111-111111111111'$t$, '%only line_text and the typed dimensions can change%');
select pg_temp.expect_error('no delete: documents', $t$delete from core.journal_document$t$, '%no hard delete%');
select pg_temp.expect_error('no delete: lines', $t$delete from core.journal_line$t$, '%no hard delete%');
select pg_temp.expect_error('no delete: accounts', $t$delete from core.gl_account$t$, '%no hard delete%');

select pg_temp.expect_error('account: type is fixed once the account has postings',
    $t$update core.gl_account set account_type = 'LIABILITY' where tenant_id = '11111111-1111-4111-8111-111111111111' and account_no = '1000'$t$, '%fixed once the account has postings%');
select pg_temp.expect_ok('account: type may change before the first posting',
    $t$update core.gl_account set account_type = 'LIABILITY' where tenant_id = '11111111-1111-4111-8111-111111111111' and account_no = '8000'$t$);
select pg_temp.expect_error('account: tax_account is never reset',
    $t$update core.gl_account set tax_account = true where tenant_id = '11111111-1111-4111-8111-111111111111' and account_no = '4000'; update core.gl_account set tax_account = false where tenant_id = '11111111-1111-4111-8111-111111111111' and account_no = '4000'$t$,
    '%never reset%');
select pg_temp.expect_error('company: currency is fixed once the company has postings',
    $t$update core.company set local_currency = 'EUR' where tenant_id = '11111111-1111-4111-8111-111111111111'$t$, '%fixed once the company has postings%');

-- Account roles (07, 6.3): a role row is a version, never changed, and the account must satisfy the role table
select pg_temp.expect_error('role: a role row is never updated',
    $t$update core.account_role_assignment set effective_from = now() where tenant_id = '11111111-1111-4111-8111-111111111111' and role_code = 'ROUNDING'$t$, '%append-only%');
select pg_temp.expect_error('role: a role row is never deleted',
    $t$delete from core.account_role_assignment$t$, '%append-only%');
select pg_temp.expect_error('role: ROUNDING needs a P&L account',
    $t$insert into core.account_role_assignment (tenant_id, company_id, role_code, account_id, effective_from)
       select tenant_id, company_id, 'ROUNDING', account_id, now() from core.gl_account where account_no = '1000'$t$, '%ACCOUNT_TYPE%');
select pg_temp.expect_error('role: an account has one role only',
    $t$insert into core.account_role_assignment (tenant_id, company_id, role_code, account_id, effective_from)
       select tenant_id, company_id, 'FX_REALISED', account_id, now() from core.gl_account where account_no = '7900'$t$, '%ONE_ROLE_PER_ACCOUNT%');
select pg_temp.expect_error('role: a currency row needs an account without another currency',
    $t$update core.gl_account set currency_code = 'EUR' where tenant_id = '11111111-1111-4111-8111-111111111111' and account_no = '7930';
       insert into core.account_role_assignment (tenant_id, company_id, role_code, account_id, effective_from)
       select tenant_id, company_id, 'FX_UNREALISED_GAIN', account_id, now() + interval '1 day' from core.gl_account where account_no = '7930'$t$, '%(CURRENCY)%');
select pg_temp.expect_error('role: type and open item mode of a role account are fixed',
    $t$update core.gl_account set account_type = 'REVENUE' where tenant_id = '11111111-1111-4111-8111-111111111111' and account_no = '7900'$t$, '%CONFIG.REFERENCED%');
select pg_temp.expect_error('role: no revalue flag on the retained earnings account',
    $t$update core.gl_account set revalue_flag = true where tenant_id = '11111111-1111-4111-8111-111111111111' and account_no = '8100'$t$, '%REVALUE_FLAG_ON_RETAINED_EARNINGS%');

-- ---------------------------------------------------------------------------------------------------------
-- 6. Exchange rates are usage-locked
-- ---------------------------------------------------------------------------------------------------------
insert into core.exchange_rate (tenant_id, rate_id, rate_type, from_currency, to_currency, valid_from, rate)
values ('11111111-1111-4111-8111-111111111111', 'cccccccc-0000-4000-8000-000000000001', 'SPOT', 'DKK', 'EUR', date '2026-10-21', 0.1340000000);
select pg_temp.expect_ok('rate: an unused rate can change',
    $t$update core.exchange_rate set rate = 0.1341 where tenant_id = '11111111-1111-4111-8111-111111111111' and rate_id = 'cccccccc-0000-4000-8000-000000000001'$t$);
select pg_temp.expect_error('rate: the identity never changes',
    $t$update core.exchange_rate set valid_from = date '2026-10-22' where tenant_id = '11111111-1111-4111-8111-111111111111'$t$, '%cannot change%');
select pg_temp.expect_ok('rate: marking it used is allowed',
    $t$update core.exchange_rate set first_used_at = now() where tenant_id = '11111111-1111-4111-8111-111111111111' and rate_id = 'cccccccc-0000-4000-8000-000000000001'$t$);
select pg_temp.expect_error('rate: a used rate cannot change',
    $t$update core.exchange_rate set rate = 0.2 where tenant_id = '11111111-1111-4111-8111-111111111111'$t$, '%used rate cannot change%');

-- ---------------------------------------------------------------------------------------------------------
-- 7. Command roles (they only work with the roles in place: the application uses SET LOCAL ROLE)
-- ---------------------------------------------------------------------------------------------------------
select pg_temp.expect_error('role: cmd_post cannot create accounts',
    $t$set local role cmd_post; insert into core.gl_account (tenant_id, company_id, account_no, name, account_type) values ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', '9999', 'x', 'ASSET')$t$,
    '%permission denied%');
select pg_temp.expect_error('role: cmd_post cannot read the contact data of a partner',
    $t$set local role cmd_post; select email from core.business_partner_pii$t$, '%permission denied%');
select pg_temp.expect_error('role: cmd_period cannot read the personal data of a partner',
    $t$set local role cmd_period; select legal_name from core.business_partner_pii$t$, '%permission denied%');
select pg_temp.expect_error('role: cmd_clearing cannot read the personal data of a partner',
    $t$set local role cmd_clearing; select legal_name from core.business_partner_pii$t$, '%permission denied%');
select pg_temp.expect_error('role: cmd_post cannot delete documents',
    $t$set local role cmd_post; delete from core.journal_document$t$, '%permission denied%');
select pg_temp.expect_error('role: cmd_config cannot post documents',
    $t$set local role cmd_config; select pg_temp.insert_doc(3, sha256('doc2'), sha256('doc3'), 'bbbbbbbb-0000-4000-8000-000000000009', 'GL-2026-000003', 5, 'k9')$t$,
    '%permission denied%');
select pg_temp.expect_error('role: cmd_post cannot update other columns of a counter',
    $t$set local role cmd_post; update core.number_range_counter set fiscal_year = 2027 where tenant_id = '11111111-1111-4111-8111-111111111111'$t$, '%permission denied%');

create function pg_temp.head_update_count(p_role text, p_chain text) returns bigint language plpgsql as $$
declare n bigint;
begin
    execute format('set local role %I', p_role);
    execute format('update core.chain_head set last_seq = last_seq + 1, last_hash = sha256(''x''), updated_at = now() where tenant_id = ''11111111-1111-4111-8111-111111111111'' and chain_type = %L', p_chain);
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
        update core.outbox_head set last_position = p_position where tenant_id = '11111111-1111-4111-8111-111111111111';
    end if;
    insert into core.outbox_event (tenant_id, position, event_type, entity_type, entity_key, change, occurred_at, actor_ref, correlation_id)
    values ('11111111-1111-4111-8111-111111111111', p_position, 'ledger.document_posted', 'DOCUMENT', 'x', 'POSTED', now(), 'tester', 'c');
    set constraints all immediate;
    set constraints all deferred;
end $$;
select pg_temp.expect_error('outbox: an event beyond the head is rejected', $t$select pg_temp.post_event(1, false)$t$, '%beyond outbox_head%');
select pg_temp.expect_ok('outbox: an event within the head is accepted', $t$select pg_temp.post_event(1, true)$t$);
select pg_temp.expect_error('outbox: the head only moves forward',
    $t$update core.outbox_head set last_position = 1 where tenant_id = '11111111-1111-4111-8111-111111111111'$t$, '%only moves forward%');
select pg_temp.expect_error('outbox: events cannot be updated', $t$update core.outbox_event set summary = '{}'::jsonb where tenant_id = '11111111-1111-4111-8111-111111111111'$t$, '%append-only%');
select pg_temp.expect_error('outbox: the posting role cannot purge',
    $t$set local role cmd_post; delete from core.outbox_event$t$, '%permission denied%');
select pg_temp.expect_ok('outbox: the retention role can purge', $t$set local role cmd_retention; delete from core.outbox_event$t$);

-- ---------------------------------------------------------------------------------------------------------
-- change_log (02, 8) and master_change_log (07, 10.1): append-only evidence, shape checks, grants, isolation.
-- Uses the document bbbbbbbb-...0001 that the posting tests above left in this transaction.
-- ---------------------------------------------------------------------------------------------------------
create function pg_temp.cl(p_sql_values text) returns text language sql as $$
    select 'insert into core.change_log (tenant_id, change_set_id, company_id, entity_type, document_id, clearing_id, line_no, '
        || 'field_name, value_type, old_value, new_value, lock_class, period_status, reason_code, changed_by, changed_at, '
        || 'correlation_id, idempotency_key) values (' || p_sql_values || ')'
$$;
create function pg_temp.cl_row(p_entity text, p_doc text, p_clearing text, p_line text, p_old text, p_new text,
                               p_class text, p_status text, p_reason text, p_field text default 'header_text') returns text language sql as $$
    select pg_temp.cl(format('%L, %L, %L, %L, %s, %s, %s, %L, %L, %s, %s, %L, %L, %L, %L, now(), %L, %L',
        '11111111-1111-4111-8111-111111111111', 'cccccccc-0000-4000-8000-000000000001', '22222222-2222-4222-8222-222222222201',
        p_entity, coalesce(quote_literal(p_doc) || '::uuid', 'null'), coalesce(quote_literal(p_clearing) || '::uuid', 'null'),
        coalesce(p_line, 'null'), p_field, 'TEXT', coalesce(quote_literal(p_old), 'null'), coalesce(quote_literal(p_new), 'null'),
        p_class, p_status, p_reason, 'tester', 'corr-cl', 'idem-cl'))
$$;
select pg_temp.expect_ok('change_log: the posting role can log an L2 change of a header',
    format($t$set local role cmd_post; %s$t$, pg_temp.cl_row('DOCUMENT', 'bbbbbbbb-0000-4000-8000-000000000001', null, null, null, 'Text', 'L2', 'OPEN', 'CORRECTION')));
select pg_temp.expect_ok('change_log: a line change names its line',
    format($t$set local role cmd_post; %s$t$, pg_temp.cl_row('LINE', 'bbbbbbbb-0000-4000-8000-000000000001', null, '1', 'a', 'b', 'L2', 'SOFT_CLOSED', 'CORRECTION')));
select pg_temp.expect_ok('change_log: the retention role can log the L1* overwrite of a legal snapshot',
    format($t$set local role cmd_retention; %s$t$, pg_temp.cl_row('LEGAL_SNAPSHOT', 'bbbbbbbb-0000-4000-8000-000000000001', null, null, 'x', 'y', 'L1*', 'NONE', 'SYSTEM')));
select pg_temp.expect_error('change_log: the configuration role cannot write it',
    format($t$set local role cmd_config; %s$t$, pg_temp.cl_row('DOCUMENT', 'bbbbbbbb-0000-4000-8000-000000000001', null, null, null, 'Text', 'L2', 'OPEN', 'CORRECTION')),
    '%permission denied%');
select pg_temp.expect_error('change_log: a line entity without a line number is rejected',
    format($t$set local role cmd_post; %s$t$, pg_temp.cl_row('LINE', 'bbbbbbbb-0000-4000-8000-000000000001', null, null, 'a', 'b', 'L2', 'OPEN', 'CORRECTION')),
    '%violates check constraint%');
select pg_temp.expect_error('change_log: a header entity with a line number is rejected',
    format($t$set local role cmd_post; %s$t$, pg_temp.cl_row('DOCUMENT', 'bbbbbbbb-0000-4000-8000-000000000001', null, '1', 'a', 'b', 'L2', 'OPEN', 'CORRECTION')),
    '%violates check constraint%');
select pg_temp.expect_error('change_log: a document and a clearing at once is rejected',
    format($t$set local role cmd_post; %s$t$, pg_temp.cl_row('DOCUMENT', 'bbbbbbbb-0000-4000-8000-000000000001', 'dddddddd-0000-4000-8000-000000000001', null, 'a', 'b', 'L2', 'OPEN', 'CORRECTION')),
    '%violates check constraint%');
select pg_temp.expect_error('change_log: neither a document nor a clearing is rejected',
    format($t$set local role cmd_post; %s$t$, pg_temp.cl_row('DOCUMENT', null, null, null, 'a', 'b', 'L2', 'OPEN', 'CORRECTION')),
    '%violates check constraint%');
select pg_temp.expect_error('change_log: an L2 change in a hard-closed period cannot be logged (L3)',
    format($t$set local role cmd_post; %s$t$, pg_temp.cl_row('DOCUMENT', 'bbbbbbbb-0000-4000-8000-000000000001', null, null, 'a', 'b', 'L2', 'HARD_CLOSED', 'CORRECTION')),
    '%violates check constraint%');
select pg_temp.expect_error('change_log: reason SYSTEM belongs to the L1* overwrite only',
    format($t$set local role cmd_post; %s$t$, pg_temp.cl_row('DOCUMENT', 'bbbbbbbb-0000-4000-8000-000000000001', null, null, 'a', 'b', 'L2', 'OPEN', 'SYSTEM')),
    '%violates check constraint%');
select pg_temp.expect_error('change_log: L1* is for the legal snapshot only',
    format($t$set local role cmd_retention; %s$t$, pg_temp.cl_row('DOCUMENT', 'bbbbbbbb-0000-4000-8000-000000000001', null, null, 'a', 'b', 'L1*', 'NONE', 'SYSTEM')),
    '%violates check constraint%');
select pg_temp.expect_error('change_log: a change that changes nothing is rejected',
    format($t$set local role cmd_post; %s$t$, pg_temp.cl_row('DOCUMENT', 'bbbbbbbb-0000-4000-8000-000000000001', null, null, 'a', 'a', 'L2', 'OPEN', 'CORRECTION')),
    '%violates check constraint%');
select pg_temp.expect_error('change_log: an unknown document is rejected',
    format($t$set local role cmd_post; %s$t$, pg_temp.cl_row('DOCUMENT', 'bbbbbbbb-0000-4000-8000-0000000000ff', null, null, 'a', 'b', 'L2', 'OPEN', 'CORRECTION')),
    '%foreign key%');
select pg_temp.expect_error('change_log: rows cannot be updated', $t$update core.change_log set reason_text = 'x' where tenant_id = '11111111-1111-4111-8111-111111111111'$t$, '%append-only%');
select pg_temp.expect_error('change_log: rows cannot be deleted', $t$delete from core.change_log$t$, '%append-only%');
select pg_temp.expect_error('change_log: the posting role has no delete', $t$set local role cmd_post; delete from core.change_log$t$, '%permission denied%');
select pg_temp.expect_error('change_log: truncate is rejected', $t$truncate core.change_log$t$, '%append-only%');
select pg_temp.expect_count('change_log: a command role reads the rows of its tenant',
    $t$select pg_temp.count_as('cmd_read', 'select count(*) from core.change_log')$t$, 3);
select set_config('app.tenant_id', '99999999-9999-4999-8999-999999999999', false);
select pg_temp.expect_count('change_log: tenant B sees none of A''s rows',
    $t$select pg_temp.count_as('cmd_read', 'select count(*) from core.change_log')$t$, 0);
select set_config('app.tenant_id', '11111111-1111-4111-8111-111111111111', false);

-- The L2 guard (0013, F-IMPL-3): header_text, external_reference and line_text change only with a matching log row.
-- The status of period 10 of 2026, where the document lies, is whatever the tests above left it as.
create function pg_temp.real_status() returns text language sql as $$
    select status from core.fiscal_period where tenant_id = '11111111-1111-4111-8111-111111111111'
       and company_id = '22222222-2222-4222-8222-222222222201' and fiscal_year = 2026 and period_no = 10
$$;
create function pg_temp.l2log(p_entity text, p_line text, p_field text, p_old text, p_new text, p_status text default null) returns text language sql as $$
    select pg_temp.cl_row(p_entity, 'bbbbbbbb-0000-4000-8000-000000000001', null, p_line, p_old, p_new, 'L2',
                          coalesce(p_status, pg_temp.real_status()), 'CORRECTION', p_field)
$$;
select pg_temp.expect_ok('l2 guard: header_text changes with its log row',
    format($t$set local role cmd_post; %s; update core.journal_document set header_text = 'New' where tenant_id = '11111111-1111-4111-8111-111111111111' and document_id = 'bbbbbbbb-0000-4000-8000-000000000001'$t$,
        pg_temp.l2log('DOCUMENT', null, 'header_text', null, 'New')));
select pg_temp.expect_ok('l2 guard: external_reference changes with its log row',
    format($t$set local role cmd_post; %s; update core.journal_document set external_reference = 'PO-1' where tenant_id = '11111111-1111-4111-8111-111111111111' and document_id = 'bbbbbbbb-0000-4000-8000-000000000001'$t$,
        pg_temp.l2log('DOCUMENT', null, 'external_reference', null, 'PO-1')));
select pg_temp.expect_ok('l2 guard: line_text changes with its log row',
    format($t$set local role cmd_post; %s; update core.journal_line set line_text = 'Line' where tenant_id = '11111111-1111-4111-8111-111111111111' and document_id = 'bbbbbbbb-0000-4000-8000-000000000001' and line_no = 1$t$,
        pg_temp.l2log('LINE', '1', 'line_text', null, 'Line')));
select pg_temp.expect_error('l2 guard: the log row must carry the new value',
    format($t$set local role cmd_post; %s; update core.journal_document set header_text = 'Other' where tenant_id = '11111111-1111-4111-8111-111111111111' and document_id = 'bbbbbbbb-0000-4000-8000-000000000001'$t$,
        pg_temp.l2log('DOCUMENT', null, 'header_text', 'New', 'Wrong')), '%without a matching change_log row%');
select pg_temp.expect_error('l2 guard: the log row must carry the old value',
    format($t$set local role cmd_post; %s; update core.journal_document set header_text = 'Third' where tenant_id = '11111111-1111-4111-8111-111111111111' and document_id = 'bbbbbbbb-0000-4000-8000-000000000001'$t$,
        pg_temp.l2log('DOCUMENT', null, 'header_text', 'Not the old one', 'Third')), '%without a matching change_log row%');
select pg_temp.expect_error('l2 guard: a log row of an earlier transaction does not count',
    format($t$set local role cmd_post; %s; update core.journal_document set header_text = 'Third' where tenant_id = '11111111-1111-4111-8111-111111111111' and document_id = 'bbbbbbbb-0000-4000-8000-000000000001'$t$,
        replace(pg_temp.l2log('DOCUMENT', null, 'header_text', 'New', 'Third'), 'now()', $q$now() - interval '1 day'$q$)), '%without a matching change_log row%');
select pg_temp.expect_error('l2 guard: the period status of the log row must be the real one',
    format($t$set local role cmd_post; %s; update core.journal_document set header_text = 'Third' where tenant_id = '11111111-1111-4111-8111-111111111111' and document_id = 'bbbbbbbb-0000-4000-8000-000000000001'$t$,
        pg_temp.l2log('DOCUMENT', null, 'header_text', 'New', 'Third', case pg_temp.real_status() when 'OPEN' then 'SOFT_CLOSED' else 'OPEN' end)), '%without a matching change_log row%');
select pg_temp.expect_error('l2 guard: a log row of another field does not count',
    format($t$set local role cmd_post; %s; update core.journal_document set header_text = 'Third' where tenant_id = '11111111-1111-4111-8111-111111111111' and document_id = 'bbbbbbbb-0000-4000-8000-000000000001'$t$,
        pg_temp.l2log('DOCUMENT', null, 'external_reference', 'PO-1', 'Third')), '%without a matching change_log row%');
select pg_temp.expect_error('l2 guard: a typed dimension must name a dimension value',
    format($t$set local role cmd_post; %s; update core.journal_line set cost_center_id = 'dd000000-0000-4000-8000-0000000000ee' where tenant_id = '11111111-1111-4111-8111-111111111111' and document_id = 'bbbbbbbb-0000-4000-8000-000000000001' and line_no = 1$t$,
        pg_temp.l2log('LINE', '1', 'cost_center_id', null, 'dd000000-0000-4000-8000-0000000000ee')), '%DIMENSION_VALUE_INVALID%');
select pg_temp.expect_error('l2 guard: the posting role has no grant on an L1 column of a line',
    $t$set local role cmd_post; update core.journal_line set account_id = gen_random_uuid() where tenant_id = '11111111-1111-4111-8111-111111111111' and document_id = 'bbbbbbbb-0000-4000-8000-000000000001' and line_no = 1$t$,
    '%permission denied%');
select pg_temp.expect_error('l2 guard: the posting role has no grant on an L1 column of a document',
    format($t$set local role cmd_post; %s; update core.journal_document set header_text = 'Third', document_type = 'AR_INVOICE' where tenant_id = '11111111-1111-4111-8111-111111111111' and document_id = 'bbbbbbbb-0000-4000-8000-000000000001'$t$,
        pg_temp.l2log('DOCUMENT', null, 'header_text', 'New', 'Third')), '%permission denied%');
select pg_temp.expect_error('l2 guard: the configuration role cannot change an L2 column',
    $t$set local role cmd_config; update core.journal_document set header_text = 'x' where tenant_id = '11111111-1111-4111-8111-111111111111' and document_id = 'bbbbbbbb-0000-4000-8000-000000000001'$t$,
    '%permission denied%');

insert into core.command_log (tenant_id, command_log_id, company_id, idempotency_key, command_name, request_fingerprint, actor, correlation_id)
values ('11111111-1111-4111-8111-111111111111', 'eeeeeeee-0000-4000-8000-000000000001', '22222222-2222-4222-8222-222222222201',
        'mcl-1', 'gl-accounts:update', sha256('x'), 'tester', 'corr-mcl');
create function pg_temp.mcl(p_action text, p_field text, p_pii boolean, p_old text, p_new text, p_log text default 'eeeeeeee-0000-4000-8000-000000000001')
returns text language sql as $$
    select format($f$insert into core.master_change_log (tenant_id, change_set_id, entity_type, entity_id, action, field_name, is_pii,
        old_value, new_value, reason_code, changed_by, changed_at, correlation_id, command_log_id)
        values ('11111111-1111-4111-8111-111111111111', 'ffffffff-0000-4000-8000-000000000001', 'GL_ACCOUNT', 'x', %L, %L, %L, %L, %L,
        null, 'tester', now(), 'corr-mcl', %L); set constraints all immediate; set constraints all deferred$f$,
        p_action, p_field, p_pii, p_old, p_new, p_log)
$$;
select pg_temp.expect_ok('master_change_log: the configuration role can log a field change',
    format($t$set local role cmd_config; %s$t$, pg_temp.mcl('UPDATE', 'name', false, 'Old', 'New')));
select pg_temp.expect_ok('master_change_log: a create has no field and no values',
    format($t$set local role cmd_config; %s$t$, pg_temp.mcl('CREATE', null, false, null, null)));
select pg_temp.expect_ok('master_change_log: a personal field is logged without values',
    format($t$set local role cmd_config; %s$t$, pg_temp.mcl('UPDATE', 'email', true, null, null)));
select pg_temp.expect_ok('master_change_log: the retention role can log an anonymisation',
    format($t$set local role cmd_retention; %s$t$, pg_temp.mcl('ANONYMISE', 'email', true, null, null)));
select pg_temp.expect_error('master_change_log: the posting role cannot write it',
    format($t$set local role cmd_post; %s$t$, pg_temp.mcl('UPDATE', 'name', false, 'Old', 'New')), '%permission denied%');
select pg_temp.expect_error('master_change_log: a personal value is rejected',
    format($t$set local role cmd_config; %s$t$, pg_temp.mcl('UPDATE', 'email', true, 'a@b.dk', null)), '%violates check constraint%');
select pg_temp.expect_error('master_change_log: an update names its field',
    format($t$set local role cmd_config; %s$t$, pg_temp.mcl('UPDATE', null, false, 'Old', 'New')), '%violates check constraint%');
select pg_temp.expect_error('master_change_log: a create names no field',
    format($t$set local role cmd_config; %s$t$, pg_temp.mcl('CREATE', 'name', false, null, null)), '%violates check constraint%');
select pg_temp.expect_error('master_change_log: a deactivation carries no values',
    format($t$set local role cmd_config; %s$t$, pg_temp.mcl('DEACTIVATE', null, false, 'a', 'b')), '%violates check constraint%');
select pg_temp.expect_error('master_change_log: an update that changes nothing is rejected',
    format($t$set local role cmd_config; %s$t$, pg_temp.mcl('UPDATE', 'name', false, 'Same', 'Same')), '%violates check constraint%');
select pg_temp.expect_error('master_change_log: an unknown action is rejected',
    format($t$set local role cmd_config; %s$t$, pg_temp.mcl('RENAME', 'name', false, 'a', 'b')), '%violates check constraint%');
select pg_temp.expect_error('master_change_log: a row needs the command log row of its command at commit',
    format($t$set local role cmd_config; %s$t$, pg_temp.mcl('UPDATE', 'name', false, 'Old', 'New', 'eeeeeeee-0000-4000-8000-0000000000ff')),
    '%foreign key%');
select pg_temp.expect_error('master_change_log: rows cannot be updated', $t$update core.master_change_log set reason_code = 'X' where tenant_id = '11111111-1111-4111-8111-111111111111'$t$, '%append-only%');
select pg_temp.expect_error('master_change_log: rows cannot be deleted', $t$delete from core.master_change_log$t$, '%append-only%');
select pg_temp.expect_error('master_change_log: truncate is rejected', $t$truncate core.master_change_log$t$, '%append-only%');
select pg_temp.expect_count('master_change_log: a command role reads the rows of its tenant',
    $t$select pg_temp.count_as('cmd_read', 'select count(*) from core.master_change_log')$t$, 4);
select set_config('app.tenant_id', '99999999-9999-4999-8999-999999999999', false);
select pg_temp.expect_count('master_change_log: tenant B sees none of A''s rows',
    $t$select pg_temp.count_as('cmd_read', 'select count(*) from core.master_change_log')$t$, 0);
select set_config('app.tenant_id', '11111111-1111-4111-8111-111111111111', false);


-- ---------------------------------------------------------------------------------------------------------
-- Dimensions (0022, 02, 5.3 and 6, 07, 6.5)
-- ---------------------------------------------------------------------------------------------------------
select pg_temp.expect_count('dimension_type: every tenant has the four standard types',
    $t$select count(*) from core.dimension_type where tenant_id = '11111111-1111-4111-8111-111111111111' and kind = 'STANDARD'$t$, 4);
select pg_temp.expect_error('dimension_type: a standard type cannot be deactivated',
    $t$update core.dimension_type set active = false where tenant_id = '11111111-1111-4111-8111-111111111111' and standard_role = 'PROJECT'$t$, '%violates check constraint%');
select pg_temp.expect_error('dimension_type: the kind never changes',
    $t$update core.dimension_type set kind = 'FREE', standard_role = null where tenant_id = '11111111-1111-4111-8111-111111111111' and standard_role = 'PROJECT'$t$, '%never change%');
select pg_temp.expect_error('dimension_type: the code of a standard type never changes',
    $t$update core.dimension_type set type_code = 'PRJ' where tenant_id = '11111111-1111-4111-8111-111111111111' and standard_role = 'PROJECT'$t$, '%never changes%');

insert into core.dimension_type (tenant_id, dimension_type_id, type_code, name, kind, created_by, updated_by)
values ('11111111-1111-4111-8111-111111111111', 'dd000000-0000-4000-8000-0000000000f1', 'REGION', 'Region', 'FREE', 't', 't');
insert into core.dimension_value (tenant_id, dimension_value_id, dimension_type_id, value_code, name, valid_from, created_by, updated_by)
select '11111111-1111-4111-8111-111111111111', v.id::uuid, t.dimension_type_id, v.code, v.code, date '2026-01-01', 't', 't'
  from (values ('dd000000-0000-4000-8000-0000000000a1', 'CC1', 'COST_CENTER'), ('dd000000-0000-4000-8000-0000000000a2', 'PR1', 'PROJECT'),
               ('dd000000-0000-4000-8000-0000000000a5', 'CC2', 'COST_CENTER')) v (id, code, role)
  join core.dimension_type t on t.tenant_id = '11111111-1111-4111-8111-111111111111' and t.standard_role = v.role;
insert into core.dimension_value (tenant_id, dimension_value_id, dimension_type_id, value_code, name, valid_from, created_by, updated_by)
select '11111111-1111-4111-8111-111111111111', v.id::uuid, 'dd000000-0000-4000-8000-0000000000f1', v.code, v.code, date '2026-01-01', 't', 't'
  from (values ('dd000000-0000-4000-8000-0000000000a3', 'NORTH'), ('dd000000-0000-4000-8000-0000000000a4', 'SOUTH')) v (id, code);

select pg_temp.expect_error('dimension_value: a value is not its own parent',
    $t$update core.dimension_value set parent_value_id = dimension_value_id where tenant_id = '11111111-1111-4111-8111-111111111111' and value_code = 'CC1'$t$, '%HIERARCHY_CYCLE%');
select pg_temp.expect_ok('dimension_value: a parent of the same type',
    $t$update core.dimension_value set parent_value_id = 'dd000000-0000-4000-8000-0000000000a1' where tenant_id = '11111111-1111-4111-8111-111111111111' and value_code = 'CC2'$t$);
select pg_temp.expect_error('dimension_value: a cycle in the hierarchy is rejected',
    $t$update core.dimension_value set parent_value_id = 'dd000000-0000-4000-8000-0000000000a5' where tenant_id = '11111111-1111-4111-8111-111111111111' and value_code = 'CC1'$t$, '%HIERARCHY_CYCLE%');
select pg_temp.expect_error('dimension_value: a parent of another type is rejected',
    $t$update core.dimension_value set parent_value_id = 'dd000000-0000-4000-8000-0000000000a2' where tenant_id = '11111111-1111-4111-8111-111111111111' and value_code = 'CC1'$t$, '%foreign key%');
select pg_temp.expect_error('dimension_value: the end of validity is not before the start',
    $t$update core.dimension_value set valid_to = date '2025-12-31' where tenant_id = '11111111-1111-4111-8111-111111111111' and value_code = 'CC1'$t$, '%violates check constraint%');
select pg_temp.expect_error('dimension_value: the configuration role has no right to the posting tables',
    $t$set local role cmd_config; insert into core.journal_line_dimension (tenant_id, company_id, document_id, line_no, dimension_type_id, dimension_value_id)
       values ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', 'bbbbbbbb-0000-4000-8000-000000000001', 1,
               'dd000000-0000-4000-8000-0000000000f1', 'dd000000-0000-4000-8000-0000000000a3')$t$, '%permission denied%');
select pg_temp.expect_error('dimension_value: the posting role cannot create a value',
    $t$set local role cmd_post; insert into core.dimension_value (tenant_id, dimension_type_id, value_code, name, valid_from, created_by, updated_by)
       values ('11111111-1111-4111-8111-111111111111', 'dd000000-0000-4000-8000-0000000000f1', 'X', 'X', date '2026-01-01', 't', 't')$t$, '%permission denied%');
select pg_temp.expect_ok('dimension_value: the configuration role creates a value',
    $t$set local role cmd_config; insert into core.dimension_value (tenant_id, dimension_type_id, value_code, name, valid_from, created_by, updated_by)
       values ('11111111-1111-4111-8111-111111111111', 'dd000000-0000-4000-8000-0000000000f1', 'EAST', 'East', date '2026-01-01', 't', 't')$t$);

-- the typed columns of a line (L2, a log row of LINE with the field name)
select pg_temp.expect_error('typed dimension: a value changes only with its log row',
    $t$set local role cmd_post; update core.journal_line set cost_center_id = 'dd000000-0000-4000-8000-0000000000a1' where tenant_id = '11111111-1111-4111-8111-111111111111' and document_id = 'bbbbbbbb-0000-4000-8000-000000000001' and line_no = 1$t$,
    '%without a matching change_log row%');
select pg_temp.expect_ok('typed dimension: a cost center value in cost_center_id with its log row',
    format($t$set local role cmd_post; %s; update core.journal_line set cost_center_id = 'dd000000-0000-4000-8000-0000000000a1' where tenant_id = '11111111-1111-4111-8111-111111111111' and document_id = 'bbbbbbbb-0000-4000-8000-000000000001' and line_no = 1$t$,
        pg_temp.l2log('LINE', '1', 'cost_center_id', null, 'dd000000-0000-4000-8000-0000000000a1')));
select pg_temp.expect_error('typed dimension: a project value in cost_center_id is rejected',
    format($t$set local role cmd_post; %s; update core.journal_line set cost_center_id = 'dd000000-0000-4000-8000-0000000000a2' where tenant_id = '11111111-1111-4111-8111-111111111111' and document_id = 'bbbbbbbb-0000-4000-8000-000000000001' and line_no = 1$t$,
        pg_temp.l2log('LINE', '1', 'cost_center_id', 'dd000000-0000-4000-8000-0000000000a1', 'dd000000-0000-4000-8000-0000000000a2')), '%DIMENSION_VALUE_INVALID%');
select pg_temp.expect_error('typed dimension: a free value in a typed column is rejected',
    format($t$set local role cmd_post; %s; update core.journal_line set project_id = 'dd000000-0000-4000-8000-0000000000a3' where tenant_id = '11111111-1111-4111-8111-111111111111' and document_id = 'bbbbbbbb-0000-4000-8000-000000000001' and line_no = 1$t$,
        pg_temp.l2log('LINE', '1', 'project_id', null, 'dd000000-0000-4000-8000-0000000000a3')), '%DIMENSION_VALUE_INVALID%');
select pg_temp.expect_error('typed dimension: a referenced value cannot change its type',
    $t$update core.dimension_value set dimension_type_id = 'dd000000-0000-4000-8000-0000000000f1' where tenant_id = '11111111-1111-4111-8111-111111111111' and value_code = 'CC1'$t$, '%IMMUTABLE_AFTER_POSTING%');
select pg_temp.expect_ok('typed dimension: a value that is not referenced can change its name',
    $t$update core.dimension_value set name = 'Cost center two' where tenant_id = '11111111-1111-4111-8111-111111111111' and value_code = 'CC2'$t$);

-- the free dimensions of a line
create function pg_temp.dimlog(p_old text, p_new text) returns text language sql as $$
    select format($q$insert into core.change_log (tenant_id, change_set_id, company_id, entity_type, document_id, line_no, sub_key, field_name, value_type,
            old_value, new_value, lock_class, period_status, reason_code, changed_by, changed_at, correlation_id, idempotency_key)
        values ('11111111-1111-4111-8111-111111111111', 'cccccccc-0000-4000-8000-000000000002', '22222222-2222-4222-8222-222222222201', 'LINE_DIMENSION',
            'bbbbbbbb-0000-4000-8000-000000000001', 1, 'dd000000-0000-4000-8000-0000000000f1', 'dimension_value_id', 'ID', %L, %L, 'L2', %L, 'CORRECTION',
            'tester', now(), 'corr-dim', 'idem-dim')$q$, p_old, p_new, pg_temp.real_status())
$$;
select pg_temp.expect_ok('free dimension: the posting role inserts a row for a FREE type',
    $t$set local role cmd_post; insert into core.journal_line_dimension (tenant_id, company_id, document_id, line_no, dimension_type_id, dimension_value_id)
       values ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', 'bbbbbbbb-0000-4000-8000-000000000001', 1,
               'dd000000-0000-4000-8000-0000000000f1', 'dd000000-0000-4000-8000-0000000000a3')$t$);
select pg_temp.expect_error('free dimension: one row per line and type',
    $t$set local role cmd_post; insert into core.journal_line_dimension (tenant_id, company_id, document_id, line_no, dimension_type_id, dimension_value_id)
       values ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', 'bbbbbbbb-0000-4000-8000-000000000001', 1,
               'dd000000-0000-4000-8000-0000000000f1', 'dd000000-0000-4000-8000-0000000000a4')$t$, '%duplicate key%');
select pg_temp.expect_error('free dimension: a value of another type is rejected',
    $t$set local role cmd_post; insert into core.journal_line_dimension (tenant_id, company_id, document_id, line_no, dimension_type_id, dimension_value_id)
       values ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', 'bbbbbbbb-0000-4000-8000-000000000001', 2,
               'dd000000-0000-4000-8000-0000000000f1', 'dd000000-0000-4000-8000-0000000000a1')$t$, '%DIMENSION_VALUE_INVALID%');
select pg_temp.expect_error('free dimension: a standard type is not a free dimension',
    $t$set local role cmd_post; insert into core.journal_line_dimension (tenant_id, company_id, document_id, line_no, dimension_type_id, dimension_value_id)
       select '11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', 'bbbbbbbb-0000-4000-8000-000000000001', 2, t.dimension_type_id, null
         from core.dimension_type t where t.standard_role = 'PROJECT'$t$, '%must be of kind FREE%');
select pg_temp.expect_error('free dimension: the value changes only with its log row',
    $t$set local role cmd_post; update core.journal_line_dimension set dimension_value_id = 'dd000000-0000-4000-8000-0000000000a4' where tenant_id = '11111111-1111-4111-8111-111111111111'$t$, '%without a matching change_log row%');
select pg_temp.expect_ok('free dimension: the value changes with its log row',
    format($t$set local role cmd_post; %s; update core.journal_line_dimension set dimension_value_id = 'dd000000-0000-4000-8000-0000000000a4' where tenant_id = '11111111-1111-4111-8111-111111111111' and line_no = 1$t$,
        pg_temp.dimlog('dd000000-0000-4000-8000-0000000000a3', 'dd000000-0000-4000-8000-0000000000a4')));
select pg_temp.expect_ok('free dimension: a cleared value is a tombstone',
    format($t$set local role cmd_post; %s; update core.journal_line_dimension set dimension_value_id = null where tenant_id = '11111111-1111-4111-8111-111111111111' and line_no = 1$t$,
        pg_temp.dimlog('dd000000-0000-4000-8000-0000000000a4', null)));
select pg_temp.expect_error('free dimension: the type of a row never changes',
    $t$update core.journal_line_dimension set dimension_type_id = (select dimension_type_id from core.dimension_type where standard_role = 'PROJECT' and tenant_id = '11111111-1111-4111-8111-111111111111')$t$, '%L1 column%');
select pg_temp.expect_error('free dimension: no delete', $t$delete from core.journal_line_dimension$t$, '%no hard delete%');
select pg_temp.expect_error('dimension_value: no delete', $t$delete from core.dimension_value$t$, '%no hard delete%');
select pg_temp.expect_error('dimension_type: no delete', $t$delete from core.dimension_type$t$, '%no hard delete%');

-- account rules and tenant isolation
select pg_temp.expect_ok('account_dimension_rule: the configuration role sets a rule',
    $t$set local role cmd_config; insert into core.account_dimension_rule (tenant_id, company_id, account_id, dimension_type_id, rule, created_by, updated_by)
       select '11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', '55555555-5555-4555-8555-555555555503', t.dimension_type_id, 'MANDATORY', 't', 't'
         from core.dimension_type t where t.standard_role = 'COST_CENTER'$t$);
select pg_temp.expect_error('account_dimension_rule: a rule is MANDATORY, OPTIONAL or FORBIDDEN',
    $t$update core.account_dimension_rule set rule = 'SOMETIMES' where tenant_id = '11111111-1111-4111-8111-111111111111'$t$, '%violates check constraint%');
select pg_temp.expect_error('account_dimension_rule: the posting role cannot set a rule',
    $t$set local role cmd_post; update core.account_dimension_rule set rule = 'FORBIDDEN' where tenant_id = '11111111-1111-4111-8111-111111111111'$t$, '%permission denied%');
select pg_temp.expect_count('dimensions: a command role reads the rows of its tenant',
    $t$select pg_temp.count_as('cmd_post', 'select (select count(*) from core.dimension_value) + (select count(*) from core.journal_line_dimension) + (select count(*) from core.account_dimension_rule)')$t$, 8);
select set_config('app.tenant_id', '99999999-9999-4999-8999-999999999999', false);
select pg_temp.expect_count('dimensions: tenant B sees none of A''s rows',
    $t$select pg_temp.count_as('cmd_post', 'select (select count(*) from core.dimension_value) + (select count(*) from core.journal_line_dimension) + (select count(*) from core.account_dimension_rule)')$t$, 0);
select set_config('app.tenant_id', '11111111-1111-4111-8111-111111111111', false);

-- ---------------------------------------------------------------------------------------------------------
-- Result
-- ---------------------------------------------------------------------------------------------------------
select name, case when ok then 'PASS' else 'FAIL' end as result, detail from test_result order by seq;
rollback;
