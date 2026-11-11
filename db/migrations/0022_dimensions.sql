-- 0022 Dimensions (02, 5.3 and 6, 07, 6.2 and 6.5, ADR-010, ADR-034, ADR-067): dimension_type and dimension_value (pattern M),
-- account_dimension_rule (M), journal_line_dimension (the free dimensions of a line), the keys of the four typed columns of journal_line,
-- and the opening of the L2 columns cost_center_id, project_id, profit_center_id, business_unit_id and journal_line_dimension.dimension_value_id
-- (a change needs a change_log row of the same transaction, as for line_text in 0013).
-- The four standard types exist in every tenant: a trigger on platform.tenant creates them, and this script creates them for the tenants that exist.
-- What is validated where: the command checks the rules of the account, the validity of a value at the posting date and its state (06, 5.2 P2 and P4).
-- The database checks what never depends on a date: the type of a value against the column or the type it is used for, the company restriction,
-- the hierarchy, and who may change what.

-- ---------------------------------------------------------------------------------------------------------
-- dimension_type (07, 6.5)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.dimension_type (
    tenant_id         uuid not null references platform.tenant (tenant_id),
    dimension_type_id uuid not null default gen_random_uuid(),
    type_code         core.code not null,
    name              varchar(100) not null,
    kind              core.code not null check (kind in ('STANDARD', 'FREE')),                                                 -- IAP
    standard_role     core.code check (standard_role in ('COST_CENTER', 'PROJECT', 'PROFIT_CENTER', 'BUSINESS_UNIT')),         -- IAP
    active            boolean not null default true,
    row_version       bigint not null default 1,
    created_at        timestamptz not null default now(),
    created_by        varchar(100) not null,
    updated_at        timestamptz not null default now(),
    updated_by        varchar(100) not null,
    primary key (tenant_id, dimension_type_id),
    unique (tenant_id, type_code),
    check ((kind = 'STANDARD') = (standard_role is not null)),
    check (kind = 'FREE' or active)                                                                                        -- a standard type is never deactivated
);
create unique index if not exists dimension_type_standard_role on core.dimension_type (tenant_id, standard_role) where standard_role is not null;

-- ---------------------------------------------------------------------------------------------------------
-- dimension_value (07, 6.5): tenant-wide with an optional company restriction (ADR-067)
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.dimension_value (
    tenant_id          uuid not null references platform.tenant (tenant_id),
    dimension_value_id uuid not null default gen_random_uuid(),
    dimension_type_id  uuid not null,                                                                                      -- IAP
    company_id         uuid,                                                                                               -- null = all companies of the tenant, IAP once referenced
    value_code         core.code not null,
    name               varchar(100) not null,
    parent_value_id    uuid,
    valid_from         date not null,
    valid_to           date,
    active             boolean not null default true,
    row_version        bigint not null default 1,
    created_at         timestamptz not null default now(),
    created_by         varchar(100) not null,
    updated_at         timestamptz not null default now(),
    updated_by         varchar(100) not null,
    primary key (tenant_id, dimension_value_id),
    unique (tenant_id, dimension_type_id, value_code),
    unique (tenant_id, dimension_value_id, dimension_type_id),
    foreign key (tenant_id, dimension_type_id) references core.dimension_type (tenant_id, dimension_type_id),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, parent_value_id, dimension_type_id) references core.dimension_value (tenant_id, dimension_value_id, dimension_type_id),
    check (valid_to is null or valid_to >= valid_from),
    check (parent_value_id is null or parent_value_id <> dimension_value_id)
);
create index if not exists dimension_value_type on core.dimension_value (tenant_id, dimension_type_id);

