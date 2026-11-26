-- 0057 the jobs of the read access log (09, 5.2, Q6, 12, 4.6): the purge of read_access_log after read_log_retention_days, and the daily event access.summary.
-- read_log_retention_days is a tenant setting (Q6). Null means the longest retention rule of the tenant (the rules of the class READ_LOG, else of any class).
-- read_log_summary_state holds the last day that access.summary was written for (UTC), so a day is summarised once and the purge never deletes a day that has not been summarised.
-- Both jobs run as the retention role. It already deletes from read_access_log and outbox_event (0033, 0006); it now also takes outbox positions and appends the summary event, like the
-- command roles do (15, 5.2). The job is the only writer of access.summary, and reads still write no event (A6).

alter table core.tenant_setting add column if not exists read_log_retention_days int check (read_log_retention_days >= 1);

create table if not exists core.read_log_summary_state (
    tenant_id         uuid not null,
    summarised_through date not null,
    updated_at        timestamptz not null default now(),
    primary key (tenant_id),
    foreign key (tenant_id) references platform.tenant (tenant_id)
);

do $$
declare t text := 'read_log_summary_state';
begin
    execute format('alter table core.%I enable row level security', t);
    execute format('alter table core.%I force row level security', t);
    if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = t and policyname = 'tenant_isolation') then
        execute format('create policy tenant_isolation on core.%I using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id())', t);
    end if;
    execute format('revoke all on core.%I from public', t);
    execute format('grant select on core.%I to cmd_read, cmd_retention', t);
    execute format('grant insert on core.%I to cmd_retention', t);
    execute format('grant update (summarised_through, updated_at) on core.%I to cmd_retention', t);
    execute format('drop trigger if exists %I on core.%I', t || '_no_delete', t);
    execute format('create trigger %I before delete on core.%I for each row execute function core.reject_delete()', t || '_no_delete', t);
    execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
    execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_delete()', t || '_no_truncate', t);
end $$;

-- the summary state only moves forward
create or replace function core.guard_read_log_summary_state() returns trigger language plpgsql as $$
begin
    if new.summarised_through < old.summarised_through then
        raise exception 'read_log_summary_state: summarised_through only moves forward' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists read_log_summary_state_guard on core.read_log_summary_state;
create trigger read_log_summary_state_guard before update on core.read_log_summary_state
    for each row execute function core.guard_read_log_summary_state();

grant insert on core.outbox_event to cmd_retention;
grant update (last_position) on core.outbox_head to cmd_retention;
grant select on core.outbox_head to cmd_retention;

insert into platform.schema_migration (version, description)
values ('0057', 'read access log jobs: read_log_retention_days, read_log_summary_state, event insert for the retention role')
on conflict (version) do nothing;
