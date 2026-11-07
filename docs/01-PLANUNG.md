# agcowork – Planung

> Agent-first Wissens- und Arbeitsraum (Notion-Ersatz). Stand: 2026-11-07.
> Begleitdokumente: [02-ARCHITEKTUR.md](02-ARCHITEKTUR.md), [03-WORKFLOW.md](03-WORKFLOW.md)

## 1. Vision

agcowork ist der gemeinsame Arbeitsraum für ein Team aus KI-Agenten und Menschen.
Agenten (PO, Tech-Lead, Entwickler, Marketing-Analyst, …) schreiben dort ihre
Aufgaben, Entscheidungen, Analysen und Notizen hin und lesen sie wieder nach.
Menschen schauen über eine Web-Oberfläche zu, prüfen, korrigieren und steuern.

Leitgedanke: **Agenten zuerst, Menschen gleichwertig.**

- Die Schreib-/Lese-Schnittstelle für Agenten ist das Fundament (MCP + REST).
- Die Web-UI ist eine *Sicht* auf dieselben Daten, nicht das Datenmodell selbst.
- Alles ist nachvollziehbar: wer (welcher Agent, welcher Mensch) hat was wann warum geändert.
- Eine Installation beherbergt **mehrere Workspaces** (Mandanten), strikt voneinander getrennt.
  Ein Workspace ist z. B. ein Kundenprojekt, ein Produkt oder ein Team mit eigenem Agenten-Set.

## 2. Akteure

| Akteur | Beispiele | Zugriff |
|---|---|---|
| Agent | PO-Agent, Tech-Lead-Agent, Marketing-Agent, Claude Code, eigene Agenten | MCP (bevorzugt) oder REST, je Agent eigener API-Key und eigene Identität; Zugriff nur auf Workspaces, in denen er Mitglied ist |
| Mensch | Du als Betreiber, später ggf. weitere Personen | Web-UI (Angular), ein Login, Mitglied in einem oder mehreren Workspaces |
| Instanz-Admin | Du; außerdem Agenten mit dem Instanz-Recht `workspaces:create` | Legt Workspaces an, verwaltet Actors und Mitgliedschaften |
| System | Scheduler, Import/Export, Webhooks | intern |

Jeder Agent ist ein **eigener Principal** mit Name, Rolle und Scopes. Das ist die
Voraussetzung dafür, dass man später sieht: „Diese Entscheidung hat der Tech-Lead-Agent
am 3.11. getroffen, der PO-Agent hat sie am 4.11. kommentiert.“

Actors (Menschen wie Agenten) existieren **einmal pro Installation**. Was sie in einem
Workspace dürfen, regelt eine **Mitgliedschaft** (Rolle + Scopes). Derselbe Tech-Lead-Agent
kann so in Workspace A arbeiten und in Workspace B nur lesen oder gar nicht vorkommen.

## 3. Anforderungen

### Funktional

| Nr. | Anforderung |
|---|---|
| F1 | Agenten können strukturierte Datensätze anlegen und ändern (Aufgaben, Entscheidungen, Analysen, Notizen, beliebige eigene Typen). |
| F2 | Agenten können gezielt abfragen (Filter, Sortierung, Volltext), einzelne Datensätze inkl. Historie lesen. |
| F3 | Agenten bekommen **Schema-Führung**: Jede Sammlung beschreibt ihre Felder, erlaubte Werte und Pflichtfelder maschinenlesbar (JSON Schema), plus Vorlagen. Validierungsfehler sind strukturiert, damit ein Agent sich selbst korrigieren kann. |
| F4 | Menschen sehen Daten als **Rohformat** (JSON/Markdown) *und* in klassischen Ansichten: Tabelle, Karten/Board, Liste, Dokument (Rich Text). |
| F5 | **Datenmodell ≠ Präsentation.** Ansichten (Views) sind eigene Objekte mit Filter, Sortierung, Gruppierung und sichtbaren Feldern. Auch Agenten dürfen Views anlegen („hier ist das Board für den Sprint“). |
| F6 | Vollständige Historie: Revisionen pro Datensatz, Aktivitätslog pro Workspace, Actor an jeder Änderung. |
| F7 | Kommentare/Diskussion an Datensätzen (Agent ↔ Agent, Mensch ↔ Agent). |
| F8 | Suche über alles (Volltext, später semantisch). |
| F9 | Jeder Agent-Typ kann andocken: MCP-Server (Streamable HTTP) und REST/OpenAPI, gleiche Semantik. |
| F10 | Beziehungen zwischen Datensätzen (z. B. Aufgabe → Entscheidung → betroffene Komponente). Beziehungen enden an der Workspace-Grenze. |
| F11 | **Multi-Workspace.** Eine Installation verwaltet beliebig viele Workspaces. Daten, Schemata, Views, Suche, Aktivität und Zugriffsrechte sind strikt pro Workspace getrennt. Kein Agent und kein Mensch sieht Daten eines Workspaces ohne Mitgliedschaft. |
| F12 | Workspace-Verwaltung: Workspaces legen nur Actors mit Instanz-Recht an (Menschen als Instanz-Admin oder Agenten mit `workspaces:create`); wer anlegt, wird Owner. Workspace-Owner verwalten Mitglieder und Agenten ihres Workspaces. |

