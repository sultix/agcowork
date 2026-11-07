import { propertyDefinition } from '../../core/testing/test-utils';
import {
  FieldType,
  FilterNode,
  OPERATOR_MATRIX,
  andFilters,
  cleanFilter,
  convertValue,
  countConditions,
  emptyCondition,
  filterFields,
  isSortable,
  operatorsFor,
  toBuilder,
  toDsl,
} from './filter-dsl';

const SCHEMA = [
  propertyDefinition({ key: 'title', name: 'Titel', type: 'text', required: true }),
  propertyDefinition({
    key: 'status',
    name: 'Status',
    type: 'status',
    options: [
      { value: 'open', label: 'Offen', color: null, group: 'todo' },
      { value: 'in_progress', label: 'In Arbeit', color: null, group: 'in_progress' },
    ],
  }),
  propertyDefinition({ key: 'due', name: 'Fällig', type: 'date' }),
  propertyDefinition({ key: 'payload', name: 'Payload', type: 'json' }),
  propertyDefinition({ key: 'created', name: 'Angelegt', type: 'created_at', readOnly: true }),
];

describe('filter DSL', () => {
  // Architecture §3, "Typ-Matrix (verbindlich, aus AP-009)".
  it.each<[FieldType, string]>([
    ['text', 'eq neq contains starts_with in nin is_empty is_not_empty'],
    ['markdown', 'eq neq contains starts_with in nin is_empty is_not_empty'],
    ['url', 'eq neq contains starts_with in nin is_empty is_not_empty'],
    ['email', 'eq neq contains starts_with in nin is_empty is_not_empty'],
    ['title', 'eq neq contains starts_with in nin is_empty is_not_empty'],
    ['number', 'eq neq gt gte lt lte in nin is_empty is_not_empty'],
    ['checkbox', 'eq neq is_empty'],
    ['select', 'eq neq in nin is_empty is_not_empty'],
    ['status', 'eq neq in nin is_empty is_not_empty'],
    ['multi_select', 'contains in nin is_empty is_not_empty'],
    ['date', 'eq neq gt gte lt lte is_empty is_not_empty'],
    ['datetime', 'eq neq gt gte lt lte is_empty is_not_empty'],
    ['created_at', 'eq neq gt gte lt lte is_empty is_not_empty'],
    ['updated_at', 'eq neq gt gte lt lte is_empty is_not_empty'],
    ['relation', 'contains in nin is_empty is_not_empty'],
    ['actor', 'eq neq in nin is_empty is_not_empty'],
    ['created_by', 'eq neq in nin is_empty is_not_empty'],
    ['updated_by', 'eq neq in nin is_empty is_not_empty'],
    ['json', ''],
  ])('operatorsFor_%s_MatchesTypeMatrix', (type, expected) => {
    expect([...operatorsFor(type)].sort()).toEqual(expected.split(' ').filter(Boolean).sort());
  });

  it('operatorMatrix_CoversEveryPropertyType', () => {
    expect(Object.keys(OPERATOR_MATRIX)).toHaveLength(19);
  });

  it.each<[FieldType, boolean]>([
    ['multi_select', false],
    ['relation', false],
    ['json', false],
    ['number', true],
    ['title', true],
  ])('isSortable_%s_%s', (type, sortable) => {
    expect(isSortable(type)).toBe(sortable);
  });

  it('filterFields_Schema_DropsJsonMapsTitleAndAddsUnshadowedPseudoFields', () => {
    const fields = filterFields(SCHEMA);

    expect(fields.map((f) => `${f.key}:${f.type}`)).toEqual([
      'title:title',
      'status:status',
      'due:date',
      'created:created_at',
      'created_at:created_at',
      'updated_at:updated_at',
      'created_by:created_by',
      'updated_by:updated_by',
    ]);
  });

  it('cleanFilter_ServerShapeWithNulls_ReturnsPlainDsl', () => {
    const raw = {
      and: [
        { and: null, or: null, not: null, prop: 'status', op: 'eq', value: 'open' },
        { and: null, or: null, not: null, prop: 'due', op: 'is_empty', value: null },
      ],
      or: null,
      not: null,
      prop: null,
      op: null,
      value: null,
    };

    expect(cleanFilter(raw)).toEqual({
      and: [
        { prop: 'status', op: 'eq', value: 'open' },
        { prop: 'due', op: 'is_empty' },
      ],
    });
    expect(cleanFilter({})).toBeNull();
    expect(cleanFilter(null)).toBeNull();
  });

  it('cleanFilter_UnknownChildren_AreDroppedNotReplaced', () => {
    expect(
      cleanFilter({
        and: [
          {},
          'x',
          null,
          { prop: 'due', op: 'is_empty' },
          { prop: 'a', op: 'bogus' },
          { or: [{}] },
        ],
      }),
    ).toEqual({ and: [{ prop: 'due', op: 'is_empty' }] });
    expect(cleanFilter({ and: [{}] })).toBeNull();
    expect(cleanFilter({ not: {} })).toBeNull();
  });

  it('countConditionsAndToBuilder_MalformedNodes_DoNotThrow', () => {
    const malformed = { and: [{}, { prop: 'due', op: 'is_empty' }] } as unknown as FilterNode;

    expect(countConditions({} as FilterNode)).toBe(0);
    expect(countConditions(malformed)).toBe(1);
    expect(toBuilder({} as FilterNode)).toEqual({ kind: 'and', items: [] });
    expect(toDsl(toBuilder(malformed)!)).toEqual({ and: [{ prop: 'due', op: 'is_empty' }] });
  });

  it('toDsl_AcceptanceFilter_ProducesArchitectureShape', () => {
    // status in [open, in_progress] and (due < date or priority = high)
    const state = toBuilder({
      and: [
        { prop: 'status', op: 'in', value: ['open', 'in_progress'] },
        {
          or: [
            { prop: 'due', op: 'lt', value: '2026-12-01' },
            { prop: 'priority', op: 'eq', value: 'high' },
          ],
        },
      ],
    })!;

    expect(JSON.stringify(toDsl(state))).toBe(
      '{"and":[{"prop":"status","op":"in","value":["open","in_progress"]},' +
        '{"or":[{"prop":"due","op":"lt","value":"2026-12-01"},{"prop":"priority","op":"eq","value":"high"}]}]}',
    );
  });

  it.each<[string, FilterNode]>([
    ['single condition group', { and: [{ prop: 'status', op: 'eq', value: 'open' }] }],
    [
      'or root',
      {
        or: [
          { prop: 'due', op: 'is_empty' },
          { prop: 'title', op: 'contains', value: 'x' },
        ],
      },
    ],
    [
      'nested group',
      {
        and: [
          { prop: 'status', op: 'nin', value: ['open'] },
          {
            or: [
              { prop: 'due', op: 'gte', value: '2026-01-01' },
              { prop: 'due', op: 'is_empty' },
            ],
          },
        ],
      },
    ],
    ['checkbox false', { and: [{ prop: 'done', op: 'eq', value: false }] }],
    ['number zero', { and: [{ prop: 'estimate', op: 'gt', value: 0 }] }],
  ])('roundTrip_%s_DslBuilderDslIdentical', (_, dsl) => {
    expect(JSON.stringify(toDsl(toBuilder(dsl)!))).toBe(JSON.stringify(dsl));
  });

  it('toBuilder_BareCondition_BecomesAndGroup', () => {
    const state = toBuilder({ prop: 'status', op: 'eq', value: 'open' })!;

    expect(toDsl(state)).toEqual({ and: [{ prop: 'status', op: 'eq', value: 'open' }] });
  });

  it.each<[string, FilterNode]>([
    ['not', { not: { prop: 'status', op: 'eq', value: 'open' } }],
    ['not inside', { and: [{ not: { prop: 'status', op: 'eq', value: 'open' } }] }],
    ['two levels', { and: [{ or: [{ and: [{ prop: 'due', op: 'is_empty' }] }] }] }],
  ])('toBuilder_%s_IsNotRepresentable', (_, dsl) => {
    expect(toBuilder(dsl)).toBeNull();
  });

  it('toDsl_IncompleteOrUnfittingConditions_AreLeftOut', () => {
    const fields = filterFields(SCHEMA);
    const state = {
      kind: 'and' as const,
      items: [
        emptyCondition(),
        { ...emptyCondition(), prop: 'status', op: 'in' as const, value: [] },
        { ...emptyCondition(), prop: 'due', op: 'contains' as const, value: 'x' },
        { ...emptyCondition(), prop: 'title', op: 'eq' as const, value: '' },
        { ...emptyCondition(), prop: 'due', op: 'is_not_empty' as const, value: 'stale' },
      ],
    };

    expect(toDsl(state, fields)).toEqual({ and: [{ prop: 'due', op: 'is_not_empty' }] });
    expect(toDsl({ kind: 'and', items: [emptyCondition()] })).toBeNull();
  });

  it.each<[unknown, string, string, unknown]>([
    ['open', 'eq', 'in', ['open']],
    [['open', 'done'], 'in', 'eq', 'open'],
    ['open', 'eq', 'is_empty', null],
    [null, 'is_empty', 'eq', null],
    ['x', 'eq', 'contains', 'x'],
  ])('convertValue_%j_from_%s_to_%s_Gives_%j', (value, from, to, expected) => {
    expect(convertValue(value, from as never, to as never)).toEqual(expected);
  });

  it('countConditions_And_andFilters', () => {
    const a: FilterNode = {
      and: [{ prop: 'x', op: 'is_empty' }, { or: [{ prop: 'y', op: 'is_empty' }] }],
    };

    expect(countConditions(a)).toBe(2);
    expect(countConditions(null)).toBe(0);
    expect(andFilters(a, null)).toBe(a);
    expect(andFilters(a, a)).toEqual({ and: [a, a] });
  });
});
