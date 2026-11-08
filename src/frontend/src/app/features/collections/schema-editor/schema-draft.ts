import type { components } from '../../../core/api/generated/schema';
import {
  COMPUTED_TYPES,
  PropertyDefinition,
  PropertyType,
} from '../../../shared/properties/property-types';

export type PropertyDefinitionInput = components['schemas']['PropertyDefinitionInput'];
export type PropertyChangeInput = components['schemas']['PropertyChangeInput'];
export type PropertyOptionInput = components['schemas']['PropertyOptionInput'];
export type ViewFilterNode = components['schemas']['ViewFilterNode'];

// ---------------------------------------------------------------------------------------------
// Rules of the API (architecture §2 "Schema-Regeln"), mirrored for early feedback. The server
// stays the authority: everything here only avoids a round trip or a surprise.
// ---------------------------------------------------------------------------------------------

/** `^[a-z][a-z0-9_]{0,62}$`: snake_case, starting with a letter, at most 63 characters. */
export const KEY_PATTERN = /^[a-z][a-z0-9_]{0,62}$/;
export const KEY_MAX_LENGTH = 63;
export const NAME_MAX_LENGTH = 200;
export const DESCRIPTION_MAX_LENGTH = 2000;
export const UNIT_MAX_LENGTH = 32;
export const OPTION_VALUE_MAX_LENGTH = 100;
export const OPTION_LABEL_MAX_LENGTH = 200;
export const MAX_PRECISION = 10;
/** Longest text value; markdown → text cuts longer values. */
export const TEXT_MAX_LENGTH = 10_000;
export const TITLE_KEY = 'title';

/** Keys of record system fields plus `title` (always the title property). */
export const RESERVED_KEYS: readonly string[] = [
  'id',
  'collection',
  'space',
  'revision',
  'body',
  TITLE_KEY,
];

/** The type changes the API allows (text ↔ markdown, select → multi_select). */
const TYPE_CHANGES: Readonly<Partial<Record<PropertyType, readonly PropertyType[]>>> = {
  text: ['markdown'],
  markdown: ['text'],
  select: ['multi_select'],
};

/** `type` itself plus the types an existing property of `type` may become. */
export function allowedTypes(type: PropertyType): readonly PropertyType[] {
  return [type, ...(TYPE_CHANGES[type] ?? [])];
}

export function isComputed(type: PropertyType): boolean {
  return COMPUTED_TYPES.includes(type);
}

export function hasOptions(type: PropertyType): boolean {
  return type === 'select' || type === 'multi_select' || type === 'status';
}

/** relation, actor and computed types have no default. */
export function allowsDefault(type: PropertyType): boolean {
  return type !== 'relation' && type !== 'actor' && !isComputed(type);
}

/**
 * Key proposal for a display name: lowercase ASCII, umlauts spelled out, everything else `_`,
 * starting with a letter; reserved and `taken` keys get a suffix (`_2`, `_3`, …).
 */
export function suggestKey(name: string, taken: readonly string[]): string {
  let base = name
    .toLowerCase()
    .replace(/ä/g, 'ae')
    .replace(/ö/g, 'oe')
    .replace(/ü/g, 'ue')
    .replace(/ß/g, 'ss')
    .normalize('NFKD')
    .replace(/[̀-ͯ]/g, '')
    .replace(/[^a-z0-9]+/g, '_')
    .replace(/^_+|_+$/g, '');
  if (base === '') {
    return '';
  }
  if (!/^[a-z]/.test(base)) {
    base = `p_${base}`;
  }
  base = base.slice(0, KEY_MAX_LENGTH).replace(/_+$/, '');
  let key = base;
  for (let n = 2; RESERVED_KEYS.includes(key) || taken.includes(key); n++) {
    const suffix = `_${n}`;
    key = base.slice(0, KEY_MAX_LENGTH - suffix.length).replace(/_+$/, '') + suffix;
  }
  return key;
}

// ---------------------------------------------------------------------------------------------
// The form model of the property dialog and its translation into API bodies.
// ---------------------------------------------------------------------------------------------

export interface OptionDraft {
  value: string;
  label: string;
  /** Palette color or `''` (none). */
  color: string;
  /** Status group or `''`. */
  group: string;
  /** The option exists in the schema: its value is stored on records and cannot change. */
  existing: boolean;
  /** Set after a concurrent change that concerns this option (shown at the option). */
  conflict?: string;
}

export interface PropertyDraft {
  key: string;
  name: string;
  type: PropertyType;
  description: string;
  required: boolean;
  options: OptionDraft[];
  unit: string;
  precision: number | null;
  targetCollectionId: string;
  multiple: boolean;
  /** JSON value of the default; `null` = none. */
  default: unknown;
}

