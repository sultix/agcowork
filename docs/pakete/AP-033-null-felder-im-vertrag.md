# AP-033 – API-Vertrag: `null`-Felder mitsenden statt weglassen

| Feld | Wert |
|---|---|
| Status | angelegt |
| Phase | 2 |
| Implementierer | implementer-opus |
| Referenzbeispiel | `src/backend/Agcowork.Api/Program.cs:38` (`DefaultIgnoreCondition = WhenWritingNull`), `src/backend/Agcowork.Api/Realtime/RealtimeSetup.cs:26` |
| Abhängig von | AP-029 bis AP-032 (committet) |
| Betroffene Pfade | `src/backend/Agcowork.Api/` (JSON-Optionen HTTP und SignalR, ggf. MCP-Structured-Content), `tests/` (Tests, die das Fehlen von Feldern prüfen, und neuer Vertragstest). **Nicht:** `JsonColumns.cs` (Persistenz), `ViewSupport.DiffOptions`, `ProblemDetailsMapping` (`allowed` bleibt optional) |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §4.1, §4.3 (OpenAPI ist der Vertrag) |

## Ziel

Das JSON auf dem Draht entspricht dem OpenAPI-Schema: Felder, die dort als `required` und nullable stehen, kommen als `"feld": null` an statt zu fehlen. Damit funktionieren Frontend-Prüfungen wie `readAt === null` und `parentCommentId === null`, die heute an `undefined` scheitern.

## Fachlicher Kontext

- Gefunden im Rauchtest nach AP-032 (2026-11-08): Der Posteingang hält ungelesene Erwähnungen für gelesen (`readAt` fehlt → `undefined !== null`), ein Klick markiert nichts. Dasselbe Muster in AP-031: `parentCommentId === null` (Wurzel) ist bei fehlendem Feld falsch → Erledigen-Knopf fehlt an Wurzeln, `remove()` schließt Antwort-Entwürfe nicht. Unit-Tests sahen es nicht, weil Fixtures `null` setzen.
- Backlog-Hinweis aus AP-026: „OpenAPI führt nullable Felder als required, der Serializer lässt `null` weg.“
- Das Frontend wertet nirgends das Fehlen eines Felds aus (geprüft: Vergleiche behandeln `null` und `undefined`, wo nötig).

## Scope

**Drin:**
- HTTP-JSON (`ConfigureHttpJsonOptions`) und SignalR-Payload: `DefaultIgnoreCondition` auf `Never`. Felder, die bewusst optional bleiben sollen (z. B. `allowed` in Feldfehlern), behalten ihr eigenes `[JsonIgnore(WhenWritingNull)]`; im OpenAPI-Schema dürfen sie dann nicht `required` sein.
- MCP-Structured-Content: dieselbe Regel, falls er eigene Optionen nutzt (Implementierer prüft; Agenten profitieren von expliziten `null`).
- Tests anpassen, die das Fehlen eines Felds erwarten (z. B. `targetTitle`, `nextCursor`).
- Neuer Vertragstest: für eine Stichprobe von Endpunkten mit nullable Feldern (`GET /mentions` → `readAt`, `GET /records/{id}/comments` → `parentCommentId`, `resolvedAt`, `GET /activity` → `targetTitle` bei Member-Ereignis, `GET /search` → `nextCursor` auf der letzten Seite, `GET /me` → `apiKey`) ist das Feld mit `null` vorhanden. Zusätzlich ein generischer Test: Jedes Property, das OpenAPI als `required` führt, ist in einer Beispielantwort dieser Endpunkte vorhanden.
- `openapi/v1.json` unverändert, außer wo bewusst optionale Felder nun korrekt als nicht-required erscheinen.

**Ausdrücklich nicht drin:**
- Frontend-Code (nach dem Fix sind die Fixtures wieder realistisch), Persistenz-JSON, Activity-Diff-Inhalte.

## Akzeptanzkriterien

1. `GET /mentions` liefert `"readAt": null` für ungelesene Erwähnungen; `GET /records/{id}/comments` liefert `"parentCommentId": null` an Wurzeln.
2. Hub-Ereignis `activity` enthält `"targetTitle": null` bei Member-Ereignissen.
3. Vertragstest aus dem Scope grün.
4. `dotnet build`, `dotnet test` grün; Frontend `npm run build` und `npm test` grün (Fixtures unverändert).
5. Orchestrator-Rauchtest: Klick im Posteingang markiert gelesen, Badge sinkt auf 0; Wurzel-Kommentar zeigt den Erledigen-Knopf.

## Entscheidungen, die der Implementierer treffen darf

- Ob einzelne Felder bewusst optional bleiben (dann im Schema nicht `required`).

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-08 | angelegt nach Fund im Rauchtest |
