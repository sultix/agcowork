import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { authGuard, signedInGuard } from './auth.guard';
import { Me, SessionStatus, SessionStore } from './session.store';
import { ADMIN_ME } from '../testing/test-utils';

@Component({ template: 'page' })
class PageComponent {}

describe('authGuard', () => {
  let resolveReady: (status: SessionStatus) => void;
  let session: { ready: ReturnType<typeof vi.fn>; me: () => Me | null };
  let me: Me | null;

  beforeEach(() => {
    me = ADMIN_ME;
    session = {
      me: () => me,
      ready: vi.fn(
        () =>
          new Promise<SessionStatus>((resolve) => {
            resolveReady = resolve;
          }),
      ),
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'login', component: PageComponent },
          { path: 'account/password', canActivate: [signedInGuard], component: PageComponent },
          { path: 'w/:slug', canActivate: [authGuard], component: PageComponent },
        ]),
        { provide: SessionStore, useValue: session },
      ],
    });
  });

  it('canActivate_Anonymous_RedirectsToLoginWithReturnUrl', async () => {
    const harness = await RouterTestingHarness.create();

    const navigation = harness.navigateByUrl('/w/demo');
    await vi.waitFor(() => expect(session.ready).toHaveBeenCalled());
    resolveReady('anonymous');
    await navigation;

    const router = TestBed.inject(Router);
    expect(router.url).toBe('/login?returnUrl=%2Fw%2Fdemo');
    expect(router.parseUrl(router.url).queryParams['returnUrl']).toBe('/w/demo');
  });

  it('canActivate_Authenticated_AllowsNavigation', async () => {
    const harness = await RouterTestingHarness.create();

    const navigation = harness.navigateByUrl('/w/demo');
    await vi.waitFor(() => expect(session.ready).toHaveBeenCalled());
    resolveReady('authenticated');
    await navigation;

    expect(TestBed.inject(Router).url).toBe('/w/demo');
  });

  it('canActivate_StatusUnknown_WaitsForSession', async () => {
    const harness = await RouterTestingHarness.create();
    const router = TestBed.inject(Router);

    const navigation = harness.navigateByUrl('/w/demo');
    await vi.waitFor(() => expect(session.ready).toHaveBeenCalled());
    await new Promise((resolve) => setTimeout(resolve, 10));
    expect(router.url).toBe('/');

    resolveReady('authenticated');
    await navigation;
    expect(router.url).toBe('/w/demo');
  });

  it('canActivate_MustChangePassword_RedirectsToPasswordPageWithReturnUrl', async () => {
    me = { ...ADMIN_ME, mustChangePassword: true };
    const harness = await RouterTestingHarness.create();

    const navigation = harness.navigateByUrl('/w/demo');
    await vi.waitFor(() => expect(session.ready).toHaveBeenCalled());
    resolveReady('authenticated');
    // The redirect target has its own guard, which asks the session again.
    await vi.waitFor(() => expect(session.ready).toHaveBeenCalledTimes(2));
    resolveReady('authenticated');
    await navigation;

    const router = TestBed.inject(Router);
    expect(router.url).toBe('/account/password?returnUrl=%2Fw%2Fdemo');
  });

  it('signedInGuard_MustChangePassword_AllowsThePasswordPage', async () => {
    me = { ...ADMIN_ME, mustChangePassword: true };
    const harness = await RouterTestingHarness.create();

    const navigation = harness.navigateByUrl('/account/password');
    await vi.waitFor(() => expect(session.ready).toHaveBeenCalled());
    resolveReady('authenticated');
    await navigation;

    expect(TestBed.inject(Router).url).toBe('/account/password');
  });

  it('canActivate_PasswordChanged_AllowsNavigation', async () => {
    me = { ...ADMIN_ME, mustChangePassword: false };
    const harness = await RouterTestingHarness.create();

    const navigation = harness.navigateByUrl('/w/demo');
    await vi.waitFor(() => expect(session.ready).toHaveBeenCalled());
    resolveReady('authenticated');
    await navigation;

    expect(TestBed.inject(Router).url).toBe('/w/demo');
  });
});
