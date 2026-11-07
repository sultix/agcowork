# AP-005 – Tenancy: Workspace, Membership, Tenant-Kontext, Query-Filter, Members-Endpunkte

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | 1 |
| Implementierer | implementer-opus |
| Referenzbeispiel | AP-004: Use-Case-Klassen, `Result`, ProblemDetails-Format, Endpunkt-Gruppen, `AgcoworkApiFactory` mit `CreateAdminClient`/`CreateAgentClient` |
| Abhängig von | AP-004 |
| Betroffene Pfade | `src/Agcowork.Domain/Workspaces/`, `src/Agcowork.Domain/Common/` (ITenantEntity), `src/Agcowork.Application/Workspaces/`, `src/Agcowork.Application/Tenancy/`, `src/Agcowork.Infrastructure/Persistence/` (Filter, Interceptor, Konfigurationen, Migration), `src/Agcowork.Infrastructure/Tenancy/`, `src/Agcowork.Api/` (Middleware, Route-Gruppen, Endpunkte), `tests/` |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §2 (Workspace, Membership), **§2a Mandantentrennung komplett**, §4.3 (Instanz- und Workspace-Ebene), §5 (Tenancy in EF Core, Auth-Pipeline); `01-PLANUNG.md` E7, E5, „Bereits entschieden“ |

## Ziel

Nach diesem Paket gibt es Workspaces als Mandanten mit Mitgliedschaften, einen
Tenant-Kontext, der aus `/api/v1/w/{slug}/…` kommt und die Mitgliedschaft prüft, einen
Global-Query-Filter plus Interceptor in EF Core, der fremde Daten unsichtbar und
unschreibbar macht, und ein Isolations-Testmuster, das alle folgenden Pakete kopieren.

## Scope

**Drin:**

*Domain*
- `Workspace` erweitern: `Slug` (unique, Regex aus AP-001 mit `\z`), `Name`, `Description?`, `Settings` (JSONB, vorerst leer), `CreatedBy` (ActorId), `CreatedAt`, `ArchivedAt?`. Archivierte Workspaces sind read-only (Schreibzugriffe → 409 `workspace_archived`).
- `Membership`: `WorkspaceId`, `ActorId`, `Role` (`owner` | `admin` | `editor` | `viewer`), `Scopes` (abgeleitet aus Rolle, optional weiter eingeschränkt: `read`, `write`, `schema`, `admin`), `CreatedAt`, `CreatedBy`. Unique (`WorkspaceId`, `ActorId`). Rollen → Scopes: viewer = read; editor = read, write; admin = read, write, schema, admin; owner = alles + darf Owner übertragen und Workspace archivieren. Mindestens ein Owner pro Workspace (Invariante).
- `ITenantEntity { Guid WorkspaceId }` in `Domain/Common`. `Membership` implementiert es; `Workspace` selbst nicht (ist die Wurzel).

*Application*
- `ITenantContext { Guid WorkspaceId; string Slug; Membership Membership; IReadOnlySet<string> EffectiveScopes }` und `ITenantContextAccessor`. Effektive Scopes = Scopes der Mitgliedschaft ∩ Key-Scopes (falls der Key Scopes trägt); Key mit `WorkspaceId`-Einschränkung gilt nur in diesem Workspace.
- Use-Cases `Workspaces/`: `ListMyWorkspaces` (Mitgliedschaften des Actors, mit Rolle), `CreateWorkspace` (braucht `workspaces:create` oder `instance:admin`; Anleger wird Owner; `templateId` wird entgegengenommen und vorerst nur `empty` akzeptiert, andere Werte → Validierungsfehler mit `allowed`; die Vorlage „software-projekt“ kommt in AP-010), `GetWorkspace`, `UpdateWorkspace` (Name, Description; admin/owner), `ArchiveWorkspace` (owner), `ListMembers`, `AddMember` (admin/owner; Owner nur durch Owner), `UpdateMember` (Rolle/Scopes; Owner-Invariante), `RemoveMember` (Owner-Invariante).
- Autorisierungshelfer für Folgepakete: `RequireScope("write")` o. ä., das gegen `EffectiveScopes` prüft und `Forbidden` liefert.

