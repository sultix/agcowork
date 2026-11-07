# AP-022 – Härtung Backend: Passwort ändern und zurücksetzen, Sitzungen entwerten, Schema-Hook, OpenAPI-Defaults, Activity-Titel

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | Härtung (vor Phase 2) |
| Implementierer | implementer-opus |
| Referenzbeispiel | Use-Cases `src/backend/Agcowork.Application/Identity/Actors/UpdateActor.cs`, Endpunkte `src/backend/Agcowork.Api/Endpoints/Identity/`, Hook-Muster select→multi_select in `src/backend/Agcowork.Application/Records/RecordValuesSchemaHook.cs`, Body-Defaults wie `UpdateSpaceBody` in `Endpoints/Spaces/SpaceEndpoints.cs` |
| Abhängig von | AP-004, AP-005, AP-007, AP-009, AP-011 |
| Betroffene Pfade | `src/backend/Agcowork.Domain/Identity/`, `src/backend/Agcowork.Application/Identity/`, `src/backend/Agcowork.Application/Records/RecordValuesSchemaHook.cs`, `src/backend/Agcowork.Application/Activity/`, `src/backend/Agcowork.Infrastructure/Identity/`, `src/backend/Agcowork.Infrastructure/Persistence/` (Konfiguration, **eine** Migration), `src/backend/Agcowork.Api/` (Auth, Endpunkte, `openapi/v1.json`), `src/backend/Agcowork.Api/Mcp/` (nur falls das Activity-Tool das DTO nicht durchreicht), `tests/`; im Frontend nur das automatisch neu erzeugte `src/frontend/src/app/core/api/generated/schema.d.ts` |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §2 (Actor, LocalLogin, ActivityEvent), §4.1 (Fehlerformat), §4.3 (Instanz-Endpunkte), §5 (Auth-Pipeline), §7 |

## Ziel

Menschen können ihr Passwort ändern, Admins können es zurücksetzen, und das Bootstrap-Passwort bleibt nicht dauerhaft gültig. Danach sind vier kleinere Lücken aus den Phase-1-Reviews geschlossen: Altwerte bei `relation.multiple`, OpenAPI-Pflichtfelder, Activity-Events ohne Titel.

## Scope

**Drin:**

*1. Passwort ändern (eigenes Konto)*
- `PATCH /api/v1/me/password` mit Body `{ currentPassword, newPassword }` → 204. Nur Human-Actor mit Cookie-Sitzung; Agent oder API-Key → 403 mit `detail`. Falsches `currentPassword` → 400 mit `errors[{ field: "currentPassword", code: "invalid_credentials" }]`. `newPassword` nach `PasswordPolicy` (Länge), gleich dem alten → 400 `code: "unchanged"`.
- Nach Erfolg setzt der Server das Sitzungs-Cookie der aktuellen Anfrage neu (Benutzer bleibt angemeldet), alle älteren Sitzungen sind ungültig (siehe 3).

*2. Passwort zurücksetzen (Admin)*
- `PUT /api/v1/actors/{id}/password` mit Body `{ newPassword, mustChange = true }` → 204. `instance:admin`. Ziel muss Human sein (Agent → 400 `code: "not_human"`), fremde/unbekannte ID → 404. Fehlt ein `LocalLogin`, wird er angelegt.
- Setzt `LocalLogin.MustChangePassword = mustChange`.

*3. Sitzungen entwerten*
- `AuthenticateSession` bekommt den Ausstellungszeitpunkt des Cookies (`AuthenticationTicket.Properties.IssuedUtc`) und lehnt Sitzungen ab, die **vor** `LocalLogin.UpdatedAt` ausgestellt wurden (401). Damit entwertet jede Passwortänderung und jeder Reset alle bestehenden Cookies dieses Actors; Logout bleibt zustandslos.
- Zeitvergleich mit der Mikrosekunden-Rundung beachten (`MicrosecondTimeProvider`, Lehre aus Phase 1, `03-WORKFLOW.md` §7a): Das neu ausgestellte Cookie aus 1 darf nicht an derselben Mikrosekunde scheitern. Test auf Linux-Verhalten auslegen (keine Gleichheit von Ticks annehmen).

*4. Pflicht zur Änderung*
- Neue Spalte `local_logins.must_change_password` (bool, Default false), Migration `AddMustChangePassword`.
- Bootstrap legt den Admin mit `MustChangePassword = true` an. Beim Start: existiert der Bootstrap-Admin bereits und verifiziert sein Hash noch gegen das konfigurierte `AdminPassword`, wird das Flag gesetzt (deckt bestehende Installationen ab). Passwort nie loggen.
- `GET /me` liefert `mustChangePassword` (bool; für Agenten immer false). Erfolgreiches `PATCH /me/password` setzt das Flag zurück.
- Der Server sperrt bei gesetztem Flag **keine** anderen Endpunkte; die UI erzwingt die Änderung (AP-023). Im Bericht kurz begründen, falls du anders entscheidest.

*5. Schema-Hook `relation.multiple` false → true*
- `RecordValuesSchemaHook` wandelt bei dieser Änderung skalare Relation-Werte (String-ID) in ein Array mit genau diesem Element, wie bei select → multi_select. `null` bleibt `null`. Der umgekehrte Weg (true → false) bleibt, wie er heute behandelt wird; im Bericht festhalten, was das ist.

*6. OpenAPI-Pflichtfelder*
- Alle Request-Body-Records aus AP-004/005 (Identity, Workspaces, Members, inkl. direkt gebundener `…Request`-Records) bekommen Default-Werte für optionale Felder nach dem Muster aus AP-006, sodass `openapi/v1.json` nur echte Pflichtfelder als `required` führt. Neue Bodies aus diesem Paket gleich so.
- `openapi/v1.json` neu erzeugen und einchecken; `schema.d.ts` entsteht über `npm run api:generate` (läuft automatisch vor `npm run build`).

