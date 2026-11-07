import type { PropertyDefinition, PropertyType } from '../properties/property-types';

// ---------------------------------------------------------------------------------------------
// The filter DSL of architecture §3, as REST, MCP and stored views use it, plus the builder
// model the filter bar edits. The type matrix mirrors the server's `OperatorMatrix` (AP-009).
// ---------------------------------------------------------------------------------------------

export const FILTER_OPERATORS = [
  'eq',
  'neq',
  'in',
  'nin',
  'contains',
  'starts_with',
  'gt',
  'gte',
  'lt',
  'lte',
  'is_empty',
  'is_not_empty',
] as const;

export type FilterOp = (typeof FILTER_OPERATORS)[number];

/** A condition `{ prop, op, value }`; `value` is omitted for `is_empty`/`is_not_empty`. */
export interface FilterCondition {
  readonly prop: string;
  readonly op: FilterOp;
  readonly value?: unknown;
}

/** One node of the DSL: a group, a negation or a condition. */
export type FilterNode =
  | { readonly and: readonly FilterNode[] }
  | { readonly or: readonly FilterNode[] }
  | { readonly not: FilterNode }
  | FilterCondition;

/** One sort criterion `{ prop, dir }`. */
export interface SortSpec {
  readonly prop: string;
  readonly dir: 'asc' | 'desc';
}

/** Field types of the matrix: the property types plus the pseudo-field `title`. */
export type FieldType = PropertyType | 'title';

const TEXT_OPS: readonly FilterOp[] = [
  'eq',
  'neq',
  'contains',
  'starts_with',
  'in',
  'nin',
  'is_empty',
  'is_not_empty',
];
const ORDERED_OPS: readonly FilterOp[] = [
  'eq',
  'neq',
  'gt',
  'gte',
  'lt',
  'lte',
  'is_empty',
  'is_not_empty',
];
const CHOICE_OPS: readonly FilterOp[] = ['eq', 'neq', 'in', 'nin', 'is_empty', 'is_not_empty'];
const SET_OPS: readonly FilterOp[] = ['contains', 'in', 'nin', 'is_empty', 'is_not_empty'];

/** Operators allowed per field type (architecture §3, "Typ-Matrix"). `json` is not filterable. */
export const OPERATOR_MATRIX: Readonly<Record<FieldType, readonly FilterOp[]>> = {
  text: TEXT_OPS,
  markdown: TEXT_OPS,
  url: TEXT_OPS,
  email: TEXT_OPS,
  title: TEXT_OPS,
  number: ['eq', 'neq', 'gt', 'gte', 'lt', 'lte', 'in', 'nin', 'is_empty', 'is_not_empty'],
  checkbox: ['eq', 'neq', 'is_empty'],
  select: CHOICE_OPS,
  status: CHOICE_OPS,
  multi_select: SET_OPS,
  date: ORDERED_OPS,
  datetime: ORDERED_OPS,
  created_at: ORDERED_OPS,
  updated_at: ORDERED_OPS,
  relation: SET_OPS,
  actor: CHOICE_OPS,
  created_by: CHOICE_OPS,
  updated_by: CHOICE_OPS,
  json: [],
};

/** Types that cannot be sorted by (architecture §3). */
export const UNSORTABLE_TYPES: readonly FieldType[] = ['multi_select', 'relation', 'json'];

export const OPERATOR_LABELS: Readonly<Record<FilterOp, string>> = {
  eq: 'ist',
  neq: 'ist nicht',
  in: 'ist eines von',
  nin: 'ist keines von',
  contains: 'enthält',
  starts_with: 'beginnt mit',
  gt: 'größer als',
  gte: 'größer/gleich',
  lt: 'kleiner als',
  lte: 'kleiner/gleich',
  is_empty: 'ist leer',
  is_not_empty: 'ist nicht leer',
};

/** How many values an operator takes. */
export type ValueArity = 'none' | 'one' | 'many';

export function arityOf(op: FilterOp | ''): ValueArity {
  if (op === 'is_empty' || op === 'is_not_empty') {
    return 'none';
  }
  return op === 'in' || op === 'nin' ? 'many' : 'one';
}

/** A field a filter or sort may refer to: a schema property or a pseudo-field. */
export interface FilterField {
  readonly key: string;
  readonly name: string;
  readonly type: FieldType;
  /** Schema definition; for pseudo-fields a synthetic one (no options). */
  readonly definition: PropertyDefinition;
}

function syntheticDefinition(key: string, name: string, type: PropertyType): PropertyDefinition {
  return {
    key,
    name,
    type,
    description: null,
    required: false,
    readOnly: true,
    options: null,
    targetCollectionId: null,
    multiple: false,
    default: null,
    unit: null,
    precision: null,
  };
}

const PSEUDO_FIELDS: readonly { key: string; name: string; type: FieldType; as: PropertyType }[] = [
  { key: 'title', name: 'Titel', type: 'title', as: 'text' },
  { key: 'created_at', name: 'Erstellt am', type: 'created_at', as: 'created_at' },
  { key: 'updated_at', name: 'Geändert am', type: 'updated_at', as: 'updated_at' },
  { key: 'created_by', name: 'Erstellt von', type: 'created_by', as: 'created_by' },
  { key: 'updated_by', name: 'Geändert von', type: 'updated_by', as: 'updated_by' },
];

