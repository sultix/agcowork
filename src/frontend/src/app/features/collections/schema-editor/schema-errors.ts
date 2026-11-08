import type { FieldTree } from '@angular/forms/signals';
import { ApiError, ProblemFieldError } from '../../../core/api/api-error';
import {
  SERVER_ERROR_KIND,
  ServerValidationError,
  fieldErrorMessage,
} from '../../../core/api/field-errors';
import { describeError } from '../../../shared/ui/api-error-text';
import type { PropertyDraft } from './schema-draft';

/** `code` of the 409 for a stale `expectedSchemaVersion`. */
export const SCHEMA_VERSION_CONFLICT = 'schema_version_conflict';
/** `code` of the 400 for a change that loses values without `confirmRemoval`. */
export const REMOVAL_NOT_CONFIRMED = 'removal_not_confirmed';

/** German texts for the schema-specific field error codes; others use the shared texts. */
const SCHEMA_MESSAGES: Record<string, string> = {
  reserved:
    'Dieser Key ist reserviert (id, collection, space, revision, body und title). Bitte einen anderen wählen.',
  duplicate: 'Kommt bereits vor. Bitte einen anderen Wert wählen.',
  key_immutable: 'Keys lassen sich nicht ändern. Lege bei Bedarf eine neue Property an.',
  type_change_not_allowed:
    'Dieser Typwechsel ist nicht erlaubt (nur Text ↔ Markdown und Auswahl → Mehrfachauswahl).',
  target_archived:
    'Die Ziel-Collection ist archiviert und kann kein neues Ziel einer Relation werden.',
  not_found: 'Nicht gefunden. Möglicherweise wurde es inzwischen entfernt.',
  invalid_value: 'Ungültiger Wert.',
  required: 'Pflichtfeld.',
};

/** User-facing message for a field error of `PATCH …/schema`. */
export function schemaFieldMessage(error: ProblemFieldError): string {
  const text = SCHEMA_MESSAGES[error.code];
  if (!text) {
    return fieldErrorMessage(error);
  }
  return error.allowed?.length && error.code !== 'reserved'
    ? `${text} Erlaubt: ${error.allowed.join(', ')}.`
    : text;
}

/** One sentence for errors of a schema change that belong to no field. */
export function describeSchemaError(error: unknown): string {
  const apiError = ApiError.from(error);
  if (apiError.code === SCHEMA_VERSION_CONFLICT) {
    return 'Das Schema wurde inzwischen geändert.';
  }
  return describeError(apiError);
}

/** Names of the dialog's fields, for errors that cannot be shown at a locked field. */
const FIELD_LABELS: Record<string, string> = {
  key: 'Key',
  name: 'Name',
  type: 'Typ',
  description: 'Beschreibung',
  required: 'Pflicht',
  options: 'Optionen',
  unit: 'Einheit',
  precision: 'Nachkommastellen',
  targetCollectionId: 'Ziel-Collection',
  multiple: 'Mehrere Verweise',
  default: 'Standardwert',
};

/** The field of the draft form a path relative to the entry (`.options[2].value`) points to. */
function resolve(
  form: FieldTree<PropertyDraft>,
  path: string,
): FieldTree<unknown> | null | undefined {
  const segments = path.match(/[a-zA-Z]+|\[\d+\]/g) ?? [];
  if (segments.length === 0) {
    return undefined;
  }
  let node: unknown = form;
  for (const segment of segments) {
    const next: unknown = segment.startsWith('[')
      ? (node as Record<number, unknown>)[Number(segment.slice(1, -1))]
      : (node as Record<string, unknown>)[segment];
    if (typeof next !== 'function') {
      // deeper than the form knows (e.g. default.items[0]): the error stays on the last field
      return node === form ? null : (node as FieldTree<unknown>);
    }
    node = next;
  }
  return node as FieldTree<unknown>;
}

/** Field errors for the form plus a message for everything that matches no field. */
export interface SchemaFormErrors {
  readonly fieldErrors: ServerValidationError[];
  readonly formMessage: string | null;
}

/**
 * Maps a failed `PATCH …/schema` onto the property dialog. `entry` is the path of the dialog's
 * entry in the request (`add[0]`, `update[0]`); its errors land on the matching field
 * (`add[0].options[1].color` → the color of the second option); errors of locked or hidden
 * fields (e.g. `update[0].key`) and everything else become the form-level message.
 */
export function mapSchemaErrors(
  error: unknown,
  entry: string,
  form: FieldTree<PropertyDraft>,
): SchemaFormErrors {
  const apiError = ApiError.from(error);
  if (apiError.errors.length === 0) {
    return { fieldErrors: [], formMessage: describeSchemaError(apiError) };
  }
  const fieldErrors: ServerValidationError[] = [];
  const unmatched: string[] = [];
  for (const e of apiError.errors) {
    const message = schemaFieldMessage(e);
    const field =
      e.field === entry || e.field.startsWith(`${entry}.`) || e.field.startsWith(`${entry}[`)
        ? resolve(form, e.field.slice(entry.length))
        : null;
    if (field && (field().disabled() || field().hidden())) {
      // a locked or hidden field shows no errors: say it above the form, with the field's name
      const label = FIELD_LABELS[e.field.slice(entry.length).match(/[a-zA-Z]+/)?.[0] ?? ''];
      unmatched.push(label ? `${label}: ${message}` : message);
    } else if (field) {
      fieldErrors.push({
        kind: SERVER_ERROR_KIND,
        code: e.code,
        field: e.field,
        message,
        fieldTree: field,
      });
    } else {
      unmatched.push(message);
    }
  }
  return { fieldErrors, formMessage: unmatched.length > 0 ? unmatched.join('\n') : null };
}
