# Backlog – Arbeitspakete

Status: geplant (nur Zeile, Paket folgt) · angelegt · in Arbeit · im Review · Nacharbeit · abgeschlossen · zurückgestellt

| AP | Titel | Phase | Implementierer | Abhängig von | Status | Commit |
|---|---|---|---|---|---|---|
| [AP-001](AP-001-backend-geruest.md) | Backend-Gerüst (.NET 10, EF Core, PostgreSQL, Aspire) | 0 | implementer-opus | – | abgeschlossen | 7d47dcb |
| [AP-002](AP-002-angular-geruest.md) | Angular-Gerüst (Angular 22, zoneless, Tailwind, Vitest) | 0 | implementer-opus | – | abgeschlossen | 926ec91 |
| [AP-003](AP-003-aspire-verdrahtung-ci.md) | Aspire-Verdrahtung, CI und README | 0 | implementer-sonnet | AP-001, AP-002 | abgeschlossen | 49ff518 |
| [AP-004](AP-004-identitaet.md) | Identität: Actors, API-Keys, Login, Auth-Pipeline, Instanz-Endpunkte | 1 | implementer-opus | AP-001 | angelegt | |
| AP-005 | Tenancy: Workspace, Membership, Tenant-Kontext, Query-Filter, Members-Endpunkte, Isolations-Fixture | 1 | implementer-opus | AP-004 | geplant | |
| AP-006 | Spaces, Collections, Property-Schema, Validierung, JSON Schema, Record-Vorlagen | 1 | implementer-opus | AP-005 | geplant | |
| AP-007 | Records, Revisionen, Activity, optimistische Nebenläufigkeit, append | 1 | implementer-opus | AP-006 | geplant | |
| AP-008 | Views: CRUD, Standard-Views (table, raw) je Collection | 1 | implementer-sonnet | AP-006 | geplant | |
| AP-009 | Filter-DSL, Sortierung, Cursor-Paginierung, Abfrage-Endpunkt | 1 | implementer-opus | AP-007 | geplant | |
| AP-010 | Workspace-Vorlage „Software-Projekt“ und `empty`, Anlage aus Vorlage | 1 | implementer-sonnet | AP-006, AP-008 | geplant | |
| AP-011 | MCP-Server: Workspace- und Instanz-Tools, Resources, Auth, Isolationstests | 1 | implementer-opus | AP-009, AP-010 | geplant | |
| [AP-012](AP-012-editor-bausteine.md) | Frontend: Markdown-Editor und Raw-View als Bausteine | 1 | implementer-opus | AP-002 | angelegt | |
| AP-013 | Frontend: OpenAPI-Client, Login, Session, Workspace-Liste, Auth-Guard | 1 | implementer-sonnet | AP-005 | geplant | |
| AP-014 | Frontend: Space-Navigation, Collections, Dokument-View verdrahtet | 1 | implementer-sonnet | AP-007, AP-012, AP-013 | geplant | |
| AP-015 | Frontend: Tabellen-View mit Inline-Edit, Filter-Leiste, Property-Editoren | 1 | implementer-opus | AP-009, AP-013 | geplant | |
| AP-016 | Frontend: Aktivitätsfeed, Mitglieder, Agenten/Keys, Workspace-Verwaltung | 1 | implementer-sonnet | AP-007, AP-013 | geplant | |
| AP-017 | Frontend: Raw-View und Historie verdrahtet | 1 | implementer-sonnet | AP-007, AP-012, AP-013 | geplant | |

## Zurückgestellte Review-Hinweise

| Quelle | Hinweis | Entscheidung |
|---|---|---|
| AP-002 Review | `HealthService` lädt `/health` nur einmal; startet das Backend später, bleibt „nicht erreichbar“ bis zum Reload | Phase 1: Polling oder Klick-Reload im Paket zur Kopfzeile/Statusanzeige |
| AP-003 Review | `actions/*@v4` laufen auf Node 20, das GitHub 2026 abkündigt | Erledigt 2026-11-07: checkout v7, setup-node v7, setup-dotnet v6 |
| AP-001 Review | `/health/ready` öffnet pro Aufruf eine DB-Verbindung und ist wie `/openapi/v1.json` in Production ohne Auth erreichbar | Wenn Hosting entschieden ist: Reverse-Proxy/Rate-Limit; OpenAPI in Production ggf. hinter Auth. In Architektur §7 vermerkt |
| AP-002 Review | Jede 2xx-Antwort gilt als „ok“, auch `Degraded`; 503 erscheint als „nicht erreichbar“ | Phase 1: Health-JSON auswerten, drei Zustände anzeigen |
