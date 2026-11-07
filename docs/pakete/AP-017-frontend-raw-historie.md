# AP-017 – Frontend: Raw-View (bearbeitbar), Historie mit Diff, Aktivitätsfeed

| Feld | Wert |
|---|---|
| Status | in Arbeit |
| Phase | 1 |
| Implementierer | implementer-sonnet |
| Referenzbeispiel | AP-014: `features/records/record-document.component.ts` + `record-save.service.ts` (Laden, Save mit `expectedRevision`, 409), `features/spaces` (Activity-Ausschnitt auf der Startseite); AP-012: `shared/raw-view` (`language`, `isValid`, `valueChange`); AP-016: Listen mit Cursor, `describeError` |
| Abhängig von | AP-007, AP-012, AP-013, AP-014 |
| Betroffene Pfade | ausschließlich `src/Agcowork.Web/` (neu: `features/records/raw`, `features/records/history`, `features/activity`; geändert: Dokument-View-Tabs, Shell-Navigation „Aktivität“, Routen) |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §3 (`raw`), §6 Routen (`/records/:id/raw`, `/records/:id/history`, `/activity`), §4.4 (Markdown mit Frontmatter); AP-007 (History-/Revision-/Activity-Endpunkte, Diff-Format) |

## Ziel

Nach diesem Paket kann ein Mensch jeden Record im Rohformat sehen und bearbeiten (JSON der
Properties, Markdown-Body, Markdown mit Frontmatter read-only), die Revisionshistorie mit
Diff zwischen Revisionen ansehen und wiederherstellen, und im Workspace-Aktivitätsfeed
nachvollziehen, welcher Agent oder Mensch was wann geändert hat. Damit ist F6
(Nachvollziehbarkeit) für Menschen erfüllt.

## Scope

**Drin:**

*Raw-View (`/w/:slug/records/:id/raw`)*
- Tab in der Dokument-View (Dokument | Rohdaten | Historie). Drei Bereiche: Properties als JSON (Raw-View `json`, editierbar, `isValid` gate), Body als Markdown (Raw-View `markdown`, editierbar), Markdown mit Frontmatter (aus `?format=markdown`, read-only, Kopieren-Button).
- Speichern: Properties-JSON → `PATCH { properties, expectedRevision }` (nur gesendete Keys; entfernte Keys werden als `null` gesendet, damit sie gelöscht werden – Diff gegen den geladenen Stand berechnen), Body → `PATCH { body, expectedRevision }`; 409-Dialog aus AP-014 wiederverwenden. Validierungsfehler (`properties.<key>`) als Liste über dem JSON-Editor mit Zeilenhinweis, wenn möglich.
- Read-only für Viewer/archiviert.

*Historie (`/w/:slug/records/:id/history`)*
- Liste der Revisionen (`GET …/history`, Cursor): Nr, Kind (created/updated/appended/archived/restored), Actor (Name, Kind-Badge), Zeit, Reason.
- Auswahl zweier Revisionen → **Diff**: Properties als Tabelle (Key, alt, neu, markiert), Body als Zeilen-Diff (einfacher LCS-Zeilen-Diff, eigene kleine Implementierung oder `diff` npm-Paket ≤ 20 kB; Entscheidung im Bericht), Titel-Änderung.
- „Diese Revision wiederherstellen“: `PATCH` mit Titel/Properties/Body der Revision und aktueller `expectedRevision` (neue Revision entsteht; nichts wird gelöscht), Bestätigung.

*Aktivitätsfeed (`/w/:slug/activity`)*
- Liste aus `GET /activity` (Cursor „Mehr laden“), Filter-Leiste: Space, Collection, Actor (Mitgliederliste), Typen (Mehrfachauswahl), Zeitraum (seit). Einträge: Zeit, Actor (Badge Mensch/Agent), Typ als lesbarer Text („hat Record ‚X‘ geändert“), Ziel als Link, aufklappbarer Diff (geänderte Keys alt/neu, `bodyDelta`), bei `collection.schema_changed` `affectedRecords`.
- Startseite (`/w/:slug`, AP-014) nutzt dieselbe Eintrag-Komponente für die letzten 10.
- Shell-Navigation „Aktivität“ verlinkt hierher.

