import type { PropertyDefinition, PropertyType } from '../../shared/properties/property-types';
import type { RecordDto } from '../records/record-save.service';

/** Property types a gallery cover can be built from (no images, see AP-012). */
export const COVER_TYPES: readonly PropertyType[] = [
  'select',
  'status',
  'multi_select',
  'checkbox',
  'text',
  'url',
  'email',
  'number',
  'date',
];

export function isCoverType(definition: PropertyDefinition): boolean {
  return COVER_TYPES.includes(definition.type);
}

export function propertyValue(record: RecordDto, key: string): unknown {
  return (record.properties as Record<string, unknown>)[key];
}

export function hasPropertyValue(record: RecordDto, key: string): boolean {
  const value = propertyValue(record, key);
  return (
    value !== null &&
    value !== undefined &&
    value !== '' &&
    !(Array.isArray(value) && value.length === 0)
  );
}

/** The first `max` of `fields` that have a value on `record`. */
export function fieldsWithValue(
  record: RecordDto,
  fields: readonly PropertyDefinition[],
  max: number,
): PropertyDefinition[] {
  return fields.filter((field) => hasPropertyValue(record, field.key)).slice(0, max);
}

/** Number of grid columns, read from how many items share the row of the first one. */
export function gridColumns(items: readonly HTMLElement[]): number {
  if (items.length === 0) {
    return 1;
  }
  const top = items[0].offsetTop;
  const inRow = items.findIndex((item) => item.offsetTop !== top);
  return inRow === -1 ? items.length : Math.max(1, inRow);
}

/**
 * Index to focus after `key` in a list of `count` items shown in rows of `columns` (`1`: a plain
 * list). `null`: the key does nothing here.
 */
export function nextIndex(
  key: string,
  index: number,
  count: number,
  columns: number,
  horizontal: boolean,
): number | null {
  const last = count - 1;
  const clamp = (n: number) => Math.min(last, Math.max(0, n));
  switch (key) {
    case 'ArrowDown':
      return clamp(index + columns);
    case 'ArrowUp':
      return clamp(index - columns);
    case 'ArrowRight':
      return horizontal ? clamp(index + 1) : null;
    case 'ArrowLeft':
      return horizontal ? clamp(index - 1) : null;
    case 'Home':
      return 0;
    case 'End':
      return last;
    default:
      return null;
  }
}
