import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { Route, Router, Routes, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from './app.routes';
import { authGuard } from './core/auth/auth.guard';
import { SessionStatus, SessionStore } from './core/auth/session.store';
import { FakeCodeEditor, provideFakeCodeEditor } from './shared/raw-view/fake-code-editor';

/** Expands lazy `loadChildren` so the full path tree can be checked. */
async function collectPaths(list: Routes, prefix = ''): Promise<string[]> {
  const paths: string[] = [];
  for (const route of list) {
    const path = [prefix, route.path ?? ''].filter((p) => p !== '').join('/');
    if (route.component || route.loadComponent || route.redirectTo !== undefined) {
      paths.push(path);
    }
    const children = route.children ?? (await loadChildren(route));
    if (children) {
      paths.push(...(await collectPaths(children, path)));
    }
  }
  return paths;
}

async function loadChildren(route: Route): Promise<Routes | undefined> {
  if (!route.loadChildren) {
    return undefined;
  }
  return (await route.loadChildren()) as Routes;
}

/** Session stub for routing tests: the guard only asks `ready()`. */
function sessionStub(status: SessionStatus) {
  return {
    provide: SessionStore,
    useValue: { ready: () => Promise.resolve(status), me: () => null },
  };
}

describe('app routes', () => {
  it('routes_Configuration_ContainsAllPhase0PathsFromArchitecture', async () => {
    const paths = await collectPaths(routes);

    expect(paths).toEqual(
      expect.arrayContaining([
        'login',
        'workspaces',
        'w/:workspaceSlug',
        'w/:workspaceSlug/spaces/:spaceId',
        'w/:workspaceSlug/search',
        'w/:workspaceSlug/activity',
        'w/:workspaceSlug/settings/members',
        'dev/playground',
        '**',
      ]),
    );
  });

  it('routes_Wildcard_RedirectsToWorkspaces', () => {
    const wildcard = routes.find((r) => r.path === '**');
    expect(wildcard?.redirectTo).toBe('workspaces');
  });

  it('routes_TopLevelFeatures_AreLazy', () => {
    for (const route of routes.filter((r) => r.redirectTo === undefined)) {
      expect(route.component, `route ${route.path} must be lazy`).toBeUndefined();
      expect(route.loadComponent ?? route.loadChildren).toBeDefined();
    }
  });

  it.each(['/w/demo/xyz', '/foo/bar', '/'])(
    'navigate_UnknownOrRootUrl_%s_EndsOnWorkspaces',
    async (url) => {
      TestBed.configureTestingModule({
        providers: [
          provideRouter(routes),
          provideHttpClient(),
          provideHttpClientTesting(),
          sessionStub('authenticated'),
        ],
      });
      const harness = await RouterTestingHarness.create();

      await harness.navigateByUrl(url);

      expect(TestBed.inject(Router).url).toBe('/workspaces');
    },
  );

  it.each(['/workspaces', '/w/demo', '/w/demo/activity'])(
    'navigate_AnonymousToProtected_%s_EndsOnLoginWithReturnUrl',
    async (url) => {
      TestBed.configureTestingModule({
        providers: [
          provideRouter(routes),
          provideHttpClient(),
          provideHttpClientTesting(),
          sessionStub('anonymous'),
        ],
      });
      const harness = await RouterTestingHarness.create();

      await harness.navigateByUrl(url);

      const router = TestBed.inject(Router);
      expect(router.url.startsWith('/login?')).toBe(true);
      expect(router.parseUrl(router.url).queryParams['returnUrl']).toBe(url);
    },
  );

  it('routes_ProtectedAreas_HaveAuthGuard', () => {
    for (const path of ['workspaces', 'w/:workspaceSlug']) {
      expect(routes.find((r) => r.path === path)?.canActivate, path).toEqual([authGuard]);
    }
    expect(routes.find((r) => r.path === 'login')?.canActivate).toBeUndefined();
  });

  it('navigate_DevPlayground_RendersEditorAndBothRawViews', async () => {
    const editors: FakeCodeEditor[] = [];
    TestBed.configureTestingModule({
      providers: [provideRouter(routes), provideFakeCodeEditor(editors)],
    });
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl('/dev/playground');
    await harness.fixture.whenStable();

    const root = harness.routeNativeElement as HTMLElement;
    expect(TestBed.inject(Router).url).toBe('/dev/playground');
    expect(root.querySelector('ag-markdown-editor .ProseMirror')).not.toBeNull();
    expect(editors.map((e) => e.language)).toEqual(['markdown', 'json']);
  });
});
