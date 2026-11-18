# Vejledning: moms i finance-core, fra opsætning til angivelse

Til den, der skal sætte en virksomhed op med dansk moms og køre en momsperiode igennem. Vejledningen følger det, der er bygget (fase V1 til V5 og DK-pakken). Det, der ikke er bygget, står sidst. Detaljerne og de valg, der er truffet i forhold til specerne, står i [07 §13.2k til §13.2p](07-configuration-and-master-data.md) og i [db/README.md](db/README.md).

## 1. Overblik

Flowet er:

1. **Opsæt** tenant, virksomhed og pakke (platform-CLI).
2. **Installér momsindholdet** fra DK-pakken (`package apply-vat`): koder, bokse, regler, fradragsnøgler, afregningsrolle og nummerserie.
3. **Bogfør.** Momskoden bestemmes af reglerne, og momslinjerne følger med dokumentet.
4. **Luk perioden** og forbered angivelsen (`vat-returns:prepare`).
5. **Registrér indberetningen** (`:record-filing`) og **afregn** (`:settle`).

Alt, der skriver, går gennem kommandoer med idempotensnøgle. Platform-CLI'en er operatørens værktøj og kører med migrationsrollen. Den er ikke en del af API'et.

## 2. Forudsætninger

| Hvad | Hvordan |
|---|---|
| Databasen | `FINANCECORE_ADMIN_DB` (migrationsrollen, til CLI) og `FINANCECORE_DB` (kommandorollerne, til API og tests) er sat |
| Skemaet | `db migrate --dry-run`, derefter `db migrate`. Der skal være 28 migrationer (0001 til 0028) |
| CLI'en | `dotnet src/FinanceCore.Platform.Cli/bin/Debug/net9.0/financecore-platform.dll <kommando>` (herefter kun `financecore-platform`) |
| Tenant-formål | DK-pakken er **DRAFT** og kan kun bruges på tenants med formål `TEST` eller `REHEARSAL`. En `PRODUCTION`-tenant afviser den med `PACKAGE.NOT_RELEASED`, indtil pakken er verificeret og RELEASED |

```
financecore-platform db migrate --dry-run
financecore-platform db migrate
```

## 3. Opsæt tenant og virksomhed

```
financecore-platform tenant create --code ACME --name "Acme" --purpose TEST --currencies DKK,EUR
financecore-platform company create --tenant ACME --code DK01 --name "Acme ApS" --country DK --currency DKK --reporting EUR
financecore-platform company setup --tenant ACME --company DK01 --package dk-minimal --year 2026
financecore-platform rate add --tenant ACME --from DKK --to EUR --date 2026-03-15 --rate 0.134
```

`dk-minimal` giver kontoplan, nummerserier (bl.a. `GL-JRN`), grundroller, opbevaringsregler og det åbne regnskabsår. Den har **ingen moms**. Momsen kommer i næste trin. Kurser tilføjes pr. dag, så tilføj de dage, du bogfører på. Virksomheden aktiveres til sidst (afsnit 5).

## 4. Installér DK-pakkens momsindhold

```
financecore-platform package apply-vat --tenant ACME --company DK01 --dir country-packages --package DK \
    --vat-id DK12345678 --filing-frequency QUARTERLY --vat-profile 1 --first-fiscal-year-start 2026-01-01
```

### 4.1 Inputs

| Input | Værdier | Bemærkning |
|---|---|---|
| `--vat-id` | CVR-baseret momsnummer | Påkrævet |
| `--filing-frequency` | MONTHLY, QUARTERLY, HALF_YEARLY | Afhænger af omsætningen. Kernen beregner det ikke |
| `--vat-profile` | 1 til 4 | Forretningstypen i den officielle momskodeliste (se 4.2) |
| `--first-fiscal-year-start` | `yyyy-MM-dd` | Registreringens `valid_from` og satsernes startdato |
| `--ec-list-frequency` | MONTHLY, QUARTERLY | Kun hvis virksomheden indberetter EU-salg |
| `--correction-method` | ADJUST_NEXT (standard), SUPPLEMENTARY, REPLACEMENT | Hvordan en periode rettes efter indberetning (afsnit 8) |
| `--include-special-codes` | flag | Tilføjer de 6 specialkoder (profil 5) |
| `--pro-rata-rate`, `--estimate-rate`, `--sector-rate`, `--area-rate` | procent | Startsats for en fradragsnøgle. Satsen er altid PROVISIONAL |
| `--currency` | standard DKK | Angivelsens valuta |