-- ---------------------------------------------------------------------------------------------------------
-- account_dimension_rule (07, 6.2): absence of a row means OPTIONAL. The rule in force at command time counts (Q8), no versions.
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.account_dimension_rule (
    tenant_id         uuid not null,
    company_id        uuid not null,
    account_id        uuid not null,
    dimension_type_id uuid not null,
    rule              core.code not null check (rule in ('MANDATORY', 'OPTIONAL', 'FORBIDDEN')),
    row_version       bigint not null default 1,
    created_at        timestamptz not null default now(),
    created_by        varchar(100) not null,
    updated_at        timestamptz not null default now(),
    updated_by        varchar(100) not null,
    primary key (tenant_id, company_id, account_id, dimension_type_id),
    foreign key (tenant_id, company_id, account_id) references core.gl_account (tenant_id, company_id, account_id),
    foreign key (tenant_id, dimension_type_id) references core.dimension_type (tenant_id, dimension_type_id)
);

-- ---------------------------------------------------------------------------------------------------------
-- journal_line_dimension (02, 6): the free dimensions of a line. The row is never deleted, clearing a value sets it to null (Q6).
-- The foreign key to (value, type) makes the value belong to the type of the row.
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.journal_line_dimension (
    tenant_id          uuid not null,
    company_id         uuid not null,
    document_id        uuid not null,
    line_no            int not null,
    dimension_type_id  uuid not null,                                                                                      -- L1
    dimension_value_id uuid,                                                                                               -- L2
    primary key (tenant_id, company_id, document_id, line_no, dimension_type_id),
    foreign key (tenant_id, company_id, document_id, line_no) references core.journal_line (tenant_id, company_id, document_id, line_no),
    foreign key (tenant_id, dimension_type_id) references core.dimension_type (tenant_id, dimension_type_id),
    foreign key (tenant_id, dimension_value_id, dimension_type_id) references core.dimension_value (tenant_id, dimension_value_id, dimension_type_id)
);
create index if not exists journal_line_dimension_value on core.journal_line_dimension (tenant_id, dimension_value_id);

-- the four typed columns of journal_line (0005) get their keys
do $$
declare c text;
begin
    foreach c in array array['cost_center_id', 'project_id', 'profit_center_id', 'business_unit_id'] loop
        if not exists (select 1 from pg_constraint where conname = 'journal_line_' || c || '_fkey') then
            execute format('alter table core.journal_line add constraint %I foreign key (tenant_id, %I) references core.dimension_value (tenant_id, dimension_value_id)',
                           'journal_line_' || c || '_fkey', c);
        end if;
    end loop;
end $$;
create index if not exists journal_line_cost_center on core.journal_line (tenant_id, cost_center_id) where cost_center_id is not null;
create index if not exists journal_line_project on core.journal_line (tenant_id, project_id) where project_id is not null;
create index if not exists journal_line_profit_center on core.journal_line (tenant_id, profit_center_id) where profit_center_id is not null;
create index if not exists journal_line_business_unit on core.journal_line (tenant_id, business_unit_id) where business_unit_id is not null;

