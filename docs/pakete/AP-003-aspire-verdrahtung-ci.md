# AP-003 – Aspire-Verdrahtung, CI und README

| Feld | Wert |
|---|---|
| Status | angelegt |
| Phase | 0 |
| Implementierer | implementer-sonnet |
| Referenzbeispiel | `src/Agcowork.AppHost/` aus AP-001 (Postgres + Api), `src/Agcowork.Web/package.json` aus AP-002 |
| Abhängig von | AP-001, AP-002 |
| Betroffene Pfade | `src/Agcowork.AppHost/`, `src/Agcowork.Web/proxy.conf.json`, `.github/workflows/ci.yml`, `README.md` |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §8 Betrieb; `CLAUDE.md` Befehle |

## Ziel

Nach diesem Paket startet `dotnet run --project src/Agcowork.AppHost` Postgres, API und
Angular-Dev-Server gemeinsam, die Angular-App erreicht die API über den Proxy, und ein
GitHub-Actions-Workflow baut und testet Backend und Frontend.

## Scope

**Drin:**
- AppHost: Angular-App als Node/npm-Ressource (`Aspire.Hosting.NodeJs`, `AddNpmApp` oder aktuelles Äquivalent) mit Referenz auf die Api; der Api-Endpunkt wird als Umgebungsvariable an den Dev-Server gereicht; `proxy.conf.json` liest das Ziel daraus (Fallback bleibt `http://localhost:5000`). `npm install` muss vor dem Start laufen (Aspire-Option nutzen, wenn vorhanden, sonst im README dokumentieren).
- Api: CORS für den Angular-Dev-Origin nur in Development, falls der Proxy nicht reicht.
- `.github/workflows/ci.yml`: zwei Jobs. `backend`: `actions/setup-dotnet` 10.x, `dotnet build`, `dotnet test` (Testcontainers nutzt den Docker des Runners). `frontend`: `actions/setup-node` 24, `npm ci`, `npm run lint`, `npm test`, `npm run build` in `src/Agcowork.Web`. Trigger: push auf `main`, pull_request.
- `README.md` (Deutsch, kurz): Was ist agcowork, Voraussetzungen, Start mit Aspire, Tests, Links auf `docs/`.

**Ausdrücklich nicht drin:**
- Änderungen an Fachcode, Tests aus AP-001/AP-002 (außer sie brechen durch die Verdrahtung; dann melden, nicht still anpassen).
- Deployment, Docker-Compose (später, wenn Hosting entschieden ist).

## Fachlicher Kontext

- Es gibt noch kein GitHub-Remote. Der Workflow kann lokal nicht ausgeführt werden; er wird syntaktisch geprüft (`actionlint` per `npx`, falls verfügbar, sonst sorgfältig gegengelesen) und beim ersten Push verifiziert.
- Aspire verteilt Ports dynamisch; der Angular-Proxy darf keinen festen Port annehmen.

## Akzeptanzkriterien

1. `dotnet run --project src/Agcowork.AppHost` startet Postgres, Api und Angular; im Aspire-Dashboard sind alle drei Ressourcen „Running“ (Nachweis: Logausgabe oder Screenshot-Beschreibung im Bericht).
2. Die Angular-Kopfzeile zeigt im Aspire-Lauf den Backend-Status „ok“.
3. `dotnet build`, `dotnet test`, `npm test`, `npm run build` weiterhin grün.
4. `ci.yml` ist syntaktisch gültig und deckt beide Jobs ab.
5. README beschreibt den Start in höchstens einer Bildschirmseite.

## Tests, die mindestens entstehen

- Keine neuen Unit-Tests; Nachweis über die Akzeptanzkriterien 1–3.

## Entscheidungen, die der Implementierer treffen darf

- Art der Übergabe des Api-Endpunkts an den Proxy (Env-Variable, generierte Datei).
- Ob CI Node- und NuGet-Caches nutzt.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
