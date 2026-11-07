import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { SessionStore } from './session.store';

/**
 * Lets authenticated actors through; sends everyone else to `/login?returnUrl=<target>`.
 * Waits while the session is still `unknown` (first `GET /me` at app start).
 */
export const authGuard: CanActivateFn = async (_route, state) => {
  const session = inject(SessionStore);
  const router = inject(Router);

  const status = await session.ready();
  return status === 'authenticated'
    ? true
    : router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
};
