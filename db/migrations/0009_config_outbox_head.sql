-- 0009 the configuration role may take outbox positions. Every command writes its outbox event in its own transaction
-- and moves outbox_head last (06, 6.3, class 7). 0006 gave that update to cmd_post and cmd_period only, so
-- companies:activate (a configuration command that emits company.activated) could not run (finding F-IMPL-11).
-- The guard trigger outbox_head_guard still allows only a forward move.
grant update (last_position) on core.outbox_head to cmd_config;

insert into platform.schema_migration (version, description)
values ('0009', 'configuration role may move outbox_head forward')
on conflict (version) do nothing;
