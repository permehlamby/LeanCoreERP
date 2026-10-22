-- 0007 the posting command takes a share lock on the period rows (06, 9, lock order step 3).
-- PostgreSQL only allows SELECT ... FOR SHARE for a role with UPDATE privilege on at least one column. cmd_post got no
-- UPDATE on the period tables in 0006, so the lock failed with 42501 (finding F-IMPL-9, found by the posting tests).
-- The grant is on last_event_id only, and the guard trigger of 0006 (guard_period_status) rejects every change of it that
-- does not match the latest period_event of the same period and status. The role can lock the row, it cannot change it.
-- The share lock is what makes a period close wait for a running posting, and a posting after the close see the new status.
grant update (last_event_id) on core.fiscal_period, core.fiscal_year to cmd_post;

insert into platform.schema_migration (version, description)
values ('0007', 'posting role may lock period rows for share (last_event_id guarded by trigger)')
on conflict (version) do nothing;
