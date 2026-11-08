import { TestBed } from '@angular/core/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import {
  ADMIN_ME,
  AGENT_ID,
  API,
  COLLECTION_ID,
  RECORD_ID,
  SPACE_ID,
  activityEvent,
  collection,
  expectRequest,
  member,
  pageProviders,
  respond,
  serve,
  settle,
  space,
} from '../../core/testing/test-utils';
import { FakeRealtime, provideFakeRealtime } from '../../core/testing/fake-hub';
import { ActivityFeedComponent, matchesQuery } from './activity-feed.component';

const OTHER_COLLECTION_ID = '0199a000-0000-7000-8000-0000000000c2';

describe('ActivityFeedComponent', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;
  let realtime: FakeRealtime;

  async function render(first: { items: unknown[]; nextCursor: string | null }): Promise<void> {
    realtime = new FakeRealtime();
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({
          routes: [{ path: 'w/:workspaceSlug/activity', component: ActivityFeedComponent }],
        }),
        provideFakeRealtime(realtime),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl('/w/demo/activity');
    await serve(http, [
      [`${API}/activity?limit=30`, first],
      [`${API}/spaces`, [space()]],
      [
        `${API}/collections`,
        [collection(), collection({ id: OTHER_COLLECTION_ID, name: 'Bugs', spaceId: 'other' })],
      ],
      [`${API}/members`, [member(), member({ actorId: AGENT_ID, name: 'Coder', kind: 'agent' })]],
    ]);
    await navigation;
    await settle(harness.fixture);
  }

  afterEach(() => http.verify());

  const root = () => harness.routeNativeElement as HTMLElement;
  const q = <T extends HTMLElement = HTMLElement>(id: string) =>
    root().querySelector<T>(`[data-testid="${id}"]`);
  const all = (id: string) =>
    Array.from(root().querySelectorAll<HTMLElement>(`[data-testid="${id}"]`));
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  async function choose(id: string, value: string): Promise<void> {
    // The Helm native select wraps the <select>; the test id sits on the wrapper.
    const select = q(id)!.querySelector('select')!;
    select.value = value;
    select.dispatchEvent(new Event('change', { bubbles: true }));
    harness.fixture.detectChanges();
    await settle(harness.fixture);
  }

  it('render_Events_ListsEntriesWithActorTypeAndTargetLink', async () => {
    await render({
      items: [
        activityEvent({
          id: 'a',
          type: 'record.created',
          diff: { title: { old: null, new: 'X' } },
        }),
        activityEvent({ id: 'b', actorId: ADMIN_ME.id, type: 'record.archived' }),
      ],
      nextCursor: null,
    });

    const entries = all('activity-item');
    expect(entries).toHaveLength(2);
    expect(text(entries[0].querySelector('[data-testid="actor-name"]'))).toBe('Coder');
    expect(text(entries[0].querySelector('[data-testid="actor-kind"]'))).toBe('Agent');
    expect(text(entries[0].querySelector('[data-testid="entry-text"]'))).toBe(
      'hat Record angelegt',
    );
    expect(text(entries[0].querySelector('[data-testid="entry-name"]'))).toBe('„X“');
    expect(text(entries[1].querySelector('[data-testid="actor-kind"]'))).toBe('Mensch');
    expect(text(entries[1].querySelector('[data-testid="entry-text"]'))).toBe(
      'hat einen Record archiviert',
    );
    expect(all('activity-link')[0].getAttribute('href')).toBe(`/w/demo/records/${RECORD_ID}`);
    expect(q('load-more')).toBeNull();
  });

  it('render_NoEvents_ShowsHint', async () => {
    await render({ items: [], nextCursor: null });

    expect(text(q('activity-empty'))).toBe('Keine Ereignisse.');
  });

  it('render_DiffToggle_OpensTheChangedKeys', async () => {
    await render({
      items: [activityEvent({ diff: { properties: { status: { old: 'todo', new: 'done' } } } })],
      nextCursor: null,
    });

    q('entry-toggle')!.click();
    harness.fixture.detectChanges();

    expect(text(q('entry-change'))).toBe('status todo done');
  });

  it('loadMore_Cursor_AppendsNextPage', async () => {
    await render({ items: [activityEvent({ id: 'a' })], nextCursor: 'c1' });

    q('load-more')!.click();
    await respond(http, 'GET', `${API}/activity?limit=30&cursor=c1`, {
      items: [activityEvent({ id: 'b' })],
      nextCursor: null,
    });
    await settle(harness.fixture);

    expect(all('activity-item')).toHaveLength(2);
    expect(q('load-more')).toBeNull();
  });

  it('filter_Collection_RequestsWithCollectionId', async () => {
    await render({ items: [], nextCursor: null });

    const change = choose('filter-collection', COLLECTION_ID);
    await respond(http, 'GET', `${API}/activity?collectionId=${COLLECTION_ID}&limit=30`, {
      items: [activityEvent({ id: 'filtered' })],
      nextCursor: null,
    });
    await change;
    await settle(harness.fixture);

    expect(all('activity-item')).toHaveLength(1);
  });

  it('filter_ActorSpaceAndSince_RequestsWithAllParameters', async () => {
    await render({ items: [], nextCursor: null });

    const actor = choose('filter-actor', AGENT_ID);
    await respond(http, 'GET', `${API}/activity?actorId=${AGENT_ID}&limit=30`, {
      items: [],
      nextCursor: null,
    });
    await actor;
    const spaceChange = choose('filter-space', SPACE_ID);
    await respond(http, 'GET', `${API}/activity?spaceId=${SPACE_ID}&actorId=${AGENT_ID}&limit=30`, {
      items: [],
      nextCursor: null,
    });
    await spaceChange;
    const since = q<HTMLInputElement>('filter-since')!;
    since.value = '2026-11-01';
    since.dispatchEvent(new Event('change', { bubbles: true }));
    const expectedSince = new Date('2026-11-01T00:00:00').toISOString();
    await respond(
      http,
      'GET',
      `${API}/activity?spaceId=${SPACE_ID}&actorId=${AGENT_ID}&since=${expectedSince}&limit=30`,
      { items: [], nextCursor: null },
    );
    await settle(harness.fixture);
  });

  it('filter_Types_SendsRepeatedTypesParameter', async () => {
    await render({ items: [], nextCursor: null });

    q('filter-types-summary')!.click();
    await harness.fixture.whenStable();
    // The popover renders into the CDK overlay container in <body>.
    const box = (type: string) =>
      document.body.querySelector<HTMLElement>(`[data-testid="filter-type-${type}"] button`)!;
    box('record.created').click();
    harness.fixture.detectChanges();
    await respond(http, 'GET', `${API}/activity?types=record.created&limit=30`, {
      items: [],
      nextCursor: null,
    });
    box('member.added').click();
    harness.fixture.detectChanges();
    await respond(http, 'GET', `${API}/activity?types=record.created&types=member.added&limit=30`, {
      items: [],
      nextCursor: null,
    });
    await settle(harness.fixture);
  });

  it('filter_TypesPopover_DialogHasAccessibleName', async () => {
    await render({ items: [], nextCursor: null });

    q('filter-types-summary')!.click();
    await harness.fixture.whenStable();

    // Brain sets role="dialog" on the overlay element and offers no label hook, so the content
    // inside carries the accessible name.
    const dialog = document.body.querySelector('[role="dialog"]');
    const group = dialog?.querySelector('[data-testid="filter-types"]');
    expect(group?.getAttribute('role')).toBe('group');
    expect(group?.getAttribute('aria-label')).toBe('Typen filtern');
  });

  it('filter_Space_OffersOnlyCollectionsOfThatSpace', async () => {
    await render({ items: [], nextCursor: null });

    const change = choose('filter-space', SPACE_ID);
    await respond(http, 'GET', `${API}/activity?spaceId=${SPACE_ID}&limit=30`, {
      items: [],
      nextCursor: null,
    });
    await change;

    const options = Array.from(q('filter-collection')!.querySelectorAll('option')).map((o) =>
      text(o),
    );
    expect(options).toEqual(['Alle', 'Aufgaben']);
  });

  it('filter_Reset_ReloadsWithoutFilters', async () => {
    await render({ items: [], nextCursor: null });
    const change = choose('filter-actor', AGENT_ID);
    await respond(http, 'GET', `${API}/activity?actorId=${AGENT_ID}&limit=30`, {
      items: [],
      nextCursor: null,
    });
    await change;

    q('filter-reset')!.click();
    await respond(http, 'GET', `${API}/activity?limit=30`, { items: [], nextCursor: null });
    await settle(harness.fixture);

    expect(q('filter-reset')).toBeNull();
  });

  it('filter_WorkspaceChanges_ResetsFiltersAndLoadsTheOtherWorkspace', async () => {
    await render({ items: [], nextCursor: null });
    const change = choose('filter-actor', AGENT_ID);
    await respond(http, 'GET', `${API}/activity?actorId=${AGENT_ID}&limit=30`, {
      items: [],
      nextCursor: null,
    });
    await change;

    const navigation = harness.navigateByUrl('/w/other/activity');
    await respond(http, 'GET', '/api/v1/w/other/activity?limit=30', {
      items: [],
      nextCursor: null,
    });
    await respond(http, 'GET', '/api/v1/w/other/spaces', []);
    await respond(http, 'GET', '/api/v1/w/other/collections', []);
    await respond(http, 'GET', '/api/v1/w/other/members', []);
    await navigation;
    await settle(harness.fixture);

    expect(q('filter-reset')).toBeNull();
    expect(q('filter-actor')!.querySelector('select')!.value).toBe('');
  });

  it('render_LoadError_ShowsMessage', async () => {
    TestBed.configureTestingModule({
      providers: pageProviders({
        routes: [{ path: 'w/:workspaceSlug/activity', component: ActivityFeedComponent }],
      }),
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl('/w/demo/activity');
    await respond(
      http,
      'GET',
      `${API}/activity?limit=30`,
      { code: 'forbidden', status: 403 },
      {
        status: 403,
        statusText: 'Forbidden',
      },
    );
    await serve(http, [
      [`${API}/spaces`, []],
      [`${API}/collections`, []],
      [`${API}/members`, []],
    ]);
    await navigation;
    await settle(harness.fixture);

    expect(text(q('activity-error'))).toContain('Rechte');
  });

  // --- live events (AP-025) -------------------------------------------------------------------

  it('live_NewEvent_AppearsOnTopWithoutReload', async () => {
    await render({
      items: [activityEvent({ id: 'b', seq: 5 }), activityEvent({ id: 'a', seq: 4 })],
      nextCursor: 'c1',
    });

    realtime.emit(
      activityEvent({ id: 'c', seq: 6, type: 'record.created', targetTitle: 'Neu vom Agenten' }),
    );
    await settle(harness.fixture);

    const entries = all('activity-item');
    expect(entries).toHaveLength(3);
    expect(text(entries[0].querySelector('[data-testid="entry-text"]'))).toContain(
      'Record angelegt',
    );
    expect(text(entries[0])).toContain('Neu vom Agenten');
    http.expectNone(`${API}/activity?limit=30`);
    // the cursor of the loaded pages is untouched
    q('load-more')!.click();
    await respond(http, 'GET', `${API}/activity?limit=30&cursor=c1`, {
      items: [activityEvent({ id: 'z', seq: 3 })],
      nextCursor: null,
    });
    await settle(harness.fixture);
    expect(all('activity-item')).toHaveLength(4);
  });

  it('live_SameSeqAsListed_IsNotShownTwice', async () => {
    await render({ items: [activityEvent({ id: 'b', seq: 5 })], nextCursor: null });

    realtime.emit(activityEvent({ id: 'b', seq: 5 }));
    realtime.emit(activityEvent({ id: 'c', seq: '6' as never }));
    realtime.emit(activityEvent({ id: 'c', seq: 6 }));
    await settle(harness.fixture);

    expect(all('activity-item')).toHaveLength(2);
  });

  it('live_FilteredOut_IsNotShown', async () => {
    await render({ items: [], nextCursor: null });
    const change = choose('filter-collection', COLLECTION_ID);
    await respond(http, 'GET', `${API}/activity?collectionId=${COLLECTION_ID}&limit=30`, {
      items: [],
      nextCursor: null,
    });
    await change;
    await settle(harness.fixture);

    realtime.emit(activityEvent({ id: 'other', seq: 7, collectionId: OTHER_COLLECTION_ID }));
    // member events come without collection (null fields are omitted by the hub)
    const memberEvent = activityEvent({ id: 'member', seq: 8, type: 'member.added' });
    delete (memberEvent as Partial<typeof memberEvent>).collectionId;
    realtime.emit(memberEvent);
    realtime.emit(activityEvent({ id: 'match', seq: 9 }));
    await settle(harness.fixture);

    expect(all('activity-item')).toHaveLength(1);
    expect(text(all('activity-item')[0])).not.toContain('Mitglied');
  });

  it('live_EventDuringFirstLoad_IsMergedOnceThePageIsThere', async () => {
    realtime = new FakeRealtime();
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({
          routes: [{ path: 'w/:workspaceSlug/activity', component: ActivityFeedComponent }],
        }),
        provideFakeRealtime(realtime),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl('/w/demo/activity');
    const first = await expectRequest(http, 'GET', `${API}/activity?limit=30`);
    realtime.emit(activityEvent({ id: 'listed', seq: 5 }));
    realtime.emit(activityEvent({ id: 'new', seq: 6 }));
    first.flush({ items: [activityEvent({ id: 'listed', seq: 5 })], nextCursor: null });
    await serve(http, [
      [`${API}/spaces`, [space()]],
      [`${API}/collections`, [collection()]],
      [`${API}/members`, [member()]],
    ]);
    await navigation;
    await settle(harness.fixture);

    expect(all('activity-item')).toHaveLength(2);
  });

  it('live_OtherWorkspaceConnection_IsIgnored', async () => {
    await render({ items: [], nextCursor: null });
    realtime.slug = 'other';

    realtime.emit(activityEvent({ id: 'x', seq: 9 }));
    await settle(harness.fixture);

    expect(all('activity-item')).toHaveLength(0);
  });

  it('resync_ReloadsTheFirstPage', async () => {
    await render({ items: [activityEvent({ id: 'a', seq: 4 })], nextCursor: null });

    realtime.emitResync();
    await respond(http, 'GET', `${API}/activity?limit=30`, {
      items: [activityEvent({ id: 'b', seq: 5 }), activityEvent({ id: 'a', seq: 4 })],
      nextCursor: null,
    });
    await settle(harness.fixture);

    expect(all('activity-item')).toHaveLength(2);
  });
});

describe('matchesQuery', () => {
  it('matchesQuery_NoFilters_LetsEverythingThrough', () => {
    expect(matchesQuery(activityEvent(), {})).toBe(true);
  });

  it('matchesQuery_EachFilter_MustMatch', () => {
    const event = activityEvent({ type: 'record.updated', actorId: AGENT_ID });
    expect(matchesQuery(event, { spaceId: SPACE_ID, collectionId: COLLECTION_ID })).toBe(true);
    expect(matchesQuery(event, { spaceId: 'other' })).toBe(false);
    expect(matchesQuery(event, { collectionId: OTHER_COLLECTION_ID })).toBe(false);
    expect(matchesQuery(event, { actorId: ADMIN_ME.id })).toBe(false);
    expect(matchesQuery(event, { types: ['record.created'] })).toBe(false);
    expect(matchesQuery(event, { types: ['record.created', 'record.updated'] })).toBe(true);
    expect(matchesQuery(event, { since: '2026-11-08T00:00:00Z' })).toBe(false);
    expect(matchesQuery(event, { since: '2026-11-07T00:00:00Z' })).toBe(true);
  });

  it('matchesQuery_FieldOmittedByHub_DoesNotMatchAFilterOnIt', () => {
    const event = activityEvent({ type: 'member.added', targetType: 'member' });
    delete (event as Partial<typeof event>).spaceId;
    delete (event as Partial<typeof event>).collectionId;

    expect(matchesQuery(event, {})).toBe(true);
    expect(matchesQuery(event, { spaceId: SPACE_ID })).toBe(false);
    expect(matchesQuery(event, { collectionId: COLLECTION_ID })).toBe(false);
  });
});
