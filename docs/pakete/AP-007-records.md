# AP-007 – Records, Revisionen, Activity, optimistische Nebenläufigkeit, append

| Feld | Wert |
|---|---|
| Status | in Arbeit |
| Phase | 1 |
| Implementierer | implementer-opus |
| Referenzbeispiel | AP-006: Collections-Use-Cases, `PropertyValueValidator`, Endpunkte und Isolationstests; AP-005: `RequireWritable`/`RequireScope`, `LockWorkspaceAsync`; AP-004: Fehler-Muster, `SaveChangesOrConflictAsync` |
| Abhängig von | AP-006 |
| Betroffene Pfade | `src/Agcowork.Domain/Records/`, `src/Agcowork.Domain/Activity/`, `src/Agcowork.Application/Records/`, `src/Agcowork.Application/Activity/`, `src/Agcowork.Infrastructure/Persistence/` (Konfigurationen, Migration), `src/Agcowork.Api/Endpoints/{Records,Activity}/`, `tests/`, `src/Agcowork.Api/openapi/v1.json`, `src/Agcowork.Web/src/app/core/api/generated/schema.d.ts` (nur neu erzeugen) |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §2 (Record, Revision, ActivityEvent, Comment nur als Hinweis), §2a, §4.1 (Idempotenz & Nebenläufigkeit), §4.2 (`record_*`, `history_get`, `activity_recent`), §4.3, §4.4 (Markdown mit Frontmatter), §5 (keine Retry-Strategie, Reihenfolge nie über Id, Unique-Konflikte); `01-PLANUNG.md` E2, E6, F1, F2, F6 |

## Ziel

Nach diesem Paket können Agenten und Menschen Records anlegen, lesen, ändern, Markdown
anhängen und archivieren. Jede Änderung erzeugt eine Revision und ein ActivityEvent mit
Actor. Updates können eine erwartete Revision mitgeben, damit sich zwei Agenten nicht
überschreiben. Records sind als JSON und als Markdown mit Frontmatter abrufbar.

## Scope

**Drin:**

*Domain*
- `Record` (`ITenantEntity`): `WorkspaceId`, `SpaceId`, `CollectionId?` (null = freie Seite im Space), `ParentRecordId?`, `Title`, `Properties` (JSONB, Dictionary<string, JsonNode?>), `Body` (Markdown, max. 2 MB), `RevisionNo` (ab 1), `CreatedBy`, `CreatedAt`, `UpdatedBy`, `UpdatedAt`, `ArchivedAt?`. Invarianten: Collection und Parent im selben Workspace; Parent nicht zyklisch (Tiefe ≤ 32); Titel ≤ 500 Zeichen, nicht leer.
- `Revision` (`ITenantEntity`): `RecordId`, `No`, `Title`, `Properties`, `Body`, `ActorId`, `At`, `Reason?`, `Kind` (created | updated | appended | archived | restored). Vollständiger Snapshot (Architektur §5).
- `ActivityEvent` (`ITenantEntity`): `Type` (`record.created`, `record.updated`, `record.appended`, `record.archived`, `record.restored`, `collection.created`, `collection.schema_changed`, `space.created`, `member.added`, `member.updated`, `member.removed`, …), `ActorId`, `TargetType`, `TargetId`, `SpaceId?`, `CollectionId?`, `Diff` (JSONB: geänderte Property-Keys mit alt/neu, Body-Änderung als `{ bodyChanged: true, bodyDelta: +n/-m Zeichen }`), `At`, `Seq` (monoton, `bigint identity`), für Sortierung (nie über Id).

