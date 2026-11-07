# AP-001 – Backend-Gerüst (.NET 10, EF Core, PostgreSQL, Aspire)

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | 0 |
| Implementierer | implementer-opus |
| Referenzbeispiel | – (erstes Auftreten) |
| Abhängig von | – |
| Betroffene Pfade | `agcowork.sln`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `src/Agcowork.Domain/`, `src/Agcowork.Application/`, `src/Agcowork.Infrastructure/`, `src/Agcowork.Api/`, `src/Agcowork.ServiceDefaults/`, `src/Agcowork.AppHost/`, `tests/` |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §1 Gesamtbild, §5 Backend-Struktur, §8 Betrieb; `01-PLANUNG.md` E4, E8 |

## Ziel

Nach diesem Paket existiert eine baubare, testbare .NET-10-Solution mit der Schichtenstruktur
aus der Architektur, einer PostgreSQL-Anbindung per EF Core inklusive Migrations-Pipeline,
einem lauffähigen API-Host mit Health-Endpunkt und OpenAPI, und einem Aspire AppHost, der
Postgres und die API startet. Jedes spätere Backend-Paket baut auf dieser Struktur auf.

## Scope

**Drin:**
- Solution und Projekte (siehe Pfade). Abhängigkeiten nur nach innen: Domain ← Application ← Infrastructure ← Api. Api referenziert ServiceDefaults. AppHost referenziert Api.
- `Directory.Build.props`: `net10.0`, `Nullable` und `ImplicitUsings` an, `TreatWarningsAsErrors` an, `LangVersion` latest, Analyzer-Level latest-recommended.
- `Directory.Packages.props`: zentrale Paketverwaltung, alle Versionen dort, jeweils aktuelle stabile Version.
- `global.json` auf SDK 10.0.x mit `rollForward: latestFeature`.
- **Domain**: Entität `Workspace` minimal (`Id` Guid UUIDv7, `Slug`, `Name`, `CreatedAt` DateTimeOffset). Sie dient hier als Rauchtest der gesamten Pipeline; die vollständige Ausprägung kommt in Phase 1.
- **Application**: Projekt mit `DependencyInjection`-Erweiterung (`AddApplication()`), noch ohne Use-Cases. Platzhalter-Ordnerstruktur für Vertical Slices nicht vorab anlegen.
- **Infrastructure**: `AgcoworkDbContext` mit `DbSet<Workspace>`, Konfiguration per `IEntityTypeConfiguration`, Tabellenname `workspaces`, Spalten snake_case (EFCore.NamingConventions oder manuell, Entscheidung frei), eindeutiger Index auf `slug`. Erste Migration `InitialCreate`. `AddInfrastructure(configuration)` registriert DbContext mit Npgsql. Migrationen werden beim Start der Api angewendet (`MigrateAsync`), Single-Instance-Annahme laut Architektur §8.
- **ServiceDefaults**: Aspire-Standard (OpenTelemetry, Health-Checks, Resilience, Service Discovery).
- **Api**: Minimal-API-Host. `/health` (Liveness) und `/health/ready` (inkl. Npgsql-Check). OpenAPI-Dokument unter `/openapi/v1.json`, Scalar-UI unter `/scalar` (nur Development). Noch keine fachlichen Endpunkte. Typisierte Results vorbereitet (`TypedResults`).
- **AppHost**: Aspire. Postgres-Container mit Datenbank `agcowork` und persistentem Volume, Api-Projekt mit Referenz auf die Datenbank. Kein Angular (kommt in AP-003).
- **Tests**: `tests/Agcowork.Api.Tests` mit `WebApplicationFactory` und Testcontainers-Postgres (eine Container-Instanz pro Testklasse oder Collection); `tests/Agcowork.Domain.Tests` mit einem echten Test (z. B. UUIDv7-Erzeugung monoton); `tests/Agcowork.Application.Tests` nur anlegen, wenn es etwas zu testen gibt, sonst weglassen.
- Logging strukturiert (Serilog oder Microsoft.Extensions.Logging mit OpenTelemetry; Entscheidung frei, Begründung im Bericht).

**Ausdrücklich nicht drin:**
- Angular, CI, README (AP-002, AP-003).
- Fachliche Endpunkte, Auth, Tenancy-Middleware, MCP, SignalR (Phase 1).
- Weitere Entitäten außer `Workspace`.

