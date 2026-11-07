# AP-011 – MCP-Server: Workspace- und Instanz-Tools, Resources, Auth, Isolationstests

| Feld | Wert |
|---|---|
| Status | angelegt |
| Phase | 1 |
| Implementierer | implementer-opus |
| Referenzbeispiel | Alle Use-Cases aus AP-004–AP-010 (MCP ist ein dünner Adapter darüber); `TenantResolutionMiddleware` + `TenantEndpointMetadata` (AP-005); `ProblemDetailsMapping` (Fehlerformat); `TenancyFixture` |
| Abhängig von | AP-009, AP-010 |
| Betroffene Pfade | `src/Agcowork.Api/Mcp/` (neu), `src/Agcowork.Api/Program.cs` (Endpunkte `/w/{workspaceSlug}/mcp` und `/mcp`), `Directory.Packages.props` (`ModelContextProtocol`, `ModelContextProtocol.AspNetCore`), `src/Agcowork.Mcp.Stdio/` (optionaler Proxy, siehe Scope), `tests/Agcowork.Api.Tests/Mcp/`, `README.md` (Agent-Anbindung) |
| Architektur-Bezug | `02-ARCHITEKTUR.md` **§4 komplett** (4.1 Grundsätze, 4.2 Tools, Resources, Transport, Beispielkonfiguration, 4.4 Markdown), §2a (MCP-Endpunkt trägt `TenantEndpointMetadata`), §5 Auth-Pipeline; `01-PLANUNG.md` E1, F7, F9 |

## Ziel

Nach diesem Paket kann jeder MCP-fähige Agent (Claude Code, eigene Agenten) agcowork als
MCP-Server nutzen: pro Workspace ein Endpunkt mit den Tools aus Architektur §4.2, dazu ein
Instanz-Endpunkt für Workspace-Verwaltung, Resources für Records und Schemata, Auth per
API-Key, identische Semantik und Fehler wie REST. Damit ist das Kernversprechen des
Produkts („agent-first“) eingelöst.

## Scope

**Drin:**

*Transport und Auth*
- `ModelContextProtocol.AspNetCore` (2.x), Streamable HTTP, **stateless**. Endpunkte: `/w/{workspaceSlug}/mcp` (mit `TenantEndpointMetadata` und Routenwert `workspaceSlug`, damit die Tenancy-Middleware greift) und `/mcp` (Instanz-Ebene). Bearer-API-Key über die bestehende Auth-Pipeline (`ApiKey`-Schema); Cookie-Auth für MCP **nicht** zulassen (nur Agenten).
- Jeder Tool-Aufruf ist ein OpenTelemetry-Span mit Tags `actor_id`, `workspace_id`, `tool` (Architektur §5 Observability).

*Workspace-Tools (`/w/{slug}/mcp`)*, exakt die Liste aus §4.2 Phase 1, als dünne Adapter über die Use-Cases (keine Fachlogik in `Mcp/`):
`workspace_overview`, `collection_describe`, `collection_create`, `collection_update_schema`, `records_query`, `record_get`, `record_create`, `record_update`, `record_append`, `record_archive`, `comment_add` **(entfällt, Phase 2 – nicht anlegen)**, `history_get`, `view_create`, `activity_recent`, plus `records_list` (einfache Liste) und `space_create`/`spaces_list` (klein, nötig für Orientierung). `search` entfällt (Phase 2).
- Tool-Beschreibungen sind das Herzstück: deutsch oder englisch? **Englisch** (LLM-Standard), präzise, mit Hinweisen „call `workspace_overview` first“, „use `expectedRevision` from `record_get`“, „`record_append` is the preferred way to add log-like entries“, Activity: „poll from page 1 with cursor, not with `since`“. Parameter-Schemata aus den Request-Records mit `[Description]`; Enum-Werte als `allowed` in Beschreibungen.
- Ergebnisse: strukturierte JSON-Inhalte (`structuredContent`) **und** ein lesbarer Text-Block; `record_get` liefert standardmäßig Markdown mit Frontmatter (§4.4), optional JSON (`format`).
- Fehler: Use-Case-Fehler werden zu MCP-Tool-Fehlern (`isError: true`) mit demselben Inhalt wie ProblemDetails (`code`, `detail`, `errors[]` mit `allowed`), damit ein Agent sich selbst korrigiert. Keine Exceptions nach außen; 404-Semantik (fremder Workspace) wird auf Transportebene 404.

*Instanz-Tools (`/mcp`)*: `workspaces_list`, `workspace_create` (`templateId`, Standard `software-projekt`), `workspace_add_member`, `templates_list`, `me` (Actor, Instanz-Scopes, Key-Einschränkung).

*Resources*
- `agcowork://record/{id}` (Markdown mit Frontmatter), `agcowork://collection/{id}/schema` (JSON Schema), `agcowork://space/{id}/overview` (Markdown-Übersicht); Listing über `resources/list` beschränkt (z. B. Collections eines Workspaces); Resource-Templates für die URI-Muster.

