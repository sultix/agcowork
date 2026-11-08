import { DestroyRef, InjectionToken, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { CollectionStore } from './collection.store';

/** Quiet time after the last change before the view reloads (changes in between are bundled). */
export const LIVE_REFRESH_DELAY_MS = new InjectionToken<number>('LIVE_REFRESH_DELAY_MS', {
  providedIn: 'root',
  factory: () => 500,
});

/** Under a steady stream of changes the view still reloads at least this often. */
export const LIVE_REFRESH_MAX_WAIT_MS = new InjectionToken<number>('LIVE_REFRESH_MAX_WAIT_MS', {
  providedIn: 'root',
  factory: () => 2000,
});

export interface LiveRefreshOptions {
  /** Collection whose records the view shows; events of other collections are ignored. */
  readonly collectionId: () => string | null | undefined;
  /**
   * True while a reload must wait (a cell editor is open, a card is dragged, a save runs, a
   * conflict is open). Must be signal-based: the reload runs once it turns false.
   */
  readonly blocked: () => boolean;
  /**
   * Grows with every edit or drag that starts. A reload whose rows were fetched before one
   * started is outdated even if the edit has ended by the time the rows arrive.
   */
  readonly generation: () => number;
  /** Reloads the rows; must not replace them when `canApply()` is false, and say so. */
  readonly refresh: (canApply: () => boolean) => Promise<boolean>;
  /**
   * Also hold back schema and metadata changes of the surrounding {@link CollectionStore} while
   * {@link blocked} holds (for views whose editors and cards are built from the schema).
   */
  readonly holdSchema?: boolean;
}

/**
 * Keeps a view (table, board) current while others change its records: record events of the
 * collection, schema changes of it (they can rewrite stored values without record events), and
 * resyncs after a lost connection, are bundled into one silent reload
 * ({@link LIVE_REFRESH_DELAY_MS} after the last change, at most {@link LIVE_REFRESH_MAX_WAIT_MS}
 * after the first). While {@link LiveRefreshOptions.blocked} holds, the reload waits and runs
 * once afterwards; rows fetched before an edit began are thrown away and fetched again. So an
 * open editor or a running save never gets server data under it. With
 * {@link LiveRefreshOptions.holdSchema} the same lock holds back schema changes of the
 * {@link CollectionStore} of the collection page. Call in an injection context (component
 * constructor).
 */
export function liveRefresh(options: LiveRefreshOptions): void {
  const realtime = inject(RealtimeService);
  const context = inject(WorkspaceContext);
  const destroyRef = inject(DestroyRef);
  const delay = inject(LIVE_REFRESH_DELAY_MS);
  const maxWait = inject(LIVE_REFRESH_MAX_WAIT_MS);
  const store = options.holdSchema ? inject(CollectionStore, { optional: true }) : null;
  if (store) {
    destroyRef.onDestroy(store.holdWhile(options.blocked));
  }

  const pending = signal(false);
  let timer: ReturnType<typeof setTimeout> | null = null;
  let firstAt: number | null = null;
  let running = false;

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
        pending.set(true);
      },
      Math.max(0, Math.min(delay, firstAt + maxWait - now)),
    );
  };

  realtime.activity.pipe(takeUntilDestroyed(destroyRef)).subscribe((event) => {
    const collectionId = options.collectionId();
    if (
      realtime.slug === context.slug() &&
      (event.targetType === 'record' || event.type === 'collection.schema_changed') &&
      !!collectionId &&
      event.collectionId === collectionId
    ) {
      schedule();
    }
  });
  realtime.resync.pipe(takeUntilDestroyed(destroyRef)).subscribe(() => schedule());

  const run = async (): Promise<void> => {
    if (running) {
      return;
    }
    running = true;
    pending.set(false);
    const generation = untracked(options.generation);
    const current = () => !options.blocked() && options.generation() === generation;
    try {
      const applied = await options.refresh(current);
      if (!applied && !untracked(current)) {
        pending.set(true); // an edit started while loading: once more after it
      }
    } finally {
      running = false;
      if (untracked(pending) && !untracked(options.blocked)) {
        void run();
      }
    }
  };

  effect(() => {
    if (pending() && !options.blocked()) {
      untracked(() => void run());
    }
  });

  destroyRef.onDestroy(() => {
    if (timer !== null) {
      clearTimeout(timer);
    }
  });
}
