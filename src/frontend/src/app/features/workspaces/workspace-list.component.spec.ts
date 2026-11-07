import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ApiError } from '../../core/api/api-error';
import { SessionStore } from '../../core/auth/session.store';
import { ADMIN_ME, fakeSession, workspace } from '../../core/testing/test-utils';
import { WorkspaceListComponent } from './workspace-list.component';
import { Workspace, WorkspacesStore } from './workspaces.store';

describe('WorkspaceListComponent', () => {
  const workspaces = signal<Workspace[]>([]);
  const canCreate = signal(false);
  const error = signal<ApiError | null>(null);

  async function render(url = '/workspaces'): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(
          [{ path: 'workspaces', component: WorkspaceListComponent }],
          withComponentInputBinding(),
        ),
        { provide: SessionStore, useValue: fakeSession(ADMIN_ME) },
        {
          provide: WorkspacesStore,
          useValue: { workspaces, canCreate, error, isLoading: signal(false), reload: vi.fn() },
        },
      ],
    });
    const harness = await RouterTestingHarness.create(url);
    return harness.routeNativeElement as HTMLElement;
  }

  const q = (root: HTMLElement, id: string) => root.querySelector(`[data-testid="${id}"]`);

  beforeEach(() => {
    workspaces.set([]);
    canCreate.set(false);
    error.set(null);
  });

  it('render_Workspaces_ShowsNameSlugRoleAndArchivedMarker', async () => {
    workspaces.set([
      workspace({ name: 'Demo', slug: 'demo', role: 'owner' }),
      workspace({
        id: 'x',
        name: 'Alt',
        slug: 'alt',
        role: 'viewer',
        archivedAt: '2026-01-01T00:00:00Z',
      }),
    ]);

    const root = await render();

    const rows = Array.from(root.querySelectorAll('[data-testid="workspace-row"]'));
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('Demo');
    expect(rows[0].textContent).toContain('owner');
    expect(rows[0].querySelector('a')!.getAttribute('href')).toBe('/w/demo');
    expect(rows[1].textContent).toContain('archiviert');
  });

  it('render_NoWorkspaces_ShowsEmptyHint', async () => {
    const root = await render();

    expect(q(root, 'empty')?.textContent).toContain('keinem Workspace');
  });

  it('render_CanCreate_ShowsNewWorkspaceButton', async () => {
    canCreate.set(true);

    const root = await render();

    expect(q(root, 'create-workspace')?.getAttribute('href')).toBe('/workspaces/new');
  });

  it('render_CannotCreate_HidesNewWorkspaceButton', async () => {
    const root = await render();

    expect(q(root, 'create-workspace')).toBeNull();
  });

  it('render_LoadError_ShowsMessage', async () => {
    error.set(new ApiError(0, 'network_error'));

    const root = await render();

    expect(q(root, 'load-error')?.textContent).toContain('nicht erreichbar');
  });

  it('render_NoticeAdminRequired_ShowsHint', async () => {
    const root = await render('/workspaces?notice=admin_required');

    expect(q(root, 'notice')?.textContent).toContain('Instanz-Admins');
  });
});
