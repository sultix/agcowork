import type { components } from '../../../core/api/generated/schema';
import { COMPUTED_TYPES } from '../../../shared/properties/property-types';
import type { PropertyDefinition, PropertyValue } from '../../../shared/properties/property-types';

export type TemplateDto = components['schemas']['TemplateDto'];
export type CreateTemplateBody = components['schemas']['CreateTemplateBody'];
export type UpdateTemplateBody = components['schemas']['UpdateTemplateBody'];

/** Limits of the API (`Template` in the domain). */
export const TEMPLATE_NAME_MAX_LENGTH = 200;
export const TEMPLATE_DESCRIPTION_MAX_LENGTH = 2000;
export const TEMPLATE_BODY_MAX_LENGTH = 200_000;

/** Property values by key, as a template presets them. */
export type Preset = Record<string, PropertyValue>;

/** The editable state of a template: what the dialog starts with and what it saves. */
export interface TemplateSeed {
  readonly name: string;
  readonly description: string;
  readonly isDefault: boolean;
  readonly preset: Preset;
  readonly body: string;
}

/**
 * Properties a template can preset: not the computed ones (the system fills them) and not
 * read-only ones. The title can be preset: a record created without a title (e.g. by an agent)
 * takes the template's.
 */
export function presettable(schema: readonly PropertyDefinition[]): PropertyDefinition[] {
  return schema.filter((p) => !p.readOnly && !COMPUTED_TYPES.includes(p.type));
}

/** The preset limited to the given property keys (what the current schema still knows). */
export function presetOfKeys(preset: Preset, keys: ReadonlySet<string>): Preset {
  return Object.fromEntries(Object.entries(preset).filter(([key]) => keys.has(key)));
}

/** Whether a preset entry holds nothing worth sending (cleared input). */
export function isEmptyValue(value: PropertyValue): boolean {
  return (
    value === null ||
    value === undefined ||
    value === '' ||
    (Array.isArray(value) && value.length === 0)
  );
}

/** The preset without empty entries; "clear the field" removes the key. */
export function cleanPreset(preset: Preset): Preset {
  return Object.fromEntries(Object.entries(preset).filter(([, value]) => !isEmptyValue(value)));
}

/** The preset after the user set `key` (an empty value removes the entry). */
export function withPresetValue(preset: Preset, key: string, value: PropertyValue): Preset {
  const rest = Object.fromEntries(Object.entries(preset).filter(([k]) => k !== key));
  return isEmptyValue(value) ? rest : { ...rest, [key]: value };
}

export function seedOf(template: TemplateDto): TemplateSeed {
  return {
    name: template.name,
    description: template.description ?? '',
    isDefault: template.isDefault,
    preset: cleanPreset({ ...(template.propertiesPreset as Preset) }),
    body: template.bodyMarkdown,
  };
}

export const EMPTY_SEED: TemplateSeed = {
  name: '',
  description: '',
  isDefault: false,
  preset: {},
  body: '',
};

/**
 * A draft for a new template from a record: the values of its presettable properties (computed
 * ones and the title are left out) and its body; the name is suggested from the title.
 */
export function seedFromRecord(
  schema: readonly PropertyDefinition[],
  record: { readonly title: string; readonly properties: Preset; readonly body?: string | null },
): TemplateSeed {
  const preset: Preset = {};
  for (const definition of presettable(schema)) {
    const value = record.properties[definition.key];
    if (!isEmptyValue(value)) {
      preset[definition.key] = structuredClone(value);
    }
  }
  return {
    ...EMPTY_SEED,
    name: record.title.trim().slice(0, TEMPLATE_NAME_MAX_LENGTH),
    preset,
    body: record.body ?? '',
  };
}

function same(a: unknown, b: unknown): boolean {
  return JSON.stringify(sortKeys(a)) === JSON.stringify(sortKeys(b));
}

function sortKeys(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map(sortKeys);
  }
  if (value && typeof value === 'object') {
    return Object.fromEntries(
      Object.entries(value as Record<string, unknown>)
        .sort(([a], [b]) => a.localeCompare(b))
        .map(([k, v]) => [k, sortKeys(v)]),
    );
  }
  return value;
}

/** Whether two presets hold the same values (key order does not matter). */
export function samePreset(a: Preset, b: Preset): boolean {
  return same(cleanPreset(a), cleanPreset(b));
}

export function toCreateBody(draft: TemplateSeed): CreateTemplateBody {
  return {
    name: draft.name.trim(),
    ...(draft.description.trim() ? { description: draft.description.trim() } : {}),
    propertiesPreset: cleanPreset(draft.preset) as Record<string, never>,
    ...(draft.body ? { bodyMarkdown: draft.body } : {}),
    isDefault: draft.isDefault,
  };
}

/**
 * Only the fields that differ from `original`; `null` when nothing changed. The preset replaces
 * the stored one as a whole, so it is sent complete whenever any entry differs. A cleared
 * description or body is sent as `""` (null would leave it unchanged).
 */
export function toUpdateBody(
  original: TemplateSeed,
  draft: TemplateSeed,
): UpdateTemplateBody | null {
  const body: UpdateTemplateBody = {};
  if (draft.name.trim() !== original.name) {
    body.name = draft.name.trim();
  }
  if (draft.description.trim() !== original.description.trim()) {
    body.description = draft.description.trim();
  }
  if (!samePreset(draft.preset, original.preset)) {
    body.propertiesPreset = cleanPreset(draft.preset) as Record<string, never>;
  }
  if (draft.body !== original.body) {
    body.bodyMarkdown = draft.body;
  }
  if (draft.isDefault !== original.isDefault) {
    body.isDefault = draft.isDefault;
  }
  return Object.keys(body).length > 0 ? body : null;
}

/** Plain-text start of a Markdown body for the list (markers dropped, whitespace collapsed). */
export function bodyExcerpt(markdown: string, max = 140): string {
  const text = markdown
    .replace(/```[^\n]*\n?/g, ' ')
    .replace(/^[ \t]*(?:#{1,6}|>|[-*+]|\d+\.)[ \t]+/gm, '')
    .replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/[*_~`]/g, '')
    .replace(/\s+/g, ' ')
    .trim();
  return text.length > max ? `${text.slice(0, max).trimEnd()} …` : text;
}

/** Names of the properties a template presets, in schema order; unknown keys by key. */
export function presetNames(
  template: TemplateDto,
  schema: readonly PropertyDefinition[],
): string[] {
  const keys = Object.keys(template.propertiesPreset);
  const known = schema.filter((p) => keys.includes(p.key)).map((p) => p.name);
  const unknown = keys.filter((k) => !schema.some((p) => p.key === k));
  return [...known, ...unknown];
}
