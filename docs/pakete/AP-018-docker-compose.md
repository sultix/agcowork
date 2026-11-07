# AP-018 – Docker Compose für den lokalen Start ohne Aspire

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | Betrieb (zwischen Phase 1 und 2) |
| Implementierer | implementer-sonnet |
| Referenzbeispiel | `src/backend/Agcowork.AppHost/AppHost.cs` (welche Ressourcen und Umgebungsvariablen die App braucht), `src/frontend/proxy.conf.js` (welche Pfade zur API gehen), `src/backend/Agcowork.Api/Auth/AuthenticationSetup.cs` (Cookie-Policy), `README.md`, `.github/workflows/ci.yml` |
| Abhängig von | AP-011 |
| Betroffene Pfade | `docker-compose.yml` (neu), `.env.example` (neu), `src/backend/Agcowork.Api/Dockerfile` (neu), `src/frontend/Dockerfile` (neu), `src/frontend/nginx.conf` (neu), `.dockerignore` (neu, Root), `src/backend/Agcowork.Api/Auth/AuthenticationSetup.cs` + `appsettings.json` (Cookie-Option), `README.md`, `.github/workflows/ci.yml` (Image-Build), `docs/02-ARCHITEKTUR.md` §8 |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §8 Betrieb („Alternativ `docker compose up`“), §7 Sicherheit (Cookies, Health/OpenAPI-Erreichbarkeit), §4.2 Transport (`/w/{slug}/mcp`, `/mcp`) |

## Ziel

`docker compose up` startet agcowork komplett in Containern: PostgreSQL, die API und ein
Nginx mit dem Angular-Produktionsbuild. Danach ist die App unter `http://localhost:8080`
erreichbar, ein Agent kann sich per MCP unter `http://localhost:8080/w/{slug}/mcp` anbinden,
und der Bootstrap-Admin kann sich anmelden. Kein Aspire, kein lokales SDK nötig.

## Scope

**Drin:**

*Backend-Änderung (klein)*
- Konfigurationsoption `Agcowork:Cookies:SecurePolicy` (`Always` | `SameAsRequest`), Standard: `Always` außerhalb von Development, `SameAsRequest` in Development (heutiges Verhalten bleibt). Compose setzt `SameAsRequest`, weil lokal kein TLS läuft; README erklärt, dass hinter einem TLS-Reverse-Proxy `Always` gilt. Test: Option wirkt (Cookie ohne `Secure`-Flag bei `SameAsRequest` über http).
- Health-Endpunkte und Migrationen beim Start bleiben wie sie sind.

*Images*
- `src/backend/Agcowork.Api/Dockerfile`: Multi-Stage (`mcr.microsoft.com/dotnet/sdk:10.0` → `aspnet:10.0`), Build-Kontext ist das Repo-Root (wegen `Directory.*.props`, `global.json`, Projektreferenzen), `dotnet publish -c Release` der Api; Non-Root-User; `ASPNETCORE_URLS=http://+:8080`; Healthcheck auf `/health`. Die OpenAPI-Generierung zur Build-Zeit muss im Container ohne DB funktionieren (Guard aus AP-013) oder per `-p:OpenApiGenerateDocumentsOnBuild=false` abgeschaltet werden.
- `src/frontend/Dockerfile`: `node:24-alpine` Build (`npm ci`, `npm run build`; `api:generate` braucht `../backend/Agcowork.Api/openapi/v1.json`, daher ebenfalls Root-Kontext oder Pre-Hook deaktivieren) → `nginx:1.27-alpine` mit `dist/agcowork-web/browser`.
- `src/frontend/nginx.conf`: SPA-Fallback auf `index.html`; Proxy zur API für `/api/`, `/health`, `/health/ready`, `/openapi/`, `/mcp`, und `~ ^/w/[^/]+/mcp$` (nur der MCP-Pfad, nicht die Angular-Routen unter `/w/`); `proxy_set_header Host $host`, `X-Forwarded-For`, `X-Forwarded-Proto`; Streamable HTTP braucht `proxy_buffering off` und `proxy_read_timeout` ≥ 300 s für MCP; Gzip; Cache-Header für gehashte Assets, `no-cache` für `index.html`.
- `.dockerignore` im Root: `**/bin`, `**/obj`, `**/node_modules`, `**/dist`, `**/.angular`, `.git`, `docs`, `tests` (Tests nicht ins Image).

*Compose*
- `docker-compose.yml` im Root mit drei Services: `postgres` (`postgres:17-alpine`, benanntes Volume `agcowork-compose-pgdata`, Healthcheck `pg_isready`, Port **nicht** veröffentlicht; optional per Override), `api` (Build aus Root-Kontext, `depends_on: postgres: condition: service_healthy`, Env: `ConnectionStrings__agcowork`, `Agcowork__Bootstrap__AdminEmail`, `Agcowork__Bootstrap__AdminPassword`, `Agcowork__Cookies__SecurePolicy=SameAsRequest`, `ASPNETCORE_ENVIRONMENT=Production`; Port nicht veröffentlicht), `web` (Build, `depends_on: api`, Port `8080:80`).
- `.env.example` mit `POSTGRES_PASSWORD`, `AGCOWORK_ADMIN_EMAIL`, `AGCOWORK_ADMIN_PASSWORD` (≥ 12 Zeichen, Policy aus AP-004), `WEB_PORT=8080`; Compose liest `.env`; `.env` in `.gitignore`.
- Extension `btree_gin` (AP-007-Migration) ist im Postgres-Image enthalten; prüfen, dass die Migration im Container durchläuft.

