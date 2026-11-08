# AP-025 – Realtime Frontend: SignalR-Client, Live-Aktualisierung von Feed, Views, Navigation und Dokument

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | 2 |
| Implementierer | implementer-opus |
| Referenzbeispiel | – (erstes Auftreten); Workspace-Wechsel `src/frontend/src/app/core/workspace/workspace-context.service.ts`, Speichern/Konflikt `features/records/record-save.service.ts` und `conflict-dialog.component.ts`, Inline-Edit `features/views/inline-edit.service.ts`, Stores `features/views/collection.store.ts` |
| Abhängig von | AP-024 (Hub, `afterSeq`), AP-023 (Feed mit `targetTitle`) |
| Betroffene Pfade | neu `src/frontend/src/app/core/realtime/`, `features/activity/`, `features/views/` (Stores, Table, Board, view-page), `features/records/record-document.component.*` und `record-save.service.*`, `features/spaces/` bzw. Navigation in `features/workspace-shell/` (Reload + Statusanzeige), `package.json` (`@microsoft/signalr`) |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §6 „Routen“ (Wechsel der Gruppe), „Bausteine – Live“ |

## Ziel

Wer einen Workspace offen hat, sieht Änderungen anderer (vor allem von Agenten) ohne Neuladen: im Aktivitätsfeed, in Tabelle und Board, in der Navigation und im geöffneten Dokument. Ungespeicherte eigene Änderungen werden dabei nie überschrieben.

## Scope

**Drin:**

*1. `RealtimeService` (core/realtime)*
- Verbindet sich mit `/w/{slug}/hub`, sobald ein Workspace aktiv ist; beim Wechsel alte Verbindung schließen, neue öffnen; beim Verlassen schließen. Automatische Wiederverbindung mit Backoff.
- Stellt die eingehenden `activity`-Ereignisse als Stream/Signal bereit (dedupliziert nach `seq`) und einen Status `connecting | live | reconnecting | offline`.
- Nach einer Wiederverbindung meldet er „Lücke möglich“, damit Verbraucher neu laden (oder per `GET /activity?afterSeq=<letzte seq>` nachholen; Entscheidung des Implementierers).
- Statusanzeige in der Shell (kleiner Punkt/Badge mit Tooltip, Lucide-Icon), nur sichtbar, wenn nicht `live` oder per Tooltip.

*2. Aktivitätsfeed*
- Neue Ereignisse erscheinen oben, ohne dass bereits geladene Seiten springen oder doppelt erscheinen. Aktive Filter (Space, Collection, Actor, Typ) gelten auch für Live-Ereignisse.

*3. Views (Tabelle, Board)*
- Ereignisse zu Records der angezeigten Collection lösen ein entprelltes Neuladen der aktuellen Abfrage aus (≥ 500 ms Bündelung). Scroll-Position und Auswahl bleiben.
- Läuft gerade ein Inline-Edit oder ein Drag im Board, wird das Neuladen bis zum Ende verschoben; die bearbeitete Zelle wird nie durch Serverdaten ersetzt, solange sie offen ist.

*4. Navigation*
- Ereignisse zu Spaces und Collections (angelegt, umbenannt, archiviert) laden die Space-Navigation neu.

*5. Dokument*
- Ereignis zum geöffneten Record, das nicht vom eigenen Speichern stammt (Revision weicht von der lokal bekannten ab):
  - ohne lokale ungespeicherte Änderungen: Record still neu laden, kurzer Hinweis „Aktualisiert von <Actor>“;
  - mit ungespeicherten Änderungen: nicht neu laden, sondern Banner „<Actor> hat diesen Eintrag geändert“ mit „Neu laden (eigene Änderungen verwerfen)“ und „Weiter bearbeiten“. Speichern läuft danach wie bisher in den 409-Konfliktdialog.
- Archivierung des geöffneten Records durch andere: Banner, Bearbeitung bleibt möglich bis zum Speichern (dann bestehende Fehlerbehandlung).
- Eigene Speichervorgänge erzeugen keinen Hinweis (Abgleich über Revision bzw. Actor + laufendes Speichern).

**Ausdrücklich nicht drin:**
- Backend, Hub-Vertrag (AP-024).
- Presence, Cursor anderer, kollaboratives Echtzeit-Editieren (E2: Markdown, kein CRDT).
- Live-Aktualisierung von Historie und Raw-View über einen Hinweis hinaus (Raw darf das Dokument-Verhalten aus 5 übernehmen, wenn es ohne Umbau geht).

## Fachlicher Kontext

