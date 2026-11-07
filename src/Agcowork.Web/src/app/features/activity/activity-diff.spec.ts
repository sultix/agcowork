import { hasDetails, viewDiff } from './activity-diff';

describe('viewDiff', () => {
  it('viewDiff_RecordUpdate_SplitsTitlePropertiesAndBodyDelta', () => {
    const view = viewDiff({
      title: { old: 'A', new: 'B' },
      properties: { status: { old: 'todo', new: 'done' }, owner: { old: null, new: 'x' } },
      bodyChanged: true,
      bodyDelta: { added: 12, removed: 3 },
    });

    expect(view.changes.map((c) => c.key)).toEqual(['title', 'status', 'owner']);
    expect(view.changes[1]).toEqual({ key: 'status', old: 'todo', new: 'done' });
    expect(view.body).toEqual({ added: 12, removed: 3 });
    expect(hasDetails(view)).toBe(true);
  });

  it('viewDiff_SchemaChange_ExposesAffectedRecordsAndExtras', () => {
    const view = viewDiff({ added: ['a'], affectedRecords: 7, cleanedKeys: ['b'] });

    expect(view.affectedRecords).toBe(7);
    expect(view.extras).toEqual([
      { key: 'added', text: '["a"]' },
      { key: 'cleanedKeys', text: '["b"]' },
    ]);
  });

  it('viewDiff_EmptyOrInvalid_HasNoDetails', () => {
    expect(hasDetails(viewDiff({}))).toBe(false);
    expect(hasDetails(viewDiff(null))).toBe(false);
  });
});
