import { Injector, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { form } from '@angular/forms/signals';
import { ApiError } from './api-error';
import {
  SERVER_ERROR_KIND,
  fieldErrorMessage,
  fieldErrorsByName,
  toServerErrors,
} from './field-errors';

describe('field errors', () => {
  const error = new ApiError(400, 'validation_failed', [
    { field: 'email', code: 'required', message: "'email' is required." },
    {
      field: 'instanceScopes[1]',
      code: 'not_in_options',
      message: 'Must be one of: instance:admin.',
      allowed: ['instance:admin'],
    },
    { field: 'unknown', code: 'custom_code', message: 'Server says no.' },
  ]);

  it('toServerErrors_KnownFields_AttachToFieldTreesAndListPathsToRoot', () => {
    const model = signal({ email: '', instanceScopes: [] as string[] });
    const f = form(model, { injector: TestBed.inject(Injector) });

    const result = toServerErrors(error, { email: f.email, instanceScopes: f.instanceScopes });

    expect(result.map((e) => e.kind)).toEqual([
      SERVER_ERROR_KIND,
      SERVER_ERROR_KIND,
      SERVER_ERROR_KIND,
    ]);
    expect(result[0].fieldTree).toBe(f.email);
    expect(result[0].message).toBe('Pflichtfeld.');
    expect(result[1].fieldTree).toBe(f.instanceScopes);
    expect(result[1].message).toBe('Wert ist nicht erlaubt. Erlaubt: instance:admin.');
    expect(result[2].fieldTree).toBeUndefined();
    expect(result[2].message).toBe('Server says no.');
  });

  it('fieldErrorsByName_GroupsByRootField', () => {
    expect(fieldErrorsByName(error)).toEqual({
      email: ['Pflichtfeld.'],
      instanceScopes: ['Wert ist nicht erlaubt. Erlaubt: instance:admin.'],
      unknown: ['Server says no.'],
    });
  });
});

describe('fieldErrorMessage', () => {
  // Messages as the server sends them (FieldErrors.cs, PropertyValueValidator.cs, CreateRecord.cs).
  it('fieldErrorMessage_TooLong_AppendsLimitWithoutFieldName', () => {
    expect(
      fieldErrorMessage({
        field: 'title',
        code: 'too_long',
        message: "'title' must not exceed 500 characters.",
      }),
    ).toBe('Zu lang. Hinweis: Must not exceed 500 characters.');
  });

  it('fieldErrorMessage_OutOfRangeAndNotAllowed_AppendReason', () => {
    expect(
      fieldErrorMessage({
        field: 'expiresAt',
        code: 'out_of_range',
        message: "'expiresAt' must lie in the future.",
      }),
    ).toBe('Wert liegt außerhalb des erlaubten Bereichs. Hinweis: Must lie in the future.');
    expect(
      fieldErrorMessage({
        field: 'templateId',
        code: 'not_allowed',
        message: 'Free pages have no templates; templates belong to collections.',
      }),
    ).toBe(
      'Nicht erlaubt. Hinweis: Free pages have no templates; templates belong to collections.',
    );
  });

  it('fieldErrorMessage_CodesOutsideTheAllowlist_KeepGermanTextOnly', () => {
    const cases: [string, string, string][] = [
      ['required', "'title' is required.", 'Pflichtfeld.'],
      [
        'conflict',
        "'skipTemplate: true' starts without any template and cannot be combined with 'templateId'.",
        'Widerspricht einer anderen Angabe.',
      ],
      ['unchanged', 'Same.', 'Das neue Passwort muss sich vom bisherigen unterscheiden.'],
    ];
    for (const [code, message, expected] of cases) {
      expect(fieldErrorMessage({ field: 'a', code, message }), code).toBe(expected);
    }
  });

  it('fieldErrorMessage_NotInOptions_ShowsAllowedWithoutServerMessage', () => {
    expect(
      fieldErrorMessage({
        field: 'status',
        code: 'not_in_options',
        message: "'x' is not a valid value for 'status'.",
        allowed: ['todo', 'done'],
      }),
    ).toBe('Wert ist nicht erlaubt. Erlaubt: todo, done.');
  });

  it('fieldErrorMessage_UnknownCode_UsesNormalizedServerMessage', () => {
    expect(fieldErrorMessage({ field: 'a', code: 'custom', message: ' Server\n says  no. ' })).toBe(
      'Server says no.',
    );
  });

  it('fieldErrorMessage_DetailWithMarkup_StaysPlainText', () => {
    const text = fieldErrorMessage({
      field: 'a',
      code: 'not_allowed',
      message: '<img src=x onerror=1>',
    });

    expect(text).toBe('Nicht erlaubt. Hinweis: <img src=x onerror=1>');
  });

  it('fieldErrorMessage_VeryLongMessage_IsCutWithoutSplittingAnEmoji', () => {
    for (const code of ['too_long', 'custom']) {
      // 299 characters, then a surrogate pair that straddles the cut
      const message = `${'x'.repeat(298)}😀${'y'.repeat(100)}`;
      const text = fieldErrorMessage({ field: 'a', code, message });

      expect(text.endsWith('…'), code).toBe(true);
      expect(text, code).not.toMatch(/[\ud800-\udbff]…$/);
      expect(text, code).not.toContain('😀');
    }
  });
});
