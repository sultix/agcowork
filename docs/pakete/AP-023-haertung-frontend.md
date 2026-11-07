# AP-023 – Härtung Frontend: Passwort-UI, Idempotency-Key, Activity-Titel, Health-Status, dompurify

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | Härtung (vor Phase 2) |
| Implementierer | implementer-sonnet |
| Referenzbeispiel | Formular: `src/frontend/src/app/features/auth/login.component.ts`; Admin-Aktion mit Dialog: `features/admin/actor-keys.component.ts` und `shared/ui/confirm-dialog.component.ts`; Guard: `core/auth/auth.guard.ts`; Session: `core/auth/session.store.ts`; API-Aufrufe: `core/api/api-client.ts`; UI-Bausteine und Mapping: `docs/02-ARCHITEKTUR.md` §6 |
| Abhängig von | AP-022 (Endpunkte und `schema.d.ts` müssen committet sein) |
| Betroffene Pfade | `src/frontend/src/app/core/api/` (ApiClient, HealthService), `core/auth/`, `features/auth/` (neue Passwort-Seite), `features/admin/actors.component.*`, `features/activity/`, `features/records/record-create-dialog.component.*`, die Stelle in `features/workspace-shell/`, die den Health-Status zeigt, Routen-Datei(en) für die neue Seite, `src/frontend/package.json`/`package-lock.json` (dompurify) |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §5 (Auth), §6 (Frontend, UI-Bausteine) |

## Ziel

Menschen ändern ihr Passwort in der UI und werden nach dem ersten Login mit Bootstrap-Passwort dazu gezwungen; Admins setzen Passwörter zurück. Dazu: sichere Wiederholung beim Anlegen von Records, sprechende Einträge im Aktivitätsfeed, ein Health-Status, der sich erholt, und kein kritischer `npm audit`-Fund mehr.

## Scope

**Drin:**

*1. Passwort ändern*
- Neue Seite (z. B. `/account/password`), erreichbar aus dem Benutzermenü der Shell. Felder: aktuelles Passwort, neues Passwort, Wiederholung (Abgleich clientseitig). Ruft `PATCH /api/v1/me/password`; Feldfehler aus `errors[]` (`invalid_credentials`, `unchanged`, Länge) am jeweiligen Feld über die vorhandene `field-errors`-Hilfe. Nach Erfolg Hinweis und Rückkehr zur vorherigen Seite bzw. Workspace-Liste; Session neu laden (`GET /me`).
- Erzwingen: Liefert `GET /me` `mustChangePassword: true`, leitet ein Guard jede geschützte Route auf die Passwort-Seite um (Logout bleibt möglich). Die Seite zeigt dann einen erklärenden Text statt „Abbrechen“.

*2. Passwort zurücksetzen (Admin)*
- In `features/admin/actors.component` für Human-Actors eine Aktion „Passwort zurücksetzen“: Dialog mit neuem Passwort, Wiederholung und Checkbox „Muss beim nächsten Login geändert werden“ (Standard an). Ruft den Reset-Endpunkt aus AP-022 (Route laut AP-022-Bericht/OpenAPI). Für Agenten nicht angeboten.

*3. Idempotency-Key*
- `ApiClient` bekommt eine optionale `headers`-Option pro Request (typisiert als `Record<string, string>`), die zusätzlich zu `Accept` gesendet wird.
- `record-create-dialog` erzeugt pro Absicht einen Key (`crypto.randomUUID()`), wenn der Dialog öffnet bzw. nach erfolgreichem Anlegen neu, und sendet ihn als `Idempotency-Key` bei `POST /records`. Wiederholtes Absenden nach Netzwerkfehler nutzt denselben Key.

*4. Aktivitätsfeed*
- Einträge zeigen `targetTitle` als Link-Text; ist es null, bleibt der bisherige Text („Öffnen“ bzw. Typ + gekürzte ID).

*5. Health-Status*
- `HealthService` unterscheidet drei Zustände: ok, eingeschränkt (`Degraded` bzw. 503 mit Antworttext), nicht erreichbar (Netzwerkfehler). Bei „nicht erreichbar“ oder „eingeschränkt“ fragt er alle 15 s erneut nach, bis „ok“; die Anzeige in der Shell bietet zusätzlich einen Klick zum sofortigen Neuladen. Kein Polling im Zustand ok.

