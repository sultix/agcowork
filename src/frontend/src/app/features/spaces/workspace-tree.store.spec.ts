import { TestBed } from '@angular/core/testing';
import { Component } from '@angular/core';
import { HttpTestingController } from '@angular/common/http/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import {
  API,
  COLLECTION_ID,
  SPACE_ID,
  collection,
  expectRequest,
  pageProviders,
  respond,
  space,
} from '../../core/testing/test-utils';
import { WorkspaceTreeStore } from './workspace-tree.store';

@Component({ template: '' })
class BlankComponent {}

const OTHER_SPACE = space({ id: 'sp2', name: 'Technik' });
const OTHER_COLLECTION = collection({ id: 'c2', spaceId: 'sp2', name: 'Entscheidungen' });

describe('WorkspaceTreeStore', () => {
  let http: HttpTestingController;
  let harness: RouterTestingHarness;
  let store: WorkspaceTreeStore;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: pageProviders({
        routes: [
          { path: 'w/:workspaceSlug', component: BlankComponent },
          { path: 'workspaces', component: BlankComponent },
        ],
      }),
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    store = TestBed.inject(WorkspaceTreeStore);
  });

  afterEach(() => http.verify());

  async function answer(slug: string, spaces: unknown[], collections: unknown[]): Promise<void> {
    await respond(http, 'GET', `/api/v1/w/${slug}/spaces`, spaces);
    await respond(http, 'GET', `/api/v1/w/${slug}/collections`, collections);
  }

  it('tree_InWorkspace_GroupsCollectionsBySpace', async () => {
    await harness.navigateByUrl('/w/demo');
    await answer('demo', [space(), OTHER_SPACE], [collection(), OTHER_COLLECTION]);

    await vi.waitFor(() => expect(store.tree()).toHaveLength(2));
    expect(store.tree().map((n) => [n.space.name, n.collections.map((c) => c.name)])).toEqual([
      ['Produkt', ['Aufgaben']],
      ['Technik', ['Entscheidungen']],
    ]);
    expect(store.findSpace(SPACE_ID)?.name).toBe('Produkt');
    expect(store.findCollection(COLLECTION_ID)?.name).toBe('Aufgaben');
    expect(store.findSpace('nope')).toBeUndefined();
  });

  it('tree_OutsideWorkspace_DoesNotRequest', async () => {
    await harness.navigateByUrl('/workspaces');
    TestBed.tick();

    http.expectNone(`${API}/spaces`);
    expect(store.tree()).toEqual([]);
  });

  it('tree_SlugChange_ReloadsForTheNewWorkspace', async () => {
    await harness.navigateByUrl('/w/demo');
    await answer('demo', [space()], [collection()]);
    await vi.waitFor(() => expect(store.spaces()).toHaveLength(1));

    await harness.navigateByUrl('/w/other');
    await answer('other', [OTHER_SPACE], []);

    await vi.waitFor(() => expect(store.spaces().map((s) => s.name)).toEqual(['Technik']));
    expect(store.collections()).toEqual([]);
  });

  it('reload_AfterChange_FetchesBothListsAgain', async () => {
    await harness.navigateByUrl('/w/demo');
    await answer('demo', [space()], [collection()]);
    await vi.waitFor(() => expect(store.spaces()).toHaveLength(1));

    store.reload();
    await answer('demo', [space(), OTHER_SPACE], [collection()]);

    await vi.waitFor(() => expect(store.spaces()).toHaveLength(2));
  });

  it('error_ListFails_ExposesApiError', async () => {
    await harness.navigateByUrl('/w/demo');
    (await expectRequest(http, 'GET', `${API}/spaces`)).flush(
      { code: 'forbidden', status: 403 },
      { status: 403, statusText: 'Forbidden' },
    );
    await respond(http, 'GET', `${API}/collections`, []);

    await vi.waitFor(() => expect(store.error()?.code).toBe('forbidden'));
  });
});
