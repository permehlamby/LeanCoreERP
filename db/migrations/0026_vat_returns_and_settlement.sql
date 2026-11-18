-- 0026 VAT returns and settlement (08, 6.3 to 6.7, 7.1 to 7.3a): the frozen snapshot of a return with its lines and tax lines, the VAT_RETURN
-- chain, the line type of a settlement line, and the registration of the role VAT_SETTLEMENT.
-- Written by the posting role (cmd_post): preparing a return, recording its filing, posting the VAT_SETTLE document and reversing it.
-- Not in this migration: the EC list, OSS and IOSS rows (the table takes them, the commands build STANDARD only), the legacy tax summary of a
-- migration (13, 3.6) and the tax evidence (5.3).

-- ---------------------------------------------------------------------------------------------------------
-- A settlement line is a line of its own type (08, 6.7, 7.3). Only a VAT_SETTLE document has such lines and has nothing else.
-- ---------------------------------------------------------------------------------------------------------
do $$
declare c record;
begin
    for c in select conname from pg_constraint
              where conrelid = 'core.journal_line'::regclass and contype = 'c' and pg_get_constraintdef(oid) like '%line_type%' loop
        execute format('alter table core.journal_line drop constraint %I', c.conname);
    end loop;
    alter table core.journal_line add constraint journal_line_line_type_check check (line_type in (
        'BASE', 'TAX', 'TAX_NONDEDUCTIBLE', 'ROUNDING', 'FX_REALISED', 'FX_UNREALISED', 'REVAL_ADJUSTMENT',
        'TAX_ROUNDING', 'CLOSING', 'OPENING', 'CLEARING_DIFF', 'HISTORY_PL', 'VAT_SETTLEMENT'));
end $$;

create or replace function core.guard_tax_account_posting() returns trigger language plpgsql as $$
begin
    if new.line_type not in ('TAX', 'VAT_SETTLEMENT') and exists (select 1 from core.gl_account a where a.tenant_id = new.tenant_id and a.company_id = new.company_id
                                             and a.account_id = new.account_id and a.tax_account) then
        raise exception 'ACCOUNT_RULE.TAX_ACCOUNT_DIRECT_POSTING: a tax account accepts only tax lines and settlement lines' using errcode = '23514';
    end if;
    return new;
end $$;

create or replace function core.guard_vat_settlement_line() returns trigger language plpgsql as $$
declare v_type text;
begin
    select document_type into v_type from core.journal_document where tenant_id = new.tenant_id and company_id = new.company_id and document_id = new.document_id;
    if (new.line_type = 'VAT_SETTLEMENT') <> (v_type = 'VAT_SETTLE') then
        raise exception 'journal_line: a VAT_SETTLEMENT line belongs to a VAT_SETTLE document and a VAT_SETTLE document has no other line' using errcode = '23514';
    end if;
    return new;
end $$;
drop trigger if exists journal_line_vat_settlement_guard on core.journal_line;
create trigger journal_line_vat_settlement_guard before insert on core.journal_line
    for each row execute function core.guard_vat_settlement_line();

-- The role VAT_SETTLEMENT is per VAT registration (07, 6.3): now that the registrations exist the column has its key
do $$ begin
    if not exists (select 1 from pg_constraint where conname = 'account_role_assignment_vat_registration_fkey') then
        alter table core.account_role_assignment add constraint account_role_assignment_vat_registration_fkey
            foreign key (tenant_id, company_id, vat_registration_id) references core.company_vat_registration (tenant_id, company_id, registration_id);
    end if;
end $$;

-- the account of a VAT_SETTLEMENT role becomes a tax account (08, 6.7): it takes only tax lines and settlement lines from now on
create or replace function core.guard_vat_settlement_role() returns trigger language plpgsql as $$
begin
    if new.role_code = 'VAT_SETTLEMENT' then
        if new.vat_registration_id is null then
            raise exception 'CONFIG.ROLE_ACCOUNT_RULE_VIOLATED: VAT_SETTLEMENT is per VAT registration (VAT_REGISTRATION)' using errcode = '23514';
        end if;
        update core.gl_account set tax_account = true
         where tenant_id = new.tenant_id and company_id = new.company_id and account_id = new.account_id and not tax_account;
    end if;
    return new;
