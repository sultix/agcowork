# AP-003 – Aspire-Verdrahtung, CI und README

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | 0 |
| Implementierer | implementer-sonnet |
| Referenzbeispiel | `src/Agcowork.AppHost/` aus AP-001 (Postgres + Api), `src/Agcowork.Web/package.json` aus AP-002 |
| Abhängig von | AP-001, AP-002 |
| Betroffene Pfade | `src/Agcowork.AppHost/`, `src/Agcowork.Api/Properties/launchSettings.json`, `Directory.Packages.props`, `src/Agcowork.Web/proxy.conf.js` (ersetzt `proxy.conf.json`), `src/Agcowork.Web/angular.json`, `.github/workflows/ci.yml`, `README.md` |
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
- **Port 5000 ist auf dem Entwicklungs-Mac vom AirPlay-Empfänger belegt** (antwortet mit 403). Der Fallback in `proxy.conf.json` muss auf einen freien Port wechseln (z. B. 5180), und die Api sollte in `launchSettings.json` denselben Port nutzen, damit `npm start` auch ohne Aspire funktioniert.
- Deshalb: Api-Ports in `src/Agcowork.Api/Properties/launchSettings.json` auf **5180 (http) / 5181 (https)** umstellen und den Proxy-Fallback in `proxy.conf.json` auf `http://localhost:5180`. Unter Aspire kommt der Port ohnehin dynamisch.
- **Schlüsselbund-Dialog:** Beim ersten `dotnet run --project src/Agcowork.AppHost` fragt macOS nach Zugriff auf das Dev-Zertifikat (DCP-TLS). Ohne Klick hängt der Start unbegrenzt. Ins README: Dialog einmal mit „Immer erlauben“ bestätigen; Notlösung `ASPIRE_DCP_USE_DEVELOPER_CERTIFICATE=false`. Die Variable nicht ins Repo eintragen.
- Die Angular-CLI hat `src/Agcowork.Web/.vscode/` erzeugt. Nicht einchecken: Eintrag in der Root-`.gitignore` ist bereits vorhanden (`.vscode/`).

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
| 2026-11-07 | Kontext ergänzt (Ports, Schlüsselbund); an implementer-sonnet übergeben |
| 2026-11-07 | Bericht: Aspire.Hosting.JavaScript, `web`-Ressource mit npm install, proxy.conf.js liest `services__api__http__0`, Ports 5180/5181, ci.yml (backend/frontend), README. Build/Tests grün, AppHost-Lauf mit Proxy-Durchgriff auf /health/ready verifiziert. Nicht geprüft: Kopfzeile im Browser, ci.yml maschinell |
| 2026-11-07 | an reviewer übergeben |
| 2026-11-07 | Orchestrator: AppHost gestartet, Angular unter Aspire im Browser geprüft: Kopfzeile „Backend: ok“, keine Konsolenfehler, `/foo/bar` → `/workspaces`. AK 1–3 damit belegt |
| 2026-11-07 | Review: Freigabe mit Hinweisen. Wichtig: AK2 unbelegt (→ durch Orchestrator-Lauf erledigt, `ng serve --port 32410` zeigt auch, dass das `--`-Argument korrekt ankommt); HEAD nach AP-001-Commit allein nicht lauffähig (proxy.conf.json gelöscht, angular.json zeigt noch darauf) → mit diesem Commit behoben. Hinweise: ci.yml ohne permissions/timeout, README ohne Connection-String-Name und `npm run lint`, `PORT`-Env überflüssig |
| 2026-11-07 | Orchestrator: permissions + timeout-minutes in ci.yml, README ergänzt. Abgeschlossen, Commit siehe Backlog |
