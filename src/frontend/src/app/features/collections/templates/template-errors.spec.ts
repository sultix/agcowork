import { ApiError } from '../../../core/api/api-error';
import { mapTemplateErrors, templateFieldMessage } from './template-errors';

// Messages as the server sends them (PropertyValueValidator.cs, FieldErrors.cs).
describe('templateFieldMessage', () => {
  it('templateFieldMessage_UnknownProperty_GermanTextOnly', () => {
    expect(
      templateFieldMessage({
        field: 'propertiesPreset.foo',
        code: 'unknown_property',
        message: "The collection has no property 'foo'. Use one of the allowed keys.",
      }),
    ).toBe('Diese Property gibt es im Schema nicht (mehr).');
  });

  it('templateFieldMessage_CompleteCodes_KeepGermanTextOnly', () => {
    expect(
      templateFieldMessage({ field: 'name', code: 'template_name_taken', message: 'Name taken.' }),
    ).toBe('Die Collection hat schon eine Vorlage mit diesem Namen. Bitte einen anderen wählen.');
  });

  it('templateFieldMessage_TooLong_UsesSharedTextWithLimit', () => {
    expect(
      templateFieldMessage({
        field: 'name',
        code: 'too_long',
        message: "'name' must not exceed 120 characters.",
      }),
    ).toBe('Zu lang. Hinweis: Must not exceed 120 characters.');
  });

  it('mapTemplateErrors_PresetNotInOptions_ShowsAllowedAtTheProperty', () => {
    const [mapped] = mapTemplateErrors(
      new ApiError(400, 'validation_failed', [
        {
          field: 'propertiesPreset.status',
          code: 'not_in_options',
          message: "'done' is not a valid value for 'propertiesPreset.status'.",
          allowed: ['todo'],
        },
      ]),
    );

    expect(mapped).toEqual({
      kind: 'preset',
      target: 'status',
      message: 'Wert ist nicht erlaubt. Erlaubt: todo.',
    });
  });
});
