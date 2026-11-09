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
| [AP-011](AP-011-mcp-server.md) | MCP-Server: Workspace- und Instanz-Tools, Resources, Auth, Isolationstests | 1 | implementer-opus | AP-009, AP-010 | abgeschlossen | 2346906 |
| [AP-012](AP-012-editor-bausteine.md) | Frontend: Markdown-Editor und Raw-View als Bausteine | 1 | implementer-opus | AP-002 | abgeschlossen | 2fc5c4c |
| [AP-013](AP-013-frontend-client-login.md) | Frontend: OpenAPI-Client, Login, Session, Auth-Guard | 1 | implementer-opus | AP-004 | abgeschlossen | 72abfeb |
| [AP-014](AP-014-frontend-dokument.md) | Frontend: Space-Navigation, Collections, Record-Liste, Dokument-View, Property-Editoren | 1 | implementer-sonnet | AP-007, AP-012, AP-013, AP-016 | abgeschlossen | ccf39fb |
| [AP-015](AP-015-frontend-tabelle.md) | Frontend: Tabellen-View mit Inline-Edit, Filter-Leiste, View-Wechsel, Board | 1 | implementer-opus | AP-009, AP-014 | abgeschlossen | dafa127 |
| [AP-016](AP-016-frontend-verwaltung.md) | Frontend: Workspace-Liste, Workspace-Verwaltung, Mitglieder, Agenten/Keys | 1 | implementer-sonnet | AP-005, AP-013 | abgeschlossen | 45c5e25 |
| [AP-017](AP-017-frontend-raw-historie.md) | Frontend: Raw-View (bearbeitbar), Historie mit Diff, Aktivitätsfeed | 1 | implementer-sonnet | AP-007, AP-012, AP-013, AP-014 | abgeschlossen | 7492762 |
| [AP-018](AP-018-docker-compose.md) | Docker Compose für den lokalen Start ohne Aspire (Postgres, API, Nginx + Angular) | Betrieb | implementer-sonnet | AP-011 | abgeschlossen | f1c615a |
| [AP-019](AP-019-ui-fundament-spartan.md) | UI-Fundament: spartan/ui (shadcn), Theme neutral, Shell, Referenz-Migration | UI | implementer-opus | AP-012–AP-017 | abgeschlossen | eda627c |
| [AP-020](AP-020-ui-migration-verwaltung.md) | UI-Migration: Verwaltung, Spaces, Collections, Aktivität | UI | implementer-sonnet | AP-019 | abgeschlossen | 5463f97 |
| [AP-021](AP-021-ui-migration-daten.md) | UI-Migration: Records, Views, Tabelle, Filter, Property-Editoren | UI | implementer-sonnet | AP-019 | abgeschlossen | 559d0a2 |
| [AP-022](AP-022-haertung-backend.md) | Härtung Backend: Passwort ändern/zurücksetzen, Sitzungen entwerten, relation-Hook, OpenAPI-Defaults, Activity-Titel | Härtung | implementer-opus | AP-004–AP-011 | abgeschlossen | 183275b |
| [AP-023](AP-023-haertung-frontend.md) | Härtung Frontend: Passwort-UI, Idempotency-Key, Activity-Titel, Health-Status, dompurify | Härtung | implementer-sonnet | AP-022 | abgeschlossen | 6e837db |
| [AP-024](AP-024-realtime-backend.md) | Realtime Backend: commit-sichere Activity-Reihenfolge (`afterSeq`), SignalR-Hub, Dispatcher | 2 | implementer-opus | AP-022 | abgeschlossen | 6bd7d04 |
| [AP-025](AP-025-realtime-frontend.md) | Realtime Frontend: SignalR-Client, Live-Feed, Views, Navigation, Dokument ohne Datenverlust | 2 | implementer-opus | AP-023, AP-024 | abgeschlossen | 0cbeecc |
| [AP-026](AP-026-volltextsuche-backend.md) | Volltextsuche Backend: tsvector, REST `/search`, MCP `search` | 2 | implementer-opus | AP-024 | abgeschlossen | 923b736 |
| [AP-027](AP-027-volltextsuche-frontend.md) | Volltextsuche Frontend: Suchfeld, Ergebnisseite, Hervorhebung | 2 | implementer-sonnet | AP-025, AP-026 | abgeschlossen | 00615d6 |
| [AP-028](AP-028-dispatcher-fehlerpfad-surrogate.md) | Backend-Kleinpaket: Dispatcher „lückenlos oder getrennt“, ungepaarte Surrogate → 400 | 2 | implementer-opus | AP-024, AP-025 | abgeschlossen | eefd290 |
| [AP-029](AP-029-kommentare-backend.md) | Kommentare und Erwähnungen Backend: Domäne, Use-Cases, REST, MCP, Activity | 2 | implementer-opus | AP-028 | abgeschlossen | df7f195 |
| [AP-030](AP-030-realtime-frontend-nachzug.md) | Frontend-Kleinpaket Realtime: Reconnect-Backoff, Record-Zähler, Live für Mitglieder/Views/Schema | 2 | implementer-opus | AP-025, AP-028 | abgeschlossen | ff77388 |
| [AP-031](AP-031-kommentare-frontend.md) | Kommentare Frontend: Seitenleiste, Threads, `@`-Auswahl, live | 2 | implementer-opus | AP-029, AP-030 | abgeschlossen | 821449e |
| [AP-032](AP-032-erwaehnungen-posteingang.md) | Erwähnungen Frontend: Posteingang, Badge, gelesen markieren | 2 | implementer-sonnet | AP-029 | abgeschlossen | 6661e5e |
| [AP-033](AP-033-null-felder-im-vertrag.md) | API-Vertrag: `null`-Felder mitsenden statt weglassen (Fund im Rauchtest) | 2 | implementer-opus | AP-029–AP-032 | abgeschlossen | 321151d |
| [AP-034](AP-034-haertung-frontend-body.md) | Härtung Frontend: Record-Body gegen präparierten Markdown, Kommentare im Feed, Fehlertexte | 2 | implementer-opus | AP-031, AP-033 | abgeschlossen | b60c8ee |
| [AP-035](AP-035-haertung-backend-idempotenz-erwaehnungen.md) | Härtung Backend: Idempotenz mit Inhaltsprüfung, Erwähnungen in Zitaten, Activity je Record, Schema-Hooks | 2 | implementer-opus | AP-033 | abgeschlossen | e13d5e2 |
| [AP-036](AP-036-row-level-security.md) | PostgreSQL Row-Level-Security als zweites Netz (Rolle `agcowork_app`, Policies, Instanz-Bereich) | 2 | implementer-opus | AP-035 | abgeschlossen | 5a6708a |
| [AP-037](AP-037-relationen-backend.md) | Relationen Backend: Titel-Lookup, Rückverweise, `expandRelations`/`includeBacklinks`, archivierte Ziele | 2 | implementer-opus | AP-036 | abgeschlossen | 8a5cf82 |
| [AP-038](AP-038-relationen-frontend.md) | Relationen Frontend: Titel-Cache, Chips als Links, Picker mit Serversuche, Rückverweise im Dokument | 2 | implementer-opus | AP-037, AP-039 | abgeschlossen | 7067bed |
| [AP-039](AP-039-schema-editor.md) | Schema-Editor im Frontend: Properties aller Typen inkl. Relationen anlegen, ändern, entfernen | 2 | implementer-opus | – | abgeschlossen | ba449a7 |
| [AP-040](AP-040-galerie-und-liste.md) | Galerie- und Listen-View im Frontend (Cover ohne Bilder, Felder, Tastatur, live) | 2 | implementer-sonnet | AP-038, AP-039 | abgeschlossen | a779493 |
| [AP-041](AP-041-vorlagen-verwaltung.md) | Vorlagen-Verwaltung im Frontend: Record-Vorlagen anlegen, bearbeiten, Standard, aus Record übernehmen | 2 | implementer-sonnet | AP-039, AP-040 | in Arbeit | |

