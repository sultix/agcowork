import type { FieldTree } from '@angular/forms/signals';
import { ApiError, NETWORK_ERROR_CODE } from '../../core/api/api-error';
import {
  ServerValidationError,
  fieldErrorMessage,
  toServerErrors,
} from '../../core/api/field-errors';

/** German texts for the API's domain error codes (`ApiError.code`). */
const ERROR_TEXTS: Record<string, string> = {
  last_owner:
    'Ein Workspace braucht mindestens einen Owner. Ernenne zuerst ein anderes Mitglied zum Owner.',
  self_escalation:
    'Die eigene Mitgliedschaft kann nur eingeschränkt, nicht erweitert werden. Bitte einen anderen Admin oder Owner darum.',
  already_member:
    'Dieser Actor ist bereits Mitglied. Rolle und Scopes lassen sich in der Liste ändern.',
  slug_taken: 'Dieser Slug ist bereits vergeben. Bitte einen anderen wählen.',
  email_taken: 'Diese E-Mail-Adresse wird bereits von einem anderen Actor verwendet.',
  self_lockout:
    'Du kannst dich nicht selbst deaktivieren und dir nicht selbst instance:admin entziehen.',
  workspace_archived: 'Der Workspace ist archiviert und damit schreibgeschützt.',
  forbidden: 'Dafür fehlen die nötigen Rechte.',
  not_found: 'Nicht gefunden. Möglicherweise wurde der Eintrag inzwischen gelöscht.',
  unauthorized: 'Die Sitzung ist abgelaufen. Bitte erneut anmelden.',
  [NETWORK_ERROR_CODE]: 'Server nicht erreichbar. Bitte später erneut versuchen.',
};

/** One user-facing sentence for an API error; falls back to the server's `detail`. */
export function describeError(error: unknown): string {
  const apiError = ApiError.from(error);
  if (apiError.code === 'validation_failed' && apiError.errors.length > 0) {
    return apiError.errors.map((e) => `${e.field}: ${fieldErrorMessage(e)}`).join('\n');
  }
  return (
    ERROR_TEXTS[apiError.code] ??
    apiError.detail ??
    (apiError.status >= 500
      ? 'Der Server hat einen Fehler gemeldet. Bitte später erneut versuchen.'
      : 'Aktion fehlgeschlagen. Bitte erneut versuchen.')
  );
}

/** Result of mapping an API error onto a form: errors for fields plus a message for the form itself. */
export interface FormErrors {
  readonly fieldErrors: ServerValidationError[];
  readonly formMessage: string | null;
}

/**
 * Maps a failed submit onto a Signal Forms form. Field errors of the API land on their field;
 * for conflicts with a known domain code (`slug_taken`, `already_member`, …) the field shows the
 * specific German text. Everything that matches no field becomes the form-level message.
 */
export function mapFormErrors(
  error: unknown,
  fields: Readonly<Partial<Record<string, FieldTree<unknown>>>>,
): FormErrors {
  const apiError = ApiError.from(error);
  const known = apiError.status === 409 ? ERROR_TEXTS[apiError.code] : undefined;
  const mapped = toServerErrors(apiError, fields).map((e) =>
    known ? { ...e, message: known } : e,
  );
  const unmatched = mapped.filter((e) => !e.fieldTree).map((e) => e.message);
  return {
    fieldErrors: mapped.filter((e) => e.fieldTree),
    formMessage:
      mapped.length === 0 ? describeError(apiError) : unmatched.length ? unmatched.join(' ') : null,
  };
}