### 4.2 Vælg profil

| Profil | Forretningstype | Antal koder |
|---|---|---|
| 1 | Handel kun i Danmark | 10 |
| 2 | Danmark, blandet momspligtig og momsfri aktivitet | 17 |
| 3 | Også udenlandsk handel | 31 |
| 4 | Blandet og udenlandsk handel | 49 (55 med specialkoder) |

Profil 2 og 4 bruger delvist fradrag. Angiv en startsats (`--pro-rata-rate`), ellers får du `TAX.DEDUCTION_RATE_NOT_FOUND` ved første bogføring med en kode, der bruger nøglen.

### 4.3 Hvad der installeres

Skatteklasserne GOODS og SERVICES, de 10 returbokse, registreringen, de fire fradragsnøgler (PRO_RATA, ESTIMATE, SECTOR, AREA), kodernes første version med boksmapping, bestemmelsesreglerne med de tilladte alternativer, kontiene 7680, 7700, 7720, 7740 og 7840 (kun hvis de ikke findes), afregningsrollen `VAT_SETTLEMENT` og nummerserien til `VAT_SETTLE` (serien `GL-JRN`, som virksomheden skal have i forvejen).

Udskriften viser pr. objekttype, hvor mange der blev oprettet, og hvor mange der fandtes i forvejen.

### 4.4 Genkørsel og historik

Kørslen er **create-only**: et objekt, der findes, ændres aldrig, hverken ved en ny kørsel eller med andre inputs. Vil du ændre en kode eller regel, gør du det med de almindelige kommandoer (nye versioner). Hver kørsel skrives som en registrering, som kan ses sådan:

```
financecore-platform package list-applied --tenant ACME --company DK01
```

## 5. Aktivér og læg stamdata ind

```
financecore-platform company activate --tenant ACME --company DK01
```

Aktivering kræver, at virksomhedens roller og serier er på plads. Derefter:

| Opgave | Kommando | Tilladelse |
|---|---|---|
| Partner (kunde eller leverandør) | `business-partners:create` | `write.master` |
| Partners momsregistrering i et land | `partner-tax-registrations:create` | `write.master` |
| Ny sats for en fradragsnøgle | `vat-deduction-rates:create-version` | `write.config` |
| Ekstra kode, regel eller version | `tax-codes:create`, `:create-version`, `tax-determination-rules:create`, `:create-version` | `write.config` |

**Partnerens momsregistrering** afgør, om en EU-kunde er `REGISTERED` i bestemmelsesreglerne, og dens `vat_id` bliver til linjen i EC-listen. Opret den før fakturaen, for status læses på skattedatoen.

## 6. Bogfør med moms

Bogfør som normalt med `journal-documents:post`. En linje beskattes, hvis den angiver en momskode, eller hvis dens konto har regelen `MANDATORY`. Resten sker automatisk:

- **Kode.** Reglerne finder koden ud fra transaktionstype (salg for AR, køb for AP), oprindelses- og bestemmelsesland, partnerens status, skatteklasse og konto. Lavest prioritetsværdi vinder, derefter den mest specifikke regel. Angiver du en anden kode end den fundne, skal den være et alternativ i reglen, og du får advarslen `TAX.CODE_DIFFERS_FROM_DETERMINED`.
- **Land.** AR- og AP-dokumenter med partner bruger partnerens land. Andre dokumenter, for eksempel en kassekladde, skal selv angive `origin_country` og `destination_country`, ellers kommer `TAX.ORIGIN_DESTINATION_REQUIRED`.
- **Momslinjer.** Momslinjen bogføres på kodens momskonto. Afvigelser mellem den angivne og den beregnede moms inden for tolerancen bliver en afrundingslinje.
- **Reverse charge.** Der bogføres to momslinjer (udgående og indgående side), så dokumentet balancerer.
- **Delvist fradrag.** Indgående moms deles i den fradragsberettigede del (momslinjen) og resten (omkostning på grundlinjens konto). Andelen er procent × nøglesats på skattedatoen. En kode med loft kræver `deductible_tax_amount_tc` på linjen.
- **Skattedato.** Linjens `tax_date`, ellers dokumentets, ellers dokumentdatoen. Datoen afgør, hvilken momsperiode linjen hører til.
- **Momskonti** tager kun momslinjer og afregningslinjer. En direkte postering giver `ACCOUNT_RULE.TAX_ACCOUNT_DIRECT_POSTING`.