## Zurückgestellte Review-Hinweise

| Quelle | Hinweis | Entscheidung |
|---|---|---|
| AP-019 Review | `npm audit --omit=dev`: kritischer Fund `dompurify` unter `monaco-editor` (bestand schon vor AP-019); `@spartan-ng/cli` bringt Nx und einen high-Fund (`zod`, nur dev) | AP-023: `overrides` dompurify ^3.4.17 beruhigt nur `npm audit`; Monaco 0.57 bündelt eine **eigene Kopie** (`esm/vs/base/browser/dompurify`, 3.4.15), die weiter ausgeliefert wird. Advisories betreffen `IN_PLACE`/`SAFE_FOR_TEMPLATES`/XHTML, Monaco nutzt sie nicht (nicht belegt). Offen: Monaco ≥ 0.58 (bringt 3.4.17), sobald stabil; CLI-Fund beobachten |
| AP-019 Review | Theme-Aufblitzen beim Laden (Inline-Skript in `index.html`) nicht automatisiert getestet; bei künftiger CSP braucht das Skript Hash/Nonce | Mit dem ersten Playwright-Paket |
| AP-019 Review | Brain setzt `aria-invalid` fest auf `control.invalid` (schon vor der ersten Eingabe), roter Rahmen erst bei ungültig+berührt; nicht konfigurierbar | Upstream-Issue bei spartan oder eigene Brain-Erweiterung, wenn ein A11y-Paket ansteht |
| AP-020 Review | Brain-Popover setzt `role="dialog"` am Overlay ohne Label-Input; Name nur als `role="group"` innerhalb | mit dem aria-invalid-Punkt als Upstream-Issue bei spartan |
| AP-020 Bericht | Schema-Übersicht der Collection als `<details>`, kein Helm-Collapsible erzeugt | bei Bedarf `ng g @spartan-ng/cli:ui collapsible` und umstellen |
| AP-021 Review | Spalten-Panel (Spalten ein-/ausblenden) ohne Test; kein Test für `lucideKanban` im Board-Tab | mit dem nächsten Paket an `view-page` nachziehen |
| AP-019 Review | `HlmSidebarService` schreibt Cookie `sidebar_state` (`path=/`), geht bei jedem API-Request mit | unkritisch; bei Cookie-Policy-Änderungen beachten |
| AP-002 Review | `HealthService` lädt `/health` nur einmal; startet das Backend später, bleibt „nicht erreichbar“ bis zum Reload | → AP-023 |
| AP-012 Review | Markdown-Bilder laden als `<img src>` von fremder Herkunft (Tracking/IP-Leak) | Entschieden: Platzhalter-NodeView (alt + URL-Text), umgesetzt in AP-014-Nacharbeit; Bild-Proxy Phase 3 |
| AP-012 Review | Monaco-Adapter ohne Test; `token()` liest nur 6-stellige Hex-Werte; drei verwaiste CSS-Chunks (3×186 kB) im Build | Token-Parser erledigt in AP-019 (`css-color.ts`, oklch → Hex). Offen Phase 2: Adapter-Test im Browser (Playwright), verwaiste CSS-Chunks |
| AP-007 Bericht | Activity `Seq` ist Identity: Leser in kurzen Abständen können Events überspringen, deren Transaktion später committet | → AP-024 |
| AP-007 Bericht | Migration `AddRecords` braucht Extension `btree_gin` (in postgres:17 enthalten) | Beim Hosting-Entscheid prüfen, ob die gehostete DB sie erlaubt |
| AP-007 Review | `RecordValuesSchemaHook` bereinigt ohne Revision (dokumentierte Ausnahme, Event zählt `affectedRecords`) | Phase 2 optional: Revision `schema_cleanup` je Record |
| AP-007 Review | `idempotency_keys` werden nie aufgeräumt; `LoadParentAsync` bis 32 Abfragen; Frontmatter ohne YAML-1.1-Sonderfälle (`0x1F`, `.inf`) | Phase 2: Cleanup-Job, rekursive CTE, YAML-Quoting erweitern |
| AP-006 Review | `UpdateTemplate` und `UpdateSchema` ohne gemeinsames Concurrency-Token: entfernter Key kann im Preset bleiben | AP-007 prüft Presets beim Anlegen erneut; Phase 2 ggf. Token an Templates |
| AP-006 Bericht | Self-Relations (z. B. `supersedes` in Entscheidungen) brauchen beim Anlegen die eigene Collection-ID | AP-010: Vorlage legt Collection erst ohne Self-Relation an und ergänzt sie per `UpdateSchema`, oder baut die Domain direkt |
| AP-006 Bericht | Body-Records aus AP-004/005 erscheinen in OpenAPI mit allen Feldern als required | → AP-022 |
| AP-006 Bericht | Architektur §2 nennt `DefaultTemplateId`, umgesetzt ist `Template.IsDefault`; §4.3 ohne `describe`-Route | Orchestrator zieht Doku nach dem Review nach |
| AP-011 Review | `collection_describe` ruft zwei Use-Cases ohne gemeinsamen Snapshot; `ListActivity` nicht nach Record filterbar | Activity-Filter `targetId` → AP-024; Use-Case zusammenführen offen |
| AP-009 Bericht | Nach `relation.multiple` false→true bleiben Skalar-Altwerte stehen; `RecordValuesSchemaHook` greift nicht; nächster PATCH scheitert am Validator | → AP-022 |
| AP-022 Bericht | `ViewSchemaHook` und `TemplatePresetSchemaHook` behandeln select→multi_select, aber nicht `relation.multiple` false→true (skalare Presets, `eq`-Filter auf Relation) | → AP-035 |
| AP-022 Review | `PATCH /me/password` und `POST /auth/login` ohne Sperre/Rate-Limit (Brute-Force auf `currentPassword` mit gestohlenem Cookie, Dauer-Logins) | Rate-Limiting-Paket: beide Endpunkte ausdrücklich nennen |
| AP-022 Nacharbeit | In `McpText` werden Titel außer in `Activity` nicht escaped (`RecordLine` `**{Title}**`) | Kleinpaket MCP-Text, mit dem nächsten MCP-Paket |
| AP-024 Bericht | Hängende/„idle in transaction“-Transaktion im Cluster hält Vorwärtslesen und Realtime instanzweit an | Betrieb: `idle_in_transaction_session_timeout` in Compose/Hosting setzen |
| AP-024 Bericht | Dev-Proxy (`proxy.conf.js`) leitet `/w/{slug}/mcp` nicht weiter; `dotnet-ef` 10.0.11 vs. Runtime 10.0.14 | Kleinigkeit, beim nächsten Tooling-Paket |
| AP-025 Bericht | Live nicht abgedeckt: `member.*` → MembersStore (Namen im Feed), `view.*`/`collection.schema_changed` → Views-Liste und Schema im CollectionStore, Space-Seite selbst | → AP-030 |
| AP-025 Review | `ActivityDispatcher` (Z. ~144): wirft `PublishAsync` (DB-Fehler in Zugriffsprüfung/Titeln), ist der Batch verloren, Verbindungen bleiben offen; Client rückt mit nächstem Live-Ereignis vor und holt nie nach | → AP-028 |
| AP-026 Bericht | OpenAPI führt nullable Felder als required, der Serializer lässt `null` weg (z. B. `nextCursor`); `float` erscheint im TS-Typ als `number \| string` (projektweit) | → AP-033 (Rauchtest: Posteingang und Kommentare brachen an fehlendem `null`) |
| AP-026 Bericht | Suche: E-Mail/Host nur als ganzes Token, Bindestrich-Präfix (`foo-ba`) findet nichts, Positionen > 16383 bei Phrasen, Neuindexierung großer Collections in der UpdateSchema-Transaktion | Beobachten; bei Bedarf Tokenizer-Erweiterung bzw. Hintergrund-Reindex |
| AP-026 Review | Neuindexierung nach Schemaänderung läuft synchron in der UpdateSchema-Transaktion (Timeout bei großen Collections) | Folgepaket: Reindex im Hintergrund (Batch, Fortschritt), wenn Collections > ~2.000 große Records realistisch werden |
| AP-026 Nacharbeit | Body mit ungepaartem Surrogat → `DbUpdateException`/`EncoderFallbackException` bei `UpdateRecord` (vermutlich 500 statt 400); Body-Validierung prüft nicht wie `QueryText.IsStorable` | → AP-028 |
| AP-026 Review | Präfix-Defaults im Migrations-SQL (25.000 Properties) nicht per Test gegen `RecordSearch.PropertiesPrefixLength` geprüft | → AP-035 |
| AP-028 Bericht | NUL/Surrogate noch ungeprüft (vermutlich 500): Templates (Name, Body), Workspaces (Name), Actors, Member-Namen, API-Key-Namen, Login | → AP-028-Nacharbeit |
| AP-028 Review | Reconnect-Sturm bei dauerhaftem Zustellfehler: Frontend setzt `attempt = 0` direkt nach `start()` (`realtime.service.ts`), nächster Retry 0 ms plus Resync-Reloads | → AP-030 |
| AP-028 Review | MCP `resources/read`/`prompts/*` und Nicht-Objekt-`arguments` mit Surrogaten → nacktes JSON-RPC -32603; stdio-Proxy ohne Filter | beobachten, mit nächstem MCP-Paket |
| Rauchtest 2026-11-08 | Zähler „N Records“ im View-Kopf kommt aus `collection describe` (`statistics.recordCount`) und lädt bei Live-Ereignissen nicht neu (bleibt „0 Records“, Zeile erscheint) | → AP-030 |
| AP-029 Bericht | `GET /activity?targetId=<recordId>` und `activity_recent` finden Kommentar-Ereignisse eines Records nicht (`targetId` = Kommentar-ID, Record nur im Diff) | → AP-035 |
| AP-029 Review | Erwähnungs-Spam über Edits (Entfernen und Wiedereinfügen erzeugt neue ungelesene Mention); `workspace_overview` liefert Member-IDs nur im Text, nicht im Structured Content | beobachten; bei Bedarf Mention-Dedupe je (Kommentar, Actor) über Löschungen hinweg, Members in Structured Content |
| AP-031 Bericht | Aktivitätsfeed (`activity-entry.component.ts`, `EVENTS`) kennt `comment.*` nicht; `shared/ui/api-error-text.ts` kennt `collection_archived`/`space_archived` nicht | → AP-034 |
| AP-031 Review | Server-Parser `CommentMarkdown` kennt keine Blockquotes: Erwähnung in `> ``` …` oder `>     …` erzeugt Posteingang-Eintrag, UI zeigt Code | → AP-035 (Frontend-Port folgt) |
| AP-031 Review | Idempotency-Replay für `comment_add` (und `record_create`) vergleicht nur Key und Ziel, nicht den Inhalt; Frontend bindet Key an Text, Agenten könnten Key mit anderem Text wiederverwenden | → AP-035 |
| AP-031 Review | Präparierter Markdown (lange `*`/`>`-Läufe) blockiert remark/micromark rekursiv; Kommentar-Renderer abgesichert, **Record-Body über Milkdown vermutlich gleich betroffen** | → AP-034 |
| AP-035 Review | Migrationen auf großen Tabellen: `AddColumn` + Backfill + `CREATE INDEX` in einer Transaktion sperren die Tabelle (hier `activity_events`) für die ganze Dauer | Muster für künftige Migrationen: Spalte, Backfill in Batches, Index `CONCURRENTLY` (eigene Migration ohne Transaktion); in `03-WORKFLOW.md` aufnehmen |
| AP-035 Review | Frontend-Port `comment-mentions.ts` kennt Zitate noch nicht (Server zählt `> ```…` nicht, UI zeigt Chip); Thematic Breaks/Setext beenden Zitat bei Lazy Continuation im Server-Parser nicht (ohne Auswirkung auf Mentions) | Frontend-Kleinpaket: Port mit Zitaten, `MaxQuoteDepth = 32`, Stapel statt Rekursion, einmalige Suche nach `-->` (heute quadratisch auch im Port) |
| AP-034 Bericht | Editierbarer Modus kostet am Budget bis ~1,7 s (erstes Serialisieren); Debounce-Flush serialisiert große Dokumente voll (bis ~0,4 s) | Folgepaket: Parsen/Serialisieren im Web Worker mit Timeout oder inkrementelles Serialisieren |
| AP-036 Review | Eigener Login ohne Superuser (Schutz gegen kompromittierte DB-Zugangsdaten), dann ohne LEAKPROOF-Setzen; jedes Öffnen kostet einen Round-Trip für `set_config` (später Connection-Initializer der `NpgsqlDataSource`) | vor öffentlichem Hosting entscheiden |
| AP-039 Bericht | API kann `precision` nicht entfernen (`change.Precision ?? before.Precision`); erstmaliges Setzen gilt immer als Verlust; `fieldErrorMessage` ersetzt spezifische Servermeldungen durch generische (`not_allowed` → „Nicht erlaubt.“) | Backend-Kleinpaket Schema (Präzision entfernen, Verlust nur bei echter Rundung); Frontend: Server-`message` als Detail anzeigen |
| AP-038 Bericht | Kein `collection.restored`/`space.restored`-Ereignis: Archiv-Status im Titel-Cache nach Wiederherstellen eines Containers erst beim Resync korrekt; Rückverweise erkennen neue Quell-Collections außerhalb des Baums erst beim Resync | Backend: Restore-Ereignisse für Collection/Space (Use-Cases prüfen, ob Wiederherstellen existiert) |
| Rauchtest 2026-11-08 | Relations-Picker: Treffer sind Buttons ohne Pfeiltasten/`aria-activedescendant` (nur Tab erreichbar), anders als die `@`-Auswahl in Kommentaren | A11y-Kleinpaket: Picker als Combobox/Listbox nach Muster `mention-picker` |
| AP-040 Review | Backend prüft bei `cardCoverProperty` nur den Key, nicht den Typ (Agenten können relation/json/markdown setzen) | Backend-Kleinpaket Views: Cover-Typen auf select/status/multi_select/checkbox/text/url/email/number/date beschränken (400 mit `allowed`) |
| AP-015 Bericht | `QueryViewBody` nimmt keine `sorts`/`fields`; UI-Arbeitsstand läuft deshalb über den Collection-Query; `ViewDto` ohne Spaltenbreiten; kein Bulk-Archivieren; Titel-Property der Vorlage heißt „Title“ | Phase 2 Kleinpakete: View-Query mit sorts/fields-Override, `columnWidths` im View, `POST /records/archive` (Bulk), Vorlage „Titel“ |
| AP-014/017 Bericht | `ApiClient` kennt keine Request-Header; `Idempotency-Key` wird beim Anlegen nicht gesendet; Activity-Events zu `record.updated` tragen keinen Titel (Feed zeigt nur „Öffnen“) | → AP-022 (`targetTitle`), AP-023 (Header, Idempotency-Key, Feed) |
| AP-016 Bericht | Instanz-Admin sieht unter `/admin/workspaces` nur eigene Mitgliedschaften; kein Endpunkt für alle Workspaces | Backend-Kleinpaket: `GET /api/v1/admin/workspaces` (instance:admin, IgnoreQueryFilters begründet) + Beitritt als Owner |
| AP-016 Bericht | Nicht-Admins können Mitglieder nur per Actor-ID einladen; keine Actor-Suche | Backend-Kleinpaket: `GET /w/{slug}/members/candidates?q=` oder `GET /actors?q=` mit eingeschränkten Feldern für Workspace-Admins |
| AP-013 Review | `openapi-typescript` 7.13 verlangt TypeScript ^5, Projekt nutzt TS 6; npm-`overrides` erzwingt TS 6 | Bei jedem Update von openapi-typescript oder TypeScript prüfen, Override entfernen sobald Peer-Range passt |
| AP-004 Review | Kein Weg, ein Passwort nachträglich zu setzen (Reset, Änderung); Bootstrap-Passwort bleibt dauerhaft gültig | → AP-022/AP-023; Passwortänderung entwertet ältere Cookies |
| AP-004 Review | Cookies zustandslos (Logout entwertet abgegriffenes Cookie nicht) | Phase 2: Session-Revocation bei Bedarf. DataProtection-Keys: Compose-Volume in AP-018; für andere Deployments `PersistKeysToDbContext` prüfen |
| AP-018 Review | Web-Image läuft als root (nginx:alpine) | Später `nginxinc/nginx-unprivileged` mit `listen 8080` |
| AP-004 Review | Bootstrap mit mehreren Instanzen gleichzeitig → zweite stürzt ab | niedrig, Single-Instance-Annahme |
| AP-012 Bericht | Änderungen im Debounce-Fenster gehen bei Destroy des Editors verloren | AP-014: vor Navigation `getMarkdown()` abholen oder bei Blur speichern |
| AP-012 Bericht | Aufgaben-Checkboxen im WYSIWYG nicht klickbar; Milkdown vergibt Überschriften-IDs aus dem Text | Phase 2 Komfort; ID-Präfix prüfen |
| AP-003 Review | `actions/*@v4` laufen auf Node 20, das GitHub 2026 abkündigt | Erledigt 2026-11-07: checkout v7, setup-node v7, setup-dotnet v6 |
| AP-001 Review | `/health/ready` öffnet pro Aufruf eine DB-Verbindung und ist wie `/openapi/v1.json` in Production ohne Auth erreichbar | Wenn Hosting entschieden ist: Reverse-Proxy/Rate-Limit; OpenAPI in Production ggf. hinter Auth. In Architektur §7 vermerkt |
| AP-002 Review | Jede 2xx-Antwort gilt als „ok“, auch `Degraded`; 503 erscheint als „nicht erreichbar“ | → AP-023 |
