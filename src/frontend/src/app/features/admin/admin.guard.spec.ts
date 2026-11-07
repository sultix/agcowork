import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree } from '@angular/router';
import { provideRouter } from '@angular/router';
import { SessionStore } from '../../core/auth/session.store';
import { ADMIN_ME, MEMBER_ME, fakeSession } from '../../core/testing/test-utils';
import { instanceAdminGuard } from './admin.guard';

describe('instanceAdminGuard', () => {
  function run(me: Parameters<typeof fakeSession>[0]): Promise<boolean | UrlTree> {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: SessionStore, useValue: fakeSession(me) }],
    });
    return TestBed.runInInjectionContext(
      () =>
        instanceAdminGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot) as Promise<
          boolean | UrlTree
        >,
    );
  }

  it('canActivate_InstanceAdmin_Allows', async () => {
    expect(await run(ADMIN_ME)).toBe(true);
  });

  it.each([
    ['workspaces:create only', { ...MEMBER_ME, instanceScopes: ['workspaces:create'] }],
    ['no scopes', MEMBER_ME],
    ['anonymous', null],
  ])('canActivate_%s_RedirectsToWorkspacesWithNotice', async (_name, me) => {
    const result = await run(me);

    expect(result).toBeInstanceOf(UrlTree);
    expect(TestBed.inject(Router).serializeUrl(result as UrlTree)).toBe(
      '/workspaces?notice=admin_required',
    );
  });
});