Et bogført dokument rettes ikke. Det tilbagefører man (`journal-documents:reverse`), og spejlet kopierer momsrækkerne med modsat fortegn.

## 7. Dokumentation (evidence) på momslinjer

Visse koder kræver dokumentation, for eksempel en toldangivelse ved import. Dokumentationen tilføjes efter bogføring, i alle periodestatusser, også hårdt lukkede:

```
journal-documents:change-open-fields
  entity_type = TAX_EVIDENCE, line_no = momslinjens linjenummer, sub_key = evidence_no (1, 2, ...)
  fields: evidence_type, reference, (evidence_date, note)   expected_old_value = null
```

En ny række skal have både `evidence_type` og `reference` og det næste nummer. En eksisterende række ændres aldrig. En rettelse er den næste række. Mangler en linje den dokumentation, kodens version kræver, får angivelsen advarslen `VAT.EVIDENCE_MISSING` pr. linje.

## 8. Momsangivelsen

### 8.1 Forbered

Alle regnskabsperioder i momsperioden skal mindst være `SOFT_CLOSED`. Momsperioden er 1, 3 eller 6 regnskabsperioder efter filing-frekvensen, talt fra periode 1.

| Kommando | Tilladelse | Formål |
|---|---|---|
| `vat-returns:preview` | `vat.prepare` | Samme som prepare, men intet gemmes. Brug den til at se tallene først |
| `vat-returns:prepare` | `vat.prepare` | Fryser snapshottet: rubrikrækker, de momslinjer der indgår, en digest og en node i `VAT_RETURN`-kæden |
| `vat-returns:get`, `:list` | `vat.read` | Læs en angivelse eller list dem efter registrering, type og år |

Forrige momsperiode skal have en original, før den næste kan forberedes (`VAT.PREVIOUS_PERIOD_NOT_PREPARED`). Linjer med skattedato før periodens start kommer med som **sene** linjer i den næste angivelse.

### 8.2 Ret en periode, der allerede er forberedt

| Korrektionsmetode | Ny forberedelse af samme periode |
|---|---|
| ADJUST_NEXT | Afvises (`VAT.ALREADY_PREPARED`). Linjerne kommer som sene linjer i næste angivelse |
| SUPPLEMENTARY | En supplerende angivelse med kun de linjer, der ikke er indberettet endnu |
| REPLACEMENT | En erstatning for hele perioden. Den erstattede angivelse markeres som erstattet |

### 8.3 Indberet og afregn

1. `vat-returns:record-filing` (`vat.file`) registrerer kvitteringen fra indberetningen med reference og tidspunkt. Det kan kun gøres én gang, og intet kan fortryde det. Selve indberetningen sker uden for kernen.
2. `vat-returns:settle` (`vat.settle`) bogfører ét `VAT_SETTLE`-dokument: hver momskonto nulstilles, og nettobeløbet står på `VAT_SETTLEMENT`-kontoen (7840, skyldig moms). Uden angivet dato bruges den første bogføringsklare dato fra periodens slutning.
3. En erstatning afregner kun forskellen til den seneste afregnede angivelse. En angivelse uden noget at afregne giver `VAT.NOTHING_TO_SETTLE`.
4. `vat-returns:reverse-settlement` (`vat.settle`) tilbagefører afregningen. Det kan kun ske **før** filing, med årsagskode `CORRECTION` eller `ERROR`, og afregningen kan derefter laves igen. `journal-documents:reverse` afviser et `VAT_SETTLE`-dokument.

