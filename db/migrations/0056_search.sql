-- 0056 search (09, 2.4.1, 5.3, 20 section 8, ADR-214): the logical operator `contains`, a literal substring that is case- and accent-insensitive.
-- Both sides are normalised by one function, core.search_norm: Unicode NFKD, combining marks removed, lower case, runs of white space collapsed to one blank, trimmed.
-- The pattern of a search is built by core.search_pattern: the normalised value with the characters of LIKE (backslash, percent, underscore) escaped AFTER the
-- normalisation, because NFKD can produce a percent sign (U+FF05), and wrapped in percent signs. So the value is never a pattern (2.4.1).
-- Deviation from 20, section 8: the spec has the core compute a normalised column in application code. Here the database computes the same normalisation in an immutable function
-- and the reference platform indexes the expression with a trigram index: no extra column to keep in step with the L2 fields, and nothing to clear at anonymisation, because the index follows the
-- source column (the search column of a personal-data field is personal data, 2.4.1). The indexes are created only when pg_trgm is available; without them the search is correct, but scans.

create or replace function core.search_norm(v text) returns text
language sql immutable strict parallel safe as $$
    select btrim(regexp_replace(lower(regexp_replace(normalize(v, NFKD), '[̀-ͯ]', '', 'g')), '\s+', ' ', 'g'))
$$;

create or replace function core.search_pattern(v text) returns text
language sql immutable strict parallel safe as $$
    select '%' || replace(replace(replace(core.search_norm(v), E'\\', E'\\\\'), '%', E'\\%'), '_', E'\\_') || '%'
$$;

grant execute on function core.search_norm(text), core.search_pattern(text) to public;

do $$
declare
    ns text;
    spec text[];
    specs text[][] := array[
        array['jd_search_header', 'journal_document', 'header_text'],
        array['jd_search_external', 'journal_document', 'external_reference'],
        array['jd_search_source_ref', 'journal_document', 'source_document_ref'],
        array['bpp_search_legal_name', 'business_partner_pii', 'legal_name'],
        array['bpp_search_trade_name', 'business_partner_pii', 'trade_name'],
        array['bpp_search_address_1', 'business_partner_pii', 'address_line_1'],
        array['bpp_search_address_2', 'business_partner_pii', 'address_line_2'],
        array['bpp_search_postal_code', 'business_partner_pii', 'postal_code'],
        array['bpp_search_city', 'business_partner_pii', 'city'],
        array['bpp_search_email', 'business_partner_pii', 'email']];
begin
    begin
        create extension if not exists pg_trgm;
    exception when others then
        raise notice 'pg_trgm is not available: the search indexes of 0056 are not created';
    end;
    select n.nspname into ns from pg_extension e join pg_namespace n on n.oid = e.extnamespace where e.extname = 'pg_trgm';
    if ns is null then return; end if;
    foreach spec slice 1 in array specs loop
        execute format('create index if not exists %I on core.%I using gin (core.search_norm(%I) %I.gin_trgm_ops)', spec[1], spec[2], spec[3], ns);
    end loop;
end $$;

insert into platform.schema_migration (version, description)
values ('0056', 'search: core.search_norm and core.search_pattern, trigram expression indexes for contains')
on conflict (version) do nothing;
