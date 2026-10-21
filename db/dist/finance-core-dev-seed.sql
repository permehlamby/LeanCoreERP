-- Development seed: one TEST tenant with a Danish company, reference data and a small chart of accounts.
-- Run after 0001 to 0006. Safe to run again. It does not open a fiscal year: years, periods and their
-- hash-chained period events are created by the command `fiscal-years:open`, never by SQL.
-- Row-level security is forced also for the migration role, so the tenant is set for the session first.

-- Reference data (platform level, no tenant)
insert into platform.ref_currency (currency_code, numeric_code, name, minor_units) values
    ('EUR', '978', 'Euro', 2), ('DKK', '208', 'Danish krone', 2), ('SEK', '752', 'Swedish krona', 2),
    ('NOK', '578', 'Norwegian krone', 2), ('USD', '840', 'US dollar', 2), ('GBP', '826', 'Pound sterling', 2)
on conflict (currency_code) do nothing;

insert into platform.ref_country (country_code, alpha3, name) values
    ('DK', 'DNK', 'Denmark'), ('DE', 'DEU', 'Germany'), ('SE', 'SWE', 'Sweden'), ('NO', 'NOR', 'Norway'),
    ('FR', 'FRA', 'France'), ('NL', 'NLD', 'Netherlands')
on conflict (country_code) do nothing;

insert into platform.tenant (tenant_id, tenant_code, name, status, purpose)
values ('11111111-1111-4111-8111-111111111111', 'DEV', 'Development tenant', 'ACTIVE', 'TEST')
on conflict (tenant_id) do nothing;

-- Tenant scope for the rest of this session (ADR-209)
select set_config('app.tenant_id', '11111111-1111-4111-8111-111111111111', false);

insert into core.tenant_setting (tenant_id) values ('11111111-1111-4111-8111-111111111111')
on conflict (tenant_id) do nothing;
insert into core.outbox_head (tenant_id) values ('11111111-1111-4111-8111-111111111111')
on conflict (tenant_id) do nothing;
insert into core.tenant_currency (tenant_id, currency_code) values
    ('11111111-1111-4111-8111-111111111111', 'EUR'), ('11111111-1111-4111-8111-111111111111', 'DKK'),
    ('11111111-1111-4111-8111-111111111111', 'USD')
on conflict do nothing;

insert into core.fiscal_calendar (tenant_id, calendar_id, calendar_code, description, first_month, special_period_count, year_label_rule)
values ('11111111-1111-4111-8111-111111111111', '33333333-3333-4333-8333-333333333301', 'CAL-YEAR',
        'Calendar year', 1, 1, 'START')
on conflict do nothing;

insert into core.company_group (tenant_id, group_id, group_code, name, group_currency)
values ('11111111-1111-4111-8111-111111111111', '33333333-3333-4333-8333-333333333302', 'DEVGRP', 'Development group', 'EUR')
on conflict do nothing;

insert into core.company (tenant_id, company_id, company_code, legal_name, country_code, local_currency, reporting_currency,
                          fiscal_calendar_id, status)
values ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', 'DK01', 'Dev A/S',
        'DK', 'DKK', 'EUR', '33333333-3333-4333-8333-333333333301', 'DRAFT')
on conflict do nothing;

insert into core.ledger (tenant_id, company_id, ledger_id, ledger_code)
values ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201',
        '44444444-4444-4444-8444-444444444401', 'LEDGER1')
on conflict do nothing;

insert into core.group_membership (tenant_id, company_id, valid_from, group_id)
values ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', date '2026-01-01',
        '33333333-3333-4333-8333-333333333302')
on conflict do nothing;

insert into core.company_setting_version (tenant_id, company_id, effective_from)
values ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', timestamptz '2026-01-01 00:00:00+00')
on conflict do nothing;

-- A minimal chart of accounts without open item accounts (the first slice posts to plain accounts only)
insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, open_item_mode, tax_code_rule)
values
 ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', '55555555-5555-4555-8555-555555555501', '1000', 'Bank', 'ASSET', 'NONE', 'FORBIDDEN'),
 ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', '55555555-5555-4555-8555-555555555502', '3000', 'Revenue', 'REVENUE', 'NONE', 'FORBIDDEN'),
 ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', '55555555-5555-4555-8555-555555555503', '4000', 'Operating expenses', 'EXPENSE', 'NONE', 'FORBIDDEN'),
 ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', '55555555-5555-4555-8555-555555555504', '8000', 'Equity', 'EQUITY', 'NONE', 'FORBIDDEN')
on conflict do nothing;

insert into core.number_series (tenant_id, company_id, series_id, series_code, description, prefix, include_fiscal_year, separator, number_length, is_legal)
values ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', '66666666-6666-4666-8666-666666666601',
        'GL-JRN', 'General journal', 'GL', true, '-', 6, true)
on conflict do nothing;

-- Series assignment: effective_from must equal the start date of an opened fiscal year (04, 5.2, A3). The command
-- that creates assignments checks this, the table does not.
insert into core.number_series_assignment (tenant_id, company_id, assignment_id, document_type, series_id, effective_from, created_by)
values ('11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', '77777777-7777-4777-8777-777777777701',
        'JOURNAL', '66666666-6666-4666-8666-666666666601', date '2026-01-01', 'dev_seed')
on conflict do nothing;

-- Chain heads start at sequence 0 with the genesis value of 02, 9.3a, computed from the same text as the C# library.
insert into core.chain_head (tenant_id, company_id, chain_type, scope_key, last_seq, last_hash, hash_version)
select '11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222201', c.chain_type, c.scope_key, 0,
       sha256(convert_to('genesis|11111111-1111-4111-8111-111111111111|22222222-2222-4222-8222-222222222201|'
                         || c.chain_type || '|' || c.scope_key, 'UTF8')), 2
from (values ('LEDGER', '44444444-4444-4444-8444-444444444401'), ('PERIOD_EVENT', '')) as c(chain_type, scope_key)
on conflict do nothing;

insert into platform.schema_migration (version, description)
values ('seed-dev', 'development seed')
on conflict (version) do nothing;
