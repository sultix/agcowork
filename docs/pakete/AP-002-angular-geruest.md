# AP-002 – Angular-Gerüst (Angular 22, zoneless, Tailwind, Vitest)

| Feld | Wert |
|---|---|
| Status | angelegt |
| Phase | 0 |
| Implementierer | implementer-opus |
| Referenzbeispiel | – (erstes Auftreten) |
| Abhängig von | – (parallel zu AP-001, disjunkte Pfade) |
| Betroffene Pfade | ausschließlich `src/Agcowork.Web/` |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §6 Frontend; `CLAUDE.md` Konventionen Angular |

## Ziel

Nach diesem Paket existiert eine Angular-22-Anwendung mit App-Shell, Routing-Skelett nach
Architektur §6, Workspace-Kontext als Signal-Service, Tailwind-Theming mit Hell/Dunkel und
grünen Unit-Tests. Jedes spätere Frontend-Paket legt Features in diese Struktur.

## Scope

**Drin:**
- Projekt anlegen mit der aktuellen Angular CLI 22 per `npx`, Zielordner `src/Agcowork.Web`, Paketname `agcowork-web`, ohne SSR, zoneless, Standalone, Vitest als Test-Runner, npm. Kein eigenes Git-Repo im Unterordner.
- Tailwind CSS (aktuelle Hauptversion) eingebunden. Design-Tokens als CSS-Variablen in `styles.css` (Hintergrund, Fläche, Text, Akzent, Rand) mit Hell/Dunkel über `prefers-color-scheme` und manuellem Override per `data-theme`. Keine UI-Bibliothek (Entscheidung: Tailwind + eigene Komponenten, siehe `01-PLANUNG.md` offene Frage 8; der Orchestrator hat so entschieden).
- ESLint über `angular-eslint`, Prettier-Konfiguration. `npm run lint` grün.
- Ordnerstruktur: `src/app/core` (Services, Guards, Interceptors), `src/app/shared` (geteilte Bausteine), `src/app/features/<feature>` (Lazy Routes). Features in diesem Paket: `auth` (Login-Platzhalter), `workspaces` (Wechsler-Platzhalter mit statischer Liste), `workspace-shell` (Layout mit Seitenleiste, Kopfzeile, `<router-outlet>`).
- Routen nach Architektur §6: `/login`, `/workspaces`, `/w/:workspaceSlug` mit Kind-Routen-Platzhaltern (`spaces/:spaceId`, `search`, `activity`, `settings/members`), `**` → `/workspaces`. Alle Features lazy.
- `WorkspaceContext` in `core`: Signal-Service, der den aktiven Slug aus der Route ableitet (`toSignal` oder Router-Events) und als `readonly Signal<string | null>` bereitstellt; Komponenten lesen nur daraus.
- `core/api`: `API_BASE_URL` Injection-Token (Standard `/api/v1`), ein `HttpClient`-Provider mit `withFetch()`, ein `HealthService`, der `GET /health` per `httpResource` lädt. Die Kopfzeile zeigt den Backend-Status (ok / nicht erreichbar).
- Dev-Proxy `proxy.conf.json`: `/api` und `/health` auf `http://localhost:5000` (Ziel wird in AP-003 aus Aspire gesetzt); `npm start` nutzt den Proxy.
- Tests (Vitest): `WorkspaceContext` leitet Slug korrekt ab; Shell rendert mit Slug; Routen-Konfiguration enthält alle Pfade aus §6 (Snapshot oder explizite Prüfung). `npm test` grün, `npm run build` grün.

**Ausdrücklich nicht drin:**
- Echte Anmeldung, echte Workspace-Liste vom Backend, generierter OpenAPI-Client (Phase 1).
- Views (Table, Board, Document, Raw), Editoren, SignalR (Phase 1/2).
- Aspire-Anbindung, CI, README (AP-003).
- Dateien außerhalb von `src/Agcowork.Web/`.

## Fachlicher Kontext

- Konventionen aus `CLAUDE.md`: Standalone, zoneless, Signals, `inject()`, `httpResource`/`resource()`, keine NgModules, kein zone.js. Signal Forms nur, wenn ein Formular entsteht (hier nicht nötig).
- Node 24 und npm 11 sind installiert. Angular CLI ist nicht global installiert, `npx @angular/cli@22` verwenden.
- Der Workspace-Slug steckt in der URL, damit Links teilbar sind (`02-ARCHITEKTUR.md` §6, letzter Absatz).
- Das Backend läuft in AP-001 noch ohne feste Portnummer; der Proxy-Standard 5000 ist ein Platzhalter.

## Akzeptanzkriterien

1. `npm ci && npm run build` in `src/Agcowork.Web` läuft fehlerfrei; das Bundle enthält kein zone.js.
2. `npm test` grün mit mindestens den drei genannten Tests; `npm run lint` grün.
3. `npm start` liefert unter `/workspaces` den Wechsler, unter `/w/demo/spaces/1` die Shell mit sichtbarem Slug `demo`; unbekannte Routen landen auf `/workspaces`.
4. Hell/Dunkel: `prefers-color-scheme: dark` und `data-theme="dark"` schalten die Tokens um; Body hat expliziten Hintergrund.
5. Kopfzeile zeigt den Backend-Status aus `/health`; ohne Backend erscheint „nicht erreichbar“, kein Fehler in der Konsole außer dem Netzwerkfehler.
6. Keine Datei außerhalb von `src/Agcowork.Web/` geändert.

## Tests, die mindestens entstehen

- `workspace-context.service.spec.ts`, `workspace-shell.component.spec.ts`, `app.routes.spec.ts`.

## Entscheidungen, die der Implementierer treffen darf

- Tailwind v4 via `@tailwindcss/postcss` oder Vite-Plugin, je nachdem, was mit dem Angular-Builder sauber läuft.
- Prettier-Regeln, ESLint-Schärfe, Komponenten-Präfix (`ag` vorgeschlagen).
- Ob `app.config.ts` `provideZonelessChangeDetection()` explizit braucht oder die CLI-Vorgabe reicht.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
