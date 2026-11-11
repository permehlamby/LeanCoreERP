-- 0013 The L2 columns open (F-IMPL-3, 02, 8 and 10): header_text and external_reference of a document and line_text of a line
-- can change, and only together with a change_log row of the same transaction, and never in a hard-closed period (L3).
-- Every other column stays locked: L1 never changes, the S columns of a document stay as 0006 allows them.
-- Not built: the typed dimensions of a line (cost_center_id and the three others), because the dimension tables that
-- validate them do not exist (06, 5.4 P4), the free dimensions, the open-item fields and the tax evidence.

-- "The same transaction" is checked by content and by time, not by transaction id, which breaks under savepoints (F-IMPL-2):
-- the log row must carry the old and the new value, the lock class L2, the status of the governing period, and the
-- timestamp of this transaction (the command writes it with now()).
create or replace function core.require_l2_log(
    p_tenant uuid, p_company uuid, p_document uuid, p_line_no int, p_field text, p_old text, p_new text,
    p_fiscal_year int, p_fiscal_period int) returns void language plpgsql as $$
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
           and c.entity_type = case when p_line_no is null then 'DOCUMENT' else 'LINE' end
           and c.field_name = p_field
           and c.old_value is not distinct from p_old and c.new_value is not distinct from p_new
           and c.lock_class = 'L2' and c.period_status = v_status
           and c.changed_at = transaction_timestamp()) then
        raise exception 'journal record: the L2 column % cannot change without a matching change_log row in this transaction', p_field
            using errcode = '23000';
    end if;
end $$;

create or replace function core.guard_journal_document() returns trigger language plpgsql as $$
begin
    -- The S columns status and reversed_by_document_id and the L2 columns header_text and external_reference may change.
    -- Everything else is L1.
    if (to_jsonb(new) - array['status', 'reversed_by_document_id', 'header_text', 'external_reference'])
       is distinct from (to_jsonb(old) - array['status', 'reversed_by_document_id', 'header_text', 'external_reference']) then
        raise exception 'journal_document: an L1 column cannot change' using errcode = '23000';
    end if;
    if new.status is distinct from old.status and not (old.status = 'posted' and new.status = 'reversed') then
        raise exception 'journal_document: status can only change from posted to reversed' using errcode = '23000';
    end if;
    if old.reversed_by_document_id is not null and new.reversed_by_document_id is distinct from old.reversed_by_document_id then
        raise exception 'journal_document: reversed_by_document_id is never overwritten (B1)' using errcode = '23000';
    end if;
    if new.header_text is distinct from old.header_text then
        perform core.require_l2_log(new.tenant_id, new.company_id, new.document_id, null, 'header_text',
                                    old.header_text, new.header_text, new.fiscal_year, new.fiscal_period);
    end if;
    if new.external_reference is distinct from old.external_reference then
        perform core.require_l2_log(new.tenant_id, new.company_id, new.document_id, null, 'external_reference',
                                    old.external_reference, new.external_reference, new.fiscal_year, new.fiscal_period);
    end if;
    return new;
end $$;

create or replace function core.guard_journal_line() returns trigger language plpgsql as $$
begin
    -- Only line_text is open. The typed dimensions open with the dimension tables.
    if (to_jsonb(new) - 'line_text') is distinct from (to_jsonb(old) - 'line_text') then
        raise exception 'journal_line: only line_text can change, and only through the change command' using errcode = '23000';
    end if;
    if new.line_text is distinct from old.line_text then
        perform core.require_l2_log(new.tenant_id, new.company_id, new.document_id, new.line_no, 'line_text',
                                    old.line_text, new.line_text, new.fiscal_year, new.fiscal_period);
    end if;
    return new;
end $$;

-- The posting role runs the change command (02, 10). It gets UPDATE on the open columns only.
grant update (header_text, external_reference) on core.journal_document to cmd_post;
grant update (line_text) on core.journal_line to cmd_post;

insert into platform.schema_migration (version, description)
values ('0013', 'L2 columns of documents and lines open with a change_log row (F-IMPL-3)')
on conflict (version) do nothing;