*Infrastructure*
- EF: Konfigurationen `workspaces` (erweitert), `memberships`; Migration `AddTenancy`.
- **Global Query Filter** per Konvention für alle `ITenantEntity` auf `WorkspaceId == tenant.WorkspaceId`; außerhalb eines Tenant-Kontexts (Instanz-Endpunkte, Bootstrap) liefert der Filter **nichts** für Tenant-Entitäten, außer der Aufruf ist explizit als `IgnoreQueryFilters` im Use-Case markiert (nur Instanz-Admin-Use-Cases, dokumentiert).
- **SaveChanges-Interceptor**: setzt `WorkspaceId` bei `Added`, wenn leer; wirft, wenn eine `ITenantEntity` mit fremder `WorkspaceId` hinzugefügt, geändert oder gelöscht wird (Defense in Depth, `InvalidOperationException` → 500 und Log, weil es ein Programmierfehler ist).
- Alle Indizes auf Tenant-Tabellen beginnen mit `workspace_id`.

*Api*
- Route-Gruppe `/api/v1/w/{workspaceSlug}` mit Endpunkt-Filter/Middleware: Slug auflösen → Mitgliedschaft des Actors laden → `ITenantContext` setzen; kein Workspace oder keine Mitgliedschaft → **404** ohne Unterscheidung; Key-Einschränkung auf anderen Workspace → 404; archiviert + schreibender Verb → 409.
- Instanz-Endpunkte: `GET /api/v1/workspaces` (eigene Mitgliedschaften), `POST /api/v1/workspaces`.
- Workspace-Endpunkte: `GET /w/{slug}`, `PATCH /w/{slug}`, `POST /w/{slug}/archive`, `GET /w/{slug}/members`, `POST /w/{slug}/members`, `PATCH /w/{slug}/members/{actorId}`, `DELETE /w/{slug}/members/{actorId}`.
- `GET /me` um die Liste der Workspaces mit Rolle ergänzen (oder bei `GET /workspaces` belassen; eine Stelle, im Bericht nennen).

*Tests*
- Unit: Rollen → Scopes, Owner-Invariante, Effektive Scopes mit Key-Einschränkung, Slug-Validierung.
- Integration: Workspace anlegen (als Admin, als Agent mit `workspaces:create`, als Agent ohne → 403), Anleger ist Owner, Mitglieder hinzufügen/ändern/entfernen inkl. Invariante, archivierter Workspace blockt PATCH mit 409.
- **Isolations-Fixture** (`tests/Agcowork.Api.Tests/Tenancy/TenancyFixture.cs` o. ä.): zwei Workspaces `alpha` und `beta`, je ein Agent mit Key und Mitgliedschaft, ein Agent ohne Mitgliedschaft, ein Key mit Einschränkung auf `alpha`. Helfer `AssertIsolated(HttpMethod, path)`-artig, damit Folgepakete jeden neuen Endpunkt mit einer Zeile absichern können.
- Isolationstests für alle Endpunkte dieses Pakets: `GET /w/beta` mit Alpha-Key → 404; Mitgliederliste von `beta` mit Alpha-Key → 404; eingeschränkter Key in `beta` → 404; Nicht-Mitglied → 404.
- Interceptor-Test: Versuch, eine Membership mit fremder `WorkspaceId` zu speichern, wirft.

**Ausdrücklich nicht drin:**
- Spaces, Collections, Records (AP-006/007), Workspace-Vorlage „software-projekt“ (AP-010), Row-Level-Security (Phase 2), MCP (AP-011), UI.

## Fachlicher Kontext

- §2a ist die Vorlage für den Ablauf einer Anfrage; Schritt 5 (RLS) kommt später, Schritte 1–4 und 6 (SignalR entfällt noch) hier.
- Fremde oder unbekannte Workspaces antworten mit 404, nie 403, damit die Existenz nicht verraten wird.
- Actors und Keys kommen aus AP-004; Key-Scopes und Workspace-Einschränkung werden hier erstmals ausgewertet.
- IDs UUIDv7, Zeit über `TimeProvider`, keine Retry-Strategie, Transaktionen direkt (Workspace + Owner-Membership in einer Transaktion).

## Ergänzungen aus AP-004 (Bericht)

- `instance:admin` **impliziert** `workspaces:create` (Entscheidung Orchestrator). `RequireInstanceScope` entsprechend auslegen.
- Testhilfen heißen `CreateAdminClientAsync`, `CreateAgentClientAsync(params scopes)`, `CreateAgentAsync(...)` → `TestAgent(ActorId, ApiKeyId, Key, Client)`.
- `ICurrentActor.KeyScopes` und Claim `key_scope` werden bereits durchgereicht; `api_keys.workspace_id` bekommt hier den Fremdschlüssel.
- `AuthenticateApiKey` lädt Key und Actor getrackt in den scoped DbContext. Prüfen, ob der Tenant-Filter damit kollidiert (Actor ist keine Tenant-Entität, sollte also unkritisch sein).
- `GET /actors` ohne Paginierung; nicht hier lösen.

## Akzeptanzkriterien

