# AP-008 – Views: CRUD, Standard-Views je Collection

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | 1 |
| Implementierer | implementer-sonnet |
| Referenzbeispiel | **AP-006 `Collections/Templates`** (Use-Cases Create/Update/Delete/List an einer Collection, Endpunkte, Isolationstests) – Views sind strukturell dasselbe: ein Kind-Objekt einer Collection mit JSON-Inhalt |
| Abhängig von | AP-006 |
| Betroffene Pfade | `src/Agcowork.Domain/Views/`, `src/Agcowork.Application/Views/`, `src/Agcowork.Infrastructure/Persistence/` (Konfiguration, Migration), `src/Agcowork.Api/Endpoints/Views/`, `tests/`, `src/Agcowork.Api/openapi/v1.json`, `src/Agcowork.Web/src/app/core/api/generated/schema.d.ts` (nur neu erzeugen) |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §2 (View), **§3 Views komplett**, §4.2 (`view_create`), §4.3; `01-PLANUNG.md` F5 |

## Ziel

Nach diesem Paket gibt es Views als eigene Objekte an einer Collection, mit Typ, Filter,
Sortierung, Gruppierung und sichtbaren Feldern. Jede Collection bekommt automatisch die
Views `table` und `raw`. Agenten und Menschen können weitere Views anlegen. Die UI (AP-015)
rendert aus diesen Objekten.

## Scope

**Drin:**

*Domain*
- `View` (`ITenantEntity`): `WorkspaceId`, `CollectionId`, `Name`, `Type` (`table | board | gallery | list | document | raw`; `timeline`/`calendar` nicht erlaubt, Fehler mit `allowed`), `Filter` (JSONB, Filter-DSL nach Architektur §3; hier nur **strukturell** validiert: gültiges JSON-Objekt mit `and`/`or`/Bedingungen `{ prop, op, value }`, `op` aus der Liste in §3, `prop` muss im Collection-Schema existieren; die Übersetzung nach SQL kommt in AP-009), `Sorts` (JSONB `[ { prop, dir } ]`, `prop` im Schema, `dir` asc|desc), `GroupByProperty?` (Pflicht bei `board`, muss `select`/`status` sein), `VisibleProperties` (Liste von Keys im Schema; leer = alle), `CardCoverProperty?` (gallery), `IsDefault` (genau ein Default je Collection), `SortOrder`, `CreatedBy`, `CreatedAt`.
- Invarianten: Name ≤ 100 Zeichen, eindeutig je Collection; `raw`- und `table`-Default-Views sind nicht löschbar (`system`-Kennzeichen `IsSystem`), aber umbenennbar und konfigurierbar.

*Application*
- `Views/`: `CreateView`, `GetView`, `ListViews` (je Collection, sortiert nach `SortOrder`, `Name`), `UpdateView` (alle Felder außer `CollectionId`, `IsSystem`), `DeleteView` (nicht für System-Views → 409 `system_view`), `SetDefaultView`. Scopes: `read` für Lesen, `write` für Anlegen/Ändern/Löschen (Views sind Präsentation, kein Schema).
- Validierung über `ViewDefinitionValidator` (Filter-Struktur, Sorts, Keys gegen Schema aus `ISchemaProvider`); Fehler mit `allowed` (Operatoren, Property-Keys, Typen).
- **Standard-Views**: Beim Anlegen einer Collection (Hook/Event aus AP-006) entstehen `Tabelle` (`table`, Default, `IsSystem`) und `Rohdaten` (`raw`, `IsSystem`). Für bereits vorhandene Collections ohne Views: eine einmalige Nachlauf-Logik ist nicht nötig (es gibt noch keine Produktivdaten), aber `ListViews` legt sie **nicht** heimlich an.
- Bei Schemaänderungen (AP-006 `UpdateSchema`): entfernt ein Feld, werden Referenzen in Views bereinigt (aus `VisibleProperties`, `Sorts`, Filter-Bedingungen entfernt; `GroupByProperty` → View-Typ bleibt, Feld wird `null` und View meldet `needsAttention: true` im DTO). Dafür einen Hook in `UpdateSchema` nutzen oder ein Domain-Event; mit AP-006-Code abstimmen, nicht duplizieren.
- ActivityEvents `view.created`, `view.updated`, `view.deleted` über `IActivityRecorder`, falls AP-007 bereits committet ist; sonst `TODO(AP-007)` im Code und im Bericht nennen.

*Infrastructure*
- Konfiguration `views` (Index `(workspace_id, collection_id, sort_order)`, unique `(workspace_id, collection_id, name)`), Migration `AddViews`.

*Api*
- `/w/{slug}/collections/{collectionId}/views` (GET, POST), `/w/{slug}/views/{id}` (GET, PATCH, DELETE), `/w/{slug}/views/{id}/default` (POST). OpenAPI-Beschreibungen agententauglich (Typen, Pflichtfelder je Typ).