*6. dompurify*
- Kritischen Fund `dompurify` unter `monaco-editor` beheben: zuerst Monaco auf eine Version mit gefixtem dompurify heben; geht das nicht ohne Bruch, `overrides` für `dompurify` in `package.json`. `npm audit --omit=dev` zeigt danach keinen kritischen Fund mehr (Ausgabe im Bericht).

**Ausdrücklich nicht drin:**
- Backend-Änderungen, `openapi/v1.json`, `schema.d.ts` von Hand.
- Andere Admin-Funktionen, Umbau der Shell über den Menüpunkt hinaus, Passwort-vergessen-Flow.
- Playwright-Tests (eigenes Paket).

## Fachlicher Kontext

- Nach `PATCH /me/password` setzt der Server ein neues Cookie; andere Sitzungen desselben Actors werden ungültig. Ein 401 in einem anderen Tab führt über den bestehenden Interceptor zum Login, das ist gewollt.
- Der Server sperrt bei `mustChangePassword` keine anderen Endpunkte (AP-022); die UI ist hier die Durchsetzung.
- Helm-Bausteine und Mapping-Tabelle aus §6 sind verbindlich (Formular wie Login, Dialog wie die Key-Erzeugung).

## Akzeptanzkriterien

1. Passwort-Seite: erfolgreiches Ändern ruft den Endpunkt mit beiden Feldern, zeigt Erfolg und lädt die Session neu; Serverfehler erscheinen am richtigen Feld; abweichende Wiederholung verhindert das Absenden.
2. Mit `mustChangePassword: true` landet jede geschützte Route auf der Passwort-Seite; nach Erfolg nicht mehr.
3. Admin-Reset: Aktion nur bei Human-Actors, Dialog sendet `newPassword` und `mustChange`, Fehler werden angezeigt.
4. `POST /records` aus dem Anlage-Dialog trägt `Idempotency-Key`; ein zweiter Absendeversuch nach Fehler nutzt denselben Wert, ein neuer Dialog einen neuen.
5. Aktivitätsfeed zeigt `targetTitle`, Fallback bei null.
6. HealthService: drei Zustände; erneute Abfrage im Fehlerzustand, keine im ok-Zustand (Test mit Fake-Timern).
7. `npm audit --omit=dev` ohne kritischen Fund.
8. `npm run build`, `npm test`, `npm run lint`, `npm run format:check` grün.

## Tests, die mindestens entstehen

- Unit (Vitest): Passwort-Seite (Erfolg, Feldfehler, Wiederholung), Guard bei `mustChangePassword`, Reset-Dialog, ApiClient-Header, Idempotency-Key-Wiederverwendung, Feed-Titel mit Fallback, HealthService-Zustände und Polling.

## Entscheidungen, die der Implementierer treffen darf

- Route der Passwort-Seite und Platz im Benutzermenü.
- Monaco-Update oder `overrides` (siehe 6).

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
| 2026-11-07 | an implementer-sonnet übergeben (parallel zum Review von AP-022); Zusatz aus AP-022-Review: kein Reset aufs eigene Konto |
| 2026-11-07 | Bericht: 709 Tests grün, `npm audit --omit=dev` 0 Funde über `overrides` dompurify ^3.4.17 (Monaco 0.57 ist aktuell), Route `/account/password` mit `signedInGuard`, Health `checking/ok/degraded/unreachable` |
| 2026-11-07 | Review: Freigabe mit Hinweisen – Override erreicht Monacos gebündelte DOMPurify-Kopie nicht (→ Backlog), `crypto.randomUUID` fehlt ohne Secure Context (Compose über HTTP), Live-Region im Button, Abbrechen während Speichern, doppelte Passwortregeln, Fokus nach Erfolg |
| 2026-11-07 | Nacharbeit an implementer-sonnet |
| 2026-11-07 | Nacharbeit: `core/util/uuid.ts` mit `getRandomValues`-Fallback, Live-Region neben dem Status-Button, Reset-Dialog während Speichern nicht schließbar, `core/auth/password-policy.ts` gemeinsam, Fokus auf „Weiter“; 713 Tests, Orchestrator hat build/test/lint/format nachgeprüft |
| 2026-11-07 | abgeschlossen |
