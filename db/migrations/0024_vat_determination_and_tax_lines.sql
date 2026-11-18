-- 0024 VAT determination and tax lines (07, 8.4, 9.4, 02, 7.1 and 7.2, 08, 6.7): the VAT registrations of a partner, the rules that determine a tax code
-- with their alternatives, and the tax lines of a document with the base lines they cover.
-- Written by the commands of 07, 11.1 (write.config for the rules and the partner registrations) and by the posting commands (the tax lines).
-- Not in this migration: the evidence rows of a tax line (08, 5.3), the deduction keys (07, 8.6), the return snapshots (08, 6). The columns of a tax line for
-- partial deduction exist (hash_version 2 hashes them) and hold the values of "no partial deduction" until that is built.

-- ---------------------------------------------------------------------------------------------------------
-- partner_tax_registration (07, 9.4). A surrogate id is added to the key of the spec, so that the commands of a master table address a row by one id.
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.partner_tax_registration (
    tenant_id                    uuid not null references platform.tenant (tenant_id),
    partner_id                   uuid not null,
    country_code                 char(2) not null references platform.ref_country (country_code),
    valid_from                   date not null,
    partner_tax_registration_id  uuid not null default gen_random_uuid(),
    vat_status                   core.code not null check (vat_status in ('REGISTERED', 'NOT_REGISTERED')),
    vat_id                       varchar(30),
    valid_to                     date,
    active                       boolean not null default true,
    row_version                  bigint not null default 1,
    created_at                   timestamptz not null default now(),
    created_by                   varchar(100) not null,
    updated_at                   timestamptz not null default now(),
    updated_by                   varchar(100) not null,
    primary key (tenant_id, partner_id, country_code, valid_from),
    unique (tenant_id, partner_tax_registration_id),
    foreign key (tenant_id, partner_id) references core.business_partner (tenant_id, partner_id),
    check (valid_to is null or valid_to >= valid_from)
);

-- ---------------------------------------------------------------------------------------------------------
-- tax_determination_rule (07, 8.4, V) and its alternatives. A rule is a rule_id with versions. Every key column is nullable, null means any.
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.tax_determination_rule (
    tenant_id            uuid not null,
    company_id           uuid not null,
    rule_id              uuid not null,
    valid_from           date not null,
    valid_to             date,
    transaction_type     core.code check (transaction_type in ('SALES', 'PURCHASE')),
    origin_country       char(2) references platform.ref_country (country_code),
    destination_country  char(2) references platform.ref_country (country_code),
    origin_eu            boolean,
    destination_eu       boolean,
    partner_vat_status   core.code check (partner_vat_status in ('REGISTERED', 'NOT_REGISTERED')),
    account_id           uuid,
    tax_class_id         uuid,
    priority             smallint not null check (priority >= 0),
    tax_code_id          uuid not null,
    created_at           timestamptz not null default now(),
    created_by           varchar(100) not null,
    primary key (tenant_id, company_id, rule_id, valid_from),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, company_id, account_id) references core.gl_account (tenant_id, company_id, account_id),
    foreign key (tenant_id, tax_class_id) references core.tax_class (tenant_id, tax_class_id),
    foreign key (tenant_id, company_id, tax_code_id) references core.tax_code (tenant_id, company_id, tax_code_id),
    check (valid_to is null or valid_to >= valid_from)
);
create index if not exists tdr_company_validity on core.tax_determination_rule (tenant_id, company_id, valid_from);

create table if not exists core.tax_determination_alternative (
    tenant_id        uuid not null,
    company_id       uuid not null,
    rule_id          uuid not null,
    valid_from       date not null,
    alt_tax_code_id  uuid not null,
    primary key (tenant_id, company_id, rule_id, valid_from, alt_tax_code_id),
    foreign key (tenant_id, company_id, rule_id, valid_from) references core.tax_determination_rule (tenant_id, company_id, rule_id, valid_from),
    foreign key (tenant_id, company_id, alt_tax_code_id) references core.tax_code (tenant_id, company_id, tax_code_id)
);