export function emptyDraft(): PropertyDraft {
  return {
    key: '',
    name: '',
    type: 'text',
    description: '',
    required: false,
    options: [],
    unit: '',
    precision: null,
    targetCollectionId: '',
    multiple: false,
    default: null,
  };
}

export function newOption(type: PropertyType): OptionDraft {
  return {
    value: '',
    label: '',
    color: '',
    group: type === 'status' ? 'todo' : '',
    existing: false,
  };
}

export function draftOf(definition: PropertyDefinition): PropertyDraft {
  return {
    key: definition.key,
    name: definition.name,
    type: definition.type,
    description: definition.description ?? '',
    required: definition.required,
    options: (definition.options ?? []).map((o) => ({
      value: o.value,
      label: o.label,
      color: o.color ?? '',
      group: o.group ?? '',
      existing: true,
    })),
    unit: definition.unit ?? '',
    precision: definition.precision == null ? null : Number(definition.precision),
    targetCollectionId: definition.targetCollectionId ?? '',
    multiple: definition.multiple,
    default: definition.default ?? null,
  };
}

/** Fields of a draft that follow a concurrent change unless the user changed them. */
const REBASED_FIELDS = [
  'name',
  'type',
  'description',
  'required',
  'unit',
  'precision',
  'targetCollectionId',
  'multiple',
  'default',
] as const satisfies readonly (keyof PropertyDraft)[];

/** Option fields that merge one by one after a concurrent change. */
const OPTION_FIELDS = ['label', 'color', 'group'] as const satisfies readonly (keyof OptionDraft)[];

/** Hint at an option the user edited while someone else removed it. */
export const OPTION_REMOVED_ELSEWHERE =
  'Diese Option wurde inzwischen von jemand anderem entfernt. Behältst du sie, wird sie beim Speichern neu angelegt.';

/** Option fields compared by meaning: an empty label is the value, no color is `''`. */
function optionField(option: OptionDraft, field: (typeof OPTION_FIELDS)[number]): string {
  return field === 'label' ? option.label.trim() || option.value.trim() : option[field];
}

function sameOption(a: OptionDraft, b: OptionDraft): boolean {
  return OPTION_FIELDS.every((f) => optionField(a, f) === optionField(b, f));
}

/**
 * Three-way merge of the options by value: `base` is what the user started from, `draft` what the
 * user made of it, `theirs` what the server has now.
 *
 * - Options in `theirs`: kept, unless the user removed them; label, color and group each follow
 *   `theirs` unless the user changed that field.
 * - Options removed elsewhere stay removed, unless the user edited exactly that option: then it
 *   stays as the user's (as a new option) with {@link OPTION_REMOVED_ELSEWHERE}.
 * - Options the user added follow at the end.
 */
function rebaseOptions(
  draft: readonly OptionDraft[],
  base: readonly OptionDraft[],
  theirs: readonly OptionDraft[],
): OptionDraft[] {
  const key = (o: OptionDraft) => o.value.trim();
  const inBase = new Map(base.map((o) => [key(o), o]));
  const inDraft = new Map(draft.filter((o) => key(o) !== '').map((o) => [key(o), o]));
  const inTheirs = new Set(theirs.map(key));
  const result: OptionDraft[] = [];

  for (const their of theirs) {
    const value = key(their);
    const before = inBase.get(value);
    const mine = inDraft.get(value);
    if (before && !mine) {
      continue; // removed by the user
    }
    if (!before || !mine) {
      // added elsewhere; a user option with the same value merges into it
      result.push(mine ? { ...mine, existing: true, conflict: undefined } : { ...their });
      continue;
    }
    const merged: OptionDraft = { ...their };
    for (const field of OPTION_FIELDS) {
      if (optionField(mine, field) !== optionField(before, field)) {
        merged[field] = mine[field];
      }
    }
    result.push(merged);
  }

  for (const mine of draft) {
    const value = key(mine);
    if (value !== '' && inTheirs.has(value)) {
      continue; // merged above
    }
    const before = inBase.get(value);
    if (before && value !== '') {
      // removed elsewhere: gone, unless the user edited this very option
      if (!sameOption(mine, before)) {
        result.push({ ...mine, existing: false, conflict: OPTION_REMOVED_ELSEWHERE });
      }
      continue;
    }
    result.push({ ...mine, existing: false });
  }
  return result;
}

/**
 * After a concurrent change of the edited property (409, schema reloaded): every field the user
 * left as it was in `base` takes the value of `current`, every field the user changed keeps the
 * user's value. Options merge one by one ({@link rebaseOptions}).
 */
