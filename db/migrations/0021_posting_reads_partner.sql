-- 0021 The posting role reads the partner for the legal snapshot of a document (02, 4, 06, 5.2): the name, address, country, electronic address and
-- registration id of business_partner_pii. Column grants: the contact data (email, phone, contact person) stays out of reach of the posting role.

-- 0006 and 0015 grant select on all tables of the schema to these roles. They did not reach this table on a fresh database (it came later in 0017),
-- but they do when the schema file is applied again over an existing database, and a table grant would make the column grant below meaningless.
-- Found on Supabase (F-IMPL-32): the posting role could read the contact data.
revoke all on core.business_partner_pii from cmd_period, cmd_post, cmd_clearing;

grant select (tenant_id, partner_id, legal_name, address_line_1, address_line_2, postal_code, city, country_code,
              electronic_address, electronic_address_scheme, legal_registration_id)
    on core.business_partner_pii to cmd_post;

insert into platform.schema_migration (version, description)
values ('0021', 'the posting role reads the partner for the legal snapshot')
on conflict (version) do nothing;
