# AP-006 – Spaces, Collections, Property-Schema, Validierung, JSON Schema, Record-Vorlagen

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | 1 |
| Implementierer | implementer-opus |
| Referenzbeispiel | AP-005: Tenant-Entitäten, `RequireScope`, Workspace-Endpunkte, Isolations-Fixture; AP-004: Use-Case-/Fehler-Muster |
| Abhängig von | AP-005 |
| Betroffene Pfade | `src/Agcowork.Domain/Spaces/`, `src/Agcowork.Domain/Collections/` (inkl. `Schema/`), `src/Agcowork.Application/Spaces/`, `src/Agcowork.Application/Collections/`, `src/Agcowork.Application/Schema/` (Typsystem, Validierung, JSON Schema), `src/Agcowork.Infrastructure/Persistence/` (Konfigurationen, Migration), `src/Agcowork.Api/Endpoints/{Spaces,Collections}/`, `tests/` |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §2 (Space, Collection, PropertyDefinition, Template, Property-Typen, Beispiel-Sammlungen), §2a, §4.1 (Schema maschinenlesbar, Fehler sind Hinweise), §4.3; `01-PLANUNG.md` E3, F3 |

## Ziel

Nach diesem Paket können Agenten und Menschen in einem Workspace Spaces und Collections
anlegen, das Schema einer Collection beschreiben und ändern, und das Schema maschinenlesbar
abrufen (inkl. JSON Schema, Vorlagen, Beschreibungen). Die Validierung von Property-Werten
gegen ein Schema ist zentral implementiert und wird von AP-007 (Records) wiederverwendet.

## Scope

**Drin:**

*Domain*
- `Space` (`ITenantEntity`): `WorkspaceId`, `Name`, `Description?`, `Icon?`, `SortOrder`, `CreatedBy`, `CreatedAt`, `ArchivedAt?`.
- `Collection` (`ITenantEntity`): `WorkspaceId`, `SpaceId`, `Name`, `Description` (Pflicht, für Agenten geschrieben), `Schema` (geordnete Liste von `PropertyDefinition`, JSONB), `TitlePropertyKey` (Standard `title`, immer vorhanden), `SchemaVersion` (int, bei jeder Schemaänderung +1), `CreatedBy`, `CreatedAt`, `ArchivedAt?`.
- `PropertyDefinition`: `Key` (snake_case, `^[a-z][a-z0-9_]{0,62}\z`, unveränderlich nach Anlage), `Name`, `Type`, `Description?`, `Required`, `Options?` (für select/multi_select/status: Liste `{ value, label, color?, group? }`; bei status Gruppen `todo | in_progress | done`), `TargetCollectionId?` + `Multiple` (relation), `Default?`, `Unit?`/`Precision?` (number, optional).
- Property-Typen genau nach Architektur §2: `text`, `markdown`, `number`, `checkbox`, `select`, `multi_select`, `status`, `date`, `datetime`, `url`, `email`, `relation`, `actor`, `json`; berechnet (nicht speicherbar, nur lesbar): `created_at`, `updated_at`, `created_by`, `updated_by`.
- `Template` (`ITenantEntity`): `CollectionId`, `Name`, `Description?`, `PropertiesPreset` (JSONB), `BodyMarkdown`, `IsDefault`.
- Invarianten: Keys eindeutig je Collection; `title` ist reserviert und vom Typ text; Options-Values eindeutig; `relation` zeigt auf eine Collection desselben Workspaces; Umbenennen eines Keys ist nicht erlaubt (stattdessen neues Feld), Typänderung nur innerhalb erlaubter Pfade (text ↔ markdown, select → multi_select; alles andere Validierungsfehler `type_change_not_allowed`).

