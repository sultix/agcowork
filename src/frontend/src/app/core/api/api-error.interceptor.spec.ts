import { ApplicationRef, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  HttpClient,
  HttpErrorResponse,
  provideHttpClient,
  withInterceptors,
} from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { SessionStore } from '../auth/session.store';
import { ApiClient } from './api-client';
import { withoutAuthRedirect } from './api-context';
import { ApiError } from './api-error';
import { apiErrorInterceptor } from './api-error.interceptor';

@Component({ template: '' })
class BlankComponent {}

const UNAUTHORIZED = { status: 401, statusText: 'Unauthorized' } as const;
const PROBLEM_401 = { status: 401, code: 'unauthorized', title: 'Unauthorized' };

describe('apiErrorInterceptor', () => {
  let http: HttpTestingController;
  let router: Router;
  let session: { clear: ReturnType<typeof vi.fn> };

  beforeEach(async () => {
    session = { clear: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
        provideRouter([
          { path: 'login', component: BlankComponent },
          { path: 'w/:slug/activity', component: BlankComponent },
        ]),
        { provide: SessionStore, useValue: session },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    await router.navigateByUrl('/w/demo/activity');
  });

  afterEach(() => http.verify());

  async function failWith401(request: Promise<unknown>, url: string): Promise<unknown> {
    http.expectOne(url).flush(PROBLEM_401, UNAUTHORIZED);
    const error = await request.catch((e: unknown) => e);
    await TestBed.inject(ApplicationRef).whenStable();
    return error;
  }

  it('unauthorized_ApiRequest_ClearsSessionAndRedirectsWithReturnUrl', async () => {
    const api = TestBed.inject(ApiClient);
    const navigation = vi.spyOn(router, 'navigate');

    const error = await failWith401(firstValueFrom(api.get('/actors')), '/api/v1/actors');
    await vi.waitFor(() => expect(router.url).toBe('/login?returnUrl=%2Fw%2Fdemo%2Factivity'));

    expect(error).toBeInstanceOf(ApiError);
    expect(session.clear).toHaveBeenCalledOnce();
    expect(navigation).toHaveBeenCalledWith(['/login'], {
      queryParams: { returnUrl: '/w/demo/activity' },
    });
  });

  it('unauthorized_Login_NeitherClearsNorRedirects', async () => {
    const api = TestBed.inject(ApiClient);

    const error = await failWith401(
      firstValueFrom(api.post('/auth/login', { body: { email: 'a@b.c', password: 'x' } })),
      '/api/v1/auth/login',
    );

    expect((error as ApiError).status).toBe(401);
    expect(session.clear).not.toHaveBeenCalled();
    expect(router.url).toBe('/w/demo/activity');
  });

  it('unauthorized_MarkedWithSkipAuthRedirect_NeitherClearsNorRedirects', async () => {
    const api = TestBed.inject(ApiClient);

    await failWith401(
      firstValueFrom(api.get('/me', { context: withoutAuthRedirect() })),
      '/api/v1/me',
    );

    expect(session.clear).not.toHaveBeenCalled();
    expect(router.url).toBe('/w/demo/activity');
  });

  it('unauthorized_AlreadyOnLogin_DoesNotNavigateAgain', async () => {
    await router.navigateByUrl('/login');
    const navigation = vi.spyOn(router, 'navigate');

    await failWith401(firstValueFrom(TestBed.inject(ApiClient).get('/actors')), '/api/v1/actors');

    expect(session.clear).toHaveBeenCalledOnce();
    expect(navigation).not.toHaveBeenCalled();
  });

  it('problem_PlainHttpClientToApi_BecomesApiErrorWithErrors', async () => {
    const result = firstValueFrom(TestBed.inject(HttpClient).post('/api/v1/actors', {}));

    http.expectOne('/api/v1/actors').flush(
      {
        status: 400,
        code: 'validation_failed',
        errors: [{ field: 'email', code: 'required', message: "'email' is required." }],
      },
      { status: 400, statusText: 'Bad Request' },
    );

    const error = (await result.catch((e: unknown) => e)) as ApiError;
    expect(error).toBeInstanceOf(ApiError);
    expect(error.code).toBe('validation_failed');
    expect(error.errorsFor('email').map((e) => e.code)).toEqual(['required']);
  });

  it('error_NonApiUrl_IsPassedThroughUnchanged', async () => {
    const result = firstValueFrom(TestBed.inject(HttpClient).get('/health'));

    http.expectOne('/health').flush('down', UNAUTHORIZED);

    const error = await result.catch((e: unknown) => e);
    expect(error).toBeInstanceOf(HttpErrorResponse);
    expect(session.clear).not.toHaveBeenCalled();
  });
});