- Phase-1-Blocker waren Datenverlust-Pfade (Overwrite-Body, Konfliktdialog-Fokus). Jede Live-Aktualisierung, die lokale Eingaben verwerfen könnte, braucht einen Test, der ohne die Schutzlogik rot wäre.
- `ActivityEventDto` enthält `seq`, `type`, `actorId`, `targetType`, `targetId`, `spaceId`, `collectionId`, `diff`, `targetTitle`. Actor-Namen kommen aus dem vorhandenen Members-Store.
- Eine Verbindung pro Browser-Tab genügt; mehrere Tabs sind unabhängig.
- Hub-Vertrag aus AP-024: `/w/{slug}/hub`, Cookie, Server → Client nur `activity` (ActivityEventDto), Client → Server nur `Ping`. JSON wie REST: `null`-Felder fehlen (z. B. `targetTitle` bei Member-/View-Ereignissen), also `undefined` wie `null` behandeln. Zustellung höchstens einmal, keine Nachlieferung alter Ereignisse beim Verbinden.
- Nachholen nach Wiederverbindung über `GET /activity?afterSeq=<lastSeq>`: Die Antwort liefert in Commit-Reihenfolge (eine kleinere `seq` kann nach einer größeren kommen) und `lastSeq`, das immer als nächster `afterSeq` weitergegeben wird. Dedupe nach `seq` als Menge, nicht über „größte gesehene seq“. Startpunkt: `lastSeq` aus der ersten (absteigenden) Feed-Seite.
- Entzogene Rechte trennen die Verbindung serverseitig; Reconnect liefert dann 404/403, darauf nicht endlos neu verbinden (Status `offline`).

## Akzeptanzkriterien

1. Workspace-Wechsel schließt die alte und öffnet eine neue Verbindung; Verlassen schließt sie (Test mit Fake-Hub).
2. Feed: Live-Ereignis erscheint oben, Duplikat (gleiche `seq`) nicht, gefiltertes Ereignis nicht.
3. Tabelle/Board: mehrere Ereignisse in kurzer Folge → genau ein Neuladen; während Inline-Edit kein Neuladen, danach eines; offene Zelle behält Eingabe.
4. Dokument ohne lokale Änderungen: fremdes Ereignis → neu geladen + Hinweis. Mit lokalen Änderungen: kein Neuladen, Banner; „Neu laden“ verwirft, „Weiter bearbeiten“ behält.
5. Eigenes Speichern erzeugt keinen Hinweis.
6. Nach Wiederverbindung laden betroffene Ansichten neu (oder holen nach).
7. Statusanzeige zeigt `reconnecting`/`offline`.
8. `npm run build`, `npm test`, `npm run lint`, `npm run format:check` grün.
9. Orchestrator-Rauchtest im Compose-Stack: Agent legt per MCP/REST einen Record an, Tabelle und Feed im Browser zeigen ihn ohne Reload.

## Tests, die mindestens entstehen

- Unit (Vitest): RealtimeService mit gefälschtem `HubConnection` (Verbinden, Wechsel, Reconnect, Dedupe), Feed, Views-Entprellung und Inline-Edit-Sperre, Dokument-Fälle aus 4 und 5.

## Entscheidungen, die der Implementierer treffen darf

- Neu laden oder per `afterSeq` nachholen nach Wiederverbindung.
- Gestaltung von Statusanzeige, Hinweis und Banner im Rahmen von §6.
- Wie „eigenes Speichern“ erkannt wird, sofern Kriterium 5 belegt ist.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
| 2026-11-08 | an implementer-opus übergeben (parallel zur Nacharbeit AP-024); nach Nutzungslimit per Nachricht fortgesetzt |
| 2026-11-08 | Bericht: 767 Tests grün, Gegenproben (Schutzlogik entfernt → rot); Nachholen per `afterSeq` zentral im Service, Fortsetzung ab letzter Live-seq, eigener Reconnect mit REST-Vorabprüfung (401/403/404 → offline), „eigenes Speichern“ über Revisionsabgleich, SignalR lazy geladen; Status `idle` zusätzlich |
| 2026-11-08 | Review gestartet; Orchestrator hat `proxy.conf.js` (aus AP-024) per Prettier formatiert |
| 2026-11-08 | Review: **Blockiert** – B1 `retry()` legt neuen Link an und verliert die Position (kein Nachholen, kein Resync), B2 stilles Neuladen verwirft ungültige/halb getippte Property-Eingaben (JSON, Zahl, Datum melden nicht an den Saver); wichtig: veraltetes Refresh-Ergebnis überschreibt Edit aus dem Fetch-Fenster (falscher 409), Handshake-Fenster rückt Position vor; Live-seq als Cursor bestätigt |
| 2026-11-08 | Nacharbeit an implementer-opus (B1, B2, W1, W2a, Hinweise) |
| 2026-11-08 | Nacharbeit: `retry()` auf bestehendem Link, Hook `hasLocalInput` (ungültig, Leave-Dialog, Fokus → Banner), Edit-Generation in `liveRefresh`, `catchingUp` vor `hub.start()`, Revision erst ohne Body, Debounce 500/2000 ms; 777 Tests |
| 2026-11-08 | Nachprüfung: B2, W1, W2a freigegeben; **Blockiert** durch neuen N1 aus dem B1-Fix (Abbruch während Nachholen → `attempting` verschluckt den automatischen Retry, Status bleibt `reconnecting`); Hinweis Banner-Text bei „nur Fokus“ |
| 2026-11-08 | zweite Nacharbeit an implementer-opus (N1, Banner-Text) |
| 2026-11-08 | Nacharbeit: `attempting` nur über Probe und `hub.start()`, Zähler `catchUps` (nur jüngster Nachhol-Lauf setzt Position/Resync), Banner-Text nach Fall; 779 Tests |
| 2026-11-08 | Nachprüfung: Freigabe |
| 2026-11-08 | abgeschlossen (Kriterium 9, Rauchtest im Compose-Stack, folgt nach AP-026) |
