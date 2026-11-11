-- 0020 document_legal_snapshot (02, 4): the legal name, address and retention date of a document, 1:1 with journal_document and outside the hash
-- (P8, ADR-008). Every document has one, whoever writes it (posting, reversal, clearing difference document, reset mirror): the deferred trigger
-- below is the backstop. The personal columns stay empty until the header partner exists (step 3): the snapshot of a document without a partner
-- carries only its retention date. retention_until comes from the retention rules at creation (12, 4.1, consistency rule). The table is insert-only
-- here: the retention job (overwrite of the L1* columns, anonymised_at) and retention:extend (retention_until) come with their own commands.

create table if not exists core.document_legal_snapshot (
    tenant_id                 uuid not null,
    company_id                uuid not null,
    document_id               uuid not null,
    partner_id                uuid,
    legal_name                varchar(200),
    address_line_1            varchar(200),
    address_line_2            varchar(200),
    postal_code               varchar(20),
    city                      varchar(100),
    country_code              char(2) references platform.ref_country (country_code),
    partner_vat_id            varchar(30),
    electronic_address        varchar(100),
    electronic_address_scheme core.code,
    legal_registration_id     varchar(50),
    anonymised_at             timestamptz,
    retention_until           date not null,
    primary key (tenant_id, company_id, document_id),
    foreign key (tenant_id, company_id, document_id) references core.journal_document (tenant_id, company_id, document_id),
    foreign key (tenant_id, partner_id) references core.business_partner (tenant_id, partner_id)
);

alter table core.document_legal_snapshot enable row level security;
alter table core.document_legal_snapshot force row level security;
do $$ begin
    if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = 'document_legal_snapshot' and policyname = 'tenant_isolation') then
        create policy tenant_isolation on core.document_legal_snapshot
            using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id());
    end if;
end $$;
revoke all on core.document_legal_snapshot from public;
grant select on core.document_legal_snapshot to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_retention;
grant insert on core.document_legal_snapshot to cmd_post, cmd_clearing;
drop trigger if exists document_legal_snapshot_append_only on core.document_legal_snapshot;
create trigger document_legal_snapshot_append_only before update or delete on core.document_legal_snapshot
    for each row execute function core.reject_change();
drop trigger if exists document_legal_snapshot_no_truncate on core.document_legal_snapshot;
create trigger document_legal_snapshot_no_truncate before truncate on core.document_legal_snapshot
    for each statement execute function core.reject_change();

-- Backstop: a document without a snapshot cannot be committed (02, 4: 1:1)
create or replace function core.check_document_has_snapshot() returns trigger language plpgsql as $$
begin
    if not exists (select 1 from core.document_legal_snapshot where tenant_id = new.tenant_id and company_id = new.company_id
                   and document_id = new.document_id) then
        raise exception 'document % has no legal snapshot', new.document_id using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists journal_document_has_snapshot on core.journal_document;
create constraint trigger journal_document_has_snapshot after insert on core.journal_document
    deferrable initially deferred for each row execute function core.check_document_has_snapshot();

insert into platform.schema_migration (version, description)
values ('0020', 'document_legal_snapshot, one per document')
on conflict (version) do nothing;
