# AP-012 – Frontend: Markdown-Editor und Raw-View als Bausteine

| Feld | Wert |
|---|---|
| Status | abgeschlossen |
| Phase | 1 |
| Implementierer | implementer-opus |
| Referenzbeispiel | – (erstes Auftreten; Muster aus AP-002: `WorkspaceContext`, `app.routes.spec.ts`) |
| Abhängig von | AP-002 |
| Betroffene Pfade | ausschließlich `src/Agcowork.Web/` (neu: `src/app/shared/editor/`, `src/app/shared/raw-view/`, `src/app/features/dev-playground/`; geändert: `app.routes.ts`, `package.json`, `angular.json` für Budgets/Assets) |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §3 (document, raw), §6 Bausteine (Document, Raw); `01-PLANUNG.md` E2 (Markdown kanonisch) |

## Ziel

Nach diesem Paket gibt es zwei wiederverwendbare, API-unabhängige Bausteine: einen
Markdown-WYSIWYG-Editor, der Markdown verlustarm hin- und herwandelt, und eine Raw-Ansicht
mit Syntax-Highlighting für JSON und Markdown. Beide sind das technisch riskanteste Stück
des Frontends und werden deshalb früh und isoliert gebaut. Spätere Pakete verdrahten sie
mit Records.

## Scope

**Drin:**

*Markdown-Editor (`shared/editor/markdown-editor`)*
- Standalone-Komponente `ag-markdown-editor` mit Signal-Inputs `value` (Markdown-String), `readonly`, `placeholder`; Output `valueChange` (Markdown, debounced ~300 ms, nur bei echter Änderung) und `blur`. Model-Input (`model()`) ist erlaubt, wenn es sauber mit Signals arbeitet.
- Editor-Bibliothek: **Milkdown** (ProseMirror-basiert, Markdown-nativ; „Crepe“-Variante als Startpunkt erlaubt). Falls Milkdown mit Angular 22 / Vitest nicht sauber läuft, ist **TipTap + tiptap-markdown** die Alternative. Die Wahl wird im Bericht mit den konkreten Gründen festgehalten.
- Unterstützte Syntax (muss round-trippen): Überschriften 1–3, Absätze, fett/kursiv/Code inline, Listen (geordnet, ungeordnet, verschachtelt), Aufgabenlisten (`- [ ]`), Code-Blöcke mit Sprache, Zitate, Links, Tabellen (GFM), horizontale Linie. Rohes HTML wird nicht gerendert (Sicherheit, Architektur §7).
- Theming über die bestehenden `--ag-*`-Tokens, Hell/Dunkel.
- Kein Toolbar-Overkill: Slash-Menü oder minimale Leiste reicht; Tastenkürzel für fett/kursiv/Überschriften.
- Lazy geladen: Die Editor-Bibliothek darf nicht im Initial-Bundle liegen. Budget in `angular.json` für das Initial-Bundle setzen (Warnung ab 600 kB, Fehler ab 1 MB) und einhalten.

*Raw-View (`shared/raw-view`)*
- Standalone-Komponente `ag-raw-view` mit Inputs `value` (string), `language` (`json` | `markdown`), `readonly`; Output `valueChange`; bei `json` zusätzlich Signal `isValid` (Parse-Prüfung) und Fehlermeldung.
- Implementierung mit **Monaco Editor** (ESM, Worker-Konfiguration für den Angular-Builder) oder, falls Monaco zu schwer integrierbar ist, **CodeMirror 6** (leichter, ESM-nativ). Wahl im Bericht begründen. Lazy geladen, gleiche Budgetregel.
- Umschaltbar zwischen Lesen und Bearbeiten; Hell/Dunkel über Tokens.

*Spielwiese (`features/dev-playground`)*
- Lazy Route `/dev/playground` (nicht in der Navigation verlinkt): links der Markdown-Editor, rechts die Raw-View mit demselben Markdown, darunter eine Raw-View mit JSON-Beispiel. Änderungen im Editor erscheinen in der Raw-View und umgekehrt. Dient manueller Prüfung und späteren Demos.

*Tests (Vitest)*
- Round-Trip-Tests für den Editor: Für jedes unterstützte Syntax-Element wird Markdown hineingegeben und das serialisierte Markdown verglichen (normalisiert auf Whitespace). Mindestens 10 Fälle, inkl. verschachtelter Liste und Tabelle.
- Editor: `valueChange` feuert nicht beim Setzen von `value` von außen; `readonly` blockiert Eingabe.
- Raw-View: ungültiges JSON → `isValid` false mit Fehlermeldung; `valueChange` bei Eingabe; Sprachumschaltung.
- Wenn Monaco/Milkdown in jsdom nicht lauffähig ist: Die Bibliothek hinter einem kleinen Adapter kapseln, den Adapter in Tests durch einen Fake ersetzen, und die Round-Trip-Tests gegen die Serialisierungsschicht der Bibliothek (ohne DOM) fahren. Im Bericht genau sagen, was wirklich getestet ist.

