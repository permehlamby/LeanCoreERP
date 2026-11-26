# Plan fremadrettet: rækkefølgen af det, der mangler

Oprettet 2026-11-12, efter at momsområdet (V1–V5) og DK-pakkens momsindhold er bygget og verificeret på Supabase (28 migrationer, 256 SQL-checks). Planen er en anbefaling, og den kan ændres, når vi kender første kundes behov (se "Det, der kan vende rækkefølgen").

## Udgangspunkt

| Område | Status |
|---|---|
| Hovedbog, clearing, perioder, partner, retention, API-skal | Bygget (første slice) |
| Moms/VAT (08, 19), skatte- og fradragstabeller | Bygget (V1–V5) og `vat-positions:get` (F-IMPL-87). Mangler `legacy_tax_summary` |
| Dimensioner (typede og frie) | Bygget (migration 0022) |
| Landepakkeformat (18) | Signering bygget (F-IMPL-43: signeret arkiv, tillidssæt, verifikation, PRODUCTION kun signeret). Delvist: DK-pakkens momsindhold installeres med `package apply-vat`, og kørslerne registreres. VALIDATE-tilstand og `config-packages:diff` mangler |
| Revaluering og årsafslutning (05) | Bygget (0029, F-IMPL-40): revaluering med rerun, årsafslutning og genåbning, forældelsestjek ved hard-close. Mangler: SQL-checks, kørsel mod Supabase, historik-variant (migration), tidsstempeljob for anchors |
| Anchors (`chain_anchor`) og RFC 3161-tidsstempel | Ikke bygget |
| Læse-API'er og søgning (09) | To slices bygget (F-IMPL-41, F-IMPL-45): dokumenter, saldi, kontoudtog, åbne poster, aging, dokumentkontekst, kædestatus, revisionshændelser, partnerlæsning med PII-log (migration 0033). Mangler søgning (`contains`), flere selskaber, extensions, læselogtømning og `access.summary` |
| Outbox-jobs, eksport, anonymisering, tenant-sletning | Ikke bygget (hændelser skrives, men ingen jobs) |
| Migration (13) | To slices bygget (F-IMPL-44, F-IMPL-46): åbningsbalancer og åbne poster fra åbning til seal, rollback, four-eyes (commit, seal, approve) og læsninger. Mangler historik (HISTORY-chunk, historieår, CLOSING), lager, ordrer, legacy_tax_summary, legacy_id_map, VALIDATE-tilstand |
| Lager (10) | Tre slices bygget (F-IMPL-47, 48, 50): stamdata, kontobestemmelse og lagerbogen med modtagelse, udlevering og regulering til glidende gennemsnit, bogføring og STOCK-kæde. Mangler FIFO, sporing, overførsler, returvarer, landed cost, reversering, reservationer og negativ beholdning |
| Ordrer (11) | Seks slices bygget (F-IMPL-49, 51 til 55): salgs- og indkøbsordrer, levering/modtagelse mod ordrerne på lagerbogen med FULFILMENT-kæde og mængdelåse, tilbageførsel af lagerbilag, leveringer og modtagelser, returvarer med tilbageførsel, og salgsfaktura fra ordrer med tilbageførsel (sales-invoices:create og :reverse, begge modes; slice 56 fandt og lukkede et hul: clearing-rollen kunne ikke tilbageføre momsbelagte bilag). Salgskreditnotaer er bygget (F-IMPL-57: returkredit og beløbskredit, tilbageførsel, rækkefølgen kreditnota, retur, faktura). Leverandørfaktura første slice er bygget (F-IMPL-58: tre-vejs matchning af lagervarer mod modtagelser med GR/IR-clearing, tjenester mod bestilt mængde). Prisdifference med COST_ADJUST (F-IMPL-59) og tilbageførsel af leverandørfakturaer (F-IMPL-60) er bygget. Verifikation af STOCK-kæden (F-IMPL-61) og indkøbskreditnota som beløbskredit (F-IMPL-62) er bygget. Tilbageførsel af indkøbskreditnota (F-IMPL-63) og afskrivning af GR/IR-rester med tilbageførsel (F-IMPL-64) og direkte modtagelser (D3, F-IMPL-66) med kreditnota (F-IMPL-67) og faktura før modtagelse (to-vejs lagerlinjer, F-IMPL-68) og gebyrer på leverandørfakturaen (F-IMPL-69) og returkredit på indkøbskreditnoter (F-IMPL-70) og læsningen gr-ir:query (F-IMPL-71) og læsningerne open-orders:query og uninvoiced:query (F-IMPL-84) og get/list af leveringer, modtagelser, returvarer og fakturaer (F-IMPL-85) og verifikation af FULFILMENT-kæden (F-IMPL-86) er bygget. Mangler returkredit, to-vejs lagerlinjer, indkøbskreditnotaer, gebyrer, direkte modtagelser, valuta og afskrivning af GR/IR og matchning, GR/IR-afskrivning, adressesnapshots, reservationer, dropship og koncernordrer |
| Fakturering og betaling (22), konsolidering (23) | 22: første slice bygget (F-IMPL-72, migration 0049: `invoices:record-issue`, issue-evidens for ordrebyggede og direkte fakturaer). Betaling og clearing bruger de eksisterende kommandoer (`PAYMENT`, `clearings:post`). Mangler: `invoice-issue-records` og `issue_state`-filter, `einvoice-datasets:get`, `einvoice_rule_set`, bankkontokolonner (ADR-223), FT-flowtests. 23: første slice bygget (F-IMPL-73, migration 0050: `company-consolidation-settings:create-version`, ejerskab og metode per selskab). Anden slice bygget (F-IMPL-74: `exchange-rates:mark-used`, brugslås for kurser af alle typer). Tredje slice (F-IMPL-75, migration 0052: `group-chart-accounts` med `translation_rate_basis`). Fjerde slice (F-IMPL-76, migration 0053: `account-group-mappings` med reglen `CONFIG.GROUP_ACCOUNT_OTHER_GROUP`). Femte slice (F-IMPL-77: de tre parathedsadvarsler og `company-consolidation-settings:list`). Sjette slice (F-IMPL-78: gruppenøgler i `balances:query`, ét selskab). Mangler: kursliste med `rate_type`-filter, `ic-deliveries`/`ic-invoices`, `group_id` i `balances:query`. `ic-reconciliations:query` bygget (F-IMPL-81). IC: `intercompany-documents:post` og `:reverse` bygget (F-IMPL-79, F-IMPL-80, markør på basislinjer), `ic-orders:create` (F-IMPL-82, migration 0054), `ic-invoices:create` for servicelinjer (F-IMPL-83) |
| API-hærdning (mTLS, rate limits, pagination) | Første slice bygget (F-IMPL-65): rate limit pr. tenant med 429 og `Retry-After`, valgfri mTLS med certifikatbundne tokens (RFC 8705), signerede cursors, sikkerhedsheadere. Pagination (keyset, limit 100/500) fandtes allerede i læsningerne. Mangler batchgrænse (ingen batchkommando endnu), delt rate limit mellem instanser, rotation af cursor-nøgle, gateway |

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
