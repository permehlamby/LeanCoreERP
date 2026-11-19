-- 0040 reversal of stock documents (10, 6.8; 11, 6.8): the stock role mirrors a stock document with its derived journal and, for the GR/IR line of a receipt, clears the open item of
-- the original against the mirror. That needs what the clearing role has for a reversal: the CLEARING head, the clearing tables, the status of the original journal document and the
-- S columns of an open item. The guard of the open item row is re-created with one more role that may change the S columns (copy of 0018, one condition).

grant insert on core.clearing, core.clearing_item to cmd_stock;
grant update (status, reversed_by_document_id) on core.journal_document to cmd_stock;
grant update (clearing_status, cleared_amount_tc, cleared_amount_lc, cleared_amount_gc, cleared_amount_rc, last_clearing_id) on core.journal_line_open_item to cmd_stock;

drop policy if exists chain_head_stock on core.chain_head;
create policy chain_head_stock on core.chain_head as restrictive for update to cmd_stock
    using (chain_type in ('LEDGER', 'CLEARING', 'STOCK', 'FULFILMENT')) with check (chain_type in ('LEDGER', 'CLEARING', 'STOCK', 'FULFILMENT'));

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
    if (to_jsonb(new) - l2) is distinct from (to_jsonb(old) - l2) and not (pg_has_role(current_user, 'cmd_clearing', 'member') or pg_has_role(current_user, 'cmd_stock', 'member')) then
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

insert into platform.schema_migration (version, description)
values ('0040', 'reversal of stock documents: the stock role clears and marks, the open item guard allows it')
on conflict (version) do nothing;
