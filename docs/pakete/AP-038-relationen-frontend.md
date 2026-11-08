# AP-038 – Relationen Frontend: Titel-Cache, Chips als Links, Picker mit Serversuche, Rückverweise im Dokument

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | 2 |
| Implementierer | implementer-opus |
| Referenzbeispiel | Live-Stores mit Generation und Bündelung: `core/workspace/members.store.ts`, `core/realtime/live-reload.ts`; Picker: `shared/properties/relation-editor.component.ts`; Zellen: `shared/table/cell-value.component.ts`; Dokument: `features/records/record-document.component.ts`; Suche/Query-Endpunkt: `features/search/`, `features/views/query-pager.ts` |
| Abhängig von | AP-037 (Lookup, Backlinks), AP-039 (committet, falls er `collection-page`/Routen berührt) |
| Betroffene Pfade | neu `src/frontend/src/app/core/records/record-title.store.ts` (oder `shared/`), `shared/properties/relation-editor.component.*`, `shared/table/cell-value.component.*`, `features/views/board-view.component.ts` (nur Kartenanzeige), `features/records/record-document.component.*` (Rückverweise), `shared/filter/filter-value-editor.component.*`, neue Komponente für Relations-Chips. **Nicht:** Backend, Schema-Editor |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §2 „Relationen“, §6 |

## Ziel

Relationen sind überall lesbar und navigierbar: Tabelle, Board, Dokument und Filter zeigen Titel statt IDs oder Zählern, ein Klick führt zum Ziel, und jedes Dokument zeigt, wer darauf verweist. Der Picker findet jeden Record der Ziel-Collection, nicht nur die ersten 200.

## Scope

**Drin:**
1. **Titel-Cache** (`RecordTitleStore`, root, je Workspace): sammelt angefragte IDs, bündelt sie (Mikrotask/kurzer Debounce) zu `POST /records/lookup` (max. 200 je Anfrage), cached `title`, `archived`, `collectionId`; unbekannte/fremde IDs als „nicht verfügbar“. Live: `record.updated|archived|restored` (Titel/Status) aktualisieren betroffene Einträge, Resync leert den Cache; Workspace-Wechsel leert ihn. Generation gegen veraltete Antworten (Muster AP-030).
2. **Relations-Chips** (gemeinsame Komponente): Titel als Link zum Record (`/w/:slug/records/:id`), archivierte durchgestrichen mit sr-only „archiviert“, nicht verfügbare als „Nicht verfügbar“ ohne Link. Lädt über den Cache.
3. **Tabelle und Board:** Relationszellen zeigen Chips (bis 3, dann „+N“) statt „N Verknüpfungen“; kein Request je Zelle (Cache bündelt). Inline-Edit bleibt.
4. **Picker:** Serversuche in der Ziel-Collection (Query-Endpunkt mit `title contains`, aktive Records, neueste zuerst, Debounce, Limit ~20), Tastatur wie bisher; gewählte Werte über den Cache betitelt; kein 200er-Vollabruf mehr je Instanz.
5. **Filter-Leiste:** Relationswerte und Filter-Chips mit Titeln aus dem Cache.
6. **Rückverweise im Dokument:** Abschnitt „Verweise auf diesen Eintrag“ unter dem Body (oder als Tab), gruppiert nach Collection/Property, Titel als Links, „Mehr laden“ per Cursor, leerer Zustand; live neu laden, wenn `record.*` einer Quell-Collection eintrifft, deren Relation auf diesen Record zeigt (vereinfachend: bei `record.updated|created|archived` der betreffenden Quell-Collections, gebündelt).

**Ausdrücklich nicht drin:**
- Backend, Schema-Editor, Gegen-Properties, Graph-Ansicht.

## Fachlicher Kontext

