# AP-004 – Identität: Actors, API-Keys, Login, Auth-Pipeline, Instanz-Endpunkte

| Feld | Wert |
|---|---|
| Status | in Arbeit |
| Phase | 1 |
| Implementierer | implementer-opus |
| Referenzbeispiel | – (erstes Auftreten von Use-Cases, Endpunkten, Fehlerformat) |
| Abhängig von | AP-001 |
| Betroffene Pfade | `src/Agcowork.Domain/Identity/`, `src/Agcowork.Application/Identity/`, `src/Agcowork.Application/Common/` (Result/Fehler), `src/Agcowork.Infrastructure/Identity/`, `src/Agcowork.Infrastructure/Persistence/` (Konfigurationen, Migration), `src/Agcowork.Api/` (Auth, Endpunkte), `tests/` |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §2 (Actor, ApiKey), §2a (Instanz-Ebene), §4.1 (Fehler sind Hinweise, Actor überall), §4.3 (Instanz-Endpunkte), §5 (Auth-Pipeline), §7; `01-PLANUNG.md` E5, „Bereits entschieden“ |

## Ziel

Nach diesem Paket gibt es Actors (Menschen und Agenten) als globale Principals, API-Keys für
Agenten, einen lokalen Login für Menschen und eine Auth-Pipeline, die aus Bearer-Key oder
Cookie einen `ClaimsPrincipal` macht. Ein Instanz-Admin kann Actors und Keys verwalten.
Außerdem legt dieses Paket die Muster fest, die alle folgenden Backend-Pakete übernehmen:
Use-Case-Klassen, Result/Fehler-Typ, Fehlerformat auf dem Draht, Endpunkt-Gruppen, Testaufbau.

## Scope

**Drin:**

*Domain (`Agcowork.Domain/Identity/`)*
- `Actor`: `Id`, `Kind` (Human | Agent), `Name`, `Role` (frei, z. B. „Tech-Lead“, nullable), `Email` (nullable, unique wenn gesetzt, für Menschen), `InstanceScopes` (Menge aus `instance:admin`, `workspaces:create`), `IsActive`, `CreatedAt`. Factory-Methoden `CreateHuman`, `CreateAgent`.
- `LocalLogin`: `ActorId` (1:1 zu Human-Actor), `PasswordHash`, `UpdatedAt`. Getrennt vom Actor, damit Agenten nie ein Passwortfeld tragen.
- `ApiKey`: `Id`, `ActorId` (nur Agent), `Prefix` (8 Zeichen, sichtbar), `SecretHash` (SHA-256 des Geheimnisses), `Scopes` (optionale Einschränkung; leer = Rechte der Mitgliedschaft gelten voll; Scopes selbst werden erst in AP-005 ausgewertet, hier nur gespeichert), `WorkspaceId?` (optionale Einschränkung, nur gespeichert, Auswertung in AP-005), `ExpiresAt?`, `LastUsedAt?`, `RevokedAt?`, `CreatedAt`. Format des Klartext-Keys: `agck_<prefix8>_<secret32>` (Base62 o. ä., kryptografisch zufällig). Der Klartext wird genau einmal bei der Erzeugung zurückgegeben.
- Domain-Invarianten: Agent hat keine E-Mail-Pflicht, Mensch braucht E-Mail; Keys nur für Agenten; deaktivierter Actor → Keys gelten nicht.

*Application*
- `Common/`: `Result<T>` / `Result` mit typisierten Fehlern (`ValidationError` mit Feldliste, `NotFound`, `Forbidden`, `Unauthorized`, `Conflict`). Kein MediatR, keine Pipeline-Bibliothek: eine Klasse pro Use-Case mit `HandleAsync(request, ct)`, per DI registriert (Konvention + Assembly-Scan oder explizit).
- `ICurrentActor` (Id, Kind, InstanceScopes, ApiKeyId?, KeyWorkspaceRestriction?) als Abstraktion über den Principal; Implementierung in Api.
- Use-Cases `Identity/`: `Login`, `GetMe`, `CreateActor`, `UpdateActor` (Name, Role, InstanceScopes, IsActive), `ListActors`, `GetActor`, `CreateApiKey`, `ListApiKeys` (nur Metadaten), `RevokeApiKey`, `AuthenticateApiKey` (Prefix-Lookup, konstantzeitiger Hash-Vergleich, Prüfungen auf revoked/expired/inactive, `LastUsedAt` höchstens einmal pro Minute aktualisieren).
- Autorisierung in den Use-Cases, nicht nur im Endpunkt: Actor-/Key-Verwaltung verlangt `instance:admin`.

