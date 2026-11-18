-- 0034 migration batches, second slice (13): the four-eyes request and approval of commit and seal in a PRODUCTION tenant (13, 3.1, 7, Q13), and what the rollback needs.
-- The rollback reverses the documents of a batch as the clearing role (a document with open items is reversed together with the automatic clearing of its mirror, 03, 5.7),
-- so that role gets no new grant: it already inserts documents, lines, open item rows and clearings, and it reads the migration tables (0032).

alter table core.migration_batch
    add column if not exists commit_requested_by  varchar(100),
    add column if not exists commit_requested_at  timestamptz,
    add column if not exists commit_approved_by   varchar(100),
    add column if not exists commit_approved_at   timestamptz,
    add column if not exists seal_requested_by    varchar(100),
    add column if not exists seal_requested_at    timestamptz,
    add column if not exists seal_approved_by     varchar(100),
    add column if not exists seal_approved_at     timestamptz;

do $$ begin
    if not exists (select 1 from pg_constraint where conname = 'migration_batch_four_eyes_check') then
        alter table core.migration_batch add constraint migration_batch_four_eyes_check check (
            (commit_approved_by is null or (commit_requested_by is not null and commit_approved_by <> commit_requested_by)) and
            (seal_approved_by is null or (seal_requested_by is not null and seal_approved_by <> seal_requested_by)));
    end if;
end $$;

grant update (commit_requested_by, commit_requested_at, commit_approved_by, commit_approved_at,
              seal_requested_by, seal_requested_at, seal_approved_by, seal_approved_at) on core.migration_batch to cmd_migration;

-- The S columns of a batch: the status, the commit and seal marks and, new here, the four-eyes columns (13, 3.1).
create or replace function core.guard_migration_batch() returns trigger language plpgsql as $$
declare
    s constant text[] := array['status', 'committed_at', 'committed_by', 'sealed_at', 'sealed_by', 'accepted_reconciliation_id', 'row_version',
                               'commit_requested_by', 'commit_requested_at', 'commit_approved_by', 'commit_approved_at',
                               'seal_requested_by', 'seal_requested_at', 'seal_approved_by', 'seal_approved_at'];
begin
    if (to_jsonb(new) - s) is distinct from (to_jsonb(old) - s) then
        raise exception 'migration_batch: an L1 column cannot change' using errcode = '23000';
    end if;
    if new.status is distinct from old.status and not (
        (old.status = 'OPEN' and new.status in ('VALIDATED', 'RECONCILED', 'ABORTED')) or
        (old.status = 'VALIDATED' and new.status in ('OPEN', 'RECONCILED', 'ABORTED')) or
        (old.status = 'RECONCILED' and new.status in ('OPEN', 'VALIDATED', 'COMMITTING', 'ABORTED')) or
        (old.status = 'COMMITTING' and new.status in ('COMMITTED', 'ABORTED', 'ROLLED_BACK')) or
        (old.status = 'COMMITTED' and new.status in ('SEALED', 'ROLLED_BACK'))) then
        raise exception 'migration_batch: the status cannot change from % to %', old.status, new.status using errcode = '23000';
    end if;
    return new;
end $$;

insert into platform.schema_migration (version, description)
values ('0034', 'migration batches: the four-eyes columns of commit and seal')
on conflict (version) do nothing;
