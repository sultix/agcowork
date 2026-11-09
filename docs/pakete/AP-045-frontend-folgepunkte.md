# AP-045 – Frontend-Folgepunkte: „Ohne Vorlage“, Titel-Vorschlag, Server-Meldungen an Feldern, Zitate im Mention-Port

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | 2 |
| Implementierer | implementer-sonnet |
| Referenzbeispiel | Anlage-Dialog `src/frontend/src/app/features/records/record-create-dialog.component.ts` (Vorlagenauswahl aus AP-041); Feldfehler `core/api/field-errors.ts`, `features/collections/schema-editor/schema-errors.ts`, `features/collections/templates/template-errors.ts`; Server-Parser als Vorlage für den Port: `src/backend/Agcowork.Domain/Comments/CommentMarkdown.cs` (Zitate, `MaxQuoteDepth = 32`, Stapel statt Rekursion); Port `features/comments/comment-mentions.ts` |
| Abhängig von | AP-042 (Vertrag `skipTemplate`), AP-044 nur für disjunkte Dateien (parallel möglich) |
| Betroffene Pfade | `features/records/record-create-dialog.component.*`, `core/api/field-errors.ts` (+ Spec), `features/collections/schema-editor/schema-errors.ts`, `features/collections/templates/template-errors.ts`, `features/comments/comment-mentions.*`, generierte API-Typen (`npm run` Generator aus OpenAPI). **Nicht:** `shared/properties/` (AP-044), Backend |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §6 |

## Ziel

Menschen können im Dialog „Neuer Record“ ausdrücklich ohne Vorlage starten, und ein vorbelegter Titel der Vorlage erscheint als Vorschlag. Feldfehler zeigen die konkrete Servermeldung statt nur einer generischen. Die Mention-Vorschau im Kommentar-Editor erkennt Zitate genauso wie der Server.

## Scope

**Drin:**
1. **„Ohne Vorlage“:** API-Typen regenerieren. Im Anlage-Dialog (nur Collection-Records mit mindestens einer Vorlage) eine Option „Ohne Vorlage“; gewählt → `skipTemplate: true` und kein `templateId`. Vorauswahl bleibt die Standardvorlage, sonst „Ohne Vorlage“ (wenn es keine Standardvorlage gibt, entspricht das dem bisherigen Verhalten ohne Feld).
2. **Titel-Vorschlag:** Hat die gewählte Vorlage `propertiesPreset.title`, füllt der Dialog das Titelfeld damit, solange der Mensch es nicht selbst geändert hat. Ein Wechsel der Vorlage aktualisiert den Vorschlag nur, wenn das Feld noch unverändert ist; „Ohne Vorlage“ leert einen übernommenen Vorschlag.
3. **Server-Meldungen:** `fieldErrorMessage` (und die Varianten im Schema- und Vorlagen-Editor) zeigen bei bekannten Codes weiter den deutschen Text, hängen aber die Server-`message` als Detail an, wenn sie zusätzliche Information trägt (z. B. erlaubte Werte, betroffene Records), oder nutzen sie bei unbekannten Codes. Darstellung kurz und ohne Doppelungen; XSS: nur Text.
4. **Mention-Port mit Zitaten:** `comment-mentions.ts` behandelt Blockquotes wie `CommentMarkdown` auf dem Server (`> ```` …` und `>     …` sind Code, keine Erwähnung), `MaxQuoteDepth = 32`, iterativ (Stapel statt Rekursion), Suche nach `-->` einmal statt quadratisch. Gemeinsame Testfälle mit dem Server (dieselben Eingaben, gleiche Ergebnisse), mindestens die aus dem AP-035-Review.

**Ausdrücklich nicht drin:**
- Backend, Property-Editoren, A11y des Pickers (AP-044).

## Fachlicher Kontext

