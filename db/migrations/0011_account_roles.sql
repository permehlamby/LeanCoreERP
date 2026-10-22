-- 0011 account_role_assignment (07, 6.3): the accounts the core posts to on its own (rounding, FX, retained earnings,
-- intercompany, migration). The roles that depend on item group, warehouse, item or partner belong to
-- account_determination (10, 3.9) and are not part of this table.

create table if not exists core.account_role_assignment (
    tenant_id              uuid not null,
    company_id             uuid not null,
    assignment_id          uuid not null default gen_random_uuid(),
    role_code              core.code not null check (role_code in (
        'ROUNDING', 'TAX_ROUNDING', 'FX_REALISED', 'FX_UNREALISED_GAIN', 'FX_UNREALISED_LOSS', 'RETAINED_EARNINGS',
        'IC_RECEIVABLE', 'IC_PAYABLE', 'VAT_SETTLEMENT', 'MIGRATION_CLEARING', 'HISTORY_RESULT')),
    currency_code          char(3) references platform.ref_currency(currency_code),
    counterpart_company_id uuid,
    vat_registration_id    uuid,      -- no FK: company_vat_registration (08, 4.1) does not exist yet
    account_id             uuid not null,
    effective_from         timestamptz not null,
    primary key (tenant_id, company_id, assignment_id),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, company_id, account_id) references core.gl_account (tenant_id, company_id, account_id),
    foreign key (tenant_id, counterpart_company_id) references core.company (tenant_id, company_id),
    -- 6.3: a currency belongs to the roles that are per currency, a counterpart to the IC roles, a registration to VAT_SETTLEMENT.
    check (currency_code is null or role_code in ('ROUNDING', 'FX_UNREALISED_GAIN', 'FX_UNREALISED_LOSS')),
    check ((counterpart_company_id is not null) = (role_code in ('IC_RECEIVABLE', 'IC_PAYABLE'))),
    check (counterpart_company_id is null or counterpart_company_id <> company_id),
    check (vat_registration_id is null or role_code = 'VAT_SETTLEMENT')
);
-- The unique key of 6.3 with the nullable parts replaced by a fixed marker (ADR-149, D5).
create unique index if not exists account_role_assignment_key on core.account_role_assignment (
    tenant_id, company_id, role_code,
    coalesce(currency_code, '***'),
    coalesce(counterpart_company_id, '00000000-0000-0000-0000-000000000000'::uuid),
    coalesce(vat_registration_id, '00000000-0000-0000-0000-000000000000'::uuid),
    effective_from);
create index if not exists account_role_assignment_account on core.account_role_assignment (tenant_id, company_id, account_id);

-- ---------------------------------------------------------------------------------------------------------
-- Security: forced RLS, read for every command role, insert for the configuration role, never update or delete.
-- A change of a role is a new row with a later effective_from (V table).
-- ---------------------------------------------------------------------------------------------------------
alter table core.account_role_assignment enable row level security;
alter table core.account_role_assignment force row level security;
do $$ begin
    if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = 'account_role_assignment' and policyname = 'tenant_isolation') then
        create policy tenant_isolation on core.account_role_assignment using (tenant_id = core.current_tenant_id())
            with check (tenant_id = core.current_tenant_id());
    end if;
end $$;
revoke all on core.account_role_assignment from public;
grant select on core.account_role_assignment to cmd_read, cmd_config, cmd_period, cmd_post;
grant insert on core.account_role_assignment to cmd_config;

drop trigger if exists account_role_assignment_append_only on core.account_role_assignment;
create trigger account_role_assignment_append_only before update or delete on core.account_role_assignment
    for each row execute function core.reject_change();
drop trigger if exists account_role_assignment_no_truncate on core.account_role_assignment;
create trigger account_role_assignment_no_truncate before truncate on core.account_role_assignment
    for each statement execute function core.reject_change();

