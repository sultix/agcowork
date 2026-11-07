# Backlog – Arbeitspakete

Status: angelegt · in Arbeit · im Review · Nacharbeit · abgeschlossen · zurückgestellt

| AP | Titel | Phase | Implementierer | Abhängig von | Status | Commit |
|---|---|---|---|---|---|---|
| [AP-001](AP-001-backend-geruest.md) | Backend-Gerüst (.NET 10, EF Core, PostgreSQL, Aspire) | 0 | implementer-opus | – | in Arbeit | |
| [AP-002](AP-002-angular-geruest.md) | Angular-Gerüst (Angular 22, zoneless, Tailwind, Vitest) | 0 | implementer-opus | – | abgeschlossen | babf915 |
| [AP-003](AP-003-aspire-verdrahtung-ci.md) | Aspire-Verdrahtung, CI und README | 0 | implementer-sonnet | AP-001, AP-002 | angelegt | |

## Zurückgestellte Review-Hinweise

| Quelle | Hinweis | Entscheidung |
|---|---|---|
| AP-002 Review | `HealthService` lädt `/health` nur einmal; startet das Backend später, bleibt „nicht erreichbar“ bis zum Reload | Phase 1: Polling oder Klick-Reload im Paket zur Kopfzeile/Statusanzeige |
| AP-002 Review | Jede 2xx-Antwort gilt als „ok“, auch `Degraded`; 503 erscheint als „nicht erreichbar“ | Phase 1: Health-JSON auswerten, drei Zustände anzeigen |