1. Ein Actor mit `workspaces:create` legt per `POST /api/v1/workspaces` einen Workspace an und ist danach Owner; `GET /api/v1/workspaces` listet ihn mit Rolle; ein Agent ohne das Recht bekommt 403.
2. Alle `/w/{slug}/…`-Endpunkte liefern für fremde oder unbekannte Slugs 404, für Nicht-Mitglieder 404, für Keys mit Einschränkung auf einen anderen Workspace 404.
3. Rollen wirken: viewer darf `GET`, aber `PATCH /w/{slug}` → 403 mit ProblemDetails; admin darf Mitglieder verwalten, aber keinen Owner anlegen; der letzte Owner kann nicht entfernt oder herabgestuft werden (409 mit Code).
4. Archivierter Workspace: Lesen 200, Schreiben 409 `workspace_archived`.
5. Der Interceptor verhindert Schreiben mit fremder `WorkspaceId` (Test), der Query-Filter blendet fremde Memberships aus (Test über DbContext mit gesetztem Tenant-Kontext).
6. Isolations-Fixture existiert, ist dokumentiert (Kommentar im Kopf) und wird von den eigenen Tests benutzt.
7. `dotnet build` ohne Warnungen, `dotnet test` grün, OpenAPI vollständig.

## Tests, die mindestens entstehen

- `tests/Agcowork.Domain.Tests/Workspaces/MembershipTests.cs`, `WorkspaceTests.cs` (erweitert)
- `tests/Agcowork.Application.Tests/Tenancy/EffectiveScopesTests.cs`
- `tests/Agcowork.Api.Tests/Tenancy/TenancyFixture.cs`, `WorkspacesEndpointsTests.cs`, `MembersEndpointsTests.cs`, `IsolationTests.cs`, `TenantInterceptorTests.cs`

## Entscheidungen, die der Implementierer treffen darf

- Middleware vs. Endpoint-Filter für die Tenant-Auflösung; Caching des Workspace-Lookups (vorerst keins nötig).
- Ob `Scopes` als Array-Spalte oder JSONB gespeichert werden.
- Form der Isolations-Helfer, solange ein neuer Endpunkt mit einer Zeile pro Fall abgesichert werden kann.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
| 2026-11-07 | an implementer-opus übergeben |
| 2026-11-07 | Bericht: 269 Tests grün (84/90/95), 0 Warnungen. Middleware statt Endpoint-Filter (Minimal-API-Binding), benannter Query-Filter `tenant`, Interceptor fest in DbContextOptions, Scopes als text[], `LockWorkspaceAsync` (FOR UPDATE) für Owner-Invariante, `TenancyFixture` mit `AssertIsolatedAsync`. Abweichungen: `ITenantContext.IsArchived`; Interceptor erlaubt Tenant-Entitäten für neu angelegten Workspace im selben SaveChanges; FK-Index `ix_memberships_actor_id` ohne workspace_id-Präfix (dokumentiert); fehlendes `templateId` = `empty` (AP-010 stellt um). Offen: kein Test für die Sperre; ExecuteUpdate/Delete umgehen den Interceptor |
| 2026-11-07 | an reviewer übergeben |
| 2026-11-07 | Review: Freigabe mit Hinweisen; §2a Punkt für Punkt erfüllt, kein Weg zu fremden Daten gefunden. Wichtig: eingeschränkter Key behält Instanz-Scopes (Umgehung der Einschränkung über neuen Key); Middleware wertet jedes POST als Schreibzugriff (bricht Query und MCP in archivierten Workspaces). Hinweise: Sperre ohne Test, Workspace-Wurzel nicht vom Interceptor geschützt, Bulk-Regel fehlt, Migration bricht bei verwaisten api_keys.workspace_id, Fixture ohne Cookie-Fall, Admin kann eigene Einschränkung aufheben |
| 2026-11-07 | Entscheidungen: eingeschränkte Keys ohne Instanz-Scopes; 409 nur aus `RequireWritable`; eigene Mitgliedschaft nur einschränkbar. Nacharbeit an implementer-opus (8 Punkte) |
| 2026-11-07 | Nacharbeit fertig: 280 Tests grün (84/92/104). `ApiKey.IsRestricted` → keine Instanz-Scopes, `/me` zeigt effektive Instanz-Scopes; 409 nur aus `RequireWritable`; Sperre deterministisch getestet (Selbst-Herabstufung zweier Owner); `MemberAuthorization` lädt eigene Mitgliedschaft unter der Sperre neu; Fixture mit Cookie-Mensch. Vom Orchestrator verifiziert. Abgeschlossen, Commit siehe Backlog |
