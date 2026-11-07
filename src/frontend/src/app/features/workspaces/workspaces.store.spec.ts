import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApiError } from '../../core/api/api-error';
import { apiErrorInterceptor } from '../../core/api/api-error.interceptor';
import { SessionStore } from '../../core/auth/session.store';
import {
  ADMIN_ME,
  MEMBER_ME,
  FakeSession,
  expectRequest,
  fakeSession,
  workspace,
} from '../../core/testing/test-utils';
import { WorkspacesStore } from './workspaces.store';

describe('WorkspacesStore', () => {
  let http: HttpTestingController;
  let session: FakeSession;
  let store: WorkspacesStore;

  function setup(me = ADMIN_ME): void {
    session = fakeSession(me);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
        { provide: SessionStore, useValue: session },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(WorkspacesStore);
  }

  afterEach(() => http.verify());

  async function loaded(...list: ReturnType<typeof workspace>[]): Promise<void> {
    (await expectRequest(http, 'GET', '/api/v1/workspaces')).flush(list);
    await vi.waitFor(() => expect(store.workspaces()).toEqual(list));
  }

  it('workspaces_Authenticated_LoadsList', async () => {
    setup();
    const demo = workspace();

    await loaded(demo);

    expect(store.find('demo')).toEqual(demo);
    expect(store.find('other')).toBeUndefined();
  });

  it('workspaces_Anonymous_DoesNotRequest', async () => {
    setup(undefined);
    session.me.set(null);
    session.status.set('anonymous');
    TestBed.tick();

    http.expectNone('/api/v1/workspaces');
    expect(store.workspaces()).toEqual([]);
  });

  it('create_Success_AddsWorkspaceSortedByName', async () => {
    setup();
    await loaded(workspace({ id: 'a', slug: 'zeta', name: 'Zeta' }));

    const created = store.create({ slug: 'alpha', name: 'Alpha', templateId: 'empty' });
    const request = await expectRequest(http, 'POST', '/api/v1/workspaces');
    expect(request.request.body).toEqual({ slug: 'alpha', name: 'Alpha', templateId: 'empty' });
    request.flush(workspace({ id: 'b', slug: 'alpha', name: 'Alpha' }), {
      status: 201,
      statusText: 'Created',
    });

    expect((await created).slug).toBe('alpha');
    expect(store.workspaces().map((w) => w.slug)).toEqual(['alpha', 'zeta']);
  });

  it('create_SlugTaken_RejectsWithApiErrorOnSlugField', async () => {
    setup();
    await loaded();

    const created = store.create({ slug: 'demo', name: 'Demo' });
    (await expectRequest(http, 'POST', '/api/v1/workspaces')).flush(
      {
        status: 409,
        code: 'slug_taken',
        errors: [{ field: 'slug', code: 'conflict', message: 'taken' }],
      },
      { status: 409, statusText: 'Conflict' },
    );

    const error = await created.catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).code).toBe('slug_taken');
    expect((error as ApiError).errorsFor('slug')).toHaveLength(1);
    expect(store.workspaces()).toEqual([]);
  });

  it.each([
    [['workspaces:create'], true],
    [['instance:admin'], true],
    [[], false],
  ])('canCreate_InstanceScopes_%j_Is_%s', (scopes, expected) => {
    setup({ ...MEMBER_ME, instanceScopes: scopes });

    expect(store.canCreate()).toBe(expected);
  });
});