*Application*
- `Schema/`: `PropertyTypeRegistry` (Typ → Validierer, JSON-Schema-Fragment, Beispielwert), `PropertyValueValidator.Validate(schema, properties) → FieldError[]` (unbekannte Keys → `unknown_property` mit `allowed`; Pflichtfelder; Typprüfung; `not_in_options` mit `allowed`; URL/E-Mail-Format; Datum ISO 8601; relation: IDs müssen existieren und zum Ziel passen → wird in AP-007 angeschlossen, hier Interface `IRelationTargetChecker`; actor: Actor muss Mitglied des Workspaces sein, Interface analog), `PropertyValueNormalizer` (Trim, Datum normalisieren, select-Werte casesensitiv wie gespeichert), `JsonSchemaBuilder.Build(collection) → JsonObject` (Draft 2020-12, `description` aus Property-Beschreibungen, `enum` aus Options, `required`, `additionalProperties: false`, `x-agcowork-type` je Property).
- Use-Cases `Spaces/`: Create, Get, List, Update, Archive (Scopes: `read` für Lesen, `write` für Anlegen/Ändern, `admin` für Archivieren).
- Use-Cases `Collections/`: `CreateCollection` (Schema optional, `title` wird ergänzt; legt Standard-Views **nicht** an, das macht AP-008 per Hook/Event; hier Domain-Event `CollectionCreated` auslösen oder einen Erweiterungspunkt `ICollectionCreatedHook` vorsehen), `GetCollection`, `ListCollections` (je Space oder Workspace), `UpdateCollection` (Name, Description), `UpdateSchema` (additive Änderungen: Felder hinzufügen, Name/Description/Required/Options/Default ändern, Felder entfernen mit `confirmRemoval: true`; `SchemaVersion++`), `DescribeCollection` (Schema + JSON Schema + Vorlagen + Beispielwert je Property + Statistik-Platzhalter), `ArchiveCollection`; Scope `schema` für Schemaänderungen, `write` für Anlegen.
- Use-Cases `Collections/Templates/`: Create, Update, Delete, List (Scope `schema`); `PropertiesPreset` wird gegen das Schema validiert.
- Hilfen für Folgepakete: `ISchemaProvider.GetSchemaAsync(collectionId)` mit Cache pro Request.

*Infrastructure*
- Konfigurationen `spaces`, `collections` (Schema als `jsonb`, GIN nicht nötig), `templates`; Migration `AddSchema`. Indizes beginnen mit `workspace_id`; `collections(workspace_id, space_id, name)` unique.

*Api*
- `/w/{slug}/spaces` (GET, POST), `/w/{slug}/spaces/{id}` (GET, PATCH), `/w/{slug}/spaces/{id}/archive` (POST).
- `/w/{slug}/collections` (GET mit `?spaceId=`, POST), `/w/{slug}/collections/{id}` (GET, PATCH), `/w/{slug}/collections/{id}/schema` (GET, PATCH), `/w/{slug}/collections/{id}/describe` (GET), `/w/{slug}/collections/{id}/archive` (POST), `/w/{slug}/collections/{id}/templates` (GET, POST), `/w/{slug}/collections/{id}/templates/{templateId}` (PATCH, DELETE).
- OpenAPI-Beschreibungen so schreiben, dass ein Agent sie direkt als Tool-Beschreibung nutzen könnte (AP-011 übernimmt sie).

*Tests*
- Domain: Key-Regex, reservierte Keys, Options-Eindeutigkeit, erlaubte Typänderungen.
- Application: Validator je Typ (gültig/ungültig, `allowed`-Listen), Normalizer, JSON Schema (Snapshot gegen erwartete Struktur), `UpdateSchema` (additiv, Entfernen nur mit Bestätigung, Version++).
- Api: alle Endpunkte happy path + Validierung; **Isolation** über die Fixture aus AP-005 für jeden Endpunkt; viewer bekommt 403 bei POST; `schema`-Scope nötig für Schemaänderung.

**Ausdrücklich nicht drin:**
- Records, Revisionen, Activity (AP-007), Views und Standard-Views (AP-008), Filter-DSL (AP-009), Workspace-Vorlage (AP-010), MCP (AP-011), UI.
- Relation-/Actor-Existenzprüfung gegen echte Records (Interface hier, Implementierung AP-007).

## Fachlicher Kontext

- Beispiel-Sammlungen in Architektur §2 („Sammlungen der Standard-Vorlage“) zeigen, welche Property-Typen und Optionen vorkommen müssen; die Vorlage selbst kommt in AP-010.
- Beschreibungen sind für Agenten geschrieben (§4.1 „Orientierung zuerst“). `Description` der Collection ist Pflicht; der Validierungsfehler soll sagen, warum.
- Fehler strukturiert mit `allowed`, damit ein Agent sich korrigiert.
- Alle Entitäten `ITenantEntity`; Isolationstests für jeden Endpunkt sind Pflicht (CLAUDE.md).

## Akzeptanzkriterien

