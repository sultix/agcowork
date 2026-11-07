import { formatValue } from '../records/history/diff';

/** One changed field of an activity event: `old`/`new` as the backend recorded them. */
export interface FieldChange {
  readonly key: string;
  readonly old: unknown;
  readonly new: unknown;
}

/** The diff of an activity event in a shape the UI can render. */
export interface ActivityDiffView {
  readonly changes: FieldChange[];
  /** Characters added/removed in the body (records), `null` when the body did not change. */
  readonly body: { readonly added: number; readonly removed: number } | null;
  /** Other details (schema changes, members, …) as key and display text. */
  readonly extras: { readonly key: string; readonly text: string }[];
  /** Number of records cleaned by a schema change. */
  readonly affectedRecords: number | null;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isChange(value: unknown): value is { old?: unknown; new?: unknown } {
  return isRecord(value) && ('old' in value || 'new' in value);
}

/**
 * Normalises `ActivityEventDto.diff`: record events carry `title`, `properties.<key>` and
 * `bodyChanged`/`bodyDelta`; other events any set of `{ old, new }` fields or plain details.
 */
export function viewDiff(diff: unknown): ActivityDiffView {
  const view: {
    changes: FieldChange[];
    body: ActivityDiffView['body'];
    extras: { key: string; text: string }[];
    affectedRecords: number | null;
  } = { changes: [], body: null, extras: [], affectedRecords: null };
  if (!isRecord(diff)) {
    return view;
  }
  for (const [key, value] of Object.entries(diff)) {
    if (key === 'bodyChanged') {
      continue;
    }
    if (key === 'bodyDelta' && isRecord(value)) {
      view.body = { added: Number(value['added'] ?? 0), removed: Number(value['removed'] ?? 0) };
    } else if (key === 'properties' && isRecord(value)) {
      for (const [name, change] of Object.entries(value)) {
        if (isChange(change)) {
          view.changes.push({ key: name, old: change.old ?? null, new: change.new ?? null });
        }
      }
    } else if (isChange(value)) {
      view.changes.push({ key, old: value.old ?? null, new: value.new ?? null });
    } else if (key === 'affectedRecords' && typeof value === 'number') {
      view.affectedRecords = value;
    } else {
      view.extras.push({ key, text: formatValue(value) });
    }
  }
  if (diff['bodyChanged'] === true && view.body === null) {
    view.body = { added: 0, removed: 0 };
  }
  return view;
}

export function hasDetails(view: ActivityDiffView): boolean {
  return view.changes.length > 0 || view.body !== null || view.extras.length > 0;
}
