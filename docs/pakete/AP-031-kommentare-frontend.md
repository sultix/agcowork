# AP-031 – Kommentare Frontend: Seitenleiste im Dokument, Threads, Erwähnungs-Auswahl, live

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | 2 |
| Implementierer | implementer-opus |
| Referenzbeispiel | Dokument und Speichern `features/records/record-document.component.ts`, `record-save.service.ts`; Live `core/realtime/realtime.service.ts` und `features/views/live-refresh.ts`; Markdown-Anzeige `shared/editor/` (Raw/Preview), Members `core/workspace/members.store.ts`; UI-Bausteine `02-ARCHITEKTUR.md` §6 |
| Abhängig von | AP-029 (Endpunkte committet), AP-030 (committet, Members-Store live) |
| Betroffene Pfade | neu `src/frontend/src/app/features/comments/`; geändert `features/records/record-document.component.*` (Seitenleiste einbinden, Umschalter), ggf. `record-tabs.component.ts`; nicht Shell (AP-032), nicht Backend |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §2 „Kommentare und Erwähnungen“, §6 „Bausteine – Document“ (Kommentar-Seitenleiste) |

## Ziel

Menschen lesen und führen Diskussionen direkt am Record, sehen Kommentare von Agenten live und erwähnen Agenten oder Kollegen mit `@`, ohne IDs zu kennen.

## Scope

**Drin:**
- Kommentar-Seitenleiste im Dokument (ein-/ausklappbar, Zustand je Tab gemerkt), Zähler offener Threads am Umschalter.
- Threads: Wurzel mit Antworten, Autor (Name, Mensch/Agent-Badge), relative Zeit mit absolutem Tooltip, „bearbeitet“, Platzhalter für gelöschte Kommentare; erledigte Threads eingeklappt mit „Erledigt von X“, Filter „Erledigte anzeigen“.
- Aktionen: Kommentieren, Antworten, Bearbeiten (nur eigene), Löschen (eigene; mit `admin` auch fremde) mit `ag-confirm-dialog`, Erledigen/Wieder öffnen (Wurzel).
- Eingabe: einfacher Markdown-Textbereich (kein WYSIWYG) mit `@`-Auswahl: Tippen von `@` öffnet eine gefilterte Liste der Mitglieder (Menschen und Agenten, Tastatur: Pfeile, Enter, Escape); Auswahl fügt `[@Name](actor:<id>)` ein. Strg/Cmd+Enter sendet. Entwurf bleibt bei Live-Neuladen erhalten.
- Anzeige: Kommentar-Markdown sanitisiert gerendert (wie die vorhandene Markdown-Vorschau; kein rohes HTML), `actor:`-Links als Erwähnungs-Chip (Name aus Members, sonst Anzeigename), Links mit anderem Schema bleiben normale Links.
- Live: `comment.*` mit `recordId` des offenen Records lädt die Threads entprellt neu; ein offener Bearbeiten- oder Antwort-Entwurf wird dabei nie verworfen (Muster Sperre + Generation aus `liveRefresh`).
- Fehler: `not_member`, `not_author`, `not_root`, 409 (archiviert) verständlich am Formular.

**Ausdrücklich nicht drin:**
- Posteingang/Erwähnungs-Badge in der Shell (AP-032), Kommentare in Tabelle/Board, Reaktionen, Body-Erwähnungen, Backend.

## Fachlicher Kontext

- Vertrag aus AP-029 (siehe OpenAPI nach dem Commit): `GET|POST /api/v1/w/{slug}/records/{id}/comments` (`POST` mit `Idempotency-Key` wie beim Record-Anlegen), `PATCH /comments/{id}` (`markdown?`, `resolved?`), `DELETE /comments/{id}`, `POST /comments/{id}/resolve|reopen`. `CommentDto` flach mit `authorId`, `authorName`, `authorKind`, `markdown` (`null` bei gelöschtem Platzhalter), `parentCommentId`, `createdAt`, `editedAt`, `resolvedAt`, `resolvedBy`, `deletedAt`; Liste liefert Threads (Wurzel + Antworten) und `omittedThreads` (höchstens 500 neueste Threads). Fehlercodes: `not_member` (am Feld `markdown`, ID in `message`), `not_author` (403), `not_root`, `comment_deleted` (409, Antwort auf gelöschte Wurzel), 409 bei archiviertem Record/Collection/Space für alle Schreibaktionen.
- Erwähnungs-Chips nur nach denselben Regeln wie der Server-Parser (`src/backend/Agcowork.Domain/Comments/CommentMarkdown.cs`, XML-Kommentar): keine Erwähnungen in Code-Spans, Codeblöcken und HTML-Kommentaren; sonst zeigt die UI Chips ohne Posteingang-Eintrag. Der Antwort-Platzhalter `omittedReplies` (falls vorhanden) wird als „N ältere Antworten nicht angezeigt“ dargestellt.
- Live: Activity `comment.created|updated|deleted|resolved|reopened`, `targetType = comment`, `targetId` = Kommentar-ID, Diff mit `recordId`, `parentCommentId`, `mentionedActorIds`. Gefiltert wird über `diff.recordId` (nicht über `targetId`).

