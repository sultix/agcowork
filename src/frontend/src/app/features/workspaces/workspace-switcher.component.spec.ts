import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { workspace } from '../../core/testing/test-utils';
import { WorkspaceSwitcherComponent } from './workspace-switcher.component';
import { Workspace, WorkspacesStore } from './workspaces.store';

@Component({
  imports: [WorkspaceSwitcherComponent],
  template: '<ag-workspace-switcher slug="demo" />',
})
class HostComponent {}

describe('WorkspaceSwitcherComponent', () => {
  const list = signal<Workspace[]>([]);

  async function render(): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'home', component: HostComponent }]),
        {
          provide: WorkspacesStore,
          useValue: { workspaces: list, find: (s: string) => list().find((w) => w.slug === s) },
        },
      ],
    });
    return (await RouterTestingHarness.create('/home')).routeNativeElement as HTMLElement;
  }

  const q = (root: HTMLElement, id: string) =>
    root.querySelector<HTMLElement>(`[data-testid="${id}"]`);

  it('render_KnownWorkspace_ShowsNameAndOthersInMenu', async () => {
    list.set([
      workspace({ id: '1', slug: 'demo', name: 'Demo' }),
      workspace({ id: '2', slug: 'work', name: 'Arbeit' }),
    ]);
    const root = await render();

    expect(q(root, 'workspace-slug')!.textContent!.trim()).toBe('Demo');
    expect(q(root, 'switcher-menu')).toBeNull();

    q(root, 'switcher-toggle')!.click();
    TestBed.tick();

    const items = Array.from(root.querySelectorAll('[data-testid="switcher-item"]'));
    expect(items.map((a) => a.getAttribute('href'))).toEqual(['/w/work']);
    expect(q(root, 'switcher-all')!.getAttribute('href')).toBe('/workspaces');
  });

  it('render_ListNotLoaded_FallsBackToSlug', async () => {
    list.set([]);
    const root = await render();

    expect(q(root, 'workspace-slug')!.textContent!.trim()).toBe('demo');
  });
});
