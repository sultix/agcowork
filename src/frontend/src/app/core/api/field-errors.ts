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
  conflict: 'Wert ist bereits vergeben.',
  invalid_credentials: 'Das aktuelle Passwort ist falsch.',
  unchanged: 'Das neue Passwort muss sich vom bisherigen unterscheiden.',
};

/** User-facing message for one field error of the API. */
export function fieldErrorMessage(error: ProblemFieldError): string {
  const text = MESSAGES[error.code] ?? error.message;
  return error.allowed?.length ? `${text} Erlaubt: ${error.allowed.join(', ')}.` : text;
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
