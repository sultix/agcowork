import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import { LIVE_RELOAD_DELAY_MS, LIVE_RELOAD_MAX_WAIT_MS } from '../../core/realtime/live-reload';
import { FakeRealtime, provideFakeRealtime } from '../../core/testing/fake-hub';
import {
  API,
  COLLECTION_ID,
  VIEW_ID,
  activityEvent,
  collection,
  expectRequest,
  pageProviders,
  propertyDefinition,
  respond,
  space,
  viewDto,
} from '../../core/testing/test-utils';
import { CollectionStore } from './collection.store';

@Component({ template: '' })
class BlankComponent {}

const DELAY = 20;
const DESCRIBE_URL = `${API}/collections/${COLLECTION_ID}/describe`;
const VIEWS_URL = `${API}/collections/${COLLECTION_ID}/views`;
const OTHER_VIEW_ID = '0199a000-0000-7000-8000-0000000000e9';
const PRIORITY = propertyDefinition({ key: 'priority', name: 'Priorität', type: 'number' });

function describeBody(recordCount: number, schema = collection().schema) {
  return {
    collection: collection({ schema }),
    space: space(),
    jsonSchema: {},
    example: {},
    templates: [],
    statistics: { recordCount },
  };
}

describe('CollectionStore live', () => {
  let http: HttpTestingController;
  let store: CollectionStore;
  let realtime: FakeRealtime;

  const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
  const recordEvent = (seq: number, type: string, collectionId: string | null = COLLECTION_ID) =>
    activityEvent({ seq, id: `e${seq}`, type, collectionId });
  const collectionEvent = (seq: number, type: string, targetId = COLLECTION_ID) =>
    activityEvent({ seq, id: `e${seq}`, type, targetType: 'collection', targetId });
  const viewEvent = (seq: number, type: string, collectionId = COLLECTION_ID) =>
    activityEvent({
      seq,
      id: `e${seq}`,
      type,
      targetType: 'view',
      targetId: OTHER_VIEW_ID,
      collectionId,
    });

  beforeEach(async () => {
    realtime = new FakeRealtime();
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({ routes: [{ path: 'w/:workspaceSlug', component: BlankComponent }] }),
        provideFakeRealtime(realtime),
        CollectionStore,
        { provide: LIVE_RELOAD_DELAY_MS, useValue: DELAY },
        { provide: LIVE_RELOAD_MAX_WAIT_MS, useValue: DELAY * 4 },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/w/demo');
    store = TestBed.inject(CollectionStore);
    store.connect(COLLECTION_ID);
    await respond(http, 'GET', DESCRIBE_URL, describeBody(2));
    await respond(http, 'GET', VIEWS_URL, [viewDto()]);
    await vi.waitFor(() => expect(store.recordCount()).toBe(2));
    await vi.waitFor(() => expect(store.views()).toHaveLength(1));
  });

  afterEach(() => http.verify());

  it('live_RecordCreated_CountGoesUpWithoutReload', async () => {
    realtime.emit(recordEvent(1, 'record.created'));

    await respond(http, 'GET', DESCRIBE_URL, describeBody(3));
    await vi.waitFor(() => expect(store.recordCount()).toBe(3));
    http.expectNone(VIEWS_URL);
  });

  it('live_RecordArchivedAndRestored_ReloadTheCountOnce', async () => {
    realtime.emit(recordEvent(1, 'record.archived'));
    realtime.emit(recordEvent(2, 'record.restored'));
    realtime.emit(recordEvent(3, 'record.archived'));

    await respond(http, 'GET', DESCRIBE_URL, describeBody(1));
    await vi.waitFor(() => expect(store.recordCount()).toBe(1));
    await wait(DELAY * 3);
    http.expectNone(DESCRIBE_URL);
  });

  it('live_RecordUpdatedOrOtherCollection_DoesNotReloadTheCount', async () => {
    realtime.emit(recordEvent(1, 'record.updated'));
    realtime.emit(recordEvent(2, 'record.appended'));
    realtime.emit(recordEvent(3, 'record.created', 'other-collection'));
    realtime.emit(collectionEvent(4, 'collection.updated', 'other-collection'));
    await wait(DELAY * 3);

    http.expectNone(DESCRIBE_URL);
  });

  it('live_EventOfAnotherWorkspace_IsIgnored', async () => {
    realtime.slug = 'other';
    realtime.emit(recordEvent(1, 'record.created'));
    realtime.emit(viewEvent(2, 'view.created'));
    await wait(DELAY * 3);

    http.expectNone(DESCRIBE_URL);
    http.expectNone(VIEWS_URL);
  });

  it('live_SchemaChanged_ShowsTheNewSchema', async () => {
    realtime.emit(collectionEvent(1, 'collection.schema_changed'));

    await respond(http, 'GET', DESCRIBE_URL, describeBody(2, [...collection().schema, PRIORITY]));
    await vi.waitFor(() => expect(store.schema().map((d) => d.key)).toContain('priority'));
  });

  it('live_CollectionUpdated_ShowsTheNewName', async () => {
    realtime.emit(collectionEvent(1, 'collection.updated'));

    const body = describeBody(2);
    body.collection = collection({ name: 'Tickets' });
    await respond(http, 'GET', DESCRIBE_URL, body);
    await vi.waitFor(() => expect(store.collection()?.name).toBe('Tickets'));
  });

  it('live_SchemaChangedWhileAViewEdits_HoldsTheSchemaBackUntilTheEditIsOver', async () => {
    const editing = signal(true);
    const release = store.holdWhile(() => editing());
    const before = store.schema();

    realtime.emit(collectionEvent(1, 'collection.schema_changed'));
    realtime.emit(recordEvent(2, 'record.created'));
    await respond(http, 'GET', DESCRIBE_URL, describeBody(3, [...collection().schema, PRIORITY]));

    // the count is not editable: it follows at once; the schema waits for the editor
    await vi.waitFor(() => expect(store.recordCount()).toBe(3));
    expect(store.editing()).toBe(true);
    expect(store.schema()).toBe(before);

    editing.set(false);
    expect(store.schema().map((d) => d.key)).toContain('priority');
    release();
  });

  it('holdWhile_Released_NoLongerHolds', () => {
    const release = store.holdWhile(() => true);
    expect(store.editing()).toBe(true);

    release();

    expect(store.editing()).toBe(false);
  });

  it('live_SameSchemaReloaded_KeepsTheSchemaInstance', async () => {
    const before = store.schema();

    realtime.emit(recordEvent(1, 'record.created'));
    await respond(http, 'GET', DESCRIBE_URL, describeBody(3));
    await vi.waitFor(() => expect(store.recordCount()).toBe(3));

    // an unchanged schema does not reach the views as a new value
    expect(store.schema()).toBe(before);
  });

  it('live_DescribeFails_KeepsWhatIsShown', async () => {
    realtime.emit(recordEvent(1, 'record.created'));
    (await expectRequest(http, 'GET', DESCRIBE_URL)).flush(
      { code: 'internal_error' },
      { status: 500, statusText: 'Server Error' },
    );
    await wait(DELAY);

    expect(store.loadError()).toBeNull();
    expect(store.collection()?.name).toBe('Aufgaben');
    expect(store.recordCount()).toBe(2);
  });

  it('live_ViewEvents_ReloadTheViewListOnce', async () => {
    realtime.emit(viewEvent(1, 'view.created'));
    realtime.emit(viewEvent(2, 'view.updated'));

    await respond(http, 'GET', VIEWS_URL, [
      viewDto(),
      viewDto({
        id: OTHER_VIEW_ID,
        name: 'Meine',
        isDefault: false,
        isSystem: false,
        sortOrder: 1,
      }),
    ]);
    await vi.waitFor(() => expect(store.views().map((v) => v.name)).toEqual(['Tabelle', 'Meine']));
    await wait(DELAY * 3);
    http.expectNone(VIEWS_URL);
    http.expectNone(DESCRIBE_URL);
  });

  it('live_ViewEventOfAnotherCollection_DoesNotReload', async () => {
    realtime.emit(viewEvent(1, 'view.created', 'other-collection'));
    await wait(DELAY * 3);

    http.expectNone(VIEWS_URL);
  });

  it('live_ViewListFails_KeepsTheViews', async () => {
    realtime.emit(viewEvent(1, 'view.deleted'));
    (await expectRequest(http, 'GET', VIEWS_URL)).flush(
      { code: 'internal_error' },
      { status: 500, statusText: 'Server Error' },
    );
    await wait(DELAY);

    expect(store.viewsError()).toBeNull();
    expect(store.find(VIEW_ID)?.name).toBe('Tabelle');
  });

  it('live_Resync_ReloadsDescriptionAndViews', async () => {
    realtime.emitResync();

    await respond(http, 'GET', DESCRIBE_URL, describeBody(7));
    await respond(http, 'GET', VIEWS_URL, [viewDto()]);
    await vi.waitFor(() => expect(store.recordCount()).toBe(7));
  });

  it('live_OlderDescribeAnswersLast_KeepsTheNewerOne', async () => {
    realtime.emit(recordEvent(1, 'record.created'));
    const older = await expectRequest(http, 'GET', DESCRIBE_URL);
    realtime.emit(recordEvent(2, 'record.created'));
    const newer = await expectRequest(http, 'GET', DESCRIBE_URL);

    newer.flush(describeBody(4));
    older.flush(describeBody(3));
    await wait(DELAY);

    expect(store.recordCount()).toBe(4);
  });

  it('live_ViewCreatedHereWhileTheListLoads_KeepsTheNewView', async () => {
    realtime.emit(viewEvent(1, 'view.updated'));
    const running = await expectRequest(http, 'GET', VIEWS_URL);

    const creation = store.create({ name: 'Meine', type: 'table' });
    (await expectRequest(http, 'POST', VIEWS_URL)).flush(
      viewDto({
        id: OTHER_VIEW_ID,
        name: 'Meine',
        isDefault: false,
        isSystem: false,
        sortOrder: 1,
      }),
    );
    await creation;
    // the answer was put together before the view existed
    running.flush([viewDto()]);
    await wait(DELAY);

    expect(store.views().map((v) => v.name)).toEqual(['Tabelle', 'Meine']);
  });

  it('live_ViewDeletedHereWhileTheListLoads_DoesNotComeBack', async () => {
    realtime.emit(viewEvent(1, 'view.updated'));
    const running = await expectRequest(http, 'GET', VIEWS_URL);

    const removal = store.remove(VIEW_ID);
    (await expectRequest(http, 'DELETE', `${API}/views/${VIEW_ID}`)).flush(null);
    await removal;
    const reload = await expectRequest(http, 'GET', VIEWS_URL);
    running.flush([viewDto()]);
    await wait(DELAY);

    expect(store.find(VIEW_ID)).toBeUndefined();
    reload.flush([]);
  });

  it('remove_ViewDeletedHere_IsMarked', async () => {
    const removal = store.remove(VIEW_ID);
    (await expectRequest(http, 'DELETE', `${API}/views/${VIEW_ID}`)).flush(null);
    await removal;
    await respond(http, 'GET', VIEWS_URL, []);

    expect(store.wasRemovedHere(VIEW_ID)).toBe(true);
    expect(store.wasRemovedHere(OTHER_VIEW_ID)).toBe(false);
  });
});