*Infrastructure*
- EF-Konfigurationen (`actors`, `local_logins`, `api_keys`), Indizes: `actors.email` unique (filtered, nur non-null), `api_keys.prefix` unique. Migration `AddIdentity`.
- Passwort-Hashing über `Microsoft.AspNetCore.Identity.PasswordHasher<T>` (nur der Hasher, nicht ASP.NET Identity), hinter einem `IPasswordHasher`-Interface der Application.
- `IApiKeyGenerator` (Zufall + Hash) hinter Interface.
- Bootstrap: Beim Start, wenn keine Actors existieren, einen Human-Actor mit `instance:admin` aus Konfiguration `Agcowork:Bootstrap:AdminEmail` / `AdminPassword` anlegen. In `appsettings.Development.json` Dev-Werte eintragen; sonst müssen sie per Umgebung kommen, sonst Warnung im Log und kein Admin. Passwort nie loggen.

*Api*
- Zwei Authentifizierungsschemata: `ApiKey` (Bearer, eigener `AuthenticationHandler`) und `Cookie` (HttpOnly, SameSite=Lax, Secure außer localhost, 7 Tage). Policy-Schema wählt nach `Authorization`-Header.
- Claims: `actor_id`, `actor_kind`, `instance_scope` (mehrfach), `api_key_id`, `key_workspace` (optional). Authorization-Policies `Authenticated`, `InstanceAdmin`, `WorkspacesCreate`.
- Endpunkte (Instanz-Ebene, `/api/v1`), alle mit OpenAPI-Beschreibung:
  - `POST /auth/login` {email, password} → 204 + Cookie; `POST /auth/logout` → 204; nur Menschen.
  - `GET /me` → Actor, InstanceScopes, Kind, ggf. Key-Metadaten (Prefix, Einschränkung).
  - `GET /actors`, `POST /actors`, `GET /actors/{id}`, `PATCH /actors/{id}` (InstanceAdmin).
  - `POST /actors/{id}/keys` → 201 mit Klartext-Key (einmalig) + Metadaten; `GET /actors/{id}/keys` → Metadaten; `DELETE /keys/{id}` → 204 (Widerruf, kein Hard-Delete). Alles InstanceAdmin.
- **Fehlerformat auf dem Draht** (verbindlich für alle folgenden Pakete): `ProblemDetails` (RFC 9457) mit `type`, `title`, `status`, `detail` und bei Validierung zusätzlich `errors: [{ field, code, message, allowed? }]`. `code` ist maschinenlesbar (`required`, `invalid_format`, `not_in_options`, `too_long`, `conflict` …). 401 ohne Details, 403 mit `detail` „requires instance:admin“, 404 ohne Hinweis auf Existenz.
- Rate-Limiting: nur Platzhalter-Kommentar, kommt später.

*Tests*
- Unit (Domain/Application): Key-Format, Hash-Verifikation, abgelaufen/widerrufen/inaktiv, Scope-Prüfung im Use-Case, Result-Mapping.
- Integration (Api, Testcontainers, Muster aus AP-001): Login richtig/falsch, `GET /me` per Cookie und per Key, Key ungültig/widerrufen/abgelaufen → 401, Nicht-Admin erstellt Actor → 403 mit ProblemDetails, Key-Klartext nur in der 201-Antwort und nicht in der DB (nur Hash), `LastUsedAt` wird gesetzt, Bootstrap-Admin existiert nach Start. Fehlerformat per Test festgenagelt (JSON-Struktur).
- Testhilfen für Folgepakete: `AgcoworkApiFactory` erweitern um `CreateAdminClient()` (Cookie) und `CreateAgentClient(scopes…)` (erzeugt Agent + Key und gibt `HttpClient` mit Bearer zurück).