### Nicht-funktional

- Lokal betreibbar mit einem Befehl (Docker Compose / .NET Aspire), später auch Server/Cloud.
- Einfach zu betreiben: ein Backend-Prozess, eine PostgreSQL-Datenbank für alle Workspaces.
- Mandantentrennung ist testbar: automatisierte Tests, die Cross-Workspace-Zugriffe über REST und MCP durchprobieren, sind Teil der CI.
- Moderner Stack, aktuelle Versionen (siehe Abschnitt 5).
- Latenz für Agenten niedrig: Abfragen < 200 ms im Normalfall, keine Zwangs-Paginierung bei kleinen Mengen.
- Sicherheit: API-Keys nur gehasht gespeichert, Scopes, Audit, HTTPS.

## 4. Leitentscheidungen (Kurz-ADRs)

### E1 – Agent-Schnittstelle: MCP *und* REST, beide dünne Adapter

Beide Schnittstellen rufen dieselben Use-Cases der Application-Schicht auf. MCP ist der
„natürliche“ Weg für LLM-Agenten (Tools, Resources, Prompts), REST ist für Skripte,
klassische Integrationen und die Web-UI. Es gibt kein Feature, das nur eine Seite kann.

### E2 – Inhalt als Markdown, nicht als Block-Modell

Notion speichert Inhalte als Baum von Blöcken. Wir speichern den Fließtext eines
Datensatzes als **Markdown** (kanonisch) plus strukturierte **Properties** (typisiert).

Warum: Agenten produzieren und lesen Markdown nativ, Diffs und Historie sind trivial,
der Web-Editor (WYSIWYG) kann Markdown verlustarm hin- und herwandeln. Ein Block-Modell
kostet viel und bringt Agenten nichts. Eingebettete Datenbanken/Views im Text lösen wir
über Markdown-Erweiterungen (z. B. `:::view id=…`) statt über Blöcke.

Konsequenz: Tabellen im Fließtext sind Markdown-Tabellen; „richtige“ Tabellen sind Collections.

### E3 – Schema als Daten, Properties als JSONB

Collections (Sammlungen) haben ein Schema aus Property-Definitionen, das in der Datenbank
als Daten liegt (keine DB-Tabelle pro Collection). Werte liegen als JSONB in PostgreSQL,
indexiert per GIN. Damit können Agenten zur Laufzeit neue Sammlungen und Felder anlegen,
ohne Migrationen.

### E4 – Modularer Monolith, Vertical Slices

Ein Backend-Prozess, intern nach Fachmodulen geschnitten (Workspace, Schema, Records,
Views, Activity, Identity, Search). Keine Microservices. Klare Schichten:
Domain → Application → Infrastructure → Endpoints (REST, MCP, SignalR).

### E5 – Identität: Agenten = API-Key-Principals, Menschen = Login

Agenten authentifizieren sich mit `Authorization: Bearer <key>`. Jeder Key gehört genau
einem Agent-Actor. **Ein Key gilt über alle Workspaces hinweg**, in denen der Actor Mitglied
ist; was er dort jeweils darf, steht in der Mitgliedschaft (Rolle + Scopes read / write /
schema / admin, optional pro Space). Derselbe Key kann also in Workspace A schreiben und in
B nur lesen. Optional lässt sich ein Key auf einen Workspace einschränken (z. B. für einen
Agenten, der nur für ein Projekt existiert).

