# AP-020 – UI-Migration: Verwaltung, Spaces, Collections, Aktivität

| Feld | Wert |
|---|---|
| Status | angelegt |
| Phase | UI |
| Implementierer | implementer-sonnet |
| Referenzbeispiel | AP-019: `features/auth/login.component.ts` (Formular), `features/workspaces/` (Liste, Formular, Dropdown), `features/workspace-shell/` (Layout, Icons), `shared/ui/confirm-dialog.component.ts` (Alert-Dialog); Mapping-Tabelle und Faustregeln in `docs/02-ARCHITEKTUR.md` §6 „UI-Bausteine“ |
| Abhängig von | AP-019 |
| Betroffene Pfade | ausschließlich `src/frontend/src/app/features/workspace-settings/`, `features/admin/`, `features/spaces/` (ohne `workspace-tree.component.ts`), `features/collections/`, `features/activity/`, `features/dev-playground/` |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §6 „UI-Bausteine“ |

## Ziel

Die Verwaltungs- und Übersichtsseiten nutzen die Helm-Bausteine und shadcn-Klassen aus AP-019, sodass sie aussehen wie ui.shadcn.com. Verhalten bleibt unverändert.

## Scope

**Drin:**
- Alle Templates in den betroffenen Pfaden: alte Farbklassen nach Mapping-Tabelle ersetzen; rohe `<button>`, `<input>`, `<select>`, `<textarea>`, `<label>`, `<table>` durch die Helm-Direktiven/-Komponenten ersetzen; Karten/Abschnitte als Card; Tabellen (Mitglieder, Actors, Keys) als Helm-Table; Rollen/Scopes als Badge; Dialoge (Key anzeigen, Einladen, Anlegen) als Helm-Dialog; Bestätigungen über `ag-confirm-dialog`; Aktionsmenüs je Zeile als Dropdown-Menu, wo heute mehrere Buttons nebeneinander stehen; Icons nur Lucide über `ng-icon`.
- Leerzustände und Ladezustände (Skeleton) nach den Faustregeln.
- Specs anpassen, wo die Struktur sich zwangsläufig ändert (Overlay im Body, Dropdown statt Button). `data-testid`s bleiben.

**Ausdrücklich nicht drin:**
- Dateien außerhalb der betroffenen Pfade, insbesondere `shared/`, `styles.css`, Helm-Komponenten, `features/records/`, `features/views/` (AP-021 läuft parallel). Fehlt ein Helm-Baustein, nicht selbst erzeugen: im Bericht melden, bis dahin vorhandene Bausteine nutzen.
- Verhaltensänderungen, neue Funktionen, Stores, API.
- Entfernen der Übergangs-Aliase.

## Fachlicher Kontext

- Die Mapping-Tabelle in §6 ist verbindlich; vor allem `text-muted` → `text-muted-foreground` und `bg-accent` → `bg-primary` nicht wörtlich übernehmen.
- Der neu erzeugte API-Key wird nur einmal angezeigt; die Anzeige muss weiterhin Kopieren ermöglichen und deutlich als einmalig markiert sein.

## Akzeptanzkriterien

1. `npm run build`, `npm test`, `npm run lint`, `npm run format:check` grün bzw. ohne neue Fehler.
2. In den betroffenen Pfaden kommt keine alte Farbklasse aus der Mapping-Tabelle mehr vor (per `grep` im Bericht belegt).
3. Keine rohen, ungestylten Formularelemente oder Buttons mehr in den betroffenen Pfaden (Ausnahmen begründet im Bericht).
4. Alle bestehenden Specs grün; kein Test wurde abgeschwächt.
5. Beide Themes stimmig (Kontrast von Text auf Flächen, Fehlerfarben).

## Tests, die mindestens entstehen

- Bestehende Specs der betroffenen Dateien, angepasst wo nötig.
- Je ein Test für jedes neu eingeführte Dropdown-Aktionsmenü (öffnet, löst die Aktion aus).

## Entscheidungen, die der Implementierer treffen darf

- Card-Raster oder Table für Übersichten, wenn die Faustregeln beides erlauben.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
