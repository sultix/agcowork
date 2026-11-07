# agcowork

Agent-first Wissens- und Arbeitsraum (Notion-Ersatz) mit Multi-Workspace.
Backend .NET 10 (Minimal APIs, EF Core, PostgreSQL, MCP-C#-SDK, SignalR), Frontend Angular 22.

Sprache: Dokumentation und Kommunikation Deutsch. Code, Bezeichner, Kommentare, Commits Englisch.

## Pflichtlektüre

- `docs/01-PLANUNG.md`: Vision, Anforderungen, Leitentscheidungen E1–E8, Roadmap, offene und getroffene Entscheidungen.
- `docs/02-ARCHITEKTUR.md`: Domänenmodell, Tenancy (§2a), Views, MCP/REST, Backend- und Frontend-Struktur.
- `docs/03-WORKFLOW.md`: wie wir entwickeln (Orchestrator, Implementierer, Reviewer, Arbeitspakete).
- `docs/pakete/`: Arbeitspakete. `TEMPLATE.md` ist die Vorlage, `BACKLOG.md` der Stand.

## Rollen

| Rolle | Wer | Aufgabe |
|---|---|---|
| Orchestrator | Hauptsession (Fable 5.1) | Plant, schneidet Arbeitspakete, wählt Implementierer, startet Reviews parallel, entscheidet über Befunde, committet, hält Docs und Backlog aktuell. Schreibt selbst nur Kleinigkeiten. |
| `implementer-opus` | Subagent, Opus | Anspruchsvolle Pakete: Domänenmodell, Tenancy, Sicherheit, Filter-DSL, MCP-Tools, erste Ausprägung eines neuen Musters. |
| `implementer-sonnet` | Subagent, Sonnet | Pakete mit Referenzbeispiel: weitere Endpunkte/Tools nach dem ersten, UI-Komponenten nach festgelegtem Design, Tests, Migrationen, Boilerplate, Doku. |
| `reviewer` | Subagent, Opus, nur lesend | Prüft jedes abgeschlossene Paket gegen Paket und Architektur. Läuft parallel zur nächsten Implementierung. |

Definitionen in `.claude/agents/`. Jedes Paket nennt im Kopf, welcher Implementierer es bekommt.

## Ablauf pro Paket (Kurzform, Details in `docs/03-WORKFLOW.md`)

1. Orchestrator schreibt `docs/pakete/AP-NNN-<titel>.md` aus der Vorlage und trägt es im Backlog ein.
2. Implementierer bekommt nur den Pfad des Pakets plus Hinweise, setzt um, testet, berichtet.
3. Orchestrator startet `reviewer` auf das Paket und parallel den nächsten Implementierer.
4. Blocker gehen per Nachricht an denselben Implementierer zurück (Kontext bleibt erhalten).
5. Grün gebaut, grün getestet, keine Blocker: Orchestrator committet (`feat(AP-NNN): …`), setzt Status im Backlog, aktualisiert Docs, falls sich Entscheidungen geändert haben.

Zwei Implementierer gleichzeitig nur auf disjunkten Dateien; sonst nacheinander.

## Unverhandelbar

- **Mandantentrennung.** Jede workspace-gebundene Entität trägt `WorkspaceId`. Jede Abfrage läuft im Tenant-Kontext. Jeder neue Endpunkt und jedes MCP-Tool bekommt einen Isolationstest (fremde ID → 404).
- **Adapter sind dünn.** Fachlogik liegt im Application Layer. REST, MCP und SignalR rufen dieselben Use-Cases.
- **Actor aus der Authentifizierung**, nie aus dem Request. Keys nur gehasht.
- **Tests gehören zum Paket.** Akzeptanzkriterien werden zu Tests. Rote Tests sind kein Abschluss.
- **Markdown ist kanonisch** für Inhalte (E2). Keine Block-Modelle.
- **Agent-Tauglichkeit.** MCP-Tools, Collections und Properties haben Beschreibungen, die für Agenten geschrieben sind. Fehler sind strukturiert.

## Konventionen

### .NET
- .NET 10, C# aktuell, `Nullable` und `ImplicitUsings` an, file-scoped Namespaces, `record` für Werte und DTOs.
- Ablage: alle C#-Projekte unter `src/backend/`, das Angular-Projekt direkt in `src/frontend/`, Tests unter `tests/`.
- Struktur: `Agcowork.Domain` → `Agcowork.Application` (Vertical Slices pro Modul) → `Agcowork.Infrastructure` → `Agcowork.Api`. Abhängigkeiten nur nach innen.
- Minimal APIs mit typisierten Results, OpenAPI generiert. EF Core mit Npgsql, Migrationen im Infrastructure-Projekt.
- IDs: UUIDv7 (`Guid.CreateVersion7()`). Zeit: `DateTimeOffset` UTC, `TimeProvider` injizieren.
- Tests: xUnit, Integration mit Testcontainers (Postgres). Testnamen `Method_Scenario_Expectation`.

### Angular
- Angular 22, Standalone Components, zoneless, Signals, `inject()` statt Konstruktor-Injection, `httpResource`/`resource()`, Signal Forms. Keine NgModules, kein zone.js.
- Feature-Ordner unter `src/app/features/<feature>`, geteilte Bausteine unter `src/app/shared`. Lazy Routes.
- API-Client aus OpenAPI generiert, Workspace-Präfix setzt der Client.
- Tests: Vitest für Units, Playwright für E2E.

### Befehle (ab Phase 0)
```
dotnet build                                   # Solution
dotnet test                                    # alle Tests
dotnet run --project src/backend/Agcowork.AppHost      # Aspire: Postgres + API + Angular
cd src/frontend && npm test                # Frontend-Units
```

### Git
- Ein Commit pro abgeschlossenem Paket, Conventional Commits mit Paketnummer: `feat(AP-012): add records query endpoint`.
- Nur der Orchestrator committet. Kein Push ohne ausdrückliche Anweisung.
