-- 0006 row-level security, grants, protection and integrity triggers (spec 20, section 3, ADR-209; 02, 10; 04, 8)
--
-- Model: the application connects as a login role (core_app, core_job, core_retention, NOINHERIT) and starts
-- every transaction with
--     SET LOCAL ROLE cmd_post;                     -- the command role of the command, never more
--     SET LOCAL app.tenant_id = '<tenant uuid>';   -- from the validated token (ADR-209)
-- Three layers protect the ledger: grants, these triggers, and the hash chain with its anchors (02, 9).
-- A privileged user who disables a trigger is still caught by `chains:verify`.

-- ---------------------------------------------------------------------------------------------------------
-- 1. Row-level security on every tenant table, forced also for the table owner
-- ---------------------------------------------------------------------------------------------------------
do $$
declare t text;
begin
    for t in select tablename from pg_tables where schemaname = 'core' order by tablename loop
        execute format('alter table core.%I enable row level security', t);
        execute format('alter table core.%I force row level security', t);
        if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = t and policyname = 'tenant_isolation') then
            execute format('create policy tenant_isolation on core.%I using (tenant_id = core.current_tenant_id()) '
                           'with check (tenant_id = core.current_tenant_id())', t);
        end if;
    end loop;
end $$;

-- Platform tables stay outside tenant isolation (07, A6), but RLS is on as a second line of defence and so that the
-- Supabase Security Advisor has nothing to report. Reference data is readable by everyone who may query it, a
-- tenant is readable only as the tenant of the transaction, and the migration log is not readable by command roles.
-- The migration role bypasses RLS and is the only one that writes these tables.
alter table platform.ref_currency enable row level security;
alter table platform.ref_country enable row level security;
alter table platform.tenant enable row level security;
alter table platform.schema_migration enable row level security;
drop policy if exists ref_read on platform.ref_currency;
create policy ref_read on platform.ref_currency for select using (true);
drop policy if exists ref_read on platform.ref_country;
create policy ref_read on platform.ref_country for select using (true);
drop policy if exists tenant_read on platform.tenant;
create policy tenant_read on platform.tenant for select using (tenant_id = core.current_tenant_id());
-- platform.schema_migration has no policy: no command role can read or write it.

-- A command role may move only its own chain (02, 9.2: the owning command role updates the head).
drop policy if exists chain_head_period on core.chain_head;
create policy chain_head_period on core.chain_head as restrictive for update to cmd_period
    using (chain_type = 'PERIOD_EVENT') with check (chain_type = 'PERIOD_EVENT');
drop policy if exists chain_head_post on core.chain_head;
create policy chain_head_post on core.chain_head as restrictive for update to cmd_post
    using (chain_type = 'LEDGER') with check (chain_type = 'LEDGER');

-- ---------------------------------------------------------------------------------------------------------
-- 2. Grants. No role has DELETE or TRUNCATE on the transaction layer (ADR-001). Only cmd_retention may purge
--    app events of the outbox (15, 5.4).
-- ---------------------------------------------------------------------------------------------------------
revoke all on all tables in schema core from public;
grant select on all tables in schema core to cmd_read, cmd_config, cmd_period, cmd_post;
grant select on all tables in schema platform to cmd_read, cmd_config, cmd_period, cmd_post, cmd_retention;
grant select, delete on core.outbox_event to cmd_retention;

-- Configuration role (write.config, write.master, period.admin parts): create and change configuration.
grant insert on core.tenant_setting, core.tenant_currency, core.fiscal_calendar, core.company_group, core.company,
      core.ledger, core.group_membership, core.company_setting_version, core.exchange_rate, core.gl_account,
      core.number_series, core.number_series_assignment, core.posting_window,
      core.chain_head, core.outbox_head, core.command_log, core.command_log_result, core.outbox_event
      to cmd_config;
grant update on core.tenant_setting, core.tenant_currency, core.fiscal_calendar, core.company_group, core.company,
      core.ledger, core.gl_account, core.number_series to cmd_config;