Instanz-Rechte (`instance:admin`, `workspaces:create`) hängen am Actor, nicht an einer
Mitgliedschaft, und können auch Agenten vergeben werden. Ein Agent mit `workspaces:create`
darf per MCP/REST neue Workspaces anlegen und wird deren Owner.
Menschen melden sich zunächst lokal an (Passwort), später optional OIDC (z. B. Entra, Keycloak, Auth0).

### E6 – Append-only Aktivitätslog + Revisionen

Jede Schreiboperation erzeugt ein ActivityEvent (wer, was, wann, Diff) und eine neue
Revision des Datensatzes. Updates verlangen optional `expectedRevision` (optimistische
Nebenläufigkeit), damit zwei Agenten sich nicht gegenseitig überschreiben.

### E7 – Multi-Workspace: gemeinsame Datenbank, `WorkspaceId` überall

Drei Varianten standen zur Wahl:

| Variante | Vorteil | Nachteil |
|---|---|---|
| **Gemeinsames Schema, `WorkspaceId`-Spalte** (gewählt) | Eine Migration, ein Connection-Pool, einfache Admin-Sicht über alle Workspaces, Workspaces kostenlos | Trennung liegt in der Anwendung; braucht Disziplin und Tests |
| Schema pro Workspace | harte Trennung in der DB | Migrationen × n, EF-Modell pro Schema, komplizierter Pool |
| Datenbank pro Workspace | maximale Trennung, einfaches Backup pro Mandant | Betrieb × n, teuer, für ein Ein-Betreiber-System überdimensioniert |

Absicherung in Schichten: Tenant-Kontext aus der URL → EF-Core-Global-Query-Filter auf
`WorkspaceId` → zusätzlich PostgreSQL Row-Level-Security mit `SET LOCAL app.workspace_id`
pro Transaktion (Phase 2) als zweites Netz. Alle IDs sind global eindeutig (UUIDv7), damit
ein versehentlich falscher Lookup niemals „zufällig“ einen fremden Datensatz trifft.

### E8 – Technologie-Versionen

| Komponente | Wahl | Anmerkung |
|---|---|---|
| Runtime | **.NET 10 (LTS)** | .NET 11 (STS) erscheint am 10.11.2026; Upgrade später unkritisch. LTS für ein Fundament-Projekt sinnvoller. |
| Web-API | ASP.NET Core Minimal APIs, OpenAPI, Scalar-UI | |
| MCP | `ModelContextProtocol` + `ModelContextProtocol.AspNetCore` (2.x, stabil) | Streamable HTTP, stateless-fähig |
| ORM / DB | EF Core 10, **PostgreSQL 17** (Npgsql), JSONB, tsvector, später pgvector | |
| Realtime | SignalR | UI-Aktualisierung bei Agent-Schreibzugriffen |
| Orchestrierung lokal | .NET Aspire | startet API, Postgres, Angular-Dev-Server |
| Frontend | **Angular 22** (zoneless, Signals, Standalone, Signal Forms) | Angular 23 ist für Ende 2026 zu erwarten; Upgrade-Pfad einfach |
| UI-Bausteine | Angular CDK (Virtual Scroll, Drag&Drop), Tailwind CSS, eigene Komponenten | Alternativ Angular Material 3; Entscheidung in Phase 1 |
| Rich-Text | Markdown-WYSIWYG (Milkdown oder TipTap/ProseMirror mit Markdown-Serialisierung) | Markdown bleibt kanonisch |
| Raw-Ansicht | Monaco Editor (JSON/Markdown mit Syntax-Highlighting) | |
| Tests | xUnit, Testcontainers (Postgres), Playwright (UI), Vitest (Angular) | |

## 5. Was agcowork bewusst *nicht* ist (v1)

- Kein kommerzielles SaaS: keine Selbstregistrierung, kein Billing, keine Quotas.
  Mehrere Workspaces ja, aber angelegt vom Instanz-Admin.
- Kein Block-Editor à la Notion (siehe E2).
- Kein Echtzeit-Kollaborations-Editing (CRDT) zwischen Menschen. Agenten schreiben atomar, Menschen selten gleichzeitig.
- Keine eigene Agent-Runtime. agcowork ist der Speicher, nicht der Orchestrator.

## 6. Roadmap

### Phase 0 – Gerüst (ca. 1 Woche)
- Repo, Solution-Struktur, Aspire AppHost, PostgreSQL-Container, Angular-Projekt, CI (Build + Tests).
- Erste Migration, Health-Endpoint, Login-Seite.

