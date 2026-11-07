# AP-013 – Frontend: OpenAPI-Client, Login, Session, Auth-Guard

| Feld | Wert |
|---|---|
| Status | in Arbeit |
| Phase | 1 |
| Implementierer | implementer-opus |
| Referenzbeispiel | AP-002/AP-012: `core/api/api.config.ts`, `HealthService` (httpResource), `WorkspaceContext`, Adapter-Muster, Routen-Spec |
| Abhängig von | AP-004 |
| Betroffene Pfade | `src/Agcowork.Web/` (alles), zusätzlich **nur additiv**: `src/Agcowork.Api/Agcowork.Api.csproj` (OpenAPI-Dokument zur Build-Zeit), `Directory.Packages.props` (ein Paket), neues Verzeichnis `src/Agcowork.Api/openapi/`, `.github/workflows/ci.yml` (Aktualitätsprüfung). Parallel läuft AP-005 im Backend; fasse keine anderen Backend-Dateien an. |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §4.3 (REST, „Die Angular-App nutzt denselben API-Client, generiert aus OpenAPI“), §5 Use-Case-/Fehler-Muster (ProblemDetails-Format), §6 Frontend; AP-004-Paket (Endpunkte `/auth/login`, `/auth/logout`, `/me`, Cookie `agcowork_session`, ProblemDetails mit `code`, `errors[]`) |

## Ziel

Nach diesem Paket spricht die Angular-App typisiert mit der API: Das OpenAPI-Dokument entsteht
beim Backend-Build, daraus werden TypeScript-Typen und ein dünner Client generiert, und die
App hat echten Login, Session-Zustand, Auth-Guard und einheitliche Fehlerdarstellung.
Alle folgenden Frontend-Pakete nutzen diesen Client.

## Scope

**Drin:**

*OpenAPI-Dokument zur Build-Zeit (Backend, additiv)*
- `Microsoft.Extensions.ApiDescription.Server` im Api-Projekt, sodass `dotnet build` das Dokument nach `src/Agcowork.Api/openapi/v1.json` schreibt (`OpenApiDocumentsDirectory`, `OpenApiGenerateDocumentsOnBuild`). Die Datei wird **eingecheckt** (sie ist der Vertrag, den das Frontend sieht).
- CI: Der Backend-Job prüft nach `dotnet build`, dass `git diff --exit-code src/Agcowork.Api/openapi/` leer ist; sonst Fehler mit Hinweis „openapi/v1.json neu generieren und einchecken“.

*Client-Generierung (Frontend)*
- `openapi-typescript` erzeugt `src/app/core/api/generated/schema.d.ts` aus `../Agcowork.Api/openapi/v1.json` (npm-Skript `api:generate`, läuft auch in `prebuild`/`pretest`, damit Typen nie veralten). Generierte Dateien werden eingecheckt und per ESLint-Ignore ausgenommen.
- Dünner typisierter Client über `HttpClient` (kein zweiter HTTP-Stack): `ApiClient` mit Methoden je Endpunkt oder generisch `api.get<'/me'>('/me')`-artig über die `paths`-Typen. `openapi-fetch` ist erlaubt, wenn es sich sauber mit `resource()`/`httpResource` und `withCredentials` verbinden lässt; sonst eigener Wrapper. Entscheidung begründen.
- Workspace-Präfix: `ApiClient.workspace(slug)` liefert einen Sub-Client mit Präfix `/api/v1/w/{slug}` (Vorbereitung für AP-014 ff., hier nur mit Test, ohne Verwendung).
- Alle Requests mit `withCredentials: true` (Cookie), `Accept: application/json`.

*Fehlerbehandlung*
- `ApiError`-Typ, der ProblemDetails (`status`, `code`, `detail`, `errors[]`, `traceId`) abbildet; Interceptor/Wrapper wandelt `HttpErrorResponse` mit Problem-Body in `ApiError`.
- 401 außerhalb von Login → Session leeren und zu `/login?returnUrl=…` navigieren (Interceptor).
- `FieldErrors`-Hilfe, die `errors[]` auf Formularfelder abbildet (für Signal Forms nutzbar).

*Session und Login*
- `SessionStore` (Signal-Service in `core/auth`): `me` (Actor aus `GET /me` oder null), `status` (`unknown | anonymous | authenticated`), `load()`, `login(email, password)`, `logout()`. Beim App-Start einmal `load()`; `resource()` oder `httpResource` für `me`.
- Login-Seite (`features/auth`) mit **Signal Forms**: E-Mail, Passwort, Fehleranzeige aus `ApiError` (401 → „E-Mail oder Passwort falsch“, Validierungsfehler feldweise), `returnUrl`-Weiterleitung, Enter sendet.
- `authGuard` (funktional) für `/workspaces` und `/w/**`: wartet auf `status !== 'unknown'`, leitet Anonyme zu `/login`.
- Kopfzeile zeigt den angemeldeten Actor (Name, Kind) und einen Logout-Button; Logout → `POST /auth/logout` → `/login`.
- Der Workspace-Wechsler bleibt statisch (echte Liste in AP-014/016, braucht AP-005).

