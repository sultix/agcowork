/** Sample covering every Markdown element the editor must round-trip (AP-012). */
export const PLAYGROUND_MARKDOWN = `# Spielwiese

Ein Absatz mit **fett**, *kursiv*, ~~durchgestrichen~~ und \`Code\`. Dazu ein [Link](https://example.com "Beispiel").

## Listen

- Erster Punkt
- Zweiter Punkt
  - Verschachtelt
    - Noch tiefer
- Dritter Punkt

1. Eins
2. Zwei
   1. Zwei a

- [ ] Offene Aufgabe
- [x] Erledigte Aufgabe

### Code und Zitat

\`\`\`ts
const answer = 42;
\`\`\`

> Markdown ist kanonisch.

| Spalte | Wert |
| ------ | ---: |
| a      |    1 |
| b      |    2 |

---

<b>Rohes HTML wird nicht gerendert.</b>
`;

export const PLAYGROUND_JSON = `{
  "id": "0193a5b2-7c1d-7000-8000-000000000001",
  "title": "Login-Flow überarbeiten",
  "properties": {
    "status": "in_progress",
    "priority": "high",
    "owner": "actor:tech-lead",
    "due": "2026-12-01"
  },
  "revision": 3
}
`;
