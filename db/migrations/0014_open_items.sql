-- 0014 journal_line_open_item (02, 5A, ADR-033): the open-item attributes of a line on an open-item-managed account.
-- Row existence marks an open item. This slice builds the accounts of mode GL_OPEN_ITEM (no partner). The accounts of mode
-- CONTROL need business_partner (07, 9.1), which does not exist yet: partner_id has no foreign key and the backstop below
-- rejects a CONTROL line, because a CONTROL line needs a partner. The clearing tables (03) do not exist either, so the S columns
-- (clearing_status, cleared_amount_*, last_clearing_id) cannot change yet and last_clearing_id has no foreign key.

create table if not exists core.journal_line_open_item (
    tenant_id             uuid not null,
    company_id            uuid not null,
    document_id           uuid not null,
    line_no               int  not null,
    partner_id            uuid,                 -- L1. No FK until business_partner exists
    baseline_date         date,                 -- L2
    payment_terms_id      uuid,                 -- L2. No FK until payment_terms exists (07, 9.6)
    due_date              date,                 -- L2. Mandatory for CONTROL accounts
    discount_due_date     date,                 -- L2
    discount_amount_tc    core.amt,             -- L2. Informational: the core posts no discount (Q9)
    payment_block         core.code,            -- L2
    dunning_block         core.code,            -- L2
    payment_reference     varchar(100),         -- L2
    bank_account_id       uuid,                 -- L2. No FK until company_bank_account exists (07, 9.5)
    legacy_document_date  date,                 -- L1. Migrated open items (13, 6.3)
    legacy_posting_date   date,                 -- L1
    legacy_document_ref   varchar(100),         -- L1
    clearing_status       core.code not null default 'OPEN' check (clearing_status in ('OPEN', 'PARTIAL', 'CLEARED')),  -- S
    cleared_amount_tc     core.amt not null default 0,   -- S
    cleared_amount_lc     core.amt not null default 0,   -- S
    cleared_amount_gc     core.amt not null default 0,   -- S
    cleared_amount_rc     core.amt,                      -- S. Null if the line has no RC amount
    last_clearing_id      uuid,                 -- S. No FK until clearing exists (03, 4.1)
    primary key (tenant_id, company_id, document_id, line_no),
    foreign key (tenant_id, company_id, document_id, line_no) references core.journal_line (tenant_id, company_id, document_id, line_no),
    -- a status that says nothing is cleared has nothing cleared
    check (clearing_status <> 'OPEN' or (cleared_amount_tc = 0 and cleared_amount_lc = 0 and cleared_amount_gc = 0 and coalesce(cleared_amount_rc, 0) = 0)),
    check (clearing_status = 'OPEN' or last_clearing_id is not null),
    check (discount_amount_tc is null or discount_amount_tc >= 0)
);
create index if not exists jloi_partner_status on core.journal_line_open_item (tenant_id, company_id, partner_id, clearing_status);
create index if not exists jloi_partner_due on core.journal_line_open_item (tenant_id, company_id, partner_id, due_date);

-- ---------------------------------------------------------------------------------------------------------
-- Security: forced RLS, read for every command role, insert by the posting role, UPDATE on the L2 columns for the change command
-- (the posting role runs it, 02, 10). The S columns get their own grant when the clearing role exists. No delete.
-- ---------------------------------------------------------------------------------------------------------
alter table core.journal_line_open_item enable row level security;
alter table core.journal_line_open_item force row level security;
do $$ begin
    if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = 'journal_line_open_item' and policyname = 'tenant_isolation') then
        create policy tenant_isolation on core.journal_line_open_item using (tenant_id = core.current_tenant_id())
            with check (tenant_id = core.current_tenant_id());
    end if;
end $$;
revoke all on core.journal_line_open_item from public;
grant select on core.journal_line_open_item to cmd_read, cmd_config, cmd_period, cmd_post;
grant insert on core.journal_line_open_item to cmd_post;
grant update (baseline_date, payment_terms_id, due_date, discount_due_date, discount_amount_tc, payment_block, dunning_block,
              payment_reference, bank_account_id) on core.journal_line_open_item to cmd_post;
do $$ begin
    execute 'drop trigger if exists journal_line_open_item_no_delete on core.journal_line_open_item';
    execute 'create trigger journal_line_open_item_no_delete before delete on core.journal_line_open_item for each row execute function core.reject_delete()';
    execute 'drop trigger if exists journal_line_open_item_no_truncate on core.journal_line_open_item';
    execute 'create trigger journal_line_open_item_no_truncate before truncate on core.journal_line_open_item for each statement execute function core.reject_delete()';
end $$;

-- ---------------------------------------------------------------------------------------------------------
-- The L2 guard knows the entity type (0013 chose DOCUMENT or LINE by the line number). The nine-argument form of 0013 stays
-- and calls the new one.
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.require_l2_log(
    p_tenant uuid, p_company uuid, p_document uuid, p_line_no int, p_field text, p_old text, p_new text,
    p_fiscal_year int, p_fiscal_period int, p_entity text) returns void language plpgsql as $$
