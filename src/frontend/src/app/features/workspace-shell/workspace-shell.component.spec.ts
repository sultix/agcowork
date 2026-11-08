import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { HlmSidebarService } from '@spartan-ng/helm/sidebar';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import {
  API,
  COLLECTION_ID,
  SPACE_ID,
  collection,
  expectRequest,
  pageProviders,
  serve,
  settle,
  space,
  workspace,
} from '../../core/testing/test-utils';
import { FakeRealtime, provideFakeRealtime } from '../../core/testing/fake-hub';
import { WorkspacesStore } from '../workspaces/workspaces.store';
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
    await harness.navigateByUrl('/w/demo/search');
    http.expectOne('/health').flush('Healthy');
    await harness.fixture.whenStable();

    expect(text('workspace-slug')).toBe('demo');
    expect(harness.routeNativeElement?.textContent).toContain('Suche');
    expect(harness.routeNativeElement?.querySelector('router-outlet')).not.toBeNull();
  });

  it('render_NestedRoute_NavLinksResolveAgainstWorkspaceRoot', async () => {
    await harness.navigateByUrl('/w/demo/search');
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
    await harness.navigateByUrl('/w/demo/search');
    http.expectOne('/health').flush('Healthy');
    await harness.fixture.whenStable();

    expect(text('backend-status-live')).toBe('Backend: ok');
    const button = (harness.routeNativeElement as HTMLElement).querySelector(
      '[data-testid="backend-status"]',
    )!;
    expect(button.getAttribute('aria-label')).toBe('Backend: ok');
    // The live region is a sibling, not a child of the button.
    expect(button.querySelector('[role="status"]')).toBeNull();
    expect(
      (harness.routeNativeElement as HTMLElement)
        .querySelector('[data-testid="backend-status-live"]')!
        .getAttribute('role'),
    ).toBe('status');
    expect(
      (harness.routeNativeElement as HTMLElement)
        .querySelector('[data-testid="backend-status"]')!
        .getAttribute('data-status'),
    ).toBe('ok');
  });

  it('render_BackendUnreachable_ShowsNotReachable', async () => {
    await harness.navigateByUrl('/w/demo/search');
    http.expectOne('/health').error(new ProgressEvent('error'));
    await harness.fixture.whenStable();

    expect(text('backend-status-live')).toBe(
      'Backend: nicht erreichbar (Klick zum erneuten Prüfen)',
    );
    expect(
      (harness.routeNativeElement as HTMLElement)
        .querySelector('[data-testid="backend-status"]')!
        .getAttribute('data-status'),
    ).toBe('unreachable');
  });

  it('render_BackendDegraded_ShowsLimitedAndClickReloads', async () => {
    await harness.navigateByUrl('/w/demo/search');
    http.expectOne('/health').flush('Degraded');
    await harness.fixture.whenStable();

    expect(text('backend-status-live')).toBe('Backend: eingeschränkt (Klick zum erneuten Prüfen)');
    (harness.routeNativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('[data-testid="backend-status"]')!
      .click();
    (await expectRequest(http, 'GET', '/health')).flush('Healthy');
    await harness.fixture.whenStable();

    expect(text('backend-status-live')).toBe('Backend: ok');
  });

  it('render_ActiveRoute_MarksOnlyThatNavEntryActive', async () => {
    await harness.navigateByUrl('/w/demo/search');
    http.expectOne('/health').flush('Healthy');
    await harness.fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve));
    await harness.fixture.whenStable();

    const links = Array.from(
      (harness.routeNativeElement as HTMLElement).querySelectorAll<HTMLElement>(
        '[data-testid="nav-link"]',
      ),
    );
    const active = links.filter((a) => a.getAttribute('data-active') === 'true');
    expect(active.map((a) => a.textContent?.trim())).toEqual(['Suche']);
    expect(active[0].getAttribute('aria-current')).toBe('page');
    expect(
      links.find((a) => a.textContent?.trim() === 'Start')!.getAttribute('aria-current'),
    ).toBeNull();
  });

  it('keydown_ModB_DoesNotToggleSidebar', async () => {
    // Helm's sidebar service only registers its keyboard listener when matchMedia exists (jsdom
    // has none), so provide a desktop-sized one; without it this test could not fail.
    const view = document.defaultView as Window;
    Object.defineProperty(view, 'matchMedia', {
      configurable: true,
      value: () => ({
        matches: false,
        addEventListener: () => undefined,
        removeEventListener: () => undefined,
      }),
    });
    onTestFinished(() => {
      delete (view as Partial<Window>).matchMedia;
    });
    await harness.navigateByUrl('/w/demo/search');
    http.expectOne('/health').flush('Healthy');
    await harness.fixture.whenStable();
    onTestFinished(() => {
      // The trigger click below persists the state in a cookie; do not leak it into other tests.
      document.cookie = 'sidebar_state=; path=/; max-age=0';
    });

    const trigger = harness.fixture.debugElement.query(By.css('[data-sidebar="trigger"]'));
    const sidebar = trigger.injector.get(HlmSidebarService);
    expect(sidebar.open()).toBe(true);

    for (const modifier of [{ ctrlKey: true }, { metaKey: true }]) {
      window.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'b', bubbles: true, cancelable: true, ...modifier }),
      );
      await harness.fixture.whenStable();
      expect(sidebar.open()).toBe(true);
    }

    // Same service instance still reacts to the explicit toggle button.
    (trigger.nativeElement as HTMLButtonElement).click();
    await harness.fixture.whenStable();
    expect(sidebar.open()).toBe(false);
  });

  it('render_Header_HasSidebarToggleThemeToggleAndSessionMenu', async () => {
    await harness.navigateByUrl('/w/demo/search');
    http.expectOne('/health').flush('Healthy');
    await harness.fixture.whenStable();

    const root = harness.routeNativeElement as HTMLElement;
    expect(root.querySelector('[data-sidebar="trigger"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="theme-toggle"]')).not.toBeNull();
    expect(text('header-workspace')).toBe('demo');
  });
});

