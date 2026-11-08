# AP-036 – PostgreSQL Row-Level-Security als zweites Netz der Mandantentrennung

| Feld | Wert |
|---|---|
| Status | angelegt |
| Phase | 2 |
| Implementierer | implementer-opus |
| Referenzbeispiel | – (erstes Auftreten); Tenancy `src/backend/Agcowork.Application/Tenancy/` (`ResolveTenant`, `TenantQueryFilter`), `Infrastructure/Persistence/AgcoworkDbContextOptions.cs` (zentrale Provider-Konfiguration, Interceptoren), `TenantSaveChangesInterceptor`, Katalogtest `tests/Agcowork.Api.Tests/Tenancy/TenantInterceptorTests.cs` |
| Abhängig von | AP-035 (committet, Migration vorher) |
| Betroffene Pfade | `src/backend/Agcowork.Infrastructure/Persistence/` (Interceptor, Instanz-Bereich, **eine** Migration), `src/backend/Agcowork.Application/` (nur Markierung der Instanz-Pfade: Tenancy, Workspaces, Realtime, Identity-Bootstrap), `src/backend/Agcowork.Api/` (nur falls Startpfade betroffen), `tests/`, `docs/02-ARCHITEKTUR.md` nur per Textvorschlag |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §2a Schritt 5 (verbindlich, Entscheidung 2026-11-08), §2a „Weitere Regeln“ |

## Ziel

Selbst wenn ein künftiger Code-Pfad den EF-Filter vergisst, Raw-SQL ohne `workspace_id` schreibt oder ein `ExecuteUpdate` falsch eingrenzt, liefert oder ändert die Datenbank nie Zeilen eines anderen Workspace.

## Scope

**Drin:**

1. **Rolle:** Migration legt `agcowork_app` an (NOLOGIN, NOSUPERUSER, NOBYPASSRLS, idempotent: existiert sie, nur Rechte setzen) und vergibt SELECT/INSERT/UPDATE/DELETE auf alle Tabellen, USAGE auf Sequenzen, EXECUTE auf die Funktionen (Suche), dazu `GRANT agcowork_app TO current_user`. Default Privileges, damit künftige Migrationen ihre Tabellen nicht vergessen; zusätzlich Katalogtest „jede Tabelle hat Rechte für `agcowork_app`“.
2. **Policies:** RLS aktiviert auf jeder Tabelle mit `workspace_id` (heute u. a. spaces, collections, records, revisions, views, templates, memberships, activity_events, idempotency_keys, comments, mentions). Policy für alle Kommandos: USING und WITH CHECK `workspace_id = current_setting('agcowork.workspace_id', true)::uuid OR current_setting('agcowork.scope', true) = 'instance'`. Leere/fehlende Einstellung darf keinen Cast-Fehler werfen (`nullif(…,'')`). Instanz-Tabellen ohne `workspace_id` (actors, api_keys, local_logins, workspaces, Migrationshistorie, DataProtection) ohne RLS.
3. **Verbindung:** Interceptor in `AgcoworkDbContextOptions.Configure` führt beim Öffnen `SET ROLE agcowork_app` und `set_config('agcowork.workspace_id', …, false)` sowie `agcowork.scope` aus, abgeleitet aus dem Tenant-Kontext bzw. Instanz-Bereich des Scopes. Wechselt der Kontext bei offener Verbindung (z. B. `CreateWorkspace` setzt den Tenant nach Anlage, offene Transaktion), wird die Einstellung sofort aktualisiert. Pool-Reset muss Rolle und Einstellungen zurücksetzen (Test: zwei Anfragen nacheinander auf derselben physischen Verbindung, verschiedene Workspaces).
4. **Instanz-Bereich:** schmale API (z. B. `using var _ = instanceScope.Enter("reason")` oder Attribut am Use-Case), nur für: `ResolveTenant`, `ListMyWorkspaces`, `InstanceActivityReader` (Dispatcher), Identity-Bootstrap, Workspace-Anlage bis der Tenant gesetzt ist, Instanz-Admin-Pfade, die Workspace-Tabellen lesen müssen (falls vorhanden). Architekturtest: Die Menge der Aufrufer ist eine feste, im Test aufgeführte Liste. Wo heute `IgnoreQueryFilters` steht, gehört genau ein Instanz-Bereich hin (und umgekehrt).
5. **Migrationen und Startaufgaben:** `MigrateAsync` läuft ohne Rollenwechsel (Owner); Bootstrap im Instanz-Bereich. Testcontainers und Compose funktionieren ohne neue Zugangsdaten.
6. **Trigger und Funktionen:** Such-Trigger (liest `collections`), Schema-Hooks, Kommentar-Sperren, Realtime-Dispatcher, Backfills funktionieren unter der Rolle; Funktionen nicht `SECURITY DEFINER`, außer begründet.
7. **Tests:**
   - Gesamte bestehende Suite grün (inkl. Isolationstests REST/MCP/Hub).
   - Neues zweites Netz: Test, der absichtlich `IgnoreQueryFilters()` bzw. Raw-SQL ohne `workspace_id` im Tenant-Kontext `alpha` ausführt und **nichts** aus `beta` sieht; INSERT/UPDATE mit fremder `workspace_id` scheitert an WITH CHECK.
   - Ohne Tenant und ohne Instanz-Bereich: Abfrage auf Tenant-Tabelle liefert leer.
   - Katalogtests: jede Tabelle mit `workspace_id` hat RLS aktiv und die Policy; jede Tabelle hat Rechte für `agcowork_app`.
   - Pool-Wiederverwendung ohne Leck (siehe 3).