/**
 * Fields of a collection a filter can use: the schema keys (without `json`), then the
 * pseudo-fields the schema does not shadow (a schema key of the same name wins, as on the server).
 * The title property (`title`) keeps the pseudo-field's type: it maps onto the title column.
 */
export function filterFields(schema: readonly PropertyDefinition[]): FilterField[] {
  const fields: FilterField[] = schema
    .filter((d) => d.type !== 'json')
    .map((d) => ({
      key: d.key,
      name: d.name,
      type: d.key === 'title' ? 'title' : d.type,
      definition: d,
    }));
  for (const pseudo of PSEUDO_FIELDS) {
    if (!schema.some((d) => d.key === pseudo.key)) {
      fields.push({
        key: pseudo.key,
        name: pseudo.name,
        type: pseudo.type,
        definition: syntheticDefinition(pseudo.key, pseudo.name, pseudo.as),
      });
    }
  }
  return fields;
}

export function operatorsFor(type: FieldType | undefined): readonly FilterOp[] {
  return type ? OPERATOR_MATRIX[type] : [];
}

export function isSortable(type: FieldType): boolean {
  return !UNSORTABLE_TYPES.includes(type);
}

// ---------------------------------------------------------------------------------------------
// Normalising what the server sends: a stored filter arrives as `ViewFilterNode` with every key
// present (`and`, `or`, `not`, `prop`, `op`, `value`, most of them null).
// ---------------------------------------------------------------------------------------------

type LooseNode = Record<string, unknown>;