-- ---------------------------------------------------------------------------------------------------------
-- journal_line_tax (02, 7.1) and journal_line_tax_base (02, 7.2): L1 rows, written with the document, never changed
-- ---------------------------------------------------------------------------------------------------------
create table if not exists core.journal_line_tax (
    tenant_id                uuid not null,
    company_id               uuid not null,
    document_id              uuid not null,
    line_no                  int not null,
    tax_code_id              uuid not null,
    tax_code_version_id      uuid not null,
    tax_type                 core.code not null check (tax_type in ('OUTPUT', 'INPUT', 'EXEMPT', 'ZERO_RATED')),
    tax_rate                 core.rate not null check (tax_rate >= 0 and tax_rate <= 100),
    base_amount_tc           core.amt not null,
    base_amount_lc           core.amt not null,
    base_amount_gc           core.amt not null,
    base_amount_rc           core.amt,
    determined_tax_code_id   uuid not null,
    tax_class_id             uuid,
    origin_country           char(2) not null,
    destination_country      char(2) not null,
    partner_vat_status       core.code check (partner_vat_status in ('REGISTERED', 'NOT_REGISTERED')),
    tax_date                 date not null,
    vat_currency             char(3) not null references platform.ref_currency (currency_code),
    vat_base_amount          core.amt not null,
    vat_tax_amount           core.amt not null,
    vat_rate                 core.rate not null check (vat_rate > 0),
    vat_rate_date            date,
    is_override              boolean not null,
    tax_amount_supplied_tc   core.amt,
    rounding_mode            core.code not null check (rounding_mode in ('PER_DOCUMENT', 'PER_LINE')),
    origin_eu                boolean not null,
    destination_eu           boolean not null,
    deductible_pct           core.rate not null check (deductible_pct >= 0 and deductible_pct <= 100),
    deduction_key            core.code,
    deduction_key_rate       core.rate,
    deduction_share          core.rate not null check (deduction_share >= 0 and deduction_share <= 100),
    deduction_cap_amount_tc  core.amt,
    gross_tax_amount_tc      core.amt not null,
    primary key (tenant_id, company_id, document_id, line_no),
    foreign key (tenant_id, company_id, document_id, line_no) references core.journal_line (tenant_id, company_id, document_id, line_no),
    foreign key (tenant_id, tax_code_version_id) references core.tax_code_version (tenant_id, tax_code_version_id),
    foreign key (tenant_id, company_id, tax_code_id) references core.tax_code (tenant_id, company_id, tax_code_id),
    foreign key (tenant_id, company_id, determined_tax_code_id) references core.tax_code (tenant_id, company_id, tax_code_id)
);
create index if not exists jlt_tax_date on core.journal_line_tax (tenant_id, company_id, tax_date);

create table if not exists core.journal_line_tax_base (
    tenant_id              uuid not null,
    company_id             uuid not null,
    document_id            uuid not null,
    tax_line_no            int not null,
    base_line_no           int not null,
    base_amount_tc         core.amt not null,
    nondeductible_tax_tc   core.amt not null default 0,
    nondeductible_line_no  int,
    primary key (tenant_id, company_id, document_id, tax_line_no, base_line_no),
    foreign key (tenant_id, company_id, document_id, tax_line_no) references core.journal_line_tax (tenant_id, company_id, document_id, line_no),
    foreign key (tenant_id, company_id, document_id, base_line_no) references core.journal_line (tenant_id, company_id, document_id, line_no)
);

-- ---------------------------------------------------------------------------------------------------------
-- Security: forced RLS, read for the command roles, never delete. The rules and the partner registrations are written by the configuration role,
-- the tax lines by the posting role.
-- ---------------------------------------------------------------------------------------------------------
do $$
declare t text;
begin
    foreach t in array array['partner_tax_registration', 'tax_determination_rule', 'tax_determination_alternative', 'journal_line_tax', 'journal_line_tax_base'] loop
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
    -- versions of a rule and the rows of a tax line are insert-only
    foreach t in array array['tax_determination_rule', 'tax_determination_alternative', 'journal_line_tax', 'journal_line_tax_base'] loop
        execute format('drop trigger if exists %I on core.%I', t || '_append_only', t);
        execute format('create trigger %I before update on core.%I for each row execute function core.reject_change()', t || '_append_only', t);
    end loop;
end $$;
grant select on core.partner_tax_registration, core.tax_determination_rule, core.tax_determination_alternative, core.journal_line_tax, core.journal_line_tax_base
      to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_retention;
grant insert on core.partner_tax_registration, core.tax_determination_rule, core.tax_determination_alternative to cmd_config;
grant update (vat_status, vat_id, valid_to, active, row_version, updated_at, updated_by) on core.partner_tax_registration to cmd_config;
grant insert on core.journal_line_tax, core.journal_line_tax_base to cmd_post;

