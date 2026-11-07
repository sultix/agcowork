import { SchemaPath, maxLength, minLength, required, validate } from '@angular/forms/signals';

/** Bounds of the API for `newPassword` / initial passwords. */
export const MIN_PASSWORD_LENGTH = 12;
export const MAX_PASSWORD_LENGTH = 256;

type RequiredOptions = NonNullable<Parameters<typeof required<string>>[1]>;

/**
 * Client-side rules for a new password: required, at least {@link MIN_PASSWORD_LENGTH},
 * at most {@link MAX_PASSWORD_LENGTH} characters. `when` restricts all rules (e.g. humans only).
 */
export function applyPasswordPolicy(
  path: SchemaPath<string>,
  options: { requiredMessage?: string; when?: RequiredOptions['when'] } = {},
): void {
  const { requiredMessage = 'Bitte ein neues Passwort eingeben.', when } = options;
  required(path, { message: requiredMessage, when });
  minLength(path, MIN_PASSWORD_LENGTH, {
    message: `Das Passwort braucht mindestens ${MIN_PASSWORD_LENGTH} Zeichen.`,
    when,
  });
  maxLength(path, MAX_PASSWORD_LENGTH, {
    message: `Das Passwort darf höchstens ${MAX_PASSWORD_LENGTH} Zeichen haben.`,
    when,
  });
}

/** The repetition field must equal the new password field. */
export function applyRepeatRule(repeat: SchemaPath<string>, password: SchemaPath<string>): void {
  validate(repeat, ({ value, valueOf }) =>
    value() === valueOf(password)
      ? undefined
      : { kind: 'mismatch', message: 'Die Wiederholung stimmt nicht überein.' },
  );
}
