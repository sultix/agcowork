import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { SessionStore } from '../../core/auth/session.store';

/** Scope that grants access to the administration area. */
export const INSTANCE_ADMIN_SCOPE = 'instance:admin';

/**
 * Lets instance admins (`instance:admin`) through; everyone else goes to `/workspaces` with a
 * notice. Runs after the auth guard, but waits for the session itself so it is safe alone.
 */
export const instanceAdminGuard: CanActivateFn = async () => {
  const session = inject(SessionStore);
  const router = inject(Router);

  await session.ready();
  return session.me()?.instanceScopes.includes(INSTANCE_ADMIN_SCOPE)
    ? true
    : router.createUrlTree(['/workspaces'], { queryParams: { notice: 'admin_required' } });
};
