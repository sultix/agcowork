import { DestroyRef, InjectionToken, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivityEvent, RealtimeService } from './realtime.service';

/** Quiet time after the last matching event before the data reloads (events in between bundle). */
export const LIVE_RELOAD_DELAY_MS = new InjectionToken<number>('LIVE_RELOAD_DELAY_MS', {
  providedIn: 'root',
  factory: () => 500,
});

/** Under a steady stream of matching events the data still reloads at least this often. */
export const LIVE_RELOAD_MAX_WAIT_MS = new InjectionToken<number>('LIVE_RELOAD_MAX_WAIT_MS', {
  providedIn: 'root',
  factory: () => 2000,
});

export interface LiveReloadOptions {
  /**
   * Slug of the workspace the data belongs to (`null` while there is none). Events count only
   * while the realtime connection belongs to the same workspace.
   */
  readonly slug: () => string | null | undefined;
  /** Whether an event of that workspace concerns the data. */
  readonly matches: (event: ActivityEvent) => boolean;
  /** Fetches the data again; called only while {@link slug} is set. */
  readonly reload: () => void;
}

/**
 * Reloads data that others can change (members, the views or the description of a collection)
 * when matching activity arrives, and after a resync (events may have been missed). Events are
 * bundled: one reload {@link LIVE_RELOAD_DELAY_MS} after the last one, at most
 * {@link LIVE_RELOAD_MAX_WAIT_MS} after the first, so a burst of changes costs one request.
 * For data that nobody edits in place; views with editors use `liveRefresh` (features/views),
 * which also waits for the editor. Call in an injection context.
 */
export function liveReload(options: LiveReloadOptions): void {
  const realtime = inject(RealtimeService);
  const destroyRef = inject(DestroyRef);
  const delay = inject(LIVE_RELOAD_DELAY_MS);
  const maxWait = inject(LIVE_RELOAD_MAX_WAIT_MS);

  let timer: ReturnType<typeof setTimeout> | null = null;
  let firstAt: number | null = null;

  const schedule = () => {
    const now = Date.now();
    firstAt ??= now;
    if (timer !== null) {
      clearTimeout(timer);
    }
    timer = setTimeout(
      () => {
        timer = null;
        firstAt = null;
        if (options.slug()) {
          options.reload();
        }
      },
      Math.max(0, Math.min(delay, firstAt + maxWait - now)),
    );
  };

  realtime.activity.pipe(takeUntilDestroyed(destroyRef)).subscribe((event) => {
    const slug = options.slug();
    if (!!slug && realtime.slug === slug && options.matches(event)) {
      schedule();
    }
  });
  realtime.resync.pipe(takeUntilDestroyed(destroyRef)).subscribe(() => {
    const slug = options.slug();
    if (!!slug && realtime.slug === slug) {
      schedule();
    }
  });
  destroyRef.onDestroy(() => {
    if (timer !== null) {
      clearTimeout(timer);
    }
  });
}
