# AP-035 – Härtung Backend: Idempotenz mit Inhaltsprüfung, Erwähnungen in Zitaten, Activity je Record, Schema-Hooks

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | 2 (Härtung) |
| Implementierer | implementer-opus |
| Referenzbeispiel | Idempotenz `src/backend/Agcowork.Application/Records/CreateRecord.cs` (`ReplayAsync`), `Comments/AddComment.cs`, `Domain/Records/IdempotencyKey.cs`; Parser `Domain/Comments/CommentMarkdown.cs`; Activity `Application/Activity/ListActivity.cs`, `ActivityLog.cs`; Hooks `Application/Records/RecordValuesSchemaHook.cs` (select→multi_select, relation.multiple) |
| Abhängig von | AP-033 (committet) |
| Betroffene Pfade | `src/backend/` (Domain Records/Comments/Activity, Application Records/Comments/Activity/Views/Collections-Templates, Infrastructure Persistence mit **einer** Migration), `src/backend/Agcowork.Api/` (Beschreibungen, `openapi/v1.json`), `tests/`; im Frontend nur das neu erzeugte `schema.d.ts`. **Nicht:** Frontend-Code (AP-034 läuft parallel), Realtime-Transport |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §2 „Kommentare und Erwähnungen“, §4.1 (Idempotenz), §5 „Outbox-Pattern“ |

## Ziel

Agenten können sich auf Idempotenz verlassen, ohne versehentlich einen fremden Inhalt zurückzubekommen; Erwähnungen zählen nur dort, wo Menschen sie als Erwähnung sehen; ein Agent kann die gesamte Geschichte eines Records einschließlich Kommentaren abfragen.

## Scope

**Drin:**

