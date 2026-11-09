import type { FieldTree, ValidationError } from '@angular/forms/signals';
import { ApiError, ProblemFieldError, fieldRoot } from './api-error';

/** `kind` of validation errors that came from the API (as opposed to client-side validators). */
export const SERVER_ERROR_KIND = 'server';

/** A server-side validation error as a Signal Forms error; `code` is the API's error code. */
export interface ServerValidationError extends ValidationError.WithOptionalFieldTree {
  readonly kind: typeof SERVER_ERROR_KIND;
  readonly code: string;
  readonly message: string;
  readonly field?: string;
}

/** German texts for the API's field error codes; unknown codes fall back to the API message. */
const MESSAGES: Record<string, string> = {
  required: 'Pflichtfeld.',
  invalid_format: 'Ungültiges Format.',
  not_in_options: 'Wert ist nicht erlaubt.',
  too_long: 'Zu lang.',
  too_short: 'Zu kurz.',
  out_of_range: 'Wert liegt außerhalb des erlaubten Bereichs.',
  not_allowed: 'Nicht erlaubt.',
  conflict: 'Widerspricht einer anderen Angabe.',
  invalid_credentials: 'Das aktuelle Passwort ist falsch.',
  unchanged: 'Das neue Passwort muss sich vom bisherigen unterscheiden.',
};

/** Longest server message shown; longer ones are cut. */
const MAX_DETAIL_LENGTH = 300;

/**
 * Codes whose German text is generic and whose server message carries limits, formats or
 * reasons ("must not exceed 500 characters"): the message is appended as "Hinweis". For all
 * other known codes the German text (plus the allowed values) says it all.
 */
const DETAIL_CODES: ReadonlySet<string> = new Set([
  'too_long',
  'too_short',
  'out_of_range',
  'invalid_format',
  'not_allowed',
  'removal_not_confirmed',
]);

/** Whitespace collapsed, cut at {@link MAX_DETAIL_LENGTH} without splitting a surrogate pair. */
function normalize(message: string | undefined): string {
  const text = (message ?? '').replace(/\s+/g, ' ').trim();
  if (text.length <= MAX_DETAIL_LENGTH) {
    return text;
  }
  let cut = MAX_DETAIL_LENGTH - 1;
  const last = text.charCodeAt(cut - 1);
  if (last >= 0xd800 && last <= 0xdbff) {
    cut--;
  }
  return `${text.slice(0, cut)}…`;
}

/** The server message without a leading `'<field>' ` that names the field the error sits at. */
function withoutFieldPrefix(message: string, field: string): string {
  const prefix = `'${field}' `;
  if (!message.startsWith(prefix) || message.length === prefix.length) {
    return message;
  }
  const rest = message.slice(prefix.length);
  return rest[0].toUpperCase() + rest.slice(1);
}

function withAllowed(text: string, error: ProblemFieldError): string {
  const allowed = error.allowed ?? [];
  return allowed.length > 0 ? `${text} Erlaubt: ${allowed.join(', ')}.` : text;
}

/**
 * `text` (the German message for the error's code) plus the allowed values and, for the codes in
 * {@link DETAIL_CODES}, the server `message` as "Hinweis" (without a leading field name; omitted
 * when empty or equal to the text). Plain text; the templates escape it. Callers whose German
 * text is complete (`reserved`, `template_name_taken`) return it themselves instead.
 */
export function composeFieldMessage(text: string, error: ProblemFieldError): string {
  const result = withAllowed(text, error);
  if (!DETAIL_CODES.has(error.code)) {
    return result;
  }
  const detail = withoutFieldPrefix(normalize(error.message), error.field);
  return detail === '' || detail === text ? result : `${result} Hinweis: ${detail}`;
}

/**
 * User-facing message for one field error of the API: the German text of a known code, the
 * (normalized, cut) server message for unknown codes.
 */
export function fieldErrorMessage(error: ProblemFieldError): string {
  const text = MESSAGES[error.code];
  return text ? composeFieldMessage(text, error) : withAllowed(normalize(error.message), error);
}

/**
 * Maps `errors[]` of an {@link ApiError} onto form fields. `fields` maps API field names
 * (camelCase, as in the request body) to Signal Forms fields; list and child paths
 * (`scopes[1]`, `address.street`) land on their root field. Errors without a matching field
 * come back without `fieldTree`, so a submit action attaches them to the form itself.
 *
 * Use as the result of a Signal Forms submit action:
 * `catch (e) { return toServerErrors(ApiError.from(e), { email: f.email }); }`
 */
export function toServerErrors(
  error: ApiError,
  fields: Readonly<Partial<Record<string, FieldTree<unknown>>>>,
): ServerValidationError[] {
  return error.errors.map((e) => {
    const fieldTree: FieldTree<unknown> | undefined = fields[e.field] ?? fields[fieldRoot(e.field)];
    return {
      kind: SERVER_ERROR_KIND,
      code: e.code,
      field: e.field,
      message: fieldErrorMessage(e),
      ...(fieldTree ? { fieldTree } : {}),
    };
  });
}

/** Field errors grouped by root field name, for forms that do not use Signal Forms. */
export function fieldErrorsByName(error: ApiError): Record<string, string[]> {
  const result: Record<string, string[]> = {};
  for (const e of error.errors) {
    (result[fieldRoot(e.field)] ??= []).push(fieldErrorMessage(e));
  }
  return result;
}
