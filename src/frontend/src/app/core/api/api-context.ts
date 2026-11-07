import { HttpContext, HttpContextToken } from '@angular/common/http';

/**
 * Marks a request whose 401 is an expected answer, not an expired session: the error
 * interceptor then neither clears the session nor redirects to the login page.
 * Used for the session probe (`GET /me` at start-up).
 */
export const SKIP_AUTH_REDIRECT = new HttpContextToken<boolean>(() => false);

/** `HttpContext` with {@link SKIP_AUTH_REDIRECT} set. */
export function withoutAuthRedirect(context = new HttpContext()): HttpContext {
  return context.set(SKIP_AUTH_REDIRECT, true);
}
