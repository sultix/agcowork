import type { components } from '../../core/api/generated/schema';
import {
  FilterNode,
  SortSpec,
  cleanFilter,
  cleanSorts,
  sameFilter,
} from '../../shared/filter/filter-dsl';
import type { PropertyDefinition } from '../../shared/properties/property-types';
import type { TableColumn } from '../../shared/table/data-table.component';
import type { UpdateViewBody, ViewDto } from './collection.store';

type QueryDefinition = components['schemas']['QueryDefinition'];
type QueryViewBody = components['schemas']['QueryViewBody'];

/**
 * What a view shows, as the user works with it: filter, sorting, visible columns (ordered,
 * `[]` = all) and column widths. Starts from the stored view; changes stay in the session until
 * "View speichern" (widths are never stored on the server).
 */
export interface ViewWorkingState {
  readonly filter: FilterNode | null;
  readonly sorts: readonly SortSpec[];
  readonly visible: readonly string[];
  readonly widths: Readonly<Record<string, number>>;
}

/** The stored part of a view in working-state form. */
export function savedState(view: ViewDto): ViewWorkingState {
  return {
    filter: cleanFilter(view.filter),
    sorts: cleanSorts(view.sorts),
    visible: [...view.visibleProperties],
    widths: {},
  };
}

/** Whether filter, sorts or columns differ from what the view stores. */
export function isModified(state: ViewWorkingState, view: ViewDto): boolean {
  const saved = savedState(view);
  return (
    !sameFilter(state.filter, saved.filter) ||
    !sameFilter(state.sorts, saved.sorts) ||
    !sameFilter(state.visible, saved.visible)
  );
}

/** `PATCH /views/{id}` that stores the working state (`{}` / `[]` remove filter and sorts). */
export function toViewPatch(state: ViewWorkingState): UpdateViewBody {
  return {
    filter: (state.filter ?? {}) as UpdateViewBody['filter'],
    sorts: state.sorts.map((s) => ({ prop: s.prop, dir: s.dir })),
    visibleProperties: [...state.visible],
  };
}

/** Schema properties that can be columns: everything but the title property (own column). */
export function columnDefinitions(schema: readonly PropertyDefinition[]): PropertyDefinition[] {
  return schema.filter((d) => d.key !== 'title');
}

/** Columns of the table: the visible keys in their order, or all properties in schema order. */
export function tableColumns(
  schema: readonly PropertyDefinition[],
  visible: readonly string[],
): TableColumn[] {
  const definitions = columnDefinitions(schema);
  if (visible.length === 0) {
    return definitions.map((definition) => ({ key: definition.key, definition }));
  }
  return visible
    .map((key) => definitions.find((d) => d.key === key))
    .filter((d): d is PropertyDefinition => d !== undefined)
    .map((definition) => ({ key: definition.key, definition }));
}

/** A visible list equal to "all in schema order" is stored as `[]`, so new properties appear. */
export function normaliseVisible(
  schema: readonly PropertyDefinition[],
  visible: readonly string[],
): string[] {
  const all = columnDefinitions(schema).map((d) => d.key);
  return sameFilter(all, visible) ? [] : [...visible];
}

/** Shows or hides one column. */
export function toggleColumn(
  schema: readonly PropertyDefinition[],
  visible: readonly string[],
  key: string,
): string[] {
  const current = tableColumns(schema, visible).map((c) => c.key);
  const next = current.includes(key) ? current.filter((k) => k !== key) : [...current, key];
  return normaliseVisible(schema, next);
}

/** Which endpoint answers a view, and with which body. */
export type ViewQuery =
  | { readonly kind: 'view'; readonly viewId: string; readonly body: QueryViewBody }
  | { readonly kind: 'collection'; readonly collectionId: string; readonly body: QueryDefinition };

/**
 * Unchanged views run on the server as stored (`POST /views/{id}/query`). As soon as filter,
 * sorting or columns differ in the session, the working state runs as an ad-hoc query
 * (`POST /collections/{id}/query`), because the view endpoint takes neither sorts nor fields and
 * can only narrow the stored filter. `needAllFields` (board) makes sure the grouping property comes back; `extraField` (gallery cover) is added to the requested fields.
 */
export function viewQuery(
  view: ViewDto,
  state: ViewWorkingState,
  limit: number,
  needAllFields = false,
  extraField: string | null = null,
): ViewQuery {
  const visibleOk =
    (!needAllFields ||
      view.visibleProperties.length === 0 ||
      (view.groupBy !== null && view.visibleProperties.includes(view.groupBy))) &&
    (extraField === null ||
      view.visibleProperties.length === 0 ||
      view.visibleProperties.includes(extraField));
  if (!isModified(state, view) && visibleOk) {
    return { kind: 'view', viewId: view.id, body: { limit, includeCount: true } };
  }
  const fields =
    state.visible.length > 0 && !needAllFields
      ? [
          ...state.visible,
          ...(extraField !== null && !state.visible.includes(extraField) ? [extraField] : []),
        ]
      : null;
  return {
    kind: 'collection',
    collectionId: view.collectionId,
    body: {
      ...(state.filter ? { filter: state.filter as QueryDefinition['filter'] } : {}),
      sorts: state.sorts.map((s) => ({ prop: s.prop, dir: s.dir })),
      ...(fields ? { fields } : {}),
      limit,
      includeArchived: false,
      includeCount: true,
    },
  };
}

const STORAGE_PREFIX = 'ag.view.';

/** Working state of a view kept for the session (`sessionStorage`); `null` when none or unusable. */
export function loadSessionState(viewId: string): ViewWorkingState | null {
  try {
    const raw = sessionStorage.getItem(STORAGE_PREFIX + viewId);
    if (!raw) {
      return null;
    }
    const parsed = JSON.parse(raw) as Partial<ViewWorkingState>;
    return {
      filter: cleanFilter(parsed.filter ?? null),
      sorts: Array.isArray(parsed.sorts) ? cleanSorts(parsed.sorts) : [],
      visible: Array.isArray(parsed.visible)
        ? parsed.visible.filter((k): k is string => typeof k === 'string')
        : [],
      widths:
        parsed.widths && typeof parsed.widths === 'object'
          ? Object.fromEntries(
              Object.entries(parsed.widths).filter(([, w]) => typeof w === 'number'),
            )
          : {},
    };
  } catch {
    return null;
  }
}

export function storeSessionState(viewId: string, state: ViewWorkingState | null): void {
  try {
    if (state) {
      sessionStorage.setItem(STORAGE_PREFIX + viewId, JSON.stringify(state));
    } else {
      sessionStorage.removeItem(STORAGE_PREFIX + viewId);
    }
  } catch {
    // storage full or disabled: the state just lives as long as the page
  }
}
