-- 0058 extension attributes (15, 3.2, 3.5, 3.6, 3.6a, 4): the namespaces of the apps, the definitions of attributes, their values and the version counter of a (record, namespace).
-- Configuration data (M): the configuration role writes it with master_change_log rows and events. No core rule reads a value (15, 4.2). A value row exists only while the value is set:
-- clearing deletes the row, and the history is master_change_log. The counter never decreases and is never deleted (E7). Not here: the anonymisation clear of is_pii values (12, 4.3).
-- extension_max_definitions (07, 5.2) is a tenant setting, default 50.

alter table core.tenant_setting add column if not exists extension_max_definitions int not null default 50 check (extension_max_definitions >= 1);

create table if not exists core.app_namespace (
    tenant_id  uuid not null references platform.tenant (tenant_id),
    namespace  core.code not null check (namespace ~ '^[a-z][a-z0-9_]{1,49}$' and namespace <> 'core'),
    app_id     uuid not null,
    created_at timestamptz not null default now(),
    created_by varchar(100) not null,
    primary key (tenant_id, namespace),
    foreign key (tenant_id, app_id) references core.app_registration (tenant_id, app_id)
);

create table if not exists core.extension_definition (
    tenant_id          uuid not null references platform.tenant (tenant_id),
    definition_id      uuid not null default gen_random_uuid(),
    entity_type        core.code not null,
    namespace          core.code not null,
    attr_key           core.code not null check (attr_key ~ '^[a-z][a-z0-9_]{0,49}$'),
    data_type          core.code not null check (data_type in ('TEXT', 'DECIMAL', 'DATE', 'BOOLEAN', 'REF')),
    max_length         int check (max_length between 1 and 4000),
    decimal_precision  int check (decimal_precision between 1 and 28),
    decimal_scale      int check (decimal_scale between 0 and 8),
    allowed_values     jsonb,
    ref_entity_type    core.code,
    is_pii             boolean not null default false,
    is_required_on_create boolean not null default false check (not is_required_on_create),
    active             boolean not null default true,
    row_version        bigint not null default 1,
    created_at         timestamptz not null default now(),
    created_by         varchar(100) not null,
    updated_at         timestamptz not null default now(),
    updated_by         varchar(100) not null,
    primary key (tenant_id, definition_id),
    unique (tenant_id, entity_type, namespace, attr_key),
    foreign key (tenant_id, namespace) references core.app_namespace (tenant_id, namespace),
    check ((data_type = 'REF') = (ref_entity_type is not null)),
    check (allowed_values is null or data_type = 'TEXT'),
    check (max_length is null or data_type = 'TEXT'),
    check ((decimal_precision is null and decimal_scale is null) or data_type = 'DECIMAL')
);

create table if not exists core.extension_value (
    tenant_id     uuid not null,
    definition_id uuid not null,
    entity_key    varchar(200) not null,
    version_stamp bigint not null,
    company_id    uuid,
    value_text    text,
    value_decimal numeric(28, 8),
    value_date    date,
    value_bool    boolean,
    value_ref     text,
    primary key (tenant_id, definition_id, entity_key),
    foreign key (tenant_id, definition_id) references core.extension_definition (tenant_id, definition_id),
    -- exactly one value column is set, as the definition gives
    check (num_nonnulls(value_text, value_decimal, value_date, value_bool, value_ref) = 1)
);
create index if not exists extension_value_text on core.extension_value (tenant_id, definition_id, value_text) where value_text is not null;
create index if not exists extension_value_decimal on core.extension_value (tenant_id, definition_id, value_decimal) where value_decimal is not null;
create index if not exists extension_value_date on core.extension_value (tenant_id, definition_id, value_date) where value_date is not null;
create index if not exists extension_value_entity on core.extension_value (tenant_id, entity_key);

create table if not exists core.extension_counter (
    tenant_id   uuid not null references platform.tenant (tenant_id),
    entity_type core.code not null,
    entity_key  varchar(200) not null,
    namespace   core.code not null,
    counter     bigint not null default 0 check (counter >= 0),
    updated_at  timestamptz not null default now(),
    primary key (tenant_id, entity_type, entity_key, namespace)
);

-- the identity of a definition never changes, and is_pii is locked once a value exists (EXT.DEFINITION_LOCKED)
create or replace function core.guard_extension_definition() returns trigger language plpgsql as $$
begin
    if (new.entity_type, new.namespace, new.attr_key, new.data_type, new.ref_entity_type, new.created_by)
       is distinct from (old.entity_type, old.namespace, old.attr_key, old.data_type, old.ref_entity_type, old.created_by) then
        raise exception 'extension_definition: entity type, namespace, key and data type never change' using errcode = '23000';
    end if;
    if new.is_pii is distinct from old.is_pii and exists (select 1 from core.extension_value v where v.tenant_id = old.tenant_id and v.definition_id = old.definition_id) then
        raise exception 'extension_definition: is_pii cannot change once a value exists' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists extension_definition_guard on core.extension_definition;
create trigger extension_definition_guard before update on core.extension_definition
    for each row execute function core.guard_extension_definition();

-- the counter only moves forward (E7)
create or replace function core.guard_extension_counter() returns trigger language plpgsql as $$
begin
    if new.counter < old.counter or (new.tenant_id, new.entity_type, new.entity_key, new.namespace) is distinct from (old.tenant_id, old.entity_type, old.entity_key, old.namespace) then
        raise exception 'extension_counter: the counter only moves forward' using errcode = '23000';
    end if;
    return new;
end $$;
drop trigger if exists extension_counter_guard on core.extension_counter;
create trigger extension_counter_guard before update on core.extension_counter
    for each row execute function core.guard_extension_counter();

do $$
declare t text;
begin
    foreach t in array array['app_namespace', 'extension_definition', 'extension_value', 'extension_counter'] loop
        execute format('alter table core.%I enable row level security', t);
        execute format('alter table core.%I force row level security', t);
        if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = t and policyname = 'tenant_isolation') then
            execute format('create policy tenant_isolation on core.%I using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id())', t);
        end if;
        execute format('revoke all on core.%I from public', t);
        execute format('grant select on core.%I to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_retention', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
        execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_delete()', t || '_no_truncate', t);
    end loop;
    foreach t in array array['app_namespace', 'extension_definition', 'extension_counter'] loop
        execute format('drop trigger if exists %I on core.%I', t || '_no_delete', t);
        execute format('create trigger %I before delete on core.%I for each row execute function core.reject_delete()', t || '_no_delete', t);
    end loop;
end $$;

grant insert on core.app_namespace, core.extension_definition, core.extension_value, core.extension_counter to cmd_config;
grant update (allowed_values, max_length, decimal_precision, decimal_scale, is_pii, active, row_version, updated_at, updated_by) on core.extension_definition to cmd_config;
grant update (version_stamp, company_id, value_text, value_decimal, value_date, value_bool, value_ref) on core.extension_value to cmd_config;
grant delete on core.extension_value to cmd_config;
grant update (counter, updated_at) on core.extension_counter to cmd_config;

insert into platform.schema_migration (version, description)
values ('0058', 'extension attributes: app_namespace, extension_definition, extension_value, extension_counter')
on conflict (version) do nothing;