declare
    v_status text;
begin
    select status into v_status from core.fiscal_period
     where tenant_id = p_tenant and company_id = p_company and fiscal_year = p_fiscal_year and period_no = p_fiscal_period;
    if v_status = 'HARD_CLOSED' then
        raise exception 'LOCK.FIELD_LOCKED_L3: % cannot change in a hard-closed period', p_field using errcode = '23000';
    end if;
    if not exists (
        select 1 from core.change_log c
         where c.tenant_id = p_tenant and c.company_id = p_company and c.document_id = p_document
           and c.line_no is not distinct from p_line_no
           and c.entity_type = p_entity
           and c.field_name = p_field
           and c.old_value is not distinct from p_old and c.new_value is not distinct from p_new
           and c.lock_class = 'L2' and c.period_status = v_status
           and c.changed_at = transaction_timestamp()) then
        raise exception 'journal record: the L2 column % cannot change without a matching change_log row in this transaction', p_field
            using errcode = '23000';
    end if;
end $$;

create or replace function core.require_l2_log(
    p_tenant uuid, p_company uuid, p_document uuid, p_line_no int, p_field text, p_old text, p_new text,
    p_fiscal_year int, p_fiscal_period int) returns void language sql as $$
    select core.require_l2_log(p_tenant, p_company, p_document, p_line_no, p_field, p_old, p_new, p_fiscal_year, p_fiscal_period,
                               case when p_line_no is null then 'DOCUMENT' else 'LINE' end)
$$;

-- ---------------------------------------------------------------------------------------------------------
-- Lock classes of the open item (02, 5A and 10): L1 never changes, the L2 columns change with a change_log row (entity type
-- LINE_OPEN_ITEM), the S columns change only with a clearing, which cannot exist yet.
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
    if (to_jsonb(new) - l2) is distinct from (to_jsonb(old) - l2) then
        raise exception 'journal_line_open_item: the S columns change only with a clearing, and clearing is not built yet' using errcode = '23000';
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
drop trigger if exists journal_line_open_item_guard on core.journal_line_open_item;
create trigger journal_line_open_item_guard before update on core.journal_line_open_item
    for each row execute function core.guard_journal_line_open_item();

-- ---------------------------------------------------------------------------------------------------------
-- Backstop at commit (02, 5A): a line on an open-item account has a row, a line on a NONE account has none, a CONTROL line has a
-- partner. Row existence is decided at posting, so the check reads the account once at commit. The lines of 02, 5A that are
-- exempt (REVAL_ADJUSTMENT, CLOSING, HISTORY_PL, OPENING) carry no row. With no partner table yet, a CONTROL line cannot pass.
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.check_open_item_rows() returns trigger language plpgsql as $$
declare
    v_type text; v_account uuid; v_mode text; v_partner uuid; v_has boolean;
begin
    select line_type, account_id into v_type, v_account from core.journal_line
     where tenant_id = new.tenant_id and company_id = new.company_id and document_id = new.document_id and line_no = new.line_no;
    if v_type in ('REVAL_ADJUSTMENT', 'CLOSING', 'HISTORY_PL', 'OPENING') then
        return null;
    end if;
    select open_item_mode into v_mode from core.gl_account
     where tenant_id = new.tenant_id and company_id = new.company_id and account_id = v_account;
    select true, partner_id into v_has, v_partner from core.journal_line_open_item
     where tenant_id = new.tenant_id and company_id = new.company_id and document_id = new.document_id and line_no = new.line_no;
    v_has := coalesce(v_has, false);
    if v_mode = 'NONE' and v_has then
        raise exception 'journal_line_open_item: line % of document % is on an account that is not open-item managed', new.line_no, new.document_id using errcode = '23514';
    end if;
    if v_mode <> 'NONE' and not v_has then
        raise exception 'journal_line_open_item: line % of document % is on an open-item account and has no open item row', new.line_no, new.document_id using errcode = '23514';
    end if;
    if v_mode = 'CONTROL' and v_partner is null then
        raise exception 'journal_line_open_item: line % of document % is on a control account and has no partner', new.line_no, new.document_id using errcode = '23514';
    end if;
    if v_mode = 'GL_OPEN_ITEM' and v_partner is not null then
        raise exception 'journal_line_open_item: line % of document % is on a GL open-item account, which takes no partner', new.line_no, new.document_id using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists journal_line_open_item_rows on core.journal_line;
create constraint trigger journal_line_open_item_rows after insert on core.journal_line
    deferrable initially deferred for each row execute function core.check_open_item_rows();
drop trigger if exists journal_line_open_item_rows_oi on core.journal_line_open_item;
create constraint trigger journal_line_open_item_rows_oi after insert on core.journal_line_open_item
    deferrable initially deferred for each row execute function core.check_open_item_rows();

insert into platform.schema_migration (version, description)
values ('0014', 'journal_line_open_item for GL_OPEN_ITEM accounts, with its guards')
on conflict (version) do nothing;
