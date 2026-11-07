# AP-024 – Realtime Backend: Commit-sichere Activity-Reihenfolge, SignalR-Hub, Auslieferung pro Workspace

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | 2 |
| Implementierer | implementer-opus |
| Referenzbeispiel | – (erstes Auftreten von SignalR und Hintergrunddiensten); Tenancy-Muster `src/backend/Agcowork.Api/Tenancy/`, Activity `src/backend/Agcowork.Application/Activity/` |
| Abhängig von | AP-022 (committet: `ActivityEventDto.TargetTitle`, Migration) |
| Betroffene Pfade | `src/backend/Agcowork.Domain/Activity/`, `src/backend/Agcowork.Application/Activity/`, neues Modul `src/backend/Agcowork.Application/Realtime/`, `src/backend/Agcowork.Infrastructure/` (Persistence: **eine** Migration; Realtime-Dispatcher), `src/backend/Agcowork.Api/` (Hub, Auth für den Hub, `Program.cs`, `openapi/v1.json`), `src/backend/Agcowork.Api/Mcp/` (nur `activity_recent`), `src/frontend/nginx.conf` und `src/frontend/proxy.conf.js` (nur Hub-Weiterleitung mit WebSocket-Upgrade), `tests/` |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §2a (Schritt 1 und 6), §4.1, §4.2 (`activity_recent`), §5 „Outbox-Pattern“, „Reihenfolge nie über die Id“ |

## Ziel

Menschen sehen Änderungen von Agenten und anderen Menschen live, ohne neu zu laden. Agenten bekommen über `activity_recent` und `GET /activity` einen Vorwärts-Cursor, der kein Ereignis überspringt, auch wenn Transaktionen in anderer Reihenfolge committen, als ihre `Seq` vergeben wurde.

## Scope

**Drin:**

*1. Commit-sichere Sichtbarkeit (behebt AP-007-Hinweis „Seq ist Identity“)*
- `activity_events` bekommt die Spalte `xact_id xid8 NOT NULL DEFAULT pg_current_xact_id()` (Migration `AddActivityXactId`; Bestandszeilen dürfen einen Füllwert bekommen, der sie sofort sichtbar macht).
- Ein Ereignis gilt als **stabil**, wenn `xact_id < pg_snapshot_xmin(pg_current_snapshot())`. Damit ist garantiert: Alle Ereignisse mit kleinerer `Seq` sind entweder sichtbar oder gehören zu einer Transaktion, die nie committet. Vorwärtslesende Abfragen liefern nur stabile Ereignisse.
- Vorschlag ist dieses Muster ohne eigene Outbox-Tabelle: `activity_events` **ist** die Outbox. Weicht der Implementierer ab (eigene Outbox-Tabelle, LISTEN/NOTIFY als Weckruf), begründet er es im Bericht; die Garantie oben bleibt Pflicht.

*2. Vorwärts-Cursor für Agenten*
- `GET /api/v1/w/{slug}/activity` und MCP `activity_recent` bekommen `afterSeq` (long): Ereignisse mit `Seq > afterSeq`, **aufsteigend**, nur stabile, höchstens `limit`. Antwort enthält `lastSeq` (höchste gelieferte `Seq`, sonst `afterSeq`), damit der Agent weiterpollt. Ohne `afterSeq` bleibt das bisherige absteigende Blättern unverändert.
- Zusätzlich Filter `targetId` (Index existiert), damit ein Agent die Geschichte eines Records abfragen kann (AP-011-Hinweis).
- Beschreibungen in OpenAPI und Tool für Agenten: wann `afterSeq`, wann `since`, warum nicht `since` zum Pollen.

*3. SignalR-Hub*
- Hub unter `/w/{slug}/hub` (Workspace-Präfix wie REST und MCP). Authentifizierung per Cookie (Menschen) oder Bearer (Agenten, für Tests und spätere Clients). Beim Verbinden läuft dieselbe Tenant-Auflösung wie bei REST (Mitgliedschaft, Key-Einschränkung; fremd/unbekannt → Verbindung abgelehnt, ohne Existenz zu verraten); `read` ist Pflicht. Die Verbindung tritt der Gruppe `ws:{WorkspaceId}` bei.
- Server → Client nur ein Ereignistyp: `activity` mit `ActivityEventDto` (inkl. `seq`, `targetTitle`). Client → Server: keine Methoden außer ggf. `Ping`. Der Hub ist ein Adapter: keine Fachlogik.

*4. Dispatcher*
- `BackgroundService`, der stabile Ereignisse ab dem zuletzt ausgelieferten `Seq` liest (instanzweit, mit `IgnoreQueryFilters` an genau dieser begründeten, kommentierten Stelle), nach `WorkspaceId` an die Gruppe sendet und den Cursor im Speicher hält. Start-Cursor: aktuelles Maximum beim Start (kein Nachliefern alter Ereignisse an neue Verbindungen).
- Takt: kurzes Polling (≤ 500 ms) oder LISTEN/NOTIFY als Weckruf plus Polling als Netz. Keine Last ohne neue Ereignisse über einfache Abfragen hinaus.
- Annahme Single-Instance (wie Bootstrap); im Code und in der Architektur vermerken, dass mehrere Instanzen einen Backplane bräuchten.

*5. Mitgliedschaft entzogen*
- Wird eine Mitgliedschaft entfernt, auf eine Rolle ohne `read` gesetzt oder der Actor deaktiviert, bekommt er über bestehende Verbindungen keine weiteren Ereignisse dieses Workspaces (Verbindungen dieses Actors aus der Gruppe nehmen oder trennen). Ein widerrufener Key verliert seine Verbindungen spätestens beim nächsten Ereignis.

