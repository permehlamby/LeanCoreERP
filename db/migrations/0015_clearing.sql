-- 0015 clearing (03): the clearing record, its items, the clearing chain, the clearing command role and the rule that the
-- S columns of an open item are a cache of the clearing items (03, 4.3). No command writes these tables yet: clearings:post
-- (03, 5) and clearings:reset come next. The hash is computed by the application (ADR-211, 03, 6.1), SQL checks the links.

-- ---------------------------------------------------------------------------------------------------------
-- The clearing command role (02, 10: "the clearing command role"). It is not a member of cmd_post: a restrictive policy
-- of 0006 limits cmd_post to the LEDGER head, and membership would make both policies apply. It gets the grants it needs
-- itself: its own chain, a CLEARING difference document with its number and its LEDGER head, and the S columns of open items.
-- ---------------------------------------------------------------------------------------------------------
do $$ begin
    if not exists (select 1 from pg_roles where rolname = 'cmd_clearing') then
        create role cmd_clearing nologin;
    end if;
end $$;
grant cmd_clearing to core_app;
do $$ begin
    if exists (select 1 from pg_roles where rolname = 'postgres') then
        grant cmd_clearing to postgres;
    end if;
end $$;

grant usage on schema platform, core to cmd_clearing;

-- ---------------------------------------------------------------------------------------------------------
-- clearing (03, 4.1)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.clearing (
    tenant_id            uuid not null,
    company_id           uuid not null,
    clearing_id          uuid not null default gen_random_uuid(),
    chain_seq            bigint not null check (chain_seq >= 1),
    clearing_type        core.code not null check (clearing_type in ('CLEAR', 'RESET')),
    clearing_date        date not null,
    fiscal_year          int not null,
    fiscal_period        int not null,
    cleared_at           timestamptz not null,
    account_id           uuid not null,
    partner_id           uuid,                 -- no FK until business_partner exists. Null only for GL_OPEN_ITEM accounts
    document_id          uuid,                 -- the difference document, if one was posted
    reset_of_clearing_id uuid,
    clearing_method      core.code not null check (clearing_method in ('MANUAL', 'AUTO_MATCH', 'PAYMENT_ALLOCATION', 'REVERSAL')),
    matching_ref         varchar(100),
    source_system        core.code not null,
    idempotency_key      varchar(100) not null,
    command_name         core.code not null,
    cleared_by           varchar(100) not null,
    correlation_id       varchar(100) not null,
    note                 varchar(200),         -- L2. Changes will be logged with entity type CLEARING (02, 8). Not changeable yet
    prev_hash            core.hash32 not null,
    row_hash             core.hash32 not null,
    hash_version         smallint not null check (hash_version in (1, 2)),
    primary key (tenant_id, company_id, clearing_id),
    unique (tenant_id, company_id, chain_seq),
    unique (tenant_id, idempotency_key),
    unique (tenant_id, company_id, clearing_id, clearing_date),    -- the target of the date copy of clearing_item
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, company_id, account_id) references core.gl_account (tenant_id, company_id, account_id),
    foreign key (tenant_id, company_id, document_id) references core.journal_document (tenant_id, company_id, document_id),
    foreign key (tenant_id, company_id, reset_of_clearing_id) references core.clearing (tenant_id, company_id, clearing_id),
    foreign key (tenant_id, company_id, fiscal_year, fiscal_period) references core.fiscal_period (tenant_id, company_id, fiscal_year, period_no),
    check ((reset_of_clearing_id is not null) = (clearing_type = 'RESET')),
    check (reset_of_clearing_id is distinct from clearing_id)
);
create unique index if not exists clearing_document on core.clearing (tenant_id, company_id, document_id) where document_id is not null;
create unique index if not exists clearing_reset_of on core.clearing (tenant_id, company_id, reset_of_clearing_id) where reset_of_clearing_id is not null;
create index if not exists clearing_account_partner on core.clearing (tenant_id, company_id, account_id, partner_id);

