import { splitSnippet } from './snippet';

describe('splitSnippet', () => {
  it('splitSnippet_NoMarker_ReturnsOnePlainPart', () => {
    expect(splitSnippet('just text')).toEqual([{ text: 'just text', match: false }]);
  });

  it('splitSnippet_Empty_ReturnsNoParts', () => {
    expect(splitSnippet('')).toEqual([]);
  });

  it('splitSnippet_MarkerInTheMiddle_SplitsIntoThreeParts', () => {
    expect(splitSnippet('a ⟦hit⟧ b')).toEqual([
      { text: 'a ', match: false },
      { text: 'hit', match: true },
      { text: ' b', match: false },
    ]);
  });

  it('splitSnippet_MarkerAtStartAndEnd_HasNoEmptyParts', () => {
    expect(splitSnippet('⟦one⟧ and ⟦two⟧')).toEqual([
      { text: 'one', match: true },
      { text: ' and ', match: false },
      { text: 'two', match: true },
    ]);
  });

  it('splitSnippet_AdjacentMatches_KeepsBoth', () => {
    expect(splitSnippet('⟦a⟧⟦b⟧')).toEqual([
      { text: 'a', match: true },
      { text: 'b', match: true },
    ]);
  });

  it('splitSnippet_EmptyMatch_IsDropped', () => {
    expect(splitSnippet('a\u27E6\u27E7b')).toEqual([{ text: 'ab', match: false }]);
  });

  it('splitSnippet_ClosingWithoutOpening_IsIgnored', () => {
    expect(splitSnippet('a⟧b')).toEqual([{ text: 'ab', match: false }]);
  });

  it('splitSnippet_OpeningWithoutClosing_KeepsTextPlain', () => {
    expect(splitSnippet('a ⟦b c')).toEqual([{ text: 'a b c', match: false }]);
  });

  it('splitSnippet_NestedOpening_IgnoresTheInnerOne', () => {
    expect(splitSnippet('⟦a⟦b⟧')).toEqual([{ text: 'ab', match: true }]);
  });

  it('splitSnippet_Markup_StaysText', () => {
    expect(splitSnippet('<img src=x onerror=alert(1)> ⟦<b>⟧')).toEqual([
      { text: '<img src=x onerror=alert(1)> ', match: false },
      { text: '<b>', match: true },
    ]);
  });
});
