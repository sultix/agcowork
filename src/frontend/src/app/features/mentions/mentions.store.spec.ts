import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import { SessionStore } from '../../core/auth/session.store';
import { LIVE_RELOAD_DELAY_MS, LIVE_RELOAD_MAX_WAIT_MS } from '../../core/realtime/live-reload';
import { FakeRealtime, provideFakeRealtime } from '../../core/testing/fake-hub';
import {
  ADMIN_ME,
  API,
  FakeSession,
  AGENT_ID,
  activityEvent,
  expectRequest,
  pageProviders,
  respond,
} from '../../core/testing/test-utils';
import { MentionsStore, badgeLabel, mayChangeMentions, mentionsActor } from './mentions.store';

@Component({ template: '' })
class BlankComponent {}

const DELAY = 20;
const COUNT_URL = `${API}/mentions?unreadOnly=true&limit=1`;
const commentEvent = (seq: number, mentioned: string[], type = 'comment.created') =>
  activityEvent({ seq, type, targetType: 'comment', diff: { mentionedActorIds: mentioned } });
const page = (unreadCount: number) => ({ items: [], nextCursor: null, unreadCount });

describe('badgeLabel', () => {
  it.each([
    [1, '1'],
    [99, '99'],
    [100, '99+'],
    [1234, '99+'],
  ])('badgeLabel_Count_%i_Is_%s', (count, label) => {
    expect(badgeLabel(count)).toBe(label);
  });
});

describe('mentionsActor', () => {
  it('mentionsActor_OwnIdInCommentEvent_True', () => {
    expect(mentionsActor(commentEvent(1, [ADMIN_ME.id]), ADMIN_ME.id)).toBe(true);
  });

  it('mentionsActor_OtherIdOrNoDiffOrNoActor_False', () => {
    expect(mentionsActor(commentEvent(1, [AGENT_ID]), ADMIN_ME.id)).toBe(false);
    expect(mentionsActor(commentEvent(1, []), ADMIN_ME.id)).toBe(false);
    expect(mentionsActor(activityEvent({ type: 'comment.created' }), ADMIN_ME.id)).toBe(false);
    expect(mentionsActor(commentEvent(1, [ADMIN_ME.id]), undefined)).toBe(false);
  });

  it('mentionsActor_NonCommentEvent_False', () => {
    expect(mentionsActor(commentEvent(1, [ADMIN_ME.id], 'record.updated'), ADMIN_ME.id)).toBe(
      false,
    );
  });
});

describe('mayChangeMentions', () => {
  it('mayChangeMentions_DeletedOrUpdatedWithoutOwnId_True', () => {
    expect(mayChangeMentions(commentEvent(1, [], 'comment.deleted'), ADMIN_ME.id)).toBe(true);
    expect(mayChangeMentions(commentEvent(1, [AGENT_ID], 'comment.updated'), ADMIN_ME.id)).toBe(
      true,
    );
  });

  it('mayChangeMentions_CreatedOrResolvedWithoutOwnId_False', () => {
    expect(mayChangeMentions(commentEvent(1, [AGENT_ID]), ADMIN_ME.id)).toBe(false);
    expect(mayChangeMentions(commentEvent(1, [], 'comment.resolved'), ADMIN_ME.id)).toBe(false);
  });
});

describe('MentionsStore', () => {
  let http: HttpTestingController;
  let store: MentionsStore;
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
    store = TestBed.inject(MentionsStore);
    await respond(http, 'GET', COUNT_URL, page(2));
    await vi.waitFor(() => expect(store.unreadCount()).toBe(2));
  });

  afterEach(() => http.verify());

  it('unreadCount_Loaded_IsTheCountOfTheAnswer', () => {
    expect(store.unreadCount()).toBe(2);
  });

  it('unreadCount_LiveMentionOfOwnActor_ReloadsAndRises', async () => {
    realtime.emit(commentEvent(1, [ADMIN_ME.id]));
    await respond(http, 'GET', COUNT_URL, page(3));

    await vi.waitFor(() => expect(store.unreadCount()).toBe(3));
  });

  it('unreadCount_LiveMentionOfSomeoneElse_StaysAndAsksNothing', async () => {
    realtime.emit(commentEvent(1, [AGENT_ID]));
    await wait(DELAY * 3);

    expect(store.unreadCount()).toBe(2);
    http.expectNone(COUNT_URL);
  });

  it('unreadCount_CommentDeletedWithoutOwnId_Reloads', async () => {
    realtime.emit(commentEvent(1, [], 'comment.deleted'));
    await respond(http, 'GET', COUNT_URL, page(1));

    await vi.waitFor(() => expect(store.unreadCount()).toBe(1));
  });

  it('unreadCount_CommentUpdatedWithOwnId_Reloads', async () => {
    realtime.emit(commentEvent(1, [ADMIN_ME.id], 'comment.updated'));
    await respond(http, 'GET', COUNT_URL, page(4));

    await vi.waitFor(() => expect(store.unreadCount()).toBe(4));
  });

  it('unreadCount_StaleRefetchAnswer_IsDiscarded', async () => {
    realtime.emit(commentEvent(1, [ADMIN_ME.id]));
    const stale = await expectRequest(http, 'GET', COUNT_URL);
    store.accept(7);
    stale.flush(page(3));
    await wait(DELAY);

    expect(store.unreadCount()).toBe(7);
  });

  it('markRead_OlderAnswerArrivingLast_DoesNotOverwriteNewer', async () => {
    const first = store.markRead(['m1']);
    const a = await expectRequest(http, 'POST', `${API}/mentions/read`);
    const second = store.markRead(['m2']);
    const b = await expectRequest(http, 'POST', `${API}/mentions/read`);
    b.flush({ marked: 1, unreadCount: 0 });
    a.flush({ marked: 1, unreadCount: 1 });
    await Promise.all([first, second]);

    expect(store.unreadCount()).toBe(0);
  });

  it('unreadCount_SignedOut_ResetsToZero', async () => {
    (TestBed.inject(SessionStore) as unknown as FakeSession).status.set('anonymous');
    await vi.waitFor(() => expect(store.unreadCount()).toBe(0));
  });

  it('unreadCount_Resync_Reloads', async () => {
    realtime.emitResync();
    await respond(http, 'GET', COUNT_URL, page(5));

    await vi.waitFor(() => expect(store.unreadCount()).toBe(5));
  });

  it('markRead_Ids_PostsAndTakesTheCountFromTheAnswer', async () => {
    const done = store.markRead(['m1']);
    const request = await expectRequest(http, 'POST', `${API}/mentions/read`);
    expect(request.request.body).toEqual({ ids: ['m1'] });
    request.flush({ marked: 1, unreadCount: 1 });
    await done;

    expect(store.unreadCount()).toBe(1);
  });

  it('markAllRead_PostsAllAndClearsTheCount', async () => {
    const done = store.markAllRead();
    const request = await expectRequest(http, 'POST', `${API}/mentions/read`);
    expect(request.request.body).toEqual({ all: true });
    request.flush({ marked: 2, unreadCount: 0 });
    await done;

    expect(store.unreadCount()).toBe(0);
  });
});
