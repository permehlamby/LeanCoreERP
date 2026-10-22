-- 0008 the migration role may switch to the command roles. Needed to run db/tests/001_protection.sql on Supabase, where
-- the SQL Editor runs as postgres without superuser rights (set role needs membership). Found when the tests ran there.
-- It gives the migration role nothing it does not have: it already owns every object. Skipped where no role postgres exists.
do $$ begin
    if exists (select 1 from pg_roles where rolname = 'postgres') then
        grant cmd_read, cmd_config, cmd_period, cmd_post, cmd_retention to postgres;
    end if;
end $$;

insert into platform.schema_migration (version, description)
values ('0008', 'migration role is a member of the command roles (tests on Supabase)')
on conflict (version) do nothing;
