# AP-039 – Schema-Editor im Frontend: Properties anlegen, ändern, entfernen (inkl. Relationen)

| Feld | Wert |
|---|---|
| Status | angelegt |
| Phase | 2 |
| Implementierer | implementer-opus |
| Referenzbeispiel | Formulare mit Signal Forms und Feldfehlern: `features/auth/change-password.component.ts`, `core/auth/password-policy.ts`; Dialoge: `features/admin/reset-password-dialog.component.ts`, `shared/ui/confirm-dialog.component.ts`; Collection-Seite `features/collections/collection-page.component.ts`; Live-Schema `features/views/collection.store.ts` (`holdWhile`, `liveReload`); UI-Bausteine `02-ARCHITEKTUR.md` §6 |
| Abhängig von | – (nutzt den vorhandenen `PATCH /collections/{id}/schema`) |
| Betroffene Pfade | neu `src/frontend/src/app/features/collections/schema-editor/` (oder `features/schema/`), Route `/w/:slug/settings/collections/:id/schema` (laut §6 „Routen“), Link von `collection-page.component.ts`; ggf. `shared/properties/` nur für wiederverwendbare Typ-Labels. **Nicht:** Backend (AP-037 läuft parallel), `shared/properties/relation-editor.component.ts` und Zellen (AP-038 folgt) |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §2 „Schema-Regeln (aus AP-006)“, „Property-Typen“, „Relationen“, §6 |

## Ziel

Menschen mit `schema`-Recht pflegen das Schema einer Collection in der UI: neue Properties aller Typen anlegen (auch Relationen auf andere oder dieselbe Collection), Namen, Beschreibungen und Optionen ändern, erlaubte Typwechsel vornehmen und Properties bewusst entfernen – mit denselben Regeln und Fehlern wie Agenten über MCP.

## Scope

**Drin:**
- Liste der Properties (Key, Name, Typ, Pflicht, Beschreibung), Reihenfolge wie im Schema; `title` nicht entfernbar.
- **Anlegen:** Dialog mit Key (Muster `^[a-z][a-z0-9_]{0,62}$`, reservierte Keys, Vorschlag aus dem Namen), Name, Typ, Beschreibung (für Agenten, mit Hinweistext), Pflicht, Default (wo erlaubt), typ-spezifische Optionen:
  - select/multi_select/status: Optionen mit Value, Label, Farbe (feste Palette), bei status Gruppe (todo/in_progress/done);
  - number: Präzision, Einheit;
  - relation: Ziel-Collection (Auswahl aus den Collections des Workspace, archivierte ausgeblendet, eigene Collection als „diese Collection“ wählbar) und `multiple`.
- **Ändern:** Name, Beschreibung, Pflicht, Default, Optionen (hinzufügen, umbenennen, Farbe), erlaubte Typwechsel (text ↔ markdown, select → multi_select), relation `multiple` nur false → true; Ziel-Collection und Key nicht änderbar (deaktiviert mit Erklärung).
- **Destruktiv:** Property entfernen, Optionen entfernen, Präzision senken, markdown → text nur mit Bestätigungsdialog („Werte in N Records gehen verloren“, wenn die Zahl billig ermittelbar ist, sonst allgemein) und `confirmRemoval: true`.
- **Nebenläufigkeit:** `expectedSchemaVersion` mitsenden; bei 409 Hinweis „Schema wurde inzwischen geändert“, Neu laden ohne Verlust der eigenen Eingaben im offenen Dialog.
- **Fehler** aus `errors[]` am richtigen Feld (`invalid_format`, `reserved`, `not_allowed`, `target_archived` aus AP-037, falls schon da, sonst generisch).
- **Rechte:** Nur mit `schema`-Scope sichtbar/bedienbar (Link und Route; ohne Recht Leseansicht).
- **Live:** `collection.schema_changed` durch andere lädt die Liste neu; offene Dialoge bleiben (Muster `holdWhile`).

**Ausdrücklich nicht drin:**
- Backend, Reihenfolge per Drag & Drop (falls die API sie nicht kennt), Vorlagen-Verwaltung, Relationstitel in Zellen (AP-038).

## Akzeptanzkriterien

1. Property jedes Typs anlegen, inkl. Relation auf andere und eigene Collection mit `multiple`; erscheint sofort in Tabelle und Dokument.
2. Erlaubte Änderungen funktionieren; verbotene sind deaktiviert mit Erklärung; Server-Fehler erscheinen am Feld.
3. Destruktive Änderungen nur nach Bestätigung, mit `confirmRemoval`.
4. 409 bei veralteter Schema-Version: Hinweis, Neuladen, Dialog-Eingaben bleiben.
5. Ohne `schema`-Recht kein Bearbeiten.
6. A11y nach §6 (Dialoge, Labels, Fehler am Feld, Tastatur).
7. `npm run build`, `npm test`, `npm run lint`, `npm run format:check` grün; Orchestrator-Rauchtest.

## Entscheidungen, die der Implementierer treffen darf

- Ort der Route/Seite (eigene Seite oder Tab an der Collection), Gestaltung der Options-Liste, Key-Vorschlag.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-08 | angelegt |
