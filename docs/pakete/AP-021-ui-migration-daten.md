# AP-021 – UI-Migration: Records, Views, Tabelle, Filter, Property-Editoren

| Feld | Wert |
|---|---|
| Status | angelegt |
| Phase | UI |
| Implementierer | implementer-sonnet |
| Referenzbeispiel | AP-019: `features/auth/login.component.ts` (Formular), `features/workspaces/` (Liste, Dropdown), `features/workspace-shell/` (Layout, Icons), `shared/ui/confirm-dialog.component.ts` (Alert-Dialog); Mapping-Tabelle und Faustregeln in `docs/02-ARCHITEKTUR.md` §6 „UI-Bausteine“ |
| Abhängig von | AP-019 |
| Betroffene Pfade | ausschließlich `src/frontend/src/app/features/records/` (inkl. `history/`, `raw/`), `features/views/`, `shared/table/`, `shared/filter/`, `shared/properties/`, `shared/raw-view/raw-view.component.ts` (nur Template/Klassen) |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §6 „Bausteine“ und „UI-Bausteine“ |

## Ziel

Dokument-, Tabellen-, Board-, Raw- und Historien-Ansicht samt Filter-Leiste und Property-Editoren nutzen die Helm-Bausteine und shadcn-Klassen aus AP-019. Verhalten, Tastaturbedienung und Inline-Edit bleiben unverändert.

## Scope

**Drin:**
- Alle Templates in den betroffenen Pfaden: alte Farbklassen nach Mapping-Tabelle ersetzen; Buttons, Inputs, Selects, Checkboxen, Labels durch Helm-Bausteine; Record-Tabs als Helm-Tabs; View-Switcher als Tabs oder Dropdown; Filter-Leiste und Sort-Chips mit Popover/Badge/Button; Record-Anlage- und Konflikt-Dialog als Helm-Dialog; Property-Editoren mit Helm-Form-Bausteinen (Select/Multi-Select/Relation mit Popover); Board-Karten als Card; Icons nur Lucide.
- `data-table.component.ts`: Optik an die shadcn-Data-Table angleichen (Kopfzeile, Zeilen-Hover, Rahmen, Zellen-Padding). Eigene Komponente auf CDK Virtual Scroll bleibt; **nicht** durch Helm-Table ersetzen, wenn das Virtual Scroll, Spalten-Resize oder Inline-Edit gefährdet – dann nur Klassen übernehmen.
- Specs anpassen, wo die Struktur sich zwangsläufig ändert. `data-testid`s bleiben.

**Ausdrücklich nicht drin:**
- Dateien außerhalb der betroffenen Pfade, insbesondere `shared/ui/`, `shared/editor/`, `styles.css`, Helm-Komponenten, `monaco-adapter.ts`, die Features aus AP-020 (läuft parallel). Fehlt ein Helm-Baustein (z. B. Command/Combobox), nicht selbst erzeugen: im Bericht melden und vorhandene Bausteine nutzen.
- Änderungen an Inline-Edit-Logik, Speichern, Konfliktbehandlung, Filter-DSL, Stores, API.
- Entfernen der Übergangs-Aliase.

## Fachlicher Kontext

- Inline-Edit, Tastaturnavigation in der Tabelle und der Leave-Guard sind über Tests abgesichert und in Phase 1 mehrfach nachgearbeitet worden (Datenverlust-Pfade). Bei Konflikt zwischen Optik und Verhalten gewinnt das Verhalten.
- Brain-Overlays rendern in den Body; ein Popover in einer Tabellenzelle darf Fokus und Escape-Verhalten des Inline-Edits nicht brechen (Escape bricht ab, Enter speichert).
- Die Mapping-Tabelle in §6 ist verbindlich; vor allem `text-muted` → `text-muted-foreground` und `bg-accent` → `bg-primary` nicht wörtlich übernehmen.

## Akzeptanzkriterien

1. `npm run build`, `npm test`, `npm run lint`, `npm run format:check` grün bzw. ohne neue Fehler.
2. In den betroffenen Pfaden kommt keine alte Farbklasse aus der Mapping-Tabelle mehr vor (per `grep` im Bericht belegt).
3. Keine rohen, ungestylten Formularelemente oder Buttons mehr (Ausnahmen, z. B. Zellen-Editoren aus Performancegründen, begründet im Bericht).
4. Alle bestehenden Specs grün, insbesondere Inline-Edit, Data-Table, Record-Save, Filter-Builder; kein Test wurde abgeschwächt.
5. Inline-Edit in Tabelle und Property-Panel: Enter speichert, Escape bricht ab, auch wenn der Editor ein Popover öffnet (Test für mindestens Select und Relation).
6. Beide Themes stimmig, auch Diff-Farben in der Historie.

## Tests, die mindestens entstehen

- Bestehende Specs, angepasst wo nötig.
- Kriterium 5 für Select- und Relation-Editor mit Popover.

## Entscheidungen, die der Implementierer treffen darf

- View-Switcher als Tabs oder Dropdown.
- Wie weit die Data-Table Helm-Table-Direktiven übernimmt (siehe Scope).

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
