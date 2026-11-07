# AP-015 – Frontend: Tabellen-View mit Inline-Edit, Filter-Leiste, View-Wechsel, Board (einfach)

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | 1 |
| Implementierer | implementer-opus |
| Referenzbeispiel | AP-014: `shared/properties/*` (Property-Editoren mit `value`/`valueChange`/`definition`/`readonly`), `features/records/record-save.service.ts` (PATCH mit `expectedRevision`, 409-Dialog), `features/collections/collection-page.component.ts` (Record-Liste, Cursor), `WorkspaceTreeStore`; AP-016/AP-013 Muster |
| Abhängig von | AP-009, AP-014 |
| Betroffene Pfade | ausschließlich `src/Agcowork.Web/` (neu: `features/views/`, `shared/table/`, `shared/filter/`; geändert: `features/collections/collection-page` (View-Wechsel), Routen) |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §3 (Views, Filter-DSL inkl. Typ-Matrix aus AP-009), §6 Bausteine (Table, Board, Filter-Builder), Routen `/w/:slug/collections/:collectionId/views/:viewId` |

## Ziel

Nach diesem Paket sieht ein Mensch eine Collection als echte Tabelle: virtuelles Scrolling,
Spalten aus dem Schema, Inline-Bearbeitung mit den Property-Editoren, Sortierung und ein
Filter-Builder, der dieselbe Filter-DSL erzeugt, die Agenten nutzen. Views (aus AP-008)
sind wechselbar und speicherbar. Ein einfaches Board (Gruppierung nach select/status mit
Drag&Drop) kommt als zweiter View-Typ dazu.

## Scope

**Drin:**

*View-Wechsel (`features/views`)*
- Route `/w/:slug/collections/:collectionId/views/:viewId`; `/collections/:collectionId` leitet auf den Default-View um. Tabs/Dropdown mit allen Views der Collection (`GET …/views`), Typ-Icon; „Neue View“ (Name, Typ table|board|list|gallery; gallery/list rendern vorerst als Tabelle mit Hinweis), Umbenennen, Löschen (System-Views nicht), Default setzen. Änderungen an Spalten/Sortierung/Filter werden **explizit** gespeichert („View speichern“, nur `write`), sonst nur lokal für die Sitzung.
- Daten über `POST …/views/{id}/query` mit Cursor; Zusatzfilter aus der Filter-Leiste per `and`.

*Tabelle (`shared/table`)*
- `ag-data-table`: CDK Virtual Scroll (Zeilenhöhe fest), Spalten = `visibleProperties` des Views (leer = alle, in Schema-Reihenfolge) plus Titel-Spalte (fix links), Spaltenbreite per Drag, Reihenfolge per Drag (CDK DragDrop), Sortierung per Klick auf Kopf (toggelt asc/desc, Shift für Mehrfach), Zeilenklick öffnet die Dokument-View (Link), Checkbox-Auswahl für Mehrfach-Archivieren.
- **Inline-Edit**: Doppelklick/Enter auf Zelle öffnet den Property-Editor des Typs (aus AP-014) im Zellen-Overlay; Escape bricht ab; Blur/Enter speichert per `PATCH` mit `expectedRevision` (über `record-save.service`); 409 → Zelle markiert, Dialog aus AP-014; Validierungsfehler als Tooltip an der Zelle. Read-only für Viewer, archivierte Records, berechnete Felder.
- Leerer Zustand, Lade- und Fehlerzustand, „Mehr laden“ am Ende (Cursor) bzw. automatisch beim Scrollen.

*Filter-Leiste (`shared/filter`)*
- Builder, der genau die DSL aus §3 erzeugt: Bedingungen `prop`/`op`/`value` mit typabhängiger Operatorliste (Typ-Matrix aus AP-009, aus dem Schema abgeleitet) und typgerechtem Wert-Editor (Select-Options, Datum, Zahl, Actor, Relation), Gruppen `and`/`or` (eine Ebene Verschachtelung reicht), Entfernen, „Filter leeren“. Anzeige des erzeugten JSON (zum Kopieren für Agenten) in einem aufklappbaren Bereich.
- Sortierung als Chips (aus Spaltenklicks), entfernbar.

*Board (einfach)*
- View-Typ `board`: Spalten = Options des `groupByProperty` (select/status; Status-Gruppen als Trenner), Karten mit Titel + bis zu drei Properties, Drag&Drop zwischen Spalten → `PATCH` mit neuem Wert (optimistisch, bei Fehler zurück). Daten: eine Query-Seite (Limit 500) client-seitig gruppiert; Hinweis, wenn mehr existieren.

*Tests (Vitest)*
- Tabelle: Spalten aus View/Schema, Sortierung erzeugt `sorts`, Inline-Edit sendet PATCH mit `expectedRevision`, 409-Pfad, read-only-Regeln, Cursor-Nachladen.
- Filter-Builder: je Typ Operatorliste korrekt (Tabelle), erzeugtes JSON entspricht der DSL, Round-Trip (DSL → Builder → DSL identisch).
- View-Wechsel: Default-Redirect, speichern nur bei `write`, System-View nicht löschbar.
- Board: Gruppierung, Drag&Drop sendet PATCH, Rückrollen bei Fehler.
- Routen-Spec erweitert.

**Ausdrücklich nicht drin:**
- Gallery/List als eigene Darstellung, Timeline/Calendar, serverseitige Gruppierung, Aggregationen, Spaltensummen, Export, SignalR-Live-Updates.
- Backend-Änderungen.

