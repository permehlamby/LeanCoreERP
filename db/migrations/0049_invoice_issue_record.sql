-- 0049 invoice issue evidence (12, 11, 22, delta 1): which file of an invoice or credit note went out, and when. Insert-only; one invoice can have several records (re-issues).
-- Exactly one of invoice_id (the link record of an order-built invoice or credit note: sales_invoice or purchase_invoice, told apart by invoice_kind, so no foreign key) and document_id
-- (the journal document of a direct invoice or credit note, AR_INVOICE, AR_CREDIT, AP_INVOICE, AP_CREDIT). The command runs as the posting role. The file itself is never held by the core.

create table if not exists core.invoice_issue_record (
    tenant_id          uuid not null references platform.tenant (tenant_id),
    company_id         uuid not null,
    issue_id           uuid not null default gen_random_uuid(),
    invoice_kind       core.code not null check (invoice_kind in ('SALES', 'PURCHASE')),
    invoice_id         uuid,
    document_id        uuid,
    format             varchar(20) not null,
    document_reference varchar(500) not null,
    document_sha256    core.hash32 not null,
    issued_at          timestamptz not null,
    delivery_channel   core.code not null check (delivery_channel in ('EMAIL', 'PEPPOL', 'PORTAL', 'POST', 'OTHER')),
    recorded_by        varchar(100) not null,
    source_system      core.code not null,
    correlation_id     varchar(100) not null,
    idempotency_key    varchar(100) not null,
    created_at         timestamptz not null default now(),
    primary key (tenant_id, company_id, issue_id),
    unique (tenant_id, idempotency_key),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, company_id, document_id) references core.journal_document (tenant_id, company_id, document_id),
    check ((invoice_id is null) <> (document_id is null))
);
create index if not exists invoice_issue_invoice on core.invoice_issue_record (tenant_id, company_id, invoice_kind, invoice_id) where invoice_id is not null;
create index if not exists invoice_issue_document on core.invoice_issue_record (tenant_id, company_id, document_id) where document_id is not null;

do $$
declare t text := 'invoice_issue_record';
begin
    execute format('alter table core.%I enable row level security', t);
    execute format('alter table core.%I force row level security', t);
    if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = t and policyname = 'tenant_isolation') then
        execute format('create policy tenant_isolation on core.%I using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id())', t);
    end if;
    execute format('revoke all on core.%I from public', t);
    execute format('grant select on core.%I to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_retention, cmd_stock, cmd_order', t);
    if exists (select 1 from pg_roles where rolname = 'cmd_closing') then execute format('grant select on core.%I to cmd_closing', t); end if;
    if exists (select 1 from pg_roles where rolname = 'cmd_migration') then execute format('grant select on core.%I to cmd_migration', t); end if;
    execute format('grant insert on core.%I to cmd_post', t);
    execute format('drop trigger if exists %I on core.%I', t || '_append_only', t);
    execute format('create trigger %I before update on core.%I for each row execute function core.reject_change()', t || '_append_only', t);
    execute format('drop trigger if exists %I on core.%I', t || '_no_delete', t);
    execute format('create trigger %I before delete on core.%I for each row execute function core.reject_delete()', t || '_no_delete', t);
    execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
    execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_delete()', t || '_no_truncate', t);
end $$;

insert into platform.schema_migration (version, description)
values ('0049', 'invoice issue evidence: invoice_issue_record')
on conflict (version) do nothing;
