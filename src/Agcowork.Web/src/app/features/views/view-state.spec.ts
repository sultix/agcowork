import { STATUS_DEFINITION, propertyDefinition, viewDto } from '../../core/testing/test-utils';
import {
  isModified,
  loadSessionState,
  normaliseVisible,
  savedState,
  storeSessionState,
  tableColumns,
  toViewPatch,
  toggleColumn,
  viewQuery,
} from './view-state';

const SCHEMA = [
  propertyDefinition({ key: 'title', name: 'Titel', type: 'text' }),
  STATUS_DEFINITION,
  propertyDefinition({ key: 'estimate', type: 'number' }),
  propertyDefinition({ key: 'due', type: 'date' }),
];
const VIEW = viewDto({
  filter: { and: [{ prop: 'status', op: 'eq', value: 'todo', and: null, or: null }] } as never,
  sorts: [{ prop: 'due', dir: 'desc' }],
});

describe('view state', () => {
  it('tableColumns_EmptyVisible_AllButTitleInSchemaOrder_OtherwiseGivenOrder', () => {
    expect(tableColumns(SCHEMA, []).map((c) => c.key)).toEqual(['status', 'estimate', 'due']);
    expect(tableColumns(SCHEMA, ['due', 'gone', 'status']).map((c) => c.key)).toEqual([
      'due',
      'status',
    ]);
  });

  it('toggleColumn_And_normaliseVisible_StoreAllInSchemaOrderAsEmpty', () => {
    expect(toggleColumn(SCHEMA, [], 'estimate')).toEqual(['status', 'due']);
    expect(toggleColumn(SCHEMA, ['status', 'due'], 'estimate')).toEqual([
      'status',
      'due',
      'estimate',
    ]);
    expect(toggleColumn(SCHEMA, ['status', 'due'], 'due')).toEqual(['status']);
    expect(normaliseVisible(SCHEMA, ['status', 'estimate', 'due'])).toEqual([]);
  });

  it('savedState_And_isModified', () => {
    const saved = savedState(VIEW);

    expect(saved.filter).toEqual({ and: [{ prop: 'status', op: 'eq', value: 'todo' }] });
    expect(isModified(saved, VIEW)).toBe(false);
    expect(isModified({ ...saved, widths: { due: 300 } }, VIEW)).toBe(false);
    expect(isModified({ ...saved, sorts: [] }, VIEW)).toBe(true);
    expect(isModified({ ...saved, visible: ['due'] }, VIEW)).toBe(true);
  });

  it('toViewPatch_NoFilter_SendsEmptyObjectAndLists', () => {
    expect(toViewPatch({ filter: null, sorts: [], visible: [], widths: { a: 1 } })).toEqual({
      filter: {},
      sorts: [],
      visibleProperties: [],
    });
  });

  it('viewQuery_Unchanged_UsesViewEndpoint', () => {
    expect(viewQuery(VIEW, savedState(VIEW), 200)).toEqual({
      kind: 'view',
      viewId: VIEW.id,
      body: { limit: 200, includeCount: true },
    });
  });

  it('viewQuery_Changed_UsesCollectionQueryWithFieldsAndSorts', () => {
    const state = {
      ...savedState(VIEW),
      sorts: [{ prop: 'estimate', dir: 'asc' as const }],
      visible: ['due'],
    };

    expect(viewQuery(VIEW, state, 200)).toEqual({
      kind: 'collection',
      collectionId: VIEW.collectionId,
      body: {
        filter: { and: [{ prop: 'status', op: 'eq', value: 'todo' }] },
        sorts: [{ prop: 'estimate', dir: 'asc' }],
        fields: ['due'],
        limit: 200,
        includeArchived: false,
        includeCount: true,
      },
    });
  });

  it('viewQuery_BoardWhoseVisibleLacksGroupBy_AsksForAllFields', () => {
    const board = viewDto({ type: 'board', groupBy: 'status', visibleProperties: ['due'] });

    const query = viewQuery(board, savedState(board), 500, true);
    expect(query.kind).toBe('collection');
    expect('fields' in query.body).toBe(false);
  });

  it('sessionState_RoundTripAndGarbage', () => {
    const state = { ...savedState(VIEW), widths: { due: 250 } };
    storeSessionState('v1', state);
    expect(loadSessionState('v1')).toEqual(state);

    sessionStorage.setItem('ag.view.v2', '{nope');
    expect(loadSessionState('v2')).toBeNull();

    sessionStorage.setItem(
      'ag.view.v3',
      JSON.stringify({
        filter: { and: [{}, { prop: 'due', op: 'is_empty' }, 5] },
        sorts: [{}],
        visible: [1, 'due'],
      }),
    );
    expect(loadSessionState('v3')).toEqual({
      filter: { and: [{ prop: 'due', op: 'is_empty' }] },
      sorts: [],
      visible: ['due'],
      widths: {},
    });

    storeSessionState('v1', null);
    expect(loadSessionState('v1')).toBeNull();
    sessionStorage.clear();
  });
});
