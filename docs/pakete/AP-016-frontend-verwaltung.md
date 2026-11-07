# AP-016 – Frontend: Workspace-Liste, Workspace-Verwaltung, Mitglieder, Agenten und Keys

| Feld | Wert |
|---|---|
| Status | in Arbeit |
| Phase | 1 |
| Implementierer | implementer-sonnet |
| Referenzbeispiel | AP-013: `core/api/api-client.ts` (typisierter Client, `api.workspace(slug)`), `core/api/field-errors.ts` + `features/auth/login.component.ts` (Signal Forms mit Serverfehlern), `core/auth/session.store.ts` (resource-Muster), `app.routes.spec.ts`, `workspace-shell.component.spec.ts`; AP-002: `WorkspaceContext` |
| Abhängig von | AP-005, AP-013 |
| Betroffene Pfade | ausschließlich `src/Agcowork.Web/` (neu: `features/workspaces/`, `features/workspace-settings/`, `features/admin/`; geändert: `app.routes.ts`, `workspace-shell` für Navigation) |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §2 (Workspace, Membership, Actor, ApiKey), §4.3 (Instanz- und Workspace-Endpunkte), §6 Routen (`/workspaces`, `/admin/*`, `/w/:slug/settings/members`); AP-004/AP-005-Pakete (Endpunkte, Fehlerformat, Rollen) |

## Ziel

Nach diesem Paket kann ein Mensch im Browser seine Workspaces sehen und wechseln, als
Instanz-Admin Workspaces anlegen, Actors (Menschen und Agenten) verwalten und API-Keys für
Agenten erzeugen, und als Workspace-Admin Mitglieder mit Rolle und Scopes verwalten. Damit
ist agcowork erstmals ohne REST-Client bedienbar, und Agenten können eingerichtet werden.

## Scope

**Drin:**

*Workspace-Liste und Wechsler (`features/workspaces`)*
- `/workspaces`: Liste aus `GET /api/v1/workspaces` (Name, Slug, Rolle, archiviert-Kennzeichen), Klick → `/w/{slug}`. Leerer Zustand mit Hinweis. Button „Neuer Workspace“ nur, wenn `me.instanceScopes` `workspaces:create` oder `instance:admin` enthält.
- Dialog/Seite „Neuer Workspace“: Signal Forms mit Slug (Regex wie Backend, Live-Vorschau), Name, Beschreibung, Vorlage (Select aus `empty` und, falls der Server es anbietet, `software-projekt`; Liste aus `allowed` des Validierungsfehlers oder fest, Entscheidung im Bericht). Nach Erfolg → `/w/{slug}`.
- Workspace-Wechsler in der Shell-Seitenleiste: aktueller Workspace, Dropdown mit den anderen, Link zur Liste. Nutzt dieselbe Resource wie die Liste (ein Service `WorkspacesStore`).

*Workspace-Verwaltung (`features/workspace-settings`)*
- `/w/:slug/settings` (Übersicht): Name, Beschreibung bearbeiten (`PATCH`), Archivieren mit Bestätigung (`POST …/archive`), nur für Rolle admin/owner sichtbar bzw. aktiv; Rollen-/Scope-Informationen aus `WorkspaceDto` (eigene Rolle, effektive Scopes).
- `/w/:slug/settings/members`: Tabelle der Mitglieder (Name, Kind, Rolle, Scopes, seit), Mitglied hinzufügen (Actor-Auswahl aus `GET /actors`, wenn Instanz-Admin, sonst Eingabe der Actor-ID; Rolle; optional Scopes), Rolle/Scopes ändern, entfernen mit Bestätigung. Fehler wie `last_owner`, `self_escalation`, `already_member` als verständliche Meldungen aus `ApiError.code`.

*Instanz-Admin (`features/admin`)*
- `/admin/workspaces`: alle Workspaces (hier reicht `GET /workspaces` für den Admin; falls der Endpunkt nur Mitgliedschaften liefert, Hinweis im Bericht und Liste entsprechend beschriften), Anlegen wie oben.
- `/admin/actors`: Liste (Name, Kind, Rolle, E-Mail, Instanz-Scopes, aktiv), Anlegen (Mensch mit E-Mail + Initialpasswort, Agent mit Name + Rolle), Bearbeiten (Name, Rolle, Instanz-Scopes, aktiv), `self_lockout` verständlich melden.
- `/admin/actors/:id/keys`: Keys eines Agenten (Prefix, Scopes, Workspace-Einschränkung, erstellt, zuletzt benutzt, abgelaufen/widerrufen), Key erzeugen (optional Scopes, Workspace, Ablauf) mit **einmaliger Anzeige des Klartext-Keys** inkl. Kopieren-Button und deutlichem Hinweis, Widerrufen mit Bestätigung.
- Routen-Guard `instanceAdminGuard` (nur `instance:admin`), sonst Weiterleitung auf `/workspaces` mit Hinweis.