-- ---------------------------------------------------------------------------------------------------------
-- Guards
-- ---------------------------------------------------------------------------------------------------------
-- a rule version starts after the latest one of its rule, and the code is a code of the company (07, 8.3)
create or replace function core.guard_tax_determination_rule() returns trigger language plpgsql as $$
declare v_latest date;
begin
    select max(valid_from) into v_latest from core.tax_determination_rule
     where tenant_id = new.tenant_id and company_id = new.company_id and rule_id = new.rule_id;
    if v_latest is not null and new.valid_from <= v_latest then
        raise exception 'CONFIG.VALID_FROM_NOT_LATER: a version starts after the latest one (%)', v_latest using errcode = '23514';
    end if;
    return new;
end $$;
drop trigger if exists tax_determination_rule_guard on core.tax_determination_rule;
create trigger tax_determination_rule_guard before insert on core.tax_determination_rule
    for each row execute function core.guard_tax_determination_rule();

-- a partner is registered for VAT in a country once at a time, and the VAT id of a private person is not kept here (07, 9.4, Q3)
create or replace function core.guard_partner_tax_registration() returns trigger language plpgsql as $$
begin
    if new.vat_id is not null and exists (select 1 from core.business_partner p where p.tenant_id = new.tenant_id and p.partner_id = new.partner_id and p.partner_kind = 'PERSON') then
        raise exception 'SCHEMA.INVALID_VALUE: the VAT id of a private person is personal data and is not kept in this table' using errcode = '23514';
    end if;
    return new;
end $$;
drop trigger if exists partner_tax_registration_guard on core.partner_tax_registration;
create trigger partner_tax_registration_guard before insert or update on core.partner_tax_registration
    for each row execute function core.guard_partner_tax_registration();

-- a tax line is the detail of a TAX line, and its base rows lie in the same document (02, 7.1 and 7.2)
create or replace function core.guard_journal_line_tax() returns trigger language plpgsql as $$
begin
    if not exists (select 1 from core.journal_line l where l.tenant_id = new.tenant_id and l.company_id = new.company_id and l.document_id = new.document_id
                      and l.line_no = new.line_no and l.line_type = 'TAX') then
        raise exception 'journal_line_tax: the line % is not a TAX line', new.line_no using errcode = '23514';
    end if;
    return new;
end $$;
drop trigger if exists journal_line_tax_guard on core.journal_line_tax;
create trigger journal_line_tax_guard before insert on core.journal_line_tax
    for each row execute function core.guard_journal_line_tax();

-- every TAX line has its detail, checked at commit (the line is inserted before its detail)
create or replace function core.check_tax_line_has_detail() returns trigger language plpgsql as $$
begin
    if new.line_type = 'TAX' and not exists (select 1 from core.journal_line_tax t where t.tenant_id = new.tenant_id and t.company_id = new.company_id
                                                and t.document_id = new.document_id and t.line_no = new.line_no) then
        raise exception 'journal_line: the TAX line % of the document has no tax detail', new.line_no using errcode = '23514';
    end if;
    return null;
end $$;
drop trigger if exists journal_line_tax_detail_check on core.journal_line;
create constraint trigger journal_line_tax_detail_check after insert on core.journal_line
    deferrable initially deferred for each row execute function core.check_tax_line_has_detail();

-- an account that tax codes use accepts only tax lines (08, 6.7). The reversal of such a document carries the line type TAX too. The document types of the
-- VAT settlement are added when they exist.
create or replace function core.guard_tax_account_posting() returns trigger language plpgsql as $$
begin
    if new.line_type <> 'TAX' and exists (select 1 from core.gl_account a where a.tenant_id = new.tenant_id and a.company_id = new.company_id
                                             and a.account_id = new.account_id and a.tax_account) then
        raise exception 'ACCOUNT_RULE.TAX_ACCOUNT_DIRECT_POSTING: a tax account accepts only tax lines' using errcode = '23514';
    end if;
    return new;
end $$;
drop trigger if exists journal_line_tax_account_guard on core.journal_line;
create trigger journal_line_tax_account_guard before insert on core.journal_line
    for each row execute function core.guard_tax_account_posting();

insert into platform.schema_migration (version, description)
values ('0024', 'VAT determination rules, partner tax registrations and the tax lines of a document')
on conflict (version) do nothing;
