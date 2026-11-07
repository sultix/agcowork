import { HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { Injector, inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { SessionStore } from '../auth/session.store';
import { API_BASE_URL } from './api-tokens';
import { SKIP_AUTH_REDIRECT } from './api-context';
import { ApiError } from './api-error';

/** Path of the login page; also used to avoid redirect loops. */
export const LOGIN_PATH = '/login';

/**
 * For requests to the API: turns every error into an {@link ApiError} (so `httpResource` and
 * plain `HttpClient` calls see the same shape as the API client) and treats a 401 as an
 * expired session: clear the session, go to `/login?returnUrl=<current url>`.
 * A 401 of `POST /auth/login` (wrong credentials) and of requests marked with
 * {@link SKIP_AUTH_REDIRECT} is passed on unchanged.
 */
export const apiErrorInterceptor: HttpInterceptorFn = (request, next) => {
  const baseUrl = inject(API_BASE_URL);
  if (!request.url.startsWith(baseUrl)) {
    return next(request);
  }
  // Resolved lazily: SessionStore depends on HttpClient, which depends on this interceptor.
  const injector = inject(Injector);

  return next(request).pipe(
    catchError((error: unknown) => {
      const apiError = ApiError.from(error);
      if (apiError.status === 401 && redirectsOnUnauthorized(request, baseUrl)) {
        injector.get(SessionStore).clear();
        redirectToLogin(injector.get(Router));
      }
      return throwError(() => apiError);
    }),
  );
};

function redirectsOnUnauthorized(request: HttpRequest<unknown>, baseUrl: string): boolean {
  if (request.context.get(SKIP_AUTH_REDIRECT)) {
    return false;
  }
  const path = request.url.slice(baseUrl.length).split('?')[0];
  return path !== '/auth/login';
}

function redirectToLogin(router: Router): void {
  const current = router.url;
  if (current === LOGIN_PATH || current.startsWith(`${LOGIN_PATH}?`)) {
    return;
  }
  void router.navigate([LOGIN_PATH], { queryParams: { returnUrl: current } });
}