- AP-041: Der Dialog zeigt die Vorlagenauswahl ab einer Vorlage und sendet `templateId`; „Ohne Vorlage“ fehlte, weil das Backend den Weg nicht kannte (AP-042 bringt `skipTemplate`).
- AP-039: `fieldErrorMessage` ersetzt spezifische Servermeldungen durch generische (`not_allowed` → „Nicht erlaubt.“).
- AP-035: Server zählt Erwähnungen in Code innerhalb von Zitaten nicht, die UI zeigt aber einen Chip.

## Akzeptanzkriterien

1. Standardvorlage vorhanden, „Ohne Vorlage“ gewählt → POST mit `skipTemplate: true` ohne `templateId`; Standard gewählt → `templateId` wie bisher (Test).
2. Titel-Vorschlag: übernommen, nicht überschrieben nach eigener Eingabe, aktualisiert beim Wechsel nur bei unverändertem Feld (Tests).
3. Feldfehler mit Server-Detail sichtbar (Tests je Editor-Variante).
4. Mention-Port: Fälle aus AP-031/035 (Zitat mit Fence, eingerücktes Zitat, Tiefe > 32, viele `<!--`) liefern dasselbe wie der Server; Laufzeit linear (Test mit großer Eingabe unter Zeitgrenze).
5. `npm run build`, `npm test`, `npm run lint`, `npm run format:check` grün; Orchestrator-Rauchtest.

## Tests, die mindestens entstehen

- Komponente: Anlage-Dialog (Optionen, Body, Titel-Vorschlag).
- Unit: Feldfehler-Texte, Mention-Port.

## Entscheidungen, die der Implementierer treffen darf

- Darstellung des Server-Details, Platzierung der Option „Ohne Vorlage“ in der Auswahl.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-09 | angelegt |
| 2026-11-09 | an implementer-sonnet übergeben (Vertrag `skipTemplate` aus AP-042 liegt uncommittet im Working Tree; Review AP-042 läuft) |
| 2026-11-09 | Bericht: 1371 Tests grün; „Ohne Vorlage“ sendet `skipTemplate: true`, Vorauswahl Standard sonst „Ohne Vorlage“; Titel-Vorschlag aus `propertiesPreset.title`; `composeFieldMessage` mit „Erlaubt: …“ und Server-Hinweis; Mention-Port 1:1 zum Server-`BlockScanner` mit gemeinsamen Testfällen |
| 2026-11-09 | Review gestartet |
| 2026-11-09 | Review: Freigabe mit Hinweisen – Mention-Port differentiell gegen den Server geprüft (200 000 Zufallseingaben, eine Abweichung bei exotischem Whitespace); wichtig: Server-Hinweis an fast allen Codes doppelt/widerspricht dem deutschen Text; Hinweise: Whitespace-Klassen, Kürzung zerteilt Surrogate, Tests für Laderennen und Nicht-String-Titel, `idempotency_key_reused` ohne deutschen Text, zwei Ausnahmemechanismen |
| 2026-11-09 | Nacharbeit an implementer-sonnet (Allowlist für Hinweise, Whitespace, Kürzung, Tests, Text) |
| 2026-11-09 | Nacharbeit: Hinweis nur noch für Allowlist (`too_long`, `too_short`, `out_of_range`, `invalid_format`, `not_allowed`, `removal_not_confirmed`), ein Mechanismus, `conflict` neutral, Feldname-Präfix entfernt; .NET-Whitespace im Mention-Port; surrogat-sichere Kürzung; Tests Laderennen/Nicht-String-Titel; Text für `idempotency_key_reused`; 1376 Tests; wartet auf AP-042-Commit (`schema.d.ts`) |
| 2026-11-09 | abgeschlossen (Orchestrator hat build/test/lint/format im Worktree gegen den AP-042-Commit geprüft, `schema.d.ts` daraus neu erzeugt) |
| 2026-11-09 | Rauchtest: Anlage-Dialog zeigt „Ohne Vorlage“, wählt die Standardvorlage vor und setzt deren Titel als Vorschlag; „Ohne Vorlage“ leert den Vorschlag, neuer Record ohne Vorbelegung und Body |
