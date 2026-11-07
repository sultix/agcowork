# AP-019 – UI-Fundament: spartan/ui (shadcn für Angular), shadcn-Theme, Shell und Referenz-Migration

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | UI |
| Implementierer | implementer-opus |
| Referenzbeispiel | – (erstes Auftreten des Musters) |
| Abhängig von | AP-012 bis AP-017 |
| Betroffene Pfade | `src/frontend/` – neu: Helm-Komponenten (Pfad siehe Entscheidungen), `components.json`; geändert: `package.json`, `package-lock.json`, `tsconfig.json`, `angular.json` (nur falls nötig), `src/styles.css`, `src/index.html`, `core/theme/`, `shared/ui/`, `shared/editor/markdown-editor.component.css`, `shared/raw-view/monaco-adapter.ts`, `features/workspace-shell/`, `features/spaces/workspace-tree.component.ts`, `features/auth/`, `features/workspaces/`; Doku: `docs/02-ARCHITEKTUR.md` §6 (Theming, neuer Unterabschnitt „UI-Bausteine“) |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §6 (Frontend, Bausteine, Theming); CLAUDE.md Angular-Konventionen |

## Ziel

Die Oberfläche übernimmt Optik und Bausteine von shadcn/ui (https://ui.shadcn.com) über den Angular-Port **spartan/ui** (Brain-Primitives als Abhängigkeit, gestylte Helm-Komponenten im Repo). Danach gibt es ein shadcn-Theme (Basis „neutral“, hell/dunkel/system), einen Satz Helm-Komponenten, eine neu gestaltete Shell und Login-/Workspace-Seiten als Referenz, nach der AP-020 und AP-021 die übrigen Features migrieren.

## Scope

**Drin:**

*Installation und Gerüst*
- `@spartan-ng/brain` (1.9.x, unterstützt Angular 21–22) als Dependency, `@spartan-ng/cli` als devDependency, dazu die Peers und Helm-Laufzeitabhängigkeiten (`clsx`, `tailwind-merge`, `class-variance-authority`, `tw-animate-css`, `luxon` falls als Peer verlangt). Icons: `@ng-icons/core` + `@ng-icons/lucide` (shadcn nutzt Lucide).
- `components.json` per spartan-CLI (`ng g @spartan-ng/cli:init` bzw. was die CLI 1.9 anbietet; in `node_modules/@spartan-ng/cli` nachsehen). Helm-Komponenten per CLI ins Repo erzeugen, nicht von Hand abschreiben.
- Helm-Satz (mindestens): button, input, label, textarea, select, checkbox, switch, badge, card, separator, dialog, alert-dialog, dropdown-menu, popover, tabs, table, tooltip, skeleton, breadcrumb, sidebar (falls in 1.9 vorhanden; sonst Shell-Seitenleiste mit Helm-Button/Separator selbst bauen), sonner/toast **nicht**. Weitere nur, wenn eine Referenzseite sie braucht.
- Generierte Helm-Dateien sind Fremdcode im Repo: Lint-/Prettier-Ausnahmen setzen, wenn sie die Regeln verletzen, statt sie umzuschreiben. Vitest muss sie nicht abdecken.

*Theme*
- `styles.css`: shadcn-Theme „neutral“ (oklch-Variablen `--background`, `--foreground`, `--card`, `--card-foreground`, `--popover…`, `--primary…`, `--secondary…`, `--muted…`, `--accent…`, `--destructive`, `--border`, `--input`, `--ring`, `--radius`, `--chart-*`, `--sidebar-*`) für hell und dunkel, `@theme inline` wie in der shadcn-Tailwind-v4-Anleitung, `@import 'tw-animate-css'`, spartan-Styles laut deren Installationsanleitung.
- Dunkelmodus: shadcn/spartan schalten über die Klasse `.dark` (`@custom-variant dark`). `ThemeService` behält die drei Modi `system | light | dark` und den Storage-Key; er setzt `.dark` auf `<html>` nach dem **effektiven** Modus (`isDark()`), inklusive Live-Wechsel der Systemeinstellung. Kein Aufblitzen des falschen Themes beim Laden: kleines Inline-Skript in `index.html`, das Storage + `matchMedia` liest und `.dark` vor dem ersten Paint setzt (CSP prüfen: aktuell keine CSP, dann im Bericht vermerken).
- Erfolgs-Ton: shadcn hat kein `success`. Eigene Variable `--success` (+ `--success-foreground`) in beiden Themes, als Tailwind-Farbe `success` verfügbar.
- **Übergangs-Aliase**, damit noch nicht migrierte Features (AP-020/021) korrekt aussehen: die bisherigen Tailwind-Farben `bg`, `surface`, `surface-hover`, `fg`, `muted`, `accent`, `accent-fg`, `border`, `success`, `danger` bleiben als `@theme inline`-Einträge bestehen und zeigen auf die neuen Variablen. Achtung Namenskollision: shadcn definiert `muted` und `accent` als **Flächen**farben, bei uns sind `text-muted` (= `muted-foreground`) und `bg-accent` (= `primary`) gemeint. Löse das so, dass alte Klassen weiter das Alte bedeuten, solange sie existieren (z. B. Aliase unter Präfix-freien alten Namen nur dort, wo kein shadcn-Name kollidiert, und für `muted`/`accent` die alte Bedeutung beibehalten, bis AP-020/021 umgestellt haben). Dokumentiere die Lösung im Abschnitt „UI-Bausteine“ mit einer Tabelle **alte Klasse → neue Klasse**; diese Tabelle ist die Migrationsvorschrift für AP-020/021. Der Orchestrator entfernt die Aliase nach AP-021.
- `--ag-*`-Variablen entfallen. Verbraucher umstellen: `markdown-editor.component.css` auf shadcn-Variablen; `monaco-adapter.ts`: `token()` akzeptiert nur 6-stellige Hex-Werte, oklch kommt dort nicht an. Farben über einen Resolver in Hex/`#rrggbb` umrechnen (z. B. Wert auf ein Hilfselement oder `canvas` anwenden und `getComputedStyle`/`fillStyle` auslesen), Ergebnis testen. Backlog-Hinweis AP-012 („Token-Parser erweitern, wenn Tokens auf oklch wechseln“) ist damit erledigt.

*Referenz-Migration (alles mit Helm-Bausteinen und shadcn-Klassen, keine alten Farbklassen mehr in diesen Dateien)*
- `shared/ui/`: `badge` → Helm-Badge (Varianten neutral/accent/success/danger auf `secondary`/`default`/success-Variante/`destructive` abbilden; die `ag-badge`-API darf bleiben oder Verbraucher werden umgestellt, Entscheidung im Bericht – Verbraucher außerhalb des Scopes dürfen dabei nicht brechen); `confirm-dialog` → Helm-Alert-Dialog (Inputs/Outputs und `data-testid`s bleiben, Fokusfalle und Escape kommen aus Brain); `session-menu` → Dropdown-Menu mit Avatar/Initialen; `breadcrumbs` → Helm-Breadcrumb; `placeholder-page`, `field-errors`, `actor-label`, `api-error-text` optisch angleichen.
- `features/workspace-shell/`: shadcn-typisches Layout – Seitenleiste (Workspace-Switcher oben, Navigation mit Lucide-Icons und aktivem Zustand, Space-Baum, ausklappbar/collapsible wenn die Sidebar-Komponente es hergibt), Kopfzeile mit Breadcrumb-Platz, Backend-Status als dezenter Indikator (Punkt + Tooltip statt Text), Theme-Umschalter als Icon-Button mit Dropdown (System/Hell/Dunkel), Session-Menü.
- `features/spaces/workspace-tree.component.ts` (sitzt in der Seitenleiste).
- `features/auth/login.component.ts`: zentrierte Card wie shadcn-Login-Beispiel, Label/Input/Button, Feldfehler unter dem Feld.
- `features/workspaces/`: Liste als Card-Raster oder Table, Erstellen-Formular mit Helm-Form-Bausteinen, Wechsler als Dropdown-Menu.

*Doku*
- `02-ARCHITEKTUR.md` §6: „Theming“ aktualisieren (spartan/ui, shadcn neutral, `.dark`, `ThemeService`), neuer Unterabschnitt „UI-Bausteine“: wo die Helm-Komponenten liegen, wie man weitere per CLI erzeugt, Klassen-Mapping alt → neu, Faustregeln (Seitenüberschrift, Abstände, Formular-Layout, Leerzustände, Fehleranzeige, destruktive Aktionen über Alert-Dialog, Icons nur Lucide).

**Ausdrücklich nicht drin:**
- Migration aller übrigen Features (AP-020, AP-021). Deren Dateien nicht anfassen, außer ein Build-Fehler erzwingt es (dann im Bericht nennen).
- Toasts/Notifications, Command-Palette, neue Funktionen oder Routen.
- Änderungen an Verhalten, API-Client, Stores oder Backend.
- Entfernen der Übergangs-Aliase.

## Fachlicher Kontext

- Bestehende Tests selektieren über `data-testid` und Rollen. Diese bleiben erhalten; Tests dürfen angepasst werden, wo die Struktur sich zwangsläufig ändert (z. B. Dropdown statt Liste), nie um Verhalten abzuschwächen.
- Zoneless, OnPush, Signals, `inject()` gelten auch für eigene Wrapper. Helm-Code wird so übernommen, wie die CLI ihn erzeugt.
- Komponenten-Budget in `angular.json`: `anyComponentStyle` 4 kB Warnung / 8 kB Fehler, Initial 600 kB Warnung. Initial-Bundle-Größe vorher/nachher im Bericht nennen.
- Brain-Overlays (Dialog, Dropdown, Popover) rendern über CDK-Overlay in den Body; in Vitest/jsdom ggf. `document.body` statt Fixture abfragen.
- Sprache der UI-Texte bleibt Deutsch.

## Akzeptanzkriterien

1. `npm run build` und `npm test` grün, `npm run lint` und `npm run format:check` ohne neue Fehler.
2. Hell, dunkel und system funktionieren; Systemwechsel zur Laufzeit schlägt bei `system` durch; der gewählte Modus übersteht einen Reload ohne Aufblitzen.
3. Keine `--ag-*`-Variable mehr im Code; Markdown-Editor und Monaco übernehmen die neuen Farben in beiden Themes (Monaco auch bei oklch-Werten).
4. Die im Scope genannten Dateien nutzen nur noch shadcn-Klassen (`bg-background`, `text-muted-foreground`, `bg-primary` …) und Helm-Bausteine; keine der alten Farbklassen mehr.
5. Nicht migrierte Seiten (z. B. Tabelle, Dokument, Mitglieder) sehen in beiden Themes stimmig aus (Aliase), keine unlesbaren Kontraste.
6. Confirm-Dialog: Rolle `alertdialog`, Fokus im Dialog, Escape und Abbrechen lösen `cancelled`, `busy` sperrt Bestätigen; bestehende Verbraucher funktionieren unverändert.
7. Shell: Navigation zeigt den aktiven Eintrag, Theme-Umschalter bietet drei Modi, Backend-Status bleibt per `data-testid="backend-status"` prüfbar.
8. `02-ARCHITEKTUR.md` enthält den Abschnitt „UI-Bausteine“ mit Mapping-Tabelle und Faustregeln.

## Tests, die mindestens entstehen

- Unit: `ThemeService` setzt/entfernt `.dark` für alle drei Modi und reagiert auf `matchMedia`-Change.
- Unit: Farb-Resolver im Monaco-Adapter wandelt einen oklch- und einen Hex-Wert in `#rrggbb` (in jsdom ggf. Resolver injizierbar machen und Logik getrennt testen).
- Komponente: Confirm-Dialog (Kriterium 6), Session-Menü öffnet und meldet ab, Theme-Umschalter setzt den Modus, Workspace-Wechsler navigiert.
- Bestehende Specs der migrierten Dateien bleiben grün.

## Entscheidungen, die der Implementierer treffen darf

- Ablageort der Helm-Komponenten (Vorschlag: `src/app/shared/ui/helm/<name>/` mit Pfad-Alias `@spartan-ng/helm/<name>` in `tsconfig.json`, so wie die CLI es vorsieht).
- Ob `ag-badge` als dünner Wrapper bleibt oder Verbraucher direkt `hlmBadge` nutzen.
- Aufbau der Alias-Lösung für `muted`/`accent`, solange Kriterium 5 hält und die Mapping-Tabelle eindeutig ist.
- Schrift: System-Font beibehalten oder Inter/Geist über `@fontsource` (keine CDN-Fonts). Im Bericht begründen.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt (Nutzerentscheid: spartan/ui, Theme „neutral“) |
| 2026-11-07 | an implementer-opus übergeben |
| 2026-11-07 | Bericht: 21 Helm-Bausteine + avatar/field/alert/native-select per CLI, Ablage `shared/ui/helm/`, Aliase für alte Klassen, oklch→Hex-Resolver für Monaco, Inline-Skript gegen Aufblitzen; 8 Specs außerhalb des Scopes (Overlay im Body) angepasst; Initial 95,9 → 106,5 kB übertragen |
| 2026-11-07 | Review: Freigabe mit Hinweisen – Cmd+B klappt Sidebar im Editor ein, Mapping-Tabelle mehrdeutig (Rahmen, Ring, Links), `aria-invalid` zu früh |
| 2026-11-07 | Nacharbeit: Sidebar-Shortcut aus (eigene Service-Instanz in der Shell), Tabelle präzisiert; `aria-invalid` nicht konfigurierbar → Backlog |
| 2026-11-07 | abgeschlossen |
