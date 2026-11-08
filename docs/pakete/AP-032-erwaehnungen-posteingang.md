# AP-032 – Erwähnungen Frontend: Posteingang, Badge in der Shell, als gelesen markieren

| Feld | Wert |
|---|---|
| Status | angelegt |
| Phase | 2 |
| Implementierer | implementer-sonnet |
| Referenzbeispiel | Liste mit Cursor und URL-Filter: `features/search/search-page.component.ts` (AP-027); Live-Liste: `features/activity/activity-feed.component.ts`; Shell-Elemente und Live-Region: `features/workspace-shell/workspace-shell.component.ts` (Status, Suchfeld) |
| Abhängig von | AP-029 (Endpunkte committet); parallel zu AP-031 möglich (disjunkte Dateien) |
| Betroffene Pfade | neu `src/frontend/src/app/features/mentions/`; geändert `features/workspace-shell/workspace-shell.component.*` (Badge/Link), `workspace-shell.routes.ts` (Route `mentions`) |
| Architektur-Bezug | `02-ARCHITEKTUR.md` §2 „Kommentare und Erwähnungen“ (Posteingang), §6 |

## Ziel

Menschen sehen auf einen Blick, wo sie erwähnt wurden, springen zum Kommentar und arbeiten den Posteingang ab.

## Scope

**Drin:**
- Route `/w/:slug/mentions` mit Liste der eigenen Erwähnungen (neueste zuerst): wer, in welchem Record, Auszug, Zeit, ungelesen hervorgehoben; Filter „Nur ungelesene“ (in der URL), „Mehr laden“ per Cursor.
- Klick öffnet den Record mit geöffneter Kommentar-Seitenleiste und hervorgehobenem Kommentar (Query-Parameter, z. B. `?comment=<id>`; die Seitenleiste aus AP-031 liest ihn – falls AP-031 noch nicht committet ist, nur den Link bauen und im Bericht vermerken) und markiert die Erwähnung als gelesen.
- „Alle als gelesen markieren“.
- Badge in der Shell (Sidebar-Eintrag „Erwähnungen“ mit Anzahl ungelesener, Obergrenze „99+“), sr-only Text; aktualisiert sich live bei `comment.*`-Ereignissen, deren `mentionedActorIds` den eigenen Actor enthalten, und nach „gelesen“.

**Ausdrücklich nicht drin:**
- Backend, Kommentar-Seitenleiste (AP-031), Browser-Benachrichtigungen, workspaceübergreifender Posteingang.

## Fachlicher Kontext

- Vertrag aus AP-029: `GET /api/v1/w/{slug}/mentions?unreadOnly&limit&cursor` → `{ items, nextCursor, unreadCount }`; je Eintrag u. a. `id`, `commentId`, `parentCommentId`, `recordId`, Record-Titel, Auszug (reiner Text, max. 200 Zeichen), `byActorId`, `byActorName`, `createdAt`, `readAt`. `POST /mentions/read` mit `{ ids?, all? }` → `{ marked, unreadCount }`; fremde IDs werden still ignoriert.
- Live: `comment.*`-Ereignisse tragen `mentionedActorIds` im Diff (bei `updated` nur hinzugekommene).

## Akzeptanzkriterien

1. Liste lädt aus der URL, „Nur ungelesene“ filtert, „Mehr laden“ ohne Duplikate.
2. Klick navigiert zum Record mit Kommentar-Parameter und markiert als gelesen; „Alle als gelesen“ leert die Ungelesen-Anzeige.
3. Badge zeigt die Anzahl, steigt live bei einer neuen Erwähnung des eigenen Actors, nicht bei Erwähnungen anderer.
4. Auszug als reiner Text (Markup aus dem Kommentar wird nicht interpretiert).
5. `npm run build`, `npm test`, `npm run lint`, `npm run format:check` grün.

## Tests, die mindestens entstehen

- Unit (Vitest): Liste, Filter ↔ URL, Gelesen-Markierung, Badge mit Live-Ereignissen (eigene vs. fremde Erwähnung).

## Entscheidungen, die der Implementierer treffen darf

- Ob der Badge-Zähler aus `GET /mentions?unreadOnly=true&limit=…` oder einem eigenen Zähler-Feld der Antwort kommt (falls AP-029 eines liefert).

## Verlauf

| Datum | Ereignis |
|---|---|
| 2026-11-08 | angelegt |
