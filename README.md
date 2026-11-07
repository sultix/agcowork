# agcowork

Agent-first Wissens- und Arbeitsraum (Notion-Ersatz) mit Multi-Workspace.
Backend .NET 10 (Minimal APIs, EF Core, PostgreSQL), Frontend Angular 22.

## Voraussetzungen

- .NET SDK 10
- Node.js 24 mit npm
- Docker (PostgreSQL-Container und Testcontainers)

## Start mit Aspire

```
dotnet run --project src/backend/Agcowork.AppHost
```

Startet PostgreSQL, die API und den Angular-Dev-Server (inklusive `npm install`).
Das Aspire-Dashboard läuft auf http://localhost:15180; dort stehen die URLs der Ressourcen.
Die Angular-App erreicht die API über den Dev-Server-Proxy (`src/frontend/proxy.conf.js`).

Ohne Aspire: API mit `dotnet run --project src/backend/Agcowork.Api` (Port 5180) und
`cd src/frontend && npm start`. Die Datenbank muss dann selbst bereitstehen; die API liest den Connection String `ConnectionStrings__agcowork`.

**macOS, erster Start:** Der Schlüsselbund fragt nach Zugriff auf das Dev-Zertifikat.
Den Dialog einmal mit „Immer erlauben“ bestätigen, sonst hängt der Start.
Notlösung: `ASPIRE_DCP_USE_DEVELOPER_CERTIFICATE=false` in der Shell setzen.

## Start mit Docker Compose

Der Betreiber-Weg ohne Aspire und ohne lokales SDK: PostgreSQL, API und Nginx mit dem Angular-Produktionsbuild.
Aspire bleibt der Entwickler-Weg.

```
cp .env.example .env              # Passwörter und Admin-Zugang anpassen
docker compose up -d --build
```

- App: http://localhost:8080 (Port über `WEB_PORT` in `.env`). Login mit `AGCOWORK_ADMIN_EMAIL` und
  `AGCOWORK_ADMIN_PASSWORD` aus `.env` (mindestens 12 Zeichen; der Admin entsteht nur beim ersten Start mit leerer Datenbank).
- MCP für Agenten: `http://localhost:8080/w/{slug}/mcp` (Instanz-Tools unter `/mcp`), Key wie unter „Agenten anbinden“.
- Nur Nginx veröffentlicht einen Port; Postgres und API sind nur im Compose-Netz erreichbar.
- **TLS:** Compose setzt `Agcowork__Cookies__SecurePolicy=SameAsRequest`, weil lokal kein TLS läuft. Hinter einem
  TLS-Reverse-Proxy diese Variable entfernen: außerhalb von Development gilt dann `Always` (Cookie nur über https).
- Daten liegen im Volume `agcowork-compose-pgdata` und überleben `docker compose down`.
  Auch die Sitzungs-Schlüssel liegen in einem Volume (`agcowork-compose-dpkeys`), Logins überleben also einen Neustart.
  `docker compose down -v` löscht beide Volumes und setzt alles zurück, auch alle Sitzungen.
- Im API-Log erscheint beim Start eine Npgsql-Warnung zu `libgssapi_krb5`; sie ist harmlos (Kerberos wird nicht genutzt).

## Tests

```
dotnet build
dotnet test                       # Integrationstests brauchen Docker
cd src/frontend && npm test   # Frontend-Units
cd src/frontend && npm run lint
```

CI (`.github/workflows/ci.yml`) führt Build, Lint und Tests für Backend und Frontend aus.

## Agenten anbinden

agcowork ist ein MCP-Server (Streamable HTTP, stateless). Jeder Workspace hat einen eigenen Endpunkt
`/w/{slug}/mcp` mit den Inhalts-Tools (`workspace_overview`, `collection_describe`, `records_query`, `record_get`,
`record_create`, `record_update`, `record_append`, …) und den Resources `agcowork://record/{id}`,
`agcowork://collection/{id}/schema` und `agcowork://space/{id}/overview`. Der Endpunkt `/mcp` bietet die
Instanz-Tools (`me`, `workspaces_list`, `templates_list`, `workspace_create`, `workspace_add_member`).

1. **API-Key erzeugen:** in der Admin-UI unter Akteure einen Agenten anlegen und ihm einen Key ausstellen (der Key
   `agck_…` wird nur einmal angezeigt). Den Agenten im gewünschten Workspace als Mitglied hinzufügen. Ein Key kann auf
   einen Workspace und auf Scopes (z. B. nur `read`) eingeschränkt werden.
2. **Claude Code konfigurieren** (`.mcp.json` im Projekt oder `claude mcp add --transport http …`):

   ```json
   {
     "mcpServers": {
       "agcowork-projekt-alpha": {
         "type": "http",
         "url": "https://agcowork.local/w/projekt-alpha/mcp",
         "headers": { "Authorization": "Bearer agck_…" }
       }
     }
   }
   ```

   Für mehrere Workspaces je einen Eintrag anlegen. Lokal mit Aspire ist die URL die der API (siehe Aspire-Dashboard),
   z. B. `http://localhost:5xxx/w/<slug>/mcp`.
3. **Erster Aufruf:** Der Agent sollte mit `workspace_overview` beginnen (Spaces, Collections, Beschreibungen) und vor dem
   Schreiben `collection_describe` aufrufen. Fehler kommen strukturiert zurück (`code`, `errors[]` mit `allowed`,
   `currentRevision` bei `revision_mismatch`), damit der Agent sich selbst korrigieren kann.

Nur API-Keys werden akzeptiert; Session-Cookies erhalten 401. Ein Workspace, in dem der Key-Inhaber kein Mitglied ist,
antwortet mit 404.

**Clients ohne HTTP-Transport (nur stdio):** `src/backend/Agcowork.Mcp.Stdio` ist eine kleine Brücke stdio ↔ Streamable HTTP:

```json
{
  "mcpServers": {
    "agcowork-projekt-alpha": {
      "command": "dotnet",
      "args": ["run", "--project", "src/backend/Agcowork.Mcp.Stdio", "--", "--url", "https://agcowork.local/w/projekt-alpha/mcp"],
      "env": { "AGCOWORK_API_KEY": "agck_…" }
    }
  }
}
```

Den Key immer über die Umgebungsvariable `AGCOWORK_API_KEY` übergeben, nicht mit `--key` (das landet in Prozesslisten
und der Shell-History). Lehnt der Server ab, meldet der Proxy auf stderr knapp den Grund (401: Key ungültig;
404: Slug falsch, keine Mitgliedschaft oder Key auf einen anderen Workspace eingeschränkt).

Als dotnet-Tool: `dotnet pack src/backend/Agcowork.Mcp.Stdio` und `dotnet tool install --global --add-source <nupkg-Ordner>
Agcowork.Mcp.Stdio`, danach `AGCOWORK_API_KEY=agck_… agcowork-mcp-stdio --url …`.

## Dokumentation

- [Planung](docs/01-PLANUNG.md)
- [Architektur](docs/02-ARCHITEKTUR.md)
- [Workflow](docs/03-WORKFLOW.md)
- [Arbeitspakete](docs/pakete/)
