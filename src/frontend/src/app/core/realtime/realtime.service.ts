import {
  EffectRef,
  Injectable,
  Injector,
  Signal,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { Observable, Subject, firstValueFrom } from 'rxjs';
import { ApiClient } from '../api/api-client';
import { ApiError } from '../api/api-error';
import type { components } from '../api/generated/schema';
import { SessionStore } from '../auth/session.store';
import { WorkspaceContext } from '../workspace/workspace-context.service';
import {
  REALTIME_HUB_FACTORY,
  REALTIME_RETRY_DELAYS_MS,
  RealtimeHubConnection,
  hubUrl,
} from './realtime-hub';

export type ActivityEvent = components['schemas']['ActivityEventDto'];

/**
 * `idle`: no workspace open (or not started). `connecting`: first connection of a workspace.
 * `live`: connected, events arrive. `reconnecting`: connection lost or not yet possible, retrying
 * with backoff. `offline`: given up (no access any more, signed out); {@link RealtimeService.retry}
 * tries again.
 */
export type RealtimeStatus = 'idle' | 'connecting' | 'live' | 'reconnecting' | 'offline';

/** Name of the only server-to-client message of the hub. */
export const ACTIVITY_MESSAGE = 'activity';
/** Page size when catching up after a reconnect (the API's maximum). */
export const CATCH_UP_PAGE_SIZE = 200;
/** More pages than this to catch up: consumers reload instead (see {@link RealtimeService.resync}). */
export const CATCH_UP_MAX_PAGES = 5;
/** Remembered `seq`s per workspace for de-duplication; the oldest are forgotten first. */
const SEEN_LIMIT = 5_000;

/** Statuses after which the workspace is not reachable for this session (no retry). */
const ACCESS_STATUSES = [401, 403, 404];

/** The connection to one workspace's hub, from opening until it is closed. */
interface Link {
  readonly slug: string;
  closed: boolean;
  hub: RealtimeHubConnection | null;
  /** Where forward reading continues (`afterSeq`); `null` when unknown. */
  position: number | null;
  /** Live events do not move the position while a catch-up reads pages. */
  catchingUp: boolean;
  /** A connection attempt is running (probe, hub start). */
  attempting: boolean;
  /** Counts catch-ups; only the latest may move the position (an older one can still run). */
  catchUps: number;
  /**
   * The position was lost (failed catch-up) and nothing arrived since: the next connect starts
   * from the head and asks consumers to reload instead of catching up.
   */
  lost: boolean;
  readonly seen: Set<number>;
  attempt: number;
  timer: ReturnType<typeof setTimeout> | null;
}

/**
 * Live activity of the open workspace over the SignalR hub `/w/{slug}/hub` (AP-024): one
 * connection per tab, opened when a workspace is open and the session is signed in, closed on a
 * workspace switch or when leaving. Lost connections are re-established with backoff; after each
 * (re)connect the events missed meanwhile are read forward (`GET /activity?afterSeq=`) and
 * delivered like live ones, so consumers only react to {@link activity}. When that is not
 * possible, {@link resync} tells them to reload. A workspace that answers 401/403/404 (rights
 * revoked, signed out) is not retried: status `offline`.
 */
@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private readonly api = inject(ApiClient);
  private readonly context = inject(WorkspaceContext);
  private readonly session = inject(SessionStore);
  private readonly createHub = inject(REALTIME_HUB_FACTORY);
  private readonly delays = inject(REALTIME_RETRY_DELAYS_MS);
  private readonly injector = inject(Injector);

  private readonly statusSignal = signal<RealtimeStatus>('idle');
  private readonly activitySubject = new Subject<ActivityEvent>();
  private readonly resyncSubject = new Subject<void>();
  private watcher: EffectRef | null = null;
  private link: Link | null = null;

  readonly status: Signal<RealtimeStatus> = this.statusSignal.asReadonly();

  /** Events of the open workspace, each `seq` once: live, or read after a reconnect. */
  readonly activity: Observable<ActivityEvent> = this.activitySubject.asObservable();

  /** "Events may have been missed": reload what is shown. */
  readonly resync: Observable<void> = this.resyncSubject.asObservable();

  /** Slug of the workspace the connection belongs to (`null` while idle). */
  get slug(): string | null {
    return this.link?.slug ?? null;
  }

  /** Follows the open workspace from now on (idempotent). Called by the workspace shell. */
  start(): void {
    if (this.watcher) {
      return;
    }
    this.watcher = effect(
      () => {
        const slug = this.session.status() === 'authenticated' ? this.context.slug() : null;
        untracked(() => this.follow(slug));
      },
      { injector: this.injector },
    );
  }

  /** Closes the connection and stops following the workspace. */
  stop(): void {
    this.watcher?.destroy();
    this.watcher = null;
    this.follow(null);
  }

  /**
   * Tries again after `offline`, or right away while waiting for the next attempt. Keeps the
   * connection's read position, so what was missed meanwhile is still caught up.
   */
  retry(): void {
    const link = this.link;
    if (!link || link.attempting || this.status() === 'live') {
      return;
    }
    if (link.timer !== null) {
      clearTimeout(link.timer);
      link.timer = null;
    }
    link.attempt = 0;
    this.statusSignal.set('reconnecting');
    void this.connect(link);
  }

  private follow(slug: string | null): void {
    if (this.link?.slug === slug && slug !== null) {
      return;
    }
    if (this.link) {
      this.close(this.link);
    }
    if (slug) {
      this.open(slug);
    } else {
      this.statusSignal.set('idle');
    }
  }

  private open(slug: string): void {
    const link: Link = {
      slug,
      closed: false,
      hub: null,
      position: null,
      catchingUp: false,
      attempting: false,
      catchUps: 0,
      lost: false,
      seen: new Set(),
      attempt: 0,
      timer: null,
    };
    this.link = link;
    this.statusSignal.set('connecting');
    void this.connect(link);
  }

  private close(link: Link): void {
    link.closed = true;
    if (link.timer !== null) {
      clearTimeout(link.timer);
      link.timer = null;
    }
    const hub = link.hub;
    link.hub = null;
    hub?.stop().catch(() => undefined);
    if (this.link === link) {
      this.link = null;
      this.statusSignal.set('idle');
    }
  }

  /**
   * One connection attempt: checks access (and learns the log head) over REST, starts the hub,
   * then catches up. Failures retry with backoff; no access ends in `offline`. Only probe and
   * hub start count as "attempting": a connection lost during the catch-up must be able to start
   * the next attempt at once.
   */
  private async connect(link: Link): Promise<void> {
    if (link.attempting) {
      return;
    }
    link.attempting = true;
    let connected: boolean;
    try {
      connected = await this.attempt(link);
    } finally {
      link.attempting = false;
    }
    if (connected) {
      await this.catchUp(link);
    }
  }

  /** Probe and hub start; `true` when the hub is connected and the catch-up is due. */
  private async attempt(link: Link): Promise<boolean> {
    try {
      const head = await firstValueFrom(
        this.api.workspace(link.slug).get('/activity', { query: { limit: 1 } }),
      );
      if (link.closed) {
        return false;
      }
      if (link.position === null) {
        link.position = Number(head.lastSeq);
      }
    } catch (error) {
      if (!link.closed) {
        this.failed(link, error);
      }
      return false;
    }

    try {
      if (!link.hub) {
        const hub = await this.createHub(hubUrl(link.slug));
        if (link.closed) {
          return false;
        }
        hub.on(ACTIVITY_MESSAGE, (event: ActivityEvent) => this.receive(link, event));
        hub.onclose(() => this.lostConnection(link));
        link.hub = hub;
      }
      // Events can arrive with the handshake, before `start()` resolves: they must not move the
      // position past what the catch-up below still has to read. A catch-up of the previous
      // connection that is still running loses the position to the one that follows.
      link.catchUps++;
      link.catchingUp = true;
      await link.hub.start();
    } catch (error) {
      link.catchingUp = false;
      if (!link.closed) {
        this.failed(link, error);
      }
      return false;
    }
    if (link.closed) {
      link.catchingUp = false;
      return false;
    }

    link.attempt = 0;
    this.statusSignal.set('live');
    if (link.lost) {
      link.catchingUp = false;
      link.lost = false;
      this.resyncSubject.next();
      return false;
    }
    return true;
  }

  private failed(link: Link, error: unknown): void {
    if (error instanceof ApiError && ACCESS_STATUSES.includes(error.status)) {
      this.statusSignal.set('offline');
      return;
    }
    this.scheduleRetry(link);
  }

  private lostConnection(link: Link): void {
    if (link.closed || this.link !== link) {
      return;
    }
    this.scheduleRetry(link);
  }

  private scheduleRetry(link: Link): void {
    this.statusSignal.set('reconnecting');
    if (link.timer !== null) {
      return; // already waiting for the next attempt
    }
    const delays = this.delays.length > 0 ? this.delays : [0];
    const delay = delays[Math.min(link.attempt, delays.length - 1)];
    link.attempt++;
    link.timer = setTimeout(() => {
      link.timer = null;
      if (!link.closed) {
        void this.connect(link);
      }
    }, delay);
  }

  /** Reads forward from the last position and delivers what was missed. */
  private async catchUp(link: Link): Promise<void> {
    const run = ++link.catchUps;
    const latest = () => !link.closed && link.catchUps === run;
    if (link.position === null) {
      link.catchingUp = false;
      this.resyncSubject.next();
      return;
    }
    link.catchingUp = true;
    let after = link.position;
    try {
      for (let page = 0; ; page++) {
        if (page >= CATCH_UP_MAX_PAGES) {
          // Too much to replay event by event: consumers reload, reading continues from here.
          if (latest()) {
            link.position = after;
            this.resyncSubject.next();
          }
          return;
        }
        const result = await firstValueFrom(
          this.api
            .workspace(link.slug)
            .get('/activity', { query: { afterSeq: after, limit: CATCH_UP_PAGE_SIZE } }),
        );
        if (link.closed) {
          return;
        }
        for (const event of result.items) {
          this.deliver(link, event);
        }
        after = Number(result.lastSeq);
        if (result.items.length < CATCH_UP_PAGE_SIZE) {
          break;
        }
      }
      if (latest()) {
        link.position = after;
      }
    } catch {
      if (latest()) {
        // The position may be unusable; the next connect starts from the head.
        link.position = null;
        link.lost = true;
        this.resyncSubject.next();
      }
    } finally {
      if (latest()) {
        link.catchingUp = false;
      }
    }
  }

  private receive(link: Link, event: ActivityEvent): void {
    if (link.closed || this.link !== link) {
      return;
    }
    // The hub delivers in commit order, like forward reading: the last received event is a
    // valid `afterSeq`. While a catch-up reads pages, it owns the position.
    if (this.deliver(link, event) && !link.catchingUp) {
      link.position = Number(event.seq);
      link.lost = false;
    }
  }

  /** Emits an event unless its `seq` was seen already; `false` for a duplicate. */
  private deliver(link: Link, event: ActivityEvent): boolean {
    const seq = Number(event.seq);
    if (link.seen.has(seq)) {
      return false;
    }
    link.seen.add(seq);
    if (link.seen.size > SEEN_LIMIT) {
      link.seen.delete(link.seen.values().next().value as number);
    }
    this.activitySubject.next(event);
    return true;
  }
}
