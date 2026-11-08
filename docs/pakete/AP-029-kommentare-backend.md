# AP-029 – Kommentare und Erwähnungen Backend: Domäne, Use-Cases, REST, MCP, Activity

| Feld | Wert |
|---|---|
| Status | angelegt |
| Phase | 2 |
| Implementierer | implementer-opus |
| Referenzbeispiel | – (neues Modul); Muster: Records (`src/backend/Agcowork.Application/Records/`: Sperren, Activity, Idempotenz), Views (`Views/`, CRUD-Endpunkte), Activity-Titel (`Activity/ListActivity.cs`, gebündelte Auflösung), `StorableText`/`FieldErrors.Storable` (AP-028), Keyset-Paginierung (`Query/`), MCP-Tools (`Api/Mcp/Tools/`) |
| Abhängig von | AP-028 (committet) |
| Betroffene Pfade | neu `src/backend/Agcowork.Domain/Comments/`, `src/backend/Agcowork.Application/Comments/`, `src/backend/Agcowork.Api/Endpoints/Comments/`, `src/backend/Agcowork.Api/Mcp/Tools/CommentTools.cs`; geändert `Domain/Activity/ActivityEvent.cs` (Typen, Target `comment`), `Application/Activity/` (Titel für `comment`), `Application/Records/` (nur `includeComments` in `GetRecord`/`record_get`), `Infrastructure/Persistence/` (Konfigurationen, **eine** Migration), `Api/Endpoints/ApiV1.cs`, `Api/Mcp/McpSetup.cs`, `McpText.cs`, `McpDescriptions.cs`, `openapi/v1.json`, `tests/`; im Frontend nur das neu erzeugte `schema.d.ts` |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §2 „Kommentare und Erwähnungen“ (verbindlich), §2a, §4.1, §4.2, §4.3, §5 „Outbox-Pattern“ |

## Ziel

Agenten und Menschen diskutieren an Records in Threads und erwähnen einander. Jeder Actor findet per REST oder MCP heraus, wo er erwähnt wurde („Wo wurde ich gefragt?“), und alle Kommentar-Ereignisse laufen über Activity und damit live in die UI.

## Scope

**Drin:**

*1. Domäne*
- `Comment` (ITenantEntity): `Id`, `WorkspaceId`, `RecordId`, `AuthorId`, `ParentCommentId?`, `BodyMarkdown` (max. 64 KiB), `CreatedAt`, `EditedAt?`, `ResolvedAt?`, `ResolvedBy?`, `DeletedAt?`. Invarianten: Antwort nur auf Wurzel, Wurzel und Antwort am selben Record; nur Wurzeln erledigbar; gelöschte Kommentare haben leeren Body und sind nicht bearbeitbar.
- `Mention` (ITenantEntity): `Id`, `WorkspaceId`, `CommentId`, `RecordId`, `MentionedActorId`, `ByActorId`, `CreatedAt`, `ReadAt?`. Eindeutig je (`CommentId`, `MentionedActorId`).
- Zusammengesetzte Fremdschlüssel über `workspace_id` (Comment → Record, Mention → Comment), Indizes beginnen mit `workspace_id` (Katalogtest aus AP-026 muss grün bleiben): u. a. (`workspace_id`, `record_id`, `created_at`), (`workspace_id`, `mentioned_actor_id`, `read_at`, `created_at`).

*2. Erwähnungen*
- Parser für `[@Anzeigename](actor:<uuid>)` im Markdown (auch mehrfach, Duplikate zählen einmal; Links in Code-Spans/-Blöcken zählen **nicht**). Nur Mitglieder des Workspaces; sonst 400 `validation_failed` mit `code: "not_member"` am Feld `markdown` und der ID in `message`. Selbst-Erwähnung erzeugt keine Mention-Zeile.
- Bearbeiten gleicht ab: neue anlegen, entfernte löschen, unveränderte behalten (inkl. `ReadAt`). Löschen des Kommentars löscht seine Mentions.

*3. Use-Cases (Application/Comments)*
- `AddComment(recordId, markdown, parentCommentId?)` → `write`; Record darf nicht archiviert sein (409 wie bei Record-Schreibzugriffen); Sperr-Reihenfolge wie bei Records (Record vor Kommentar), keine Revision am Record. `Idempotency-Key` wie bei `CreateRecord` (REST-Header; MCP optional `idempotencyKey`, falls im Projekt so üblich, sonst nur REST).
- `UpdateComment(commentId, markdown)` → nur Autor (sonst 403 `not_author`); `EditedAt`.
- `DeleteComment(commentId)` → Autor oder `admin` (sonst 403); weich.
- `ResolveComment` / `ReopenComment` → `write`, nur Wurzeln (Antwort → 400 `not_root`).
- `ListComments(recordId, includeResolved = true)` → `read`; Threads: Wurzeln chronologisch, darunter Antworten chronologisch; Autor-Name und -Art über Members aufgelöst (gebündelt, kein N+1). Gelöschte Kommentare bleiben als Platzhalter, wenn sie Antworten haben, sonst ausgelassen.
- `ListMentions(unreadOnly = false, limit, cursor)` → eigene Erwähnungen des Aufrufers (Actor aus Authentifizierung), neueste zuerst, Keyset-Cursor; je Eintrag Record-Titel, Kommentar-Auszug (erste 200 Zeichen, reiner Text), `byActorId`, `readAt`. Die Antwort enthält zusätzlich `unreadCount` (alle ungelesenen eigenen Erwähnungen im Workspace, unabhängig von Filter und Seite) für das Badge in AP-032.
- `MarkMentionsRead(ids? , all?)` → nur eigene; fremde IDs werden ignoriert (kein Existenz-Leak), Antwort nennt die Anzahl.
- Textprüfung über `FieldErrors.Storable`.

