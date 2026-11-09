# AP-041 – Vorlagen-Verwaltung im Frontend: Record-Vorlagen anlegen, bearbeiten, als Standard setzen, aus einem Record übernehmen

| Feld | Wert |
|---|---|
| Status | angelegt |
| Phase | 2 |
| Implementierer | implementer-sonnet |
| Referenzbeispiel | Schema-Editor `src/frontend/src/app/features/collections/schema-editor/` (Seite, Store mit Live-Neuladen, Dialoge, Rechte, Fehler-Mapping, Bestätigung); Property-Editoren `shared/properties/property-editor.component.ts` (inkl. Relationen mit Picker und Chips); Markdown-Editor `shared/editor/markdown-editor.component.ts` (mit Schutz gegen präparierten Markdown aus AP-034); Anlage-Dialog `features/records/record-create-dialog.component.ts` (Vorlagen-Auswahl) |
| Abhängig von | AP-039, AP-040 (committet) |
| Betroffene Pfade | neu `src/frontend/src/app/features/collections/templates/` (Seite, Store, Dialog/Editor), Route neben dem Schema-Editor (z. B. `/w/:slug/settings/collections/:id/templates`) mit Navigation zwischen „Schema“ und „Vorlagen“, Link von `collection-page.component.ts`; optional Aktion „Als Vorlage speichern“ in `features/records/record-document.component.ts` (nur Menüpunkt, der zur Vorlagen-Seite mit vorbefülltem Entwurf führt). **Nicht:** Backend, Schema-Editor-Logik, Anlage-Dialog außer falls die Liste nach Änderung neu laden muss |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §2 (Template: `CollectionId`, `Name`, `Description`, `PropertiesPreset`, `BodyMarkdown`, `IsDefault`, genau eine Standardvorlage je Collection), Schema-Regeln (Vorlagen pflegen: Scope `schema`), §6 |

## Ziel

Menschen mit `schema`-Recht pflegen die Record-Vorlagen einer Collection in der UI – mit vorbelegten Property-Werten und Markdown-Body –, legen die Standardvorlage fest und können einen gelungenen Record als Vorlage übernehmen. Agenten profitieren davon, weil `record_create` die Standardvorlage anwendet.

## Scope

**Drin:**
1. **Liste** der Vorlagen der Collection (Name, Beschreibung, Standard-Badge, Anzahl vorbelegter Felder, Auszug des Bodys als Text), Aktionen je Vorlage: Bearbeiten, Als Standard setzen, Löschen (mit `ag-confirm-dialog`).
2. **Editor** (Dialog oder eigene Seite): Name (Pflicht), Beschreibung (für Agenten, mit Hinweis), Vorbelegung je Property über die vorhandenen Property-Editoren (nur Properties mit Werten werden gesendet; „Feld leeren“ entfernt die Vorbelegung; berechnete Typen und `title` nicht vorbelegbar, außer `title` falls das Backend es erlaubt), Body über den Markdown-Editor (Fallback bei zu großem/komplexem Text wie im Dokument, dann Bearbeitung als Textarea), „Standardvorlage“ als Schalter.
3. **Speichern** über `POST`/`PATCH /collections/{id}/templates[/{templateId}]`, nur geänderte Felder beim PATCH; Feldfehler aus `errors[]` am Feld (inkl. `propertiesPreset.<key>` an der jeweiligen Property, z. B. `not_in_options`, `unknown_property`).
4. **Standard:** Setzen einer neuen Standardvorlage entfernt das Badge der alten (Server sorgt für genau eine; UI lädt die Liste nach dem Speichern neu).
5. **Aus Record übernehmen:** Im Dokument Aktion „Als Vorlage speichern“ (nur mit `schema`-Recht, nur bei Records einer Collection): öffnet den Vorlagen-Editor vorbefüllt mit den Property-Werten (ohne berechnete Typen) und dem Body des Records; Name vorgeschlagen aus dem Titel.
6. **Rechte und Archiv:** ohne `schema`-Recht oder bei archivierter Collection/Workspace nur lesbar (Link nur mit Recht).
7. **Aktualität:** Kein Live-Kanal für Vorlagen; nach eigenen Änderungen Liste neu laden; bei `collection.schema_changed` die Liste neu laden (Vorbelegungen können serverseitig bereinigt worden sein); offene Editor-Entwürfe bleiben dabei erhalten.
8. **Anlage-Dialog:** zeigt nach Änderungen die aktuelle Vorlagenliste (falls er sie gecacht hält, beim Öffnen neu laden).

**Ausdrücklich nicht drin:**
- Backend, Concurrency-Token für Vorlagen (siehe Kontext), Workspace-Vorlagen (Phase 3), Vorlagen-Variablen/Platzhalter.

## Fachlicher Kontext

- Vorlagen haben **kein** Concurrency-Token (AP-006-Hinweis): gleichzeitiges Bearbeiten → letzte Änderung gewinnt. Die UI zeigt im Editor einen Hinweis, wenn die Vorlage seit dem Öffnen auf dem Server geändert wurde (Vergleich beim Speichern über einen frischen GET, ohne zu blockieren) – nur informativ.
- Vorbelegungen werden bei Schemaänderungen serverseitig bereinigt (`TemplatePresetSchemaHook`).

## Akzeptanzkriterien

1. Vorlage mit Vorbelegung (Auswahl, Relation, Zahl) und Markdown-Body anlegen; im Dialog „Neuer Record“ wählbar; neuer Record übernimmt Werte und Body.
2. Bearbeiten sendet nur Änderungen; Feld leeren entfernt die Vorbelegung.
3. Standard setzen: genau eine Standardvorlage sichtbar; Anlage-Dialog wählt sie vor.
4. Löschen nur nach Bestätigung.
5. „Als Vorlage speichern“ aus einem Record befüllt den Editor korrekt (ohne berechnete Felder).
6. Ohne `schema`-Recht nur Leseansicht; Feldfehler am Feld (inkl. Property-Vorbelegung).
7. Body-Editor mit Fallback bei großem Text, nichts geht verloren.
8. `npm run build`, `npm test`, `npm run lint`, `npm run format:check` grün; Orchestrator-Rauchtest.

## Entscheidungen, die der Implementierer treffen darf

- Dialog oder eigene Seite für den Editor, Darstellung der Liste, Route und Navigation zwischen Schema und Vorlagen.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-09 | angelegt |