*Application*
- `Records/`: `CreateRecord` (Collection oder Space; Template anwenden, wenn `templateId` oder Default-Vorlage; Properties gegen Schema validieren und normalisieren; `title` ist Pflicht; Relation-/Actor-Ziele prüfen über die Interfaces aus AP-006, hier implementieren), `GetRecord` (JSON; Optionen `includeBody`, `includeHistory` (letzte n Revisionen), `format: json | markdown`), `UpdateRecord` (Properties als Patch: nur gesendete Keys, `null` löscht; Body optional ersetzen; `expectedRevision?` → 409 `revision_mismatch` mit aktueller Revision im Body; `reason?`), `AppendToRecord` (Markdown an Body anhängen; optional `section` = Überschrift, unter der angehängt wird, sonst am Ende; erzeugt Revision `appended`), `ArchiveRecord`, `RestoreRecord`, `ListRecords` (einfache Liste je Collection/Space ohne Filter-DSL, Cursor-Paginierung nach `(CreatedAt, Id)`, `includeArchived`), `GetRecordHistory` (Revisionen absteigend, Cursor), `GetRevision`.
- `Activity/`: `ListActivity` (Workspace-weit, Filter `spaceId`, `collectionId`, `actorId`, `since`, `types[]`; Cursor über `Seq`), `IActivityRecorder.Record(...)`, das Use-Cases in derselben Transaktion aufrufen. **Nachrüsten**: AP-005/AP-006-Use-Cases für Space/Collection/Member erzeugen ab jetzt ActivityEvents (klein, aber im Scope, damit der Feed vollständig ist).
- Jeder schreibende Record-Use-Case: `RequireWritable`, `RequireScope("write")`, dann Transaktion (`BeginTransactionAsync`): Record ändern + Revision + ActivityEvent + `SaveChanges`. Keine Retry-Strategie (Architektur §5).
- Markdown-Frontmatter-Rendering (§4.4): `---` Block mit `id`, `collection`, `space`, `title`, allen Properties (Werte als YAML-Skalare/Listen), `revision`, `created_at`, `updated_at`, `created_by`, `updated_by` → dann Body. Umgekehrt (Frontmatter → Properties) ist nicht nötig.
- `Idempotency-Key`-Header bei `POST /records`: gleicher Key + gleicher Actor innerhalb 24 h → gleiche Antwort (201 mit demselben Record), gespeichert in Tabelle `idempotency_keys` (`WorkspaceId`, `ActorId`, `Key`, `RecordId`, `At`).
- Hilfen für AP-009 (Filter-DSL) und AP-011 (MCP): `RecordDto` mit `properties` als JSON-Objekt, `RecordMarkdown` als string, Mapper an einer Stelle.

*Infrastructure*
- Konfigurationen `records` (GIN-Index auf `properties` jsonb_path_ops, Index `(workspace_id, collection_id, created_at, id)`, `(workspace_id, space_id)`, `(workspace_id, parent_record_id)`), `revisions` (`(workspace_id, record_id, no)` unique), `activity_events` (`(workspace_id, seq)`, `(workspace_id, collection_id, seq)`, `(workspace_id, actor_id, seq)`), `idempotency_keys` (`(workspace_id, actor_id, key)` unique). Migration `AddRecords`.
- Volltext-`tsvector` **noch nicht** (Phase 2), aber Spalte nicht ausschließen.

*Api*
- `/w/{slug}/records` (GET mit `?collectionId=|spaceId=`, POST), `/w/{slug}/records/{id}` (GET mit `?format=json|markdown&includeBody&includeHistory=n`, PATCH), `/w/{slug}/records/{id}/append` (POST), `/w/{slug}/records/{id}/archive` (POST), `/w/{slug}/records/{id}/restore` (POST), `/w/{slug}/records/{id}/history` (GET), `/w/{slug}/records/{id}/revisions/{no}` (GET), `/w/{slug}/activity` (GET).
- `format=markdown` liefert `text/markdown; charset=utf-8`.
- 409 `revision_mismatch` enthält `currentRevision` und die seit `expectedRevision` geänderten Keys (damit ein Agent entscheiden kann, ob er mergen kann).

*Tests*
- Domain: Invarianten (Zyklus, Tiefe, Titel), Revision-Snapshot, Diff-Berechnung.
- Application: Create mit Vorlage, Patch-Semantik (null löscht, unbekannter Key → `unknown_property` mit `allowed`), `expectedRevision` passt/passt nicht, Append mit/ohne Section, Frontmatter-Rendering (Snapshot), Idempotenz (zwei Posts gleicher Key → ein Record), Activity-Diff.
- Api: alle Endpunkte happy path; **Isolation** für jeden Endpunkt über `AssertIsolatedAsync` und fremde Record-IDs (`AssertNotFoundAsync`); viewer → 403 bei Schreiben; archivierter Workspace → 409; parallele Updates mit gleicher `expectedRevision` → genau ein 200 und ein 409 (Transaktion + `RevisionNo`-Prüfung, bei Bedarf `SELECT … FOR UPDATE` auf den Record).

**Ausdrücklich nicht drin:**
- Filter-DSL/Abfrage-Endpunkt (AP-009), Views (AP-008), Kommentare (Phase 2), Volltext (Phase 2), SignalR (Phase 2), MCP (AP-011), UI.
- Löschen (hart) von Records; nur Archivieren.

## Fachlicher Kontext

