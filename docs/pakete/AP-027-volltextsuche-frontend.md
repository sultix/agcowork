# AP-027 – Volltextsuche Frontend: Suchfeld in der Shell, Ergebnisseite mit Hervorhebung

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | 2 |
| Implementierer | implementer-sonnet |
| Referenzbeispiel | Liste mit Paginierung: `src/frontend/src/app/features/activity/activity-feed.component.ts`; Datenzugriff: `core/api/api-client.ts` mit `httpResource`; Shell und Tastenkürzel: `features/workspace-shell/` (Cmd+B für die Sidebar); UI-Bausteine: `docs/02-ARCHITEKTUR.md` §6 |
| Abhängig von | AP-026 (Endpunkt), AP-025 (committet, berührt ebenfalls die Shell) |
| Betroffene Pfade | neu `src/frontend/src/app/features/search/`, Workspace-Routen, Suchfeld in `features/workspace-shell/` |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §6 „Routen“ (`/w/:workspaceSlug/search?q=`), „UI-Bausteine“ |

## Ziel

Menschen suchen aus jeder Seite eines Workspaces heraus und springen direkt zum Record. Die Ergebnisseite zeigt Collection, Ausschnitt mit hervorgehobenem Treffer und lässt nach Space und Collection eingrenzen.

## Scope

**Drin:**
- Suchfeld in der Shell-Kopfzeile; Tastenkürzel `Cmd/Ctrl+K` fokussiert es; Enter navigiert zu `/w/:slug/search?q=…`.
- Ergebnisseite: liest `q`, `spaceId`, `collectionId`, `includeArchived` aus der URL (teilbar), ruft `GET /search`, zeigt Titel (Link zum Record), Collection-/Space-Name, Snippet, Aktualisierungsdatum, Archiv-Badge. „Mehr laden“ über `nextCursor`.
- Hervorhebung: Snippet an den Server-Markern aus AP-026 in Textstücke zerlegen und Treffer als `<mark>` rendern, **ohne** `innerHTML`/`bypassSecurityTrust*`. Der Rest bleibt reiner Text.
- Filter: Space- und Collection-Auswahl (aus den vorhandenen Navigationsdaten), Checkbox „Archivierte einbeziehen“; Änderungen schreiben die URL.
- Zustände: leer (Hinweis zur Syntax: Wörter, `"Phrase"`, `-Wort`), lädt, keine Treffer, Fehler (400 `empty_query` als Hinweis am Feld).

**Ausdrücklich nicht drin:**
- Backend, Command-Palette mit Aktionen, Live-Vorschläge beim Tippen (eigenes Paket, falls gewünscht), Live-Aktualisierung der Ergebnisse.

## Fachlicher Kontext

- Vertrag aus AP-026 (committet `923b736`, siehe OpenAPI `GET /api/v1/w/{slug}/search`): Antwort `{ items, nextCursor }`, `nextCursor` fehlt auf der letzten Seite (null-Felder werden weggelassen). Treffer: `recordId`, `title`, `collectionId?`, `spaceId`, `snippet`, `rank`, `updatedAt`, `archived`.
- `snippet` ist **reiner Text** und kann `<`, `>` und Markup aus dem Inhalt enthalten; nur `⟦` (U+27E6) und `⟧` (U+27E7) stammen vom Server. Snippets entstehen aus den ersten 20.000 Zeichen; ein Treffer weiter hinten hat ein Snippet ohne Marker, ein reiner Titeltreffer ebenfalls.
- `archived: true` auch dann, wenn Collection oder Space archiviert ist; ohne `includeArchived` erscheinen solche Records nicht.
- Syntax für den Leer-Hinweis: Wörter, `"Phrase"`, `-Wort`, `or`; das letzte Wort wird ab 2 Zeichen als Präfix gesucht.
- Fehler: 400 `validation_failed` mit `errors[{ field: "q", code: "empty_query" | "too_long" | "invalid_value" }]`.

## Akzeptanzkriterien

1. `Cmd/Ctrl+K` fokussiert das Suchfeld; Enter navigiert mit `q` in der URL.
2. Ergebnisseite lädt aus der URL, zeigt Treffer mit Link, Kontext und Snippet; „Mehr laden“ hängt die nächste Seite ohne Duplikate an.
3. Treffer im Snippet als `<mark>`; ein Snippet mit `<img onerror=…>`-Text wird als Text angezeigt (Test).
4. Filter ändern URL und Ergebnis; Zurück-Navigation stellt den vorigen Zustand her.
5. Leere, Lade-, Keine-Treffer- und Fehlerzustände vorhanden; `empty_query` erscheint als Hinweis.
6. `npm run build`, `npm test`, `npm run lint`, `npm run format:check` grün.

## Tests, die mindestens entstehen

- Unit (Vitest): Marker-Zerlegung (Randfälle: Marker am Anfang/Ende, kein Marker, ungepaarte Marker), Ergebnisseite (Laden, Mehr laden, Filter ↔ URL, Zustände), Tastenkürzel.

## Entscheidungen, die der Implementierer treffen darf

- Platz und Breite des Suchfelds in der Kopfzeile; Darstellung der Filter (Selects oder Popover).

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
| 2026-11-08 | Vertrag aus AP-026 im Paket ergänzt, an implementer-sonnet übergeben (parallel zu AP-028) |
| 2026-11-08 | Bericht: 809 Tests grün; Suchfeld in der Kopfzeile (`role="search"`, Strg/Cmd+K, ignoriert `defaultPrevented`), eigenes Feld auf der Ergebnisseite für Fehler am Feld, URL als einziger Zustand, `splitSnippet` robust gegen kaputte Marker, Seitengröße 20; Zurück-Test über `navigateByUrl` statt `Location.back()` |
| 2026-11-08 | Review gestartet |
| 2026-11-08 | Review: Freigabe mit Hinweisen – Select zeigt „Alle“ trotz aktivem Filter, wenn der Baum nach dem Rendern lädt (nicht zurücksetzbar); Leerzeichen um `<mark>`; Hinweise zu Keydown ohne `key`, Strg+K auf macOS, A11y-Live-Regionen, 404-Filter, archivierte Kontexte |
| 2026-11-08 | Entscheidung: Suche aus der Kopfzeile setzt Filter zurück (neue Suche) |
| 2026-11-08 | Nacharbeit an implementer-sonnet |
| 2026-11-08 | Nacharbeit: `[selected]` je Option (Test mit spätem Baum, ohne Fix rot), Klartext ohne Whitespace, Keydown prüft Modifier zuerst und auf Apple nur Cmd, Seitenfeld mit Signal Forms (`maxLength` 200, Serverfehler als Validator), Live-Regionen, „Filter nicht gefunden“, archivierte Kontexte, Test für veraltete Antwort, Platzhalter-Seite entfernt; 820 Tests |
| 2026-11-08 | abgeschlossen (Orchestrator hat build/test/lint/format auf dem Commit geprüft) |
| 2026-11-08 | Rauchtest: Präfix `zauber` findet „Zauberwort“, `<mark>` gesetzt, `<img onerror>` als Text ohne Bild-Element; Filter → URL, echter Browser-Zurück stellt Zustand her; direkter Aufruf mit `spaceId` zeigt den richtigen Filter |