export function rebaseDraft(
  draft: PropertyDraft,
  base: PropertyDefinition,
  current: PropertyDefinition,
): PropertyDraft {
  const before = draftOf(base);
  const theirs = draftOf(current);
  const result: PropertyDraft = { ...draft };
  for (const field of REBASED_FIELDS) {
    if (sameJson(draft[field], before[field])) {
      (result as unknown as Record<string, unknown>)[field] = theirs[field];
    }
  }
  result.options = rebaseOptions(draft.options, before.options, theirs.options);
  return result;
}

/** `null`, `''` and `[]` mean "no default". */
export function isEmptyValue(value: unknown): boolean {
  return value == null || value === '' || (Array.isArray(value) && value.length === 0);
}

function sameJson(a: unknown, b: unknown): boolean {
  return JSON.stringify(a ?? null) === JSON.stringify(b ?? null);
}

interface NormalizedOption {
  value: string;
  label: string;
  color: string | null;
  group: string | null;
}

/** Options as the API stores them: trimmed, label defaults to the value, group only for status. */
function normalizeOptions(
  options: readonly { value: string; label: string; color: string | null; group: string | null }[],
  type: PropertyType,
): NormalizedOption[] {
  return options.map((o) => {
    const value = o.value.trim();
    return {
      value,
      label: o.label.trim() || value,
      color: o.color || null,
      group: type === 'status' ? o.group || null : null,
    };
  });
}

/** Options of a draft for the API and for previews (default editor). */
export function draftOptions(draft: PropertyDraft): NormalizedOption[] {
  return normalizeOptions(draft.options, draft.type);
}

/** The definition for `add`; only the members that apply to the type. */
export function toDefinitionInput(draft: PropertyDraft): PropertyDefinitionInput {
  const description = draft.description.trim();
  const input: PropertyDefinitionInput = {
    key: draft.key,
    name: draft.name.trim(),
    type: draft.type,
    required: isComputed(draft.type) ? false : draft.required,
  };
  if (description !== '') {
    input.description = description;
  }
  if (hasOptions(draft.type)) {
    input.options = draftOptions(draft);
  }
  if (draft.type === 'number') {
    if (draft.unit.trim() !== '') {
      input.unit = draft.unit.trim();
    }
    if (draft.precision !== null) {
      input.precision = draft.precision;
    }
  }
  if (draft.type === 'relation') {
    input.targetCollectionId = draft.targetCollectionId;
    input.multiple = draft.multiple;
  }
  if (allowsDefault(draft.type) && !isEmptyValue(draft.default)) {
    input.default = draft.default;
  }
  return input;
}

/**
 * The `update` entry for an existing property: only what the user changed compared with
 * `original` (the property as the dialog opened it), so concurrent changes to other fields of the
 * same property survive. `null` when nothing changed.
 */
export function toChangeInput(
  original: PropertyDefinition,
  draft: PropertyDraft,
): PropertyChangeInput | null {
  const change: PropertyChangeInput = { key: original.key };
  const name = draft.name.trim();
  if (name !== original.name) {
    change.name = name;
  }
  if (draft.type !== original.type) {
    change.type = draft.type;
  }
  const description = draft.description.trim();
  if (description !== (original.description ?? '')) {
    change.description = description; // '' clears it
  }
  if (!isComputed(draft.type) && draft.required !== original.required) {
    change.required = draft.required;
  }
  if (hasOptions(draft.type)) {
    const before = normalizeOptions(
      (original.options ?? []).map((o) => ({ ...o, group: o.group ?? null })),
      draft.type,
    );
    const after = draftOptions(draft);
    if (!sameJson(before, after)) {
      change.options = after;
    }
  }
  if (draft.type === 'number') {
    const unit = draft.unit.trim();
    if (unit !== (original.unit ?? '')) {
      change.unit = unit; // '' clears it
    }
    const precision = original.precision == null ? null : Number(original.precision);
    if (draft.precision !== null && draft.precision !== precision) {
      change.precision = draft.precision;
    }
  }
  if (draft.type === 'relation' && draft.multiple && !original.multiple) {
    change.multiple = true;
  }
  if (allowsDefault(draft.type)) {
    const before = original.default ?? null;
    const after = isEmptyValue(draft.default) ? null : draft.default;
    if (!sameJson(before, after)) {
      if (after === null) {
        change.clearDefault = true;
      } else {
        change.default = after;
      }
    }
  }
  return Object.keys(change).length > 1 ? change : null;
}

// ---------------------------------------------------------------------------------------------
// Changes that lose stored values: the API wants `confirmRemoval: true` for them.
// ---------------------------------------------------------------------------------------------

