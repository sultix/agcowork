# AP-014 – Frontend: Space-Navigation, Collections, Record-Liste, Dokument-View verdrahtet

| Feld | Wert |
|---|---|
| Status | im Review |
| Phase | 1 |
| Implementierer | implementer-sonnet |
| Referenzbeispiel | AP-016: `features/workspaces/workspaces.store.ts` (resource abhängig von Slug/Session), `features/workspace-settings/members.component.ts` (Liste + Formulare + Fehlercodes), `shared/ui/*` (Badge, ConfirmDialog, FieldErrors, `describeError`), `core/testing/test-utils.ts`; AP-012: `shared/editor/markdown-editor` (`value`, `valueChange`, `editorBlur`, `getMarkdown()`), `shared/raw-view`; AP-013: `api.workspace(slug)`, `toServerErrors` |
| Abhängig von | AP-007, AP-012, AP-013, AP-016 |
| Betroffene Pfade | ausschließlich `src/Agcowork.Web/` (neu: `features/spaces/`, `features/collections/`, `features/records/`; geändert: `workspace-shell` Navigation/Routen, `app.routes.spec.ts`) |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §3 (`document`-View), §4.3 (Spaces, Collections, Records, Templates), §6 Routen und Bausteine (Document, Property-Editoren); `01-PLANUNG.md` E2, F4 |

## Ziel

Nach diesem Paket kann ein Mensch im Browser durch Spaces und Collections navigieren,
Records einer Collection als einfache Liste sehen, einen Record als Dokument öffnen
(Titel, Properties, Markdown-Body im Editor), ihn bearbeiten und speichern, neue Records
aus Vorlagen anlegen und freie Seiten in einem Space verwalten. Damit ist der erste
Ende-zu-Ende-Pfad Mensch ↔ Agent über dieselben Daten offen.

## Scope

**Drin:**

*Spaces und Collections (`features/spaces`, `features/collections`)*
- `/w/:slug` (Workspace-Start): Spaces als Karten (Name, Beschreibung, Icon), zuletzt geänderte Records aus `GET /activity?limit=10` (nur Anzeige, Links auf Records).
- `/w/:slug/spaces/:spaceId`: Space-Kopf (Name, Beschreibung, bearbeiten bei `write`), Liste der Collections (Name, Beschreibung, `recordCount` aus `describe` oder Liste), Liste der freien Seiten (`GET /records?spaceId=`), Buttons „Neue Collection“ (nur Name + Beschreibung; Schema-Editor kommt später) und „Neue Seite“.
- Seitenleiste der Shell: Spaces mit ihren Collections als Baum (aus `GET /spaces` + `GET /collections`), aktiver Eintrag markiert; Daten in einem `WorkspaceTreeStore`, der bei Slug-Wechsel neu lädt.
- `/w/:slug/collections/:collectionId`: Collection-Kopf (Name, Beschreibung, Schema-Zusammenfassung als Chips: Key/Typ), **einfache Record-Liste** (Titel, `updatedAt`, `updatedBy`, Status-Property falls vorhanden; `GET /records?collectionId=`, Cursor „Mehr laden“), Button „Neuer Record“ (Vorlagenwahl aus `GET …/templates`, wenn >1). Die echte Table-View kommt in AP-015; diese Liste ist bewusst schlicht.

*Dokument-View (`features/records`)*
- `/w/:slug/records/:recordId`: 
  - Kopf: Titel (inline editierbar), Breadcrumb Space › Collection, Revision, `updatedAt`/`updatedBy`, Status-Badge „archiviert“.
  - Properties-Panel: pro Property ein Editor nach Typ (**Property-Editoren** in `shared/properties/`: text, markdown (kleiner Editor), number, checkbox, select, multi_select, status, date, datetime, url, email, actor (Select aus Mitgliedern), relation (Suche per Titel über `GET /records?collectionId=` + Eingabe, Mehrfachauswahl bei `multiple`), json (Raw-View klein); berechnete Felder read-only). Editoren sind eigenständige Standalone-Komponenten mit `value`/`valueChange`/`definition`/`readonly`, damit AP-015 sie in der Tabelle wiederverwendet.
  - Body: `ag-markdown-editor` mit dem Record-Body; `readonly`, wenn kein `write`-Scope oder archiviert.
  - **Speichern**: explizit per Button und Cmd/Ctrl+S, plus Auto-Save 2 s nach letzter Änderung (debounced). Jeder Save sendet `PATCH` mit `expectedRevision`; bei 409 `revision_mismatch` Dialog „Von jemand anderem geändert“ mit Optionen „Neu laden“ (verwirft eigene Änderungen) und „Überschreiben“ (PATCH ohne `expectedRevision`), `changedKeys` anzeigen. Vor Navigation weg: ausstehende Änderungen über `getMarkdown()` abholen und speichern (Guard `canDeactivate`), damit nichts im Debounce-Fenster verloren geht (AP-012-Hinweis).
  - Aktionen: Archivieren/Wiederherstellen mit Bestätigung; „Als Markdown anzeigen“ öffnet `?format=markdown` in der Raw-View (read-only) – die vollwertige Raw-View/Historie kommt in AP-017, hier nur die Anzeige.
  - Validierungsfehler aus `errors[]` (`properties.<key>`, `title`) am jeweiligen Editor.
- Neuer Record: Dialog mit Titel + Vorlage → `POST /records` → Navigation zur Dokument-View.

