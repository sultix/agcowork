import { ApplicationRef } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { SKIP_AUTH_REDIRECT } from '../api/api-context';
import { ApiError } from '../api/api-error';
import { apiErrorInterceptor } from '../api/api-error.interceptor';
import { Me, SessionStore } from './session.store';

const ME: Me = {
  id: '0199a000-0000-7000-8000-000000000001',
  kind: 'human',
  name: 'Admin',
  role: null,
  email: 'admin@agcowork.local',
  instanceScopes: ['instance:admin'],
  apiKey: null,
  mustChangePassword: false,
};
const UNAUTHORIZED = { status: 401, statusText: 'Unauthorized' } as const;

describe('SessionStore', () => {
  let http: HttpTestingController;
  let store: SessionStore;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(SessionStore);
  });

  afterEach(() => http.verify());

  /** Waits until the resource/effects have issued the request, then returns it. */
  async function expectRequest(method: string, url: string): Promise<TestRequest> {
    let request: TestRequest | undefined;
    await vi.waitFor(() => {
      TestBed.tick();
      request = http.expectOne({ method, url });
    });
    return request!;
  }

  async function signIn(): Promise<void> {
    const loaded = store.load();
    (await expectRequest('GET', '/api/v1/me')).flush(ME);
    await loaded;
  }

  it('status_BeforeLoad_IsUnknownWithoutRequest', async () => {
    await TestBed.inject(ApplicationRef).whenStable();

    expect(store.status()).toBe('unknown');
    expect(store.me()).toBeNull();
    http.expectNone('/api/v1/me');
  });

  it('load_MeOk_IsAuthenticatedWithActor', async () => {
    const loaded = store.load();
    const request = await expectRequest('GET', '/api/v1/me');
    expect(store.status()).toBe('unknown');
    expect(request.request.withCredentials).toBe(true);
    request.flush(ME);

    await expect(loaded).resolves.toBe('authenticated');
    expect(store.status()).toBe('authenticated');
    expect(store.me()?.name).toBe('Admin');
  });

  it('load_Me401_IsAnonymousAndSkipsAuthRedirect', async () => {
    const loaded = store.load();
    const request = await expectRequest('GET', '/api/v1/me');
    expect(request.request.context.get(SKIP_AUTH_REDIRECT)).toBe(true);
    request.flush({ status: 401, code: 'unauthorized' }, UNAUTHORIZED);

    await expect(loaded).resolves.toBe('anonymous');
    expect(store.me()).toBeNull();
  });

  it('load_ServerError_IsAnonymous', async () => {
    const loaded = store.load();
    (await expectRequest('GET', '/api/v1/me')).flush('boom', {
      status: 500,
      statusText: 'Internal Server Error',
    });

    await expect(loaded).resolves.toBe('anonymous');
  });

  it('ready_NotStarted_StartsLoad', async () => {
    const ready = store.ready();
    (await expectRequest('GET', '/api/v1/me')).flush(ME);

    await expect(ready).resolves.toBe('authenticated');
  });

  it('login_ValidCredentials_PostsThenReloadsMe', async () => {
    const loaded = store.load();
    (await expectRequest('GET', '/api/v1/me')).flush({ code: 'unauthorized' }, UNAUTHORIZED);
    await loaded;

    const login = store.login('admin@agcowork.local', 'secret');
    const post = await expectRequest('POST', '/api/v1/auth/login');
    expect(post.request.body).toEqual({ email: 'admin@agcowork.local', password: 'secret' });
    post.flush(null, { status: 204, statusText: 'No Content' });
    (await expectRequest('GET', '/api/v1/me')).flush(ME);

    await expect(login).resolves.toBeUndefined();
    expect(store.status()).toBe('authenticated');
    expect(store.me()?.email).toBe('admin@agcowork.local');
  });

  it('login_WhileInitialMeStillLoading_LoadsMeAgainAndAuthenticates', async () => {
    const initial = store.load();
    const probe = await expectRequest('GET', '/api/v1/me');

    const login = store.login('admin@agcowork.local', 'secret');
    (await expectRequest('POST', '/api/v1/auth/login')).flush(null, {
      status: 204,
      statusText: 'No Content',
    });
    // The start-up probe was sent without cookie and answers 401 only now.
    probe.flush({ status: 401, code: 'unauthorized' }, UNAUTHORIZED);
    await initial;
    (await expectRequest('GET', '/api/v1/me')).flush(ME);

    await expect(login).resolves.toBeUndefined();
    expect(store.status()).toBe('authenticated');
  });

  it('login_AfterLogout_IsAuthenticatedAgain', async () => {
    await signIn();
    const logout = store.logout();
    (await expectRequest('POST', '/api/v1/auth/logout')).flush(null, {
      status: 204,
      statusText: 'No Content',
    });
    await logout;
    expect(store.status()).toBe('anonymous');

    const login = store.login('admin@agcowork.local', 'secret');
    (await expectRequest('POST', '/api/v1/auth/login')).flush(null, {
      status: 204,
      statusText: 'No Content',
    });
    (await expectRequest('GET', '/api/v1/me')).flush(ME);

    await expect(login).resolves.toBeUndefined();
    expect(store.status()).toBe('authenticated');
    expect(store.me()?.name).toBe('Admin');
  });

  it('login_AfterClearWithoutPriorLoad_IsAuthenticated', async () => {
    store.clear();
    expect(store.status()).toBe('anonymous');

    const login = store.login('admin@agcowork.local', 'secret');
    (await expectRequest('POST', '/api/v1/auth/login')).flush(null, {
      status: 204,
      statusText: 'No Content',
    });
    (await expectRequest('GET', '/api/v1/me')).flush(ME);

    await expect(login).resolves.toBeUndefined();
    expect(store.status()).toBe('authenticated');
  });

  it('login_WrongPassword_RejectsWithApiErrorAndStaysAnonymous', async () => {
    const login = store.login('admin@agcowork.local', 'wrong');
    (await expectRequest('POST', '/api/v1/auth/login')).flush(
      { status: 401, code: 'unauthorized' },
      UNAUTHORIZED,
    );

    const error = await login.catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(401);
    expect(store.me()).toBeNull();
  });

  it('changePassword_Valid_PatchesThenReloadsMeWithoutMustChange', async () => {
    const loaded = store.load();
    (await expectRequest('GET', '/api/v1/me')).flush({ ...ME, mustChangePassword: true });
    await loaded;
    expect(store.me()?.mustChangePassword).toBe(true);

    const change = store.changePassword('old-password-1', 'a-new-password-1');
    const patch = await expectRequest('PATCH', '/api/v1/me/password');
    expect(patch.request.body).toEqual({
      currentPassword: 'old-password-1',
      newPassword: 'a-new-password-1',
    });
    patch.flush(null, { status: 204, statusText: 'No Content' });
    (await expectRequest('GET', '/api/v1/me')).flush(ME);

    await expect(change).resolves.toBeUndefined();
    expect(store.me()?.mustChangePassword).toBe(false);
  });

  it('changePassword_WrongCurrent_RejectsWithFieldErrorAndKeepsSession', async () => {
    await signIn();

    const change = store.changePassword('wrong', 'a-new-password-1');
    (await expectRequest('PATCH', '/api/v1/me/password')).flush(
      {
        status: 400,
        code: 'validation_failed',
        errors: [{ field: 'currentPassword', code: 'invalid_credentials', message: 'Wrong.' }],
      },
      { status: 400, statusText: 'Bad Request' },
    );

    const error = (await change.catch((e: unknown) => e)) as ApiError;
    expect(error).toBeInstanceOf(ApiError);
    expect(error.errors[0].field).toBe('currentPassword');
    expect(store.status()).toBe('authenticated');
  });

  it('logout_Authenticated_PostsLogoutAndClears', async () => {
    await signIn();

    const logout = store.logout();
    (await expectRequest('POST', '/api/v1/auth/logout')).flush(null, {
      status: 204,
      statusText: 'No Content',
    });
    await logout;

    expect(store.me()).toBeNull();
    expect(store.status()).toBe('anonymous');
  });

  it('logout_RequestFails_StillClears', async () => {
    await signIn();

    const logout = store.logout();
    (await expectRequest('POST', '/api/v1/auth/logout')).error(new ProgressEvent('error'));
    await logout.catch(() => undefined);

    expect(store.status()).toBe('anonymous');
  });

  it('clear_Authenticated_BecomesAnonymousWithoutRequest', async () => {
    await signIn();

    store.clear();

    expect(store.status()).toBe('anonymous');
    expect(store.me()).toBeNull();
  });
});
