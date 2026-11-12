# Plan fremadrettet: rækkefølgen af det, der mangler

Oprettet 2026-11-12, efter at momsområdet (V1–V5) og DK-pakkens momsindhold er bygget og verificeret på Supabase (28 migrationer, 256 SQL-checks). Planen er en anbefaling, og den kan ændres, når vi kender første kundes behov (se "Det, der kan vende rækkefølgen").

## Udgangspunkt

| Område | Status |
|---|---|
| Hovedbog, clearing, perioder, partner, retention, API-skal | Bygget (første slice) |
| Moms/VAT (08, 19), skatte- og fradragstabeller | Bygget (V1–V5). Mangler `vat-positions:get` og `legacy_tax_summary` |
| Dimensioner (typede og frie) | Bygget (migration 0022) |
| Landepakkeformat (18) | Signering bygget (F-IMPL-43: signeret arkiv, tillidssæt, verifikation, PRODUCTION kun signeret). Delvist: DK-pakkens momsindhold installeres med `package apply-vat`, og kørslerne registreres. VALIDATE-tilstand og `config-packages:diff` mangler |
| Revaluering og årsafslutning (05) | Bygget (0029, F-IMPL-40): revaluering med rerun, årsafslutning og genåbning, forældelsestjek ved hard-close. Mangler: SQL-checks, kørsel mod Supabase, historik-variant (migration), tidsstempeljob for anchors |
| Anchors (`chain_anchor`) og RFC 3161-tidsstempel | Ikke bygget |
| Læse-API'er og søgning (09) | Ikke bygget, bortset fra `chains:verify` og `vat-returns:get`/`:list` |
| Outbox-jobs, eksport, anonymisering, tenant-sletning | Ikke bygget (hændelser skrives, men ingen jobs) |
| Migration (13) | Første slice bygget (F-IMPL-44): åbningsbalancer og åbne poster fra åbning til seal. Mangler historik, rollback, lager, ordrer, legacy_tax_summary, legacy_id_map, four-eyes |
| Lager (10), ordrer (11) | Kun spec |
| Fakturering og betaling (22), konsolidering (23) | Kun spec |
| API-hærdning (mTLS, rate limits, pagination) | Ikke bygget |

## Rækkefølge

| # | Fase | Indhold | Hvorfor her |
|---|---|---|---|
| 1 | **05 Årsafslutning og revaluering** | Revaluering, `YEAR_END`, genåbning, auto-reversering. Med anchors (`chain_anchor`) og F-IMPL-13 (låsefunktion for kædehoveder), fordi de hører til hard-close | Uden årsafslutning kan ingen virksomhed gå i drift. Afgrænset og afhænger kun af det, der er bygget. Lager (10) og konsolidering (23) bruger den |
| 2 | **09 Læse-API'er + outbox-jobs** | Minimum af læsninger (saldi, åbne poster, kontoudtog, dokumenter) og outbox-udsendelse. Anonymisering og tenant-sletning (GDPR) hører med | Uden læsninger kan ingen se resultatet. Apps får data via outbox, og 10, 11 og 22 sender alle hændelser |
| 3 | **18 Signering** | Signeret pakkearkiv (ADR-217), verifikation ved apply, VALIDATE-tilstand, `config-packages:diff`, evt. resten af DK-pakkens indhold og flytning af `dk-minimal` ud af koden | Hård grænse for en PRODUCTION-tenant (F-IMPL-30) |
| 4 | **13 Migration** | Åbningsbalancer, åbne poster, historiske opsummeringer, `legacy_tax_summary` | Uden den kommer en rigtig kundes data ikke ind |
| 5 | **10 Lager** | Lagerdokumenter, lagerværdi, landed cost, GR/IR | Bredde. Bygger på 05 |
| 6 | **11 Ordrer** | Ordrer, leverancer, fakturagrundlag | Bygger på 10 |
| 7 | **22 Fakturering og betaling** | Fakturering, betalingsfiler, afstemning | Bygger på 11 og outbox (fase 2) |
| 8 | **23 Konsolidering** | Konsolideringsdata, intercompany | Bygger på 05 |

Efter eller sideløbende: API-hærdning (mTLS, rate limits, pagination), `vat-positions:get`, årsregulering af foreløbige fradragssatser, investeringsgoder og kontantmoms.

**Mål for første kunde:** efter fase 4 kan en kunde gå live med finans og moms. Fase 5–8 er bredde.

## Det, der kan vende rækkefølgen

- Har første kunde primært behov for **ordre-til-kontant eller lager**, rykker 10, 11 og 22 frem. Fase 1 (05) skal stadig først, og fase 3–4 (18, 13) skal stadig være klar, før kunden går live.
- Hvis kunden skal bruge **flere selskaber med koncernafregning** tidligt, rykker 23 frem lige efter 05.
- Hvis **GDPR-sletning** er et krav fra dag ét, rykkes anonymisering og tenant-sletning frem foran resten af fase 2.

## Arbejdsform for hver fase

Samme forløb som for momsen:

1. Læs spec og eksisterende kode. Beslut scope, og skriv hullerne i specen som en F-IMPL-finding (PROPOSED) i `db/README.md` og en §13.2-paragraf i `07-configuration-and-master-data.md`.
2. Migration, kommandoer, API-registrering og tests (Core, Data, Api, SQL).
3. Hele testsuiten og `UPDATE_OPENAPI=1` for Api.
4. Commit og push, når det bliver bedt om. Per kører derefter `db migrate --dry-run`, `db migrate` og `db/tests/001_protection.sql` mod Supabase.

## Åbne beslutninger, der påvirker planen

| Ref | Beslutning | Påvirker |
|---|---|---|
| F-IMPL-13 | Hard-close låser kædehoveder gennem en skrivebeskyttet låsefunktion (besluttet 2026-11-11). Implementeres, når `chain_anchor` bygges | Fase 1 |
| F-IMPL-30 | `dk-minimal` bliver i koden indtil videre, og signering er ikke bygget (besluttet 2026-11-11). Ingen PRODUCTION-blokering | Fase 3 |
| DK juridisk reviewer | Ikke navngivet. DK-pakken er DRAFT og kan ikke blive RELEASED uden | Fase 3 |
| EU-medlemskab i `ref_country_eu_membership` | UNVERIFIED. Skal tjekkes mod EU's officielle tidende, før den bruges i en rigtig tenant | Før første kunde |
| Første kundes behov | Afgør, om bredden (10, 11, 22) kommer før eller efter 18 og 13 | Hele rækkefølgen |
