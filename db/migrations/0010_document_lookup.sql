-- 0010 look up a document by its id alone. journal-documents:reverse (06, 5.3) takes only document_id, while the primary
-- key of journal_document leads with company_id.
create index if not exists jd_document on core.journal_document (tenant_id, document_id);

insert into platform.schema_migration (version, description)
values ('0010', 'journal_document lookup by document_id')
on conflict (version) do nothing;