## Akzeptanzkriterien

1. Threads werden in der Reihenfolge des Servers mit allen Zuständen angezeigt; erledigte eingeklappt.
2. `@` öffnet die Auswahl, Tastaturbedienung funktioniert, Auswahl fügt die kanonische Syntax ein; gesendeter Kommentar zeigt den Chip.
3. Bearbeiten/Löschen nur, wo erlaubt; Löschen mit Bestätigung; Erledigen nur an Wurzeln.
4. Ein Agent kommentiert per MCP: Thread erscheint ohne Reload; ein offener Entwurf im Antwortfeld bleibt erhalten (Test ohne Schutz rot).
5. Gerenderter Kommentar mit `<img onerror>` oder `javascript:`-Link führt kein Skript aus (Test).
6. A11y: Seitenleiste als `complementary` mit Namen, Auswahl als `listbox` mit `aria-activedescendant`, Live-Region für neue Kommentare.
7. `npm run build`, `npm test`, `npm run lint`, `npm run format:check` grün; Orchestrator-Rauchtest im Wegwerf-Stack.

## Tests, die mindestens entstehen

- Unit (Vitest): Thread-Darstellung, Mention-Auswahl (Filter, Tastatur, Einfügen an Cursor), Rechte-abhängige Aktionen, Live mit Entwurfsschutz, Sanitizing.

## Entscheidungen, die der Implementierer treffen darf

- Breite und Platz der Seitenleiste, Umschalter-Platz; ob die Auswahl ein Helm-Popover oder eine eigene Liste unter dem Feld ist (Fokus und Escape wie im Inline-Edit beachten).

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-08 | angelegt |
| 2026-11-08 | AP-029-Vertrag ergänzt, an implementer-opus übergeben (parallel zu AP-032) |
| 2026-11-08 | Bericht: 968 Tests grün; eigener Renderer micromark/mdast → DOM ohne `innerHTML` (Links nur http/https/mailto/relativ, Bilder als Platzhalter), `comment-mentions.ts` als Portierung des Server-Parsers, Entwürfe je Ziel im Store statt globaler Sperre (Agenten-Kommentare erscheinen auch beim Schreiben), Zurückhalten bei gelöschtem Ziel, `?comment=` öffnet und hebt hervor; offen: Record-Wechsel verwirft Kommentar-Entwürfe |
| 2026-11-08 | Review gestartet |
| 2026-11-08 | Review: **Blockiert** – B1 eigene Wurzel löschen bei offenem Antwortfeld friert Live dauerhaft ein; wichtig: Entwürfe ohne Navigations-Schutz, Thread verschwindet beim Erledigen durch andere, DoS durch präparierten Markdown (Stack Overflow, Sekunden Blockade), Retry mit geändertem Text sendet alten Kommentar; XSS sauber, Milkdown sanitisiert `javascript:` im Body bereits (7.23) |
| 2026-11-08 | Nacharbeit an implementer-opus (B1, W1–W4, Hinweise 1, 2, 4–7, 9) |
| 2026-11-08 | Nacharbeit: B1 behoben, `hasUnsentDrafts` in Guard und beforeunload, Threads mit Entwurf bleiben sichtbar, readonly-Composer, `isPathological` mit Klartext-Fallback, Key an Text gebunden, Milkdown-Regressionstest; 987 Tests |
| 2026-11-08 | Nachprüfung: Freigabe mit Hinweisen für B1, W1, W2, W4; W3 unter den Schwellen umgehbar (Lazy Continuation ~410 ms/Kommentar, summiert über Threads) |
| 2026-11-08 | zweite Nacharbeit an implementer-opus (Zeilengrenze, Längengrenze ~4000, Seitenbudget, Tests) |
| 2026-11-08 | Nacharbeit: 4 000 Zeichen, 300 Zeilen, 1 000 Formatzeichen; Worst Case unter allen Schwellen ≤ 36 ms; `CommentRenderer` mit 150-ms-Budget je Seite und Cache (200 grenzwertige Kommentare < 400 ms statt 2,9 s); 993 Tests |
| 2026-11-08 | abgeschlossen (Orchestrator hat build/test/lint/format auf dem Commit geprüft) |
