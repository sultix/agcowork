import { STATUS_DEFINITION, propertyDefinition } from '../../../core/testing/test-utils';
import {
  allowedTypes,
  describeLoss,
  draftOf,
  emptyDraft,
  OPTION_REMOVED_ELSEWHERE,
  lossFilter,
  lossesOf,
  rebaseDraft,
  suggestKey,
  toChangeInput,
  toDefinitionInput,
} from './schema-draft';

const SELECT = propertyDefinition({
  key: 'priority',
  name: 'Priorität',
  type: 'select',
  options: [
    { value: 'high', label: 'Hoch', color: 'red', group: null },
    { value: 'low', label: 'Niedrig', color: null, group: null },
  ],
});
const NUMBER = propertyDefinition({
  key: 'estimate',
  name: 'Schätzung',
  type: 'number',
  precision: 2,
});

describe('suggestKey', () => {
  it.each([
    ['Fälligkeit am', 'faelligkeit_am'],
    ['  Größe (cm) ', 'groesse_cm'],
    ['Résumé', 'resume'],
    ['2. Prüfer', 'p_2_pruefer'],
    ['', ''],
    ['!!!', ''],
  ])('suggestKey_%s_IsSnakeCase', (name, key) => {
    expect(suggestKey(name, [])).toBe(key);
  });

  it('suggestKey_ReservedOrTaken_AddsSuffix', () => {
    expect(suggestKey('Body', [])).toBe('body_2');
    expect(suggestKey('Titel', [])).toBe('titel');
    expect(suggestKey('Title', [])).toBe('title_2');
    expect(suggestKey('Status', ['status', 'status_2'])).toBe('status_3');
  });

  it('suggestKey_LongName_StaysWithinPatternLength', () => {
    const key = suggestKey('a'.repeat(100), ['a'.repeat(63)]);
    expect(key).toMatch(/^[a-z][a-z0-9_]{0,62}$/);
    expect(key.endsWith('_2')).toBe(true);
  });
});

describe('allowedTypes', () => {
  it('allowedTypes_PerType_OnlyApiChanges', () => {
    expect(allowedTypes('text')).toEqual(['text', 'markdown']);
    expect(allowedTypes('markdown')).toEqual(['markdown', 'text']);
    expect(allowedTypes('select')).toEqual(['select', 'multi_select']);
    expect(allowedTypes('multi_select')).toEqual(['multi_select']);
    expect(allowedTypes('relation')).toEqual(['relation']);
  });
});

describe('toDefinitionInput', () => {
  it('toDefinitionInput_Status_SendsOptionsWithGroupsAndLabelFallback', () => {
    const input = toDefinitionInput({
      ...emptyDraft(),
      key: 'phase',
      name: ' Phase ',
      type: 'status',
      description: '  ',
      options: [
        { value: ' open ', label: '', color: 'blue', group: 'todo', existing: false },
        { value: 'done', label: 'Fertig', color: '', group: 'done', existing: false },
      ],
      unit: 'ignored',
      precision: 3,
      default: 'open',
    });

    expect(input).toEqual({
      key: 'phase',
      name: 'Phase',
      type: 'status',
      required: false,
      options: [
        { value: 'open', label: 'open', color: 'blue', group: 'todo' },
        { value: 'done', label: 'Fertig', color: null, group: 'done' },
      ],
      default: 'open',
    });
  });

  it('toDefinitionInput_Relation_SendsTargetAndMultipleWithoutDefault', () => {
    const input = toDefinitionInput({
      ...emptyDraft(),
      key: 'blocks',
      name: 'Blockiert',
      type: 'relation',
      targetCollectionId: 'c1',
      multiple: true,
      default: 'x',
    });

    expect(input).toEqual({
      key: 'blocks',
      name: 'Blockiert',
      type: 'relation',
      required: false,
      targetCollectionId: 'c1',
      multiple: true,
    });
  });

  it('toDefinitionInput_ComputedRequired_SendsNotRequired', () => {
    const input = toDefinitionInput({
      ...emptyDraft(),
      key: 'c',
      name: 'C',
      type: 'created_at',
      required: true,
    });
    expect(input.required).toBe(false);
  });
});

