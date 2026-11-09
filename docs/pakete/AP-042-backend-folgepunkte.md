# AP-042 – Backend-Folgepunkte: Anlegen ohne Vorlage, Cover-Typen, Präzision entfernen, MCP-Titel escapen

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | 2 |
| Implementierer | implementer-sonnet |
| Referenzbeispiel | Optionale Felder im Anlegen: `src/backend/Agcowork.Application/Records/CreateRecord.cs` (`TemplateId`, `LoadTemplateAsync`), REST `Agcowork.Api/Endpoints/Records/RecordEndpoints.cs`, MCP `Agcowork.Api/Mcp/` (`record_create`); View-Validierung `Agcowork.Application/Views/ViewDefinitionValidator.cs` (Fehler mit `allowed`, z. B. bei `groupBy`); Schema-Änderung `Agcowork.Application/Collections/UpdateSchema.cs` (Verlust-Erkennung ~247–260, Übernahme ~337); Escaping `Agcowork.Api/Mcp/McpText.cs` (Activity-Zeilen) |
| Abhängig von | AP-041 (committet) |
| Betroffene Pfade | `src/backend/Agcowork.Application/{Records,Views,Collections}/`, `src/backend/Agcowork.Api/{Endpoints,Mcp}/`, `src/backend/Agcowork.Api/openapi/` (falls eingecheckt), `tests/`. **Nicht:** Frontend (folgt in AP-045), Auth, `Program.cs` |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §2 (Templates, Schema-Regeln), §3 (Views), §4 (MCP-Texte) |

## Ziel

Menschen und Agenten können einen Record ausdrücklich **ohne** Vorlage anlegen, auch wenn die Collection eine Standardvorlage hat. Views nehmen als Cover nur Typen an, die die UI darstellen kann. Die Präzision einer Zahl lässt sich entfernen, und eine Präzisionsänderung meldet nur echten Verlust. MCP-Texte sind gegen Titel mit Markdown-Steuerzeichen abgesichert.

## Scope

**Drin:**
1. **Anlegen ohne Vorlage:** `CreateRecord` bekommt `bool SkipTemplate` (Standard `false`). REST-Body `skipTemplate`, MCP `record_create`-Argument `skipTemplate` (mit agententauglicher Beschreibung: „true: start empty even if the collection has a default template“). `skipTemplate: true` zusammen mit `templateId` → 400 mit Feldfehler an `skipTemplate` (Code nach vorhandenem Muster, z. B. `conflict`/`not_allowed`). Freie Seiten (ohne Collection) ignorieren das Feld wie bisher `templateId`. Idempotenz: Der Request-Hash (AP-035) muss das Feld abdecken (Test: gleicher Key, anderes `skipTemplate` → Konflikt wie bei anderem Inhalt).
2. **Cover-Typen:** `cardCoverProperty` nur für `select`, `status`, `multi_select`, `checkbox`, `text`, `url`, `email`, `number`, `date`; sonst 400 an `cardCoverProperty` mit `allowed`. Gilt für Create und Update (REST und MCP, falls MCP Views anlegt). Bei Schemaänderung (Typwechsel auf einen nicht erlaubten Typ) entfernt der vorhandene View-Schema-Hook das Cover wie beim Löschen der Property.
3. **Präzision:** `UpdateSchema` kann `precision` entfernen (Weg nach vorhandenem Muster für „Feld leeren“ im Schema-PATCH wählen, z. B. `precision: null` explizit vs. weggelassen, oder `clearPrecision: true`; im Bericht begründen). Verlust (`lossy`/Bestätigungspflicht) nur, wenn gespeicherte Werte tatsächlich mehr Nachkommastellen haben als die neue Präzision; erstmaliges Setzen ohne betroffene Werte ist kein Verlust. Die Meldung nennt die Zahl betroffener Records, falls das Muster es hergibt.
4. **MCP-Titel escapen:** In `McpText` werden Titel (und andere Freitexte von Nutzern wie Collection-/View-Namen) in Markdown-Zeilen wie `RecordLine` (`**{Title}**`) so escaped, dass `*`, `_`, `` ` ``, `[`, `]`, Zeilenumbrüche und führende `#`/`>`/`-` die Struktur nicht brechen. Gleiches Verfahren wie bei Activity-Zeilen, an einer Stelle zentral.

**Ausdrücklich nicht drin:**
- Frontend, Rate-Limiting, Restore von Collections/Spaces (existiert nicht), Concurrency-Token für Vorlagen.

## Fachlicher Kontext