grant update (valid_to) on core.group_membership to cmd_config;
grant update (rate, source, first_used_at) on core.exchange_rate to cmd_config;

-- Period role (period.admin, period.hard_close, year_end.*): open years, change period status, own chain.
grant insert on core.fiscal_year, core.fiscal_period, core.period_event, core.number_range_counter,
      core.chain_head, core.command_log, core.command_log_result, core.outbox_event to cmd_period;
grant update (status, year_end_document_id, last_event_id) on core.fiscal_year to cmd_period;
grant update (status, last_event_id) on core.fiscal_period to cmd_period;
grant update (last_seq, last_hash, hash_version, updated_at) on core.chain_head to cmd_period;
grant update (last_position) on core.outbox_head to cmd_period;
grant update (first_used_at) on core.exchange_rate to cmd_period;

-- Posting role (journal.post): documents and lines, counter, ledger chain head, outbox.
grant insert on core.journal_document, core.journal_line, core.command_log, core.command_log_result,
      core.outbox_event, core.chain_head to cmd_post;
grant update (status, reversed_by_document_id) on core.journal_document to cmd_post;
grant update (last_number, updated_at) on core.number_range_counter to cmd_post;
grant update (last_seq, last_hash, hash_version, updated_at) on core.chain_head to cmd_post;
grant update (last_position) on core.outbox_head to cmd_post;
grant update (first_used_at) on core.exchange_rate to cmd_post;

-- ---------------------------------------------------------------------------------------------------------
-- 3. Append-only and no-delete triggers
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.reject_change() returns trigger language plpgsql as $$
begin
    if tg_op = 'DELETE' and tg_table_name = 'outbox_event' and pg_has_role(current_user, 'cmd_retention', 'member') then
        return old;
    end if;
    raise exception 'core.% is append-only: % is not allowed', tg_table_name, tg_op using errcode = '23000';
end $$;

create or replace function core.reject_delete() returns trigger language plpgsql as $$
begin
    raise exception 'core.% has no hard delete (ADR-073): deactivate instead', tg_table_name using errcode = '23000';
end $$;

do $$
declare t text;
begin
    foreach t in array array['period_event', 'command_log', 'command_log_result', 'outbox_event'] loop
        execute format('drop trigger if exists %I on core.%I', t || '_append_only', t);
        execute format('create trigger %I before update or delete on core.%I for each row execute function core.reject_change()',
                       t || '_append_only', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
        execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_change()',
                       t || '_no_truncate', t);
    end loop;
    foreach t in array array['journal_document', 'journal_line', 'chain_head', 'number_range_counter', 'fiscal_year',
                             'fiscal_period', 'company', 'gl_account', 'exchange_rate', 'number_series',
                             'number_series_assignment', 'posting_window', 'company_setting_version', 'ledger',
                             'group_membership', 'outbox_head'] loop
        execute format('drop trigger if exists %I on core.%I', t || '_no_delete', t);
        execute format('create trigger %I before delete on core.%I for each row execute function core.reject_delete()',
                       t || '_no_delete', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
        execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_delete()',
                       t || '_no_truncate', t);
    end loop;
end $$;

-- ---------------------------------------------------------------------------------------------------------
-- 4. Lock-class guards (02, 10, D2): L1 never changes, S columns only as allowed
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.guard_journal_document() returns trigger language plpgsql as $$
begin
    -- Only the S columns status and reversed_by_document_id may change. The L2 columns header_text and
    -- external_reference stay locked until the change command exists (it needs a change_log row, 02, 8).
    if (to_jsonb(new) - array['status', 'reversed_by_document_id'])
       is distinct from (to_jsonb(old) - array['status', 'reversed_by_document_id']) then
        raise exception 'journal_document: an L1 or L2 column cannot change' using errcode = '23000';
    end if;
    if new.status is distinct from old.status and not (old.status = 'posted' and new.status = 'reversed') then
        raise exception 'journal_document: status can only change from posted to reversed' using errcode = '23000';
    end if;
    if old.reversed_by_document_id is not null and new.reversed_by_document_id is distinct from old.reversed_by_document_id then
        raise exception 'journal_document: reversed_by_document_id is never overwritten (B1)' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists journal_document_guard on core.journal_document;
