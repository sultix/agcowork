import type { components } from '../../core/api/generated/schema';
import { COMPUTED_TYPES, PropertyDefinition } from '../properties/property-types';

/** A record as the table shows it (query result row). */
export type TableRecord = components['schemas']['RecordDto'];

/** Key of the title "column"; every other key is a property key. */
export const TITLE_KEY = 'title';

export type CellStatus = 'saving' | 'error' | 'conflict';

export interface CellState {
  readonly status: CellStatus;
  readonly message: string | null;
  /** The rejected value of a failed save; offered again when the cell is reopened. */
  readonly draft?: unknown;
}

/**
 * What the table needs from whoever saves its cells: the state of a cell and saving one value.
 * Implemented by the views feature (`InlineEditService`); the table knows nothing about how.
 */
export interface CellEditor {
  cell(recordId: string, key: string): CellState | undefined;
  save(record: TableRecord, key: string, value: unknown): Promise<boolean>;
}

/**
 * Whether a cell may be edited inline: never for viewers (no `write`), archived records and
 * computed properties (`readOnly` or a computed type). `definition` is `null` for the title.
 */
export function canEditCell(
  definition: PropertyDefinition | null,
  record: Pick<TableRecord, 'archivedAt'>,
  canWrite: boolean,
): boolean {
  if (!canWrite || record.archivedAt) {
    return false;
  }
  return !definition || (!definition.readOnly && !COMPUTED_TYPES.includes(definition.type));
}

/** The record with one value replaced (title or property); `undefined` removes the property. */
export function withValue(record: TableRecord, key: string, value: unknown): TableRecord {
  if (key === TITLE_KEY) {
    return { ...record, title: typeof value === 'string' ? value : '' };
  }
  const properties: Record<string, unknown> = { ...record.properties };
  if (value === undefined) {
    delete properties[key];
  } else {
    properties[key] = value;
  }
  return { ...record, properties: properties as never };
}

/** The value of `key` on a record (title or property). */
export function valueOf(record: TableRecord, key: string): unknown {
  return key === TITLE_KEY ? record.title : (record.properties as Record<string, unknown>)[key];
}
