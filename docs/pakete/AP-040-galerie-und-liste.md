# AP-040 – Galerie- und Listen-View im Frontend

| Feld | Wert |
|---|---|
| Status | angelegt |
| Phase | 2 |
| Implementierer | implementer-sonnet |
| Referenzbeispiel | Board als Karten-View: `src/frontend/src/app/features/views/board-view.component.ts` (Karten, `ag-cell-value`, `liveRefresh` mit `holdSchema`, `RecordTitleStore.prefetch`, Paginierung über `query-pager.ts`); Seite und Werkzeugleiste: `features/views/view-page.component.ts` (Spalten-Panel, `typeHint`), `view-switcher.component.ts` (Anlage-Formular), `view-state.ts`; Zellen: `shared/table/cell-value.component.ts`, Chips `shared/properties/relation-chips.component.ts` |
| Abhängig von | AP-038, AP-039 (committet) |
| Betroffene Pfade | neu `src/frontend/src/app/features/views/gallery-view.component.*`, `list-view.component.*`; geändert `view-page.component.*` (Auswahl der Darstellung, Felder-Panel und Cover-Auswahl für Galerie/Liste, `typeHint` entfernen), `view-switcher.component.*` (Hinweis „wird vorerst als Tabelle dargestellt“ entfernen, Cover beim Anlegen einer Galerie optional), `view-state.ts` falls nötig. **Nicht:** Backend, Tabelle, Board, Shell |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §3 „Views“, §6 „Bausteine – Gallery/List“, AP-012-Entscheidung „keine fremden Bilder“ |

## Ziel

Views vom Typ `gallery` und `list` werden als Karten bzw. kompakte Liste dargestellt statt als Tabelle. Menschen wählen, welche Felder erscheinen und welches Feld die Karte prägt; Filter, Sortierung und Live-Aktualisierung funktionieren wie bei Tabelle und Board.

## Scope

**Drin:**
1. **Galerie:** Raster aus Karten (responsiv, 1–4 Spalten). Karte: Cover oben, darunter Titel (Link zum Record), darunter die `visibleProperties` kompakt (Label + `ag-cell-value`, Relationen als Chips mit `navigation="link"`, max. ~5 Felder, Rest ausgelassen). Klick auf die Karte öffnet den Record; Tastatur: Karten sind fokussierbar, Enter öffnet, Pfeiltasten wechseln (Raster-Navigation), Fokus sichtbar.
2. **Cover (`cardCoverProperty`):** keine Bilder.
   - select/status/multi_select: Farbband in der Optionsfarbe (erste Option bei multi) mit Label;
   - checkbox: Band „erledigt“/„offen“;
   - text/url/email/number/date: Wert groß als Text (url nur Hostname, nicht klickbar im Cover);
   - kein Cover gesetzt oder Wert leer: neutrales Band mit Anfangsbuchstaben des Titels.
3. **Liste:** eine Zeile je Record: Titel (Link) links, `visibleProperties` rechts als kompakte Werte (auf schmalen Bildschirmen umbrechen). Tastatur: Pfeil hoch/runter wechselt, Enter öffnet, Home/End.
4. **Gemeinsam:** Daten über `query-pager` (Filter, Sortierung, „Mehr laden“ bzw. Nachladen am Ende), Live-Aktualisierung über `liveRefresh` (wie Board, ohne Inline-Edit), `RecordTitleStore.prefetch` für Relationen der geladenen Records, leere und Fehlerzustände, Skeletons.
5. **Konfiguration in der Seite:** Das vorhandene Spalten-/Felder-Panel steuert bei Galerie und Liste die `visibleProperties` (Reihenfolge wie in der Auswahl); bei Galerie zusätzlich Auswahl der Cover-Property (nur die Typen aus 2, „Kein Cover“). Speichern wie bisher über `PATCH /views/{id}` (bestehender Arbeitsstand-/Speichern-Mechanismus der Seite).
6. **Anlage:** Im View-Switcher beim Typ Galerie optional die Cover-Property wählen; Hinweis „wird vorerst als Tabelle dargestellt“ entfernen. `typeHint` in der Seite entfällt für Galerie/Liste.

**Ausdrücklich nicht drin:**
- Bilder/Anhänge als Cover, Inline-Edit in Karten, Drag & Drop, Gruppierung in Galerie/Liste, Backend.

## Akzeptanzkriterien

1. Galerie-View zeigt Karten mit Cover, Titel und gewählten Feldern; Liste zeigt kompakte Zeilen.
2. Cover je Typ wie in Scope 2; nie ein `<img>` mit fremder Quelle (Test).
3. Felder-Panel und Cover-Auswahl ändern die Darstellung und werden gespeichert.
4. Filter, Sortierung, Nachladen funktionieren; ein Agent legt einen Record an → erscheint live (Test mit FakeHub).
5. Tastatur: Galerie-Raster und Liste per Pfeiltasten navigierbar, Enter öffnet (Test).
6. A11y: Liste/Raster mit passenden Rollen (`list`/`listitem` oder `grid`), Karten mit zugänglichem Namen (Titel), Cover-Farbe nicht einziger Informationsträger (Label sichtbar).
7. `npm run build`, `npm test`, `npm run lint`, `npm run format:check` grün; Orchestrator-Rauchtest.

## Entscheidungen, die der Implementierer treffen darf

- Rollenmodell für Raster/Liste (solange Tastatur und Screenreader funktionieren), Kartengröße, Anzahl Felder je Karte.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-09 | angelegt |