## 9. EU-salg og OSS

| Angivelsestype | Registrering | Indhold |
|---|---|---|
| `EC_LIST` | Standardregistrering med `ec_list_frequency` | Én række pr. partner, land og kategori med grundbeløbet og partnerens momsnummer. Perioderne følger EC-listens frekvens. Afregnes ikke |
| `OSS`, `IOSS` | Registrering med skema OSS_UNION, OSS_NON_UNION eller IOSS | Én række pr. forbrugsland, sats og leveringsart. Afregnes som en standardangivelse |

Til EC-listen skal hver momslinje høre til et dokument med præcis én partner (`VAT.PARTNER_MISSING`). Til OSS skal kodens version angive `supply_kind` (`VAT.SUPPLY_KIND_MISSING`). DK-pakken indeholder ikke OSS-koder, så de skal oprettes manuelt.

## 10. Kontrol

`chains:verify` (`chain.verify`) genberegner hash og tjekker kæden. Kør den for `LEDGER` og `VAT_RETURN` efter en afregning. Test uden for kernen: `dotnet test` for hvert af de tre testprojekter, og `db/tests/001_protection.sql` i Supabase SQL Editor (256 checks, kræver at referencedata for EU-medlemskab er indlæst).

## 11. Fejlfinding

| Kode | Typisk årsag | Løsning |
|---|---|---|
| `PACKAGE.NOT_RELEASED` | PRODUCTION-tenant og DRAFT-pakke | Brug en TEST- eller REHEARSAL-tenant, eller få pakken verificeret |
| `PACKAGE.REFERENCE_UNRESOLVED` | Virksomheden mangler serien `GL-JRN` | Kør `company setup` med `dk-minimal` først |
| `TAX.NO_RULE_MATCHED` | Ingen regel passer, for eksempel salg til EU-forbruger | Opret en regel eller angiv koden og et alternativ |
| `TAX.DEDUCTION_RATE_NOT_FOUND` | Koden bruger en nøgle uden sats | `vat-deduction-rates:create-version` |
| `SERIES.NO_ASSIGNMENT` ved afregning | Serien til `VAT_SETTLE` mangler | `package apply-vat` tildeler den |
| `CONFIG.ROLE_NOT_ASSIGNED` ved afregning | `VAT_SETTLEMENT` mangler på registreringen | `package apply-vat`, eller `account-role-assignments:create` |
| `VAT.PERIOD_NOT_COMPLETE` | En regnskabsperiode i momsperioden er åben | Soft-luk perioderne |
| `VAT.SETTLEMENT_DATE_NOT_POSTABLE` | Datoen ligger i en lukket periode | Angiv en anden dato |
| `VAT.ALREADY_FILED` | Reverse af en afregning efter filing | Ret med korrektionsmetoden og en ny afregning |
| `LIMIT.PARTIAL_DEDUCTION_NOT_BUILT` | Delvist fradrag på en konto med åbne poster | Bogfør omkostningen på en konto uden åbne poster |
| `LIMIT.VAT_RATE_TYPE_NOT_BUILT` | Registrering med momskurs eller en returvaluta, der hverken er lokal eller reporting | Brug BOOKING og lokal valuta |

## 12. Ikke bygget

- `vat-positions:get` (løbende momsposition) og momsoversigten fra en migration (`legacy_tax_summary`).
- Året-afslutningens regulering af en foreløbig fradragssats, investeringsgoder og afregningsmetoder med betalingstidspunkt (kontantmoms).
- Signeret pakkearkiv, VALIDATE-tilstand og `config-packages:diff`. Resten af DK-pakkens indhold (hele standardkontoplanen, betalingsbetingelser, indstillinger) installeres ikke af `apply-vat`.
- DK-pakken er DRAFT. Intet i den er verificeret, og kategorien AE på reverse charge-kodernes EN 16931-data (F-DK-25) er et valg, som den juridiske reviewer skal bekræfte.