create trigger journal_document_guard before update on core.journal_document
    for each row execute function core.guard_journal_document();

create or replace function core.guard_journal_line() returns trigger language plpgsql as $$
begin
    -- No column of a line changes yet. The L2 columns (text, dimensions) open with the change command.
    raise exception 'journal_line: lines are never updated' using errcode = '23000';
end $$;
drop trigger if exists journal_line_guard on core.journal_line;
create trigger journal_line_guard before update on core.journal_line
    for each row execute function core.guard_journal_line();

-- Gapless numbering (ADR-007): a counter moves by exactly one, and only forward.
create or replace function core.guard_number_counter() returns trigger language plpgsql as $$
begin
    if (to_jsonb(new) - array['last_number', 'updated_at']) is distinct from (to_jsonb(old) - array['last_number', 'updated_at']) then
        raise exception 'number_range_counter: key columns cannot change' using errcode = '23000';
    end if;
    if new.last_number <> old.last_number + 1 then
        raise exception 'number_range_counter: last_number must increase by exactly 1 (% to %)', old.last_number, new.last_number
            using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists number_counter_guard on core.number_range_counter;
create trigger number_counter_guard before update on core.number_range_counter
    for each row execute function core.guard_number_counter();

-- Chain heads: created at sequence 0, then moved by exactly one (02, 9.2).
create or replace function core.guard_chain_head() returns trigger language plpgsql as $$
begin
    if tg_op = 'INSERT' then
        if new.last_seq <> 0 then
            raise exception 'chain_head: a new head starts at last_seq 0 with the genesis hash' using errcode = '23000';
        end if;
        return new;
    end if;
    if (to_jsonb(new) - array['last_seq', 'last_hash', 'hash_version', 'updated_at'])
       is distinct from (to_jsonb(old) - array['last_seq', 'last_hash', 'hash_version', 'updated_at']) then
        raise exception 'chain_head: key columns cannot change' using errcode = '23000';
    end if;
    if new.last_seq <> old.last_seq + 1 then
        raise exception 'chain_head: last_seq must increase by exactly 1 (% to %)', old.last_seq, new.last_seq using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists chain_head_guard on core.chain_head;
create trigger chain_head_guard before insert or update on core.chain_head
    for each row execute function core.guard_chain_head();

create or replace function core.guard_outbox_head() returns trigger language plpgsql as $$
begin
    if new.last_position <= old.last_position then
        raise exception 'outbox_head: last_position only moves forward' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists outbox_head_guard on core.outbox_head;
create trigger outbox_head_guard before update on core.outbox_head
    for each row execute function core.guard_outbox_head();

-- Period status is an S column: it changes only together with the matching, latest period_event (04, 8, ADR-041).
create or replace function core.guard_period_status() returns trigger language plpgsql as $$
declare ev core.period_event%rowtype; latest bigint;
begin
    if tg_table_name = 'fiscal_period' then
        if (to_jsonb(new) - array['status', 'last_event_id']) is distinct from (to_jsonb(old) - array['status', 'last_event_id']) then
            raise exception 'fiscal_period: an L1 column cannot change' using errcode = '23000';
        end if;
    else
        if (to_jsonb(new) - array['status', 'last_event_id', 'year_end_document_id'])
           is distinct from (to_jsonb(old) - array['status', 'last_event_id', 'year_end_document_id']) then
            raise exception 'fiscal_year: an L1 column cannot change' using errcode = '23000';
        end if;
    end if;
    if new.status is distinct from old.status or new.last_event_id is distinct from old.last_event_id then
        select * into ev from core.period_event where tenant_id = new.tenant_id and event_id = new.last_event_id;
        if not found then
            raise exception '%: status change without a period_event', tg_table_name using errcode = '23000';
        end if;
        select max(event_seq) into latest from core.period_event where tenant_id = new.tenant_id and company_id = new.company_id;
        if ev.company_id <> new.company_id or ev.fiscal_year <> new.fiscal_year or ev.to_status <> new.status
           or ev.event_seq <> latest
           or (tg_table_name = 'fiscal_period' and ev.period_no is distinct from new.period_no) then
            raise exception '%: the period_event does not match the status change', tg_table_name using errcode = '23000';
        end if;
    end if;
    return new;