export type Loss =
  | { readonly kind: 'removed'; readonly property: PropertyDefinition }
  | { readonly kind: 'options'; readonly property: PropertyDefinition; readonly values: string[] }
  | {
      readonly kind: 'precision';
      readonly property: PropertyDefinition;
      readonly precision: number;
    }
  | { readonly kind: 'markdown_to_text'; readonly property: PropertyDefinition };

/**
 * What `change` would lose on `current` (the property as the server has it now), by the rules of
 * the API: removed option values, a precision set or lowered, markdown → text.
 */
export function lossesOf(current: PropertyDefinition, change: PropertyChangeInput): Loss[] {
  const losses: Loss[] = [];
  const type = (change.type ?? current.type) as PropertyType;
  if (change.options) {
    const kept = new Set(change.options.map((o) => o.value ?? ''));
    const values = (current.options ?? []).map((o) => o.value).filter((v) => !kept.has(v));
    if (values.length > 0) {
      losses.push({ kind: 'options', property: current, values });
    }
  }
  if (type === 'number') {
    const before = current.precision == null ? null : Number(current.precision);
    const after = change.precision == null ? before : Number(change.precision);
    if (after !== null && (before === null || after < before)) {
      losses.push({ kind: 'precision', property: current, precision: after });
    }
  }
  if (current.type === 'markdown' && type === 'text') {
    losses.push({ kind: 'markdown_to_text', property: current });
  }
  return losses;
}

/**
 * Filter that finds the records holding values a loss affects, for counting them; `null` where no
 * count makes sense (json is not filterable, computed values are not stored).
 */
export function lossFilter(loss: Loss): ViewFilterNode | null {
  const { key, type } = loss.property;
  if (type === 'json' || isComputed(type)) {
    return null;
  }
  if (loss.kind === 'options') {
    return loss.values.length <= 100 ? { prop: key, op: 'in', value: loss.values } : null;
  }
  // checkbox knows no is_not_empty, every filterable type knows is_empty
  return { not: { prop: key, op: 'is_empty' } };
}

/** `undefined`: still counting; `null`: unknown. */
export type AffectedCount = number | null | undefined;

/** Upper bound of the API's count: 10 000 means "10 000 or more". */
const COUNT_LIMIT = 10_000;

function records(count: number): string {
  if (count >= COUNT_LIMIT) {
    return `mindestens ${COUNT_LIMIT.toLocaleString('de-DE')} Records`;
  }
  return count === 1 ? '1 Record' : `${count.toLocaleString('de-DE')} Records`;
}

/** One sentence per loss for the confirmation, with the number of affected records if known. */
export function describeLoss(loss: Loss, count: AffectedCount): string {
  const name = `„${loss.property.name}“`;
  const counting = 'Betroffene Records werden gezählt …';
  switch (loss.kind) {
    case 'removed':
      if (isComputed(loss.property.type)) {
        return `${name} wird entfernt. Das System berechnet diese Werte nur; gespeichert ist nichts, es geht kein Wert verloren.`;
      }
      return `${name} wird entfernt. ${
        count === undefined
          ? counting
          : count === null
            ? 'Gespeicherte Werte gehen verloren.'
            : count === 0
              ? 'Kein Record hat dort einen Wert.'
              : `Werte in ${records(count)} gehen verloren.`
      }`;
    case 'options': {
      const values = loss.values.map((v) => `„${v}“`).join(', ');
      const what = loss.values.length === 1 ? 'Die Option' : 'Die Optionen';
      return `${what} ${values} von ${name} ${loss.values.length === 1 ? 'wird' : 'werden'} entfernt. ${
        count === undefined
          ? counting
          : count === null
            ? 'Records mit diesen Werten verlieren sie.'
            : count === 0
              ? 'Kein Record verwendet sie.'
              : `Werte in ${records(count)} gehen verloren.`
      }`;
    }
    case 'precision':
      return `${name} speichert nur noch ${loss.precision} Nachkommastellen. ${
        count === undefined
          ? counting
          : count === null
            ? 'Vorhandene Werte werden gerundet.'
            : count === 0
              ? 'Kein Record hat dort einen Wert.'
              : `Werte in bis zu ${records(count)} werden gerundet.`
      }`;
    case 'markdown_to_text':
      return `${name} wird von Markdown zu Text: Werte über ${TEXT_MAX_LENGTH.toLocaleString('de-DE')} Zeichen werden gekürzt, Formatierung wird nicht mehr dargestellt. ${
        count === undefined
          ? counting
          : count === null
            ? ''
            : count === 0
              ? 'Kein Record hat dort einen Wert.'
              : `Betroffen sind ${records(count)}.`
      }`.trim();
  }
}
