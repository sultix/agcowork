# AP-026 – Volltextsuche Backend: tsvector, REST `/search`, MCP-Tool `search`

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | 2 |
| Implementierer | implementer-opus |
| Referenzbeispiel | Abfrage-Use-Case und Keyset-Paginierung `src/backend/Agcowork.Application/Query/`, MCP-Tools `src/backend/Agcowork.Api/Mcp/RecordTools.cs` (bzw. Datei mit `records_query`), Isolations-Fixture in `tests/` |
| Abhängig von | AP-024 (committet; Migration vorher) |
| Betroffene Pfade | neues Modul `src/backend/Agcowork.Application/Search/`, `src/backend/Agcowork.Infrastructure/Persistence/` (RecordConfiguration, **eine** Migration, ggf. SQL-Hilfen), `src/backend/Agcowork.Api/Endpoints/Search/`, `src/backend/Agcowork.Api/Mcp/` (neues Tool `search`), `openapi/v1.json`, `tests/`; im Frontend nur das neu erzeugte `schema.d.ts` |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §2a („Volltext- und semantische Suche filtern immer auf WorkspaceId“), §4.1, §4.2 (`search`), §4.3 (`GET /search?q=`), §5 „Suche“ |

## Ziel

Agenten und Menschen finden Records über Wörter in Titel, Text-Properties und Body, ohne die Collection oder die Filter-DSL zu kennen. Das Ergebnis nennt, wo der Treffer liegt, und zeigt einen Ausschnitt.

## Scope

**Drin:**

*1. Index*
- `records.search_vector tsvector` als generierte Spalte (STORED) oder per Trigger gepflegt, gewichtet: Titel A, Text-Properties B (`text`, `markdown`, `url`, `email`, Optionen-Labels von `select`/`multi_select`/`status`, falls billig), Body C. GIN-Index, der mit `workspace_id` beginnt (Modelltest „Indizes beginnen mit workspace_id“; wenn GIN mit führendem `workspace_id` `btree_gin` braucht: die Extension ist seit AP-007 vorhanden).
- **Grenze von tsvector:** PostgreSQL verweigert tsvector über 1 MB („string is too long for tsvector“). Der Body darf bis 2 MiB groß sein; der Index nimmt deshalb nur einen begrenzten Präfix des Bodys (z. B. `left(body, 200000)`), sodass Schreiben eines maximal großen Records nie scheitert. Test mit 2-MiB-Body Pflicht.
- Textsuchkonfiguration: `simple` (Inhalte sind gemischt Deutsch/Englisch, keine falsche Stammformbildung) plus Präfixsuche für das letzte Wort. Abweichung (z. B. zusätzlich `german`) begründen.
- Relation- und Actor-IDs, JSON-Payloads und Zahlen kommen nicht in den Index.

*2. Use-Case `SearchRecords`*
- Eingabe: `q` (Pflicht, 1–200 Zeichen), `spaceId?`, `collectionId?`, `includeArchived` (Default false), `limit` (Default 20, max 50), `cursor`.
- Parser: `websearch_to_tsquery('simple', q)` plus Präfix (`:*`) auf dem letzten Term; leere/unbrauchbare Anfrage → 400 `code: "empty_query"`.
- Sortierung nach `ts_rank_cd` absteigend, dann `id` (Keyset-Cursor über Rang + Id, opaker Cursor wie bei Query).
- Ergebnis je Treffer: `recordId`, `title`, `collectionId?`, `spaceId`, `snippet`, `rank`, `updatedAt`, `archived`. `snippet` per `ts_headline` nur für die ausgelieferten Treffer, mit festen Markern **ohne HTML** (z. B. `StartSel=⟦, StopSel=⟧` oder Private-Use-Zeichen), damit Clients selbst hervorheben und kein Markup injiziert wird. `MaxFragments`/`MaxWords` begrenzen.
- Requires `read`; Tenant-Filter wie überall (kein `IgnoreQueryFilters`).

*3. Adapter*
- REST `GET /api/v1/w/{slug}/search?q=…&spaceId=…&collectionId=…&includeArchived=…&limit=…&cursor=…` → 200 `{ items, nextCursor }`.
- MCP-Tool `search(query, spaceId?, collectionId?, includeArchived?, limit?, cursor?)` mit Beschreibung für Agenten: wann `search` statt `records_query`, Syntax (Wörter, `"Phrase"`, `-ausschließen`, `oder`), Bedeutung der Marker im Snippet, dass nur ein Präfix des Bodys indiziert ist. Den in §4.2 genannten Parameter `mode` **nicht** anbieten, bis es semantische Suche gibt (Phase 3); §4.2 entsprechend anpassen (Textvorschlag im Bericht).

**Ausdrücklich nicht drin:**
- Frontend (AP-027), semantische Suche/pgvector, Suche in Kommentaren, Ranking nach Aktualität, Synonyme.

## Fachlicher Kontext