-- ---------------------------------------------------------------------------------------------------------
-- clearing_item (03, 4.2)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.clearing_item (
    tenant_id             uuid not null,
    company_id            uuid not null,
    clearing_id           uuid not null,
    item_no               int not null check (item_no >= 1),
    clearing_date         date not null,
    open_item_document_id uuid not null,
    open_item_line_no     int not null,
    currency_tc           char(3) not null references platform.ref_currency (currency_code),
    cleared_amount_tc     core.amt not null,
    cleared_amount_lc     core.amt not null,
    cleared_amount_gc     core.amt not null,
    cleared_amount_rc     core.amt,
    primary key (tenant_id, company_id, clearing_id, item_no),
    unique (tenant_id, company_id, clearing_id, open_item_document_id, open_item_line_no),
    foreign key (tenant_id, company_id, clearing_id, clearing_date) references core.clearing (tenant_id, company_id, clearing_id, clearing_date),
    foreign key (tenant_id, company_id, open_item_document_id, open_item_line_no)
        references core.journal_line_open_item (tenant_id, company_id, document_id, line_no)
);
create index if not exists clearing_item_open_item on core.clearing_item
    (tenant_id, company_id, open_item_document_id, open_item_line_no, clearing_date);

-- The open item points at the clearing that touched it last (S column of 0014).
do $$ begin
    if not exists (select 1 from pg_constraint where conname = 'journal_line_open_item_last_clearing_fk') then
        alter table core.journal_line_open_item add constraint journal_line_open_item_last_clearing_fk
            foreign key (tenant_id, company_id, last_clearing_id) references core.clearing (tenant_id, company_id, clearing_id);
    end if;
end $$;