- Markdown ist kanonisch (E2); `append` ist der bevorzugte Weg für Agenten, Log-artige Einträge zu schreiben, ohne den Body zu kennen (§4.2).
- Reihenfolge nie über die Id (§5): Revisionen über `No`, Activity über `Seq`.
- Patch-Semantik der Properties: nur gesendete Keys werden geändert; `null` entfernt den Wert; Pflichtfelder dürfen nicht auf `null` gesetzt werden (`required`).
- Property-Validierung und -Normalisierung kommen aus AP-006 (`PropertyValueValidator`, `PropertyValueNormalizer`); nicht duplizieren.
- Record-Vorlagen (`Template`) aus AP-006: `PropertiesPreset` wird mit den gesendeten Properties gemischt (gesendet gewinnt), `BodyMarkdown` wird Body, wenn keiner gesendet wurde.

## Ergänzungen aus AP-006 (Bericht)

- `IRelationTargetChecker` echt implementieren (heute `NoRecordsRelationTargetChecker` per `TryAdd`; eigene Registrierung muss diese ersetzen).
- Beim Entfernen von Properties (`UpdateSchema` mit `confirmRemoval`) die Werte aus den Records löschen (JSONB-Update, tenant-gefiltert, ohne `IgnoreQueryFilters`); AP-006 bereinigt nur Template-Presets.
- `CollectionStatisticsDto.RecordCount` in `DescribeCollection` füllen.
- Beim Anlegen aus Vorlage das Preset erneut gegen das aktuelle Schema prüfen (Options können entfernt worden sein).
- `ISchemaChangedHook` (aus AP-006-Nacharbeit) registrieren: bei entfernten Keys, entfernten Options, gesenkter Präzision und markdown→text die Record-Werte bereinigen bzw. kürzen (tenant-gefiltert, in derselben Unit of Work). Trimmen von Property-Strings ist Normalizer-Verhalten; Präfixe wie `"Bug: "` nicht aus Properties zusammensetzen.
- `PropertyValidationMode.Full` verlangt `title` im Properties-Dictionary: Titel beim Validieren hineinmischen oder separat prüfen; eine Stelle, dokumentiert.

## Akzeptanzkriterien

1. Agent mit `write` legt einen Record in einer Collection an (mit und ohne Vorlage); `GET` liefert ihn als JSON und als Markdown mit Frontmatter (Properties, Revision, Zeitstempel).
2. `PATCH` mit `expectedRevision` passt → 200 mit neuer Revision; veraltet → 409 `revision_mismatch` mit `currentRevision` und geänderten Keys; ohne `expectedRevision` → letzter Schreiber gewinnt.
3. `append` hängt Markdown unter einer Section bzw. am Ende an und erzeugt Revision `appended`; Body-Limit wird durchgesetzt (413 oder 400 mit Code `too_long`).
4. History listet alle Revisionen mit Actor, Kind, Reason; jede Revision ist einzeln abrufbar; Activity-Feed zeigt Record- und (nachgerüstet) Space-/Collection-/Member-Ereignisse mit Diff, filterbar und per Cursor paginiert.
5. Idempotency-Key: zweiter identischer POST liefert denselben Record, kein Duplikat.
6. Isolation: alle Endpunkte 404 für fremden Workspace, fremde Record-IDs 404; viewer 403; archiviert 409.
7. Parallele Updates mit gleicher `expectedRevision`: genau ein Erfolg.
8. `dotnet build` ohne Warnungen, `dotnet test` grün, `openapi/v1.json` und `schema.d.ts` neu erzeugt.

## Tests, die mindestens entstehen

- `tests/Agcowork.Domain.Tests/Records/{RecordTests,RevisionTests}.cs`
- `tests/Agcowork.Application.Tests/Records/{CreateRecordTests,UpdateRecordTests,AppendToRecordTests,MarkdownRenderingTests,IdempotencyTests}.cs`, `Activity/ListActivityTests.cs`
- `tests/Agcowork.Api.Tests/Records/{RecordsEndpointsTests,RecordsIsolationTests,ConcurrencyTests}.cs`, `Activity/ActivityEndpointsTests.cs`

## Entscheidungen, die der Implementierer treffen darf

- Diff-Format im ActivityEvent (muss alt/neu je Key enthalten); Section-Matching bei `append` (exakte Überschrift, case-insensitiv).
- Row-Lock vs. Prüfung von `RevisionNo` im `UPDATE … WHERE revision_no = @expected` (Letzteres bevorzugt, einfacher und ohne Lock).
- YAML-Serialisierung für Frontmatter (eigene minimale Serialisierung reicht; keine große Bibliothek).

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
| 2026-11-07 | an implementer-opus übergeben |
