# AP-034 – Härtung Frontend: Record-Body gegen präparierten Markdown, Kommentare im Aktivitätsfeed, Fehlertexte

| Feld | Wert |
|---|---|
| Status | angelegt |
| Phase | 2 (Härtung) |
| Implementierer | implementer-opus |
| Referenzbeispiel | Kommentar-Schutz `src/frontend/src/app/features/comments/comment-markdown.ts` (`isPathological`, Klartext-Fallback, Messung) und `comment-renderer.ts` (Budget); Editor `shared/editor/milkdown-adapter.ts`, `markdown-editor.component.ts`; Feed `features/activity/activity-entry.component.ts` (`EVENTS`); Fehlertexte `shared/ui/api-error-text.ts` |
| Abhängig von | AP-031, AP-033 (committet) |
| Betroffene Pfade | `src/frontend/src/app/shared/editor/`, `features/records/` (nur Dokument- und Raw-Anzeige, soweit für den Fallback nötig), `features/activity/`, `shared/ui/api-error-text.ts`, ggf. gemeinsamer Helfer nach `shared/markdown/` (Prüfung aus `comment-markdown.ts` dorthin verschieben und von Kommentaren nutzen). **Nicht:** Backend |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §6 „Bausteine – Kommentare“ (Schutz gegen präparierten Markdown), §7 „Markdown-Rendering sanitisiert“ |

## Ziel

Ein Record-Body, den ein Agent schreibt, kann den Browser eines Menschen nicht einfrieren oder zum Absturz bringen. Der Aktivitätsfeed zeigt Kommentar-Ereignisse verständlich.

## Fachlicher Kontext

- AP-031-Review: remark/micromark parst rekursiv und teils quadratisch. Gemessen für Kommentare: `'>'×10000` → sofort Stack Overflow; `'*'×10000+x+'*'×10000` → 3,3 s + Stack Overflow; Lazy Continuation (`'> a\n' + 'b\n'×3900`) → ~410 ms. Milkdown nutzt dieselbe Kette; der Body darf bis 2 MiB groß sein. Ob und wie stark der Body betroffen ist, ist **noch nicht gemessen** – das ist Schritt 1.
- Der Editor ist auch die Lese-Ansicht (readonly). Ein Fallback darf Bearbeitung nicht still kaputt machen: Wer einen pathologischen Body öffnet, soll ihn mindestens lesen und im Raw-View (Monaco, kein Markdown-Parser) bearbeiten können.

## Scope

**Drin:**
1. **Messen:** Body-Rendering in Milkdown (readonly und editierbar) mit den Mustern aus dem AP-031-Review und realistischen großen Bodies (100 KB, 500 KB, 2 MiB normaler Markdown). Ergebnis als Tabelle im Bericht.
2. **Absichern:** Vorprüfung vor dem Laden in Milkdown (gemeinsamer Helfer mit den Kommentar-Regeln, Schwellen für den Body eigenständig nach Messung). Bei pathologischem oder zu großem Body: Hinweis „Dieser Text ist zu groß oder zu komplex für den Editor“ und Anzeige als reiner Text (`whitespace-pre-wrap`), Link „Im Raw-View bearbeiten“. Zusätzlich try/catch um das Laden in Milkdown mit demselben Fallback. Der Speichern-Pfad bleibt unberührt (kein stilles Überschreiben des Bodys durch den Fallback).
3. **Normale große Bodies** (z. B. 500 KB Fließtext mit Überschriften und Listen) laden weiter im Editor, sofern die Messung das hergibt; sonst eigene Schwelle mit Begründung.
4. **Aktivitätsfeed:** `comment.created|updated|deleted|resolved|reopened` in `EVENTS` (z. B. „hat einen Kommentar geschrieben“, „… erledigt“), Link zum Record mit `?comment=<id>` (aus `diff.recordId` und `targetId`), `targetTitle` ist der Record-Titel. Typen-Filter des Feeds kennt die neuen Typen.
5. **Fehlertexte:** `collection_archived`, `space_archived`, `workspace_archived` in `ERROR_TEXTS`; lokale Kopie in `features/comments/comment-errors.ts` auf den gemeinsamen Text umstellen.
6. **Optional, falls billig:** Klick auf einen Link mit leerem `href` (von Milkdown neutralisiertes `javascript:`) im Readonly-Editor lädt die Seite nicht neu.

**Ausdrücklich nicht drin:**
- Backend, Wechsel der Editor-Bibliothek, Web Worker fürs Parsen (falls die Messung das nahelegt: im Bericht als Folgepaket vorschlagen).

## Akzeptanzkriterien

1. Messtabelle im Bericht; für jedes Muster aus dem AP-031-Review öffnet die Dokumentseite in < 200 ms (Fallback) ohne Fehler im ErrorHandler.
2. Ein normaler Body unterhalb der Schwelle lädt weiter im Editor (Test).
3. Fallback zeigt Hinweis, Text und Link zum Raw-View; der Body wird dadurch nicht verändert gespeichert (Test: kein PATCH).
4. Kommentar-Prüfung nutzt denselben Helfer; alle bestehenden Kommentar-Tests grün.
5. Feed zeigt Kommentar-Ereignisse mit Text und Link; Filter nach Typ funktioniert.
6. Fehlertexte für die drei Archiv-Codes vorhanden.
7. `npm run build`, `npm test`, `npm run lint`, `npm run format:check` grün; Gegenproben für die Schutzlogik.

## Entscheidungen, die der Implementierer treffen darf

- Schwellen für den Body (nach Messung), Ort des gemeinsamen Helfers, Darstellung des Fallbacks.

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-08 | angelegt |
