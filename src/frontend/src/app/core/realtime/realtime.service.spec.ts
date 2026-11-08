import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import { FakeHub, provideFakeHub } from '../testing/fake-hub';
import {
  API,
  activityEvent,
  expectRequest,
  pageProviders,
  respond,
  workspace,
} from '../testing/test-utils';
import { ActivityEvent, CATCH_UP_MAX_PAGES, RealtimeService } from './realtime.service';

@Component({ template: '' })
class BlankComponent {}

const HEAD = (lastSeq: number) => ({ items: [], nextCursor: null, lastSeq });
const FORWARD = (items: ActivityEvent[], lastSeq: number) => ({ items, nextCursor: null, lastSeq });

describe('RealtimeService', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;
  let hub: FakeHub;
  let realtime: RealtimeService;
  let received: number[];
  let resyncs: number;
  let delays: number[];

  beforeEach(async () => {
    hub = new FakeHub();
    delays = [0];
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({
          routes: [
            { path: 'w/:workspaceSlug', component: BlankComponent },
            { path: 'workspaces', component: BlankComponent },
          ],
          ws: workspace(),
        }),
        provideFakeHub(hub, delays),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    realtime = TestBed.inject(RealtimeService);
    received = [];
    resyncs = 0;
    realtime.activity.subscribe((e) => received.push(Number(e.seq)));
    realtime.resync.subscribe(() => resyncs++);
  });

  afterEach(() => {
    realtime.stop();
    http.verify();
  });

  /** Answers the access probe and the catch-up of one connect; resolves when live. */
  async function connect(slug = 'demo', lastSeq = 10): Promise<void> {
    await respond(http, 'GET', `/api/v1/w/${slug}/activity?limit=1`, HEAD(lastSeq));
    await respond(
      http,
      'GET',
      `/api/v1/w/${slug}/activity?afterSeq=${lastSeq}&limit=200`,
      FORWARD([], lastSeq),
    );
    await vi.waitFor(() => expect(realtime.status()).toBe('live'));
  }

  it('start_WorkspaceOpen_ConnectsToItsHub', async () => {
    await harness.navigateByUrl('/w/demo');
    realtime.start();
    TestBed.tick();
    expect(realtime.status()).toBe('connecting');

    await connect();

    expect(hub.connections).toHaveLength(1);
    expect(hub.last.url).toBe('/w/demo/hub');
    expect(hub.last.starts).toBe(1);
  });

  it('start_NoWorkspace_StaysIdle', async () => {
    await harness.navigateByUrl('/workspaces');
    realtime.start();
    TestBed.tick();

    expect(realtime.status()).toBe('idle');
    expect(hub.connections).toHaveLength(0);
    http.expectNone(`${API}/activity?limit=1`);
  });

  it('switchWorkspace_ClosesOldAndOpensNewConnection', async () => {
    await harness.navigateByUrl('/w/demo');
    realtime.start();
    await connect();
    const first = hub.last;

    await harness.navigateByUrl('/w/other');
    await connect('other', 3);

    expect(first.stops).toBe(1);
    expect(first.state).toBe('disconnected');
    expect(hub.connections).toHaveLength(2);
    expect(hub.last.url).toBe('/w/other/hub');
    expect(hub.open).toEqual([hub.last]);
  });

  it('leaveWorkspace_ClosesConnection', async () => {
    await harness.navigateByUrl('/w/demo');
    realtime.start();
    await connect();

    await harness.navigateByUrl('/workspaces');
    TestBed.tick();

    expect(hub.last.stops).toBe(1);
    expect(hub.open).toEqual([]);
    expect(realtime.status()).toBe('idle');
  });

  it('stop_ClosesConnection', async () => {
    await harness.navigateByUrl('/w/demo');
    realtime.start();
    await connect();

    realtime.stop();

    expect(hub.last.stops).toBe(1);
    expect(realtime.status()).toBe('idle');
  });

  it('activity_SameSeqTwice_IsDeliveredOnce', async () => {
    await harness.navigateByUrl('/w/demo');
    realtime.start();
    await connect();

    hub.last.emit('activity', activityEvent({ seq: 11 }));
    hub.last.emit('activity', activityEvent({ seq: 11 }));
    hub.last.emit('activity', activityEvent({ seq: 12 }));

    expect(received).toEqual([11, 12]);
  });

  it('activity_EventsOfOldWorkspaceAfterSwitch_AreIgnored', async () => {
    await harness.navigateByUrl('/w/demo');
    realtime.start();
    await connect();
    const first = hub.last;

    await harness.navigateByUrl('/w/other');
    await connect('other', 3);
    first.emit('activity', activityEvent({ seq: 50 }));

    expect(received).toEqual([]);
  });

  it('reconnect_AfterDrop_CatchesUpFromLastLiveEventWithoutDuplicates', async () => {
    await harness.navigateByUrl('/w/demo');
    realtime.start();
    await connect();
    hub.last.emit('activity', activityEvent({ seq: 11 }));

    hub.last.drop();
    expect(realtime.status()).toBe('reconnecting');
    await respond(http, 'GET', `${API}/activity?limit=1`, HEAD(14));
    // commit order: 13 committed before 12; 11 was seen live already
    await respond(
      http,
      'GET',
      `${API}/activity?afterSeq=11&limit=200`,
      FORWARD([activityEvent({ seq: 13 }), activityEvent({ seq: 12 })], 12),
    );
    await vi.waitFor(() => expect(received).toEqual([11, 13, 12]));

    expect(realtime.status()).toBe('live');
    expect(hub.connections).toHaveLength(1);
    expect(hub.last.starts).toBe(2);
    expect(resyncs).toBe(0);

    // the next live events continue normally, duplicates of caught-up ones are dropped
    hub.last.emit('activity', activityEvent({ seq: 13 }));
    hub.last.emit('activity', activityEvent({ seq: 14 }));
    expect(received).toEqual([11, 13, 12, 14]);
  });

  it('reconnect_FullPages_ReadsOnUntilAPageIsNotFull', async () => {
    await harness.navigateByUrl('/w/demo');
    realtime.start();
    await connect();

    hub.last.drop();
    await respond(http, 'GET', `${API}/activity?limit=1`, HEAD(500));
    const page = Array.from({ length: 200 }, (_, i) => activityEvent({ seq: 11 + i }));
    await respond(http, 'GET', `${API}/activity?afterSeq=10&limit=200`, FORWARD(page, 210));
    await respond(
      http,
      'GET',
      `${API}/activity?afterSeq=210&limit=200`,
      FORWARD([activityEvent({ seq: 211 })], 211),
    );

    await vi.waitFor(() => expect(received).toHaveLength(201));
    expect(resyncs).toBe(0);
  });

  it('reconnect_TooMuchMissed_AsksForResync', async () => {
    await harness.navigateByUrl('/w/demo');
    realtime.start();
    await connect();

    hub.last.drop();
    await respond(http, 'GET', `${API}/activity?limit=1`, HEAD(5000));
    let after = 10;
    for (let i = 0; i < CATCH_UP_MAX_PAGES; i++) {
      const page = Array.from({ length: 200 }, (_, n) => activityEvent({ seq: after + 1 + n }));
      await respond(
        http,
        'GET',
        `${API}/activity?afterSeq=${after}&limit=200`,
        FORWARD(page, after + 200),
      );
      after += 200;
    }

    await vi.waitFor(() => expect(resyncs).toBe(1));
  });

  it('reconnect_CatchUpFails_AsksForResync', async () => {
    await harness.navigateByUrl('/w/demo');
    realtime.start();
    await connect();

    hub.last.drop();
    await respond(http, 'GET', `${API}/activity?limit=1`, HEAD(12));
    await respond(
      http,
      'GET',
      `${API}/activity?afterSeq=10&limit=200`,
      { code: 'internal_error' },
      { status: 500, statusText: 'Server Error' },
    );

    await vi.waitFor(() => expect(resyncs).toBe(1));
    expect(realtime.status()).toBe('live');

    // position lost: the next reconnect starts from the head and asks for a reload again
    hub.last.drop();
    await respond(http, 'GET', `${API}/activity?limit=1`, HEAD(20));
    await vi.waitFor(() => expect(resyncs).toBe(2));
    http.expectNone(`${API}/activity?afterSeq=20&limit=200`);
  });

  it('reconnect_NoAccessAnyMore_GoesOfflineWithoutRetrying', async () => {
    await harness.navigateByUrl('/w/demo');
    realtime.start();
    await connect();

    hub.last.drop();
    await respond(
      http,
      'GET',
      `${API}/activity?limit=1`,
      { code: 'not_found' },
      { status: 404, statusText: 'Not Found' },
    );

    await vi.waitFor(() => expect(realtime.status()).toBe('offline'));
    await new Promise((resolve) => setTimeout(resolve, 20));
    http.expectNone(`${API}/activity?limit=1`);
    expect(hub.last.starts).toBe(1);
  });

  it('reconnect_Forbidden_GoesOffline', async () => {
    await harness.navigateByUrl('/w/demo');
    realtime.start();
    await respond(
      http,
      'GET',
      `${API}/activity?limit=1`,
      { code: 'forbidden' },
      { status: 403, statusText: 'Forbidden' },
    );

    await vi.waitFor(() => expect(realtime.status()).toBe('offline'));
    expect(hub.connections).toHaveLength(0);
  });

  it('retry_AfterOffline_ConnectsAgain', async () => {
    await harness.navigateByUrl('/w/demo');
    realtime.start();
    await respond(
      http,
      'GET',
      `${API}/activity?limit=1`,
      { code: 'not_found' },
      { status: 404, statusText: 'Not Found' },
    );
    await vi.waitFor(() => expect(realtime.status()).toBe('offline'));

    realtime.retry();
    expect(realtime.status()).toBe('reconnecting');
    await connect();

    expect(hub.open).toHaveLength(1);
  });

  it('retry_WhileReconnecting_CatchesUpFromLastPosition', async () => {
    delays.push(60_000); // the second attempt would only come in a minute
    await harness.navigateByUrl('/w/demo');
    realtime.start();
    await connect();
    hub.last.emit('activity', activityEvent({ seq: 11 }));

    hub.last.drop();
    const probe = await expectRequest(http, 'GET', `${API}/activity?limit=1`);
    probe.error(new ProgressEvent('error'));
    await new Promise((resolve) => setTimeout(resolve, 10)); // failure handled, next try in 60 s
    http.expectNone(`${API}/activity?limit=1`);
    expect(realtime.status()).toBe('reconnecting');

    realtime.retry();
    await respond(http, 'GET', `${API}/activity?limit=1`, HEAD(30));
    await respond(
      http,
      'GET',
      `${API}/activity?afterSeq=11&limit=200`,
      FORWARD([activityEvent({ seq: 12 })], 12),
    );

    await vi.waitFor(() => expect(received).toEqual([11, 12]));
    expect(realtime.status()).toBe('live');
    expect(hub.connections).toHaveLength(1);
  });

  it('retry_AttemptRunning_DoesNotStartASecondOne', async () => {
    await harness.navigateByUrl('/w/demo');
    realtime.start();
    await connect();

    hub.last.drop();
    const probe = await expectRequest(http, 'GET', `${API}/activity?limit=1`);
    realtime.retry();
    TestBed.tick();

    http.expectNone(`${API}/activity?limit=1`);
    probe.flush(HEAD(10));
    await respond(http, 'GET', `${API}/activity?afterSeq=10&limit=200`, FORWARD([], 10));
    await vi.waitFor(() => expect(realtime.status()).toBe('live'));
  });

  it('reconnect_DropDuringCatchUp_ConnectsAgain', async () => {
    await harness.navigateByUrl('/w/demo');
    realtime.start();
    await respond(http, 'GET', `${API}/activity?limit=1`, HEAD(10));
    const catchUp = await expectRequest(http, 'GET', `${API}/activity?afterSeq=10&limit=200`);
    expect(realtime.status()).toBe('live');

    // the connection drops while the catch-up still reads
    hub.last.drop();
    await new Promise((resolve) => setTimeout(resolve, 10)); // the retry timer (0 ms) has fired
    catchUp.flush(FORWARD([activityEvent({ seq: 11 })], 11));
    await respond(http, 'GET', `${API}/activity?limit=1`, HEAD(11));
    await respond(http, 'GET', `${API}/activity?afterSeq=11&limit=200`, FORWARD([], 11));

    await vi.waitFor(() => expect(realtime.status()).toBe('live'));
    expect(hub.last.starts).toBe(2);
    expect(hub.last.state).toBe('connected');
    hub.last.emit('activity', activityEvent({ seq: 12 }));
    expect(received).toEqual([11, 12]);
  });

  it('reconnect_DropDuringCatchUp_OldCatchUpDoesNotMoveThePosition', async () => {
    await harness.navigateByUrl('/w/demo');
    realtime.start();
    await respond(http, 'GET', `${API}/activity?limit=1`, HEAD(10));
    const oldCatchUp = await expectRequest(http, 'GET', `${API}/activity?afterSeq=10&limit=200`);

    hub.last.drop();
    await respond(http, 'GET', `${API}/activity?limit=1`, HEAD(10));
    const newCatchUp = await expectRequest(http, 'GET', `${API}/activity?afterSeq=10&limit=200`);
    newCatchUp.flush(FORWARD([activityEvent({ seq: 11 })], 11));
    await vi.waitFor(() => expect(received).toEqual([11]));
    oldCatchUp.flush(FORWARD([activityEvent({ seq: 11 }), activityEvent({ seq: 12 })], 12));
    await vi.waitFor(() => expect(received).toEqual([11, 12]));

    // the next reconnect continues where the latest catch-up stopped
    hub.last.drop();
    await respond(http, 'GET', `${API}/activity?limit=1`, HEAD(12));
    await respond(http, 'GET', `${API}/activity?afterSeq=11&limit=200`, FORWARD([], 11));
    await vi.waitFor(() => expect(realtime.status()).toBe('live'));
  });

  it('reconnect_EventWithHandshake_DoesNotMoveTheCatchUpPosition', async () => {
    await harness.navigateByUrl('/w/demo');
    realtime.start();
    await connect();
    hub.last.emit('activity', activityEvent({ seq: 11 }));

    hub.last.duringNextStart = () => hub.last.emit('activity', activityEvent({ seq: 15 }));
    hub.last.drop();
    await respond(http, 'GET', `${API}/activity?limit=1`, HEAD(15));
    // reads after 11, not after 15: 12 to 14 were missed
    await respond(
      http,
      'GET',
      `${API}/activity?afterSeq=11&limit=200`,
      FORWARD(
        [activityEvent({ seq: 12 }), activityEvent({ seq: 13 }), activityEvent({ seq: 15 })],
        15,
      ),
    );

    await vi.waitFor(() => expect(received).toEqual([11, 15, 12, 13]));
  });

  it('start_HubUnreachable_RetriesWithBackoff', async () => {
    await harness.navigateByUrl('/w/demo');
    realtime.start();
    await respond(http, 'GET', `${API}/activity?limit=1`, HEAD(10));
    await vi.waitFor(() => expect(hub.connections).toHaveLength(1));
    // the first start already ran; make the retry fail once more
    await vi.waitFor(() => expect(hub.last.starts).toBe(1));
    await respond(http, 'GET', `${API}/activity?afterSeq=10&limit=200`, FORWARD([], 10));
    await vi.waitFor(() => expect(realtime.status()).toBe('live'));

    hub.last.failNextStart = new Error('WebSocket failed');
    hub.last.drop();
    await respond(http, 'GET', `${API}/activity?limit=1`, HEAD(10));
    await vi.waitFor(() => expect(hub.last.starts).toBe(2));
    expect(realtime.status()).toBe('reconnecting');

    await connect();
    expect(hub.last.starts).toBe(3);
  });

  it('start_NetworkErrorOnProbe_Retries', async () => {
    await harness.navigateByUrl('/w/demo');
    realtime.start();
    const probe = await expectRequest(http, 'GET', `${API}/activity?limit=1`);
    probe.error(new ProgressEvent('error'));

    await vi.waitFor(() => expect(realtime.status()).toBe('reconnecting'));
    await connect();
    expect(hub.connections).toHaveLength(1);
  });
});
