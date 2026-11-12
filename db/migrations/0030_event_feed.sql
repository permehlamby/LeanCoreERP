-- 0030 the event feed (15, 3.1, 3.3, 3.4, 5): app registrations, their subscriptions, their consumer offsets, the settings of the purge and the purge mark.
-- The registry is configuration (cmd_config writes it, with master_change_log rows and audit events). An offset moves forward by an ack and backward only by
-- the rewind command. The purge of app events is a job of the retention role (15, 5.4). Namespaces and extension attributes (15, 3.2, 3.5) are not built.

alter table core.tenant_setting add column if not exists outbox_app_event_retention_days int not null default 30 check (outbox_app_event_retention_days >= 1);
alter table core.tenant_setting add column if not exists outbox_consumer_max_lag_days int not null default 90 check (outbox_consumer_max_lag_days >= 1);

create table if not exists core.app_registration (
    tenant_id     uuid not null,
    app_id        uuid not null default gen_random_uuid(),
    app_code      core.code not null,
    name          varchar(100) not null,
    active        boolean not null default true,
    company_scope jsonb,                                  -- null = all companies, else a list of company ids
    row_version   int not null default 1,
    created_at    timestamptz not null default now(),
    primary key (tenant_id, app_id),
    unique (tenant_id, app_code),
    foreign key (tenant_id) references platform.tenant (tenant_id),
    check (company_scope is null or jsonb_typeof(company_scope) = 'array'),
    check (app_code = lower(app_code) and app_code <> 'core')
);

create table if not exists core.app_event_subscription (
    tenant_id  uuid not null,
    app_id     uuid not null,
    event_type varchar(100) not null,                     -- an exact type or a family ending in .*
    primary key (tenant_id, app_id, event_type),
    foreign key (tenant_id, app_id) references core.app_registration (tenant_id, app_id)
);

create table if not exists core.app_consumer_offset (
    tenant_id     uuid not null,
    app_id        uuid not null,
    consumer_id   core.code not null,
    last_position bigint not null check (last_position >= 0),      -- S
    updated_at    timestamptz not null default now(),
    primary key (tenant_id, app_id, consumer_id),
    foreign key (tenant_id, app_id) references core.app_registration (tenant_id, app_id)
);

-- What the purge has removed: a consumer whose position lies below it has missed events and must resynchronise (15, 5.4, EVENT.POSITION_EXPIRED).
create table if not exists core.outbox_purge_state (
    tenant_id     uuid not null,
    purged_through bigint not null check (purged_through >= 0),
    purged_at     timestamptz not null default now(),
    primary key (tenant_id),
    foreign key (tenant_id) references platform.tenant (tenant_id)
);

create or replace function core.guard_app_registration() returns trigger language plpgsql as $$
begin
    if (to_jsonb(new) - array['name', 'active', 'company_scope', 'row_version']) is distinct from (to_jsonb(old) - array['name', 'active', 'company_scope', 'row_version']) then
        raise exception 'app_registration: the code and the identity of an app never change' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists app_registration_guard on core.app_registration;
create trigger app_registration_guard before update on core.app_registration
    for each row execute function core.guard_app_registration();

-- An offset moves forward. A move backward is allowed only inside the rewind command, which says so for its transaction (a backstop, not an access rule).
create or replace function core.guard_app_consumer_offset() returns trigger language plpgsql as $$
begin
    if (to_jsonb(new) - array['last_position', 'updated_at']) is distinct from (to_jsonb(old) - array['last_position', 'updated_at']) then
        raise exception 'app_consumer_offset: the key never changes' using errcode = '23000';
    end if;
    if new.last_position < old.last_position and coalesce(current_setting('app.allow_rewind', true), '') <> 'on' then
        raise exception 'app_consumer_offset: an offset moves forward, a rewind is its own command' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists app_consumer_offset_guard on core.app_consumer_offset;
create trigger app_consumer_offset_guard before update on core.app_consumer_offset
    for each row execute function core.guard_app_consumer_offset();

do $$
declare t text;
begin
    foreach t in array array['app_registration', 'app_event_subscription', 'app_consumer_offset', 'outbox_purge_state'] loop
        execute format('alter table core.%I enable row level security', t);
        execute format('alter table core.%I force row level security', t);
        if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = t and policyname = 'tenant_isolation') then
            execute format('create policy tenant_isolation on core.%I using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id())', t);
        end if;
        execute format('revoke all on core.%I from public', t);
        execute format('grant select on core.%I to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_closing, cmd_retention', t);
    end loop;
    foreach t in array array['app_registration', 'app_consumer_offset', 'outbox_purge_state'] loop
        execute format('drop trigger if exists %I on core.%I', t || '_no_delete', t);
        execute format('create trigger %I before delete on core.%I for each row execute function core.reject_delete()', t || '_no_delete', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
        execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_delete()', t || '_no_truncate', t);
    end loop;
end $$;

-- The configuration role writes the registry and the offsets (an ack and a rewind are configuration of the consumer, not ledger writes). A subscription set is
-- replaced as a whole, so the subscription rows may be deleted. The retention role writes the purge mark.
grant insert on core.app_registration, core.app_event_subscription, core.app_consumer_offset to cmd_config;
grant update (name, active, company_scope, row_version) on core.app_registration to cmd_config;
grant update (last_position, updated_at) on core.app_consumer_offset to cmd_config;
grant delete on core.app_event_subscription to cmd_config;
grant insert, update on core.outbox_purge_state to cmd_retention;
grant select on core.tenant_setting to cmd_retention;

insert into platform.schema_migration (version, description)
values ('0030', 'the event feed: apps, subscriptions, consumer offsets, the purge settings and mark')
on conflict (version) do nothing;
