# AP-010 – Workspace-Vorlage „Software-Projekt“ und `empty`, Anlage aus Vorlage

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | 1 |
| Implementierer | implementer-sonnet |
| Referenzbeispiel | AP-005 `CreateWorkspace` (Transaktion Workspace + Owner-Membership, Interceptor-Ausnahme für neue Workspaces); AP-006 `CreateCollection`, `UpdateSchema`, `Templates/CreateTemplate` (Use-Cases, die die Vorlage aufruft); `IdentityBootstrapper` (Seed-Stil) |
| Abhängig von | AP-006, AP-008 (für Standard-Views; läuft parallel – siehe Kontext) |
| Betroffene Pfade | `src/Agcowork.Application/Workspaces/Templates/` (neu), `src/Agcowork.Application/Workspaces/CreateWorkspace.cs` (Vorlage anwenden), `src/Agcowork.Api/Endpoints/Workspaces/` (`GET /api/v1/workspace-templates`), `tests/`, `openapi/v1.json`, `schema.d.ts` (neu erzeugen). **Keine Migration.** |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §2 „Standard-Vorlage ‚Software-Projekt‘“ und „Sammlungen der Standard-Vorlage“, §4.2 (`workspace_create` mit `templateId`, `templates_list`); `01-PLANUNG.md` „Bereits entschieden“ (Standard-Vorlage), Phase 1 |

## Ziel

Nach diesem Paket startet jeder neue Workspace standardmäßig mit der Vorlage
„Software-Projekt“: drei Spaces, vier Collections mit Schema, Beschreibungen für Agenten
und Record-Vorlagen. `empty` bleibt wählbar. Agenten können die verfügbaren Vorlagen abfragen.

## Scope

**Drin:**

*Application/Workspaces/Templates*
- `IWorkspaceTemplate { Id, Name, Description, ApplyAsync(workspaceId, actorId, ct) }` und Registry `WorkspaceTemplates` (im Code, Phase 1). Zwei Vorlagen: `empty` (tut nichts) und `software-projekt` (Standard).
- `SoftwareProjectTemplate` legt **über die bestehenden Use-Cases** an (damit Hooks wie Standard-Views laufen und Validierung greift), nicht über direkte DbSet-Schreibzugriffe:
  - Spaces: „Produkt“, „Technik“, „Notizen“ (mit Beschreibung, Icon, SortOrder).
  - Collections nach Architektur §2 „Sammlungen der Standard-Vorlage“, mit Beschreibungen, die für Agenten geschrieben sind (wann anlegen, was hinein gehört):
    - **Aufgaben** (Produkt): `status` (status: todo/in_progress/done-Gruppen mit Optionen „Offen“, „In Arbeit“, „Erledigt“, „Verworfen“), `owner` (actor), `priority` (select: niedrig/mittel/hoch/kritisch), `sprint` (select, leer), `related_decision` (relation → Entscheidungen), `due` (date), `estimate` (number, Unit „h“).
    - **Analysen** (Produkt): `kind` (select: Markt/Technik/Wettbewerb/Nutzer), `author` (actor), `sources` (markdown), `confidence` (select: niedrig/mittel/hoch), `summary` (text, required).
    - **Entscheidungen** (Technik): `status` (status: vorgeschlagen/akzeptiert/verworfen/ersetzt mit Gruppen), `decided_by` (actor), `date` (date), `affects` (relation → Komponenten, multiple), `supersedes` (relation → Entscheidungen, Self-Relation: Collection erst ohne dieses Feld anlegen, dann per `UpdateSchema` ergänzen).
    - **Komponenten** (Technik): `kind` (select: Service/Bibliothek/Datenbank/UI/Infrastruktur), `owner` (actor), `repo_url` (url), `status` (status: geplant/aktiv/veraltet).
  - Record-Vorlagen je Collection (Default): Entscheidung mit Body „## Kontext / ## Optionen / ## Entscheidung / ## Konsequenzen“; Aufgabe mit „## Ziel / ## Akzeptanzkriterien“; Analyse mit „## Fragestellung / ## Befunde / ## Empfehlung“; Komponente mit „## Zweck / ## Schnittstellen“.
  - Reihenfolge wegen Relationen: Komponenten → Entscheidungen (ohne `supersedes`) → `supersedes` ergänzen → Aufgaben → Analysen.
- `CreateWorkspace`: `templateId` **Standard `software-projekt`** (Verhaltensänderung gegenüber AP-005, wo fehlend = `empty` war), `empty` erlaubt, unbekannt → 400 mit `allowed`. Vorlage wird in derselben Transaktion wie Workspace + Owner-Membership angewendet (bei Fehler nichts angelegt). Tenant-Kontext: Die Use-Cases der Vorlage brauchen `ITenantContext`; den für den frisch angelegten Workspace innerhalb der Transaktion setzen (Muster: `ITenantContextAccessor.SetTenant`, einmal pro Scope → ggf. eigener Scope oder ein `IWorkspaceTemplateRunner` in Infrastructure, der einen Scope mit Tenant öffnet; Entscheidung im Bericht).
- `ListWorkspaceTemplates` (Instanz-Ebene, jeder Authentifizierte): Id, Name, Beschreibung, `isDefault`.

*Api*
- `GET /api/v1/workspace-templates`. `POST /api/v1/workspaces` dokumentiert `templateId` mit `allowed`.