end $$;
drop trigger if exists fiscal_period_guard on core.fiscal_period;
create trigger fiscal_period_guard before update on core.fiscal_period
    for each row execute function core.guard_period_status();
drop trigger if exists fiscal_year_guard on core.fiscal_year;
create trigger fiscal_year_guard before update on core.fiscal_year
    for each row execute function core.guard_period_status();

-- Exchange rates are usage-locked (ADR-019, 07, 7.2): a used rate never changes, the identity never changes.
create or replace function core.guard_exchange_rate() returns trigger language plpgsql as $$
begin
    if (to_jsonb(new) - array['rate', 'source', 'first_used_at']) is distinct from (to_jsonb(old) - array['rate', 'source', 'first_used_at']) then
        raise exception 'exchange_rate: type, currencies and valid_from cannot change' using errcode = '23000';
    end if;
    if old.first_used_at is not null and (new.rate is distinct from old.rate or new.source is distinct from old.source
                                         or new.first_used_at is distinct from old.first_used_at) then
        raise exception 'exchange_rate: a used rate cannot change' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists exchange_rate_guard on core.exchange_rate;
create trigger exchange_rate_guard before update on core.exchange_rate
    for each row execute function core.guard_exchange_rate();

-- Immutable-after-posting columns (IAP, 07, 2.3).
create or replace function core.guard_gl_account() returns trigger language plpgsql as $$
begin
    if new.tax_account is distinct from old.tax_account and not (new.tax_account and not old.tax_account) then
        raise exception 'gl_account: tax_account is never reset' using errcode = '23000';
    end if;
    if (new.account_type, new.open_item_mode, new.currency_code, new.account_no)
       is distinct from (old.account_type, old.open_item_mode, old.currency_code, old.account_no)
       and exists (select 1 from core.journal_line l
                   where l.tenant_id = old.tenant_id and l.company_id = old.company_id and l.account_id = old.account_id) then
        raise exception 'gl_account: account type, open item mode, currency and number are fixed once the account has postings'
            using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists gl_account_guard on core.gl_account;
create trigger gl_account_guard before update on core.gl_account
    for each row execute function core.guard_gl_account();

create or replace function core.guard_company() returns trigger language plpgsql as $$
begin
    if (new.country_code, new.local_currency, new.reporting_currency) is distinct from
       (old.country_code, old.local_currency, old.reporting_currency)
       and exists (select 1 from core.journal_document d where d.tenant_id = old.tenant_id and d.company_id = old.company_id) then
        raise exception 'company: country and currencies are fixed once the company has postings' using errcode = '23000';
    end if;
    if new.fiscal_calendar_id is distinct from old.fiscal_calendar_id
       and exists (select 1 from core.fiscal_year y where y.tenant_id = old.tenant_id and y.company_id = old.company_id) then
        raise exception 'company: the calendar is fixed once a fiscal year is open' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists company_guard on core.company;
create trigger company_guard before update on core.company
    for each row execute function core.guard_company();