8. **Doku:** Textvorschlag für `02-ARCHITEKTUR.md` §2a/§7 und `03-WORKFLOW.md` (Regel für neue Tabellen: Policy + Rechte, Katalogtest erzwingt es).

**Ausdrücklich nicht drin:**
- Separater Login ohne Superuser in Compose/Aspire (Ausbaustufe), Space-Rechte, Änderungen an Frontend oder API-Verträgen.

## Fachlicher Kontext

- Heute laufen alle Verbindungen als Superuser (`POSTGRES_USER` in Compose, Aspire-Standard) → RLS wäre ohne Rollenwechsel wirkungslos, auch mit `FORCE ROW LEVEL SECURITY`.
- Der Dispatcher liest instanzweit und stellt pro Workspace in eigenem Scope mit `RealtimeTenant` zu; die Zustellung muss unter dem jeweiligen Workspace laufen (Titel, Mitgliedschaften).
- Npgsql setzt beim Zurückgeben in den Pool den Sitzungszustand per `DISCARD ALL` zurück (Standard); das darf nicht abgeschaltet sein.
- Leistung: Policies nutzen die vorhandenen Indizes mit `workspace_id` vorn; im Bericht EXPLAIN für eine Record-Abfrage und die Suche vorher/nachher.

## Akzeptanzkriterien

1. Alle bestehenden Tests grün.
2. Zweites Netz belegt (Tests aus 7): Fremdzeilen unsichtbar trotz `IgnoreQueryFilters`/Raw-SQL; fremde `workspace_id` beim Schreiben abgelehnt; ohne Kontext leer.
3. Katalogtests für RLS und Rechte grün; Architekturtest begrenzt den Instanz-Bereich auf die Liste.
4. Kein Leck über Pool-Wiederverwendung.
5. Compose-Stack startet frisch und auf Bestandsdaten (Migration auf bestehender DB); Orchestrator-Rauchtest (Login, Workspace anlegen, Record, Suche, Kommentar, Live-Update).
6. EXPLAIN vorher/nachher im Bericht ohne Verschlechterung des Plans.

## Entscheidungen, die der Implementierer treffen darf

- Form der Instanz-Bereichs-API und Ort der Markierung.
- Ob die Einstellung per Verbindungs-Open plus Aktualisierung oder pro Transaktion gesetzt wird, solange 3 und 4 erfüllt sind.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-08 | angelegt |
