# AP-009 – Filter-DSL, Sortierung, Cursor-Paginierung, Abfrage-Endpunkt

| Feld | Wert |
|---|---|
| Status | in Arbeit |
| Phase | 1 |
| Implementierer | implementer-opus |
| Referenzbeispiel | AP-007: `ListRecords` (Cursor nach `(CreatedAt, Id)`), `RecordDto`-Mapper; AP-008: `ViewDefinitionValidator` (strukturelle Filter-Validierung, Records für Filter/Sorts); AP-006: `ISchemaProvider`, Property-Typen |
| Abhängig von | AP-007, AP-008 |
| Betroffene Pfade | `src/Agcowork.Application/Query/`, `src/Agcowork.Infrastructure/Query/` (SQL-Übersetzung), `src/Agcowork.Api/Endpoints/Records/` (Query-Endpunkt), `src/Agcowork.Application/Views/` (nur: View ausführen), `tests/`, `openapi/v1.json`, `schema.d.ts` (neu erzeugen) |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §3 Filter-DSL (Syntax, Operatoren, Cursor, GIN), §4.2 (`records_query`), §4.3 (`POST /collections/{id}/query`), §5 (JSONB-Index) |

## Ziel

Nach diesem Paket können Agenten und UI Records einer Collection mit der Filter-DSL aus
Architektur §3 abfragen: beliebig verschachtelte `and`/`or`, typabhängige Operatoren,
Sortierung nach Properties, Cursor-Paginierung, Feldauswahl. Views lassen sich direkt
ausführen (Filter + Sorts aus dem View). Die Übersetzung nach SQL nutzt den GIN-Index auf
`properties`.

## Scope

**Drin:**

*Application/Query*
- `QueryDefinition` (Records): `Filter?` (Baum aus `And`, `Or`, `Not?` – `not` ist neu, erlaubt), `Condition { Prop, Op, Value }`, `Sorts[]`, `Limit` (Standard 50, max. 500), `Cursor?`, `Fields?` (Property-Keys; `title`, `body` sind Pseudo-Felder; `body` nur auf Anfrage), `IncludeArchived`.
- `QueryParser`: JSON → `QueryDefinition`; Fehler strukturiert (`unknown_operator` mit `allowed`, `unknown_property` mit `allowed`, `operator_not_applicable` mit `allowed` je Typ, `invalid_value` mit erwartetem Typ, `invalid_cursor`).
- Operator-Matrix nach Typ (verbindlich, im Code als Tabelle):
  - `text`/`markdown`/`url`/`email`: eq, neq, contains (case-insensitiv), starts_with, in, nin, is_empty, is_not_empty
  - `number`: eq, neq, gt, gte, lt, lte, in, nin, is_empty, is_not_empty
  - `checkbox`: eq, neq, is_empty
  - `select`/`status`: eq, neq, in, nin, is_empty, is_not_empty
  - `multi_select`: contains (ein Wert), in (Schnittmenge nicht leer), nin, is_empty, is_not_empty
  - `date`/`datetime`: eq, neq, gt, gte, lt, lte, is_empty, is_not_empty (Werte ISO 8601; relative Werte wie `today` **nicht** in diesem Paket)
  - `relation`: contains (Record-Id), in, nin, is_empty, is_not_empty
  - `actor`: eq, neq, in, nin, is_empty, is_not_empty (Wert `actor:<id>` oder Guid)
  - Pseudo-Felder: `title` wie text; `created_at`/`updated_at` wie datetime; `created_by`/`updated_by` wie actor. **Berechnete Properties** (Schema-Typen `created_at` usw. unter beliebigem Key) werden beim Filtern und Sortieren auf die Spalten abgebildet, nie auf `properties->>'key'`.
- Sortierung: `[ { prop, dir } ]`, Pseudo-Felder erlaubt; immer stabil durch Tie-Breaker `(created_at, id)`; Sortierung über JSONB-Werte typgerecht (number als numeric, date als timestamptz, text als text, select nach Options-Reihenfolge ist **nicht** nötig).
- Cursor: opaker Base64url-String aus den Sortierwerten + `(created_at, id)`; Keyset-Paginierung (keine OFFSETs); `nextCursor` nur, wenn weitere Treffer existieren.
- `ExecuteQuery` (Use-Case): Schema laden, parsen, Tenant-Filter ist implizit, Archivierte standardmäßig aus, Ergebnis `QueryResult { Items: RecordDto[], NextCursor?, Count? }` (kein Total-Count; optional `includeCount: true` mit `COUNT(*)` bis max. 10 000).
- `ExecuteView` (Use-Case in `Views/`): View laden, dessen Filter/Sorts/VisibleProperties als Query ausführen; zusätzlicher Request-Filter wird per `and` kombiniert.

