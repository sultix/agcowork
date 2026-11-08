# AP-037 – Relationen Backend: Titel-Lookup, Rückverweise, Auflösung für Agenten, archivierte Ziele

| Feld | Wert |
|---|---|
| Status | angelegt |
| Phase | 2 |
| Implementierer | implementer-opus |
| Referenzbeispiel | Gebündelte Titelauflösung `src/backend/Agcowork.Application/Activity/ListActivity.cs` (`ActivityTitles.ResolveAsync`); Relation-Prüfung `Application/Schema/ReferenceCheckers.cs`, `PropertyTypeRegistry.CheckRelation`; JSONB-Containment `Infrastructure/Query/SqlQueryTranslator.cs` (`Holds`); Keyset-Paginierung `Application/Query/`; MCP `Api/Mcp/Tools/RecordTools.cs` |
| Abhängig von | AP-036 (committet) |
| Betroffene Pfade | `src/backend/Agcowork.Application/Records/` (neu `LookupRecords.cs`, `GetBacklinks.cs`, `GetRecord` für `expandRelations`/`includeBacklinks`), `Application/Collections/CollectionInputValidation.cs`, `Infrastructure/` nur falls Abfragehilfen nötig (keine Migration erwartet), `Api/Endpoints/Records/`, `Api/Mcp/Tools/RecordTools.cs`, `McpText.cs`, `openapi/v1.json`, `tests/`; im Frontend nur `schema.d.ts`. **Nicht:** Frontend-Code (AP-039 läuft parallel) |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §2 „Relationen“ (verbindlich), §2a (Tenancy, RLS), §4.1, §4.2 |

## Ziel

Menschen und Agenten sehen zu jeder Relation den Titel des Ziels und finden zu jedem Record, wer auf ihn verweist – ohne für jede ID einzeln anzufragen oder alle Collections von Hand abzufragen.

## Scope

**Drin:**

1. **Lookup:** `POST /api/v1/w/{slug}/records/lookup` mit `{ ids: uuid[] }` (1–200, Duplikate egal) → `{ items: [{ id, title, collectionId, spaceId, archived }] }`, Reihenfolge wie angefragt, unbekannte/fremde IDs fehlen. `read`. Eine Abfrage (kein N+1), Tenant-Filter. `archived` wie in der Suche (Record oder Container archiviert).
2. **Rückverweise:** `GET /api/v1/w/{slug}/records/{id}/backlinks?limit&cursor` → Gruppen `{ collectionId, collectionName, propertyKey, propertyName, items: [{ id, title, archived }], nextCursor? }` bzw. eine flache, nach (Collection, Property, Record) sortierte Liste mit Gruppierungsfeldern – Implementierer entscheidet, Hauptsache paginierbar und ohne Gesamtscan. Nur Collections, deren Schema eine Relation auf die Collection des Records hat (auch Self-Relations); Abfrage per JSONB-Containment (skalar und Array), Index-tauglich. Archivierte Quell-Records standardmäßig ausgeschlossen, `includeArchived` optional. Freie Seiten (ohne Collection) haben keine Rückverweise per Relation.
3. **Agenten:** `record_get(expandRelations?: bool, includeBacklinks?: bool)`: Mit `expandRelations` enthält das Ergebnis zu jeder Relation-Property die Titel (Structured Content: Map `relations: { key: [{ id, title, archived }] }`; Markdown-Ausgabe zeigt Titel neben IDs). Mit `includeBacklinks` die ersten N Rückverweise je Gruppe mit Hinweis auf mehr. REST `GET /records/{id}` bekommt dieselben Optionen. Keine neuen Tools (Tool-Anzahl klein halten), Beschreibungen ergänzen.
4. **Archivierte Ziel-Collection:** `CheckRelationTargetsAsync` lehnt eine archivierte Ziel-Collection beim Anlegen einer Relation (neue Property, `CreateCollection`) mit 400 `target_archived` ab; bestehende Relationen auf später archivierte Collections bleiben gültig.
5. **Doku-Text:** Vorschlag für `02-ARCHITEKTUR.md` §4.2/§4.3 (neue Parameter, Endpunkte).

**Ausdrücklich nicht drin:**
- Gegen-Properties/zweiseitige Relationen, Kaskaden beim Archivieren, Frontend.

## Akzeptanzkriterien

1. Lookup liefert Titel, Container-IDs und `archived` in Anfrage-Reihenfolge; fremde und unbekannte IDs fehlen; > 200 → 400; eine Datenbankabfrage (Test über Befehlszähler oder Log).
2. Backlinks findet Verweise aus mehreren Collections und Self-Relations, skalare und Array-Werte; Paginierung ohne Duplikate; archivierte Quellen nur mit `includeArchived`.
3. EXPLAIN für Backlinks auf 100k Records nutzt einen Index (GIN auf `properties` mit `workspace_id`), kein Seq Scan über alle Records; im Bericht.
4. `record_get(expandRelations, includeBacklinks)` und REST-Pendant liefern Titel und Rückverweise; Beschreibungen erklären beides.
5. Neue Relation auf archivierte Collection → 400 `target_archived`; bestehende bleiben.
6. Isolation: Lookup und Backlinks (REST und MCP) sehen nie Records anderer Workspaces; fremde Record-ID → 404 bei Backlinks; RLS-Suite bleibt grün.
7. `dotnet build`, `dotnet test` grün; Frontend `npm run build` grün.

## Entscheidungen, die der Implementierer treffen darf

- Antwortform der Backlinks (gruppiert oder flach mit Gruppenfeldern), solange paginierbar.
- Ob `expandRelations` auch in Query-Ergebnissen angeboten wird (nur, wenn ohne N+1 und ohne großen Umbau).

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-08 | angelegt |