function isObject(value: unknown): value is LooseNode {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/**
 * Turns a filter as the API (or the session storage) returns it into the DSL: null keys dropped,
 * children that are no node at all left out, empty groups removed. `null` for "no filter"
 * (`null`, `{}`, a group without valid children).
 */
export function cleanFilter(raw: unknown): FilterNode | null {
  if (!isObject(raw)) {
    return null;
  }
  for (const kind of ['and', 'or'] as const) {
    if (Array.isArray(raw[kind])) {
      const children = (raw[kind] as unknown[])
        .map((child) => cleanFilter(child))
        .filter((child): child is FilterNode => child !== null);
      if (children.length === 0) {
        return null;
      }
      return kind === 'and' ? { and: children } : { or: children };
    }
  }
  if (isObject(raw['not'])) {
    const child = cleanFilter(raw['not']);
    return child ? { not: child } : null;
  }
  if (
    typeof raw['prop'] === 'string' &&
    typeof raw['op'] === 'string' &&
    (FILTER_OPERATORS as readonly string[]).includes(raw['op'])
  ) {
    const op = raw['op'] as FilterOp;
    const condition: { prop: string; op: FilterOp; value?: unknown } = {
      prop: raw['prop'],
      op,
    };
    if (arityOf(op) !== 'none' && raw['value'] !== undefined && raw['value'] !== null) {
      condition.value = raw['value'];
    }
    return condition;
  }
  return null;
}

export function cleanSorts(raw: readonly { prop: string | null; dir: string | null }[] | null) {
  return (raw ?? [])
    .filter((s): s is { prop: string; dir: string } => !!s && typeof s.prop === 'string')
    .map((s): SortSpec => ({ prop: s.prop, dir: s.dir === 'desc' ? 'desc' : 'asc' }));
}

export function isCondition(node: FilterNode): node is FilterCondition {
  return 'prop' in node;
}

/** Children of a group; `[]` for anything else (also for malformed nodes such as `{}`). */
function childrenOf(node: FilterNode): readonly FilterNode[] {
  const loose = node as { and?: unknown; or?: unknown };
  const children = Array.isArray(loose.and) ? loose.and : Array.isArray(loose.or) ? loose.or : [];
  return children as FilterNode[];
}

function groupKind(node: FilterNode): GroupKind {
  return Array.isArray((node as { and?: unknown }).and) ? 'and' : 'or';
}

function isGroup(node: FilterNode): node is { and: FilterNode[] } | { or: FilterNode[] } {
  return (
    Array.isArray((node as { and?: unknown }).and) || Array.isArray((node as { or?: unknown }).or)
  );
}

/** Number of conditions in a filter (for the badge on the filter button). */
export function countConditions(node: FilterNode | null): number {
  if (!node) {
    return 0;
  }
  if (isCondition(node)) {
    return 1;
  }
  if ('not' in node && node.not) {
    return countConditions(node.not);
  }
  return childrenOf(node).reduce((sum, child) => sum + countConditions(child), 0);
}

/** Two filters (or sort lists) are the same when their JSON is. */
export function sameFilter(a: unknown, b: unknown): boolean {
  return JSON.stringify(a ?? null) === JSON.stringify(b ?? null);
}

// ---------------------------------------------------------------------------------------------
// Builder model: a root group (and/or) with conditions and at most one level of subgroups.
// ---------------------------------------------------------------------------------------------

export type GroupKind = 'and' | 'or';

export interface ConditionDraft {
  readonly kind: 'condition';
  readonly id: number;
  readonly prop: string;
  readonly op: FilterOp | '';
  readonly value: unknown;
}

export interface GroupDraft {
  readonly kind: GroupKind;
  readonly id: number;
  readonly items: readonly ConditionDraft[];
}

export interface BuilderState {
  readonly kind: GroupKind;
  readonly items: readonly (ConditionDraft | GroupDraft)[];
}

export const EMPTY_BUILDER: BuilderState = { kind: 'and', items: [] };

let nextId = 1;
export function draftId(): number {
  return nextId++;
}

export function emptyCondition(): ConditionDraft {
  return { kind: 'condition', id: draftId(), prop: '', op: '', value: null };
}

function toDraft(node: FilterNode): ConditionDraft | null {
  if (!isCondition(node)) {
    return null;
  }
  return {
    kind: 'condition',
    id: draftId(),
    prop: node.prop,
    op: node.op,
    value: node.value ?? null,
  };
}

/**
 * DSL → builder. `null` when the filter has a shape the builder cannot show (`not`, more than
 * one level of nested groups); the filter bar then offers only "show JSON" and "clear".
 * A bare condition becomes an `and` group with that one condition.
 */
export function toBuilder(filter: FilterNode | null): BuilderState | null {
  if (!filter) {
    return EMPTY_BUILDER;
  }
  if (isCondition(filter)) {
    const draft = toDraft(filter);
    return draft ? { kind: 'and', items: [draft] } : null;
  }
  if (!isGroup(filter)) {
    return 'not' in filter ? null : EMPTY_BUILDER; // `not` cannot be shown, `{}` is no filter
  }
  const kind = groupKind(filter);
  const items: (ConditionDraft | GroupDraft)[] = [];
  for (const child of childrenOf(filter)) {
    if (isCondition(child)) {
      items.push(toDraft(child)!);
      continue;
    }
    if (!isGroup(child)) {
      if ('not' in child) {
        return null;
      }
      continue; // malformed child: nothing to show
    }
    const subKind = groupKind(child);
    const drafts = childrenOf(child).map(toDraft);
    if (drafts.some((d) => d === null)) {
      return null;
    }
    items.push({ kind: subKind, id: draftId(), items: drafts as ConditionDraft[] });
  }
  return { kind, items };
}

/** Whether a draft is complete enough to be sent; incomplete ones are left out of the DSL. */
export function isComplete(draft: ConditionDraft, fields: readonly FilterField[] | null): boolean {
  if (draft.prop === '' || draft.op === '') {
    return false;
  }
  if (fields) {
    const field = fields.find((f) => f.key === draft.prop);
    if (!field || !operatorsFor(field.type).includes(draft.op)) {
      return false;
    }
  }
  switch (arityOf(draft.op)) {
    case 'none':
      return true;
    case 'many':
      return Array.isArray(draft.value) && draft.value.length > 0;
    default:
      return draft.value !== null && draft.value !== undefined && draft.value !== '';
  }
}

function conditionOf(draft: ConditionDraft): FilterCondition {
  const op = draft.op as FilterOp;
  return arityOf(op) === 'none'
    ? { prop: draft.prop, op }
    : { prop: draft.prop, op, value: draft.value };
}

/**
 * Builder → DSL: `{ "<kind>": [ … ] }` with the complete conditions, subgroups as nested groups;
 * `null` when nothing is left ("kein Filter"). `fields` (optional) also drops conditions whose
 * operator does not fit the property type.
 */
export function toDsl(
  state: BuilderState,
  fields: readonly FilterField[] | null = null,
): FilterNode | null {
  const items: FilterNode[] = [];
  for (const item of state.items) {
    if (item.kind === 'condition') {
      if (isComplete(item, fields)) {
        items.push(conditionOf(item));
      }
      continue;
    }
    const children = item.items.filter((d) => isComplete(d, fields)).map(conditionOf);
    if (children.length > 0) {
      items.push(item.kind === 'and' ? { and: children } : { or: children });
    }
  }
  if (items.length === 0) {
    return null;
  }
  return state.kind === 'and' ? { and: items } : { or: items };
}

/**
 * Value carried over when the operator changes: one value ↔ list, nothing for the empty checks.
 */
export function convertValue(value: unknown, from: FilterOp | '', to: FilterOp | ''): unknown {
  const before = arityOf(from);
  const after = arityOf(to);
  if (after === 'none') {
    return null;
  }
  if (before === after || from === '') {
    return before === 'none' ? null : value;
  }
  if (after === 'many') {
    return value === null || value === undefined || value === '' ? [] : [value];
  }
  return Array.isArray(value) ? (value[0] ?? null) : null;
}

/** `{ and: [ x ] }` combined with `{ and: [ y ] }` → `{ and: [ x, y ] }`; nulls are skipped. */
export function andFilters(...filters: (FilterNode | null)[]): FilterNode | null {
  const present = filters.filter((f): f is FilterNode => f !== null);
  if (present.length <= 1) {
    return present[0] ?? null;
  }
  return { and: present };
}
