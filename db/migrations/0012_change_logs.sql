-- 0012 change_log (02, 8) and master_change_log (07, 10.1): the field-level evidence of L2 changes of journal records
-- and of changes of master data. Both are append-only. No command writes them yet: change_log waits for
-- journal-documents:change-open-fields, master_change_log for the first {resource}:update of a master table (M).
-- V tables (such as account_role_assignment) have no rows here: their rows are the history (07, 10.1).

-- ---------------------------------------------------------------------------------------------------------
-- change_log (02, 8)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.change_log (
    tenant_id       uuid not null,
    change_id       uuid not null default gen_random_uuid(),
    change_set_id   uuid not null,
    company_id      uuid not null,
    entity_type     core.code not null check (entity_type in (
        'DOCUMENT', 'LINE', 'LINE_OPEN_ITEM', 'LINE_DIMENSION', 'LEGAL_SNAPSHOT', 'CLEARING', 'TAX_EVIDENCE')),
    document_id     uuid,
    clearing_id     uuid,     -- no FK: clearing (03, 4.1) does not exist yet
    line_no         int,
    sub_key         varchar(50),
    field_name      core.code not null,
    value_type      core.code not null check (value_type in ('AMOUNT', 'DATE', 'TEXT', 'ID', 'CODE', 'BOOL')),
    old_value       varchar(500),
    new_value       varchar(500),
    lock_class      core.code not null check (lock_class in ('L2', 'L1*')),
    period_status   core.code not null check (period_status in ('OPEN', 'SOFT_CLOSED', 'NONE')),
    reason_code     core.code not null,
    reason_text     varchar(500),
    changed_by      varchar(100) not null,
    changed_at      timestamptz not null,
    correlation_id  varchar(100) not null,
    idempotency_key varchar(100) not null,
    primary key (tenant_id, change_id),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, company_id, document_id) references core.journal_document (tenant_id, company_id, document_id),
    foreign key (tenant_id, company_id, document_id, line_no) references core.journal_line (tenant_id, company_id, document_id, line_no),
    -- 02, 8: exactly one of document_id and clearing_id is set. CLEARING has the clearing and no line.
    check ((document_id is null) <> (clearing_id is null)),
    check ((entity_type = 'CLEARING') = (clearing_id is not null)),
    -- line_no: null for header changes and for CLEARING, set for the entity types that belong to a line.
    check ((entity_type in ('LINE', 'LINE_OPEN_ITEM', 'LINE_DIMENSION', 'TAX_EVIDENCE')) = (line_no is not null)),
    -- L2 changes happen in an open or soft-closed period (a hard-closed period rejects them, L3). The one logged L1
    -- exception, the retention overwrite of the legal snapshot, is not period-governed and has the reason SYSTEM.
    check ((lock_class = 'L2' and period_status in ('OPEN', 'SOFT_CLOSED') and reason_code <> 'SYSTEM')
        or (lock_class = 'L1*' and period_status = 'NONE' and reason_code = 'SYSTEM' and entity_type = 'LEGAL_SNAPSHOT')),
    check (old_value is distinct from new_value)
);
create index if not exists change_log_document on core.change_log (tenant_id, company_id, document_id, line_no);
create index if not exists change_log_clearing on core.change_log (tenant_id, company_id, clearing_id);
create index if not exists change_log_correlation on core.change_log (tenant_id, correlation_id);
create index if not exists change_log_time on core.change_log (tenant_id, changed_at);

-- ---------------------------------------------------------------------------------------------------------
-- master_change_log (07, 10.1)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.master_change_log (
    tenant_id      uuid not null,
    change_id      uuid not null default gen_random_uuid(),
    change_set_id  uuid not null,
    entity_type    core.code not null,
    entity_id      varchar(100) not null,
    action         core.code not null check (action in ('CREATE', 'UPDATE', 'DEACTIVATE', 'REACTIVATE', 'ANONYMISE', 'CLEAR')),
    app_id         uuid,      -- no FK: app registration (15, 4) does not exist yet
    field_name     core.code,
    is_pii         boolean not null default false,
    old_value      varchar(500),
    new_value      varchar(500),
    reason_code    core.code,
    changed_by     varchar(100) not null,
    changed_at     timestamptz not null,
    correlation_id varchar(100) not null,
    command_log_id uuid not null,
    primary key (tenant_id, change_id),
    foreign key (tenant_id) references platform.tenant (tenant_id),
    -- The command writes its log row in the same transaction, after the change rows, so the check runs at commit.
    foreign key (tenant_id, command_log_id) references core.command_log (tenant_id, command_log_id) deferrable initially deferred,
    -- Null for CREATE (the whole row is the new state) and for DEACTIVATE. A field change names its field.
    check (action not in ('CREATE', 'DEACTIVATE') or field_name is null),
    check (action <> 'UPDATE' or field_name is not null),
    -- The log never holds a personal value (07, 10.1). Anonymisation does not touch it, because it never held the values.
    check (not is_pii or (old_value is null and new_value is null)),
    -- A create or a deactivation carries no field values. An update changes a value.
    check (action not in ('CREATE', 'DEACTIVATE') or (old_value is null and new_value is null)),
    check (action <> 'UPDATE' or is_pii or old_value is distinct from new_value)
);
create index if not exists master_change_log_entity on core.master_change_log (tenant_id, entity_type, entity_id, changed_at);
create index if not exists master_change_log_correlation on core.master_change_log (tenant_id, correlation_id);
create index if not exists master_change_log_set on core.master_change_log (tenant_id, change_set_id);

-- ---------------------------------------------------------------------------------------------------------
-- Security: forced RLS, read for every command role, insert only for the roles that write the log, never update or delete.
-- change_log: the posting role runs the change command (02, 10 lists "the change command role"), the retention role
-- writes the L1* row of its snapshot overwrite. master_change_log: the configuration role, and the retention role for ANONYMISE.
-- ---------------------------------------------------------------------------------------------------------
do $$
declare t text;
begin
    foreach t in array array['change_log', 'master_change_log'] loop
        execute format('alter table core.%I enable row level security', t);
        execute format('alter table core.%I force row level security', t);
        if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = t and policyname = 'tenant_isolation') then
            execute format('create policy tenant_isolation on core.%I using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id())', t);
        end if;
        execute format('revoke all on core.%I from public', t);
        execute format('grant select on core.%I to cmd_read, cmd_config, cmd_period, cmd_post', t);
        execute format('drop trigger if exists %I on core.%I', t || '_append_only', t);
        execute format('create trigger %I before update or delete on core.%I for each row execute function core.reject_change()', t || '_append_only', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
        execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_change()', t || '_no_truncate', t);
    end loop;
end $$;
grant insert on core.change_log to cmd_post, cmd_retention;
grant insert on core.master_change_log to cmd_config, cmd_retention;
grant select on core.change_log, core.master_change_log to cmd_retention;

insert into platform.schema_migration (version, description)
values ('0012', 'change_log and master_change_log, append-only')
on conflict (version) do nothing;