- Heute: `TemplateId == null` → Standardvorlage wird angewendet (`CreateRecord.cs` ~151). Ein Mensch, der im Dialog „Ohne Vorlage“ wählt, hat dafür keinen Weg.
- AP-040 hat die Cover-Typen nur im Frontend geprüft; Agenten können heute `relation`/`json`/`markdown` setzen, die UI zeigt dann kein Cover.
- `UpdateSchema.cs:337` `Precision = change.Precision ?? before.Precision` macht Entfernen unmöglich; `:255` meldet Verlust auch beim ersten Setzen.

## Akzeptanzkriterien

1. Collection mit Standardvorlage: `POST /records` mit `skipTemplate: true` legt den Record ohne Vorbelegung und ohne Body der Vorlage an; ohne Feld wird wie bisher die Standardvorlage angewendet; MCP gleich.
2. `skipTemplate` + `templateId` → 400 mit Feldfehler.
3. View mit `cardCoverProperty` auf eine Relation → 400 mit `allowed`; Typwechsel einer Cover-Property auf `json` entfernt das Cover.
4. Präzision entfernen klappt; Senken der Präzision ohne betroffene Werte ist kein Verlust, mit betroffenen Werten wie bisher bestätigungspflichtig.
5. Record mit Titel `**fett** [x](y)\n# H` erscheint in `record_*`-Texten escaped (Test auf genaue Ausgabe).
6. Isolation: fremde Collection-ID bei `skipTemplate` → 404 (vorhandene Isolationstests decken Records/Views ab; ergänzen, wo neue Pfade entstehen).
7. `dotnet build` ohne Warnungen, `dotnet test` grün, OpenAPI aktualisiert (Frontend-Typen regeneriert AP-045).

## Tests, die mindestens entstehen

- Unit: Präzisions-Verlust (mit/ohne betroffene Werte), Escaping in `McpText`.
- Integration: REST und MCP `skipTemplate`, Konflikt mit `templateId`, Idempotenz-Hash, Cover-Validierung Create/Update, Hook bei Typwechsel, Präzision entfernen.

## Entscheidungen, die der Implementierer treffen darf

- Fehlercodes nach vorhandenem Katalog, Form des „Präzision entfernen“ im PATCH (im Bericht begründen).

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-09 | angelegt |
| 2026-11-09 | an implementer-sonnet übergeben (parallel zu AP-044, disjunkte Dateien) |
| 2026-11-09 | Orchestrator: `skipTemplate` erschien im OpenAPI mit `default: false` (Frontend-Generator → Pflichtfeld) → zurück an den Implementierer; jetzt `bool?` ohne Default, Vertragstest |
| 2026-11-09 | Bericht: 1472 Tests grün; `skipTemplate` (REST/MCP, Konflikt mit `templateId` → 400 `conflict`, im Idempotenz-Hash nur bei true), Cover-Typen mit `allowed` und Hook bei Typwechsel, `clearPrecision` (Muster `ClearDefault`), Verlust nur bei Werten mit mehr Nachkommastellen (mit Anzahl), `McpText.Quote` für Titel/Namen (`**Titel**` → `"Titel"`); Auffälligkeit: Emoji in `Quote` als `\uD83D…` |
| 2026-11-09 | Review gestartet |
| 2026-11-09 | Review: Freigabe mit Hinweisen – Rundung/Tenancy von `PrecisionLossesAsync` und Fingerprint geprüft; wichtig: Senken der Präzision verwirft Vorlagen-Presets ohne Bestätigung, `title` als Cover erlaubt (UI kann ihn nicht darstellen); Hinweise: Emoji-Escaping (eingebaute Encoder escapen außerhalb der BMP immer), Beschreibungen in MCP-Texten unescaped, Texte zu `skipTemplate`, Performance (später), einmal nicht zuordenbarer Testfehler; Abweichung AK3: Typwechsel auf json gibt es nicht, Hook über text → markdown getestet |
| 2026-11-09 | Nacharbeit an implementer-sonnet (W1 Presets zählen und runden, W2 `title` ausschließen, eigene Quote-Funktion, Beschreibungen, Texte) |
| 2026-11-09 | Nacharbeit: Vorlagen-Presets zählen bei Präzisionsverlust mit und werden gerundet statt gelöscht, `title` kein Cover, zentrale `QuotedString.Of` (Emoji lesbar, ersetzt Duplikate in `McpText`/`CommentText`), Beschreibungen in MCP-Texten einzeilig mit `Description:`-Präfix, Texte zu `skipTemplate`, „precision set to“ beim ersten Setzen; 1481 Tests zweimal grün |
| 2026-11-09 | abgeschlossen (Orchestrator hat build/test im Worktree geprüft, 1481 grün, 0 Warnungen) |