*Tests*
- Application (Testcontainers): `software-projekt` erzeugt 3 Spaces, 4 Collections mit erwarteten Keys/Typen/Options, `supersedes` zeigt auf die eigene Collection, `affects` auf Komponenten, je Collection eine Default-Record-Vorlage; wenn AP-008 vorhanden ist: je Collection `table` + `raw` System-Views (Test mit `Skip`-Hinweis, falls Views noch fehlen; im Bericht nennen); `empty` erzeugt nichts; Fehler in der Vorlage (simuliert) rollt Workspace zurück.
- Api: `POST /workspaces` ohne `templateId` → Standard angewendet; `templateId: "empty"` → leer; unbekannt → 400 `allowed`; `GET /workspace-templates` listet beide; Isolation: Vorlage eines Workspaces landet nicht in einem anderen (zwei Workspaces nacheinander anlegen, Collections getrennt).

**Ausdrücklich nicht drin:**
- Vorlagen als Daten (bestehender Workspace als Vorlage, Phase 3), UI für Vorlagenwahl (AP-016 zeigt `allowed`), Records in der Vorlage (keine Beispiel-Records).

## Fachlicher Kontext

- AP-008 (Views) läuft parallel. Standard-Views entstehen über `ICollectionCreatedHook`, sobald AP-008 committet ist; dieses Paket muss nichts dafür tun, außer Collections über `CreateCollection` anzulegen.
- Self-Relation `supersedes`: AP-006-Bericht nennt den Zwei-Schritt-Weg.
- Beschreibungen sind der wichtigste Teil dieser Vorlage: Ein Agent liest `workspace_overview`/`describe` und muss daraus wissen, wann er welche Collection nutzt. Deutsche Texte, je 2–4 Sätze, konkret („Lege hier jede Architektur- oder Produktentscheidung ab, bevor du sie umsetzt. Verweise unter `affects` auf betroffene Komponenten.“).
- Keine Migration in diesem Paket.

## Akzeptanzkriterien

1. `POST /workspaces` ohne `templateId` liefert einen Workspace mit 3 Spaces und 4 Collections samt Schema, Beschreibungen und Default-Record-Vorlagen; `describe` jeder Collection zeigt gültiges JSON Schema.
2. `templateId: "empty"` erzeugt einen leeren Workspace; unbekannte Vorlage → 400 mit `allowed`.
3. `GET /workspace-templates` listet `software-projekt` (default) und `empty`.
4. Fehler beim Anwenden der Vorlage rollt alles zurück (kein halber Workspace).
5. Zwei Workspaces nacheinander: Collections strikt getrennt (Isolation).
6. `dotnet build` ohne Warnungen, `dotnet test` grün, `openapi/v1.json`/`schema.d.ts` neu erzeugt.

## Tests, die mindestens entstehen

- `tests/Agcowork.Application.Tests/Workspaces/Templates/SoftwareProjectTemplateTests.cs`
- `tests/Agcowork.Api.Tests/Tenancy/WorkspaceTemplatesEndpointsTests.cs`

## Entscheidungen, die der Implementierer treffen darf

- Wie der Tenant-Kontext für die Vorlage gesetzt wird (eigener DI-Scope vs. Runner), solange alles in einer Transaktion bleibt.
- Exakte Option-Labels/Farben aus der Palette; Icons der Spaces.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
| 2026-11-07 | an implementer-sonnet übergeben |
| 2026-11-07 | Bericht: 696 Tests grün. Statische Registry `WorkspaceTemplates.All`; Vorlage läuft über Use-Cases im selben Scope (`SetTenant` nach Owner-Membership, eine Transaktion); `BeginOrJoinTransactionAsync` (`JoinableTransaction`) in UpdateSchema/CreateTemplate, damit sie die äußere Transaktion mitnutzen; Option-Werte englisch, Labels deutsch; Standard-Views-Test läuft real (AP-008 schon im Baum). Offen: Tenant bleibt nach CreateWorkspace im Scope gesetzt (MCP `workspace_create` braucht Reset) |
| 2026-11-07 | an reviewer übergeben |
| 2026-11-07 | Review: Freigabe mit Hinweisen; Tenancy und Transaktion korrekt, Inhalt vollständig. Wichtig: Beschreibungen zitieren Labels statt Option-Werte (`'ersetzt'` vs `superseded`). Hinweise: Vorlagenfehler erscheinen als 400 des Aufrufers; Result-Failure-Rollback ungetestet; Join-Semantik nur dokumentiert; `sprint`-Hinweis ohne Scope; Vorlage läuft mit vollen Owner-Scopes (bewusst); `ApplyAsync(IServiceProvider…)` als Service-Locator (vertretbar); Zusatz-Views aus §2 fehlen |
| 2026-11-07 | Entscheidungen: `ApplyAsync(IServiceProvider …)` akzeptiert; Vorlage läuft mit vollen Owner-Scopes (Architektur §2a). Nacharbeit an implementer-sonnet: Werte statt Labels, sprint-Hinweis, Vorlagenfehler → 500, Rollback-Test, Join-Kommentar, Zusatz-Views (Board, Galerie, Sortierung) |
| 2026-11-07 | Nacharbeit fertig: Werte statt Labels + Regex-Test, sprint-Hinweis, Vorlagenfehler → InvalidOperationException mit Rollback-Test, Join-Kommentar, Zusatz-Views (Aufgaben Board, Analysen Galerie, Entscheidungen nach date desc). AP-010-Tests grün; 3 rote Tests gehören zur laufenden AP-008-Nacharbeit. Wartet auf AP-008 für gemeinsamen Verifikationslauf |
| 2026-11-07 | Verifikationslauf mit AP-008: 726 Tests grün. Abgeschlossen, Commit `7d1faa5` |
