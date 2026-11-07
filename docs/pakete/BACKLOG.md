# Backlog – Arbeitspakete

Status: geplant (nur Zeile, Paket folgt) · angelegt · in Arbeit · im Review · Nacharbeit · abgeschlossen · zurückgestellt

| AP | Titel | Phase | Implementierer | Abhängig von | Status | Commit |
|---|---|---|---|---|---|---|
| [AP-001](AP-001-backend-geruest.md) | Backend-Gerüst (.NET 10, EF Core, PostgreSQL, Aspire) | 0 | implementer-opus | – | abgeschlossen | 7d47dcb |
| [AP-002](AP-002-angular-geruest.md) | Angular-Gerüst (Angular 22, zoneless, Tailwind, Vitest) | 0 | implementer-opus | – | abgeschlossen | 926ec91 |
| [AP-003](AP-003-aspire-verdrahtung-ci.md) | Aspire-Verdrahtung, CI und README | 0 | implementer-sonnet | AP-001, AP-002 | abgeschlossen | 49ff518 |
| [AP-004](AP-004-identitaet.md) | Identität: Actors, API-Keys, Login, Auth-Pipeline, Instanz-Endpunkte | 1 | implementer-opus | AP-001 | abgeschlossen | 7680648 |
| [AP-005](AP-005-tenancy.md) | Tenancy: Workspace, Membership, Tenant-Kontext, Query-Filter, Members-Endpunkte, Isolations-Fixture | 1 | implementer-opus | AP-004 | abgeschlossen | 9ed0093 |
| [AP-006](AP-006-schema.md) | Spaces, Collections, Property-Schema, Validierung, JSON Schema, Record-Vorlagen | 1 | implementer-opus | AP-005 | abgeschlossen | b0ad4fd |
| [AP-007](AP-007-records.md) | Records, Revisionen, Activity, optimistische Nebenläufigkeit, append | 1 | implementer-opus | AP-006 | abgeschlossen | aa97706 |
| [AP-008](AP-008-views.md) | Views: CRUD, Standard-Views (table, raw) je Collection | 1 | implementer-sonnet | AP-006 | abgeschlossen | 26c830f |
| [AP-009](AP-009-filter-dsl.md) | Filter-DSL, Sortierung, Cursor-Paginierung, Abfrage-Endpunkt | 1 | implementer-opus | AP-007, AP-008 | abgeschlossen | 0189d8a |
| [AP-010](AP-010-workspace-vorlage.md) | Workspace-Vorlage „Software-Projekt“ und `empty`, Anlage aus Vorlage | 1 | implementer-sonnet | AP-006, AP-008 | abgeschlossen | 7d1faa5 |
| [AP-011](AP-011-mcp-server.md) | MCP-Server: Workspace- und Instanz-Tools, Resources, Auth, Isolationstests | 1 | implementer-opus | AP-009, AP-010 | abgeschlossen | (folgt) |
| [AP-012](AP-012-editor-bausteine.md) | Frontend: Markdown-Editor und Raw-View als Bausteine | 1 | implementer-opus | AP-002 | abgeschlossen | 2fc5c4c |
| [AP-013](AP-013-frontend-client-login.md) | Frontend: OpenAPI-Client, Login, Session, Auth-Guard | 1 | implementer-opus | AP-004 | abgeschlossen | 72abfeb |
| [AP-014](AP-014-frontend-dokument.md) | Frontend: Space-Navigation, Collections, Record-Liste, Dokument-View, Property-Editoren | 1 | implementer-sonnet | AP-007, AP-012, AP-013, AP-016 | abgeschlossen | ccf39fb |
| [AP-015](AP-015-frontend-tabelle.md) | Frontend: Tabellen-View mit Inline-Edit, Filter-Leiste, View-Wechsel, Board | 1 | implementer-opus | AP-009, AP-014 | im Review | |
| [AP-016](AP-016-frontend-verwaltung.md) | Frontend: Workspace-Liste, Workspace-Verwaltung, Mitglieder, Agenten/Keys | 1 | implementer-sonnet | AP-005, AP-013 | abgeschlossen | 45c5e25 |
| [AP-017](AP-017-frontend-raw-historie.md) | Frontend: Raw-View (bearbeitbar), Historie mit Diff, Aktivitätsfeed | 1 | implementer-sonnet | AP-007, AP-012, AP-013, AP-014 | abgeschlossen | 7492762 |

