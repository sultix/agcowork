import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import { LIVE_RELOAD_DELAY_MS, LIVE_RELOAD_MAX_WAIT_MS } from '../realtime/live-reload';
import { FakeRealtime, provideFakeRealtime } from '../testing/fake-hub';
import {
  API,
  ADMIN_ME,
  activityEvent,
  expectRequest,
  member,
  pageProviders,
  respond,
} from '../testing/test-utils';
import { MembersStore } from './members.store';

@Component({ template: '' })
class BlankComponent {}

const DELAY = 20;
const NEW_ACTOR = '0199a000-0000-7000-8000-0000000000f7';
const memberEvent = (seq: number, type = 'member.added') =>
  activityEvent({ seq, type, targetType: 'member', targetId: NEW_ACTOR, collectionId: null });

describe('MembersStore', () => {
  let http: HttpTestingController;
  let store: MembersStore;
  let realtime: FakeRealtime;

  const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

  beforeEach(async () => {
    realtime = new FakeRealtime();
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({ routes: [{ path: 'w/:workspaceSlug', component: BlankComponent }] }),
        provideFakeRealtime(realtime),
        { provide: LIVE_RELOAD_DELAY_MS, useValue: DELAY },
        { provide: LIVE_RELOAD_MAX_WAIT_MS, useValue: DELAY * 4 },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/w/demo');
    store = TestBed.inject(MembersStore);
    await respond(http, 'GET', `${API}/members`, [member()]);
    await vi.waitFor(() => expect(store.members()).toHaveLength(1));
  });

  afterEach(() => http.verify());

  it('live_MemberAdded_ShowsTheNameOfTheNewActor', async () => {
    // the feed shows an actor it does not know by the start of its id
    expect(store.nameOf(NEW_ACTOR)).toBe(NEW_ACTOR.slice(0, 8));

    realtime.emit(memberEvent(1));
    await respond(http, 'GET', `${API}/members`, [
      member(),
      member({ actorId: NEW_ACTOR, name: 'Planer', kind: 'agent' }),
    ]);

    await vi.waitFor(() => expect(store.nameOf(NEW_ACTOR)).toBe('Planer'));
    expect(store.kindOf(NEW_ACTOR)).toBe('agent');
    expect(store.nameOf(ADMIN_ME.id)).toBe('Admin');
  });

  it('live_SeveralMemberEvents_ReloadOnce', async () => {
    realtime.emit(memberEvent(1));
    realtime.emit(memberEvent(2, 'member.updated'));
    realtime.emit(memberEvent(3, 'member.removed'));

    await respond(http, 'GET', `${API}/members`, [member()]);
    await wait(DELAY * 3);
    http.expectNone(`${API}/members`);
  });

  it('live_OtherEvents_DoNotReload', async () => {
    realtime.emit(activityEvent({ seq: 1, type: 'record.created' }));
    realtime.emit(activityEvent({ seq: 2, type: 'space.created', targetType: 'space' }));
    await wait(DELAY * 3);

    http.expectNone(`${API}/members`);
  });

  it('live_EventOfAnotherWorkspace_DoesNotReload', async () => {
    realtime.slug = 'other';
    realtime.emit(memberEvent(1));
    await wait(DELAY * 3);

    http.expectNone(`${API}/members`);
  });

  it('live_Resync_Reloads', async () => {
    realtime.emitResync();

    await respond(http, 'GET', `${API}/members`, [
      member(),
      member({ actorId: NEW_ACTOR, name: 'Planer' }),
    ]);
    await vi.waitFor(() => expect(store.members()).toHaveLength(2));
  });

  it('live_ResyncOfAnotherWorkspace_DoesNotReload', async () => {
    realtime.slug = 'other';
    realtime.emitResync();
    await wait(DELAY * 3);

    http.expectNone(`${API}/members`);
  });

  it('live_OlderAnswerArrivesLast_KeepsTheNewerList', async () => {
    realtime.emit(memberEvent(1));
    const older = await expectRequest(http, 'GET', `${API}/members`);
    realtime.emit(memberEvent(2, 'member.updated'));
    const newer = await expectRequest(http, 'GET', `${API}/members`);

    newer.flush([member(), member({ actorId: NEW_ACTOR, name: 'Planerin' })]);
    older.flush([member(), member({ actorId: NEW_ACTOR, name: 'Planer' })]);
    await wait(DELAY);

    expect(store.nameOf(NEW_ACTOR)).toBe('Planerin');
  });

  it('live_ReloadFails_KeepsTheNames', async () => {
    realtime.emit(memberEvent(1));
    (await expectRequest(http, 'GET', `${API}/members`)).flush(
      { code: 'internal_error' },
      { status: 500, statusText: 'Server Error' },
    );
    await wait(DELAY);

    expect(store.members()).toHaveLength(1);
    expect(store.nameOf(ADMIN_ME.id)).toBe('Admin');
  });
});