*Navigation*
- Shell-Seitenleiste zeigt den Baum; Kopfzeile Breadcrumbs. Bestehende Platzhalter (`spaces/:spaceId` etc.) werden ersetzt.

*Tests (Vitest)*
- `WorkspaceTreeStore`: lädt Spaces + Collections, Slug-Wechsel lädt neu.
- Space- und Collection-Seiten: rendern, „Neu“-Flows mit Requests, Fehlercodes.
- Property-Editoren: je Typ ein Spec mit `value`/`valueChange` und Validierungsanzeige (Theory-artig über eine Tabelle).
- Dokument-View: Laden, Bearbeiten, Save mit `expectedRevision`, 409-Dialog beide Pfade, Auto-Save-Debounce, `canDeactivate` speichert, readonly bei Viewer/archiviert, Archivieren.
- Routen-Spec erweitert.

**Ausdrücklich nicht drin:**
- Table-View, Board, Filter-Leiste (AP-015), Raw-View mit Bearbeitung und Historie (AP-017), Schema-Editor für Collections (Phase 2), Kommentare, SignalR, Bilder-Upload.
- Backend-Änderungen. Fehlendes melden.

## Fachlicher Kontext

- Record-Endpunkte und Patch-Semantik: `PATCH /records/{id}` mit `{ title?, properties? (nur gesendete Keys; null löscht), body?, expectedRevision?, reason? }`; Antwort ohne Body (`body: null`), `GET ?includeBody=true` zum Laden. 409 `revision_mismatch` trägt `currentRevision` und `changedKeys`.
- `title` kann auch in `properties.title` stehen; die UI sendet ihn als `title`.
- Schema-Informationen: `GET /collections/{id}/schema` oder `describe` (Property-Definitionen mit Typ, Options, Required, Description).
- Mitglieder für actor-Editoren: `GET /members`.
- Editor-Verhalten aus AP-012: `valueChange` debounced; `getMarkdown()` für sofortiges Abholen; Werte von außen gewinnen gegen ungesendete Änderungen (daher nach erfolgreichem Save nicht blind `value` neu setzen, sondern nur Revision/Metadaten übernehmen).
- Entscheidung Orchestrator (AP-012-Backlog): Bilder im Markdown werden vorerst **nicht** geladen (Platzhalter mit URL-Text), bis ein Proxy existiert. Falls der Editor das nicht ohne Eingriff kann, im Bericht nennen; dann bleibt es wie in AP-012.
- Konventionen aus `CLAUDE.md`; Muster aus AP-016 (Resources idle ohne Slug, `describeError`, Formularmeldungen).

## Akzeptanzkriterien

1. Nach Login: `/w/{slug}` zeigt Spaces; Klick führt zur Space-Seite mit Collections und freien Seiten; Seitenleiste zeigt den Baum.
2. Collection-Seite listet Records mit „Mehr laden“; „Neuer Record“ mit Vorlagenwahl erzeugt einen Record und öffnet ihn.
3. Dokument-View: Titel, alle Property-Typen der Standard-Vorlage editierbar, Body im Editor; Save schickt `PATCH` mit `expectedRevision`; Auto-Save nach 2 s; Cmd/Ctrl+S speichert sofort.
4. Konflikt: Wird der Record zwischenzeitlich per API geändert (Test oder manuell per curl), zeigt die UI den Dialog mit `changedKeys`; „Neu laden“ und „Überschreiben“ funktionieren.
5. Wegnavigieren mit ungespeicherter Änderung speichert vorher (kein Verlust).
6. Viewer sieht alles read-only; archivierter Record read-only mit Wiederherstellen-Button für `write`.
7. `npm test`, `npm run lint`, `npm run build` grün; AK 1–5 im Browser gegen die laufende API geprüft (Playwright-Skript im Scratchpad).

## Tests, die mindestens entstehen

- `features/spaces/workspace-tree.store.spec.ts`, `space-page.component.spec.ts`
- `features/collections/collection-page.component.spec.ts`
- `features/records/record-document.component.spec.ts`, `record-save.service.spec.ts` (Save/Autosave/Konflikt), `record-create-dialog.component.spec.ts`
- `shared/properties/property-editor.spec.ts` (alle Typen)
- `app.routes.spec.ts` erweitert

## Entscheidungen, die der Implementierer treffen darf

- Aufteilung der Komponenten; ob Auto-Save per Service oder im Component-State; Darstellung des Konfliktdialogs.
- Relation-Suche: einfache Titel-Filterung client-seitig über die erste Seite (bis AP-009 die Query liefert).

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
| 2026-11-07 | an implementer-sonnet übergeben |
| 2026-11-07 | Bericht: 366 Tests grün, Lint/Build grün; AK 1–6 per Playwright (Workspace mit 10 Property-Typen, 60 Records). `ag-property-editor` als Dispatcher (eigene Komponenten nur Relation/Actor), `RecordSaveService` (Auto-Save, Konflikt), `record-leave.guard`, `MembersStore`, `WorkspaceTreeStore`, Fixtures in test-utils. Abweichungen: Baseline für `getMarkdown()` erst beim ersten Fokus; Editor wird bei Reload neu erzeugt; keine Shell-Breadcrumbs (Seiten-Breadcrumbs); `recordCount` nur auf Collection-Seite. Offen: Monaco-Konsolenfehler im Dev-Server (AP-012), Strg+S bei Markdown-Property im Debounce, `Idempotency-Key` nicht gesendet (ApiClient ohne Header) |
| 2026-11-07 | an reviewer übergeben |