describe('toChangeInput', () => {
  it('toChangeInput_Unchanged_IsNull', () => {
    expect(toChangeInput(SELECT, draftOf(SELECT))).toBeNull();
    expect(toChangeInput(STATUS_DEFINITION, draftOf(STATUS_DEFINITION))).toBeNull();
    expect(toChangeInput(NUMBER, draftOf(NUMBER))).toBeNull();
  });

  it('toChangeInput_NameAndClearedDescription_SendsOnlyThose', () => {
    const original = { ...SELECT, description: 'Wie dringend' };
    const change = toChangeInput(original, {
      ...draftOf(original),
      name: 'Dringlichkeit ',
      description: '',
    });

    expect(change).toEqual({ key: 'priority', name: 'Dringlichkeit', description: '' });
  });

  it('toChangeInput_OptionRelabeledAndAdded_SendsWholeList', () => {
    const draft = draftOf(SELECT);
    draft.options[0] = { ...draft.options[0], label: 'Sehr hoch' };
    draft.options.push({ value: 'mid', label: '', color: 'yellow', group: '', existing: false });

    expect(toChangeInput(SELECT, draft)).toEqual({
      key: 'priority',
      options: [
        { value: 'high', label: 'Sehr hoch', color: 'red', group: null },
        { value: 'low', label: 'Niedrig', color: null, group: null },
        { value: 'mid', label: 'mid', color: 'yellow', group: null },
      ],
    });
  });

  it('toChangeInput_TypeAndDefault_SendsTypeAndClearDefault', () => {
    const original = { ...SELECT, default: 'high' as never };
    const change = toChangeInput(original, {
      ...draftOf(original),
      type: 'multi_select',
      default: null,
    });

    expect(change).toEqual({ key: 'priority', type: 'multi_select', clearDefault: true });
  });

  it('toChangeInput_RelationMultiple_SendsOnlyWidening', () => {
    const relation = propertyDefinition({ key: 'rel', type: 'relation', targetCollectionId: 'c1' });
    expect(toChangeInput(relation, { ...draftOf(relation), multiple: true })).toEqual({
      key: 'rel',
      multiple: true,
    });
  });

  it('toChangeInput_NumberUnitClearedAndPrecision_SendsBoth', () => {
    const original = { ...NUMBER, unit: 'h' };
    expect(toChangeInput(original, { ...draftOf(original), unit: '', precision: 0 })).toEqual({
      key: 'estimate',
      unit: '',
      precision: 0,
    });
  });
});

describe('lossesOf', () => {
  it('lossesOf_RemovedOption_ListsValue', () => {
    const losses = lossesOf(SELECT, { key: 'priority', options: [{ value: 'high' }] });
    expect(losses).toEqual([{ kind: 'options', property: SELECT, values: ['low'] }]);
  });

  it('lossesOf_PrecisionLoweredOrIntroduced_IsLoss', () => {
    expect(lossesOf(NUMBER, { key: 'estimate', precision: 1 })).toEqual([
      { kind: 'precision', property: NUMBER, precision: 1 },
    ]);
    expect(lossesOf(NUMBER, { key: 'estimate', precision: 3 })).toEqual([]);
    const free = { ...NUMBER, precision: null };
    expect(lossesOf(free, { key: 'estimate', precision: 4 })).toHaveLength(1);
  });

  it('lossesOf_MarkdownToText_IsLossButNotTheOtherWay', () => {
    const markdown = propertyDefinition({ key: 'notes', type: 'markdown' });
    const text = propertyDefinition({ key: 'notes', type: 'text' });
    expect(lossesOf(markdown, { key: 'notes', type: 'text' })).toEqual([
      { kind: 'markdown_to_text', property: markdown },
    ]);
    expect(lossesOf(text, { key: 'notes', type: 'markdown' })).toEqual([]);
  });

  it('lossesOf_SelectToMultiSelectAndRename_NoLoss', () => {
    expect(lossesOf(SELECT, { key: 'priority', type: 'multi_select', name: 'X' })).toEqual([]);
  });
});

describe('lossFilter and describeLoss', () => {
  it('lossFilter_PerKind_CountsAffectedRecords', () => {
    expect(lossFilter({ kind: 'removed', property: SELECT })).toEqual({
      not: { prop: 'priority', op: 'is_empty' },
    });
    expect(lossFilter({ kind: 'options', property: SELECT, values: ['low'] })).toEqual({
      prop: 'priority',
      op: 'in',
      value: ['low'],
    });
    expect(
      lossFilter({ kind: 'removed', property: propertyDefinition({ key: 'j', type: 'json' }) }),
    ).toBeNull();
    expect(
      lossFilter({
        kind: 'removed',
        property: propertyDefinition({ key: 'c', type: 'created_at' }),
      }),
    ).toBeNull();
  });

  it('describeLoss_Counts_NamesRecords', () => {
    const removed = { kind: 'removed', property: NUMBER } as const;
    expect(describeLoss(removed, 12)).toBe(
      '„Schätzung“ wird entfernt. Werte in 12 Records gehen verloren.',
    );
    expect(describeLoss(removed, 1)).toContain('Werte in 1 Record gehen verloren.');
    expect(describeLoss(removed, 0)).toContain('Kein Record hat dort einen Wert.');
    expect(describeLoss(removed, null)).toContain('Gespeicherte Werte gehen verloren.');
    expect(describeLoss(removed, undefined)).toContain('werden gezählt');
    expect(describeLoss(removed, 10000)).toContain('mindestens 10.000 Records');
    expect(
      describeLoss(
        {
          kind: 'removed',
          property: propertyDefinition({ key: 'c', name: 'C', type: 'created_at' }),
        },
        null,
      ),
    ).toBe(
      '„C“ wird entfernt. Das System berechnet diese Werte nur; gespeichert ist nichts, es geht kein Wert verloren.',
    );
    expect(describeLoss({ kind: 'options', property: SELECT, values: ['low'] }, 3)).toBe(
      'Die Option „low“ von „Priorität“ wird entfernt. Werte in 3 Records gehen verloren.',
    );
  });
});

