import { InjectionToken } from '@angular/core';

/**
 * The part of a SignalR `HubConnection` the realtime service uses. A real connection comes from
 * {@link REALTIME_HUB_FACTORY}; tests provide a fake.
 */
export interface RealtimeHubConnection {
  start(): Promise<void>;
  stop(): Promise<void>;
  on(method: string, handler: (...args: never[]) => void): void;
  onclose(callback: (error?: Error) => void): void;
}

/** Creates one (not yet started) connection to the hub at `url`. */
export type RealtimeHubFactory = (url: string) => Promise<RealtimeHubConnection>;

/**
 * Builds SignalR connections. The client library is loaded on first use, so it stays out of the
 * initial bundle. Reconnecting is done by the realtime service, not by SignalR (it has to tell
 * "no access any more" from "network gone", which SignalR's retry policy cannot see).
 */
export const REALTIME_HUB_FACTORY = new InjectionToken<RealtimeHubFactory>('REALTIME_HUB_FACTORY', {
  providedIn: 'root',
  factory: () => async (url: string) => {
    const signalr = await import('@microsoft/signalr');
    return new signalr.HubConnectionBuilder()
      .withUrl(url, { withCredentials: true })
      .configureLogging(signalr.LogLevel.Warning)
      .build();
  },
});

/**
 * Waits between connection attempts after a failure, by attempt (the last value repeats). The
 * first retry after losing a stable connection comes almost at once (see
 * {@link REALTIME_RETRY_JITTER}); every further one waits longer, until a connection holds for
 * {@link REALTIME_STABLE_AFTER_MS}.
 */
export const REALTIME_RETRY_DELAYS_MS = new InjectionToken<readonly number[]>(
  'REALTIME_RETRY_DELAYS_MS',
  { providedIn: 'root', factory: () => [0, 2_000, 5_000, 10_000, 30_000] },
);

/**
 * A connection has to stay up this long before the backoff starts from the first delay again.
 * A server that drops connections right after the handshake (AP-028: delivery failures) thus
 * sees ever longer waits instead of reconnects every second.
 */
export const REALTIME_STABLE_AFTER_MS = new InjectionToken<number>('REALTIME_STABLE_AFTER_MS', {
  providedIn: 'root',
  factory: () => 30_000,
});

/** Random spread of the waits between connection attempts, so clients do not retry in step. */
export interface RealtimeRetryJitter {
  /** Each wait varies by up to this share in both directions (`0.2`: ±20 %). */
  readonly ratio: number;
  /** A wait of 0 becomes a random wait of up to this many milliseconds. */
  readonly zeroSpreadMs: number;
  /** Source of randomness, a number in [0, 1). */
  readonly random: () => number;
}

export const REALTIME_RETRY_JITTER = new InjectionToken<RealtimeRetryJitter>(
  'REALTIME_RETRY_JITTER',
  {
    providedIn: 'root',
    factory: () => ({ ratio: 0.2, zeroSpreadMs: 1_000, random: () => Math.random() }),
  },
);

/** `delay` with jitter applied: within ±`ratio`, or up to `zeroSpreadMs` for a delay of 0. */
export function jitteredDelay(delay: number, jitter: RealtimeRetryJitter): number {
  const random = Math.min(Math.max(jitter.random(), 0), 1);
  if (delay <= 0) {
    return Math.round(random * jitter.zeroSpreadMs);
  }
  return Math.max(0, Math.round(delay * (1 + jitter.ratio * (2 * random - 1))));
}

/** URL of the hub of a workspace (outside the REST prefix, see AP-024). */
export function hubUrl(slug: string): string {
  return `/w/${encodeURIComponent(slug)}/hub`;
}