*Tests (Vitest)*
- Raw-View-Seite: Properties-Diff → PATCH mit `null` für entfernte Keys, ungültiges JSON blockiert Speichern, Body-Save, 409, read-only.
- Historie: Liste, Diff-Berechnung (Properties und Body-Zeilen, Unit-Tests der Diff-Funktion), Wiederherstellen sendet den richtigen PATCH.
- Activity: Filter erzeugen die richtigen Query-Parameter, Einträge rendern Typ-Texte, Diff aufklappbar, Cursor.
- Routen-Spec erweitert.

**Ausdrücklich nicht drin:**
- Live-Updates (SignalR, Phase 2), Kommentare, Export, Vergleich über Records hinweg, Backend-Änderungen.

## Fachlicher Kontext

- Endpunkte (AP-007): `GET /records/{id}?format=markdown&includeBody=true`, `GET /records/{id}/history?limit&cursor`, `GET /records/{id}/revisions/{no}`, `PATCH /records/{id}`, `GET /activity?spaceId&collectionId&actorId&since&types&limit&cursor` (neueste zuerst). Diff-Format im Event: `{ title:{old,new}, properties:{k:{old,new}}, bodyChanged, bodyDelta:{added,removed} }`.
- Revisionen enthalten Titel, Properties, Body, Actor, Kind, Reason; berechnete Properties nicht.
- Muster aus AP-014/AP-016; `describeError`; Resources idle ohne Slug.

## Ergänzungen aus AP-014 (Bericht)

- Im Dev-Server wirft Monaco in der Raw-View Konsolenfehler („depends on UNKNOWN service ICodeLensCache“) aus `shared/raw-view/monaco-adapter` (kuratierte Imports). Ursache beheben (fehlende Contribution/Service-Registrierung ergänzen oder Feature abschalten), damit die Konsole sauber ist; Prüfung im Dev- und Prod-Build.
- `ApiClient` kennt keine Request-Header; für `Idempotency-Key` beim Anlegen (optional hier) müsste er erweitert werden – nur, wenn es in dieses Paket passt, sonst Backlog.
- Muster aus AP-014: `RecordSaveService` (Save/Konflikt), `ag-property-editor`, `record-pager.ts`, Fixtures in `core/testing/test-utils.ts`.

## Akzeptanzkriterien

1. Rohdaten-Tab zeigt Properties-JSON, Body-Markdown und Frontmatter-Markdown; Bearbeiten und Speichern beider editierbaren Bereiche funktioniert mit `expectedRevision`; Entfernen eines Keys im JSON löscht ihn serverseitig.
2. Historie listet Revisionen; Diff zweier Revisionen zeigt Property- und Body-Änderungen; Wiederherstellen erzeugt eine neue Revision mit dem alten Stand.
3. Aktivitätsfeed zeigt Ereignisse mit Actor, Typ, Ziel-Link und aufklappbarem Diff; Filter nach Collection und Actor wirken; „Mehr laden“ funktioniert.
4. Viewer: alles read-only, kein Wiederherstellen.
5. `npm test`, `npm run lint`, `npm run build` grün; AK 1–3 im Browser gegen die laufende API geprüft.

## Tests, die mindestens entstehen

- `features/records/raw/record-raw.component.spec.ts`, `features/records/history/record-history.component.spec.ts`, `features/records/history/diff.spec.ts`, `features/activity/activity-feed.component.spec.ts`, `features/activity/activity-entry.component.spec.ts`, `app.routes.spec.ts` erweitert

## Entscheidungen, die der Implementierer treffen darf

- Diff-Bibliothek vs. eigene Implementierung; Darstellung des Body-Diffs (nebeneinander vs. untereinander).

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
| 2026-11-07 | an implementer-sonnet übergeben |