describe('rebaseDraft', () => {
  it('rebaseDraft_UntouchedFieldsFollowServer_ChangedFieldsStay', () => {
    const base = { ...SELECT, description: 'alt' };
    const current = {
      ...SELECT,
      name: 'Dringlichkeit',
      description: 'neu von anderen',
      options: [
        ...(SELECT.options ?? []),
        { value: 'mid', label: 'Mittel', color: null, group: null },
      ],
    };
    const draft = { ...draftOf(base), description: 'meine Beschreibung' };

    const rebased = rebaseDraft(draft, base, current);

    expect(rebased.name).toBe('Dringlichkeit');
    expect(rebased.description).toBe('meine Beschreibung');
    expect(rebased.options.map((o) => o.value)).toEqual(['high', 'low', 'mid']);
    expect(rebased.options.every((o) => o.existing)).toBe(true);
  });

  it('rebaseDraft_UserAddedOptionWhileOtherRemovedUntouchedOne_RemovalStaysWithoutLoss', () => {
    const current = { ...SELECT, options: [SELECT.options![0]] };
    const draft = draftOf(SELECT);
    draft.options.push({ value: 'mid', label: '', color: '', group: '', existing: false });

    const rebased = rebaseDraft(draft, SELECT, current);

    expect(rebased.options.map((o) => [o.value, o.existing, o.conflict])).toEqual([
      ['high', true, undefined],
      ['mid', false, undefined],
    ]);
    const change = toChangeInput(current, rebased)!;
    expect(lossesOf(current, change)).toEqual([]);
  });

  it('rebaseDraft_ConcurrentOptionChanges_MergeOptionByOption', () => {
    // B: high relabeled to "Sehr hoch", mid added, low removed; A: color of low changed
    const current = {
      ...SELECT,
      options: [
        { value: 'high', label: 'Sehr hoch', color: 'red', group: null },
        { value: 'mid', label: 'Mittel', color: 'yellow', group: null },
      ],
    };
    const draft = draftOf(SELECT);
    draft.options[1] = { ...draft.options[1], color: 'green' };

    const rebased = rebaseDraft(draft, SELECT, current);

    expect(rebased.options).toEqual([
      { value: 'high', label: 'Sehr hoch', color: 'red', group: '', existing: true },
      { value: 'mid', label: 'Mittel', color: 'yellow', group: '', existing: true },
      {
        value: 'low',
        label: 'Niedrig',
        color: 'green',
        group: '',
        existing: false,
        conflict: OPTION_REMOVED_ELSEWHERE,
      },
    ]);
    // nothing of B's change is lost: low comes back as a new option, no confirmation needed
    const change = toChangeInput(current, rebased)!;
    expect(change.options).toEqual([
      { value: 'high', label: 'Sehr hoch', color: 'red', group: null },
      { value: 'mid', label: 'Mittel', color: 'yellow', group: null },
      { value: 'low', label: 'Niedrig', color: 'green', group: null },
    ]);
    expect(lossesOf(current, change)).toEqual([]);
  });

  it('rebaseDraft_SameOptionChangedOnBothSides_EachFieldKeepsItsChanger', () => {
    const current = {
      ...SELECT,
      options: [
        { value: 'high', label: 'Sehr hoch', color: 'red', group: null },
        SELECT.options![1],
      ],
    };
    const draft = draftOf(SELECT);
    draft.options[0] = { ...draft.options[0], color: 'orange' };

    const rebased = rebaseDraft(draft, SELECT, current);

    expect(rebased.options[0]).toMatchObject({
      value: 'high',
      label: 'Sehr hoch',
      color: 'orange',
    });
  });

  it('rebaseDraft_UserRemovedOptionOtherRelabeled_RemovalStays', () => {
    const current = {
      ...SELECT,
      options: [SELECT.options![0], { value: 'low', label: 'Gering', color: null, group: null }],
    };
    const draft = draftOf(SELECT);
    draft.options.splice(1, 1);

    const rebased = rebaseDraft(draft, SELECT, current);

    expect(rebased.options.map((o) => o.value)).toEqual(['high']);
  });
});
