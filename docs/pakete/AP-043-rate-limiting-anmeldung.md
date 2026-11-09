# AP-043 – Rate-Limiting für Anmeldung und Passwortänderung

| Feld | Wert |
|---|---|
| Status | in Arbeit |
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