## Zurückgestellte Review-Hinweise

| Quelle | Hinweis | Entscheidung |
|---|---|---|
| AP-002 Review | `HealthService` lädt `/health` nur einmal; startet das Backend später, bleibt „nicht erreichbar“ bis zum Reload | Phase 1: Polling oder Klick-Reload im Paket zur Kopfzeile/Statusanzeige |
| AP-012 Review | Markdown-Bilder laden als `<img src>` von fremder Herkunft (Tracking/IP-Leak) | Entschieden: Platzhalter-NodeView (alt + URL-Text), umgesetzt in AP-014-Nacharbeit; Bild-Proxy Phase 3 |
| AP-012 Review | Monaco-Adapter ohne Test; `token()` liest nur 6-stellige Hex-Werte; drei verwaiste CSS-Chunks (3×186 kB) im Build | Phase 2: Adapter-Test im Browser (Playwright), Token-Parser erweitern, wenn Tokens auf oklch wechseln |
| AP-007 Bericht | Activity `Seq` ist Identity: Leser in kurzen Abständen können Events überspringen, deren Transaktion später committet | Phase 2 (SignalR/Outbox): Outbox mit Commit-Reihenfolge oder `pg_current_xact_id`-Fenster |
| AP-007 Bericht | Migration `AddRecords` braucht Extension `btree_gin` (in postgres:17 enthalten) | Beim Hosting-Entscheid prüfen, ob die gehostete DB sie erlaubt |
| AP-007 Review | `RecordValuesSchemaHook` bereinigt ohne Revision (dokumentierte Ausnahme, Event zählt `affectedRecords`) | Phase 2 optional: Revision `schema_cleanup` je Record |
| AP-007 Review | `idempotency_keys` werden nie aufgeräumt; `LoadParentAsync` bis 32 Abfragen; Frontmatter ohne YAML-1.1-Sonderfälle (`0x1F`, `.inf`) | Phase 2: Cleanup-Job, rekursive CTE, YAML-Quoting erweitern |
| AP-006 Review | `UpdateTemplate` und `UpdateSchema` ohne gemeinsames Concurrency-Token: entfernter Key kann im Preset bleiben | AP-007 prüft Presets beim Anlegen erneut; Phase 2 ggf. Token an Templates |
| AP-006 Bericht | Self-Relations (z. B. `supersedes` in Entscheidungen) brauchen beim Anlegen die eigene Collection-ID | AP-010: Vorlage legt Collection erst ohne Self-Relation an und ergänzt sie per `UpdateSchema`, oder baut die Domain direkt |
| AP-006 Bericht | Body-Records aus AP-004/005 erscheinen in OpenAPI mit allen Feldern als required | Kleinpaket: Default-Werte an Body-Records (Muster AP-006), danach `schema.d.ts` neu erzeugen |
| AP-006 Bericht | Architektur §2 nennt `DefaultTemplateId`, umgesetzt ist `Template.IsDefault`; §4.3 ohne `describe`-Route | Orchestrator zieht Doku nach dem Review nach |
| AP-011 Review | `collection_describe` ruft zwei Use-Cases ohne gemeinsamen Snapshot; `ListActivity` nicht nach Record filterbar | Phase 2: `history_get` mit Activity je Record, Use-Case zusammenführen |
| AP-009 Bericht | Nach `relation.multiple` false→true bleiben Skalar-Altwerte stehen; `RecordValuesSchemaHook` greift nicht; nächster PATCH scheitert am Validator | Kleinpaket Backend: Hook wandelt Skalar → Array bei dieser Typänderung (Muster select→multi_select) |
| AP-015 Bericht | `QueryViewBody` nimmt keine `sorts`/`fields`; UI-Arbeitsstand läuft deshalb über den Collection-Query; `ViewDto` ohne Spaltenbreiten; kein Bulk-Archivieren; Titel-Property der Vorlage heißt „Title“ | Phase 2 Kleinpakete: View-Query mit sorts/fields-Override, `columnWidths` im View, `POST /records/archive` (Bulk), Vorlage „Titel“ |
| AP-014/017 Bericht | `ApiClient` kennt keine Request-Header; `Idempotency-Key` wird beim Anlegen nicht gesendet; Activity-Events zu `record.updated` tragen keinen Titel (Feed zeigt nur „Öffnen“) | Kleinpaket Frontend: Header-Option im ApiClient + Idempotency-Key bei POST /records; Backend: `targetTitle` im ActivityEvent-DTO |
| AP-016 Bericht | Instanz-Admin sieht unter `/admin/workspaces` nur eigene Mitgliedschaften; kein Endpunkt für alle Workspaces | Backend-Kleinpaket: `GET /api/v1/admin/workspaces` (instance:admin, IgnoreQueryFilters begründet) + Beitritt als Owner |
| AP-016 Bericht | Nicht-Admins können Mitglieder nur per Actor-ID einladen; keine Actor-Suche | Backend-Kleinpaket: `GET /w/{slug}/members/candidates?q=` oder `GET /actors?q=` mit eingeschränkten Feldern für Workspace-Admins |
| AP-013 Review | `openapi-typescript` 7.13 verlangt TypeScript ^5, Projekt nutzt TS 6; npm-`overrides` erzwingt TS 6 | Bei jedem Update von openapi-typescript oder TypeScript prüfen, Override entfernen sobald Peer-Range passt |
| AP-004 Review | Kein Weg, ein Passwort nachträglich zu setzen (Reset, Änderung); Bootstrap-Passwort bleibt dauerhaft gültig | Phase 2: `PATCH /me/password`, Admin-Reset; Bootstrap-Admin nach erstem Login zum Ändern auffordern |
| AP-004 Review | Cookies zustandslos (Logout entwertet abgegriffenes Cookie nicht); DataProtection-Keys nicht persistiert (Sessions weg nach Neustart) | Betrieb/Deploy: DataProtection-Keys in DB oder Volume persistieren; Session-Revocation bei Bedarf |
| AP-004 Review | Bootstrap mit mehreren Instanzen gleichzeitig → zweite stürzt ab | niedrig, Single-Instance-Annahme |
| AP-012 Bericht | Änderungen im Debounce-Fenster gehen bei Destroy des Editors verloren | AP-014: vor Navigation `getMarkdown()` abholen oder bei Blur speichern |
| AP-012 Bericht | Aufgaben-Checkboxen im WYSIWYG nicht klickbar; Milkdown vergibt Überschriften-IDs aus dem Text | Phase 2 Komfort; ID-Präfix prüfen |
| AP-003 Review | `actions/*@v4` laufen auf Node 20, das GitHub 2026 abkündigt | Erledigt 2026-11-07: checkout v7, setup-node v7, setup-dotnet v6 |
| AP-001 Review | `/health/ready` öffnet pro Aufruf eine DB-Verbindung und ist wie `/openapi/v1.json` in Production ohne Auth erreichbar | Wenn Hosting entschieden ist: Reverse-Proxy/Rate-Limit; OpenAPI in Production ggf. hinter Auth. In Architektur §7 vermerkt |
| AP-002 Review | Jede 2xx-Antwort gilt als „ok“, auch `Degraded`; 503 erscheint als „nicht erreichbar“ | Phase 1: Health-JSON auswerten, drei Zustände anzeigen |
