import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { SessionStore } from './session.store';

/** Page where humans change their own password; the target of the forced redirect. */
export const PASSWORD_PATH = '/account/password';

/**
 * Lets authenticated actors through; sends everyone else to `/login?returnUrl=<target>`.
 * Humans who must change their password (bootstrap or admin-set) are sent to
 * `/account/password?returnUrl=<target>` first. Waits while the session is still `unknown`
 * (first `GET /me` at app start).
 */
export const authGuard: CanActivateFn = async (_route, state) => {
  const session = inject(SessionStore);
  const router = inject(Router);

  const status = await session.ready();
  if (status !== 'authenticated') {
    return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
  }
  return session.me()?.mustChangePassword
    ? router.createUrlTree([PASSWORD_PATH], { queryParams: { returnUrl: state.url } })
    : true;
};

/** Like {@link authGuard} but without the password redirect: for the password page itself. */
export const signedInGuard: CanActivateFn = async (_route, state) => {
  const session = inject(SessionStore);
  const router = inject(Router);

  const status = await session.ready();
  return status === 'authenticated'
    ? true
    : router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
};