describe('WorkspaceShellComponent navigation by role', () => {
  async function navLinks(role: 'owner' | 'admin' | 'editor' | 'viewer'): Promise<string[]> {
    const list = signal([workspace({ role })]);
    TestBed.configureTestingModule({
      providers: [
        provideRouter(
          [{ path: 'w/:workspaceSlug', children: workspaceShellRoutes }],
          withComponentInputBinding(),
        ),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: WorkspacesStore,
          useValue: { workspaces: list, find: (s: string) => list().find((w) => w.slug === s) },
        },
      ],
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/w/demo/search');
    TestBed.inject(HttpTestingController).expectOne('/health').flush('Healthy');
    await harness.fixture.whenStable();
    return Array.from((harness.routeNativeElement as HTMLElement).querySelectorAll('nav a')).map(
      (a) => a.getAttribute('href') ?? '',
    );
  }

  it.each(['owner', 'admin'] as const)('render_Role_%s_ShowsSettingsLink', async (role) => {
    expect(await navLinks(role)).toContain('/w/demo/settings');
  });

  it.each(['editor', 'viewer'] as const)(
    'render_Role_%s_HidesSettingsLinkKeepsMembers',
    async (role) => {
      const links = await navLinks(role);

      expect(links).not.toContain('/w/demo/settings');
      expect(links).toContain('/w/demo/settings/members');
    },
  );
});

describe('WorkspaceShellComponent sidebar tree', () => {
  it('render_SpacesAndCollections_ShowsTreeWithLinksAndMarksActivePage', async () => {
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({
          routes: [{ path: 'w/:workspaceSlug', children: workspaceShellRoutes }],
        }),
        provideFakeRealtime(),
      ],
    });
    const http = TestBed.inject(HttpTestingController);
    const harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl(`/w/demo/collections/${COLLECTION_ID}`);
    await serve(http, [
      ['/health', 'Healthy'],
      [`${API}/spaces`, [space(), space({ id: 'sp2', name: 'Technik', icon: '🛠' })]],
      [
        `${API}/collections`,
        [collection(), collection({ id: 'c2', spaceId: 'sp2', name: 'Entscheidungen' })],
      ],
      [
        `${API}/collections/${COLLECTION_ID}/describe`,
        {
          collection: collection(),
          space: space(),
          jsonSchema: {},
          example: {},
          templates: [],
          statistics: { recordCount: 0 },
        },
      ],
      [`${API}/collections/${COLLECTION_ID}/views`, []],
    ]);
    await navigation;
    await settle(harness.fixture);

    const root = harness.routeNativeElement as HTMLElement;
    const spaces = Array.from(root.querySelectorAll<HTMLElement>('[data-testid="tree-space"]'));
    const collections = Array.from(
      root.querySelectorAll<HTMLElement>('[data-testid="tree-collection"]'),
    );
    expect(spaces.map((a) => a.getAttribute('href'))).toEqual([
      `/w/demo/spaces/${SPACE_ID}`,
      '/w/demo/spaces/sp2',
    ]);
    expect(collections.map((a) => a.textContent!.trim())).toEqual(['Aufgaben', 'Entscheidungen']);
    expect(collections[0].getAttribute('data-active')).toBe('true');
    expect(collections[0].getAttribute('aria-current')).toBe('page');
    expect(collections[1].getAttribute('data-active')).toBe('false');
    expect(collections[1].getAttribute('aria-current')).toBeNull();
    http.verify();
  });
});

