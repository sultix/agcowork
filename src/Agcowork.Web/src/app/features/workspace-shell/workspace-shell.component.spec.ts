import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { workspaceShellRoutes } from './workspace-shell.routes';

describe('WorkspaceShellComponent', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(
          [{ path: 'w/:workspaceSlug', children: workspaceShellRoutes }],
          withComponentInputBinding(),
        ),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => http.verify());

  function text(testId: string): string {
    const el = (harness.routeNativeElement as HTMLElement | null)?.querySelector(
      `[data-testid="${testId}"]`,
    );
    return el?.textContent?.trim() ?? '';
  }

  it('render_WithSlug_ShowsSlugAndChildPage', async () => {
    await harness.navigateByUrl('/w/demo/spaces/1');
    http.expectOne('/health').flush('Healthy');
    await harness.fixture.whenStable();

    expect(text('workspace-slug')).toBe('demo');
    expect(harness.routeNativeElement?.textContent).toContain('Space');
    expect(harness.routeNativeElement?.querySelector('router-outlet')).not.toBeNull();
  });

  it('render_NestedRoute_NavLinksResolveAgainstWorkspaceRoot', async () => {
    await harness.navigateByUrl('/w/demo/spaces/1');
    http.expectOne('/health').flush('Healthy');
    await harness.fixture.whenStable();

    const links = Array.from(
      (harness.routeNativeElement as HTMLElement).querySelectorAll('nav a'),
    ).map((a) => a.getAttribute('href'));
    expect(links).toEqual([
      '/w/demo',
      '/w/demo/search',
      '/w/demo/activity',
      '/w/demo/settings/members',
    ]);
  });

  it('render_BackendHealthy_ShowsOk', async () => {
    await harness.navigateByUrl('/w/demo');
    http.expectOne('/health').flush('Healthy');
    await harness.fixture.whenStable();

    expect(text('backend-status')).toBe('Backend: ok');
  });

  it('render_BackendUnreachable_ShowsNotReachable', async () => {
    await harness.navigateByUrl('/w/demo');
    http.expectOne('/health').error(new ProgressEvent('error'));
    await harness.fixture.whenStable();

    expect(text('backend-status')).toBe('Backend: nicht erreichbar');
  });
});