*Navigation*
- Shell-Seitenleiste: Eintrag „Einstellungen“ (nur admin/owner), „Mitglieder“; Admin-Bereich über das Session-Menü (nur Instanz-Admin).

*Gemeinsame Bausteine (`shared/ui`)*
- Kleine, wiederverwendbare Komponenten, sofern noch nicht vorhanden: Bestätigungsdialog, einfache Tabelle (ohne Virtualisierung; die echte Table-View kommt in AP-015), Formular-Feldfehler, Badge für Rolle/Kind/Status. Tailwind mit Tokens, keine UI-Bibliothek.

*Tests (Vitest)*
- `WorkspacesStore`: lädt Liste, `create()` fügt hinzu, Fehler `slug_taken` landet am Feld.
- Liste und Wechsler: rendern aus Store, Button-Sichtbarkeit nach Scopes.
- Mitglieder: Anzeige, Hinzufügen mit Rolle, Fehlercodes → Meldungen, Entfernen mit Bestätigung.
- Admin: Guard (Nicht-Admin → `/workspaces`), Actor anlegen (Mensch/Agent, Validierungsfehler am Feld), Key erzeugen zeigt Klartext genau einmal, Liste danach ohne Klartext.
- Routen-Spec um alle neuen Pfade erweitert.

**Ausdrücklich nicht drin:**
- Spaces, Collections, Records, Views (AP-014/015/017), Aktivitätsfeed (AP-017).
- Backend-Änderungen jeder Art. Fehlt ein Endpunkt oder Feld, im Bericht melden und im UI mit Hinweis auslassen.
- Passwort ändern/Reset, OIDC.

## Fachlicher Kontext

- Endpunkte und DTOs stehen typisiert in `core/api/generated/schema.d.ts` (nach AP-005 neu erzeugt). Workspace-Endpunkte über `api.workspace(slug)`, Instanz-Endpunkte über `api`.
- Rollen und Scopes: viewer = read; editor = read, write; admin = read, write, schema, admin; owner = alles. Owner kann nur Owner anlegen; letzter Owner nicht entfernbar (`last_owner`); eigene Mitgliedschaft nur einschränkbar (`self_escalation`); Admin kann sich nicht selbst deaktivieren (`self_lockout`).
- Key-Klartext kommt nur in der 201-Antwort; danach nie wieder. Nicht in `localStorage`, nicht in Logs.
- Fehlerformat: `ApiError` mit `code`, `errors[]`; `toServerErrors()` für Formulare.
- Deutsch in der UI, Englisch im Code.

## Akzeptanzkriterien

1. Nach Login zeigt `/workspaces` die eigenen Workspaces; ein Instanz-Admin legt einen neuen an und landet darin; ein Slug-Konflikt erscheint am Feld.
2. `/w/{slug}/settings/members`: Owner fügt einen Agenten als editor hinzu, ändert ihn auf viewer, entfernt ihn; Versuch, den letzten Owner zu entfernen, zeigt die `last_owner`-Meldung.
3. `/admin/actors`: Admin legt einen Agenten an, erzeugt einen Key, sieht den Klartext genau einmal und kann ihn kopieren; nach Reload zeigt die Liste nur Prefix und Metadaten; Widerruf markiert den Key.
4. Nicht-Admin erreicht `/admin/*` nicht; Viewer sieht in den Einstellungen keine Schreibaktionen.
5. `npm test`, `npm run lint`, `npm run build` grün; alle neuen Routen im Routen-Spec.
6. Kriterien 1–3 im Browser gegen die laufende API geprüft (Playwright-Skript im Scratchpad oder manuell, Ergebnis im Bericht).

## Tests, die mindestens entstehen

- `features/workspaces/workspaces.store.spec.ts`, `workspace-list.component.spec.ts`, `workspace-create.component.spec.ts`
- `features/workspace-settings/members.component.spec.ts`, `workspace-settings.component.spec.ts`
- `features/admin/admin.guard.spec.ts`, `actors.component.spec.ts`, `actor-keys.component.spec.ts`
- `app.routes.spec.ts` erweitert

## Entscheidungen, die der Implementierer treffen darf

- Dialog vs. eigene Seite für Anlegen-Formulare; Aufteilung der Komponenten; Namen der Stores.
- Wie die Vorlagenliste beim Workspace-Anlegen bestimmt wird (fest oder aus `allowed`).

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
| 2026-11-07 | an implementer-sonnet übergeben |
