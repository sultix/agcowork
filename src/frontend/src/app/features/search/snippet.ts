/** Server markers around a match in a search snippet (U+27E6, U+27E7). */
export const MATCH_START = '⟦';
export const MATCH_END = '⟧';

export interface SnippetPart {
  readonly text: string;
  readonly match: boolean;
}

/**
 * Splits a search snippet at the server's markers into plain-text parts; `match` parts are the
 * hits. The snippet is plain text, so every part is rendered as text, never as markup.
 *
 * Lenient with broken input: empty matches are dropped, a closing marker without an opening one
 * and a nested opening marker are ignored, and an opening marker that is never closed is
 * ignored too (its text stays plain). Markers never appear in the parts.
 */
export function splitSnippet(snippet: string): SnippetPart[] {
  const parts: SnippetPart[] = [];
  let plain = '';
  let hit: string | null = null;

  const pushPlain = (): void => {
    if (plain !== '') {
      parts.push({ text: plain, match: false });
      plain = '';
    }
  };

  for (const char of snippet) {
    if (char === MATCH_START) {
      if (hit === null) {
        hit = '';
      }
    } else if (char === MATCH_END) {
      if (hit !== null) {
        if (hit !== '') {
          pushPlain();
          parts.push({ text: hit, match: true });
        }
        hit = null;
      }
    } else if (hit !== null) {
      hit += char;
    } else {
      plain += char;
    }
  }
  // Unclosed marker: the text after it is not a hit.
  plain += hit ?? '';
  pushPlain();
  return parts;
}