-- ---------------------------------------------------------------------------------------------------------
-- Security: forced RLS, read for the command roles, write for the configuration role (the commands of 07, 11.1) and the posting roles
-- (journal_line_dimension), never delete.
-- ---------------------------------------------------------------------------------------------------------
do $$
declare t text;
begin
    foreach t in array array['dimension_type', 'dimension_value', 'account_dimension_rule', 'journal_line_dimension'] loop
        execute format('alter table core.%I enable row level security', t);
        execute format('alter table core.%I force row level security', t);
        if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = t and policyname = 'tenant_isolation') then
            execute format('create policy tenant_isolation on core.%I using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id())', t);
        end if;
        execute format('revoke all on core.%I from public', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_delete', t);
        execute format('create trigger %I before delete on core.%I for each row execute function core.reject_delete()', t || '_no_delete', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
        execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_delete()', t || '_no_truncate', t);
    end loop;
end $$;
grant select on core.dimension_type, core.dimension_value, core.account_dimension_rule, core.journal_line_dimension
      to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_retention;
grant insert on core.dimension_type, core.dimension_value, core.account_dimension_rule to cmd_config;
grant update (type_code, name, active, row_version, updated_at, updated_by) on core.dimension_type to cmd_config;
grant update (company_id, value_code, name, parent_value_id, valid_from, valid_to, active, row_version, updated_at, updated_by)
      on core.dimension_value to cmd_config;
grant update (rule, row_version, updated_at, updated_by) on core.account_dimension_rule to cmd_config;
grant insert on core.journal_line_dimension to cmd_post, cmd_clearing;
grant update (dimension_value_id) on core.journal_line_dimension to cmd_post;
grant update (cost_center_id, project_id, profit_center_id, business_unit_id) on core.journal_line to cmd_post;

-- ---------------------------------------------------------------------------------------------------------
-- The four standard types of every tenant (ADR-010): created with the tenant, and now for the tenants that exist
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.seed_standard_dimension_types() returns trigger language plpgsql security definer
set search_path = pg_catalog, core as $$
begin
    insert into core.dimension_type (tenant_id, type_code, name, kind, standard_role, created_by, updated_by)
    select new.tenant_id, v.code, v.name, 'STANDARD', v.code, 'system', 'system'
      from (values ('COST_CENTER', 'Cost center'), ('PROJECT', 'Project'), ('PROFIT_CENTER', 'Profit center'), ('BUSINESS_UNIT', 'Business unit')) v (code, name)
    on conflict (tenant_id, type_code) do nothing;
    return new;
end $$;
drop trigger if exists tenant_standard_dimension_types on platform.tenant;
create trigger tenant_standard_dimension_types after insert on platform.tenant
    for each row execute function core.seed_standard_dimension_types();

insert into core.dimension_type (tenant_id, type_code, name, kind, standard_role, created_by, updated_by)
select t.tenant_id, v.code, v.name, 'STANDARD', v.code, 'system', 'system'
  from platform.tenant t
 cross join (values ('COST_CENTER', 'Cost center'), ('PROJECT', 'Project'), ('PROFIT_CENTER', 'Profit center'), ('BUSINESS_UNIT', 'Business unit')) v (code, name)
on conflict (tenant_id, type_code) do nothing;

-- ---------------------------------------------------------------------------------------------------------
-- Guards of the master tables (07, 2.3, 6.5)
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.guard_dimension_type() returns trigger language plpgsql as $$
begin
    if new.kind <> old.kind or new.standard_role is distinct from old.standard_role then
        raise exception 'CONFIG.IMMUTABLE_AFTER_POSTING: the kind and the standard role of a dimension type never change' using errcode = '23000';
    end if;
    if old.kind = 'STANDARD' and new.type_code <> old.type_code then
        raise exception 'CONFIG.IMMUTABLE_AFTER_POSTING: the code of a standard dimension type never changes' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists dimension_type_guard on core.dimension_type;
create trigger dimension_type_guard before update on core.dimension_type
    for each row execute function core.guard_dimension_type();

create or replace function core.dimension_value_referenced(p_tenant uuid, p_value uuid) returns boolean language sql stable as $$
    select exists (select 1 from core.journal_line l
                    where l.tenant_id = p_tenant and p_value in (l.cost_center_id, l.project_id, l.profit_center_id, l.business_unit_id))
        or exists (select 1 from core.journal_line_dimension d where d.tenant_id = p_tenant and d.dimension_value_id = p_value)
$$;

create or replace function core.guard_dimension_value() returns trigger language plpgsql as $$
declare
    v_up uuid;
    v_depth int := 0;
begin
    if tg_op = 'UPDATE' then
        if new.dimension_type_id <> old.dimension_type_id and core.dimension_value_referenced(old.tenant_id, old.dimension_value_id) then
            raise exception 'CONFIG.IMMUTABLE_AFTER_POSTING: the type of a dimension value that lines refer to cannot change' using errcode = '23000';
        end if;
        if new.company_id is distinct from old.company_id and new.company_id is not null
           and exists (select 1 from core.journal_line l
                        where l.tenant_id = old.tenant_id and l.company_id <> new.company_id
                          and old.dimension_value_id in (l.cost_center_id, l.project_id, l.profit_center_id, l.business_unit_id)
                       union all
                       select 1 from core.journal_line_dimension d
                        where d.tenant_id = old.tenant_id and d.company_id <> new.company_id and d.dimension_value_id = old.dimension_value_id) then
            raise exception 'CONFIG.DIMENSION_COMPANY_MISMATCH: lines of another company refer to this value' using errcode = '23000';
        end if;
    end if;
    -- no cycle in the rollup hierarchy (07, 6.5)
    v_up := new.parent_value_id;
    while v_up is not null loop
        if v_up = new.dimension_value_id then
            raise exception 'CONFIG.HIERARCHY_CYCLE: the parent chain of a dimension value leads back to the value' using errcode = '23000';
        end if;
        v_depth := v_depth + 1;
        if v_depth > 1000 then
            raise exception 'CONFIG.HIERARCHY_CYCLE: the hierarchy is deeper than 1000 levels' using errcode = '23000';
        end if;
        select parent_value_id into v_up from core.dimension_value where tenant_id = new.tenant_id and dimension_value_id = v_up;
    end loop;
    return new;
end $$;
drop trigger if exists dimension_value_guard on core.dimension_value;
create trigger dimension_value_guard before insert or update on core.dimension_value
    for each row execute function core.guard_dimension_value();

-- ---------------------------------------------------------------------------------------------------------
-- What a line may carry (02, 5.3 and 6): the value of a typed column belongs to a type with the matching standard role, the value of a free
-- dimension belongs to a FREE type, and a value restricted to a company is used by that company only. Dates and states are the command's.
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.check_line_dimension(p_tenant uuid, p_company uuid, p_value uuid, p_role text, p_column text) returns void
language plpgsql as $$
declare
    v_company uuid;
    v_kind text;
    v_role text;
begin
    if p_value is null then return; end if;
    select v.company_id, t.kind, t.standard_role into v_company, v_kind, v_role
      from core.dimension_value v join core.dimension_type t on t.tenant_id = v.tenant_id and t.dimension_type_id = v.dimension_type_id
     where v.tenant_id = p_tenant and v.dimension_value_id = p_value;
    if not found then
        raise exception 'ACCOUNT_RULE.DIMENSION_VALUE_INVALID: % does not name a dimension value', p_column using errcode = '23000';
    end if;
    if p_role is null and v_kind <> 'FREE' or p_role is not null and v_role is distinct from p_role then
        raise exception 'ACCOUNT_RULE.DIMENSION_VALUE_INVALID: the value of % belongs to a dimension type of another kind or role', p_column using errcode = '23000';
    end if;
    if v_company is not null and v_company <> p_company then
        raise exception 'ACCOUNT_RULE.DIMENSION_VALUE_INVALID: the value of % is restricted to another company', p_column using errcode = '23000';
    end if;
end $$;

create or replace function core.check_journal_line_dimensions() returns trigger language plpgsql as $$
begin
    perform core.check_line_dimension(new.tenant_id, new.company_id, new.cost_center_id, 'COST_CENTER', 'cost_center_id');
    perform core.check_line_dimension(new.tenant_id, new.company_id, new.project_id, 'PROJECT', 'project_id');
    perform core.check_line_dimension(new.tenant_id, new.company_id, new.profit_center_id, 'PROFIT_CENTER', 'profit_center_id');
    perform core.check_line_dimension(new.tenant_id, new.company_id, new.business_unit_id, 'BUSINESS_UNIT', 'business_unit_id');
    return new;
end $$;
drop trigger if exists journal_line_dimensions_check on core.journal_line;
create trigger journal_line_dimensions_check before insert or update on core.journal_line
    for each row execute function core.check_journal_line_dimensions();

-- ---------------------------------------------------------------------------------------------------------
-- The lock classes of a line now open the four typed columns (02, 8, 10), each with its change_log row (entity type LINE)
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.guard_journal_line() returns trigger language plpgsql as $$
declare
    f text;
    open_columns constant text[] := array['line_text', 'cost_center_id', 'project_id', 'profit_center_id', 'business_unit_id'];
begin
    if (to_jsonb(new) - open_columns) is distinct from (to_jsonb(old) - open_columns) then
        raise exception 'journal_line: only line_text and the typed dimensions can change, and only through the change command' using errcode = '23000';
    end if;
    foreach f in array open_columns loop
        if (to_jsonb(new) ->> f) is distinct from (to_jsonb(old) ->> f) then
            perform core.require_l2_log(new.tenant_id, new.company_id, new.document_id, new.line_no, f,
                                        to_jsonb(old) ->> f, to_jsonb(new) ->> f, new.fiscal_year, new.fiscal_period);
        end if;
    end loop;
    return new;
end $$;

-- journal_line_dimension: the key and the type are L1, the value is L2 and changes with a change_log row (LINE_DIMENSION, sub_key = the type)
create or replace function core.guard_journal_line_dimension() returns trigger language plpgsql as $$
declare
    v_year int; v_period int; v_status text;
begin
    if tg_op = 'INSERT' then
        perform core.check_line_dimension(new.tenant_id, new.company_id, new.dimension_value_id, null, 'dimension_value_id');
        if not exists (select 1 from core.dimension_type t where t.tenant_id = new.tenant_id and t.dimension_type_id = new.dimension_type_id and t.kind = 'FREE') then
            raise exception 'journal_line_dimension: the type of a free dimension must be of kind FREE' using errcode = '23000';
        end if;
        return new;
    end if;
    if (to_jsonb(new) - 'dimension_value_id') is distinct from (to_jsonb(old) - 'dimension_value_id') then
        raise exception 'journal_line_dimension: an L1 column cannot change' using errcode = '23000';
    end if;
    perform core.check_line_dimension(new.tenant_id, new.company_id, new.dimension_value_id, null, 'dimension_value_id');
    select fiscal_year, fiscal_period into v_year, v_period from core.journal_line
     where tenant_id = new.tenant_id and company_id = new.company_id and document_id = new.document_id and line_no = new.line_no;
    select status into v_status from core.fiscal_period
     where tenant_id = new.tenant_id and company_id = new.company_id and fiscal_year = v_year and period_no = v_period;
    if v_status = 'HARD_CLOSED' then
        raise exception 'LOCK.FIELD_LOCKED_L3: a dimension cannot change in a hard-closed period' using errcode = '23000';
    end if;
    if not exists (
        select 1 from core.change_log c
         where c.tenant_id = new.tenant_id and c.company_id = new.company_id and c.document_id = new.document_id and c.line_no = new.line_no
           and c.entity_type = 'LINE_DIMENSION' and c.sub_key = new.dimension_type_id::text and c.field_name = 'dimension_value_id'
           and c.old_value is not distinct from old.dimension_value_id::text and c.new_value is not distinct from new.dimension_value_id::text
           and c.lock_class = 'L2' and c.period_status = v_status and c.changed_at = transaction_timestamp()) then
        raise exception 'journal_line_dimension: the L2 column dimension_value_id cannot change without a matching change_log row in this transaction'
            using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists journal_line_dimension_guard on core.journal_line_dimension;
create trigger journal_line_dimension_guard before insert or update on core.journal_line_dimension
    for each row execute function core.guard_journal_line_dimension();

insert into platform.schema_migration (version, description)
values ('0022', 'dimensions: types, values, account rules, journal_line_dimension, the L2 guard of the typed columns')
on conflict (version) do nothing;
