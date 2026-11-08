import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import { FakeHub, NO_JITTER, provideFakeHub } from '../testing/fake-hub';
import {
  API,
  activityEvent,
  expectRequest,
  pageProviders,
  respond,
  workspace,
} from '../testing/test-utils';
import { REALTIME_STABLE_AFTER_MS, RealtimeRetryJitter, jitteredDelay } from './realtime-hub';
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

describe('RealtimeService backoff', () => {
  const STABLE_AFTER = 30_000;
  const DELAYS = [0, 2_000, 5_000, 10_000];
  let harness: RouterTestingHarness;
  let http: HttpTestingController;
  let hub: FakeHub;
  let realtime: RealtimeService;
  let random: number;
  let resyncs: number;

  beforeEach(async () => {
    hub = new FakeHub();
    random = 0.5; // no spread in either direction, half of the spread of a 0 wait
    const jitter: RealtimeRetryJitter = { ratio: 0.2, zeroSpreadMs: 1_000, random: () => random };
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({
          routes: [{ path: 'w/:workspaceSlug', component: BlankComponent }],
          ws: workspace(),
        }),
        provideFakeHub(hub, DELAYS, jitter),
        { provide: REALTIME_STABLE_AFTER_MS, useValue: STABLE_AFTER },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    realtime = TestBed.inject(RealtimeService);
    resyncs = 0;
    realtime.resync.subscribe(() => resyncs++);
    await harness.navigateByUrl('/w/demo');
    realtime.start();
    await respond(http, 'GET', `${API}/activity?limit=1`, HEAD(10));
    await respond(http, 'GET', `${API}/activity?afterSeq=10&limit=200`, FORWARD([], 10));
    await vi.waitFor(() => expect(realtime.status()).toBe('live'));
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
  });

  afterEach(() => {
    vi.useRealTimers();
    realtime.stop();
    http.verify();
  });

  /** Lets the promise chain of a connection attempt run (no timers involved). */
  async function flush(): Promise<void> {
    for (let i = 0; i < 20; i++) {
      TestBed.tick();
      await Promise.resolve();
    }
  }

  const probes = () => http.match({ method: 'GET', url: `${API}/activity?limit=1` });

  /** Advances the clock until the next access probe goes out; returns how long that took. */
  async function waitForProbe(): Promise<number> {
    let waited = 0;
    for (;;) {
      await vi.advanceTimersByTimeAsync(0);
      await flush();
      const issued = probes();
      if (issued.length === 1) {
        issued[0].flush(HEAD(10));
        return waited;
      }
      expect(issued).toHaveLength(0);
      if (waited > 120_000) {
        throw new Error('No reconnect within two minutes.');
      }
      await vi.advanceTimersByTimeAsync(50);
      waited += 50;
    }
  }

  /** Answers the catch-up of an attempt whose probe was answered; the connection is live. */
  async function finishConnect(): Promise<void> {
    await flush();
    const catchUp = http.match({ method: 'GET', url: `${API}/activity?afterSeq=10&limit=200` });
    expect(catchUp).toHaveLength(1);
    catchUp[0].flush(FORWARD([], 10));
    await flush();
    expect(realtime.status()).toBe('live');
  }

  it('reconnect_ServerDropsFreshConnections_WaitsGrowUntilAConnectionHolds', async () => {
    // AP-028: the server drops each fresh connection again right after the handshake
    const waits: number[] = [];
    for (let i = 0; i < 3; i++) {
      hub.last.drop();
      waits.push(await waitForProbe());
      await finishConnect();
      await vi.advanceTimersByTimeAsync(1_000); // up for a second only
    }
    expect(waits).toEqual([500, 2_000, 5_000]);

    // a connection that holds for the minimum lifetime starts the backoff from the bottom
    await vi.advanceTimersByTimeAsync(STABLE_AFTER);
    hub.last.drop();
    expect(await waitForProbe()).toBe(500);
    await finishConnect();
    expect(hub.last.starts).toBe(5);
  });

  it('reconnect_ConnectionHeldJustUnderTheMinimum_KeepsBackingOff', async () => {
    hub.last.drop();
    await waitForProbe();
    await finishConnect();

    await vi.advanceTimersByTimeAsync(STABLE_AFTER - 100);
    hub.last.drop();

    expect(await waitForProbe()).toBe(2_000);
    await finishConnect();
  });

  it('reconnect_FailingProbesAfterADrop_KeepBackingOff', async () => {
    hub.last.drop();
    await flush();
    await vi.advanceTimersByTimeAsync(500);
    await flush();
    probes()[0].error(new ProgressEvent('error'));

    expect(await waitForProbe()).toBe(2_000);
    await finishConnect();
  });

  it('reconnect_Jitter_SpreadsTheWaitsWithinTheConfiguredRange', async () => {
    random = 0; // lower end: −20 %, and no wait at all for the first step
    hub.last.drop();
    expect(await waitForProbe()).toBe(0);
    await finishConnect();

    random = 0.999; // upper end: +20 %
    hub.last.drop();
    expect(await waitForProbe()).toBe(2_400);
    await finishConnect();
  });

  it('reconnect_FlappingWithFailingCatchUps_AsksForAtMostOneReloadPerConnection', async () => {
    for (let i = 0; i < 3; i++) {
      hub.last.drop();
      await waitForProbe();
      await flush();
      for (const request of http.match({
        method: 'GET',
        url: `${API}/activity?afterSeq=10&limit=200`,
      })) {
        request.flush({ code: 'internal_error' }, { status: 500, statusText: 'Server Error' });
      }
      await flush();
      expect(realtime.status()).toBe('live');
    }

    // three reconnects, three reloads: the first after the failed catch-up, then one per
    // connection that starts from the head
    expect(hub.last.starts).toBe(4);
    expect(resyncs).toBe(3);
  });
});

describe('jitteredDelay', () => {
  const jitter = (random: number): RealtimeRetryJitter => ({
    ratio: 0.2,
    zeroSpreadMs: 1_000,
    random: () => random,
  });

  it('jitteredDelay_PositiveDelay_StaysWithinTwentyPercent', () => {
    expect(jitteredDelay(10_000, jitter(0))).toBe(8_000);
    expect(jitteredDelay(10_000, jitter(0.5))).toBe(10_000);
    expect(jitteredDelay(10_000, jitter(0.75))).toBe(11_000);
    expect(jitteredDelay(10_000, jitter(1))).toBe(12_000);
  });

  it('jitteredDelay_ZeroDelay_BecomesARandomWaitUpToTheSpread', () => {
    expect(jitteredDelay(0, jitter(0))).toBe(0);
    expect(jitteredDelay(0, jitter(0.25))).toBe(250);
    expect(jitteredDelay(0, jitter(0.999))).toBe(999);
  });

  it('jitteredDelay_NoJitter_KeepsTheDelay', () => {
    expect(jitteredDelay(0, NO_JITTER)).toBe(0);
    expect(jitteredDelay(5_000, NO_JITTER)).toBe(5_000);
  });
});