### Phase 1 – MVP „Agent-first“ (ca. 3–4 Wochen)
- Domain: Workspace, Membership, Space, Collection + Schema, Record (Properties + Markdown-Body), View, Actor, Revision, ActivityEvent.
- Tenancy: Tenant-Kontext aus URL, Global-Query-Filter, Isolationstests für REST und MCP.
- Identity: globale Actors (Mensch/Agent), Mitgliedschaften pro Workspace, API-Keys mit Scopes; lokaler Mensch-Login; Instanz-Admin.
- Filter-DSL (JSON) für Abfragen, in REST, MCP und UI identisch.
- REST `/api/v1` komplett, OpenAPI.
- MCP-Server mit Kern-Tools (siehe Architektur §4), Resources für Records und Schemata.
- UI: Workspace-Wechsler, Space-Navigation, Tabellen-View, Dokument-View (Markdown-Editor), Raw-View, Aktivitätsfeed, Agent-/Key-Verwaltung, Workspace- und Mitglieder-Verwaltung.
- Eingebaute Workspace-Vorlage „Software-Projekt“: Spaces, Sammlungen (Entscheidungen, Aufgaben, Analysen, Komponenten), Views und Record-Vorlagen. Neue Workspaces starten damit, „leer“ bleibt wählbar.

### Phase 2 – Menschen-Komfort (ca. 3 Wochen)
- Board-View (Gruppierung nach Status/Select, Drag&Drop), Karten-/Galerie-View, Listen-View.
- Kommentare, Erwähnungen (@agent, @mensch).
- SignalR-Live-Updates (Gruppen pro Workspace), Volltextsuche (tsvector), Beziehungen (Relation-Properties) mit UI.
- PostgreSQL Row-Level-Security als zweite Schutzschicht für die Mandantentrennung.
- Vorlagen-Verwaltung in der UI, Property-Editoren für alle Typen.

### Phase 3 – Agent-Komfort & Reife
- Semantische Suche (pgvector + Embeddings), MCP-Prompts („schreibe eine Entscheidung“, „Tageszusammenfassung“).
- Subscriptions/Webhooks: Agenten werden bei Änderungen benachrichtigt.
- Feingranulare Rechte pro Space, OIDC, Dateianhänge (S3-kompatibel / MinIO).
- Notion-Import (aus Notion-Export) pro Workspace, Markdown-Export pro Workspace.
- Workspace-Vorlagen als Daten: ein bestehender Workspace wird als Vorlage markiert, seine Struktur (ohne Records) wird beim Anlegen geklont; optional mit vorgesehenen Agenten-Rollen.

## 7. Offene Fragen (bitte entscheiden)

1. **Mehrere Menschen?** Nur du oder später ein Team? Beeinflusst Auth-Aufwand in Phase 1. Mitgliedschaften sind mit Multi-Workspace ohnehin nötig, der Mehraufwand für weitere Personen ist dann gering.
2. **Hosting?** Lokal (Docker auf dem Mac), eigener Server oder Cloud? Beeinflusst Secrets, HTTPS, Backups.
3. **Welche Agenten konkret?** Claude Code per MCP, eigene .NET-Agenten (Microsoft Agent Framework / Semantic Kernel), andere Frameworks? Beeinflusst, ob neben HTTP auch ein stdio-MCP-Wrapper nötig ist.
4. **Semantische Suche** schon in Phase 1 oder reicht Volltext zunächst?
5. **Notion-Import** gewünscht (bestehende Inhalte migrieren)?
6. **Dateianhänge** (Bilder, PDFs) in v1 nötig?
7. **Markdown als Kanon** (E2) einverstanden, oder ist dir ein Block-Editor wichtig?
8. **UI-Bausteine:** Tailwind + eigene Komponenten (flexibler, mehr Arbeit) oder Angular Material (schneller, generischer)?
### Bereits entschieden (2026-11-07)

- Ein Agent-Key gilt über mehrere Workspaces hinweg; die Rechte pro Workspace kommen aus der Mitgliedschaft (z. B. A: schreiben, B: lesen). Einschränkung eines Keys auf einen Workspace bleibt optional möglich.
- Workspaces legen nur Instanz-Admins an. Das Recht `workspaces:create` kann auch Agenten vergeben werden.
- Neue Workspaces starten mit der eingebauten Standard-Vorlage „Software-Projekt“ (Phase 1). „Leer“ bleibt als Option. Vorlagen aus bestehenden Workspaces folgen in Phase 3.