1. **Idempotenz mit Inhaltsprüfung** (AP-031-Review): `idempotency_keys` speichert einen Hash der normalisierten Anfrage (SHA-256 über Ziel und Inhalt: bei `record_create` Collection/Space, Titel, Properties, Body, Template, Parent; bei `comment_add` Record, Parent, Markdown). Gleicher Key mit abweichendem Hash → 409 `idempotency_key_reused` mit Hinweis „same key, different request“. Gleicher Key, gleicher Hash → Replay wie bisher. Bestandsschlüssel ohne Hash: Replay wie bisher (laufen nach 24 h aus). Beschreibungen in OpenAPI und MCP ergänzen.
2. **Erwähnungen in Blockquotes** (AP-031-Review): `CommentMarkdown` erkennt Blockquote-Präfixe (`>` mit optionalem Leerzeichen, verschachtelt) und wendet die Code-Regeln (Fence, 4-Spalten-Einzug, HTML-Kommentar) innerhalb des Zitats an. `> ```` … ```` und `>     [@a](actor:id)` zählen nicht. XML-Kommentar und Tests ergänzen. Den Frontend-Port (`features/comments/comment-mentions.ts`) **nicht** ändern, aber im Bericht die genaue Regeländerung beschreiben (Folgeänderung im Frontend übernimmt der Orchestrator in einem Kleinpaket).
3. **Activity je Record:** `activity_events.related_record_id` (nullable, Index mit `workspace_id` vorn): bei Record-Ereignissen = `targetId`, bei Kommentar-Ereignissen = Record des Kommentars. Filter `recordId` in `GET /activity` und `activity_recent` (findet Record- und Kommentar-Ereignisse; `targetId` bleibt wie bisher). Backfill für Bestandsereignisse in der Migration (Record-Ereignisse aus `target_id`, Kommentar-Ereignisse aus `diff->>'recordId'`).
4. **Schema-Hooks bei `relation.multiple` false→true** (AP-022-Bericht): `ViewSchemaHook` (Filter auf die Relation, z. B. `eq` mit skalarem Wert → passender Array-Operator oder Bereinigung nach bestehendem Muster) und `TemplatePresetSchemaHook` (skalarer Preset-Wert → Array) wie bei select→multi_select.
5. **Konsistenztest Suche** (AP-026-Review): Test, dass die Präfix-Defaults im Migrations-SQL bzw. in der Datenbankfunktion (`p_properties_length`, Body-Länge) den Konstanten in `RecordSearch` entsprechen (über `pg_get_function_arguments` o. Ä.).

**Ausdrücklich nicht drin:**
- Frontend-Code, Mention-Dedupe über Löschungen hinweg, MCP-Ressourcen mit Surrogaten, Hintergrund-Reindex.

## Akzeptanzkriterien

1. `record_create` und `comment_add` (REST und MCP): gleicher Key + gleicher Inhalt → Replay; gleicher Key + anderer Inhalt → 409 `idempotency_key_reused`; Bestandsschlüssel ohne Hash → Replay.
2. Erwähnungen in Blockquote-Codeblöcken und eingerücktem Code im Zitat erzeugen keine Mention; normale Erwähnung im Zitat (`> [@a](actor:id)`) zählt.
3. `GET /activity?recordId=…` und `activity_recent(recordId)` liefern Record- und Kommentar-Ereignisse des Records, vorwärts (`afterSeq`) und rückwärts; Isolation (fremder Record → leer bzw. 404 nach bestehendem Muster).
4. Migration füllt `related_record_id` für Bestandsereignisse (Test auf Bestandsdaten).
5. View-Filter und Template-Presets auf eine Relation bleiben nach `multiple` false→true gültig (Test).
6. Konsistenztest Such-Präfixe grün.
7. `dotnet build`, `dotnet test` grün; Frontend `npm run build` grün.

## Entscheidungen, die der Implementierer treffen darf

- Normalisierung für den Hash (Reihenfolge von Properties, Whitespace), solange gleich gemeinte Anfragen gleich hashen und dokumentiert ist, was zählt.
- Ob `recordId` und `targetId` zusammen erlaubt sind.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-08 | angelegt |
| 2026-11-08 | an implementer-opus übergeben (parallel zu AP-034) |
| 2026-11-08 | Bericht: 1349 Tests grün, Migration `AddRequestHashAndRelatedRecord`; `IdempotencyFingerprint` (SHA-256, gerahmt, Schlüsselreihenfolge egal, Skalare roh), `BlockScanner` mit Zitaten im Mention-Parser, `related_record_id` mit Backfill und Filter `recordId`, Template-Preset-Hook; ViewSchemaHook ohne Codeänderung (Relationen haben dieselben Operatoren) |
| 2026-11-08 | Review gestartet |
| 2026-11-08 | Review: **Blockiert** – B1 Stack Overflow im Mention-Parser bei ~3 KB verschachtelter `>` (Prozess stirbt, auch beim Bearbeiten von Bestandskommentaren); wichtig: Property-Strings mit unterschiedlichen Escapes hashen verschieden (falsches 409 zwischen REST und MCP); Hinweise: Endianness, Wettlauf-Test, Key-Länge, Migration sperrt `activity_events` für ihre Dauer |
| 2026-11-08 | Nacharbeit an implementer-opus (B1, W1, H1–H3, H5) |
| 2026-11-08 | Nacharbeit: Parser iterativ mit Container-Stapel und Offsets, Tiefe 32 (64 KiB `>` in 3 ms statt Absturz), zwei weitere quadratische Fälle behoben; Hash über Inhalt (GetString, kanonische Zahlen, Tags, UTF-16LE, gepinnter Test); Wettlauf-Tests; 1359/1359 |
| 2026-11-08 | Nachprüfung: Freigabe mit Hinweisen (linear auch bei Tiefe 32, Hash kulturunabhängig); Orchestrator: Rundungssatz im XML-Kommentar, Zeitgrenzen der Parser-Tests auf 500/1000 ms |
| 2026-11-08 | abgeschlossen |
