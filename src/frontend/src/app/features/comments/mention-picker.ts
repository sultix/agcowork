import { mentionLink } from './comment-mentions';

/** An `@` being typed: where it starts and what follows it up to the caret. */
export interface MentionTrigger {
  /** Index of the `@`. */
  readonly start: number;
  /** Text between `@` and the caret. */
  readonly query: string;
}

/** Longest text after `@` that still counts as a name being typed. */
const MAX_QUERY = 40;

/**
 * The `@` the caret is typing a name after, or `null`. The `@` must start a word (start of text,
 * after whitespace or an opening bracket or quote), so e-mail addresses and the `[@Name]` of an
 * inserted mention open nothing; the name runs to the caret without line breaks or brackets.
 */
export function mentionTriggerAt(text: string, caret: number): MentionTrigger | null {
  const before = text.slice(0, caret);
  const at = before.lastIndexOf('@');
  if (at < 0) {
    return null;
  }
  const previous = at === 0 ? '' : before[at - 1];
  if (previous !== '' && !/[\s("'„“]/.test(previous)) {
    return null;
  }
  const query = before.slice(at + 1);
  if (query.length > MAX_QUERY || /[\n\r[\]()]/.test(query) || /^\s/.test(query)) {
    return null;
  }
  return { start: at, query };
}

export interface MentionCandidate {
  readonly actorId: string;
  readonly name: string;
  readonly kind: string;
}

/** Most entries the picker lists. */
export const MAX_CANDIDATES = 8;

/**
 * Members whose name contains `query` (case-insensitive): names starting with it first, then
 * the rest, each group alphabetical; at most {@link MAX_CANDIDATES}.
 */
export function filterCandidates(
  members: readonly MentionCandidate[],
  query: string,
): MentionCandidate[] {
  const needle = query.trim().toLocaleLowerCase('de');
  const scored = members
    .map((m) => ({ m, name: m.name.toLocaleLowerCase('de') }))
    .filter(({ name }) => name.includes(needle))
    .sort(
      (a, b) =>
        Number(!a.name.startsWith(needle)) - Number(!b.name.startsWith(needle)) ||
        a.m.name.localeCompare(b.m.name, 'de'),
    );
  return scored.slice(0, MAX_CANDIDATES).map(({ m }) => m);
}

/**
 * Replaces the typed `@query` with the canonical mention `[@Name](actor:<id>)` and a space
 * (none before whitespace or punctuation);
 * returns the new text and the caret position after the insertion.
 */
export function insertMention(
  text: string,
  trigger: MentionTrigger,
  caret: number,
  candidate: MentionCandidate,
): { text: string; caret: number } {
  const after = text.slice(caret);
  const link = mentionLink(candidate.name, candidate.actorId);
  const inserted = /^[\s.,;:!?)]/.test(after) ? link : `${link} `;
  return {
    text: text.slice(0, trigger.start) + inserted + after,
    caret: trigger.start + inserted.length,
  };
}