## Fachlicher Kontext

- Filter-DSL: `{ and: [ { prop, op, value }, { or: [...] } ] }`, Operatoren und Typ-Matrix stehen nach AP-009 in Architektur §3; `POST /views/{id}/query` Body `{ filter?, sorts?, limit?, cursor?, fields? }`.
- View-Objekt: `{ type, filter, sorts, groupByProperty, visibleProperties, isDefault, isSystem, needsAttention }`; `PATCH /views/{id}`.
- Berechnete Properties sind read-only und werden auf Spalten gemappt (AP-009).
- Performance: Tabelle muss mit 5 000 geladenen Zeilen flüssig scrollen (Virtual Scroll, OnPush, Signals; keine Pipes mit Objekterzeugung pro Zelle).
- Muster aus AP-014 wiederverwenden; keine zweite Editor-Implementierung.

## Akzeptanzkriterien

1. Collection öffnet im Default-Table-View; Spalten entsprechen dem Schema; 5 000 Zeilen scrollen flüssig (manuell geprüft, Angabe im Bericht).
2. Inline-Edit eines select-, text- und number-Werts speichert per PATCH mit `expectedRevision`; Konflikt zeigt den Dialog.
3. Filter-Builder: Filter „status in [offen, in_arbeit] und (due < Datum oder priority = hoch)“ lässt sich zusammenklicken, das JSON entspricht der DSL, die Tabelle zeigt das Ergebnis; Sortierung per Spaltenkopf.
4. View speichern persistiert Spalten, Sortierung, Filter; neue View anlegen, wechseln, Default setzen, löschen (System-View gesperrt).
5. Board: Karten gruppiert nach Status, Drag&Drop ändert den Status per PATCH.
6. `npm test`, `npm run lint`, `npm run build` grün; AK 1–5 im Browser gegen die laufende API geprüft (Workspace aus Standard-Vorlage mit Beispiel-Records per API).

## Tests, die mindestens entstehen

- `shared/table/data-table.component.spec.ts`, `shared/table/inline-edit.spec.ts`
- `shared/filter/filter-builder.component.spec.ts`, `shared/filter/filter-dsl.spec.ts`
- `features/views/view-switcher.component.spec.ts`, `features/views/board-view.component.spec.ts`
- `app.routes.spec.ts` erweitert

## Entscheidungen, die der Implementierer treffen darf

- Eigene Tabelle auf CDK (bevorzugt) vs. AG Grid Community (nur, wenn CDK-Variante absehbar mehr als das Paket kostet; begründen).
- Zeilenhöhe, Zellen-Overlay-Technik, Persistenz der Sitzungs-Einstellungen (`sessionStorage` erlaubt, mit try/catch).

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
| 2026-11-07 | an implementer-opus übergeben |
| 2026-11-07 | Unterbrechung durch Nutzungslimit, danach fortgesetzt |
| 2026-11-07 | Bericht: 582 Tests grün; AK 1–5 per Playwright (5 000 Records, 44 Zeilen im DOM, Frame-Zeit Median 16,6 ms). `CollectionStore`, `ag-data-table` (CDK Virtual Scroll, Resize/Reorder, Shift-Mehrfachsortierung, Auswahl, Inline-Edit im Overlay), `InlineEditService` über `RecordSaveService.attach()`, Filter-Builder mit DSL-Round-Trip, Board mit Drag&Drop und Rollback. Abweichungen: Arbeitsstand läuft über `POST /collections/{id}/query` (QueryViewBody nimmt keine sorts/fields), View-Query nur bei unverändertem View; Spaltenbreiten nur in Sitzung. Offen: `shared/table` hängt von `features/records` ab; Flackern bei neuer Abfrage; kein Bulk-Archivieren |
| 2026-11-07 | an reviewer übergeben |
| 2026-11-07 | Review: Freigabe mit Hinweisen; Inline-Edit (genau ein Save, `bodyTracked` korrekt), DSL-Matrix identisch mit Backend, Views, Performance, Cleanup ohne Befund. Wichtig: `shared/table` importiert `features/records` (Zyklus); natives Select speichert bei Tastaturnavigation sofort (plattformabhängig); `query-pager` ohne Spec. Hinweise: ungültiger Edit A blockiert Edit B, Entwurf geht bei Validierungsfehler verloren, `{}`-Knoten lassen View-Seite abstürzen, Retry pro Scrollschritt, fehlende Tests, `setBody` ohne Guard im attach-Modus |
| 2026-11-07 | Dokumentierte Abweichungen (akzeptiert): Arbeitsstand läuft über `POST /collections/{id}/query` statt View-Query + `and`, weil `QueryViewBody` keine `sorts`/`fields` annimmt (Backlog-Kleinpaket); nackte Bedingung wird nach Bearbeiten zu `{and:[…]}` (semantisch gleich). Nacharbeit an implementer-opus (W1–W3, H1–H3, H5, H6, H8) |
| 2026-11-07 | Nacharbeit fertig: 600 Tests grün. `InlineEditService` in features/views mit `CellEditor`-Interface, Lint-Regel gegen `shared → features`, Maus-/Tastatur-Unterscheidung bei Selects, `query-pager.spec`, Edits je Record strikt sequenziell mit Serverstand, Entwurf bleibt bei Fehler, robustes `cleanFilter`, kein Auto-Retry nach Fehler, `setBody`-Guard. Vom Orchestrator verifiziert. Abgeschlossen, Commit `dafa127` |
