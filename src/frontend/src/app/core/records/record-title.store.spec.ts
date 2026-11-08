import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HttpTestingController, TestRequest } from '@angular/common/http/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { FakeRealtime, provideFakeRealtime } from '../testing/fake-hub';
import {
  API,
  COLLECTION_ID,
  SPACE_ID,
  activityEvent,
  expectRequest,
  pageProviders,
  recordDto,
  workspace,
} from '../testing/test-utils';
import { WorkspacesStore } from '../../features/workspaces/workspaces.store';
import {
  LOOKUP_MAX_IDS,
  RECORD_TITLE_BATCH_MS,
  RECORD_TITLE_RETRY_MS,
  RecordTitleStore,
} from './record-title.store';

@Component({ template: '' })
class BlankComponent {}

const LOOKUP = `${API}/records/lookup`;
const BATCH = 5;
const RETRY = 40;
const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

/** A record id (UUID) from a number. */
const id = (n: number) => `0199a000-0000-7000-8000-${String(n).padStart(12, '0')}`;

const item = (n: number, title = `Record ${n}`, archived = false) => ({
  id: id(n),
  title,
  collectionId: COLLECTION_ID,
  spaceId: SPACE_ID,
  archived,
});

describe('RecordTitleStore', () => {
  let http: HttpTestingController;
  let store: RecordTitleStore;
  let realtime: FakeRealtime;
  let harness: RouterTestingHarness;

  beforeEach(async () => {
    realtime = new FakeRealtime();
    const list = signal([workspace(), workspace({ slug: 'other', name: 'Other' })]);
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({ routes: [{ path: 'w/:workspaceSlug', component: BlankComponent }] }),
        provideFakeRealtime(realtime),
        { provide: RECORD_TITLE_BATCH_MS, useValue: BATCH },
        { provide: RECORD_TITLE_RETRY_MS, useValue: RETRY },
        {
          provide: WorkspacesStore,
          useValue: { workspaces: list, find: (s: string) => list().find((w) => w.slug === s) },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/w/demo');
    store = TestBed.inject(RecordTitleStore);
  });

  afterEach(() => http.verify());

  /** The lookups open and not yet taken by `expectRequest` (`match` takes them). */
  const lookups = (): TestRequest[] => http.match({ method: 'POST', url: LOOKUP });
  const idsOf = (request: TestRequest) => (request.request.body as { ids: string[] }).ids;

  /** Reads `ids` and answers the one lookup that follows with `items`. */
  async function resolve(ids: string[], items: unknown[]): Promise<TestRequest> {
    ids.forEach((i) => store.get(i));
    const request = await expectRequest(http, 'POST', LOOKUP);
    request.flush({ items });
    await wait(0);
    return request;
  }

  it('get_ManyIdsAtOnce_OneLookupPer200DistinctIds', async () => {
    const ids = Array.from({ length: 450 }, (_, i) => id(i));

    for (const i of [...ids, ...ids]) {
      expect(store.get(i)).toEqual({ state: 'loading' });
    }
    await wait(BATCH * 3);

    const requests = lookups();
    expect(requests.map((r) => idsOf(r).length)).toEqual([LOOKUP_MAX_IDS, LOOKUP_MAX_IDS, 50]);
    expect(new Set(requests.flatMap(idsOf)).size).toBe(450);
    requests.forEach((r) => r.flush({ items: idsOf(r).map((i) => ({ ...item(0), id: i })) }));
  });

  it('get_FoundAndMissing_AreCachedAndNotAskedAgain', async () => {
    await resolve([id(1), id(2)], [item(1, 'Login bauen', true)]);

    expect(store.get(id(1))).toEqual({
      state: 'found',
      title: 'Login bauen',
      archived: true,
      collectionId: COLLECTION_ID,
      spaceId: SPACE_ID,
    });
    expect(store.get(id(2))).toEqual({ state: 'missing' });
    expect(store.label(id(2))).toBe('Nicht verfügbar');
    await wait(BATCH * 3);
    http.expectNone(LOOKUP);
  });

  it('get_WhileTheLookupRuns_DoesNotAskTwice', async () => {
    store.get(id(1));
    const request = await expectRequest(http, 'POST', LOOKUP);
    store.get(id(1));
    await wait(BATCH * 3);

    expect(lookups()).toHaveLength(0); // no second request besides `request`
    request.flush({ items: [item(1)] });
  });

  it('get_NoRecordId_IsMissingWithoutRequest', async () => {
    expect(store.get('not-a-uuid')).toEqual({ state: 'missing' });
    await wait(BATCH * 3);

    http.expectNone(LOOKUP);
  });

  it('get_LookupFails_MarksFailedAndShowsTheIdStart', async () => {
    store.get(id(1));
    (await expectRequest(http, 'POST', LOOKUP)).flush(
      { code: 'internal_error' },
      { status: 500, statusText: 'Server Error' },
    );
    await wait(0);

    expect(store.get(id(1))).toMatchObject({ state: 'failed' });
    expect(store.label(id(1))).toBe(id(1).slice(0, 8));
  });

  it('get_FailedAndRetryTimeOver_AsksAgain', async () => {
    store.get(id(1));
    (await expectRequest(http, 'POST', LOOKUP)).flush(
      { code: 'bad_gateway' },
      { status: 502, statusText: 'Bad Gateway' },
    );
    await wait(0);
    store.get(id(1)); // within the retry time: no new request
    await wait(BATCH * 3);
    http.expectNone(LOOKUP);

    await wait(RETRY);
    expect(store.get(id(1))).toMatchObject({ state: 'failed' }); // shown until the answer
    (await expectRequest(http, 'POST', LOOKUP)).flush({ items: [item(1, 'Wieder da')] });
    await wait(0);

    expect(store.label(id(1))).toBe('Wieder da');
  });

  it('realtime_BackLive_AsksAgainForWhatFailed', async () => {
    store.get(id(1));
    (await expectRequest(http, 'POST', LOOKUP)).flush(
      { code: 'bad_gateway' },
      { status: 502, statusText: 'Bad Gateway' },
    );
    await wait(0);

    realtime.status.set('reconnecting');
    TestBed.tick();
    await wait(BATCH * 3);
    http.expectNone(LOOKUP);
    realtime.status.set('live');
    TestBed.tick();
    (await expectRequest(http, 'POST', LOOKUP)).flush({ items: [item(1, 'Wieder da')] });
    await wait(0);

    expect(store.label(id(1))).toBe('Wieder da');
  });

  it('prefetch_ManyIds_OneLookupAndReadersFindThem', async () => {
    store.prefetch([id(1), id(2), 'no-uuid', id(1)]);
    const request = await expectRequest(http, 'POST', LOOKUP);
    expect(idsOf(request)).toEqual([id(1), id(2)]);
    store.prefetch([id(1), id(2)]); // on its way: not again
    store.get(id(2));
    await wait(BATCH * 3);
    expect(lookups()).toHaveLength(0);
    request.flush({ items: [item(1), item(2)] });
    await wait(0);

    store.prefetch([id(1)]); // known: not again
    await wait(BATCH * 3);
    http.expectNone(LOOKUP);
    expect(store.label(id(2))).toBe('Record 2');
  });

  it('live_RenamedWhileTheFirstLookupRuns_OlderAnswerIsNotShown', async () => {
    store.get(id(1));
    const first = await expectRequest(http, 'POST', LOOKUP);

    realtime.emit(activityEvent({ targetId: id(1), diff: { title: { old: 'Alt', new: 'Neu' } } }));
    first.flush({ items: [item(1, 'Alt')] }); // read before the rename
    await wait(0);
    expect(store.get(id(1))).toEqual({ state: 'loading' });
    const second = await expectRequest(http, 'POST', LOOKUP);
    second.flush({ items: [item(1, 'Neu')] });
    await wait(0);

    expect(store.label(id(1))).toBe('Neu');
  });

  it('resync_ForgetsTheLookupsOfTheOldCache', async () => {
    store.get(id(1));
    const old = await expectRequest(http, 'POST', LOOKUP);
    const pending = (store as unknown as { pending: Map<string, unknown> }).pending;
    expect(pending.has(id(1))).toBe(true);

    realtime.emitResync();
    store.get(id(2));

    expect(pending.has(id(1))).toBe(false);
    old.flush({ items: [] });
    (await expectRequest(http, 'POST', LOOKUP)).flush({ items: [] });
  });

  it('prime_SearchResults_AreKnownWithoutLookup', async () => {
    store.prime([recordDto({ id: id(7), title: 'Gefunden' })]);

    expect(store.label(id(7))).toBe('Gefunden');
    await wait(BATCH * 3);
    http.expectNone(LOOKUP);
  });

  describe('live', () => {
    beforeEach(async () => {
      await resolve([id(1), id(2)], [item(1, 'Alt'), item(2, 'Zwei')]);
    });

    it('live_TitleChangedByAgent_FetchesAgainAndShowsNewTitle', async () => {
      realtime.emit(
        activityEvent({ targetId: id(1), diff: { title: { old: 'Alt', new: 'Neu' } } }),
      );
      const request = await expectRequest(http, 'POST', LOOKUP);
      expect(idsOf(request)).toEqual([id(1)]);
      expect(store.label(id(1))).toBe('Alt'); // shown until the answer is there
      request.flush({ items: [item(1, 'Neu')] });
      await wait(0);

      expect(store.label(id(1))).toBe('Neu');
    });

    it('live_SeveralChangesAtOnce_OneLookup', async () => {
      realtime.emit(activityEvent({ seq: 1, targetId: id(1), type: 'record.archived' }));
      realtime.emit(activityEvent({ seq: 2, targetId: id(2), type: 'record.archived' }));
      const request = await expectRequest(http, 'POST', LOOKUP);
      request.flush({ items: [item(1, 'Alt', true), item(2, 'Zwei', true)] });
      await wait(0);

      expect(idsOf(request)).toEqual([id(1), id(2)]);
      expect(store.get(id(2))).toMatchObject({ archived: true });
    });

    it('live_PropertyChangeOnly_DoesNotFetch', async () => {
      realtime.emit(
        activityEvent({
          targetId: id(1),
          diff: { properties: { status: { old: 'a', new: 'b' } } },
        }),
      );
      await wait(BATCH * 3);

      http.expectNone(LOOKUP);
    });

    it('live_UncachedRecord_DoesNotFetch', async () => {
      realtime.emit(activityEvent({ targetId: id(9), type: 'record.archived' }));
      await wait(BATCH * 3);

      http.expectNone(LOOKUP);
    });

    it('live_CollectionArchived_FetchesItsCachedRecordsAgain', async () => {
      realtime.emit(
        activityEvent({
          type: 'collection.archived',
          targetType: 'collection',
          targetId: COLLECTION_ID,
        }),
      );
      const request = await expectRequest(http, 'POST', LOOKUP);
      expect(idsOf(request)).toEqual([id(1), id(2)]);
      request.flush({ items: [item(1, 'Alt', true), item(2, 'Zwei', true)] });
      await wait(0);

      expect(store.get(id(1))).toMatchObject({ archived: true });
    });

    it('live_EventOfAnotherWorkspace_IsIgnored', async () => {
      realtime.slug = 'other';
      realtime.emit(activityEvent({ targetId: id(1), type: 'record.archived' }));
      await wait(BATCH * 3);

      http.expectNone(LOOKUP);
    });

    it('live_OlderAnswerArrivesLast_KeepsTheNewerTitle', async () => {
      realtime.emit(activityEvent({ seq: 1, targetId: id(1), type: 'record.archived' }));
      const older = await expectRequest(http, 'POST', LOOKUP);
      realtime.emit(activityEvent({ seq: 2, targetId: id(1), type: 'record.restored' }));
      await wait(BATCH * 3);
      const newer = lookups().find((r) => r !== older)!;

      newer.flush({ items: [item(1, 'Neuer')] });
      older.flush({ items: [item(1, 'Älter', true)] });
      await wait(0);

      expect(store.get(id(1))).toMatchObject({ title: 'Neuer', archived: false });
    });

    it('live_OlderAnswerArrivesFirst_IsIgnoredAndTheNewerApplies', async () => {
      realtime.emit(activityEvent({ seq: 1, targetId: id(1), type: 'record.archived' }));
      const older = await expectRequest(http, 'POST', LOOKUP);
      realtime.emit(activityEvent({ seq: 2, targetId: id(1), type: 'record.restored' }));
      await wait(BATCH * 3);
      const newer = lookups().find((r) => r !== older)!;

      older.flush({ items: [item(1, 'Älter', true)] });
      await wait(0);
      expect(store.label(id(1))).toBe('Alt');
      newer.flush({ items: [item(1, 'Neuer')] });
      await wait(0);

      expect(store.get(id(1))).toMatchObject({ title: 'Neuer', archived: false });
    });

    it('live_RefreshOfMissingFails_StaysMissing', async () => {
      await resolve([id(3)], []);
      realtime.emit(activityEvent({ targetId: id(3), type: 'record.restored' }));
      (await expectRequest(http, 'POST', LOOKUP)).flush(
        { code: 'internal_error' },
        { status: 500, statusText: 'Server Error' },
      );
      await wait(0);

      expect(store.get(id(3))).toEqual({ state: 'missing' });
    });

    it('live_RefreshFails_KeepsTheTitle', async () => {
      realtime.emit(activityEvent({ targetId: id(1), type: 'record.archived' }));
      (await expectRequest(http, 'POST', LOOKUP)).flush(
        { code: 'internal_error' },
        { status: 500, statusText: 'Server Error' },
      );
      await wait(0);

      expect(store.label(id(1))).toBe('Alt');
    });

    it('resync_EmptiesTheCache_ReadersAskAgain', async () => {
      realtime.emitResync();

      expect(store.get(id(1))).toEqual({ state: 'loading' });
      const request = await expectRequest(http, 'POST', LOOKUP);
      expect(idsOf(request)).toEqual([id(1)]);
      request.flush({ items: [item(1, 'Frisch')] });
      await wait(0);

      expect(store.label(id(1))).toBe('Frisch');
    });

    it('resync_AnswerOfBeforeArrivesLater_IsDropped', async () => {
      realtime.emit(activityEvent({ targetId: id(1), type: 'record.archived' }));
      const before = await expectRequest(http, 'POST', LOOKUP);
      realtime.emitResync();
      before.flush({ items: [item(1, 'Vorher')] });
      await wait(0);

      expect(store.get(id(1))).toEqual({ state: 'loading' });
      (await expectRequest(http, 'POST', LOOKUP)).flush({ items: [item(1, 'Nachher')] });
      await wait(0);
      expect(store.label(id(1))).toBe('Nachher');
    });
  });

  it('workspaceSwitch_EmptiesTheCacheAndDropsAnswersOfTheOldWorkspace', async () => {
    store.get(id(1));
    const old = await expectRequest(http, 'POST', LOOKUP);

    await TestBed.inject(Router).navigateByUrl('/w/other');
    old.flush({ items: [item(1, 'Aus demo')] });
    await wait(0);

    expect(store.get(id(1))).toEqual({ state: 'loading' });
    (await expectRequest(http, 'POST', '/api/v1/w/other/records/lookup')).flush({ items: [] });
    await wait(0);
    expect(store.get(id(1))).toEqual({ state: 'missing' });
  });
});