**Ausdrücklich nicht drin:**
- Workspaces, Memberships, Tenant-Kontext, Workspace-Endpunkte, Auswertung von Key-Scopes und Workspace-Einschränkung (AP-005).
- OIDC, Passwort-Reset, E-Mail-Versand, Rate-Limiting, MCP, UI.

## Fachlicher Kontext

- Actors sind global pro Installation; Rechte pro Workspace kommen in AP-005 über Memberships. Instanz-Rechte hängen am Actor (`01-PLANUNG.md`, „Bereits entschieden“).
- Agenten geben sich nie selbst aus: Der Actor kommt ausschließlich aus dem Key.
- Fehler müssen so strukturiert sein, dass ein Agent sich selbst korrigieren kann (§4.1).
- Persistenz-Muster aus AP-001 (Architektur §5, „Persistenz-Muster“) übernehmen; keine Retry-Strategie, Transaktionen direkt.
- IDs UUIDv7, Zeit über `TimeProvider`.

## Akzeptanzkriterien

1. Nach Start mit Dev-Konfiguration kann sich der Bootstrap-Admin per `POST /auth/login` anmelden und `GET /me` liefert `instance:admin`.
2. Ein Admin kann einen Agent-Actor anlegen und einen Key erzeugen; die 201-Antwort enthält den Klartext-Key genau einmal, in der DB steht nur der Hash, `GET /actors/{id}/keys` zeigt Prefix und Metadaten ohne Geheimnis.
3. `GET /me` mit diesem Key liefert den Agent-Actor; nach `DELETE /keys/{id}` liefert derselbe Key 401; ein Key mit `ExpiresAt` in der Vergangenheit liefert 401; ein deaktivierter Actor liefert 401.
4. Ein Agent ohne `instance:admin` bekommt bei `POST /actors` 403 als ProblemDetails; ein Mensch mit falschem Passwort bekommt 401 ohne Hinweis, ob die E-Mail existiert.
5. Validierungsfehler (z. B. Actor ohne Namen, Mensch ohne E-Mail, ungültige Instance-Scope) kommen als ProblemDetails mit `errors[]` inkl. `field`, `code`, `message` und bei Enum-Feldern `allowed`.
6. `dotnet build` ohne Warnungen, `dotnet test` grün, OpenAPI-Dokument enthält alle Endpunkte mit Beschreibung.
7. Keine Isolationstests nötig (noch keine workspace-gebundenen Daten), aber die Testhilfen `CreateAdminClient`/`CreateAgentClient` existieren und werden in den eigenen Tests benutzt.

## Tests, die mindestens entstehen

- `tests/Agcowork.Domain.Tests/Identity/ActorTests.cs`, `ApiKeyTests.cs`
- `tests/Agcowork.Application.Tests/` (neu anlegen): `AuthenticateApiKeyTests.cs`, `CreateApiKeyTests.cs`, `ResultTests.cs`
- `tests/Agcowork.Api.Tests/Identity/AuthTests.cs`, `ActorsEndpointsTests.cs`, `ApiKeysEndpointsTests.cs`, `ProblemDetailsFormatTests.cs`

## Entscheidungen, die der Implementierer treffen darf

- Konkrete Form von `Result<T>` (eigener Typ vs. kleines Paket wie `ErrorOr`; eigener Typ bevorzugt, keine große Bibliothek).
- Registrierung der Use-Cases (Scan vs. explizit), Ordnerstruktur innerhalb der Slices.
- Base62 vs. Base64url für Key-Geheimnis; Länge ≥ 32 Zeichen.
- Ob Cookie-Login zusätzlich ein Anti-CSRF-Token braucht (für SameSite=Lax + JSON-API vorerst nein; kurz begründen).
- Wie das Policy-Schema zwischen Cookie und Bearer wählt.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
| 2026-11-07 | an implementer-opus übergeben |
