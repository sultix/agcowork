import { diffLines, diffProperties, diffRevisions, formatValue, sameValue } from './diff';

describe('diffLines', () => {
  it('diffLines_IdenticalTexts_AllLinesSame', () => {
    expect(diffLines('a\nb', 'a\nb').map((l) => l.kind)).toEqual(['same', 'same']);
  });

  it('diffLines_AddedLine_MarksOnlyThatLine', () => {
    expect(diffLines('a\nc', 'a\nb\nc')).toEqual([
      { kind: 'same', text: 'a' },
      { kind: 'add', text: 'b' },
      { kind: 'same', text: 'c' },
    ]);
  });

  it('diffLines_RemovedAndChangedLines_ShowsRemoveBeforeAdd', () => {
    expect(diffLines('a\nb\nc\nd', 'a\nB\nd')).toEqual([
      { kind: 'same', text: 'a' },
      { kind: 'remove', text: 'b' },
      { kind: 'remove', text: 'c' },
      { kind: 'add', text: 'B' },
      { kind: 'same', text: 'd' },
    ]);
  });

  it('diffLines_MovedBlock_UsesLongestCommonSubsequence', () => {
    const result = diffLines('1\n2\n3\n4', '3\n4\n1\n2');
    expect(result.filter((l) => l.kind === 'same')).toHaveLength(2);
    expect(result.filter((l) => l.kind === 'remove')).toHaveLength(2);
    expect(result.filter((l) => l.kind === 'add')).toHaveLength(2);
  });

  it('diffLines_EmptySide_AllAddedOrAllRemoved', () => {
    expect(diffLines('', 'x\ny').map((l) => l.kind)).toEqual(['add', 'add']);
    expect(diffLines('x', '').map((l) => l.kind)).toEqual(['remove']);
    expect(diffLines('', '')).toEqual([]);
  });

  it('diffLines_CrLfLineBreaks_TreatedLikeLf', () => {
    expect(diffLines('a\r\nb', 'a\nb').every((l) => l.kind === 'same')).toBe(true);
  });

  it('diffLines_HugeDistinctTexts_FallsBackToReplacedBlock', () => {
    const a = Array.from({ length: 2500 }, (_, i) => `a${i}`).join('\n');
    const b = Array.from({ length: 2500 }, (_, i) => `b${i}`).join('\n');
    const result = diffLines(a, b);
    expect(result.filter((l) => l.kind === 'remove')).toHaveLength(2500);
    expect(result.filter((l) => l.kind === 'add')).toHaveLength(2500);
  });
});

describe('diffProperties', () => {
  it('diffProperties_AddedChangedRemoved_ClassifiesAndSortsByKey', () => {
    expect(
      diffProperties({ b: 1, c: 'x', d: 'same' }, { a: true, b: 2, d: 'same' }).map((c) => [
        c.key,
        c.kind,
      ]),
    ).toEqual([
      ['a', 'added'],
      ['b', 'changed'],
      ['c', 'removed'],
    ]);
  });

  it('diffProperties_NullEqualsAbsent', () => {
    expect(diffProperties({ a: null }, {})).toEqual([]);
    expect(diffProperties({ a: 1 }, { a: null })[0].kind).toBe('removed');
  });

  it('diffProperties_ArraysAndObjects_ComparedStructurally', () => {
    expect(diffProperties({ t: ['a', 'b'] }, { t: ['a', 'b'] })).toEqual([]);
    expect(diffProperties({ t: ['a', 'b'] }, { t: ['b', 'a'] })).toHaveLength(1);
    expect(sameValue({ x: 1, y: 2 }, { y: 2, x: 1 })).toBe(true);
  });
});

describe('diffRevisions', () => {
  it('diffRevisions_TitlePropertiesAndBody_AllReported', () => {
    const diff = diffRevisions(
      { title: 'A', properties: { s: 'todo' }, body: 'x' },
      { title: 'B', properties: { s: 'done' }, body: 'x\ny' },
    );

    expect(diff.title).toEqual({ old: 'A', new: 'B' });
    expect(diff.properties).toEqual([{ key: 's', kind: 'changed', old: 'todo', new: 'done' }]);
    expect(diff.bodyChanged).toBe(true);
    expect(diff.body.map((l) => l.kind)).toEqual(['same', 'add']);
  });

  it('diffRevisions_Identical_ReportsNothing', () => {
    const content = { title: 'A', properties: { s: 1 }, body: 'x' };
    const diff = diffRevisions(content, { ...content });

    expect(diff.title).toBeNull();
    expect(diff.properties).toEqual([]);
    expect(diff.bodyChanged).toBe(false);
  });
});

describe('formatValue', () => {
  it('formatValue_Values_RenderedForCells', () => {
    expect(formatValue(null)).toBe('');
    expect(formatValue('text')).toBe('text');
    expect(formatValue(['a', 2])).toBe('["a",2]');
    expect(formatValue(3)).toBe('3');
  });
});
