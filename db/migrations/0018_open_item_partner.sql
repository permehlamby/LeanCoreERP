-- 0018 Partners on open items (02, 5A, 07, 9). The partner and the payment terms of an open item, and the partner of a clearing, now refer to
-- their tables. A CONTROL line needs a partner (0014) and now also a due date, at posting and for good: the due date of such an open item cannot
-- be cleared by a change (06, 5.4). Whether a partner may be posted to (status, relation, restriction) is a rule of the command, not of a key.

do $$ begin
    if not exists (select 1 from pg_constraint where conname = 'journal_line_open_item_partner_fk') then
        alter table core.journal_line_open_item add constraint journal_line_open_item_partner_fk
            foreign key (tenant_id, partner_id) references core.business_partner (tenant_id, partner_id);
    end if;
    if not exists (select 1 from pg_constraint where conname = 'journal_line_open_item_terms_fk') then
        alter table core.journal_line_open_item add constraint journal_line_open_item_terms_fk
            foreign key (tenant_id, payment_terms_id) references core.payment_terms (tenant_id, payment_terms_id);
    end if;
    if not exists (select 1 from pg_constraint where conname = 'clearing_partner_fk') then
        alter table core.clearing add constraint clearing_partner_fk
            foreign key (tenant_id, partner_id) references core.business_partner (tenant_id, partner_id);
    end if;
end $$;

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
    -- the due date of an open item on a control account stays mandatory (02, 5A, 06, 5.4)
    if new.due_date is null and old.due_date is not null and exists (
        select 1 from core.journal_line l join core.gl_account a on a.tenant_id = l.tenant_id and a.company_id = l.company_id and a.account_id = l.account_id
         where l.tenant_id = new.tenant_id and l.company_id = new.company_id and l.document_id = new.document_id and l.line_no = new.line_no
           and a.open_item_mode = 'CONTROL') then
        raise exception 'ACCOUNT_RULE.DUE_DATE_REQUIRED: the due date of an open item on a control account cannot be cleared' using errcode = '23000';
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

create or replace function core.check_open_item_rows() returns trigger language plpgsql as $$
declare
    v_type text; v_account uuid; v_mode text; v_partner uuid; v_due date; v_has boolean;
begin
    select line_type, account_id into v_type, v_account from core.journal_line
     where tenant_id = new.tenant_id and company_id = new.company_id and document_id = new.document_id and line_no = new.line_no;
    if v_type in ('REVAL_ADJUSTMENT', 'CLOSING', 'HISTORY_PL', 'OPENING') then
        return null;
    end if;
    select open_item_mode into v_mode from core.gl_account
     where tenant_id = new.tenant_id and company_id = new.company_id and account_id = v_account;
    select true, partner_id, due_date into v_has, v_partner, v_due from core.journal_line_open_item
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
    if v_mode = 'CONTROL' and v_due is null then
        raise exception 'journal_line_open_item: line % of document % is on a control account and has no due date', new.line_no, new.document_id using errcode = '23514';
    end if;
    if v_mode = 'GL_OPEN_ITEM' and v_partner is not null then
        raise exception 'journal_line_open_item: line % of document % is on a GL open-item account, which takes no partner', new.line_no, new.document_id using errcode = '23514';
    end if;
    return null;
end $$;

insert into platform.schema_migration (version, description)
values ('0018', 'partner and payment terms of an open item refer to their tables, the due date of a control account item stays')
on conflict (version) do nothing;
