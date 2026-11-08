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
 * first retry after losing a live connection comes at once.
 */
export const REALTIME_RETRY_DELAYS_MS = new InjectionToken<readonly number[]>(
  'REALTIME_RETRY_DELAYS_MS',
  { providedIn: 'root', factory: () => [0, 2_000, 5_000, 10_000, 30_000] },
);

/** URL of the hub of a workspace (outside the REST prefix, see AP-024). */
export function hubUrl(slug: string): string {
  return `/w/${encodeURIComponent(slug)}/hub`;
}
