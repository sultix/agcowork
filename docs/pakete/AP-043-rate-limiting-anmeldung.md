# AP-043 – Rate-Limiting für Anmeldung und Passwortänderung

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | 2 |
| Implementierer | implementer-opus |
| Referenzbeispiel | Auth-Pipeline `src/backend/Agcowork.Api/Auth/`, Endpunkte `Agcowork.Api/Endpoints/Identity/` (`POST /auth/login`, `PATCH /me/password` in `MeEndpoints.cs`), strukturierte Fehler wie in den übrigen Endpunkten; Frontend `src/frontend/src/app/features/auth/login.component.ts`, `change-password.component.ts` |
| Abhängig von | AP-042 (nacheinander wegen gemeinsamer Build-Ausgabe) |
| Betroffene Pfade | `src/backend/Agcowork.Api/{Auth,Endpoints/Identity,Program.cs}`, ggf. `Agcowork.Application/Identity/`, `appsettings*.json`, `docker-compose*.yml`/nginx-Konfiguration nur für Forwarded-Header, `tests/`; Frontend nur `features/auth/` (429-Meldung). **Nicht:** Records, Views, MCP-Tools |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §5/§7 (Sicherheit, Betrieb) |

## Ziel

Wer ein Passwort raten will – über den Login oder mit einem gestohlenen Cookie über `PATCH /me/password` –, wird nach wenigen Fehlversuchen gebremst. Legitime Nutzer merken davon nichts, und Agenten mit API-Keys sind nicht betroffen.

## Scope

**Drin:**
1. **Login:** Fehlversuche begrenzen je **Konto** (normalisierte E-Mail) und je **Client-IP**. Vorschlag: je Konto 5 Fehlversuche in 15 min, danach Sperre mit wachsender Wartezeit (z. B. 1, 5, 15 min); je IP 20 Fehlversuche in 15 min. Erfolgreicher Login setzt den Konto-Zähler zurück. Gesperrt → **429** mit `Retry-After` und strukturiertem Fehler (`code: rate_limited`, `retryAfterSeconds`), **ohne** zu verraten, ob das Konto existiert (unbekannte E-Mails werden genauso gezählt und beantwortet).
2. **Passwortänderung:** falsches `currentPassword` zählt je Actor; gleiche Grenzen und Antwort. Nach Sperre auch richtige Passwörter erst nach Ablauf.
3. **Client-IP:** über Forwarded-Header nur von bekannten Proxies (Web-Container/nginx im Compose-Netz); konfigurierbar (`KnownProxies`/`KnownNetworks`). Ohne Konfiguration zählt die Verbindungs-IP. Kein blindes Vertrauen in `X-Forwarded-For`.
4. **Speicher:** In-Memory (Single-Instance-Annahme, im Bericht und in `02-ARCHITEKTUR.md`-Hinweis vermerken), mit Begrenzung der Einträge (kein Speicherwachstum durch zufällige E-Mails) und `TimeProvider` für Tests.
5. **Konfiguration** in `appsettings.json` (Grenzen, Fenster, Sperrdauern), Abschalten nur für Tests.
6. **Frontend:** Login und Passwortänderung zeigen bei 429 „Zu viele Versuche. Bitte in N Minuten erneut versuchen.“ (aus `retryAfterSeconds`).
7. **Logging/Activity:** Sperren werden geloggt (ohne Passwort, E-Mail gekürzt/gehasht nach vorhandenem Muster).

**Ausdrücklich nicht drin:**
- Verteilte Speicher (Redis), CAPTCHA, Rate-Limits für API-Key-Requests, Session-Revocation.

## Fachlicher Kontext

- Aus dem AP-022-Review: `PATCH /me/password` und `POST /auth/login` haben kein Limit (Brute-Force auf `currentPassword` mit gestohlenem Cookie, Dauer-Logins).
- Passwort-Hashing ist absichtlich teuer; ohne Limit ist der Login zugleich ein CPU-DoS-Hebel. Gesperrte Versuche sollen den Hash **nicht** berechnen.

## Akzeptanzkriterien

