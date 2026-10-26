-- 0021 The posting role reads the partner for the legal snapshot of a document (02, 4, 06, 5.2): the name, address, country, electronic address and
-- registration id of business_partner_pii. Column grants: the contact data (email, phone, contact person) stays out of reach of the posting role.

grant select (tenant_id, partner_id, legal_name, address_line_1, address_line_2, postal_code, city, country_code,
              electronic_address, electronic_address_scheme, legal_registration_id)
    on core.business_partner_pii to cmd_post;

insert into platform.schema_migration (version, description)
values ('0021', 'the posting role reads the partner for the legal snapshot')
on conflict (version) do nothing;