describe('WorkspaceShellComponent realtime status', () => {
  let harness: RouterTestingHarness;
  let realtime: FakeRealtime;

  beforeEach(async () => {
    realtime = new FakeRealtime();
    TestBed.configureTestingModule({
      providers: [
        provideRouter(
          [
            { path: 'w/:workspaceSlug', children: workspaceShellRoutes },
            { path: 'workspaces', children: [] },
          ],
          withComponentInputBinding(),
        ),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideFakeRealtime(realtime),
      ],
    });
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/w/demo/search');
    TestBed.inject(HttpTestingController).expectOne('/health').flush('Healthy');
    await harness.fixture.whenStable();
  });

  const indicator = () =>
    (harness.routeNativeElement as HTMLElement | null)?.querySelector<HTMLButtonElement>(
      '[data-testid="realtime-status"]',
    ) ?? null;
  const live = () =>
    (harness.routeNativeElement as HTMLElement)
      .querySelector('[data-testid="realtime-status-live"]')!
      .textContent!.trim();

  it('create_StartsRealtimeAndDestroy_StopsIt', async () => {
    expect(realtime.starts).toBe(1);

    await harness.navigateByUrl('/workspaces');

    expect(realtime.stops).toBe(1);
  });

  it('render_Live_ShowsNoIndicator', () => {
    expect(indicator()).toBeNull();
    expect(live()).toBe('');
  });

  it('render_Reconnecting_ShowsIndicatorWithText', async () => {
    realtime.status.set('reconnecting');
    await harness.fixture.whenStable();

    expect(indicator()!.getAttribute('data-status')).toBe('reconnecting');
    expect(indicator()!.getAttribute('aria-label')).toBe(
      'Live-Aktualisierung unterbrochen, Verbindung wird wiederhergestellt (Klick verbindet sofort neu)',
    );
    expect(live()).toContain('unterbrochen');
    expect(indicator()!.querySelector('ng-icon')!.getAttribute('class')).toContain(
      'motion-safe:animate-spin',
    );
    indicator()!.click();
    expect(realtime.retries).toBe(1);
  });

  it('render_Offline_ShowsIndicatorAndClickRetries', async () => {
    realtime.status.set('offline');
    await harness.fixture.whenStable();

    expect(indicator()!.getAttribute('data-status')).toBe('offline');
    expect(indicator()!.getAttribute('aria-label')).toBe(
      'Live-Aktualisierung getrennt (Klick zum erneuten Verbinden)',
    );
    indicator()!.click();

    expect(realtime.retries).toBe(1);
  });
});