-- ---------------------------------------------------------------------------------------------------------
-- The account rules of the role table (07, 6.3) as a backstop. The command checks the same rules first and answers with
-- parameters; this trigger stops every other writer. Error code in the message: CONFIG.ROLE_ACCOUNT_RULE_VIOLATED.
-- Not checked here, because the table does not exist yet: "no mandatory dimension rules" (account_dimension_rule, 6.2).
-- ---------------------------------------------------------------------------------------------------------
create or replace function core.guard_account_role_assignment() returns trigger language plpgsql as $$
declare a core.gl_account%rowtype; pl boolean; bs boolean;
begin
    select * into a from core.gl_account
     where tenant_id = new.tenant_id and company_id = new.company_id and account_id = new.account_id;
    if not found then
        raise exception 'CONFIG.INVALID_REFERENCE: account % of the role % does not exist', new.account_id, new.role_code using errcode = '23503';
    end if;
    pl := new.role_code in ('ROUNDING', 'TAX_ROUNDING', 'FX_REALISED', 'FX_UNREALISED_GAIN', 'FX_UNREALISED_LOSS');
    bs := new.role_code in ('IC_RECEIVABLE', 'IC_PAYABLE', 'VAT_SETTLEMENT', 'MIGRATION_CLEARING', 'HISTORY_RESULT');
    if pl and a.account_type not in ('REVENUE', 'EXPENSE') then
        raise exception 'CONFIG.ROLE_ACCOUNT_RULE_VIOLATED: role % needs a P&L account (ACCOUNT_TYPE)', new.role_code using errcode = '23514';
    end if;
    if bs and a.account_type not in ('ASSET', 'LIABILITY', 'EQUITY') then
        raise exception 'CONFIG.ROLE_ACCOUNT_RULE_VIOLATED: role % needs a balance sheet account (ACCOUNT_TYPE)', new.role_code using errcode = '23514';
    end if;
    if new.role_code = 'RETAINED_EARNINGS' and a.account_type <> 'EQUITY' then
        raise exception 'CONFIG.ROLE_ACCOUNT_RULE_VIOLATED: role RETAINED_EARNINGS needs an EQUITY account (ACCOUNT_TYPE)' using errcode = '23514';
    end if;
    if new.role_code = 'RETAINED_EARNINGS' and a.revalue_flag then
        raise exception 'CONFIG.REVALUE_FLAG_ON_RETAINED_EARNINGS: the account has revalue_flag' using errcode = '23514';
    end if;
    if a.open_item_mode <> 'NONE' then
        raise exception 'CONFIG.ROLE_ACCOUNT_RULE_VIOLATED: role % needs open_item_mode NONE (OPEN_ITEM_MODE)', new.role_code using errcode = '23514';
    end if;
    if exists (select 1 from core.account_role_assignment r
                where r.tenant_id = new.tenant_id and r.company_id = new.company_id and r.account_id = new.account_id
                  and r.role_code <> new.role_code) then
        raise exception 'CONFIG.ROLE_ACCOUNT_RULE_VIOLATED: the account has another role (ONE_ROLE_PER_ACCOUNT)' using errcode = '23514';
    end if;
    if new.currency_code is not null and a.currency_code is not null and a.currency_code <> new.currency_code then
        raise exception 'CONFIG.ROLE_ACCOUNT_RULE_VIOLATED: the account is for % only (CURRENCY)', a.currency_code using errcode = '23514';
    end if;
    if new.currency_code is null and a.currency_code is not null then
        raise exception 'CONFIG.ROLE_ACCOUNT_RULE_VIOLATED: a default row needs an account without currency (CURRENCY)' using errcode = '23514';
    end if;
    if a.tax_account and new.role_code <> 'VAT_SETTLEMENT' then
        raise exception 'CONFIG.ROLE_ACCOUNT_RULE_VIOLATED: a tax account accepts only VAT_SETTLEMENT (TAX_ACCOUNT)' using errcode = '23514';
    end if;
    return new;
end $$;
drop trigger if exists account_role_assignment_guard on core.account_role_assignment;
create trigger account_role_assignment_guard before insert on core.account_role_assignment
    for each row execute function core.guard_account_role_assignment();

-- gl_account: what a role row fixes (07, 6.1 and the role rules F8, 1).
create or replace function core.guard_gl_account() returns trigger language plpgsql as $$
begin
    if new.tax_account is distinct from old.tax_account and not (new.tax_account and not old.tax_account) then
        raise exception 'gl_account: tax_account is never reset' using errcode = '23000';
    end if;
    if (new.account_type, new.open_item_mode, new.currency_code, new.account_no)
       is distinct from (old.account_type, old.open_item_mode, old.currency_code, old.account_no)
       and exists (select 1 from core.journal_line l
                   where l.tenant_id = old.tenant_id and l.company_id = old.company_id and l.account_id = old.account_id) then
        raise exception 'gl_account: account type, open item mode, currency and number are fixed once the account has postings'
            using errcode = '23000';
    end if;
    if (new.account_type, new.open_item_mode) is distinct from (old.account_type, old.open_item_mode)
       and exists (select 1 from core.account_role_assignment r
                   where r.tenant_id = old.tenant_id and r.company_id = old.company_id and r.account_id = old.account_id) then
        raise exception 'CONFIG.REFERENCED: account type and open item mode are fixed while a role refers to the account' using errcode = '23000';
    end if;
    if new.revalue_flag and not old.revalue_flag
       and exists (select 1 from core.account_role_assignment r
                   where r.tenant_id = old.tenant_id and r.company_id = old.company_id and r.account_id = old.account_id
                     and r.role_code = 'RETAINED_EARNINGS') then
        raise exception 'CONFIG.REVALUE_FLAG_ON_RETAINED_EARNINGS: the account has the role RETAINED_EARNINGS' using errcode = '23000';
    end if;
    return new;
end $$;

insert into platform.schema_migration (version, description)
values ('0011', 'account_role_assignment with the account rules of the role table')
on conflict (version) do nothing;