*6. Weiterleitung*
- `src/frontend/nginx.conf` (Compose) und `src/frontend/proxy.conf.js` (Dev-Server) leiten `/w/{slug}/hub` inkl. WebSocket-Upgrade und Negotiate an die API weiter, analog zur MCP-Location. Lange Verbindungen nicht durch Proxy-Timeouts kappen.

*7. Doku*
- `02-ARCHITEKTUR.md` §5 „Outbox-Pattern“ und §2a Schritt 6 an die Umsetzung anpassen (der Implementierer liefert den Textvorschlag im Bericht; der Orchestrator trägt ein).

**Ausdrücklich nicht drin:**
- Frontend (AP-025).
- Webhooks/Subscriptions (Phase 3), Backplane/Redis, Presence („wer ist gerade hier“), Tipp-Indikatoren.
- Eigene Ereignistypen neben `activity` (Kommentare usw. kommen über Activity, sobald es sie gibt).

## Fachlicher Kontext

- Heute schreiben Use-Cases das ActivityEvent in derselben `SaveChanges`-Transaktion wie die Änderung (`IActivityRecorder`). `Seq` ist eine Identity-Spalte und wird beim INSERT vergeben, nicht beim Commit. Zwei parallele Transaktionen können deshalb Seq 11 vor Seq 10 sichtbar machen; ein Leser mit `afterSeq = 11` verliert sonst Ereignis 10.
- `pg_snapshot_xmin(pg_current_snapshot())` ist die älteste noch laufende Transaktion. Lange Transaktionen verzögern die Auslieferung; das ist akzeptiert.
- Die Mikrosekunden-Uhr (§7a) betrifft `At`, nicht `Seq`.

## Akzeptanzkriterien

1. Zwei Transaktionen: A schreibt ein Ereignis (Seq n) und bleibt offen, B schreibt (Seq n+1) und committet. `afterSeq = n-1` liefert nichts, bis A committet; danach n und n+1 in dieser Reihenfolge. Bricht A ab, kommt n+1.
2. `afterSeq` liefert aufsteigend, respektiert `limit`, gibt `lastSeq` zurück; ohne `afterSeq` bleibt das alte Verhalten (bestehende Tests grün).
3. `targetId`-Filter liefert nur Ereignisse dieses Ziels.
4. Hub: Mitglied mit `read` verbindet sich und empfängt ein `activity`-Ereignis nach `POST /records` innerhalb von 2 s, mit `targetTitle`.
5. Hub: Nicht-Mitglied, unbekannter Slug, auf anderen Workspace eingeschränkter Key → Verbindung abgelehnt. Mitglied von `alpha` empfängt keine Ereignisse aus `beta`.
6. Nach Entfernen der Mitgliedschaft empfängt eine bestehende Verbindung keine weiteren Ereignisse.
7. MCP `activity_recent` mit `afterSeq` und `targetId` wie REST; Isolationstest.
8. `dotnet build`, `dotnet test` grün; `npm run build` im Frontend weiter grün (nur neu erzeugtes `schema.d.ts`).

## Tests, die mindestens entstehen

- Integration (Testcontainers): Kriterium 1 mit zwei echten Verbindungen/Transaktionen, Kriterien 2, 3, 7.
- Integration Hub (`Microsoft.AspNetCore.SignalR.Client` gegen `WebApplicationFactory`): Kriterien 4–6.
- Isolation: `AssertIsolatedAsync` für `GET /activity?afterSeq=…`; Hub-Ablehnung für fremden Workspace.

## Entscheidungen, die der Implementierer treffen darf

- Eigene Outbox-Tabelle statt `xact_id` an `activity_events` (begründen, Garantie bleibt).
- Polling-Intervall, LISTEN/NOTIFY ja/nein.
- Ob entzogene Mitglieder getrennt oder nur aus der Gruppe genommen werden.
- Name des Felds `lastSeq`, sofern im Tool und in OpenAPI beschrieben.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
| 2026-11-07 | an implementer-opus übergeben |
| 2026-11-08 | Bericht: 1069 Tests grün; **Korrektur des Pakets**: Vorwärtslesen nach (`xact_id`, `seq`), weil eine Transaktion mit früher xid eine höhere Seq zuerst committen kann (Test belegt Verlust bei reiner Seq-Ordnung); keine eigene Outbox-Tabelle, Polling 250 ms, Entzug vor jeder Zustellung geprüft, `ActivityPage` mit `lastSeq` |
| 2026-11-08 | Review: Freigabe mit Hinweisen – Lückenfreiheit bestätigt (auch Savepoints, PG 17); wichtig: langsamer Empfänger blockiert Dispatcher, Rennfenster Seite/Head im Rückblick, keine Origin-Prüfung am Hub (CSWSH); Hinweise: Kommentar/Doku zu Item-seq, Zeilenvergleich statt OR, Wortlaut „commit order“, leere Scopes im RealtimeTenant, Backoff |
| 2026-11-08 | Nacharbeit an implementer-opus (alle Punkte); AP-025 parallel gestartet |
| 2026-11-08 | Nacharbeit: Postausgang pro Verbindung mit Sende-Timeout, Head vor Seite (Test ohne Fix rot), Origin-Prüfung für Cookie-Verbindungen (GET ohne Origin erlaubt, weil Browser ihn same-origin weglassen und Lax-Cookies cross-site dort nicht mitgehen), Backoff, Wortlaut; 1087/1087 grün |
| 2026-11-08 | abgeschlossen |