1. Agent mit `write` legt Space und Collection mit Schema an; `GET …/describe` liefert Schema, JSON Schema (gültiges Draft 2020-12, `additionalProperties: false`, `enum` aus Options, Beschreibungen) und Vorlagen.
2. Ungültiges Schema (doppelter Key, Key `title` mit anderem Typ, relation auf fremde Collection, unbekannter Typ) → 400 mit `errors[]` inkl. `allowed` für den Typ.
3. `PropertyValueValidator` lehnt für jeden Typ ungültige Werte mit passendem Code ab und akzeptiert gültige; unbekannte Keys liefern `unknown_property` mit `allowed`.
4. `PATCH …/schema` fügt Felder hinzu (Version++), lehnt Umbenennung von Keys und nicht erlaubte Typänderungen ab, entfernt Felder nur mit `confirmRemoval: true`.
5. Vorlage mit Preset, das nicht zum Schema passt → 400.
6. Isolation: Jeder Endpunkt liefert für fremden Workspace 404; viewer → 403 bei schreibenden Endpunkten; ohne `schema`-Scope → 403 bei Schemaänderung.
7. `dotnet build` ohne Warnungen, `dotnet test` grün, `openapi/v1.json` neu generiert und eingecheckt (Pipeline aus AP-013).

## Tests, die mindestens entstehen

- `tests/Agcowork.Domain.Tests/Collections/{PropertyDefinitionTests,CollectionSchemaTests}.cs`
- `tests/Agcowork.Application.Tests/Schema/{PropertyValueValidatorTests,JsonSchemaBuilderTests,UpdateSchemaTests}.cs`
- `tests/Agcowork.Api.Tests/{Spaces,Collections}/…EndpointsTests.cs`, `…IsolationTests.cs`

## Entscheidungen, die der Implementierer treffen darf

- Repräsentation von `PropertyDefinition` als Record-Hierarchie vs. flaches Record mit optionalen Feldern (JSONB-Serialisierung muss stabil und versionierbar sein; `$type`-Discriminator vermeiden).
- Domain-Event vs. Hook-Interface für „Collection angelegt“ (AP-008 hängt sich daran).
- JSON-Schema-Bibliothek (nur zum Erzeugen; `JsonObject` von Hand ist erlaubt) und ob JSON Schema im Test gegen einen Validator geprüft wird.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
| 2026-11-07 | an implementer-opus übergeben |
| 2026-11-07 | Bericht: 474 Tests grün (152/184/138), 0 Warnungen; 17 Endpunkte mit Isolationstests; zusammengesetzte FKs `(workspace_id, space_id)` / `(workspace_id, collection_id)`; `schema_version` als Concurrency-Token; JSONB-Hülle `{version:1, properties:[…]}`; PATCH /schema als `{add, update, remove, confirmRemoval, expectedSchemaVersion}`; JSON Schema gegen Draft-2020-12-Metaschema getestet (JsonSchema.Net, nur Tests); `ICollectionCreatedHook` für AP-008; `IRelationTargetChecker` als Platzhalter für AP-007. Abweichungen: ListTemplates = read; Update/Archive Collection = schema; Relation-Ziel unveränderlich; Option-Farben aus Palette |
| 2026-11-07 | an reviewer übergeben |
| 2026-11-07 | Review: Freigabe mit Hinweisen; Tenancy ohne Befund. Wichtig: leere Pflichtwerte akzeptiert; `schema_version`-Token → 500 in UpdateCollection/ArchiveCollection bei Race; destruktive Schemaänderungen (Options entfernen, precision senken, markdown→text) machen Werte still ungültig, kein Hook für Folgepakete. Hinweise: Hook-Persistenztest, Platzhalter im describe-Beispiel, `null` nicht im JSON Schema, Raw-JSON-Snapshot, deaktivierte Actors als Wert, no-op erhöht Version |
| 2026-11-07 | Entscheidungen: destruktive Änderungen brauchen `confirmRemoval`; `ISchemaChangedHook` als Erweiterungspunkt. Nacharbeit an implementer-opus (9 Punkte) |
| 2026-11-07 | Nacharbeit fertig: 502 Tests grün. `ISchemaChangedHook` + `TemplatePresetSchemaHook`, `CollectionSave.SaveAsync` → 409 bei Token-Konflikt, destruktive Änderungen mit `confirmRemoval`, JSON Schema mit `minLength/minItems` und `null`-Typen, Raw-Snapshot-Test, `actor_inactive`, no-op ohne Versionssprung. Vom Orchestrator verifiziert. Abgeschlossen, Commit siehe Backlog |