1. 6. Fehlversuch für dasselbe Konto innerhalb des Fensters → 429 mit `Retry-After`; nach Ablauf (TimeProvider) wieder möglich.
2. Unbekannte und bekannte E-Mail sind in Status, Body und (grob) Laufzeit nicht unterscheidbar.
3. Viele E-Mails von einer IP → IP-Grenze greift; anderer Client (andere IP) unberührt.
4. Gespoofter `X-Forwarded-For` von einem nicht vertrauenswürdigen Client ändert die gezählte IP nicht.
5. Passwortänderung: 6 falsche `currentPassword` → 429; API-Key-Requests ohne Einfluss.
6. Gesperrte Versuche berechnen keinen Passwort-Hash (Test über Zähler/Fake-Hasher).
7. Frontend zeigt die 429-Meldung (Unit-Test).
8. `dotnet test`, `npm test`, `npm run build`, Lint, Format grün.

## Tests, die mindestens entstehen

- Unit: Limiter-Logik mit TimeProvider (Fenster, Backoff, Reset, Eintragsbegrenzung).
- Integration: Login/Passwort 429, Forwarded-Header, unbekannte E-Mail.
- Frontend: 429-Meldungen.

## Entscheidungen, die der Implementierer treffen darf

- Konkrete Grenzen und Backoff-Stufen, ASP.NET-Core-RateLimiter vs. eigener Zähler (Konto-Sperre braucht vermutlich eigenen Zähler), Ort im Layering (Policy in Application, Speicher in Infrastructure/Api).

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-09 | angelegt |
| 2026-11-09 | an implementer-opus übergeben (AP-045 liegt uncommittet im Frontend, berührt `features/auth/` nicht) |
| 2026-11-09 | Bericht: 1533 Backend- und 1387 Frontend-Tests grün, zwölf Gegenproben, Compose-Probe mit gespooftem XFF; Konto-Pseudonym, IP je /64, Actor; Sperren 1/5/15 min; Prüfung vor Lookup und Hash; laufende Versuche zählen mit; In-Memory mit 50 000 Einträgen; Forwarded-Header nur von konfigurierten Proxies (Compose: festes Subnetz, feste web-IP); 429 `rate_limited` mit `retryAfterSeconds`/`Retry-After`; Abschalten außerhalb Development verhindert den Start; Abweichung: optionales Feld in `core/api/api-error.ts` |
| 2026-11-09 | Orchestrator: Startsperre bei abgeschaltetem Limit und festes, per Variable änderbares Compose-Subnetz bestätigt |
| 2026-11-09 | Review gestartet |
| 2026-11-09 | Review: Freigabe mit Hinweisen – Enumeration, Pending/Settle, Forwarded-Header unter .NET 10 und Verdrängung geprüft (Aufheben einer Sperre kostet ~180 000 Hashes); wichtig: hinter einem weiteren Proxy teilen sich unbemerkt alle Nutzer ein IP-Limit, Architektur-Hinweis fehlte (Orchestrator trägt §7/§8 ein); Hinweise: gesperrte Einträge von Verdrängung ausnehmen, „1 Minute“ bei 1 s, Obergrenzen für Fenster, Ort der Startsperre, `Retry-After` in OpenAPI; Compose legt das Netz bei Änderung selbst neu an (kein manuelles `down`) |
| 2026-11-09 | Nacharbeit an implementer-opus (Warnung bei unvollständig vertrauter Proxy-Kette, README-Abschnitt TLS-Proxy, H3–H7) |
| 2026-11-09 | Nacharbeit: Warnung bei ungenutztem `X-Forwarded-For` (drei Fälle, gedrosselt), README-Abschnitt TLS-Proxy, gesperrte/laufende Einträge nie verdrängt (volle Tabelle → 429 für neue Schlüssel, vom Orchestrator als „fail closed“ akzeptiert und dokumentiert), „gleich erneut versuchen“ unter 60 s, Obergrenze 1 Tag, `AddCredentialThrottling` mit Startsperre, `Retry-After` in OpenAPI; 1549 + 1389 Tests, neun Gegenproben |
| 2026-11-09 | abgeschlossen (Orchestrator hat build/test/lint/format im Worktree geprüft) |
| 2026-11-09 | Rauchtest (Wegwerf-Stack mit eigenem Subnetz): 6. Fehlversuch → 429 `rate_limited` mit `Retry-After: 60`; 20 Versuche mit gefälschten `X-Forwarded-For` vom Host zählen alle auf die Gateway-Adresse, danach IP-Sperre 15 min; API-Log warnt mit konkretem Hinweis; Login-Seite zeigt „Zu viele Versuche. Bitte in 15 Minuten erneut versuchen.“ |
