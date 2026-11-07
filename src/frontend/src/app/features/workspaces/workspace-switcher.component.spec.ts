import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { workspace } from '../../core/testing/test-utils';
import { WorkspaceSwitcherComponent } from './workspace-switcher.component';
import { Workspace, WorkspacesStore } from './workspaces.store';

@Component({
  imports: [WorkspaceSwitcherComponent],
  template: '<ag-workspace-switcher slug="demo" />',
})
class HostComponent {}

@Component({ template: 'target' })
class TargetComponent {}

describe('WorkspaceSwitcherComponent', () => {
  const list = signal<Workspace[]>([]);
  let harness: RouterTestingHarness;

  async function render(): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'home', component: HostComponent },
          { path: 'w/:slug', component: TargetComponent },
          { path: 'workspaces', component: TargetComponent },
        ]),
        {
          provide: WorkspacesStore,
          useValue: { workspaces: list, find: (s: string) => list().find((w) => w.slug === s) },
        },
      ],
    });
    harness = await RouterTestingHarness.create('/home');
    return harness.routeNativeElement as HTMLElement;
  }

  const q = (root: HTMLElement, id: string) =>
    root.querySelector<HTMLElement>(`[data-testid="${id}"]`);
  // The menu renders into the CDK overlay container in <body>.
  const inBody = (id: string) => document.body.querySelector<HTMLElement>(`[data-testid="${id}"]`);

  async function openMenu(root: HTMLElement): Promise<void> {
    q(root, 'switcher-toggle')!.click();
    await harness.fixture.whenStable();
  }

  beforeEach(() => {
    list.set([
      workspace({ id: '1', slug: 'demo', name: 'Demo' }),
      workspace({ id: '2', slug: 'work', name: 'Arbeit' }),
    ]);
  });

  it('render_KnownWorkspace_ShowsNameAndOthersInMenu', async () => {
    const root = await render();

    expect(q(root, 'workspace-slug')!.textContent!.trim()).toBe('Demo');
    expect(q(root, 'switcher-toggle')!.getAttribute('aria-expanded')).toBe('false');
    expect(inBody('switcher-menu')).toBeNull();

    await openMenu(root);

    expect(q(root, 'switcher-toggle')!.getAttribute('aria-expanded')).toBe('true');
    const items = Array.from(document.body.querySelectorAll('[data-testid="switcher-item"]'));
    expect(items.map((a) => a.getAttribute('href'))).toEqual(['/w/work']);
    expect(items.map((a) => a.getAttribute('role'))).toEqual(['menuitem']);
    expect(inBody('switcher-all')!.getAttribute('href')).toBe('/workspaces');
  });

  it('select_OtherWorkspace_NavigatesAndClosesMenu', async () => {
    const root = await render();
    await openMenu(root);

    inBody('switcher-item')!.click();
    await harness.fixture.whenStable();

    await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/w/work'));
    await vi.waitFor(() => expect(inBody('switcher-menu')).toBeNull());
  });

  it('select_AllWorkspaces_NavigatesToList', async () => {
    const root = await render();
    await openMenu(root);

    inBody('switcher-all')!.click();

    await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/workspaces'));
  });

  it('render_ListNotLoaded_FallsBackToSlug', async () => {
    list.set([]);
    const root = await render();

    expect(q(root, 'workspace-slug')!.textContent!.trim()).toBe('demo');
  });
});