-- ---------------------------------------------------------------------------------------------------------
-- Security: forced RLS, read for every command role, append-only, insert for the clearing role only.
-- ---------------------------------------------------------------------------------------------------------
do $$
declare t text;
begin
    foreach t in array array['clearing', 'clearing_item'] loop
        execute format('alter table core.%I enable row level security', t);
        execute format('alter table core.%I force row level security', t);
        if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = t and policyname = 'tenant_isolation') then
            execute format('create policy tenant_isolation on core.%I using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id())', t);
        end if;
        execute format('revoke all on core.%I from public', t);
        execute format('grant select on core.%I to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing', t);
        execute format('drop trigger if exists %I on core.%I', t || '_append_only', t);
        execute format('create trigger %I before update or delete on core.%I for each row execute function core.reject_change()', t || '_append_only', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
        execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_change()', t || '_no_truncate', t);
    end loop;
end $$;
grant insert on core.clearing, core.clearing_item to cmd_clearing;

-- What the clearing role needs besides its own tables. A difference document (type CLEARING) is a journal document, so the role
-- inserts documents, lines and open item rows, takes a number, moves the LEDGER head and, for a reset, marks the mirrored
-- document as reversed. It locks the period rows in share mode (A1) and writes the command log and the outbox.
grant select on all tables in schema core to cmd_clearing;
grant select on all tables in schema platform to cmd_clearing;
grant insert on core.journal_document, core.journal_line, core.journal_line_open_item, core.command_log, core.command_log_result,
      core.outbox_event, core.chain_head to cmd_clearing;
grant update (status, reversed_by_document_id) on core.journal_document to cmd_clearing;
grant update (last_number, updated_at) on core.number_range_counter to cmd_clearing;
grant update (last_seq, last_hash, hash_version, updated_at) on core.chain_head to cmd_clearing;
grant update (last_position) on core.outbox_head to cmd_clearing;
grant update (last_event_id) on core.fiscal_period, core.fiscal_year to cmd_clearing;
-- The S columns of an open item (02, 5A, 10). Only the clearing role changes them.
grant update (clearing_status, cleared_amount_tc, cleared_amount_lc, cleared_amount_gc, cleared_amount_rc, last_clearing_id)
      on core.journal_line_open_item to cmd_clearing;

-- A command role moves only its own chain (02, 9.2). The clearing role moves CLEARING, and LEDGER for its difference documents.
drop policy if exists chain_head_clearing on core.chain_head;
create policy chain_head_clearing on core.chain_head as restrictive for update to cmd_clearing
    using (chain_type in ('LEDGER', 'CLEARING')) with check (chain_type in ('LEDGER', 'CLEARING'));

-- ---------------------------------------------------------------------------------------------------------
-- The S columns of an open item (03, 4.3). The guard of 0014 rejected every change. Now the clearing role may change them, and
-- the check at commit below makes sure that they are what the clearing items say. A change of an L1 column is still rejected.
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.guard_journal_line_open_item() returns trigger language plpgsql as $$
declare
    v_year int; v_period int;
    f text;
    l2 constant text[] := array['baseline_date', 'payment_terms_id', 'due_date', 'discount_due_date', 'discount_amount_tc',
                                'payment_block', 'dunning_block', 'payment_reference', 'bank_account_id'];
    s  constant text[] := array['clearing_status', 'cleared_amount_tc', 'cleared_amount_lc', 'cleared_amount_gc', 'cleared_amount_rc', 'last_clearing_id'];
begin
    if (to_jsonb(new) - l2 - s) is distinct from (to_jsonb(old) - l2 - s) then
        raise exception 'journal_line_open_item: an L1 column cannot change' using errcode = '23000';
    end if;
    if (to_jsonb(new) - l2) is distinct from (to_jsonb(old) - l2) and not pg_has_role(current_user, 'cmd_clearing', 'member') then
        raise exception 'journal_line_open_item: the S columns change only by the clearing role' using errcode = '23000';
    end if;
    select fiscal_year, fiscal_period into v_year, v_period from core.journal_line
     where tenant_id = new.tenant_id and company_id = new.company_id and document_id = new.document_id and line_no = new.line_no;
    foreach f in array l2 loop
        if (to_jsonb(new) ->> f) is distinct from (to_jsonb(old) ->> f) then
            perform core.require_l2_log(new.tenant_id, new.company_id, new.document_id, new.line_no, f,
                                        to_jsonb(old) ->> f, to_jsonb(new) ->> f, v_year, v_period, 'LINE_OPEN_ITEM');
        end if;
    end loop;
    return new;
end $$;

-- The cache of an open item is the sum of its clearing items (ADR-037, 03, 4.3): the cleared amounts, the status and the most
-- recent clearing. The cleared amount stays between 0 and the line amount, in transaction and in local currency, with the sign of
-- the line (never over-cleared, never negative, 4.2). Checked at commit, for the open item that a row of either table names.
create or replace function core.check_open_item_cache() returns trigger language plpgsql as $$
declare
    v_t uuid := new.tenant_id; v_c uuid := new.company_id; v_doc uuid; v_line int;
    l core.journal_line%rowtype; o core.journal_line_open_item%rowtype;
    s_tc numeric; s_lc numeric; s_gc numeric; s_rc numeric; n int; v_last uuid; v_status text;
begin
    if tg_table_name = 'clearing_item' then
        v_doc := new.open_item_document_id; v_line := new.open_item_line_no;
    else
        v_doc := new.document_id; v_line := new.line_no;
    end if;
    select * into o from core.journal_line_open_item where tenant_id = v_t and company_id = v_c and document_id = v_doc and line_no = v_line;
    select * into l from core.journal_line where tenant_id = v_t and company_id = v_c and document_id = v_doc and line_no = v_line;
    select coalesce(sum(cleared_amount_tc), 0), coalesce(sum(cleared_amount_lc), 0), coalesce(sum(cleared_amount_gc), 0),
           sum(cleared_amount_rc), count(*)
      into s_tc, s_lc, s_gc, s_rc, n
      from core.clearing_item
     where tenant_id = v_t and company_id = v_c and open_item_document_id = v_doc and open_item_line_no = v_line;
    select c.clearing_id into v_last from core.clearing c
      join core.clearing_item i on i.tenant_id = c.tenant_id and i.company_id = c.company_id and i.clearing_id = c.clearing_id
     where i.tenant_id = v_t and i.company_id = v_c and i.open_item_document_id = v_doc and i.open_item_line_no = v_line
     order by c.chain_seq desc limit 1;

    if s_tc not between least(0, l.amount_tc) and greatest(0, l.amount_tc)
       or s_lc not between least(0, l.amount_lc) and greatest(0, l.amount_lc) then
        raise exception 'open item % line %: cleared amounts (tc %, lc %) are outside the line amount (tc %, lc %)',
            v_doc, v_line, s_tc, s_lc, l.amount_tc, l.amount_lc using errcode = '23514';
    end if;
    if o.cleared_amount_tc <> s_tc or o.cleared_amount_lc <> s_lc or o.cleared_amount_gc <> s_gc
       or coalesce(o.cleared_amount_rc, 0) <> coalesce(s_rc, 0) then
        raise exception 'open item % line %: the cleared amounts differ from the sum of the clearing items', v_doc, v_line using errcode = '23514';
    end if;
    v_status := case when s_tc = 0 and s_lc = 0 then 'OPEN'
                     when s_tc = l.amount_tc and s_lc = l.amount_lc then 'CLEARED' else 'PARTIAL' end;
    if o.clearing_status <> v_status then
        raise exception 'open item % line %: clearing_status is % but the clearing items give %', v_doc, v_line, o.clearing_status, v_status using errcode = '23514';
    end if;
    if o.last_clearing_id is distinct from v_last then
        raise exception 'open item % line %: last_clearing_id is not the most recent clearing of the item', v_doc, v_line using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists journal_line_open_item_cache on core.journal_line_open_item;
create constraint trigger journal_line_open_item_cache after insert or update on core.journal_line_open_item
    deferrable initially deferred for each row execute function core.check_open_item_cache();
drop trigger if exists clearing_item_cache on core.clearing_item;
create constraint trigger clearing_item_cache after insert on core.clearing_item
    deferrable initially deferred for each row execute function core.check_open_item_cache();

-- ---------------------------------------------------------------------------------------------------------
-- A clearing at commit (03, 4.1, 4.2, 5.3): its items are on its account and partner, in the currency of their document, with a
-- reporting amount where the line has one. They net to zero in local and group currency and in reporting currency, and in transaction
-- currency when they share one. A CLEAR has at least two items (the offset line of a difference document is one of them), a
-- reset at least one. The difference document is of the type that belongs to the clearing type and has the date of the clearing.
-- A reset undoes a CLEAR of the same account and partner (5.4).
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.check_clearing_content() returns trigger language plpgsql as $$
declare
    c core.clearing%rowtype; n int; ccy int; s_tc numeric; s_lc numeric; s_gc numeric; s_rc numeric; bad int; d record; r record; v_type text;
begin
    select * into c from core.clearing where tenant_id = new.tenant_id and company_id = new.company_id and clearing_id = new.clearing_id;
    select count(*), count(distinct currency_tc), sum(cleared_amount_tc), sum(cleared_amount_lc), sum(cleared_amount_gc), sum(cleared_amount_rc)
      into n, ccy, s_tc, s_lc, s_gc, s_rc
      from core.clearing_item where tenant_id = c.tenant_id and company_id = c.company_id and clearing_id = c.clearing_id;
    if n < 1 or (c.clearing_type = 'CLEAR' and n < 2) then
        raise exception 'clearing %: a % has % item(s)', c.clearing_id, c.clearing_type, n using errcode = '23514';
    end if;
    if s_lc <> 0 or s_gc <> 0 or coalesce(s_rc, 0) <> 0 or (ccy = 1 and s_tc <> 0) then
        raise exception 'clearing %: the items do not net to zero (tc %, lc %, gc %, rc %)', c.clearing_id, s_tc, s_lc, s_gc, s_rc using errcode = '23514';
    end if;
    select count(*) into bad
      from core.clearing_item i
      join core.journal_line l on l.tenant_id = i.tenant_id and l.company_id = i.company_id and l.document_id = i.open_item_document_id and l.line_no = i.open_item_line_no
      join core.journal_document d2 on d2.tenant_id = l.tenant_id and d2.company_id = l.company_id and d2.document_id = l.document_id
      join core.journal_line_open_item o on o.tenant_id = l.tenant_id and o.company_id = l.company_id and o.document_id = l.document_id and o.line_no = l.line_no
     where i.tenant_id = c.tenant_id and i.company_id = c.company_id and i.clearing_id = c.clearing_id
       and (l.account_id <> c.account_id
            or o.partner_id is distinct from c.partner_id
            or i.currency_tc <> d2.transaction_currency
            or (i.cleared_amount_rc is null) <> (l.amount_rc is null));
    if bad > 0 then
        raise exception 'clearing %: % item(s) are not on the account and partner of the clearing, or differ in currency or reporting amount from their line', c.clearing_id, bad using errcode = '23514';
    end if;
    if c.document_id is not null then
        select document_type, posting_date into d from core.journal_document
         where tenant_id = c.tenant_id and company_id = c.company_id and document_id = c.document_id;
        v_type := case when c.clearing_type = 'CLEAR' then 'CLEARING' else 'CLEARING_RESET' end;
        if d.document_type <> v_type or d.posting_date <> c.clearing_date then
            raise exception 'clearing %: the difference document must be of type % and dated %', c.clearing_id, v_type, c.clearing_date using errcode = '23514';
        end if;
    end if;
    if c.clearing_type = 'RESET' then
        select clearing_type, account_id, partner_id into r from core.clearing
         where tenant_id = c.tenant_id and company_id = c.company_id and clearing_id = c.reset_of_clearing_id;
        if r.clearing_type <> 'CLEAR' or r.account_id <> c.account_id or r.partner_id is distinct from c.partner_id then
            raise exception 'clearing %: a reset undoes a CLEAR of the same account and partner', c.clearing_id using errcode = '23514';
        end if;
    end if;
    return null;
end $$;
drop trigger if exists clearing_content on core.clearing;
create constraint trigger clearing_content after insert on core.clearing
    deferrable initially deferred for each row execute function core.check_clearing_content();
drop trigger if exists clearing_item_content on core.clearing_item;
create constraint trigger clearing_item_content after insert on core.clearing_item
    deferrable initially deferred for each row execute function core.check_clearing_content();

-- The clearing chain (03, 6, 02, 9.2): the head equals the newest clearing, the clearing links to its predecessor, sequence 1
-- starts at the genesis value (from the same text as the application's library).
create or replace function core.check_clearing_chain() returns trigger language plpgsql as $$
declare h core.chain_head%rowtype; max_seq bigint; max_hash bytea; expected_prev bytea;
begin
    select * into h from core.chain_head
     where tenant_id = new.tenant_id and company_id = new.company_id and chain_type = 'CLEARING' and scope_key = '';
    if not found then
        raise exception 'no CLEARING chain head for company %', new.company_id using errcode = '23514';
    end if;
    select chain_seq, row_hash into max_seq, max_hash from core.clearing
     where tenant_id = new.tenant_id and company_id = new.company_id order by chain_seq desc limit 1;
    if h.last_seq <> max_seq or h.last_hash <> max_hash then
        raise exception 'CLEARING chain head (seq %) does not match the newest clearing (seq %)', h.last_seq, max_seq using errcode = '23514';
    end if;
    if new.chain_seq = 1 then
        expected_prev := sha256(convert_to('genesis|' || new.tenant_id::text || '|' || new.company_id::text || '|CLEARING|', 'UTF8'));
    else
        select row_hash into expected_prev from core.clearing
         where tenant_id = new.tenant_id and company_id = new.company_id and chain_seq = new.chain_seq - 1;
    end if;
    if expected_prev is distinct from new.prev_hash then
        raise exception 'clearing % does not link to its predecessor in the chain', new.clearing_id using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists clearing_chain on core.clearing;
create constraint trigger clearing_chain after insert on core.clearing
    deferrable initially deferred for each row execute function core.check_clearing_chain();

insert into platform.schema_migration (version, description)
values ('0015', 'clearing and clearing_item, the clearing chain and role, and the cache rule of the open item S columns')
on conflict (version) do nothing;
