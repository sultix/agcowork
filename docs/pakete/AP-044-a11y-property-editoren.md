# AP-044 – Barrierefreiheit: Property-Editoren mit Label, Relations-Picker als Combobox

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | 2 |
| Implementierer | implementer-sonnet |
| Referenzbeispiel | Combobox/Listbox mit Pfeiltasten und `aria-activedescendant`: `src/frontend/src/app/features/comments/mention-picker.ts` (und seine Verwendung im Kommentar-Editor); Editoren `shared/properties/property-editor.component.ts`, `actor-editor.component.ts`, `relation-editor.component.ts` |
| Abhängig von | AP-041 (committet) |
| Betroffene Pfade | `src/frontend/src/app/shared/properties/` (Editoren und Specs), Aufrufer nur, soweit sie Label-IDs übergeben müssen: `features/records/record-document.component.*`, `features/collections/templates/template-dialog.component.*`, `shared/filter/filter-value-editor.component.*`, Tabellen-Inline-Edit. **Nicht:** Backend, `features/records/record-create-dialog.*`, `core/api/`, `features/comments/` (außer Lesen) |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §6 (UI-Bausteine, A11y) |

## Ziel

Screenreader nennen bei jedem Property-Editor den Namen der Property („Status, Auswahl, Offen“) statt nur den Wert („–“). Den Relations-Picker bedient man komplett per Tastatur wie die `@`-Auswahl in Kommentaren.

## Scope

**Drin:**
1. **Label-Bindung:** Jeder Editor (text, number, date, url, email, checkbox, select/status, multi_select, actor, relation, markdown/textarea falls vorhanden) bekommt seinen zugänglichen Namen vom sichtbaren Label: entweder `id`/`for`, wenn der Editor ein natives Control ist, oder ein Input `labelledBy` (ID des Labels), das auf `aria-labelledby` des Controls geht. Wo kein sichtbares Label existiert (Tabellenzelle im Inline-Edit), `aria-label` mit dem Property-Namen. IDs eindeutig je Instanz (mehrere Dialoge/Zeilen gleichzeitig).
2. **Prüfen an allen Aufrufern:** Dokument (Property-Liste), Vorlagen-Dialog, Filter-Wert-Editor, Inline-Edit der Tabelle, Board-Karte falls editierbar. Hilfetext/Beschreibung der Property per `aria-describedby`, wo sie sichtbar ist.
3. **Relations-Picker als Combobox:** Suchfeld `role="combobox"`, `aria-expanded`, `aria-controls`, `aria-autocomplete="list"`; Trefferliste `role="listbox"`, Treffer `role="option"` mit `aria-selected`; Pfeil hoch/runter bewegt die aktive Option (`aria-activedescendant`), Enter wählt, Escape schließt die Liste (ohne den umgebenden Dialog zu schließen), Home/End optional. Maus wie bisher. Leer-/Lade-/Fehlerzustand als Live-Region (`role="status"`). Gewählte Chips: Entfernen-Button mit Namen „<Titel> entfernen“.
4. Verhalten des Pickers (Serversuche, Debounce, `chipLinks`) bleibt unverändert.

**Ausdrücklich nicht drin:**
- Spartan-Brain-Upstream-Themen (`aria-invalid`, Popover-Rolle), Anlage-Dialog, Backend.

## Fachlicher Kontext

- Rauchtest 2026-11-09: Im Vorlagen-Dialog melden die Auswahl-Editoren als Namen nur „–“.
- Rauchtest 2026-11-08: Treffer im Relations-Picker sind Buttons ohne Pfeiltasten; nur per Tab erreichbar.
- Escape im Picker darf den Vorlagen-Dialog (`disableClose` + Verwerfen-Rückfrage) nicht auslösen: `stopPropagation`, wenn die Liste offen ist.

## Akzeptanzkriterien

1. Jeder Editor-Typ hat im Test einen zugänglichen Namen gleich dem Property-Namen (Testing-Library `getByRole(..., { name })` o. Ä.), im Dokument und im Vorlagen-Dialog.
2. Zwei Instanzen derselben Property (z. B. Dokument + offener Dialog) haben verschiedene IDs.
3. Picker: Tippen → Liste; Pfeil runter/hoch → `aria-activedescendant` wandert; Enter wählt; Escape schließt nur die Liste (Test im Vorlagen-Dialog: Dialog bleibt offen).
4. Bestehende Tests grün; `npm run build`, `npm test`, `npm run lint`, `npm run format:check` grün; Orchestrator-Rauchtest.

## Tests, die mindestens entstehen

- Unit/Komponente: Namen je Editor-Typ, eindeutige IDs, Picker-Tastatur, Escape-Isolation.

## Entscheidungen, die der Implementierer treffen darf

- `labelledBy`-Input vs. Content-Projection des Labels, gemeinsame ID-Erzeugung, ob der Picker eine gemeinsame Listbox-Hilfe mit `mention-picker` teilt (nur wenn ohne Änderung an `features/comments/` möglich).

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-09 | angelegt |
| 2026-11-09 | an implementer-sonnet übergeben (parallel zu AP-042, disjunkte Dateien) |
| 2026-11-09 | Bericht: 1335 Tests grün (ohne `pretest`, da Backend-OpenAPI `skipTemplate` vorübergehend als Pflichtfeld führte); `labelledBy`/`describedBy` an allen Editoren, Relations-Picker als Combobox/Listbox mit Pfeiltasten, Escape schließt nur die Liste, Live-Region; Abweichung: `hlm-input.ts` gibt `aria-describedby` der Host-Directive frei; offen: Standardwert-Editor im Schema-Dialog ohne Label-ID |
| 2026-11-09 | Review gestartet |
| 2026-11-09 | Review: Freigabe mit Hinweisen – wichtig: lokaler Patch an `hlm-input.ts` undokumentiert (Orchestrator trägt ihn in `02-ARCHITEKTUR.md` §6 ein; Patch behebt nebenbei verlorene `aria-describedby` in Login und Suche); Hinweise: IME-Schutz, Pfeiltaste öffnet Liste nicht wieder, Liste bleibt bei Blur offen, doppelte Namensansage im Vorlagen-Dialog, Test-Helfer ohne `aria-hidden`, Live-Region, Markdown/JSON nur Gruppenname |
| 2026-11-09 | Nacharbeit an implementer-sonnet (IME, Pfeiltaste/Blur, doppelte Ansage, Helfer) |
| 2026-11-09 | Nacharbeit: IME-Schutz, Pfeiltasten öffnen die Liste wieder, Blur schließt sie, keine doppelte Namensansage im Vorlagen-Dialog, Test-Helfer filtert `aria-hidden`; 1342 Tests, sechs Gegenproben |
| 2026-11-09 | abgeschlossen (Orchestrator hat build/test/lint/format im Worktree geprüft) |
