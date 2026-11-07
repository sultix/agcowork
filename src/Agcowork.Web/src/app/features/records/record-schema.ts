import type { components } from '../../core/api/generated/schema';

type PropertyDefinition = components['schemas']['PropertyDefinitionDto'];

/**
 * The properties a user may write: computed ones (`readOnly` in the schema) are left out of
 * raw editing and of restoring. Without a schema all properties count as writable.
 */
export function editableProperties(
  properties: Readonly<Record<string, unknown>>,
  schema: readonly PropertyDefinition[] | null,
): Record<string, unknown> {
  const readOnly = new Set((schema ?? []).filter((d) => d.readOnly).map((d) => d.key));
  return Object.fromEntries(Object.entries(properties).filter(([key]) => !readOnly.has(key)));
}

/** Keys of `properties` that the current schema does not (or no longer) know; none without a schema. */
export function unknownProperties(
  properties: Readonly<Record<string, unknown>>,
  schema: readonly PropertyDefinition[] | null,
): string[] {
  if (schema === null) {
    return [];
  }
  const known = new Set(schema.map((d) => d.key));
  return Object.keys(properties).filter((key) => !known.has(key));
}
