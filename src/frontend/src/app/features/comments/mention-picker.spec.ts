import { findMentions } from './comment-mentions';
import {
  MAX_CANDIDATES,
  filterCandidates,
  insertMention,
  mentionTriggerAt,
} from './mention-picker';

const AGENT = '0190f3a2-7c4e-7b1a-9d2e-3f4a5b6c7d8e';
const members = [
  { actorId: 'h1', name: 'Mia Schulz', kind: 'human' },
  { actorId: AGENT, name: 'Coder', kind: 'agent' },
  { actorId: 'h2', name: 'Admin', kind: 'human' },
  { actorId: 'h3', name: 'Marco', kind: 'human' },
];

describe('mentionPicker', () => {
  it('mentionTriggerAt_AtStartOfWord_ReturnsQueryUpToCaret', () => {
    expect(mentionTriggerAt('@', 1)).toEqual({ start: 0, query: '' });
    expect(mentionTriggerAt('Hallo @Co', 9)).toEqual({ start: 6, query: 'Co' });
    expect(mentionTriggerAt('Hallo @Mia Sch', 14)).toEqual({ start: 6, query: 'Mia Sch' });
    expect(mentionTriggerAt('(@Co', 4)).toEqual({ start: 1, query: 'Co' });
    // Caret in the middle: only the text before it counts.
    expect(mentionTriggerAt('@Coder rest', 3)).toEqual({ start: 0, query: 'Co' });
  });

  it('mentionTriggerAt_EmailLineBreakOrInsertedMention_ReturnsNull', () => {
    expect(mentionTriggerAt('a@b.de', 6)).toBeNull();
    expect(mentionTriggerAt('@Co\nder', 8)).toBeNull();
    expect(mentionTriggerAt(`[@Coder](actor:${AGENT}) `, 46)).toBeNull();
    expect(mentionTriggerAt('@ x', 3)).toBeNull();
    expect(mentionTriggerAt('kein at', 7)).toBeNull();
    expect(mentionTriggerAt('@' + 'x'.repeat(41), 42)).toBeNull();
  });

  it('filterCandidates_PrefixFirstThenContains_CaseInsensitive', () => {
    expect(filterCandidates(members, 'm').map((m) => m.name)).toEqual([
      'Marco',
      'Mia Schulz',
      'Admin',
    ]);
    expect(filterCandidates(members, 'CODE').map((m) => m.name)).toEqual(['Coder']);
    expect(filterCandidates(members, '').map((m) => m.name)).toEqual([
      'Admin',
      'Coder',
      'Marco',
      'Mia Schulz',
    ]);
    expect(filterCandidates(members, 'zz')).toEqual([]);
    const many = Array.from({ length: 20 }, (_, i) => ({
      actorId: `a${i}`,
      name: `A${i}`,
      kind: 'human',
    }));
    expect(filterCandidates(many, 'a')).toHaveLength(MAX_CANDIDATES);
  });

  it('insertMention_ReplacesQueryAtCaret_WithCanonicalLinkAndSpace', () => {
    const text = 'Hallo @Co, bitte prüfen';
    const result = insertMention(text, { start: 6, query: 'Co' }, 9, members[1]);

    expect(result.text).toBe(`Hallo [@Coder](actor:${AGENT}), bitte prüfen`);
    expect(result.caret).toBe(`Hallo [@Coder](actor:${AGENT})`.length);
    expect(findMentions(result.text).actorIds).toEqual([AGENT]);

    const beforeSpace = insertMention('@Co danke', { start: 0, query: 'Co' }, 3, members[1]);
    expect(beforeSpace.text).toBe(`[@Coder](actor:${AGENT}) danke`);
    const atEnd = insertMention('Frage an @Co', { start: 9, query: 'Co' }, 12, members[1]);
    expect(atEnd.text).toBe(`Frage an [@Coder](actor:${AGENT}) `);
    expect(atEnd.caret).toBe(atEnd.text.length);
  });
});