*CI*
- Neuer Job `images` in `ci.yml`: `docker compose build` (ohne Push), damit Dockerfiles nicht veralten. Läuft parallel zu backend/frontend.

*README*
- Abschnitt „Start mit Docker Compose“: `.env` anlegen, `docker compose up -d`, URL, Admin-Login, MCP-URL für Agenten, Hinweis TLS/`SecurePolicy`, `docker compose down -v` zum Zurücksetzen. Aspire bleibt der Entwickler-Weg, Compose der Betreiber-Weg.

*Tests/Nachweis*
- Backend-Unit-/Integrationstest für die Cookie-Option.
- Nachweis im Bericht: `docker compose up --build` lokal, `curl http://localhost:8080/health/ready` → 200, Login per `curl` mit Cookie-Jar gegen `/api/v1/auth/login` → 204, `GET /api/v1/me` → 200, MCP `initialize` per `curl` auf `/w/{slug}/mcp` mit einem per API erzeugten Key → gültige Antwort (Workspace vorher per API mit Standard-Vorlage anlegen), Angular-Startseite liefert `index.html`, eine Angular-Route wie `/workspaces` ebenfalls (SPA-Fallback). Danach `docker compose down -v`.

**Ausdrücklich nicht drin:**
- TLS/Reverse-Proxy-Setup, Deployment auf einen Server, Image-Registry/Push, Backups, Aspire-Änderungen.

## Fachlicher Kontext

- Ports: 5000/7000 sind auf dem Mac von AirPlay belegt (AP-002/003); deshalb 8080 nach außen.
- Der Bootstrap-Admin entsteht nur, wenn noch keine Actors existieren und das Passwort ≥ 12 Zeichen hat (AP-004).
- MCP läuft stateless über Streamable HTTP; Nginx darf Antworten nicht puffern.
- Angular-Routen liegen unter `/w/{slug}/...`; der MCP-Endpunkt `/w/{slug}/mcp` muss davon getrennt zur API, alles andere unter `/w/` zur SPA.
- `WhenWritingNull` und ProblemDetails bleiben unverändert; keine Fachlogik anfassen.

## Akzeptanzkriterien

1. `cp .env.example .env && docker compose up -d --build` startet drei gesunde Container; `http://localhost:8080` zeigt die Login-Seite, Login mit dem Admin aus `.env` funktioniert (Cookie wird über http gesetzt).
2. Workspace per UI oder API anlegen (Standard-Vorlage), Agent + Key per Admin-UI/API erzeugen, MCP `initialize` und `tools/list` über `http://localhost:8080/w/{slug}/mcp` liefern Antworten.
3. SPA-Fallback: `/workspaces` und `/w/{slug}/activity` liefern `index.html`; `/w/{slug}/mcp` geht zur API.
4. Neustart (`docker compose down && up`) behält die Daten; `down -v` setzt zurück.
5. Backend-Tests grün inkl. Cookie-Option; `docker compose build` als CI-Job vorhanden.
6. README-Abschnitt vorhanden; Architektur §8 ergänzt.

## Tests, die mindestens entstehen

- `tests/Agcowork.Api.Tests/Identity/CookiePolicyTests.cs` (Option `SameAsRequest` → Cookie ohne `Secure` über http; Standard → mit `Secure`).

## Entscheidungen, die der Implementierer treffen darf

- Ob OpenAPI-Generierung im Image-Build läuft oder abgeschaltet wird; Node-Build-Strategie für `api:generate`; Nginx-Feinheiten (Gzip-Liste, Cache-Dauer); ob `api` optional einen Host-Port per Override bekommt.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
| 2026-11-07 | an implementer-sonnet übergeben |
| 2026-11-07 | Bericht: drei Services, Volume `agcowork_agcowork-compose-pgdata`, nur `web` veröffentlicht Port; API-Image mit `OpenApiGenerateDocumentsOnBuild=false`, Non-Root, bash-Healthcheck; Web-Image kopiert `v1.json` für `api:generate`; Nginx mit MCP-Pfaden ohne Buffering; Cookie-Option `Agcowork:Cookies:SecurePolicy` via Options-Pattern, 3 Tests; 971 Tests grün; Nachweise per curl (Login, /me, MCP initialize/tools-list, SPA-Fallback, Neustart behält Daten). Offen: DataProtection-Keys nicht persistiert → Sessions nach Container-Neustart ungültig |
| 2026-11-07 | an reviewer übergeben |
| 2026-11-07 | Review: Freigabe mit Hinweisen; Start, Login, MCP über Nginx, SPA-Fallback, Persistenz, Sicherheit selbst nachgestellt. Wichtig: Nginx cacht die API-IP (502 nach Recreate); DataProtection-Keys ephemer (Volume + chown nötig). Hinweise: MCP-Pfad mit Schrägstrich, `^~`-Locations, Layer-Caching im API-Dockerfile, `ValidateOnStart`, Passwort-Sonderzeichen, Buildx-Cache in CI, Log-Rauschen; Web-Image als root (Paket-Vorgabe) |
| 2026-11-07 | Nacharbeit an implementer-sonnet (9 Punkte) |
| 2026-11-07 | Nacharbeit fertig: Nginx-Resolver mit `resolve`, `depends_on` healthy + restart, DataProtection-Volume mit chown, MCP-Pfade mit Schrägstrich, `^~`-Locations, Restore-Layer, `ValidateOnStart`, Passwort-Quoting, Buildx-Cache (bake) in CI, kein `ASPNETCORE_URLS`. Orchestrator: 973 Tests grün, `docker compose up --build` → 3 gesunde Container, Login und Workspace-Anlage aus Vorlage im Browser über Port 8080 geprüft. Abgeschlossen, Commit siehe Backlog |