*4. Activity und Realtime*
- Typen `comment.created|updated|deleted|resolved|reopened`, Target `comment`; Diff mit `recordId`, `parentCommentId`, `mentionedActorIds` (bei updated: hinzugekommene). `targetTitle` für `comment` = Titel des Records (gebündelt aufgelöst wie in AP-022). Space/Collection des Records im Event setzen, damit Filter greifen.

*5. Adapter*
- REST: `GET|POST /api/v1/w/{slug}/records/{id}/comments`, `PATCH|DELETE /comments/{id}`, `POST /comments/{id}/resolve`, `POST /comments/{id}/reopen`, `GET /mentions?unreadOnly&limit&cursor`, `POST /mentions/read` (`{ ids?, all? }`).
- MCP: `comment_add`, `comments_list`, `comment_update` (markdown und/oder `resolved: true|false`; löschen über `deleted: true` oder eigenes Tool – Implementierer entscheidet, Toolanzahl klein halten), `mentions_list`, `mentions_mark_read`. Beschreibungen für Agenten: Syntax der Erwähnung mit Beispiel, woher die Actor-IDs kommen, dass Erwähnte über `mentions_list` benachrichtigt werden, dass Kommentare keine Record-Revision erzeugen.
- `record_get(includeComments: true)` bzw. `GET /records/{id}?includeComments=true` liefert die Threads mit.
- Server-Instructions/`workspace_overview`: ein Satz, dass `mentions_list` die offene Fragen an den Agenten zeigt.

**Ausdrücklich nicht drin:**
- Frontend (AP-031, AP-032), Erwähnungen im Record-Body, Reaktionen/Emojis, Kommentar-Revisionen, E-Mail/Webhook-Benachrichtigungen, Suche in Kommentaren, eigener `comment`-Scope.

## Fachlicher Kontext

- Entscheidungen stehen verbindlich in `02-ARCHITEKTUR.md` §2 „Kommentare und Erwähnungen“. Abweichungen nur mit Begründung im Bericht.
- Mitglieder können entfernt werden: Kommentare und Mentions bleiben, der Autor wird weiter mit Namen angezeigt (Actor ist global). Ein entferntes Mitglied sieht seine Mentions nicht mehr (Tenant-Kontext verlangt Mitgliedschaft).
- Archivierte Records: lesen ja, kommentieren/erledigen nein (409).

## Akzeptanzkriterien

1. Kommentar anlegen, beantworten, erledigen, wieder öffnen, bearbeiten (nur Autor), löschen (Autor oder admin) über REST und MCP; Antwort auf Antwort → 400; Erledigen einer Antwort → 400 `not_root`.
2. `ListComments` liefert Threads in der beschriebenen Ordnung inkl. Autor-Infos; gelöschter Kommentar mit Antworten erscheint als Platzhalter ohne Body.
3. Erwähnung eines Mitglieds erzeugt genau eine Mention-Zeile; Nicht-Mitglied → 400 `not_member`; Erwähnung in Code-Span zählt nicht; Selbst-Erwähnung erzeugt keine Zeile.
4. Bearbeiten gleicht Mentions ab (neu, entfernt, unverändert mit erhaltenem `ReadAt`); Löschen entfernt sie.
5. `GET /mentions` und `mentions_list` zeigen nur eigene Erwähnungen, `unreadOnly` filtert, Paginierung ohne Duplikate; `mentions/read` mit fremder ID ändert nichts und verrät nichts.
6. Activity-Ereignisse für alle Kommentar-Aktionen mit Diff und `targetTitle`; ein Hub-Client empfängt `comment.created` (Integration wie AP-024).
7. `record_get(includeComments)` liefert Threads.
8. Archivierter Record: Kommentieren 409, Lesen 200.
9. NUL/Surrogat im Markdown → 400 `invalid_value`.
10. Isolation: fremde Record-/Comment-/Mention-IDs → 404 (REST `AssertIsolatedAsync`, MCP-Isolationstest); Mentions anderer Workspaces erscheinen nie.
11. `dotnet build`, `dotnet test` grün; Frontend `npm run build` grün (nur `schema.d.ts`).

## Tests, die mindestens entstehen

- Unit: Mention-Parser (Mehrfach, Duplikate, Code-Span/-Block, kaputte Links, Groß-/Kleinschreibung der UUID), Domänen-Invarianten.
- Integration: Kriterien 1–9 (REST und MCP), Hub-Empfang.
- Isolation: alle neuen Endpunkte und Tools.

## Entscheidungen, die der Implementierer treffen darf

- Toolzuschnitt für Löschen (eigenes Tool oder Feld in `comment_update`), solange höchstens fünf neue Tools entstehen.
- Ob `ListComments` paginiert (bei > 500 Kommentaren je Record) oder begrenzt mit Hinweis.
- Idempotenz für MCP `comment_add`, sofern im Projekt etabliert.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-08 | angelegt |