## Fachlicher Kontext

- Aspire-Templates sind auf dem Rechner nicht installiert: `dotnet new install Aspire.ProjectTemplates` oder Projekte manuell mit `Aspire.AppHost.Sdk` anlegen. Aktuelle Aspire-Hauptversion verwenden.
- Docker läuft lokal (Docker 27, Engine 29), Testcontainers funktioniert.
- IDs sind UUIDv7 (`Guid.CreateVersion7()`), Zeit ist `DateTimeOffset` UTC über `TimeProvider`.
- Die Solution heißt `agcowork`. `.sln` oder `.slnx` ist deine Entscheidung; `.slnx` bevorzugt, wenn das SDK es sauber unterstützt.

## Akzeptanzkriterien

1. `dotnet build` im Repo-Root läuft ohne Warnungen und Fehler durch.
2. `dotnet test` ist grün; die Api-Tests starten einen Postgres-Container, wenden die Migration an und prüfen: `GET /health` → 200, `GET /health/ready` → 200, `GET /openapi/v1.json` → 200 mit gültigem JSON.
3. Ein Integrationstest legt einen `Workspace` über den DbContext an und liest ihn per Slug zurück; ein zweiter prüft, dass ein doppelter Slug einen Unique-Verstoß auslöst.
4. `dotnet build src/Agcowork.AppHost` ist grün, und `dotnet run --project src/Agcowork.AppHost` startet (Nachweis: Log-Zeile des Aspire-Dashboards oder der API innerhalb von 90 s; danach beenden).
5. Projektabhängigkeiten zeigen nur nach innen (Domain hat keine Paketreferenzen außer ggf. Analyzern).
6. Alle Paketversionen stehen in `Directory.Packages.props`.

## Tests, die mindestens entstehen

- Domain: ein Unit-Test.
- Api-Integration: Health, Ready, OpenAPI, Workspace-Roundtrip, Slug-Unique.

## Entscheidungen, die der Implementierer treffen darf

- `.sln` vs `.slnx`; Namenskonvention-Paket vs. manuelle Spaltennamen; Logging-Bibliothek; xUnit v2 vs v3; Test-Fixture-Strategie für den Container.
- Exakte Paketversionen (aktuell stabil, keine Previews außer wenn für .NET 10 unvermeidbar; dann im Bericht nennen).

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
| 2026-11-07 | an implementer-opus übergeben |
| 2026-11-07 | Bericht: slnx, EF Core 10 + Npgsql, Migration InitialCreate, Aspire 13.6 AppHost, 18 Tests grün (13 Domain, 5 Api mit Testcontainers), AppHost-Start verifiziert. Abweichungen: Aspire-SDK-Version in global.json (NU1009), Microsoft.Testing.Platform statt VSTest, Health in allen Umgebungen. Hinweise: Schlüsselbund-Dialog beim ersten Aspire-Start, Port 5000/AirPlay, Retry-Strategie bei Transaktionen |
| 2026-11-07 | an reviewer übergeben |
| 2026-11-07 | Review: Freigabe mit Hinweisen. Wichtig: Slug-Regex `$` lässt „acme\n“ durch (→ `\z`); Aspire-Retry-Strategie aktiv, Entscheidung nötig. Hinweise: AK6 formal nicht erfüllt (Aspire-SDK-Version in global.json, akzeptiert wegen NU1009); Health/OpenAPI in Production offen; Port 5000; UUIDv7 nicht innerhalb einer ms monoton |
| 2026-11-07 | Entscheidungen: AK6-Abweichung akzeptiert. Retry-Strategie wird deaktiviert (`DisableRetry = true`), Architektur §5 ergänzt; Einzeiler folgt nach AP-003. Nacharbeit an implementer-opus: Slug-Regex + Tests |
| 2026-11-07 | Nacharbeit fertig: `\z`-Regex, 20 Tests grün. Commit folgt nach AP-003, weil AP-003 AppHost.cs und launchSettings.json bereits verändert hat (diese zwei Dateien tragen im AP-001-Commit schon AP-003-Stand) |
| 2026-11-07 | Orchestrator: `DisableRetry = true` gesetzt, Build und 20 Tests selbst verifiziert; abgeschlossen, Commit `7d47dcb` |
