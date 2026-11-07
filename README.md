# agcowork

Agent-first Wissens- und Arbeitsraum (Notion-Ersatz) mit Multi-Workspace.
Backend .NET 10 (Minimal APIs, EF Core, PostgreSQL), Frontend Angular 22.

## Voraussetzungen

- .NET SDK 10
- Node.js 24 mit npm
- Docker (PostgreSQL-Container und Testcontainers)

## Start mit Aspire

```
dotnet run --project src/Agcowork.AppHost
```

Startet PostgreSQL, die API und den Angular-Dev-Server (inklusive `npm install`).
Das Aspire-Dashboard läuft auf http://localhost:15180; dort stehen die URLs der Ressourcen.
Die Angular-App erreicht die API über den Dev-Server-Proxy (`src/Agcowork.Web/proxy.conf.js`).

Ohne Aspire: API mit `dotnet run --project src/Agcowork.Api` (Port 5180) und
`cd src/Agcowork.Web && npm start`. Die Datenbank muss dann selbst bereitstehen; die API liest den Connection String `ConnectionStrings__agcowork`.

**macOS, erster Start:** Der Schlüsselbund fragt nach Zugriff auf das Dev-Zertifikat.
Den Dialog einmal mit „Immer erlauben“ bestätigen, sonst hängt der Start.
Notlösung: `ASPIRE_DCP_USE_DEVELOPER_CERTIFICATE=false` in der Shell setzen.

## Tests

```
dotnet build
dotnet test                       # Integrationstests brauchen Docker
cd src/Agcowork.Web && npm test   # Frontend-Units
cd src/Agcowork.Web && npm run lint
```

CI (`.github/workflows/ci.yml`) führt Build, Lint und Tests für Backend und Frontend aus.

## Dokumentation

- [Planung](docs/01-PLANUNG.md)
- [Architektur](docs/02-ARCHITEKTUR.md)
- [Workflow](docs/03-WORKFLOW.md)
- [Arbeitspakete](docs/pakete/)
