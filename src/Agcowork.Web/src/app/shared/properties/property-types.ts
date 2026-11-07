import type { components } from '../../core/api/generated/schema';

export type PropertyDefinition = components['schemas']['PropertyDefinitionDto'];
export type PropertyOption = components['schemas']['PropertyOptionDto'];
export type PropertyType = components['schemas']['PropertyType'];

/** A property value as it travels in `properties`: any JSON value, `null`/`undefined` = no value. */
export type PropertyValue = unknown;

/** Types the system computes; their editors are always read-only. */
export const COMPUTED_TYPES: readonly PropertyType[] = [
  'created_at',
  'updated_at',
  'created_by',
  'updated_by',
];

export function asText(value: PropertyValue): string {
  return typeof value === 'string' ? value : value == null ? '' : String(value);
}

export function asStringList(value: PropertyValue): string[] {
  if (Array.isArray(value)) {
    return value.filter((v): v is string => typeof v === 'string');
  }
  return typeof value === 'string' && value !== '' ? [value] : [];
}

/** Label of an option value (select, status); the value itself when the option is gone. */
export function optionLabel(definition: PropertyDefinition, value: PropertyValue): string {
  const text = asText(value);
  return definition.options?.find((o) => o.value === text)?.label ?? text;
}
