-- 0016 The status of an open item looks at every currency (03, 4.3, finding F-IMPL-20). 0015 used TC and LC: OPEN at TC 0 and LC 0,
-- CLEARED at the line's TC and LC. The offset line of a difference document that arises only in group or reporting currency has TC 0 and
-- LC 0 and could never be CLEARED. Now OPEN means nothing is cleared in any currency, CLEARED that every currency of the line is
-- cleared, and the cleared group amount stays between 0 and the line amount like TC and LC.

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
       or s_lc not between least(0, l.amount_lc) and greatest(0, l.amount_lc)
       or s_gc not between least(0, l.amount_gc) and greatest(0, l.amount_gc) then
        raise exception 'open item % line %: cleared amounts (tc %, lc %, gc %) are outside the line amount (tc %, lc %, gc %)',
            v_doc, v_line, s_tc, s_lc, s_gc, l.amount_tc, l.amount_lc, l.amount_gc using errcode = '23514';
    end if;
    if o.cleared_amount_tc <> s_tc or o.cleared_amount_lc <> s_lc or o.cleared_amount_gc <> s_gc
       or coalesce(o.cleared_amount_rc, 0) <> coalesce(s_rc, 0) then
        raise exception 'open item % line %: the cleared amounts differ from the sum of the clearing items', v_doc, v_line using errcode = '23514';
    end if;
    -- OPEN when nothing is cleared in any currency, CLEARED when every currency of the line is cleared, else PARTIAL. TC and LC alone
    -- would not do: the offset line of a difference document that arises only in group or reporting currency has TC 0 and LC 0.
    v_status := case when s_tc = 0 and s_lc = 0 and s_gc = 0 and coalesce(s_rc, 0) = 0 then 'OPEN'
                     when s_tc = l.amount_tc and s_lc = l.amount_lc and s_gc = l.amount_gc and coalesce(s_rc, 0) = coalesce(l.amount_rc, 0) then 'CLEARED'
                     else 'PARTIAL' end;
    if o.clearing_status <> v_status then
        raise exception 'open item % line %: clearing_status is % but the clearing items give %', v_doc, v_line, o.clearing_status, v_status using errcode = '23514';
    end if;
    if o.last_clearing_id is distinct from v_last then
        raise exception 'open item % line %: last_clearing_id is not the most recent clearing of the item', v_doc, v_line using errcode = '23514';
    end if;
    return null;
end $$;

insert into platform.schema_migration (version, description)
values ('0016', 'open item status looks at every currency')
on conflict (version) do nothing;