- Titel ≤ 500 Zeichen, Body ≤ 2 MiB Markdown (AP-007). Properties liegen als JSONB in `records`.
- `websearch_to_tsquery` wirft nicht bei Benutzereingaben; trotzdem NUL-Zeichen und Steuerzeichen vorher abweisen (Phase-1-Blocker AP-009: NUL-Zeichen → 500).
- Generierte Spalten dürfen nur IMMUTABLE-Funktionen nutzen; `to_tsvector(regconfig, text)` mit expliziter Konfiguration ist es, `jsonb_to_tsvector(regconfig, jsonb, jsonb)` ebenso. Die Auswahl der Property-Werte nach Typ hängt am Collection-Schema; ist das in einer generierten Spalte nicht ausdrückbar, ist ein Trigger oder das Pflegen der Spalte im Schreib-Use-Case (Records + `RecordValuesSchemaHook`) zulässig. Entscheidung im Bericht.

## Akzeptanzkriterien

1. Treffer im Titel rangiert vor Treffer nur im Body (gleiches Wort).
2. Präfix: `entsch` findet „Entscheidung“; Phrase und Ausschluss funktionieren (`"event sourcing" -kafka`).
3. Filter `spaceId`, `collectionId`, `includeArchived` wirken; archivierte Records standardmäßig nicht dabei.
4. Snippet enthält die Marker um den Treffer und kein HTML aus dem Body (Body mit `<script>` → Snippet ohne interpretierbares Markup bzw. unverändert als Text, Marker nur vom Server).
5. Record mit 2-MiB-Body lässt sich anlegen und ändern; Wort am Anfang des Bodys wird gefunden.
6. Nach `PATCH` (Titel/Body/Property) findet die Suche den neuen Inhalt, nicht mehr den alten.
7. `q` leer, nur Satzzeichen, > 200 Zeichen, mit NUL → 400 mit `code`, nie 500.
8. Paginierung: `limit` + `nextCursor` liefern alle Treffer ohne Duplikate.
9. Isolation: Treffer aus `beta` erscheinen nie in `alpha` (REST und MCP); `AssertIsolatedAsync` für `/search`.
10. `dotnet build`, `dotnet test`, `npm run build` grün.

## Tests, die mindestens entstehen

- Integration (Testcontainers): Kriterien 1–9; Modelltest Index beginnt mit `workspace_id`.
- MCP: Tool-Aufruf mit Filter, Fehler strukturiert.

## Entscheidungen, die der Implementierer treffen darf

- Generierte Spalte, Trigger oder Pflege im Use-Case (siehe Kontext).
- Länge des indizierten Body-Präfixes, Snippet-Parameter, Marker-Zeichen (in OpenAPI und Tool beschreiben).
- Ob Optionen-Labels von Select-Typen in den Index kommen.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
| 2026-11-08 | an implementer-opus übergeben (parallel zu AP-025); nach Nutzungslimit per Nachricht fortgesetzt |
| 2026-11-08 | Bericht: 1126 Tests grün; Trigger statt generierter Spalte (Schema bestimmt indexierte Werte), `search_vector` außerhalb des EF-Modells mit Translator, Body-Präfix 100.000 / Property 50.000 Zeichen, Label und Value von Select-Optionen, Marker ⟦⟧, Rang mit Titelbonus und `float4` (Keyset-Gleichstand), `or` statt `oder`, Katalogtest für Indizes |
| 2026-11-08 | Review gestartet |
| 2026-11-08 | Review: **Blockiert** – B1 tsvector-Limit mit zulässigen Inhalten erreichbar (4-Byte-Zeichen in Bindestrich-Komposita ≈ 8 B/Zeichen, Body + Property → 1,2 MB, gemessen); wichtig: `ts_headline` bis 313 ms je Dokument, Neuindexierung bei Schemaänderung läuft in den 30-s-Timeout, Snippet-Zusage „kein HTML“ falsch; Tenancy, tsquery-Injection, Cursor in Ordnung |
| 2026-11-08 | Entscheidung Orchestrator: Records in archivierten Collections/Spaces gelten in der Suche als archiviert (Standard ausgeschlossen, `archived: true`) |
| 2026-11-08 | Nacharbeit an implementer-opus (B1 mit Präfixen und Fallback im Trigger, W1–W3, H1–H4, H6) |
| 2026-11-08 | Nacharbeit: Präfixe 75.000/25.000 plus plpgsql-Fallback bei `program_limit_exceeded` (Zehntel, dann nur Titel), Snippet aus den ersten 20.000 Zeichen (≈ 6 ms je Dokument), Neuindexierung nur für Records mit geändertem Key, Snippet-Formulierung, archivierte Container; 1132/1132 |
| 2026-11-08 | Nachprüfung gestartet |
| 2026-11-08 | Nachprüfung: Freigabe mit Hinweisen (Fallback-Stufen nachgemessen, Exception-Block vergibt keine Sub-XID); Konsistenztest SQL-Default ↔ C#-Konstante → Backlog |
| 2026-11-08 | abgeschlossen |
