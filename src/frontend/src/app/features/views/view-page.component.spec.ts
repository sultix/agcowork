import { Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { By } from '@angular/platform-browser';
import { Router, RouterOutlet } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { LIVE_RELOAD_DELAY_MS, LIVE_RELOAD_MAX_WAIT_MS } from '../../core/realtime/live-reload';
import { FakeRealtime, provideFakeRealtime } from '../../core/testing/fake-hub';
import {
  API,
  COLLECTION_ID,
  RECORD_ID,
  VIEW_ID,
  activityEvent,
  collection,
  expectRequest,
  member,
  pageProviders,
  propertyDefinition,
  queryPage,
  recordDto,
  respond,
  respondIfRequested,
  settle,
  space,
  typeInto,
  viewDto,
} from '../../core/testing/test-utils';
import { CollectionStore } from './collection.store';
import { LIVE_REFRESH_DELAY_MS } from './live-refresh';
import { ViewPageComponent } from './view-page.component';

/** Stands in for the collection page: provides and connects the store. */
@Component({
  imports: [RouterOutlet],
  providers: [CollectionStore],
  template: '<router-outlet />',
})
class CollectionHostComponent {
  readonly store = inject(CollectionStore);
  constructor() {
    this.store.connect(COLLECTION_ID);
  }
}

const DELAY = 20;
const CUSTOM_ID = '0199a000-0000-7000-8000-0000000000e9';
const SYSTEM = viewDto();
const CUSTOM = viewDto({
  id: CUSTOM_ID,
  name: 'Meine Aufgaben',
  isDefault: false,
  isSystem: false,
  sortOrder: 1,
});
const DESCRIBE_URL = `${API}/collections/${COLLECTION_ID}/describe`;
const VIEWS_URL = `${API}/collections/${COLLECTION_ID}/views`;
const PRIORITY = propertyDefinition({ key: 'priority', name: 'Priorität', type: 'number' });

function describeBody(schema = collection().schema) {
  return {
    collection: collection({ schema }),
    space: space(),
    jsonSchema: {},
    example: {},
    templates: [],
    statistics: { recordCount: 1 },
  };
}

const row = () =>
  recordDto({ id: RECORD_ID, revision: 2, properties: { status: 'todo', estimate: 1 } as never });

describe('ViewPageComponent live', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;
  let realtime: FakeRealtime;

  async function render(viewId: string, views = [SYSTEM, CUSTOM]): Promise<void> {
    realtime = new FakeRealtime();
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({
          routes: [
            {
              path: 'w/:workspaceSlug/collections/:collectionId',
              component: CollectionHostComponent,
              children: [{ path: 'views/:viewId', component: ViewPageComponent }],
            },
          ],
        }),
        provideFakeRealtime(realtime),
        { provide: LIVE_RELOAD_DELAY_MS, useValue: DELAY },
        { provide: LIVE_RELOAD_MAX_WAIT_MS, useValue: DELAY * 4 },
        { provide: LIVE_REFRESH_DELAY_MS, useValue: DELAY },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl(
      `/w/demo/collections/${COLLECTION_ID}/views/${viewId}`,
    );
    await respond(http, 'GET', DESCRIBE_URL, describeBody());
    await respond(http, 'GET', VIEWS_URL, views);
    await respond(http, 'POST', `${API}/views/${viewId}/query`, queryPage([row()]));
    await navigation;
    await respondIfRequested(http, `${API}/members`, [member()]);
    await settle(harness.fixture);
  }

  afterEach(() => {
    http.match(`${API}/members`).forEach((r) => r.flush([]));
    http.verify();
    sessionStorage.clear();
  });

  const root = () => harness.fixture.nativeElement as HTMLElement;
  const q = (id: string) => root().querySelector<HTMLElement>(`[data-testid="${id}"]`);
  const all = (id: string) =>
    Array.from(root().querySelectorAll<HTMLElement>(`[data-testid="${id}"]`));
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const headers = () => all('column-header').map((h) => h.getAttribute('data-key') ?? text(h));
  const editor = () => document.querySelector<HTMLElement>('[data-testid="cell-editor"]');
  const store = () =>
    harness.fixture.debugElement
      .query(By.directive(CollectionHostComponent))
      .injector.get(CollectionStore);
  const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
  const viewDeleted = (seq: number, viewId: string) =>
    activityEvent({
      seq,
      id: `e${seq}`,
      type: 'view.deleted',
      targetType: 'view',
      targetId: viewId,
      collectionId: COLLECTION_ID,
    });

  it('live_ShownViewDeletedElsewhere_SwitchesToTheDefaultViewWithANotice', async () => {
    await render(CUSTOM_ID);
    expect(q('view-deleted-notice')).toBeNull();

    realtime.emit(viewDeleted(1, CUSTOM_ID));
    await respond(http, 'GET', VIEWS_URL, [SYSTEM]);
    await respond(http, 'POST', `${API}/views/${VIEW_ID}/query`, queryPage([row()]));
    await settle(harness.fixture);

    expect(TestBed.inject(Router).url).toBe(
      `/w/demo/collections/${COLLECTION_ID}/views/${VIEW_ID}`,
    );
    expect(q('view-missing')).toBeNull();
    expect(text(q('view-deleted-notice'))).toContain('„Meine Aufgaben“ wurde gelöscht');
  });

  it('live_ShownDefaultViewDeletedElsewhere_SwitchesToTheNewDefaultView', async () => {
    await render(CUSTOM_ID, [
      { ...SYSTEM, isDefault: false },
      { ...CUSTOM, isDefault: true },
    ]);

    realtime.emit(viewDeleted(1, CUSTOM_ID));
    // the server moved the default to the remaining view
    await respond(http, 'GET', VIEWS_URL, [SYSTEM]);
    await respond(http, 'POST', `${API}/views/${VIEW_ID}/query`, queryPage([row()]));
    await settle(harness.fixture);

    expect(TestBed.inject(Router).url).toBe(
      `/w/demo/collections/${COLLECTION_ID}/views/${VIEW_ID}`,
    );
    expect(text(q('view-deleted-notice'))).toContain('„Meine Aufgaben“ wurde gelöscht');
  });

  it('live_ShownViewDeletedDuringOutage_ResyncSwitchesToTheDefaultView', async () => {
    await render(CUSTOM_ID);

    realtime.emitResync();
    await respond(http, 'GET', DESCRIBE_URL, describeBody());
    await respond(http, 'GET', VIEWS_URL, [SYSTEM]);
    await respond(http, 'POST', `${API}/views/${VIEW_ID}/query`, queryPage([row()]));
    await settle(harness.fixture);
    // the resync may have reloaded the table of the view shown before the switch as well
    for (const request of http.match({ method: 'POST', url: `${API}/views/${CUSTOM_ID}/query` })) {
      if (!request.cancelled) {
        request.flush(queryPage([row()]));
      }
    }

    expect(TestBed.inject(Router).url).toBe(
      `/w/demo/collections/${COLLECTION_ID}/views/${VIEW_ID}`,
    );
    expect(q('view-deleted-notice')).not.toBeNull();
  });

  it('live_OtherViewDeleted_StaysWithoutNotice', async () => {
    await render(VIEW_ID);

    realtime.emit(viewDeleted(1, CUSTOM_ID));
    await respond(http, 'GET', VIEWS_URL, [SYSTEM]);
    await settle(harness.fixture);

    expect(TestBed.inject(Router).url).toBe(
      `/w/demo/collections/${COLLECTION_ID}/views/${VIEW_ID}`,
    );
    expect(q('view-deleted-notice')).toBeNull();
  });

  it('live_ViewDeletedHere_NoNotice', async () => {
    await render(CUSTOM_ID);

    const removal = store().remove(CUSTOM_ID);
    (await expectRequest(http, 'DELETE', `${API}/views/${CUSTOM_ID}`)).flush(null);
    await removal;
    await respond(http, 'GET', VIEWS_URL, [SYSTEM]);
    await settle(harness.fixture);

    // the view switcher navigates itself after deleting; the page does not step in
    expect(TestBed.inject(Router).url).toBe(
      `/w/demo/collections/${COLLECTION_ID}/views/${CUSTOM_ID}`,
    );
    expect(q('view-deleted-notice')).toBeNull();
    expect(q('view-missing')).not.toBeNull();
  });

  it('live_UnknownViewId_KeepsTheMissingText', async () => {
    await render(CUSTOM_ID);
    await harness.navigateByUrl(`/w/demo/collections/${COLLECTION_ID}/views/unknown`);
    await settle(harness.fixture);

    expect(TestBed.inject(Router).url).toBe(`/w/demo/collections/${COLLECTION_ID}/views/unknown`);
    expect(q('view-missing')).not.toBeNull();
    expect(q('view-deleted-notice')).toBeNull();
  });

  it('live_SchemaChangedWhileACellIsEdited_ShowsTheNewColumnAfterTheEdit', async () => {
    await render(VIEW_ID);
    expect(headers()).not.toContain('priority');

    const cell = all('table-cell').find((c) => c.getAttribute('data-key') === 'estimate')!;
    cell.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    await settle(harness.fixture);
    const input = editor()!.querySelector<HTMLInputElement>('[data-testid="editor-input"]')!;
    typeInto(input, '8');

    realtime.emit(
      activityEvent({
        seq: 1,
        type: 'collection.schema_changed',
        targetType: 'collection',
        targetId: COLLECTION_ID,
      }),
    );
    await respond(http, 'GET', DESCRIBE_URL, describeBody([...collection().schema, PRIORITY]));
    await wait(DELAY * 3);
    await settle(harness.fixture);

    // the open editor is left alone: same input, typed value kept, no new column under it
    expect(editor()).not.toBeNull();
    expect(editor()!.querySelector('[data-testid="editor-input"]')).toBe(input);
    expect(input.value).toBe('8');
    expect(headers()).not.toContain('priority');
    http.expectNone(`${API}/views/${VIEW_ID}/query`);

    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await settle(harness.fixture);

    expect(editor()).toBeNull();
    expect(headers()).toContain('priority');
    // the rows follow too: a schema change can rewrite stored values
    await respond(http, 'POST', `${API}/views/${VIEW_ID}/query`, queryPage([row()]));
    await settle(harness.fixture);
  });
});
