import { Injector, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { form } from '@angular/forms/signals';
import { ApiError } from './api-error';
import { SERVER_ERROR_KIND, fieldErrorsByName, toServerErrors } from './field-errors';

describe('field errors', () => {
  const error = new ApiError(400, 'validation_failed', [
    { field: 'email', code: 'required', message: "'email' is required." },
    {
      field: 'instanceScopes[1]',
      code: 'not_in_options',
      message: 'bad',
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
