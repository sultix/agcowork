# AP-028 – Backend-Kleinpaket: Dispatcher „lückenlos oder getrennt“, ungepaarte Surrogate in Record-Texten

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | 2 |
| Implementierer | implementer-opus |
| Referenzbeispiel | `src/backend/Agcowork.Infrastructure/Realtime/ActivityDispatcher.cs`, `src/backend/Agcowork.Application/Realtime/PublishActivity.cs`, `src/backend/Agcowork.Api/Realtime/RealtimeConnections.cs` (Trennen bei Überlauf/Timeout); Eingabeprüfung `QueryText.IsStorable` (Suche, AP-026) |
| Abhängig von | AP-024, AP-025 (committet) |
| Betroffene Pfade | `src/backend/Agcowork.Infrastructure/Realtime/`, `src/backend/Agcowork.Application/Realtime/`, `src/backend/Agcowork.Api/Realtime/`, Record-Validierung in `src/backend/Agcowork.Application/Records/` und Property-Validierung in `src/backend/Agcowork.Domain/Collections/Schema/` bzw. dort, wo Text-Properties geprüft werden, `tests/` (eigene neue Testdateien bevorzugt). **Nicht:** `Search/`, Migrationen, `openapi/v1.json` außer Beschreibungstexten |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §5 „Outbox-Pattern“, §4.1 „Fehler sind Hinweise“ |

## Ziel

Die Realtime-Zustellung hält ihre Invariante „lückenlos oder getrennt“ auch bei Fehlern im Dispatcher; ein Client erfährt durch die Trennung, dass er nachholen muss. Ungültige UTF-16-Texte in Records führen zu 400 statt 500.

## Scope

**Drin:**

*1. Dispatcher-Fehlerpfad (AP-025-Review)*
- Wirft die Zustellung eines Batches für einen Workspace (z. B. DB-Fehler in der Zugriffsprüfung `FindRevokedAsync` oder in `ActivityTitles`), werden **alle Verbindungen dieses Workspaces getrennt**, bevor der Cursor über den Batch hinweggeht. Andere Workspaces sind nicht betroffen.
- Der Kommentar „clients reload after reconnecting“ wird damit wahr; Kommentar entsprechend präzisieren.
- Ebenso: Schlägt das Ablegen in einen Postausgang fehl (außer bei der vorhandenen Überlauf-Behandlung), wird diese Verbindung getrennt.

*2. Ungepaarte Surrogate*
- Titel, Body und Text-Properties (`text`, `markdown`, `url`, `email`, Optionen-Labels/-Values im Schema, Namen/Beschreibungen von Spaces, Collections, Views) mit ungepaarten Surrogaten oder NUL ergeben 400 mit Feldfehler `invalid_value` und Hinweis im `message`, statt `DbUpdateException`/`EncoderFallbackException` → 500. Eine gemeinsame Hilfsfunktion (Muster `QueryText.IsStorable`) statt verstreuter Prüfungen; falls sie heute in `Search/` liegt, in `Common/` verschieben **nur, wenn** AP-026 bereits committet ist, sonst duplizieren und im Bericht vermerken.
- Gilt für REST und MCP gleichermaßen (Use-Case-Ebene). `append` eingeschlossen.

**Ausdrücklich nicht drin:**
- Suche, Migrationen, Frontend, Backplane, Wiederholung fehlgeschlagener Zustellungen.

## Akzeptanzkriterien

1. Wirft die Zustellung für Workspace A, werden alle Verbindungen von A getrennt, B empfängt weiter; der Cursor rückt danach vor (kein Endlos-Retry desselben Batches).
2. Nach der Trennung holt ein Client per `afterSeq` die Ereignisse des fehlgeschlagenen Batches nach (Integrationstest mit Hub-Client oder Application-Test mit Fake-Verbindungen).
3. `POST /records`, `PATCH /records/{id}`, `POST /records/{id}/append` und die MCP-Pendants mit ungepaartem Surrogat oder NUL in Titel, Body oder Text-Property → 400 `invalid_value` am richtigen Feld; dasselbe für Namen von Space/Collection/View und Optionen im Schema.
4. `dotnet build`, `dotnet test` grün.

## Tests, die mindestens entstehen

- Unit/Application: Dispatcher mit Publish, das wirft (Fake), Kriterium 1.
- Integration: Kriterium 2 (soweit mit vertretbarem Aufwand), Kriterium 3 als Theory über die Endpunkte.

## Entscheidungen, die der Implementierer treffen darf

- Ob getrennt oder nur aus der Gruppe genommen und mit einem Hinweis-Ereignis zum Neuladen versorgt wird; Trennen ist der Vorschlag, weil der Client das bereits sauber behandelt.
- Ort der gemeinsamen Textprüfung.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-08 | angelegt |
| 2026-11-08 | an implementer-opus übergeben (parallel zu AP-026-Abschluss und AP-027) |
| 2026-11-08 | Bericht: 1212 Tests grün; `IActivityPublisher` als Naht, Trennen bei jedem Zustellfehler, `StorableText` zentral (Records, Properties, Spaces, Collections, Views, Schema); Erweiterungen: REST-Binder-Fehler mit Pfad → `validation_failed`, MCP-Message-Filter für unlesbare Argumente |
| 2026-11-08 | Review gestartet |
| 2026-11-08 | Review: **Blockiert** – REST-Feldfehler nur in Development (`ThrowOnBadRequest` Default = IsDevelopment, Testfactory setzt Development); wichtig: Reconnect-Sturm bei Dauerfehler (Frontend setzt Backoff nach Start zurück), NUL in Workspace-/Actor-/Key-/Template-Namen und Login weiter 500; Dispatcher-Garantie und MCP-Filter in Ordnung |
| 2026-11-08 | Nacharbeit an implementer-opus (B1, W2 mit allen Stellen, Fehlerzähler, Hinweise) |
| 2026-11-08 | Nacharbeit: `ThrowOnBadRequest` global mit unterdrücktem Error-Log, Production-Test (Gegenprobe rot), Storable für Workspace/Actor/Template/Login, Fehlerzähler pro Workspace; 1220/1220 |
| 2026-11-08 | Nachprüfung: Freigabe mit Hinweisen (keine Nebenwirkungen auf Statuscodes, optionale Bodies, MCP, Hub) |
| 2026-11-08 | abgeschlossen |
