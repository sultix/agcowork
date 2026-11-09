import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { HttpTestingController } from '@angular/common/http/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import {
  API,
  COLLECTION_ID,
  RECORD_ID,
  VIEW_ID,
  View,
  collection,
  expectRequest,
  member,
  pageProviders,
  propertyDefinition,
  queryPage,
  recordDto,
  respond,
  respondIfRequested,
  serve,
  settle,
  space,
  typeInto,
  viewDto,
  workspace,
} from '../../core/testing/test-utils';
import { CollectionPageComponent } from '../collections/collection-page.component';
import { DefaultViewRedirectComponent } from './default-view-redirect.component';
import { ViewPageComponent } from './view-page.component';

@Component({ template: 'stub' })
class StubComponent {}

const CUSTOM_ID = '0199a000-0000-7000-8000-0000000000e9';
const SYSTEM = viewDto({ isDefault: false });
const CUSTOM = viewDto({
  id: CUSTOM_ID,
  name: 'Offene',
  isSystem: false,
  isDefault: true,
  sortOrder: 2,
  filter: { and: [{ prop: 'status', op: 'eq', value: 'todo' }] } as never,
});
const BASE = `/w/demo/collections/${COLLECTION_ID}/views`;

describe('ViewSwitcherComponent and view page', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;

  async function render(
    options: {
      scopes?: string[];
      views?: View[];
      url?: string;
      firstQuery?: string;
      records?: ReturnType<typeof recordDto>[];
      schemaExtra?: ReturnType<typeof propertyDefinition>[];
    } = {},
  ): Promise<void> {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: pageProviders({
        ws: workspace({ scopes: options.scopes ?? ['read', 'write'] }),
        routes: [
          {
            path: 'w/:workspaceSlug',
            children: [
              {
                path: 'collections/:collectionId',
                component: CollectionPageComponent,
                children: [
                  { path: '', component: DefaultViewRedirectComponent },
                  { path: 'views/:viewId', component: ViewPageComponent },
                ],
              },
              { path: 'records/:recordId', component: StubComponent },
            ],
          },
        ],
      }),
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl(options.url ?? `/w/demo/collections/${COLLECTION_ID}`);
    await serve(http, [
      [
        `${API}/collections/${COLLECTION_ID}/describe`,
        {
          collection: collection({
            schema: [...collection().schema, ...(options.schemaExtra ?? [])],
          }),
          space: space(),
          jsonSchema: {},
          example: {},
          templates: [],
          statistics: { recordCount: 1 },
        },
      ],
      [`${API}/collections/${COLLECTION_ID}/views`, options.views ?? [SYSTEM, CUSTOM]],
    ]);
    await respond(
      http,
      'POST',
      options.firstQuery ?? `${API}/views/${CUSTOM_ID}/query`,
      queryPage(options.records ?? [recordDto()]),
    );
    await serve(http, [
      [`${API}/spaces`, [space()]],
      [`${API}/collections`, [collection()]],
    ]);
    await navigation;
    await respondIfRequested(http, `${API}/members`, [member()]);
    await settle(harness.fixture);
  }

  afterEach(() => {
    http.match(`${API}/members`).forEach((r) => r.flush([]));
    http.verify();
    sessionStorage.clear();
  });

  const router = () => TestBed.inject(Router);
  const root = () => harness.routeNativeElement as HTMLElement;
  // Overlays (e.g. the confirm dialog) render into the CDK overlay container in <body>.
  const q = (id: string) =>
    root().querySelector<HTMLElement>(`[data-testid="${id}"]`) ??
    document.body.querySelector<HTMLElement>(`[data-testid="${id}"]`);
  const all = (id: string) =>
    Array.from(root().querySelectorAll<HTMLElement>(`[data-testid="${id}"]`));
  /** Whether each view tab carries the default-view mark (the star icon). */
  const defaultMarks = () =>
    all('view-tab').map((tab) => tab.querySelector('[data-testid="view-default-mark"]') !== null);
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  /** Clicks and lets the click take effect; does not wait for stability (requests may follow). */
  async function click(element: HTMLElement | null): Promise<void> {
    element!.click();
    TestBed.tick();
    await new Promise((resolve) => setTimeout(resolve, 10));
    TestBed.tick();
  }

  it('open_Collection_RedirectsToDefaultViewAndRunsItsQuery', async () => {
    await render();

    expect(router().url).toBe(`${BASE}/${CUSTOM_ID}`);
    expect(all('view-tab').map(text)).toEqual(['Tabelle', 'Offene (Standard)']);
    expect(defaultMarks()).toEqual([false, true]);
    expect(
      all('view-tab').map((tab) => tab.querySelector('ng-icon')!.getAttribute('data-icon')),
    ).toEqual(['lucideTable', 'lucideTable']);
    expect(q('view-tab')!.getAttribute('aria-current')).toBeNull();
    expect(all('view-tab')[1].getAttribute('aria-current')).toBe('page');
    expect(all('table-row')).toHaveLength(1);
  });

  it('open_ViewUrlDirectly_RunsThatView', async () => {
    await render({ url: `${BASE}/${VIEW_ID}`, firstQuery: `${API}/views/${VIEW_ID}/query` });

    expect(router().url).toBe(`${BASE}/${VIEW_ID}`);
  });

  it('switch_ClickTab_NavigatesAndQueriesOtherView', async () => {
    await render();

    await click(all('view-tab')[0]);
    const query = await expectRequest(http, 'POST', `${API}/views/${VIEW_ID}/query`);
    expect(query.request.body).toEqual({ limit: 200, includeCount: true });
    query.flush(queryPage([]));
    await settle(harness.fixture);

    expect(router().url).toBe(`${BASE}/${VIEW_ID}`);
    expect(q('table-empty')).not.toBeNull();
  });

  it('sort_LocalChange_RunsAdHocQueryAndSaveStoresIt', async () => {
    await render();

    await click(root().querySelector('[data-key="estimate"] [data-testid="sort-button"]'));
    const query = await expectRequest(http, 'POST', `${API}/collections/${COLLECTION_ID}/query`);
    expect(query.request.body).toEqual({
      filter: { and: [{ prop: 'status', op: 'eq', value: 'todo' }] },
      sorts: [{ prop: 'estimate', dir: 'asc' }],
      limit: 200,
      includeArchived: false,
      includeCount: true,
    });
    query.flush(queryPage([recordDto()]));
    await settle(harness.fixture);
    expect(all('sort-chip').map((chip) => text(chip).replace('Schätzung', 'Schätzung '))).toEqual([
      'Schätzung aufsteigend',
    ]);
    expect(all('sort-chip-dir').map((d) => d.dataset['dir'])).toEqual(['asc']);
    expect(q('view-modified')).not.toBeNull();

    await click(q('view-save'));
    const patch = await expectRequest(http, 'PATCH', `${API}/views/${CUSTOM_ID}`);
    expect(patch.request.body).toEqual({
      filter: { and: [{ prop: 'status', op: 'eq', value: 'todo' }] },
      sorts: [{ prop: 'estimate', dir: 'asc' }],
      visibleProperties: [],
    });
    patch.flush({ ...CUSTOM, sorts: [{ prop: 'estimate', dir: 'asc' }] });
    // stored again: the view endpoint answers once more
    await respond(http, 'POST', `${API}/views/${CUSTOM_ID}/query`, queryPage([recordDto()]));
    await settle(harness.fixture);

    expect(q('view-modified')).toBeNull();
  });

  it('sort_Viewer_KeepsChangeLocalWithoutSaveButton', async () => {
    await render({ scopes: ['read'] });

    await click(root().querySelector('[data-key="estimate"] [data-testid="sort-button"]'));
    await respond(http, 'POST', `${API}/collections/${COLLECTION_ID}/query`, queryPage([]));
    await settle(harness.fixture);

    expect(q('view-modified')).not.toBeNull();
    expect(q('view-save')).toBeNull();
    expect(q('view-new')).toBeNull();
    expect(q('view-delete')).toBeNull();
  });

  it('reset_DropsLocalChanges', async () => {
    await render();
    await click(root().querySelector('[data-key="estimate"] [data-testid="sort-button"]'));
    await respond(http, 'POST', `${API}/collections/${COLLECTION_ID}/query`, queryPage([]));
    await settle(harness.fixture);

    await click(q('view-reset'));
    await respond(http, 'POST', `${API}/views/${CUSTOM_ID}/query`, queryPage([]));
    await settle(harness.fixture);

    expect(q('view-modified')).toBeNull();
    expect(all('sort-chip')).toHaveLength(0);
  });

  it('filter_AddCondition_CombinesIntoAdHocQuery', async () => {
    await render();

    await click(q('filter-toggle'));
    expect(text(q('filter-json-text'))).toContain('"status"');
    await click(q('filter-add'));
    const conditions = all('filter-condition');
    typeInto(
      conditions[1].querySelector<HTMLSelectElement>('[data-testid="filter-prop"]')!,
      'estimate',
    );
    await settle(harness.fixture);
    typeInto(conditions[1].querySelector<HTMLInputElement>('[data-testid="editor-input"]')!, '5');

    const query = await expectRequest(http, 'POST', `${API}/collections/${COLLECTION_ID}/query`);
    expect(query.request.body.filter).toEqual({
      and: [
        { prop: 'status', op: 'eq', value: 'todo' },
        { prop: 'estimate', op: 'eq', value: 5 },
      ],
    });
    query.flush(queryPage([]));
    await settle(harness.fixture);
    expect(text(q('filter-toggle'))).toContain('2');
  });

  it('delete_SystemView_IsDisabled', async () => {
    await render({ url: `${BASE}/${VIEW_ID}`, firstQuery: `${API}/views/${VIEW_ID}/query` });

    const button = q('view-delete') as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    expect(button.getAttribute('title')).toContain('System-Views');
  });

  it('delete_CustomView_DeletesAndOpensNewDefault', async () => {
    await render();

    await click(q('view-delete'));
    await click(q('confirm-ok'));
    (await expectRequest(http, 'DELETE', `${API}/views/${CUSTOM_ID}`)).flush(null, {
      status: 204,
      statusText: 'No Content',
    });
    await respond(http, 'GET', `${API}/collections/${COLLECTION_ID}/views`, [
      { ...SYSTEM, isDefault: true },
    ]);
    await respond(http, 'POST', `${API}/views/${VIEW_ID}/query`, queryPage([]));
    await settle(harness.fixture);

    expect(router().url).toBe(`${BASE}/${VIEW_ID}`);
    expect(all('view-tab')).toHaveLength(1);
  });

  it('makeDefault_PostsDefaultAndMovesStar', async () => {
    await render({ url: `${BASE}/${VIEW_ID}`, firstQuery: `${API}/views/${VIEW_ID}/query` });

    await click(q('view-make-default'));
    (await expectRequest(http, 'POST', `${API}/views/${VIEW_ID}/default`)).flush({
      ...SYSTEM,
      isDefault: true,
    });
    await settle(harness.fixture);

    expect(all('view-tab').map(text)).toEqual(['Tabelle (Standard)', 'Offene']);
    expect(defaultMarks()).toEqual([true, false]);
    expect(q('view-make-default')).toBeNull();
  });

  it('create_Board_PostsWithGroupByAndOpensIt', async () => {
    await render();

    await click(q('view-new'));
    typeInto(q('view-create-name') as HTMLInputElement, 'Board');
    typeInto(q('view-create-type') as HTMLSelectElement, 'board');
    await settle(harness.fixture);
    expect((q('view-create-group') as HTMLSelectElement).value).toBe('status');
    await click(q('view-create-submit'));

    const post = await expectRequest(http, 'POST', `${API}/collections/${COLLECTION_ID}/views`);
    expect(post.request.body).toEqual({ name: 'Board', type: 'board', groupBy: 'status' });
    const board = viewDto({
      id: 'b1',
      name: 'Board',
      type: 'board',
      groupBy: 'status',
      isDefault: false,
      isSystem: false,
      sortOrder: 3,
    });
    post.flush(board, { status: 201, statusText: 'Created' });
    const query = await expectRequest(http, 'POST', `${API}/views/b1/query`);
    expect(query.request.body).toEqual({ limit: 500, includeCount: true });
    query.flush(queryPage([recordDto()]));
    await settle(harness.fixture);

    expect(router().url).toBe(`${BASE}/b1`);
    expect(all('board-column').length).toBeGreaterThan(0);
  });

  it('rename_PatchesName', async () => {
    await render();

    await click(q('view-rename'));
    typeInto(q('view-rename-name') as HTMLInputElement, 'Nur offene');
    await click(q('view-rename-submit'));
    const patch = await expectRequest(http, 'PATCH', `${API}/views/${CUSTOM_ID}`);
    expect(patch.request.body).toEqual({ name: 'Nur offene' });
    patch.flush({ ...CUSTOM, name: 'Nur offene' });
    await settle(harness.fixture);

    expect(text(all('view-tab')[1])).toContain('Nur offene');
  });

  it('create_DocumentType_ShowsTableHint', async () => {
    await render({
      views: [SYSTEM, { ...CUSTOM, type: 'document' }],
    });

    expect(text(q('view-type-hint'))).toContain('Dokument');
    expect(q('data-table')).not.toBeNull();
  });

  it('open_GalleryAndListViews_ShowNoTableHintAndRenderTheirOwnView', async () => {
    await render({ views: [SYSTEM, { ...CUSTOM, type: 'gallery' }] });

    expect(q('view-type-hint')).toBeNull();
    expect(q('data-table')).toBeNull();
    expect(all('gallery-card')).toHaveLength(1);
  });

  it('create_Gallery_PostsWithOptionalCoverAndOpensIt', async () => {
    await render();

    await click(q('view-new'));
    typeInto(q('view-create-name') as HTMLInputElement, 'Karten');
    typeInto(q('view-create-type') as HTMLSelectElement, 'gallery');
    await settle(harness.fixture);
    const cover = q('view-create-cover') as HTMLSelectElement;
    expect(Array.from(cover.options).map((o) => o.value)).toEqual(['', 'status', 'estimate']);
    typeInto(cover, 'status');
    await click(q('view-create-submit'));

    const post = await expectRequest(http, 'POST', `${API}/collections/${COLLECTION_ID}/views`);
    expect(post.request.body).toEqual({
      name: 'Karten',
      type: 'gallery',
      cardCoverProperty: 'status',
    });
    post.flush(
      viewDto({
        id: 'g1',
        name: 'Karten',
        type: 'gallery',
        cardCoverProperty: 'status',
        isDefault: false,
        isSystem: false,
        sortOrder: 3,
      }),
      { status: 201, statusText: 'Created' },
    );
    const query = await expectRequest(http, 'POST', `${API}/views/g1/query`);
    expect(query.request.body).toEqual({ limit: 60, includeCount: true });
    query.flush(queryPage([recordDto()]));
    await settle(harness.fixture);

    expect(router().url).toBe(`${BASE}/g1`);
    expect(all('gallery-card')).toHaveLength(1);
  });

  it('create_GalleryWithoutCover_PostsNoCover', async () => {
    await render();

    await click(q('view-new'));
    typeInto(q('view-create-name') as HTMLInputElement, 'Karten');
    typeInto(q('view-create-type') as HTMLSelectElement, 'gallery');
    await settle(harness.fixture);
    await click(q('view-create-submit'));

    const post = await expectRequest(http, 'POST', `${API}/collections/${COLLECTION_ID}/views`);
    expect(post.request.body).toEqual({ name: 'Karten', type: 'gallery' });
    post.flush(viewDto({ id: 'g2', type: 'gallery', isDefault: false, isSystem: false }), {
      status: 201,
      statusText: 'Created',
    });
    (await expectRequest(http, 'POST', `${API}/views/g2/query`)).flush(queryPage([]));
    await settle(harness.fixture);
  });

  it('cover_ChangeInPanel_PatchesCardCoverProperty', async () => {
    await render({ views: [SYSTEM, { ...CUSTOM, type: 'gallery' }] });

    await click(q('columns-toggle'));
    expect(text(q('columns-toggle'))).toBe('Felder');
    const select = q('cover-select') as HTMLSelectElement;
    expect(Array.from(select.options).map((o) => o.value)).toEqual(['', 'status', 'estimate']);
    typeInto(select, 'status');
    const patch = await expectRequest(http, 'PATCH', `${API}/views/${CUSTOM_ID}`);
    expect(patch.request.body).toEqual({ cardCoverProperty: 'status' });
    patch.flush({ ...CUSTOM, type: 'gallery', cardCoverProperty: 'status' });
    // the cover is a field the query must return: all fields are asked for only when needed
    await settle(harness.fixture);
  });

  it('fields_ListPanel_SelectionRunsAdHocQueryAndSaves', async () => {
    await render({ views: [SYSTEM, { ...CUSTOM, type: 'list' }] });

    await click(q('columns-toggle'));
    expect(text(q('columns-panel'))).toContain('Sichtbare Felder');
    expect(q('cover-select')).toBeNull();
    await click(root().querySelector<HTMLElement>('hlm-checkbox[data-key="status"] brn-checkbox'));
    const query = await expectRequest(http, 'POST', `${API}/collections/${COLLECTION_ID}/query`);
    expect((query.request.body as { fields: string[] }).fields).toEqual(['estimate']);
    query.flush(queryPage([recordDto()]));
    await settle(harness.fixture);

    await click(q('view-save'));
    const patch = await expectRequest(http, 'PATCH', `${API}/views/${CUSTOM_ID}`);
    expect((patch.request.body as { visibleProperties: string[] }).visibleProperties).toEqual([
      'estimate',
    ]);
    patch.flush({ ...CUSTOM, type: 'list', visibleProperties: ['estimate'] });
    // the saved view runs as stored again
    await respond(http, 'POST', `${API}/views/${CUSTOM_ID}/query`, queryPage([recordDto()]));
    await settle(harness.fixture);
    expect(q('view-modified')).toBeNull();
  });

  it('fields_GalleryPanel_KeepsCoverInFields', async () => {
    await render({
      views: [SYSTEM, { ...CUSTOM, type: 'gallery', cardCoverProperty: 'status' }],
    });

    await click(q('columns-toggle'));
    await click(
      root().querySelector<HTMLElement>('hlm-checkbox[data-key="estimate"] brn-checkbox'),
    );
    const query = await expectRequest(http, 'POST', `${API}/collections/${COLLECTION_ID}/query`);
    expect((query.request.body as { fields: string[] }).fields).toEqual(['status']);
    query.flush(queryPage([recordDto()]));
    await settle(harness.fixture);
  });

  it('cover_UnsupportedType_ShowsHintInsteadOfNoCover', async () => {
    await render({
      views: [SYSTEM, { ...CUSTOM, type: 'gallery', cardCoverProperty: 'notes' }],
      schemaExtra: [propertyDefinition({ key: 'notes', name: 'Notizen', type: 'markdown' })],
    });

    await click(q('columns-toggle'));
    expect(text(q('cover-unsupported'))).toBe('Cover-Feld vom Typ markdown wird nicht unterstützt');
  });

  it('cover_PatchFails_SelectShowsStoredValueAgain', async () => {
    await render({ views: [SYSTEM, { ...CUSTOM, type: 'gallery' }] });

    await click(q('columns-toggle'));
    const select = q('cover-select') as HTMLSelectElement;
    typeInto(select, 'status');
    (await expectRequest(http, 'PATCH', `${API}/views/${CUSTOM_ID}`)).flush(
      { code: 'internal_error', status: 500 },
      { status: 500, statusText: 'Server Error' },
    );
    await settle(harness.fixture);

    expect(select.value).toBe('');
    expect(q('view-save-error')).not.toBeNull();
  });

  it('archive_SelectedRows_ArchivesEachAndReloads', async () => {
    await render({ records: [recordDto(), recordDto({ id: 'r2', title: 'Zweiter' })] });

    await click(all('row-select')[0]);
    await click(all('row-select')[1]);
    expect(text(q('selection-bar'))).toContain('2 ausgewählt');
    await click(q('archive-selected'));
    await click(q('confirm-ok'));

    await respond(http, 'POST', `${API}/records/${RECORD_ID}/archive`, recordDto());
    await respond(http, 'POST', `${API}/records/r2/archive`, recordDto({ id: 'r2' }));
    await respond(http, 'POST', `${API}/views/${CUSTOM_ID}/query`, queryPage([]));
    await settle(harness.fixture);

    expect(q('selection-bar')).toBeNull();
    expect(q('table-empty')).not.toBeNull();
  });

  it('archive_Viewer_HasNoArchiveButton', async () => {
    await render({ scopes: ['read'] });

    await click(all('row-select')[0]);
    expect(q('selection-bar')).not.toBeNull();
    expect(q('archive-selected')).toBeNull();
  });
});