*Dev-Setup*
- Proxy leitet `/api` weiter (vorhanden). `npm start` gegen laufende API (Aspire) funktioniert mit dem Dev-Admin aus `appsettings.Development.json` (`admin@agcowork.local`).

*Tests (Vitest)*
- `ApiClient`: URL-Bildung inkl. Workspace-Präfix, `withCredentials`, Fehler → `ApiError` mit `errors[]`.
- Interceptor: 401 leert Session und navigiert zu `/login?returnUrl=…`; 401 auf `/auth/login` navigiert nicht.
- `SessionStore`: `load()` setzt `authenticated`/`anonymous`; `login()` lädt `me` nach; `logout()` leert.
- Login-Komponente: Validierung, 401-Meldung, Feldfehler aus `errors[]`, Weiterleitung mit `returnUrl`.
- `authGuard`: anonym → `/login`, authentifiziert → true, wartet bei `unknown`.
- Generierte Typen: ein Compile-Test, der `paths['/api/v1/me']['get']` verwendet (Tippfehler im Pfad bricht den Build).

**Ausdrücklich nicht drin:**
- Workspace-Liste, Mitglieder, Agenten-/Key-Verwaltung (AP-014/016), Records, Views.
- Änderungen an Backend-Endpunkten oder -Logik; neue Backend-Tests.
- Passwort-Reset, OIDC.

## Fachlicher Kontext

- Cookie-Login: `POST /api/v1/auth/login` `{ email, password }` → 204 + Cookie; `GET /api/v1/me` → Actor (`id`, `kind`, `name`, `role`, `email`, `instanceScopes`); `POST /api/v1/auth/logout` → 204. Genaue Schemata im generierten Dokument.
- Fehlerformat: ProblemDetails mit `code` (immer), `detail` nur bei 400/403/409, `errors[]` mit `field`, `code`, `message`, optional `allowed`, plus `traceId`. 401 ohne Details.
- SameSite=Lax-Cookie; Dev-Server und API laufen auf verschiedenen Ports, der Proxy macht daraus eine Origin. Direkt gegen die API (ohne Proxy) ist CORS nicht konfiguriert; das bleibt so.
- Zum Erzeugen von `openapi/v1.json` reicht `dotnet build src/Agcowork.Api`; es braucht keine laufende Datenbank. Prüfe das und melde, falls der Generator doch den Host startet und Postgres verlangt (dann `ConnectionStrings__agcowork` auf einen Dummy setzen, der nicht verbindet, oder das Bootstrapping im Generator-Modus überspringen; Letzteres nur, wenn es in `Program.cs` ohne Konflikt mit AP-005 geht: dort arbeitet gerade AP-005, also lieber Umgebungsvariable).

## Akzeptanzkriterien

1. `dotnet build` erzeugt `src/Agcowork.Api/openapi/v1.json`; Datei ist eingecheckt; CI schlägt fehl, wenn sie veraltet ist (Workflow-Schritt vorhanden).
2. `npm run api:generate` erzeugt die Typen; `npm run build` und `npm test` laufen inklusive Generierung grün; `npm run lint` grün.
3. Gegen die laufende API: Login mit dem Dev-Admin funktioniert, `/workspaces` ist danach erreichbar, Logout führt zu `/login`, direkter Aufruf von `/w/demo` ohne Session landet auf `/login?returnUrl=/w/demo` und nach Login wieder dort.
4. Falsches Passwort zeigt eine Meldung ohne Hinweis, ob die E-Mail existiert; ungültige Eingaben zeigen Feldfehler.
5. Jede API-Antwort mit ProblemDetails wird als `ApiError` mit `code` und `errors[]` verfügbar (Test).
6. Keine Backend-Datei außer den genannten additiven Stellen geändert; Backend-Tests weiterhin grün.

## Tests, die mindestens entstehen

- `core/api/api-client.spec.ts`, `core/api/api-error.interceptor.spec.ts`, `core/auth/session.store.spec.ts`, `core/auth/auth.guard.spec.ts`, `features/auth/login.component.spec.ts`, `core/api/generated/schema.spec.ts` (Compile-Test)

## Entscheidungen, die der Implementierer treffen darf

- `openapi-fetch` vs. eigener Wrapper; Ablage der generierten Datei; ob `api:generate` in `prebuild`/`pretest` oder als expliziter Schritt plus CI-Prüfung läuft.
- Darstellung von Fehlern (Toast vs. inline), solange Feldfehler am Feld erscheinen.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
| 2026-11-07 | an implementer-opus übergeben |
