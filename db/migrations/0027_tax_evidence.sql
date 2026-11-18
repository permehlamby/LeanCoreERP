-- 0027 Tax evidence (08, 5.3): references that prove a tax line (customs declaration, proof of export, transport document, partner VAT check).
-- An evidence row is added by journal-documents:change-open-fields (entity type TAX_EVIDENCE) after posting, in every period status, and is never
-- changed or deleted. It is not part of any hash. Each field of a new row has its change_log row in the same transaction.

create table if not exists core.journal_line_tax_evidence (
    tenant_id      uuid not null,
    company_id     uuid not null,
    document_id    uuid not null,
    line_no        int not null,                      -- the line number of the tax line
    evidence_no    int not null check (evidence_no >= 1),
    evidence_type  core.code not null check (evidence_type in ('CUSTOMS_DECLARATION', 'PROOF_OF_EXPORT', 'TRANSPORT_DOCUMENT', 'PARTNER_VAT_CHECK', 'OTHER')),
    reference      varchar(100) not null,
    evidence_date  date,
    note           varchar(200),
    created_at     timestamptz not null default now(),
    primary key (tenant_id, company_id, document_id, line_no, evidence_no),
    foreign key (tenant_id, company_id, document_id, line_no) references core.journal_line_tax (tenant_id, company_id, document_id, line_no)
);

alter table core.journal_line_tax_evidence enable row level security;
alter table core.journal_line_tax_evidence force row level security;
do $$ begin
    if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = 'journal_line_tax_evidence' and policyname = 'tenant_isolation') then
        create policy tenant_isolation on core.journal_line_tax_evidence using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id());
    end if;
end $$;
revoke all on core.journal_line_tax_evidence from public;
grant select on core.journal_line_tax_evidence to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing;
grant insert on core.journal_line_tax_evidence to cmd_post;
drop trigger if exists journal_line_tax_evidence_append_only on core.journal_line_tax_evidence;
create trigger journal_line_tax_evidence_append_only before update or delete on core.journal_line_tax_evidence
    for each row execute function core.reject_change();
drop trigger if exists journal_line_tax_evidence_no_truncate on core.journal_line_tax_evidence;
create trigger journal_line_tax_evidence_no_truncate before truncate on core.journal_line_tax_evidence
    for each statement execute function core.reject_change();

-- The next number of the tax line, and a change_log row of this transaction for the row (the L2 rule of 0013)
create or replace function core.guard_journal_line_tax_evidence() returns trigger language plpgsql as $$
declare v_max int;
begin
    select coalesce(max(evidence_no), 0) into v_max from core.journal_line_tax_evidence
     where tenant_id = new.tenant_id and company_id = new.company_id and document_id = new.document_id and line_no = new.line_no;
    if new.evidence_no <> v_max + 1 then
        raise exception 'journal_line_tax_evidence: the next evidence_no of the tax line is %', v_max + 1 using errcode = '23514';
    end if;
    if not exists (select 1 from core.change_log c
                    where c.tenant_id = new.tenant_id and c.company_id = new.company_id and c.document_id = new.document_id and c.line_no = new.line_no
                      and c.entity_type = 'TAX_EVIDENCE' and c.sub_key = new.evidence_no::text and c.field_name = 'reference'
                      and c.new_value = new.reference and c.changed_at = transaction_timestamp()) then
        raise exception 'journal_line_tax_evidence: a row is added only with a change_log row in this transaction' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists journal_line_tax_evidence_guard on core.journal_line_tax_evidence;
create trigger journal_line_tax_evidence_guard before insert on core.journal_line_tax_evidence
    for each row execute function core.guard_journal_line_tax_evidence();

-- Evidence is added in a hard-closed period too (08, 5.3, a controlled exception to L3): the log row says so
do $$
declare c record;
begin
    for c in select conname from pg_constraint where conrelid = 'core.change_log'::regclass and contype = 'c' and pg_get_constraintdef(oid) like '%period_status%' loop
        execute format('alter table core.change_log drop constraint %I', c.conname);
    end loop;
    alter table core.change_log add constraint change_log_period_status_check check (period_status in ('OPEN', 'SOFT_CLOSED', 'HARD_CLOSED', 'NONE'));
    alter table core.change_log add constraint change_log_lock_class_period_check check (
           (lock_class = 'L2' and reason_code <> 'SYSTEM' and (period_status in ('OPEN', 'SOFT_CLOSED') or (period_status = 'HARD_CLOSED' and entity_type = 'TAX_EVIDENCE')))
        or (lock_class = 'L1*' and period_status = 'NONE' and reason_code = 'SYSTEM' and entity_type = 'LEGAL_SNAPSHOT'));
end $$;

insert into platform.schema_migration (version, description)
values ('0027', 'tax evidence of a tax line, added by change-open-fields in every period status')
on conflict (version) do nothing;