*Prompts*: keine (Phase 3).

*stdio-Proxy* (`src/Agcowork.Mcp.Stdio/`): kleines .NET-Konsolen-Tool, das stdio ↔ Streamable HTTP proxyt (`--url`, `--key` aus Env `AGCOWORK_API_KEY`). **Nur anlegen, wenn das SDK dafür keine fertige Brücke bietet**; sonst README-Anleitung mit dem SDK-Weg. Entscheidung im Bericht.

*README*: Abschnitt „Agenten anbinden“ mit der Beispielkonfiguration aus §4.2 für Claude Code (`type: http`, `url`, `headers`), Key-Erzeugung über die Admin-UI, Hinweis auf `workspace_overview`.

*Tests* (`tests/Agcowork.Api.Tests/Mcp/`)
- MCP-Client aus dem SDK gegen die `WebApplicationFactory`: `initialize`, `tools/list` (alle Tools mit Beschreibungen), jedes Tool mindestens ein Happy-Path-Aufruf, Fehlerfall mit `errors[]`/`allowed` (z. B. `record_create` mit unbekanntem Property), `record_update` mit veralteter `expectedRevision` → Fehler mit `currentRevision`.
- **Isolation**: jeder Workspace-Tool-Aufruf mit Key ohne Mitgliedschaft, eingeschränktem Key und unbekanntem Slug → 404 auf Transportebene (keine Tool-Antwort); `AssertIsolatedAsync` sinngemäß für MCP erweitern.
- Cookie-Session auf `/mcp` → 401.
- Resources: `resources/read` für Record und Schema; fremde IDs → Fehler ohne Existenzhinweis.
- Ein Ende-zu-Ende-Szenario als Test: Agent ruft `workspace_overview`, `collection_describe` (Entscheidungen), `record_create` (ADR aus Vorlage), `record_append` (Konsequenz), `records_query` (status = akzeptiert), `history_get`.

**Ausdrücklich nicht drin:**
- `search`, `comment_add`, Prompts, Subscriptions/Notifications (Phase 2/3), Sampling, OAuth.
- UI.

## Fachlicher Kontext

- §4.1 Grundsätze gelten wörtlich: Orientierung zuerst, Schema maschinenlesbar, Fehler sind Hinweise, Idempotenz (`Idempotency-Key` als Tool-Parameter `idempotencyKey` bei `record_create`), Actor aus dem Key, Workspace implizit über die URL, wenige Tools.
- Alle Tools rufen dieselben Use-Cases wie REST; wo REST und MCP abweichen, ist das ein Fehler (Test: `tools/list` enthält genau die dokumentierte Liste; ein Vergleichstest ruft `record_create` per REST und per MCP mit gleichem Input und vergleicht die Ergebnisse).
- Tenancy-Middleware und `TenantEndpointMetadata`: Kommentar aus AP-005 beachten; der MCP-Endpunkt liegt außerhalb von `/api/v1`.
- Aspire: Keine neuen Ressourcen nötig.

## Akzeptanzkriterien

1. Claude Code (oder ein SDK-Client) verbindet sich mit `/w/{slug}/mcp` per Bearer-Key, `tools/list` zeigt die Tools mit Beschreibungen, `workspace_overview` liefert Spaces und Collections der Standard-Vorlage mit Beschreibungen.
2. Der Ende-zu-Ende-Test (ADR anlegen, anhängen, abfragen, Historie) läuft grün; `record_get` liefert Markdown mit Frontmatter.
3. Fehler kommen strukturiert (`code`, `errors[]`, `allowed`, `currentRevision`), nie als Exception; REST und MCP liefern für gleichen Input gleiche Ergebnisse.
4. Isolation: kein Tool und keine Resource ist ohne Mitgliedschaft erreichbar; eingeschränkte Keys gelten nur im eigenen Workspace; Cookie-Sessions werden abgelehnt.
5. `/mcp` Instanz-Tools funktionieren mit Instanz-Rechten; `workspace_create` nutzt Standard-Vorlage.
6. README-Abschnitt vorhanden; `dotnet build` ohne Warnungen, `dotnet test` grün; OpenTelemetry-Spans pro Tool-Aufruf (Test über `ActivityListener`).

## Tests, die mindestens entstehen

- `tests/Agcowork.Api.Tests/Mcp/{McpClientFixture,ToolsListTests,WorkspaceToolsTests,InstanceToolsTests,ResourcesTests,McpIsolationTests,RestParityTests,AgentScenarioTests,TelemetryTests}.cs`

## Entscheidungen, die der Implementierer treffen darf

- Attribut-basierte Tools (`[McpServerTool]`) vs. manuelle Registrierung; Aufteilung in Tool-Klassen je Modul.
- Textformat des lesbaren Blocks je Tool; ob `records_query` die Items als Markdown-Tabelle oder als Liste rendert (zusätzlich zum JSON).
- stdio-Proxy ja/nein (siehe Scope).

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