*Tests*
- Domain: Typ-Allowlist, Board braucht GroupBy, System-View nicht löschbar.
- Application: Validator (Filter-Struktur, unbekannter Operator/Key mit `allowed`), Standard-Views bei Collection-Anlage, Bereinigung bei Feldentfernung, `SetDefault` wechselt genau einen Default.
- Api: alle Endpunkte happy path, Isolation (`AssertIsolatedAsync`, fremde View-IDs 404), viewer 403 bei Schreiben, archiviert 409.

**Ausdrücklich nicht drin:**
- Ausführung des Filters gegen Records (AP-009), UI (AP-015), `timeline`/`calendar` (Phase 3).

## Fachlicher Kontext

- Views sind Daten, nicht Code (§3). Dieselbe Collection kann beliebig viele Views haben.
- Filter-DSL-Syntax in §3; Operatoren: `eq, neq, in, nin, contains, starts_with, gt, gte, lt, lte, is_empty, is_not_empty`.
- Muster für Use-Cases, Endpunkte, Isolationstests exakt wie in AP-006 `Templates`.

## Ergänzungen aus AP-006 (Bericht)

- `ICollectionCreatedHook` existiert und läuft vor `SaveChanges` in derselben Unit of Work; hier registrieren, um die System-Views anzulegen.
- Für die Bereinigung bei Feldentfernung `ISchemaChangedHook` (aus AP-006-Nacharbeit, `RemovedKeys`, `RemovedOptions`, `TypeChanges`) implementieren; `AdjustTemplatesAsync` ist das Referenzbeispiel.
- Scope-Konvention aus AP-006: Lesen = `read`; Views sind Präsentation → Anlegen/Ändern/Löschen = `write`.

## Akzeptanzkriterien

1. Nach `POST /collections` existieren zwei System-Views (`table` Default, `raw`); `GET …/views` listet sie.
2. `POST …/views` mit Typ `board`, `groupBy: status` und Filter nach §3 → 201; ungültiger Operator oder unbekannter `prop` → 400 mit `allowed`; `board` ohne `groupBy` → 400; `timeline` → 400 mit `allowed`.
3. `DELETE` einer System-View → 409 `system_view`; `POST …/default` wechselt den Default, genau einer bleibt Default.
4. Entfernen eines Schema-Feldes (AP-006 `confirmRemoval`) bereinigt Views; betroffener Board-View zeigt `needsAttention`.
5. Isolation für alle Endpunkte; viewer 403; archiviert 409.
6. `dotnet build` ohne Warnungen, `dotnet test` grün, `openapi/v1.json` und `schema.d.ts` neu erzeugt.

## Tests, die mindestens entstehen

- `tests/Agcowork.Domain.Tests/Views/ViewTests.cs`
- `tests/Agcowork.Application.Tests/Views/{ViewDefinitionValidatorTests,DefaultViewsTests,SchemaChangeCleanupTests}.cs`
- `tests/Agcowork.Api.Tests/Views/{ViewsEndpointsTests,ViewsIsolationTests}.cs`

## Entscheidungen, die der Implementierer treffen darf

- Namen der Standard-Views (deutsch, z. B. „Tabelle“, „Rohdaten“); JSON-Form von `Filter`/`Sorts` als Records vs. `JsonObject` (Records bevorzugt, weil AP-009 sie übersetzt).

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
| 2026-11-07 | an implementer-sonnet übergeben |
| 2026-11-07 | Bericht: 713 Tests grün (65 neu). `DefaultViewsHook` (table „Tabelle“ Default, raw „Rohdaten“, System), `ViewSchemaHook` (Bereinigung, `affectedViews` im Event), Sperr-Reihenfolge Collection shared → View. Wire-Feld `groupBy`; `NeedsAttention` berechnet; System-View-Typ nicht änderbar; Default-Löschung macht Tabelle zum Default; kein `view.created` für Standard-Views |
| 2026-11-07 | an reviewer übergeben |
| 2026-11-07 | Review: Freigabe mit Hinweisen; Tenancy und Sperr-Reihenfolge korrekt, Record-Form für AP-009 geeignet. Wichtig: unbekannte Filter-Schlüssel heben den Filter still auf; entfernte Optionen bleiben in Filterwerten. Hinweise: eigene Transaktion statt Join (kollidiert mit AP-010-Vorlage), `value` bei is_empty akzeptiert, Tests für SetDefault-Race und archivierten Workspace fehlen, DeleteView-Kommentar, `groupBy` Whitespace, `null`-Felder im JSON |
| 2026-11-07 | Nacharbeit an implementer-sonnet (7 Punkte, Join-Transaktion zuerst) |
| 2026-11-07 | Nacharbeit fertig: 726 Tests grün. Join-Transaktion in allen View-Schreibern, unbekannte Filter-Schlüssel → `unknown_property` mit `allowed`, Filterwerte-Bereinigung (`View.RewriteConditions`), `is_*` ohne value, SetDefault-Race-Test, archivierter Workspace, `WhenWritingNull` global. Vom Orchestrator mit Frontend verifiziert. Abgeschlossen, Commit `26c830f` |