*7. Activity mit Titel*
- `ActivityEventDto` bekommt `TargetTitle` (string?, nullable). `ListActivity` löst für Events mit `TargetType` record (und collection/space, falls billig) den **aktuellen** Titel in einer gebündelten Abfrage auf (kein N+1). Gelöschte oder unbekannte Ziele → null. Tenant-Kontext gilt wie bisher.
- MCP-Tool für Activity liefert das Feld mit, Tool-Beschreibung ergänzen („targetTitle is the current title, null if the target no longer exists“).

**Ausdrücklich nicht drin:**
- Frontend-Code (AP-023), außer dem neu erzeugten `schema.d.ts`. Läuft das Frontend nach der Neuerzeugung nicht mehr grün, im Bericht melden statt Frontend-Code zu ändern.
- Rate-Limiting für Login und Passwort-Endpunkte, Passwort-vergessen per E-Mail, OIDC, serverseitige Session-Tabelle.
- Admin-Workspace-Liste, Actor-Suche für Workspace-Admins (eigene Kleinpakete).

## Fachlicher Kontext

- Cookies sind zustandslos (AP-004). Die Entwertung über `UpdatedAt` braucht keine Session-Tabelle, weil `AuthenticateSession` ohnehin bei jeder Anfrage den Actor lädt; das zusätzliche Laden von `LocalLogin` in derselben Abfrage ist gewollt.
- `LocalLogin.ChangePasswordHash` existiert bereits und setzt `UpdatedAt`.
- Fehler sind Hinweise für Agenten (§4.1): `code` maschinenlesbar, `detail` sagt, was zu tun ist.

## Akzeptanzkriterien

1. `PATCH /me/password` mit richtigem aktuellen Passwort → 204; Login mit altem Passwort danach 401, mit neuem 204.
2. Falsches `currentPassword` → 400 mit `invalid_credentials` am Feld; zu kurzes `newPassword` → 400 mit Längenfehler; gleiches Passwort → 400 `unchanged`.
3. `PATCH /me/password` per API-Key → 403.
4. Nach `PATCH /me/password` funktioniert das Cookie aus der Antwort, ein vorher abgegriffenes zweites Cookie desselben Actors → 401.
5. `PUT /actors/{id}/password` als Admin → 204, alle Cookies des Ziels → 401, `GET /me` des Ziels nach neuem Login zeigt `mustChangePassword: true`. Als Nicht-Admin → 403, für Agent → 400 `not_human`, unbekannte ID → 404.
6. Frisch gebooteter Admin hat `mustChangePassword: true`; nach eigener Änderung false. Bestehender Admin mit noch gültigem Bootstrap-Passwort bekommt das Flag beim Neustart.
7. Schemaänderung `relation.multiple` false → true wandelt Skalarwerte in Arrays; anschließender PATCH des Records validiert ohne Fehler.
8. `openapi/v1.json`: optionale Felder der Identity-/Workspace-/Member-Bodies stehen nicht mehr unter `required` (Test oder Snapshot-Prüfung auf mindestens drei Bodies).
9. `GET /activity` und das MCP-Activity-Tool liefern `targetTitle` mit aktuellem Titel; nach Umbenennen des Records der neue Titel; Ziel im fremden Workspace taucht nicht auf.
10. `dotnet build`, `dotnet test`, im Frontend `npm run build` und `npm test` grün.

## Tests, die mindestens entstehen

- Unit: Sitzungsprüfung mit IssuedUtc vor/nach/gleich `UpdatedAt` (Mikrosekunden), Hook relation Skalar → Array und null.
- Integration: Kriterien 1–9, Bootstrap-Flag (frisch und bestehend).
- Isolation: Reset auf Actor ist Instanz-Ebene (nur Admin-Test); Activity-Titel aus fremdem Workspace wird nicht aufgelöst.

## Entscheidungen, die der Implementierer treffen darf

- Route und Methode für den Reset (`PUT /actors/{id}/password` ist Vorschlag), sofern REST-konsistent und im Bericht genannt.
- Ob `targetTitle` neben Records auch für Collections/Spaces aufgelöst wird.
- Ob der Server bei `mustChangePassword` doch bestimmte Endpunkte sperrt (dann begründen).

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
| 2026-11-07 | an implementer-opus übergeben |
| 2026-11-07 | Bericht: 1042/1042 grün; eigener Cookie-Wert `agcowork.issued_at` (IssuedUtc nur Sekunden), `UpgradeHash` ohne `UpdatedAt`, fail closed ohne LocalLogin, Reset `PUT /actors/{id}/password`; Frontend-Fixtures vom Orchestrator nachgezogen |
| 2026-11-07 | Review: Freigabe mit Hinweisen – W1 Race Login ↔ Wechsel (Cookie mit altem Passwort überlebt), H2 Lost Update beim Rehash, H5 Titel-Escaping in MCP-Text, H6 null-Rückgabe im Hook; H1 → AP-023, H3 → Backlog (Rate-Limit) |
| 2026-11-07 | Nacharbeit an implementer-opus: Credential-Stamp statt Ausstellungszeit, bedingter Rehash, H4–H6 |
| 2026-11-07 | Nacharbeit: Stamp `agcowork.password_at` mit exakter Gleichheit (Race-Test ohne Fix rot), Rehash per bedingtem `ExecuteUpdate`, null-Pfad im Hook zurückgenommen, Titel-Escaping in `McpText.Activity`; 1044/1044 grün; Orchestrator hat Stamp-Prüfung gelesen |
| 2026-11-07 | abgeschlossen |