-- ---------------------------------------------------------------------------------------------------------
-- 5. Integrity checks at commit (deferred constraint triggers): backstops for the posting command (02, 3)
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.check_document_balance() returns trigger language plpgsql as $$
declare s_tc numeric; s_lc numeric; s_gc numeric; s_rc numeric; n int; n_rc int;
begin
    select sum(amount_tc), sum(amount_lc), sum(amount_gc), sum(amount_rc), count(*), count(amount_rc)
      into s_tc, s_lc, s_gc, s_rc, n, n_rc
      from core.journal_line
     where tenant_id = new.tenant_id and company_id = new.company_id and document_id = new.document_id;
    if s_tc <> 0 or s_lc <> 0 or s_gc <> 0 then
        raise exception 'document % does not balance: tc %, lc %, gc %', new.document_id, s_tc, s_lc, s_gc using errcode = '23514';
    end if;
    if n_rc > 0 and (n_rc <> n or s_rc <> 0) then
        raise exception 'document % has an unbalanced or partial reporting currency', new.document_id using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists journal_line_balance on core.journal_line;
create constraint trigger journal_line_balance after insert on core.journal_line
    deferrable initially deferred for each row execute function core.check_document_balance();

create or replace function core.check_document_has_lines() returns trigger language plpgsql as $$
begin
    if not exists (select 1 from core.journal_line where tenant_id = new.tenant_id and company_id = new.company_id
                   and document_id = new.document_id) then
        raise exception 'document % has no lines', new.document_id using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists journal_document_has_lines on core.journal_document;
create constraint trigger journal_document_has_lines after insert on core.journal_document
    deferrable initially deferred for each row execute function core.check_document_has_lines();

-- The ledger chain: the head equals the newest document, the document links to its predecessor, and sequence 1
-- starts from the genesis value of 02, 9.3a (computed here in SQL from the same text as the C# library).
create or replace function core.check_ledger_chain() returns trigger language plpgsql as $$
declare h core.chain_head%rowtype; max_seq bigint; max_hash bytea; expected_prev bytea;
begin
    select * into h from core.chain_head
     where tenant_id = new.tenant_id and company_id = new.company_id and chain_type = 'LEDGER' and scope_key = new.ledger_id::text;
    if not found then
        raise exception 'no LEDGER chain head for ledger %', new.ledger_id using errcode = '23514';
    end if;
    select chain_seq, row_hash into max_seq, max_hash from core.journal_document
     where tenant_id = new.tenant_id and company_id = new.company_id and ledger_id = new.ledger_id
     order by chain_seq desc limit 1;
    if h.last_seq <> max_seq or h.last_hash <> max_hash then
        raise exception 'LEDGER chain head (seq %) does not match the newest document (seq %)', h.last_seq, max_seq using errcode = '23514';
    end if;
    if new.chain_seq = 1 then
        expected_prev := sha256(convert_to('genesis|' || new.tenant_id::text || '|' || new.company_id::text || '|LEDGER|' || new.ledger_id::text, 'UTF8'));
    else
        select row_hash into expected_prev from core.journal_document
         where tenant_id = new.tenant_id and company_id = new.company_id and ledger_id = new.ledger_id and chain_seq = new.chain_seq - 1;
    end if;
    if expected_prev is distinct from new.prev_hash then
        raise exception 'document % does not link to its predecessor in the chain', new.document_id using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists journal_document_chain on core.journal_document;
create constraint trigger journal_document_chain after insert on core.journal_document
    deferrable initially deferred for each row execute function core.check_ledger_chain();

-- Outbox positions never run past the head (15, 5.2).
create or replace function core.check_outbox_position() returns trigger language plpgsql as $$
declare head bigint;
begin
    select last_position into head from core.outbox_head where tenant_id = new.tenant_id;
    if head is null or new.position > head then
        raise exception 'outbox_event position % is beyond outbox_head %', new.position, head using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists outbox_event_position on core.outbox_event;
create constraint trigger outbox_event_position after insert on core.outbox_event
    deferrable initially deferred for each row execute function core.check_outbox_position();

insert into platform.schema_migration (version, description)
values ('0006', 'security: RLS, grants, protection and integrity triggers')
on conflict (version) do nothing;
