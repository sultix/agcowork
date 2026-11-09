import { HttpErrorResponse } from '@angular/common/http';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ApiError } from '../../core/api/api-error';
import { Me, SessionStore } from '../../core/auth/session.store';
import { ADMIN_ME } from '../../core/testing/test-utils';
import { ChangePasswordComponent } from './change-password.component';

@Component({ template: 'target' })
class TargetComponent {}

describe('ChangePasswordComponent', () => {
  let harness: RouterTestingHarness;
  let me: ReturnType<typeof signal<Me | null>>;
  let session: {
    me: typeof me;
    changePassword: ReturnType<typeof vi.fn>;
    logout: ReturnType<typeof vi.fn>;
  };

  async function render(mustChangePassword: boolean, url = '/account/password'): Promise<void> {
    me = signal<Me | null>({ ...ADMIN_ME, mustChangePassword });
    session = {
      me,
      changePassword: vi.fn().mockResolvedValue(undefined),
      logout: vi.fn().mockResolvedValue(undefined),
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter(
          [
            { path: 'account/password', component: ChangePasswordComponent },
            { path: 'login', component: TargetComponent },
            { path: 'workspaces', component: TargetComponent },
            { path: 'w/:slug', component: TargetComponent },
          ],
          withComponentInputBinding(),
        ),
        { provide: SessionStore, useValue: session },
      ],
    });
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
  }

  const root = () => harness.routeNativeElement as HTMLElement;
  const q = (id: string) => root().querySelector<HTMLElement>(`[data-testid="${id}"]`);
  const text = (id: string) => q(id)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

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

  async function fill(current: string, next: string, repeat: string): Promise<void> {
    await type('pw-current', current);
    await type('pw-new', next);
    await type('pw-repeat', repeat);
    await submit();
  }

  const NEW_PASSWORD = 'a-new-password-1';

  it('submit_Valid_CallsApiShowsSuccessAndContinuesToReturnUrl', async () => {
    await render(false, '/account/password?returnUrl=%2Fw%2Fdemo');

    await fill('old-password-1', NEW_PASSWORD, NEW_PASSWORD);

    expect(session.changePassword).toHaveBeenCalledWith('old-password-1', NEW_PASSWORD);
    expect(text('success')).toContain('Dein Passwort wurde geändert');
    expect(q('submit')).toBeNull();
    expect(document.activeElement).toBe(q('continue'));

    q('continue')!.click();
    await harness.fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve));
    expect(TestBed.inject(Router).url).toBe('/w/demo');
  });

  it('submit_RepeatDiffers_ShowsErrorAndDoesNotCallApi', async () => {
    await render(false);

    await fill('old-password-1', NEW_PASSWORD, 'something-else-123');

    expect(text('repeat-errors')).toBe('Die Wiederholung stimmt nicht überein.');
    expect(session.changePassword).not.toHaveBeenCalled();
  });

  it('submit_EmptyOrShort_ShowsClientErrors', async () => {
    await render(false);

    await fill('', 'short', 'short');

    expect(text('current-errors')).toBe('Bitte das aktuelle Passwort eingeben.');
    expect(text('new-errors')).toContain('mindestens 12 Zeichen');
    expect(session.changePassword).not.toHaveBeenCalled();
  });

  it('submit_WrongCurrentPassword_ShowsErrorAtCurrentField', async () => {
    await render(false);
    session.changePassword.mockRejectedValue(
      new ApiError(400, 'validation_failed', [
        { field: 'currentPassword', code: 'invalid_credentials', message: 'Wrong.' },
      ]),
    );

    await fill('wrong-password-1', NEW_PASSWORD, NEW_PASSWORD);

    expect(text('current-errors')).toBe('Das aktuelle Passwort ist falsch.');
    expect(text('new-errors')).toBe('');
    expect(q('success')).toBeNull();
  });

  it('submit_UnchangedPassword_ShowsErrorAtNewField', async () => {
    await render(false);
    session.changePassword.mockRejectedValue(
      new ApiError(400, 'validation_failed', [
        { field: 'newPassword', code: 'unchanged', message: 'Same.' },
      ]),
    );

    await fill(NEW_PASSWORD, NEW_PASSWORD, NEW_PASSWORD);

    expect(text('new-errors')).toBe('Das neue Passwort muss sich vom bisherigen unterscheiden.');
  });

  it('submit_TooManyAttemptsUnderAMinute_ShowsTryAgainShortlyAndNoFieldError', async () => {
    await render(false);
    session.changePassword.mockRejectedValue(
      new HttpErrorResponse({
        status: 429,
        error: {
          status: 429,
          code: 'rate_limited',
          detail: 'Too many failed attempts.',
          retryAfterSeconds: 45,
        },
      }),
    );

    await fill('old-password-1', NEW_PASSWORD, NEW_PASSWORD);

    expect(text('form-error')).toBe('Zu viele Versuche. Bitte gleich erneut versuchen.');
    expect(text('current-errors')).toBe('');
    expect(text('new-errors')).toBe('');
    expect(q('success')).toBeNull();
  });

  it('submit_ServerErrorWithoutField_ShowsFormError', async () => {
    await render(false);
    session.changePassword.mockRejectedValue(new ApiError(0, 'network_error'));

    await fill('old-password-1', NEW_PASSWORD, NEW_PASSWORD);

    expect(text('form-error')).toContain('nicht erreichbar');
  });

  it('render_Voluntary_OffersCancelBackToReturnUrl', async () => {
    await render(false, '/account/password?returnUrl=%2Fw%2Fdemo');

    expect(q('forced-hint')).toBeNull();
    expect(q('logout')).toBeNull();

    q('cancel')!.click();
    await harness.fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve));
    expect(TestBed.inject(Router).url).toBe('/w/demo');
  });

  it('render_MustChangePassword_ExplainsAndOffersLogoutInsteadOfCancel', async () => {
    await render(true);

    expect(text('forced-hint')).toContain('vorläufigen Passwort');
    expect(q('cancel')).toBeNull();

    q('logout')!.click();
    await harness.fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve));
    expect(session.logout).toHaveBeenCalled();
    expect(TestBed.inject(Router).url).toBe('/login');
  });

  it('submit_AfterForcedChange_HintIsGone', async () => {
    await render(true);

    await fill('old-password-1', NEW_PASSWORD, NEW_PASSWORD);

    expect(q('forced-hint')).toBeNull();
    expect(q('success')).not.toBeNull();
  });
});