end $$;
drop trigger if exists account_role_assignment_vat_settlement on core.account_role_assignment;
create trigger account_role_assignment_vat_settlement before insert on core.account_role_assignment
    for each row execute function core.guard_vat_settlement_role();

-- ---------------------------------------------------------------------------------------------------------
-- vat_return (08, 6.3): L1 columns never change, the S columns are set once (filing) or while the return lives (settlement, replacement)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.vat_return (
    tenant_id                  uuid not null,
    company_id                 uuid not null,
    return_id                  uuid not null default gen_random_uuid(),
    registration_id            uuid not null,
    return_type                core.code not null check (return_type in ('STANDARD', 'EC_LIST', 'OSS', 'IOSS')),
    fiscal_year                int not null,
    vat_period_no              smallint not null check (vat_period_no >= 1),
    period_start_date          date not null,
    period_end_date            date not null,
    return_kind                core.code not null check (return_kind in ('ORIGINAL', 'SUPPLEMENTARY', 'REPLACEMENT')),
    return_seq                 int not null check (return_seq >= 1),
    replaces_return_id         uuid,
    superseded_by_return_id    uuid,                                  -- S
    prepared_at                timestamptz not null,
    prepared_by                varchar(100) not null,
    correlation_id             varchar(100) not null,
    idempotency_key            varchar(100) not null,
    tax_line_count             int not null check (tax_line_count >= 0),
    late_line_count            int not null check (late_line_count >= 0),
    snapshot_digest            core.hash32 not null,
    chain_seq                  bigint not null check (chain_seq >= 1),
    prev_hash                  core.hash32 not null,
    row_hash                   core.hash32 not null,
    hash_version               smallint not null check (hash_version = 2),
    filing_reference           varchar(100),                          -- S
    filed_at                   timestamptz,                           -- S
    filed_by                   varchar(100),                          -- S
    settled_document_id        uuid,                                  -- S
    primary key (tenant_id, company_id, return_id),
    unique (tenant_id, company_id, registration_id, return_type, fiscal_year, vat_period_no, return_seq),
    unique (tenant_id, idempotency_key),
    unique (tenant_id, company_id, chain_seq),
    foreign key (tenant_id, company_id, registration_id) references core.company_vat_registration (tenant_id, company_id, registration_id),
    foreign key (tenant_id, company_id, replaces_return_id) references core.vat_return (tenant_id, company_id, return_id),
    foreign key (tenant_id, company_id, superseded_by_return_id) references core.vat_return (tenant_id, company_id, return_id),
    foreign key (tenant_id, company_id, settled_document_id) references core.journal_document (tenant_id, company_id, document_id),
    check (period_end_date >= period_start_date),
    check ((return_kind = 'REPLACEMENT') = (replaces_return_id is not null)),
    check ((return_kind = 'ORIGINAL') = (return_seq = 1)),
    check ((filing_reference is null) = (filed_at is null) and (filed_at is null) = (filed_by is null))
);
create index if not exists vat_return_period on core.vat_return (tenant_id, company_id, registration_id, return_type, fiscal_year, vat_period_no);

create table if not exists core.vat_return_line (
    tenant_id           uuid not null,
    company_id          uuid not null,
    return_id           uuid not null,
    line_no             int not null check (line_no >= 1),
    row_type            core.code not null check (row_type in ('BOX', 'EC_LIST', 'OSS')),
    box_code            core.code,
    partner_id          uuid,
    partner_vat_id      varchar(30),
    country_code        char(2),
    category            core.code check (category in ('GOODS', 'SERVICES', 'TRIANGULATION')),
    rate                core.rate,
    supply_kind         core.code check (supply_kind in ('GOODS', 'SERVICES')),
    currency_code       char(3) not null references platform.ref_currency (currency_code),
    base_amount         core.amt not null,
    tax_amount          core.amt not null,
    line_count          int not null check (line_count >= 1),
    legacy_base_amount  core.amt not null default 0,
    legacy_tax_amount   core.amt not null default 0,
    primary key (tenant_id, company_id, return_id, line_no),
    foreign key (tenant_id, company_id, return_id) references core.vat_return (tenant_id, company_id, return_id),
    check (row_type <> 'BOX' or (box_code is not null and partner_id is null and partner_vat_id is null and country_code is null and category is null and rate is null and supply_kind is null)),
    check (row_type <> 'EC_LIST' or (box_code is null and partner_id is not null and country_code is not null and category is not null and rate is null and supply_kind is null)),
    check (row_type <> 'OSS' or (box_code is null and partner_id is null and partner_vat_id is null and country_code is not null and category is null and rate is not null and supply_kind is not null))
);