**Ausdrücklich nicht drin:**
- Anbindung an Records, Speichern, Kommentare, Erwähnungen, eingebettete Views (`:::view`), Bild-Upload.
- Dateien außerhalb von `src/Agcowork.Web/`.

## Fachlicher Kontext

- Markdown ist das kanonische Format (E2). Der Editor ist eine Sicht darauf; die Wahrheit ist der String. Deshalb zählt Round-Trip-Treue mehr als Editor-Komfort.
- Agenten schreiben Markdown, Menschen editieren es im WYSIWYG. Beide Seiten müssen das Ergebnis des jeweils anderen unverändert lesen können.
- Konventionen aus `CLAUDE.md` und die Muster aus AP-002 (Signals, `inject()`, Tokens in `styles.css`, Tests mit echten Routen).
- Node 24, npm 11, Angular 22.2 mit Vitest 5 sind im Projekt eingerichtet.

## Akzeptanzkriterien

1. `/dev/playground` zeigt Editor und Raw-Views; Tippen im Editor aktualisiert die Markdown-Raw-View innerhalb einer Sekunde, Ändern der Raw-View aktualisiert den Editor.
2. Round-Trip: Die 10+ Testfälle laufen grün; ein manuell eingefügtes Beispiel mit allen Syntax-Elementen kommt nach Editor → Raw unverändert zurück (Whitespace-normalisiert).
3. Rohes HTML im Markdown wird im Editor nicht als HTML gerendert.
4. `npm run build` grün, Initial-Bundle unter dem Budget, Editor- und Raw-Bibliothek in Lazy-Chunks (im Build-Output sichtbar).
5. `npm test` und `npm run lint` grün; Hell/Dunkel wirkt auf beide Bausteine.
6. Bericht nennt die gewählten Bibliotheken mit Version und die Gründe.

## Tests, die mindestens entstehen

- `shared/editor/markdown-editor.component.spec.ts` (oder Serialisierungs-Spec), `shared/raw-view/raw-view.component.spec.ts`, Routen-Spec um `/dev/playground` ergänzt.

## Getroffene Entscheidungen (aus dem Bericht)

- **Milkdown 7.23 über `@milkdown/kit`**, nicht Crepe: läuft in Vitest/jsdom ohne Fake; Crepe bringt CodeMirror, KaTeX und eigenes Theming mit (größerer Chunk, Token-Kollision). TipTap nicht nötig.
- **Monaco 0.57 kuratiert** (Editor-API, gezielte Contribs, Markdown, JSON) statt `editor.main`; Worker als lokale Dateien, CSS als nicht-injizierter `styles`-Eintrag, `.ttf`-Loader. CodeMirror nicht nötig.
- **Adapter-Schnitt**: Engines hinter `InjectionToken` mit `import()`-Factory, Fake-Provider für Tests. Standard für jede weitere Fremdbibliothek.
- Serializer: `-` für Listen, `*` für Hervorhebung, Backtick-Fences.

## Entscheidungen, die der Implementierer treffen darf

- Milkdown vs. TipTap; Monaco vs. CodeMirror 6 (jeweils begründen).
- Debounce-Zeit, Toolbar-Umfang, Adapter-Schnitt für Tests.
- Ob der Playground in Production-Builds per Route-Guard ausgeblendet wird (vorerst nicht nötig).

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-07 | angelegt |
| 2026-11-07 | an implementer-opus übergeben |
| 2026-11-07 | Bericht: Milkdown 7.23 (@milkdown/kit, nicht Crepe), Monaco 0.57 kuratiert mit lokalen Workern; Adapter-Schnitt mit Injection-Tokens; 71 Tests grün (18 Round-Trips gegen echtes Milkdown, Raw-View nur gegen Fake), Initial 285 kB, Editor 468 kB lazy, Monaco 3,3 MB lazy; manuell in Chrome geprüft. Abweichungen: Output `editorBlur`, Input `debounceMs`. Offen: Änderungen im Debounce-Fenster gehen bei Destroy verloren; Checkboxen nicht klickbar; javascript:-Links und img-src ungefiltert |
| 2026-11-07 | an reviewer übergeben |
| 2026-11-07 | Review: Freigabe mit Hinweisen. Wichtig: Listen-Test normalisiert Einrückung weg (nicht beweiskräftig); Editor normalisiert beim Bearbeiten das ganze Dokument (Escapes, Autolinks, `*`→`-`), Entscheidung vor Record-Anbindung; Destroy-Verlust bekannt. Hinweise: javascript:-Links filtert Milkdown bereits (Allowlist), nur img-src offen; kein Fehlerzustand bei fehlgeschlagenem Nachladen; Monaco-Adapter ohne Test; `token()` nur Hex; drei verwaiste CSS-Chunks |
| 2026-11-07 | Entscheidungen: Normalisierung akzeptiert (Architektur E2 ergänzt). Nacharbeit an implementer-opus: Listen-Test mit Einrückung, Link-Filter-Test, Fehlerzustand beim Nachladen |
| 2026-11-07 | Nacharbeit fertig: 80 Tests grün, Lint/Build grün, Initial 285 kB; vom Orchestrator verifiziert. Abgeschlossen, Commit `2fc5c4c` |
