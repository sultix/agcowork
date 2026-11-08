import { Provider, signal } from '@angular/core';
import { Subject } from 'rxjs';
import { ActivityEvent, RealtimeService, RealtimeStatus } from '../realtime/realtime.service';
import {
  REALTIME_HUB_FACTORY,
  REALTIME_RETRY_DELAYS_MS,
  RealtimeHubConnection,
} from '../realtime/realtime-hub';

type Handler = (...args: never[]) => void;

/** Stand-in for a SignalR `HubConnection`: records start/stop, lets a test push messages. */
export class FakeHubConnection implements RealtimeHubConnection {
  readonly handlers = new Map<string, Handler[]>();
  private readonly closeCallbacks: ((error?: Error) => void)[] = [];
  starts = 0;
  stops = 0;
  state: 'disconnected' | 'connected' = 'disconnected';
  /** Rejects the next `start()` with this error (once). */
  failNextStart: Error | null = null;
  /** Runs inside the next successful `start()`, before it resolves (messages with the handshake). */
  duringNextStart: (() => void) | null = null;

  constructor(readonly url: string) {}

  start(): Promise<void> {
    this.starts++;
    if (this.failNextStart) {
      const error = this.failNextStart;
      this.failNextStart = null;
      return Promise.reject(error);
    }
    this.state = 'connected';
    const during = this.duringNextStart;
    this.duringNextStart = null;
    during?.();
    return Promise.resolve();
  }

  stop(): Promise<void> {
    this.stops++;
    this.state = 'disconnected';
    return Promise.resolve();
  }

  on(method: string, handler: Handler): void {
    this.handlers.set(method, [...(this.handlers.get(method) ?? []), handler]);
  }

  onclose(callback: (error?: Error) => void): void {
    this.closeCallbacks.push(callback);
  }

  /** The server sends `method` with `args`. */
  emit(method: string, ...args: unknown[]): void {
    for (const handler of this.handlers.get(method) ?? []) {
      (handler as (...a: unknown[]) => void)(...args);
    }
  }

  /** The connection drops (network, server restart, rights revoked). */
  drop(error: Error = new Error('connection lost')): void {
    this.state = 'disconnected';
    for (const callback of this.closeCallbacks) {
      callback(error);
    }
  }
}

/** Collects the fake connections the realtime service creates. */
export class FakeHub {
  readonly connections: FakeHubConnection[] = [];

  /** The most recently created connection. */
  get last(): FakeHubConnection {
    const last = this.connections.at(-1);
    if (!last) {
      throw new Error('No hub connection was created.');
    }
    return last;
  }

  /** Connections that are started and not stopped. */
  get open(): FakeHubConnection[] {
    return this.connections.filter((c) => c.state === 'connected');
  }
}

/** Providers that replace SignalR with {@link FakeHub}; retries happen without waiting. */
export function provideFakeHub(hub = new FakeHub(), delays: readonly number[] = [0]): Provider[] {
  return [
    { provide: FakeHub, useValue: hub },
    {
      provide: REALTIME_HUB_FACTORY,
      useValue: (url: string) => {
        const connection = new FakeHubConnection(url);
        hub.connections.push(connection);
        return Promise.resolve(connection);
      },
    },
    { provide: REALTIME_RETRY_DELAYS_MS, useValue: delays },
  ];
}

/**
 * Stand-in for `RealtimeService` in feature tests: a test pushes events (`emit`) and resyncs and
 * sets the status; start/stop/retry are only counted.
 */
export class FakeRealtime {
  readonly status = signal<RealtimeStatus>('live');
  readonly activitySubject = new Subject<ActivityEvent>();
  readonly resyncSubject = new Subject<void>();
  readonly activity = this.activitySubject.asObservable();
  readonly resync = this.resyncSubject.asObservable();
  slug: string | null = 'demo';
  starts = 0;
  stops = 0;
  retries = 0;

  start(): void {
    this.starts++;
  }

  stop(): void {
    this.stops++;
  }

  retry(): void {
    this.retries++;
  }

  /** A live event of the open workspace. */
  emit(event: ActivityEvent): void {
    this.activitySubject.next(event);
  }

  /** "Events may have been missed". */
  emitResync(): void {
    this.resyncSubject.next();
  }
}

/** Replaces `RealtimeService` with {@link FakeRealtime}. */
export function provideFakeRealtime(realtime = new FakeRealtime()): Provider[] {
  return [
    { provide: FakeRealtime, useValue: realtime },
    { provide: RealtimeService, useValue: realtime },
  ];
}