create table if not exists core.vat_return_tax_line (
    tenant_id            uuid not null,
    company_id           uuid not null,
    return_id            uuid not null,
    document_id          uuid not null,
    line_no              int not null,
    registration_id      uuid not null,
    return_type          core.code not null,
    tax_date             date not null,
    is_late              boolean not null,
    tax_code_version_id  uuid not null,
    vat_base_amount      core.amt not null,
    vat_tax_amount       core.amt not null,
    is_current           boolean not null,                              -- S
    primary key (tenant_id, company_id, return_id, document_id, line_no),
    foreign key (tenant_id, company_id, return_id) references core.vat_return (tenant_id, company_id, return_id),
    foreign key (tenant_id, company_id, document_id, line_no) references core.journal_line_tax (tenant_id, company_id, document_id, line_no),
    foreign key (tenant_id, tax_code_version_id) references core.tax_code_version (tenant_id, tax_code_version_id)
);
-- A tax line is currently reported at most once per registration and return type (08, 6.5)
create unique index if not exists vat_return_tax_line_current on core.vat_return_tax_line
    (tenant_id, company_id, registration_id, return_type, document_id, line_no) where is_current;

-- ---------------------------------------------------------------------------------------------------------
-- Security: forced RLS, read for the command roles, written by the posting role, never deleted
-- ---------------------------------------------------------------------------------------------------------
do $$
declare t text;
begin
    foreach t in array array['vat_return', 'vat_return_line', 'vat_return_tax_line'] loop
        execute format('alter table core.%I enable row level security', t);
        execute format('alter table core.%I force row level security', t);
        if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = t and policyname = 'tenant_isolation') then
            execute format('create policy tenant_isolation on core.%I using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id())', t);
        end if;
        execute format('revoke all on core.%I from public', t);
        execute format('grant select on core.%I to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_delete', t);
        execute format('create trigger %I before delete on core.%I for each row execute function core.reject_delete()', t || '_no_delete', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
        execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_delete()', t || '_no_truncate', t);
    end loop;
end $$;
grant insert on core.vat_return, core.vat_return_line, core.vat_return_tax_line to cmd_post;
grant update (superseded_by_return_id, filing_reference, filed_at, filed_by, settled_document_id) on core.vat_return to cmd_post;
grant update (is_current) on core.vat_return_tax_line to cmd_post;
-- the settlement document is a journal document: the posting role already inserts documents and lines, takes numbers and moves the LEDGER head.
-- It now also moves the VAT_RETURN head.
drop policy if exists chain_head_post on core.chain_head;
create policy chain_head_post on core.chain_head as restrictive for update to cmd_post
    using (chain_type in ('LEDGER', 'VAT_RETURN')) with check (chain_type in ('LEDGER', 'VAT_RETURN'));

-- The L1 columns of a return never change. The S columns: filing once, supersession once, settlement while the return lives.
create or replace function core.guard_vat_return() returns trigger language plpgsql as $$
declare s constant text[] := array['superseded_by_return_id', 'filing_reference', 'filed_at', 'filed_by', 'settled_document_id'];
begin
    if (to_jsonb(new) - s) is distinct from (to_jsonb(old) - s) then
        raise exception 'vat_return: an L1 column cannot change' using errcode = '23000';
    end if;
    if old.filed_at is not null and (new.filing_reference is distinct from old.filing_reference or new.filed_at is distinct from old.filed_at
                                     or new.filed_by is distinct from old.filed_by) then
        raise exception 'VAT.ALREADY_FILED: a filing is recorded once' using errcode = '23000';
    end if;
    if old.superseded_by_return_id is not null and new.superseded_by_return_id is distinct from old.superseded_by_return_id then
        raise exception 'vat_return: a return is superseded once' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists vat_return_guard on core.vat_return;
create trigger vat_return_guard before update on core.vat_return for each row execute function core.guard_vat_return();

create or replace function core.guard_vat_return_tax_line() returns trigger language plpgsql as $$
begin
    if (to_jsonb(new) - 'is_current') is distinct from (to_jsonb(old) - 'is_current') then
        raise exception 'vat_return_tax_line: only is_current changes' using errcode = '23000';
    end if;
    if new.is_current and not old.is_current then
        raise exception 'vat_return_tax_line: a line that is no longer current does not become current again' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists vat_return_tax_line_guard on core.vat_return_tax_line;
create trigger vat_return_tax_line_guard before update on core.vat_return_tax_line for each row execute function core.guard_vat_return_tax_line();

create or replace function core.guard_vat_return_line() returns trigger language plpgsql as $$
begin
    raise exception 'vat_return_line is append-only' using errcode = '23000';
end $$;
drop trigger if exists vat_return_line_guard on core.vat_return_line;
create trigger vat_return_line_guard before update on core.vat_return_line for each row execute function core.guard_vat_return_line();

-- The return chain (08, 6.6, 6.8): the head equals the newest return, a return links to its predecessor, sequence 1 starts at the genesis value
create or replace function core.check_vat_return_chain() returns trigger language plpgsql as $$
declare h core.chain_head%rowtype; max_seq bigint; max_hash bytea; expected_prev bytea;
begin
    select * into h from core.chain_head
     where tenant_id = new.tenant_id and company_id = new.company_id and chain_type = 'VAT_RETURN' and scope_key = '';
    if not found then
        raise exception 'no VAT_RETURN chain head for company %', new.company_id using errcode = '23514';
    end if;
    select chain_seq, row_hash into max_seq, max_hash from core.vat_return
     where tenant_id = new.tenant_id and company_id = new.company_id order by chain_seq desc limit 1;
    if h.last_seq <> max_seq or h.last_hash <> max_hash then
        raise exception 'VAT_RETURN chain head (seq %) does not match the newest return (seq %)', h.last_seq, max_seq using errcode = '23514';
    end if;
    if new.chain_seq = 1 then
        expected_prev := sha256(convert_to('genesis|' || new.tenant_id::text || '|' || new.company_id::text || '|VAT_RETURN|', 'UTF8'));
    else
        select row_hash into expected_prev from core.vat_return
         where tenant_id = new.tenant_id and company_id = new.company_id and chain_seq = new.chain_seq - 1;
    end if;
    if expected_prev is distinct from new.prev_hash then
        raise exception 'vat return % does not link to its predecessor in the chain', new.return_id using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists vat_return_chain on core.vat_return;
create constraint trigger vat_return_chain after insert on core.vat_return
    deferrable initially deferred for each row execute function core.check_vat_return_chain();

-- The tax_line_count and late_line_count of a return are what its tax line rows say (checked at commit)
create or replace function core.check_vat_return_counts() returns trigger language plpgsql as $$
declare r core.vat_return%rowtype; n int; l int;
begin
    select * into r from core.vat_return where tenant_id = new.tenant_id and company_id = new.company_id and return_id = new.return_id;
    select count(*), count(*) filter (where is_late) into n, l from core.vat_return_tax_line
     where tenant_id = r.tenant_id and company_id = r.company_id and return_id = r.return_id;
    if n <> r.tax_line_count or l <> r.late_line_count then
        raise exception 'vat return %: the counts (%, %) differ from its tax lines (%, %)', r.return_id, r.tax_line_count, r.late_line_count, n, l using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists vat_return_counts on core.vat_return;
create constraint trigger vat_return_counts after insert on core.vat_return
    deferrable initially deferred for each row execute function core.check_vat_return_counts();
drop trigger if exists vat_return_tax_line_counts on core.vat_return_tax_line;
create constraint trigger vat_return_tax_line_counts after insert on core.vat_return_tax_line
    deferrable initially deferred for each row execute function core.check_vat_return_counts();

insert into platform.schema_migration (version, description)
values ('0026', 'VAT returns: the snapshot of a return, its chain, the settlement line type and the registration of the role VAT_SETTLEMENT')
on conflict (version) do nothing;
