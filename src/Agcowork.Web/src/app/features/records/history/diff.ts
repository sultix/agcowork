/**
 * Small diff helpers for revisions: property tables and a line diff for Markdown bodies.
 * No dependency: a line-based LCS is all the history view needs.
 */

export type LineKind = 'same' | 'add' | 'remove';

export interface DiffLine {
  readonly kind: LineKind;
  readonly text: string;
}

/** Above this many cells the LCS table is skipped and the middle part is shown as replaced. */
const MAX_LCS_CELLS = 4_000_000;

/** Line diff of two texts (`\n` or `\r\n`); unchanged lines are kept for context. */
export function diffLines(oldText: string, newText: string): DiffLine[] {
  const a = splitLines(oldText);
  const b = splitLines(newText);

  let start = 0;
  while (start < a.length && start < b.length && a[start] === b[start]) {
    start++;
  }
  let endA = a.length;
  let endB = b.length;
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) {
    endA--;
    endB--;
  }

  const result: DiffLine[] = a.slice(0, start).map((text) => ({ kind: 'same', text }));
  result.push(...diffMiddle(a.slice(start, endA), b.slice(start, endB)));
  result.push(...a.slice(endA).map((text): DiffLine => ({ kind: 'same', text })));
  return result;
}

function splitLines(text: string): string[] {
  return text === '' ? [] : text.split(/\r?\n/);
}

function diffMiddle(a: string[], b: string[]): DiffLine[] {
  if (a.length === 0 || b.length === 0 || a.length * b.length > MAX_LCS_CELLS) {
    return [
      ...a.map((text): DiffLine => ({ kind: 'remove', text })),
      ...b.map((text): DiffLine => ({ kind: 'add', text })),
    ];
  }
  // lcs[i][j] = length of the LCS of a[i..] and b[j..]
  const width = b.length + 1;
  const lcs = new Uint32Array((a.length + 1) * width);
  for (let i = a.length - 1; i >= 0; i--) {
    for (let j = b.length - 1; j >= 0; j--) {
      lcs[i * width + j] =
        a[i] === b[j]
          ? lcs[(i + 1) * width + j + 1] + 1
          : Math.max(lcs[(i + 1) * width + j], lcs[i * width + j + 1]);
    }
  }
  const result: DiffLine[] = [];
  let i = 0;
  let j = 0;
  while (i < a.length && j < b.length) {
    if (a[i] === b[j]) {
      result.push({ kind: 'same', text: a[i] });
      i++;
      j++;
    } else if (lcs[(i + 1) * width + j] >= lcs[i * width + j + 1]) {
      result.push({ kind: 'remove', text: a[i++] });
    } else {
      result.push({ kind: 'add', text: b[j++] });
    }
  }
  while (i < a.length) {
    result.push({ kind: 'remove', text: a[i++] });
  }
  while (j < b.length) {
    result.push({ kind: 'add', text: b[j++] });
  }
  return result;
}

export interface PropertyChange {
  readonly key: string;
  readonly kind: 'added' | 'removed' | 'changed';
  readonly old: unknown;
  readonly new: unknown;
}

/** Structural equality of JSON values; object key order does not matter. */
export function sameValue(a: unknown, b: unknown): boolean {
  return canonical(a) === canonical(b);
}

function canonical(value: unknown): string {
  if (value === undefined || value === null) {
    return 'null';
  }
  if (Array.isArray(value)) {
    return `[${value.map(canonical).join(',')}]`;
  }
  if (typeof value === 'object') {
    const entries = Object.entries(value as Record<string, unknown>)
      .filter(([, v]) => v !== undefined)
      .sort(([x], [y]) => (x < y ? -1 : x > y ? 1 : 0));
    return `{${entries.map(([k, v]) => `${JSON.stringify(k)}:${canonical(v)}`).join(',')}}`;
  }
  return JSON.stringify(value);
}

/** Keys whose value differs (a `null` value counts as absent), sorted by key. */
export function diffProperties(
  oldProps: Readonly<Record<string, unknown>>,
  newProps: Readonly<Record<string, unknown>>,
): PropertyChange[] {
  const keys = new Set([...Object.keys(oldProps), ...Object.keys(newProps)]);
  const changes: PropertyChange[] = [];
  for (const key of [...keys].sort()) {
    const before = oldProps[key] ?? null;
    const after = newProps[key] ?? null;
    if (sameValue(before, after)) {
      continue;
    }
    changes.push({
      key,
      kind: before === null ? 'added' : after === null ? 'removed' : 'changed',
      old: before,
      new: after,
    });
  }
  return changes;
}

/** Content of one revision as far as the diff is concerned. */
export interface RevisionContent {
  readonly title: string;
  readonly properties: Readonly<Record<string, unknown>>;
  readonly body: string;
}

export interface RevisionDiff {
  readonly title: { readonly old: string; readonly new: string } | null;
  readonly properties: PropertyChange[];
  readonly body: DiffLine[];
  readonly bodyChanged: boolean;
}

export function diffRevisions(from: RevisionContent, to: RevisionContent): RevisionDiff {
  const body = diffLines(from.body, to.body);
  return {
    title: from.title === to.title ? null : { old: from.title, new: to.title },
    properties: diffProperties(from.properties, to.properties),
    body,
    bodyChanged: from.body !== to.body,
  };
}

/** Display text of a property value in a diff cell. */
export function formatValue(value: unknown): string {
  if (value === null || value === undefined) {
    return '';
  }
  return typeof value === 'string' ? value : JSON.stringify(value);
}