- Vertrag aus AP-037 (committet): `POST /api/v1/w/{slug}/records/lookup { ids }` (1–200) → `{ items: [{ id, title, collectionId, spaceId, archived }] }` in Anfrage-Reihenfolge, fremde/unbekannte fehlen. `GET /records/{id}/backlinks?limit&cursor&includeArchived` → `{ recordId, groups: [{ collectionId, collectionName, spaceId, propertyKey, propertyName, items: [{ id, title, archived }], nextCursor, more }], nextGroupsCursor }`; `limit` je Gruppe; `nextCursor` einer Gruppe holt nur diese Gruppe; höchstens 50 Gruppen je Aufruf, weitere über `nextGroupsCursor` (als `cursor`); falscher Cursor → 400 `invalid_format` an `cursor`.
- Titel kommen als reiner Text; `archived` umfasst Container.

## Akzeptanzkriterien

1. Tabelle mit 100 Zeilen und Relationen erzeugt höchstens eine Lookup-Anfrage je 200 verschiedene IDs (Test).
2. Chips zeigen Titel, Links funktionieren, archivierte/nicht verfügbare Ziele korrekt markiert.
3. Umbenennen eines Ziel-Records durch einen Agenten aktualisiert alle sichtbaren Chips live.
4. Picker findet einen Record jenseits der ersten 200 der Ziel-Collection per Titelsuche (Test mit gefälschtem Server).
5. Dokument zeigt Rückverweise gruppiert, paginiert, live.
6. Filter zeigt Titel für Relationswerte.
7. XSS: Titel nur als Text.
8. `npm run build`, `npm test`, `npm run lint`, `npm run format:check` grün; Orchestrator-Rauchtest.

## Entscheidungen, die der Implementierer treffen darf

- Ort des Caches, Bündelungsintervall, Darstellung der Rückverweise (Abschnitt oder Tab), Chip-Limit in Zellen.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-08 | angelegt |
| 2026-11-08 | Vertrag aus AP-037 ergänzt, an implementer-opus übergeben; nach Verbindungsabbruch per Nachricht fortgesetzt |
| 2026-11-08 | Bericht: 1176 Tests grün; `RecordTitleStore` (Bündelung 20 ms, Pakete zu 200, Epoche + Ticket, live), `ag-relation-chips`, Zellen mit Chips (bis 3 + „+N“), Picker mit Serversuche über Query `title contains`, Rückverweise als Abschnitt unter dem Body mit Gruppen-/Seiten-Cursor und Live-Neuladen; Abweichung: Container-Archivierung lädt gecachte Titel neu |
| 2026-11-08 | Review gestartet |
| 2026-11-08 | Review: Freigabe mit Hinweisen – wichtig: `failed` im Titel-Cache erholt sich nie, AK1 nur für den sichtbaren Ausschnitt (Virtual Scroll lädt in Schüben); Hinweise: Rennen beim Umbenennen, `pending`-Leck, Backlinks im Skelett hängend, Schema-Änderung lädt Backlinks nicht, Doppelklick auf Chip-Links in Zellen |
| 2026-11-08 | Nacharbeit an implementer-opus (W1, W2, H1–H3, H5, H6/H8) |
| 2026-11-08 | Nacharbeit: `failed` mit Frist und Neuladen bei `live`, `prefetch` aller Relations-IDs geladener Zeilen (100 Zeilen/200 IDs = ein Lookup, auch nach Scrollen), Umbenennen während Lookup, `pending` je Epoche, Backlinks ohne hängendes Skelett und mit Neuladen bei Schemaänderung, Chips in Zellen navigieren nach 250 ms (Doppelklick öffnet Editor), im Edit-Pane ohne Links; 1188 Tests |
| 2026-11-08 | abgeschlossen (Orchestrator hat build/test/lint/format auf dem Commit geprüft) |
| 2026-11-08 | Rauchtest (Wegwerf-Stack): Tabelle zeigt Titel-Chips statt Zählern; Agent benennt Entscheidung per MCP um, alle Chips aktualisieren live; Dokument zeigt Rückverweise gruppiert; Schema-Editor legt Relation „Blockiert durch“ (diese Collection, mehrere) an; Picker findet per Serversuche und speichert per Klick. Fund: Picker ohne Pfeiltasten-Bedienung (war vorher schon so) → Backlog |
