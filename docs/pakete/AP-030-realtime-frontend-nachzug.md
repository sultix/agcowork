# AP-030 – Frontend-Kleinpaket Realtime: Reconnect-Backoff, Record-Zähler, Live für Mitglieder, Views und Schema

| Feld | Wert |
|---|---|
| Status | angelegt |
| Phase | 2 |
| Implementierer | implementer-opus |
| Referenzbeispiel | `src/frontend/src/app/core/realtime/realtime.service.ts` (Backoff, `attempt`), `features/views/live-refresh.ts` (Bündelung, Sperre, Generation), `features/spaces/workspace-tree.store.ts` (Live-Neuladen der Navigation) |
| Abhängig von | AP-025, AP-028 (committet) |
| Betroffene Pfade | `src/frontend/src/app/core/realtime/`, `core/workspace/members.store.ts`, `features/views/collection.store.ts`, `features/views/view-page.component.ts`, `features/views/view-switcher.component.ts` (nur falls die View-Liste dort geladen wird), jeweils mit Specs. **Nicht:** `features/records/` (AP-031 folgt dort), Shell, Backend |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §5 „Outbox-Pattern“ (AP-028: Trennen bei Zustellfehlern), §6 „Bausteine – Live“ |

## Ziel

Hält ein Zustellfehler im Backend an, verbinden sich die Clients nicht mehr im Sekundentakt neu und überlasten die Datenbank nicht zusätzlich. Mitglieder, Views, Schema und der Record-Zähler einer Collection aktualisieren sich live wie schon Tabelle und Feed.

## Scope

**Drin:**

*1. Reconnect-Backoff (AP-028-Review W1)*
- `attempt` wird erst zurückgesetzt, wenn eine Verbindung eine Mindestlebensdauer erreicht hat (Vorschlag 30 s, als Injection-Token wie `REALTIME_RETRY_DELAYS_MS`). Trennt der Server vorher erneut, steigt die Wartezeit weiter.
- Jitter auf jede Wartezeit (z. B. ±20 %), damit viele Clients nicht im Gleichtakt neu verbinden; auch die 0-ms-Stufe bekommt eine kleine zufällige Verzögerung (z. B. 0–1 s).
- Resync-Reloads nach Wiederverbindung bleiben, laufen aber nicht häufiger als die Wiederverbindungen selbst.
- Bestehende Garantien aus AP-025 (Position, `catchUps`, `attempting`, Handshake-Fenster) bleiben unverändert; ihre Tests bleiben grün.

*2. Record-Zähler*
- Der Zähler „N Records“ im View-Kopf (`CollectionStore.recordCount`, aus `collection describe`) lädt bei `record.created|archived|restored` der Collection und bei `resync` entprellt neu (Muster `liveRefresh`, ohne Sperre, da nicht editierbar).

*3. Live für Mitglieder, Views und Schema*
- `member.*` → MembersStore lädt neu (Namen im Feed, Actor-Auswahl).
- `view.*` der angezeigten Collection → View-Liste lädt neu; wird die angezeigte View gelöscht, Hinweis und Wechsel zur Standard-View statt Fehlerseite.
- `collection.schema_changed`/`collection.updated` der angezeigten Collection → Schema/Metadaten laden neu; läuft gerade ein Inline-Edit, greift die bestehende Sperre (kein Neuladen unter dem offenen Editor, danach nachholen).

**Ausdrücklich nicht drin:**
- Backend, Dokument-/Record-Seite, Kommentare, Shell-Statusanzeige.

## Akzeptanzkriterien

1. Trennt der Server eine frische Verbindung dreimal kurz nacheinander, steigen die Wartezeiten (kein Zurück auf 0); nach einer Verbindung, die die Mindestlebensdauer erreicht, beginnt der Backoff wieder unten (Fake-Timer-Test).
2. Wartezeiten haben Jitter im konfigurierten Rahmen (Test mit gefälschtem Zufall).
3. Record-Zähler steigt nach `record.created` der Collection ohne Reload.
4. `member.added` aktualisiert den Namen eines neuen Actors im Feed; gelöschte angezeigte View führt zur Standard-View mit Hinweis; Schemaänderung zeigt neue Spalte, ohne einen offenen Zelleditor zu stören.
5. Alle bestehenden Realtime-Tests grün; `npm run build`, `npm test`, `npm run lint`, `npm run format:check` grün.

## Tests, die mindestens entstehen

- Unit (Vitest): Backoff mit Mindestlebensdauer und Jitter, Zähler, MembersStore, View-Liste/gelöschte View, Schema-Neuladen mit Editor-Sperre. Jeder Schutz mit Gegenprobe (ohne Fix rot).

## Entscheidungen, die der Implementierer treffen darf

- Konkrete Mindestlebensdauer und Jitter-Spanne.
- Ob Members/Views/Schema über einen gemeinsamen Live-Helfer laufen.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-08 | angelegt |
