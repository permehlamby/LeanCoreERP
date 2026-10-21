-- 0001 foundation: schemas, domains, roles, helper functions (spec 20, ADR-208, ADR-209)
-- Run order: 0001 to 0006, then optionally db/seed/dev_seed.sql. Every script can be run again.
-- Run as the migration role: `postgres` in Supabase, a superuser or the database owner elsewhere.
-- This role is the "core_owner" of 20, section 3. Nothing here is used at runtime by the application.

create extension if not exists btree_gist;

create schema if not exists platform;   -- tenant registry and reference data, outside tenant isolation (07, A6)
create schema if not exists core;       -- every tenant table, subject to row-level security (P7)

create table if not exists platform.schema_migration (
    version     text primary key,
    description text not null,
    applied_at  timestamptz not null default now()
);

-- Types of the specs (02, types line): AMT = DECIMAL(19,4), RATE = DECIMAL(19,10), BINARY(32), CODE.
do $$ begin
    if not exists (select 1 from pg_type t join pg_namespace n on n.oid = t.typnamespace
                   where n.nspname = 'core' and t.typname = 'amt') then
        create domain core.amt  as numeric(19,4);
    end if;
    if not exists (select 1 from pg_type t join pg_namespace n on n.oid = t.typnamespace
                   where n.nspname = 'core' and t.typname = 'rate') then
        create domain core.rate as numeric(19,10);
    end if;
    if not exists (select 1 from pg_type t join pg_namespace n on n.oid = t.typnamespace
                   where n.nspname = 'core' and t.typname = 'hash32') then
        create domain core.hash32 as bytea check (length(value) = 32);
    end if;
    if not exists (select 1 from pg_type t join pg_namespace n on n.oid = t.typnamespace
                   where n.nspname = 'core' and t.typname = 'code') then
        create domain core.code as varchar(50) check (value <> '');
    end if;
end $$;

-- Roles (20, section 3). The command roles are group roles without login. The login roles of the processes
-- are created here without a password and without login: set both outside this file, see db/README.md.
do $$
declare r text;
begin
    foreach r in array array['cmd_read', 'cmd_config', 'cmd_period', 'cmd_post', 'cmd_retention',
                             'core_app', 'core_job', 'core_retention']
    loop
        if not exists (select 1 from pg_roles where rolname = r) then
            execute format('create role %I nologin', r);
        end if;
    end loop;
end $$;

-- The login roles do not inherit the privileges of the command roles: every transaction names its command
-- role with SET LOCAL ROLE (db/README.md). That makes the per-command grants and policies effective.
alter role core_app noinherit;
alter role core_job noinherit;
alter role core_retention noinherit;

grant cmd_read, cmd_config, cmd_period, cmd_post to core_app;
grant cmd_read to core_job;
grant cmd_read, cmd_retention to core_retention;

-- The tenant of the current transaction. The application sets it with SET LOCAL as the first statement
-- of every transaction, from the validated token (ADR-209). Without a value no row is visible.
create or replace function core.current_tenant_id() returns uuid
language sql stable as $$
    select nullif(current_setting('app.tenant_id', true), '')::uuid
$$;

-- Nothing of ours is reachable through the Supabase data API. These revokes do nothing outside Supabase.
revoke all on schema core, platform from public;
do $$
declare r text;
begin
    foreach r in array array['anon', 'authenticated', 'service_role']
    loop
        if exists (select 1 from pg_roles where rolname = r) then
            execute format('revoke all on schema core, platform from %I', r);
            execute format('alter default privileges in schema core revoke all on tables from %I', r);
        end if;
    end loop;
end $$;

grant usage on schema platform to cmd_read, cmd_config, cmd_period, cmd_post, cmd_retention;
grant usage on schema core to cmd_read, cmd_config, cmd_period, cmd_post, cmd_retention;

insert into platform.schema_migration (version, description)
values ('0001', 'foundation: schemas, domains, roles, tenant function')
on conflict (version) do nothing;
