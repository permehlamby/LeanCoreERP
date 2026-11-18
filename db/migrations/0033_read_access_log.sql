-- 0033 read_access_log (09, 5.2): the log of the reads that return personal data. Insert-only, written in the transaction of the read by the read role,
-- so a response is never returned without its log row. It holds only ids, so the anonymisation of a partner does not touch it. It is not part of a hash chain
-- (operational audit) and a read writes no outbox event (A6). The one DELETE the system performs on it is the retention job's (12, 4.6), under cmd_retention.
-- Not here: the setting read_log_retention_days (Q6) and the job that purges and that writes access.summary, the search column of 2.4.1.

create table if not exists core.read_access_log (
    tenant_id      uuid not null references platform.tenant (tenant_id),
    read_id        uuid not null default gen_random_uuid(),
    read_at        timestamptz not null default now(),
    actor          varchar(100) not null,
    operation      core.code not null,
    company_id     uuid,
    partner_id     uuid,
    partner_count  int check (partner_count >= 0),
    export_job_id  uuid,
    document_id    uuid,
    field_group    core.code not null check (field_group in (
        'PII_IDENTITY', 'PII_ADDRESS', 'PII_CONTACT', 'PII_BANK', 'PII_TAX_ID', 'PII_CREDIT', 'ORDER_ADDRESS', 'SEARCH', 'PII_EXTENSION', 'LEGAL_SNAPSHOT')),
    purpose_code   core.code,
    correlation_id varchar(100) not null,
    result_position int check (result_position >= 1),
    primary key (tenant_id, read_id),
    -- an export job logs one row per job and field group, never per partner (5.2): no partner, a partner count, no result position
    check (export_job_id is null or (partner_id is null and partner_count is not null and result_position is null)),
    check (partner_count is null or export_job_id is not null),
    -- the legal snapshot of a document names the document
    check ((field_group = 'LEGAL_SNAPSHOT') = (document_id is not null))
);
create index if not exists read_access_log_partner on core.read_access_log (tenant_id, partner_id, read_at);
create index if not exists read_access_log_actor on core.read_access_log (tenant_id, actor, read_at);
create index if not exists read_access_log_export on core.read_access_log (tenant_id, export_job_id);

alter table core.read_access_log enable row level security;
alter table core.read_access_log force row level security;
do $$ begin
    if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = 'read_access_log' and policyname = 'tenant_isolation') then
        create policy tenant_isolation on core.read_access_log
            using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id());
    end if;
end $$;
revoke all on core.read_access_log from public;
grant select, insert on core.read_access_log to cmd_read;
grant select, delete on core.read_access_log to cmd_retention;

create or replace function core.guard_read_access_log() returns trigger language plpgsql as $$
begin
    if tg_op = 'DELETE' and pg_has_role(current_user, 'cmd_retention', 'member') then
        return old;
    end if;
    raise exception 'core.read_access_log is insert-only: % is not allowed', tg_op using errcode = '23000';
end $$;
drop trigger if exists read_access_log_guard on core.read_access_log;
create trigger read_access_log_guard before update or delete on core.read_access_log
    for each row execute function core.guard_read_access_log();
drop trigger if exists read_access_log_no_truncate on core.read_access_log;
create trigger read_access_log_no_truncate before truncate on core.read_access_log
    for each statement execute function core.reject_change();

insert into platform.schema_migration (version, description)
values ('0033', 'read_access_log: the insert-only log of reads of personal data')
on conflict (version) do nothing;
