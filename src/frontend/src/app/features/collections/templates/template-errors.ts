import { ApiError, ProblemFieldError, fieldRoot } from '../../../core/api/api-error';
import { composeFieldMessage, fieldErrorMessage } from '../../../core/api/field-errors';
import { describeError } from '../../../shared/ui/api-error-text';

/** German texts for the template-specific codes; others use the shared texts. */
const TEMPLATE_TEXTS: Record<string, string> = {
  template_name_taken:
    'Die Collection hat schon eine Vorlage mit diesem Namen. Bitte einen anderen wählen.',
  template_default_conflict:
    'Gleichzeitig wurde eine andere Vorlage zur Standardvorlage. Die Liste ist neu geladen; bitte erneut versuchen.',
  unknown_property: 'Diese Property gibt es im Schema nicht (mehr).',
};

const PRESET_PREFIX = 'propertiesPreset';

/** Codes whose German text is complete: it comes back as it is (no allowed list). */
const TEMPLATE_COMPLETE: ReadonlySet<string> = new Set([
  'template_name_taken',
  'template_default_conflict',
]);

/** Message for one field error of a template request. */
export function templateFieldMessage(error: ProblemFieldError): string {
  const text = TEMPLATE_TEXTS[error.code];
  if (!text) {
    return fieldErrorMessage(error);
  }
  return TEMPLATE_COMPLETE.has(error.code) ? text : composeFieldMessage(text, error);
}

/** One sentence for an error of a template request that belongs to no field. */
export function describeTemplateError(error: unknown): string {
  const apiError = ApiError.from(error);
  return TEMPLATE_TEXTS[apiError.code] ?? describeError(apiError);
}

/** Field a template error belongs to: a form field, a preset property or neither. */
export interface TemplateError {
  readonly kind: 'field' | 'preset' | 'form';
  /** Form field name (`name`, `description`, `bodyMarkdown`, `isDefault`) or property key. */
  readonly target: string;
  readonly message: string;
}

/**
 * Sorts the errors of a failed create/update: `propertiesPreset.<key>` belongs to that property's
 * editor, `name`/`description`/`bodyMarkdown`/`isDefault` to their fields, everything else (also
 * a 409 without field) to the form.
 */
export function mapTemplateErrors(error: unknown): TemplateError[] {
  const apiError = ApiError.from(error);
  if (apiError.errors.length === 0) {
    return [{ kind: 'form', target: '', message: describeTemplateError(apiError) }];
  }
  return apiError.errors.map((e): TemplateError => {
    const message = templateFieldMessage(e);
    const root = fieldRoot(e.field);
    if (root === PRESET_PREFIX) {
      const key = /^propertiesPreset\.([^.[]+)/.exec(e.field)?.[1];
      return key ? { kind: 'preset', target: key, message } : { kind: 'form', target: '', message };
    }
    if (['name', 'description', 'bodyMarkdown', 'isDefault'].includes(root)) {
      return { kind: 'field', target: root, message };
    }
    return { kind: 'form', target: '', message };
  });
}
