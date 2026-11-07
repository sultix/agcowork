import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ApiError } from '../../core/api/api-error';
import { apiErrorInterceptor } from '../../core/api/api-error.interceptor';
import { expectRequest, settle, workspace } from '../../core/testing/test-utils';
import { Workspace, WorkspacesStore } from '../workspaces/workspaces.store';
import { WorkspaceSettingsComponent } from './workspace-settings.component';

describe('WorkspaceSettingsComponent', () => {
  beforeEach(() => {
    loading.set(false);
    loadError.set(null);
  });

  let harness: RouterTestingHarness;
  let http: HttpTestingController;
  const list = signal<Workspace[]>([]);
  const upsert = vi.fn();
  const loading = signal(false);
  const loadError = signal<ApiError | null>(null);

  async function render(ws: Workspace | null): Promise<void> {
    list.set(ws ? [ws] : []);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'w/:workspaceSlug/settings', component: WorkspaceSettingsComponent },
        ]),
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
        {
          provide: WorkspacesStore,
          useValue: {
            workspaces: list,
            find: (s: string) => list().find((w) => w.slug === s),
            upsert,
            isLoading: loading,
            error: loadError,
          },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create('/w/demo/settings');
    await settle(harness.fixture);
  }

  afterEach(() => http.verify());

  const root = () => harness.routeNativeElement as HTMLElement;
  const q = (id: string) => root().querySelector<HTMLElement>(`[data-testid="${id}"]`);

  async function type(id: string, value: string): Promise<void> {
    const input = root().querySelector<HTMLInputElement>(`#${id}`)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await harness.fixture.whenStable();
  }

  it('render_Owner_ShowsRoleScopesAndFormFilledFromWorkspace', async () => {
    await render(workspace({ name: 'Demo', description: 'Beschr.' }));

    expect(root().textContent).toContain('owner');
    expect(q('scopes')!.textContent).toBe('read, write, schema, admin');
    expect(root().querySelector<HTMLInputElement>('#set-name')!.value).toBe('Demo');
    expect(root().querySelector<HTMLTextAreaElement>('#set-description')!.value).toBe('Beschr.');
    expect(q('save')).not.toBeNull();
    expect(q('archive')).not.toBeNull();
  });

  it('render_Viewer_ShowsNoWriteActionsAndDisablesFields', async () => {
    await render(workspace({ role: 'viewer', scopes: ['read'] }));

    expect(q('save')).toBeNull();
    expect(q('archive')).toBeNull();
    expect(q('readonly-notice')).not.toBeNull();
    expect(root().querySelector<HTMLInputElement>('#set-name')!.disabled).toBe(true);
  });

  it('render_Admin_CanEditButNotArchive', async () => {
    await render(workspace({ role: 'admin' }));

    expect(q('save')).not.toBeNull();
    expect(q('archive')).toBeNull();
  });

  it('render_Archived_IsReadOnlyWithNotice', async () => {
    await render(workspace({ archivedAt: '2026-11-01T00:00:00Z' }));

    expect(q('archived-notice')).not.toBeNull();
    expect(q('save')).toBeNull();
    expect(q('archive')).toBeNull();
  });

  it('submit_ChangedName_PatchesAndUpdatesStore', async () => {
    await render(workspace({ name: 'Demo' }));
    await type('set-name', 'Neu');

    root().querySelector('form')!.requestSubmit();
    const request = await expectRequest(http, 'PATCH', '/api/v1/w/demo');
    expect(request.request.body).toEqual({ name: 'Neu', description: '' });
    const updated = workspace({ name: 'Neu' });
    request.flush(updated);
    await settle(harness.fixture);

    expect(upsert).toHaveBeenCalledWith(updated);
    expect(q('saved')).not.toBeNull();
  });

  it('submit_EmptyName_ShowsErrorWithoutRequest', async () => {
    await render(workspace());
    await type('set-name', '');

    root().querySelector('form')!.requestSubmit();
    await settle(harness.fixture);

    expect(q('name-errors')!.textContent).toContain('Namen');
  });

  it('archive_Confirmed_PostsArchiveAndUpdatesStore', async () => {
    await render(workspace());

    q('archive')!.click();
    await harness.fixture.whenStable();
    expect(q('confirm-dialog')).not.toBeNull();
    q('confirm-ok')!.click();
    const request = await expectRequest(http, 'POST', '/api/v1/w/demo/archive');
    const archived = workspace({ archivedAt: '2026-11-07T12:00:00Z' });
    request.flush(archived);
    await settle(harness.fixture);

    expect(upsert).toHaveBeenCalledWith(archived);
    expect(q('confirm-dialog')).toBeNull();
  });

  it('archive_Cancelled_SendsNothing', async () => {
    await render(workspace());

    q('archive')!.click();
    await harness.fixture.whenStable();
    q('confirm-cancel')!.click();
    await harness.fixture.whenStable();

    http.expectNone('/api/v1/w/demo/archive');
    expect(q('confirm-dialog')).toBeNull();
  });

  it('render_UnknownWorkspace_ShowsNotFound', async () => {
    await render(null);

    expect(q('not-found')!.textContent).toContain('nicht gefunden');
  });

  it('render_ListLoadFailed_ShowsError', async () => {
    loadError.set(new ApiError(0, 'network_error'));
    await render(null);

    expect(q('load-error')!.textContent).toContain('nicht erreichbar');
    expect(q('not-found')).toBeNull();
  });

  it('render_StillLoading_ShowsNeitherErrorNorNotFound', async () => {
    loading.set(true);
    await render(null);

    expect(root().textContent).toContain('wird geladen');
    expect(q('not-found')).toBeNull();
  });
});
