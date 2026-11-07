import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ApiError } from '../../core/api/api-error';
import { SessionStore } from '../../core/auth/session.store';
import { INVALID_CREDENTIALS_MESSAGE, LoginComponent, safeReturnUrl } from './login.component';

@Component({ template: 'target' })
class TargetComponent {}

describe('LoginComponent', () => {
  let harness: RouterTestingHarness;
  let session: { login: ReturnType<typeof vi.fn> };

  beforeEach(async () => {
    session = { login: vi.fn().mockResolvedValue(undefined) };
    TestBed.configureTestingModule({
      providers: [
        provideRouter(
          [
            { path: 'login', component: LoginComponent },
            { path: 'workspaces', component: TargetComponent },
            { path: 'w/:slug', component: TargetComponent },
          ],
          withComponentInputBinding(),
        ),
        { provide: SessionStore, useValue: session },
      ],
    });
    harness = await RouterTestingHarness.create();
  });

  function root(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  function text(testId: string): string {
    return root().querySelector(`[data-testid="${testId}"]`)?.textContent?.trim() ?? '';
  }

  async function type(id: string, value: string): Promise<void> {
    const input = root().querySelector<HTMLInputElement>(`#${id}`)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await harness.fixture.whenStable();
  }

  async function submit(): Promise<void> {
    root().querySelector('form')!.requestSubmit();
    await harness.fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve));
    await harness.fixture.whenStable();
  }

  async function fillAndSubmit(email: string, password: string): Promise<void> {
    await type('login-email', email);
    await type('login-password', password);
    await submit();
  }

  it('submit_EmptyForm_ShowsRequiredErrorsWithoutCallingApi', async () => {
    await harness.navigateByUrl('/login');

    await submit();

    expect(text('email-errors')).toBe('Bitte die E-Mail-Adresse eingeben.');
    expect(text('password-errors')).toBe('Bitte das Passwort eingeben.');
    expect(session.login).not.toHaveBeenCalled();
  });

  it('submit_InvalidEmail_ShowsFormatError', async () => {
    await harness.navigateByUrl('/login');

    await fillAndSubmit('not-an-email', 'secret');

    expect(text('email-errors')).toBe('Bitte eine gültige E-Mail-Adresse eingeben.');
    expect(text('password-errors')).toBe('');
    expect(session.login).not.toHaveBeenCalled();
  });

  it('submit_WrongCredentials_ShowsGenericMessageOnly', async () => {
    session.login.mockRejectedValue(new ApiError(401, 'unauthorized'));
    await harness.navigateByUrl('/login');

    await fillAndSubmit('admin@agcowork.local', 'wrong');

    expect(session.login).toHaveBeenCalledWith('admin@agcowork.local', 'wrong');
    expect(text('form-error')).toBe(INVALID_CREDENTIALS_MESSAGE);
    expect(text('email-errors')).toBe('');
    expect(text('password-errors')).toBe('');
    expect(TestBed.inject(Router).url).toBe('/login');
  });

  it('submit_ServerFieldErrors_AreShownAtTheirFields', async () => {
    session.login.mockRejectedValue(
      new ApiError(400, 'validation_failed', [
        { field: 'password', code: 'required', message: "'password' is required." },
        { field: 'other', code: 'weird', message: 'Something else.' },
      ]),
    );
    await harness.navigateByUrl('/login');

    await fillAndSubmit('admin@agcowork.local', 'x');

    expect(text('password-errors')).toBe('Pflichtfeld.');
    expect(text('email-errors')).toBe('');
    expect(text('form-error')).toBe('Something else.');
  });

  it('submit_NetworkError_ShowsServerUnreachable', async () => {
    session.login.mockRejectedValue(new ApiError(0, 'network_error'));
    await harness.navigateByUrl('/login');

    await fillAndSubmit('admin@agcowork.local', 'x');

    expect(text('form-error')).toContain('nicht erreichbar');
  });

  it('submit_Success_NavigatesToReturnUrl', async () => {
    await harness.navigateByUrl('/login?returnUrl=%2Fw%2Fdemo');

    await fillAndSubmit('admin@agcowork.local', 'secret');

    expect(TestBed.inject(Router).url).toBe('/w/demo');
  });

  it('submit_SuccessWithoutReturnUrl_NavigatesToWorkspaces', async () => {
    await harness.navigateByUrl('/login');

    await fillAndSubmit('admin@agcowork.local', 'secret');

    expect(TestBed.inject(Router).url).toBe('/workspaces');
  });

  it('submit_ExternalReturnUrl_IsIgnored', async () => {
    await harness.navigateByUrl('/login?returnUrl=%2F%2Fevil.example');

    await fillAndSubmit('admin@agcowork.local', 'secret');

    expect(TestBed.inject(Router).url).toBe('/workspaces');
  });

  it('form_SubmitButton_IsSubmitTypeSoEnterSends', async () => {
    await harness.navigateByUrl('/login');

    const button = root().querySelector('form button')!;
    expect(button.getAttribute('type')).toBe('submit');
    expect(root().querySelector('#login-email')!.closest('form')).toBe(
      root().querySelector('form'),
    );
  });

  it.each([
    [undefined, '/workspaces'],
    ['', '/workspaces'],
    ['/w/demo?x=1', '/w/demo?x=1'],
    ['//evil.example', '/workspaces'],
    ['/\\evil.example', '/workspaces'],
    ['https://evil.example', '/workspaces'],
    ['/login?returnUrl=%2Fw', '/workspaces'],
  ])('safeReturnUrl_%s_Is_%s', (input, expected) => {
    expect(safeReturnUrl(input)).toBe(expected);
  });
});