*Infrastructure/Query*
- `SqlQueryTranslator`: `QueryDefinition` → EF-`IQueryable<Record>` mit `EF.Functions.JsonContains`/Raw-SQL-Fragmenten für JSONB (`properties @> …` für eq/contains auf select/relation, `properties->>'key'` mit Cast für Vergleiche, `jsonb_exists_any` für in auf multi_select). Parametrisiert, keine String-Interpolation von Werten. Der Tenant-Query-Filter bleibt aktiv (kein `IgnoreQueryFilters`).
- Index-Nutzung: Tests, die mit `EXPLAIN` prüfen, dass `eq` auf select und `contains` auf relation den GIN-Index verwenden (ein Test reicht, Testcontainers).

*Api*
- `POST /w/{slug}/collections/{id}/query` (Body: `QueryDefinition`), `POST /w/{slug}/views/{id}/query` (Body optional: zusätzlicher Filter, Cursor, Limit).
- OpenAPI: Operator-Matrix in der Beschreibung des Endpunkts (Agenten lesen das).

*Tests*
- Parser: jede Fehlerklasse mit `allowed`; verschachtelte Bäume; Pseudo-Felder.
- Translator (Testcontainers, Seed mit ~50 Records über alle Typen): je Operator je Typ mindestens ein Fall; `and`/`or`/`not` verschachtelt; Sortierung je Typ mit Nulls (`is_empty`-Semantik: Key fehlt oder null); Cursor über mehrere Seiten ohne Lücken/Duplikate (auch bei gleichen Sortierwerten); `includeArchived`; `fields` reduziert Properties; Limit-Grenzen.
- Api: Query-Endpunkte happy path; **Isolation** (`AssertIsolatedAsync`, fremde Collection/View 404); viewer darf abfragen (read); Filter auf fremden Property-Key → 400 mit `allowed`.

**Ausdrücklich nicht drin:**
- Volltext (`search`, Phase 2), relative Datumswerte, Aggregationen, Gruppierung (das macht die UI client-seitig für Boards innerhalb einer Seite; serverseitige Gruppierung Phase 2), MCP (AP-011), UI (AP-015).

## Fachlicher Kontext

- Syntax und Operatorliste in Architektur §3; dieses Paket legt die Typ-Matrix verbindlich fest und trägt sie in §3 nach (Doku-Änderung im Scope).
- Dieselbe DSL gilt für REST, MCP und UI; der Parser ist die einzige Stelle, die sie versteht. AP-008 hat nur strukturell validiert; dessen `ViewDefinitionValidator` auf den Parser umstellen (eine Wahrheit).
- Sicherheit: Nie Werte in SQL interpolieren; Property-Keys sind durch das Schema validiert (Regex `^[a-z][a-z0-9_]{0,62}\z`), trotzdem parametrisieren, wo möglich.

## Akzeptanzkriterien

1. Das Beispiel aus Architektur §3 (`and` mit `in`, `eq` auf actor, `or` mit `lt` auf date und `eq` auf select) läuft gegen einen Seed und liefert genau die erwarteten Records.
2. Jeder Operator der Matrix ist je Typ getestet; nicht anwendbare Kombinationen → 400 `operator_not_applicable` mit `allowed`.
3. Cursor-Paginierung über mindestens drei Seiten liefert jeden Record genau einmal, auch bei gleichen Sortierwerten; ungültiger Cursor → 400.
4. `POST /views/{id}/query` führt Filter und Sorts des Views aus und kombiniert einen Zusatzfilter per `and`.
5. `EXPLAIN` zeigt Index-Nutzung für `eq` auf select (`@>`).
6. Isolation für beide Endpunkte; viewer darf lesen.
7. `dotnet build` ohne Warnungen, `dotnet test` grün, `openapi/v1.json`/`schema.d.ts` neu erzeugt, Architektur §3 um die Typ-Matrix ergänzt.

## Tests, die mindestens entstehen

- `tests/Agcowork.Application.Tests/Query/{QueryParserTests,CursorTests}.cs`
- `tests/Agcowork.Application.Tests/Query/SqlQueryTranslatorTests.cs` (Testcontainers)
- `tests/Agcowork.Api.Tests/Records/{QueryEndpointsTests,QueryIsolationTests}.cs`

## Ergänzungen aus AP-008 (Review)

- `ViewFilterNode` (Domain/Views) ist die Wire- und Speicherform (flaches Record mit `and`/`or`/`prop`/`op`/`value`, unbekannte Schlüssel sind Fehler). Der Parser übersetzt in einen getypten AST (Group/Condition mit Operator-Enum); `ViewDefinitionValidator` wird auf den Parser umgestellt.
- Filter-Bedingungen auf Werte, die im Schema nicht mehr existieren (z. B. nach Bereinigung übersehen), liefern `not_in_options` mit `allowed`, nie still leere Ergebnisse.

## Entscheidungen, die der Implementierer treffen darf

- EF-LINQ mit JSON-Funktionen vs. `FromSql`-Fragmente (gemischt erlaubt; parametrisiert ist Pflicht).
- Cursor-Format (Base64url-JSON oder kompakter), solange opak und manipulationssicher gegen Fehler (ungültig → 400, nie 500).
- Ob `not` als eigener Knoten oder als Negation je Bedingung umgesetzt wird (Knoten bevorzugt).

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
| 2026-11-07 | an implementer-opus übergeben |
