import {
  DestroyRef,
  Injectable,
  InjectionToken,
  computed,
  effect,
  inject,
  linkedSignal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../api/api-client';
import type { components } from '../api/generated/schema';
import { SessionStore } from '../auth/session.store';
import { ActivityEvent, RealtimeService } from '../realtime/realtime.service';
import { WorkspaceContext } from '../workspace/workspace-context.service';

type RecordSummary = components['schemas']['RecordDto'];
type PropertyDefinition = components['schemas']['PropertyDefinitionDto'];

/** What the UI knows about the record behind an id (relation value, chip, filter value). */
export type RecordTitle =
  | { readonly state: 'loading' }
  | {
      readonly state: 'found';
      readonly title: string;
      /** The record, its collection or its space is archived; references stay valid. */
      readonly archived: boolean;
      readonly collectionId: string | null;
      readonly spaceId: string;
    }
  /** Unknown id, or one of another workspace (the server does not say which). */
  | { readonly state: 'missing' }
  /** The lookup failed (network, server) at `at` (ms); the record may well exist. */
  | { readonly state: 'failed'; readonly at: number };

/** Ids asked for within this window go to the server in one lookup (per 200 ids). */
export const RECORD_TITLE_BATCH_MS = new InjectionToken<number>('RECORD_TITLE_BATCH_MS', {
  providedIn: 'root',
  factory: () => 20,
});

/** A failed id is asked for again when it is read this long after the failure. */
export const RECORD_TITLE_RETRY_MS = new InjectionToken<number>('RECORD_TITLE_RETRY_MS', {
  providedIn: 'root',
  factory: () => 30_000,
});

/** Most ids one `POST /records/lookup` takes. */
export const LOOKUP_MAX_IDS = 200;

const LOADING: RecordTitle = { state: 'loading' };
const MISSING: RecordTitle = { state: 'missing' };
/** Ticket of a lookup whose answer must not be applied (its id changed meanwhile). */
const STALE_TICKET = 0;

/** Record events that can change what the cache holds about their target. */
const TITLE_EVENTS = ['record.updated', 'record.archived', 'record.restored'];

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** The cache of one workspace; `epoch` changes whenever it starts over. */
interface Cache {
  readonly slug: string | null;
  readonly epoch: number;
  readonly entries: ReadonlyMap<string, RecordTitle>;
}

/**
 * The relation values of `records` for the relation properties among `definitions`: what a
 * table or board shows as chips, for {@link RecordTitleStore.prefetch}.
 */
export function relationIdsOf(
  records: readonly { readonly properties: unknown }[],
  definitions: readonly PropertyDefinition[],
): Set<string> {
  const keys = definitions.filter((d) => d.type === 'relation').map((d) => d.key);
  const ids = new Set<string>();
  if (keys.length === 0) {
    return ids;
  }
  for (const record of records) {
    const properties = (record.properties ?? {}) as Record<string, unknown>;
    for (const key of keys) {
      const value = properties[key];
      for (const id of Array.isArray(value) ? value : [value]) {
        if (typeof id === 'string' && id !== '') {
          ids.add(id);
        }
      }
    }
  }
  return ids;
}

/** Whether a `record.updated` diff can have changed the title (unknown shapes: assume yes). */
function touchesTitle(diff: unknown): boolean {
  return typeof diff !== 'object' || diff === null || Array.isArray(diff) || 'title' in diff;
}

/**
 * Shared title cache of the active workspace (architecture §2 "Relationen"): turns record ids
 * (relation values) into title, archived state and place. Readers call {@link get} as often as
 * they like, also from templates: unknown ids are collected for {@link RECORD_TITLE_BATCH_MS}
 * and resolved with `POST /records/lookup`, at most {@link LOOKUP_MAX_IDS} per request, each id
 * once. Ids the server does not return are "missing" (unknown or of another workspace).
 *
 * Live: `record.updated` (with a title change), `record.archived` and `record.restored` of a
 * cached id fetch it again (the old value stays shown until then), as do `collection.archived`
 * and `space.archived` for the cached records inside. A resync and a workspace switch empty the
 * cache; what is still shown asks again. Answers that arrive after the cache started over, or
 * after a newer lookup or a change event of the same id, are dropped.
 *
 * A failed lookup marks the ids "failed"; they are asked for again when read
 * {@link RECORD_TITLE_RETRY_MS} later, and at once when the realtime connection is live again.
 * Lists (table, board) call {@link prefetch} with all relation ids of their loaded rows, so rows
 * rendered later by virtual scrolling find their titles already asked for.
 */
@Injectable({ providedIn: 'root' })
export class RecordTitleStore {
  private readonly api = inject(ApiClient);
  private readonly context = inject(WorkspaceContext);
  private readonly session = inject(SessionStore);
  private readonly realtime = inject(RealtimeService);
  private readonly batchMs = inject(RECORD_TITLE_BATCH_MS);
  private readonly retryMs = inject(RECORD_TITLE_RETRY_MS);

  private readonly slug = computed(() =>
    this.session.status() === 'authenticated' ? this.context.slug() : null,
  );

  private epochs = 0;
  private readonly cache = linkedSignal<string | null, Cache>({
    source: () => this.slug(),
    computation: (slug) => ({ slug, epoch: ++this.epochs, entries: new Map() }),
  });

  /** Ids waiting for the next lookup, for the cache of {@link queueEpoch}. */
  private readonly queue = new Set<string>();
  private queueEpoch = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;
  /** Latest lookup per id of the cache of {@link pendingEpoch}; only its answer is applied. */
  private readonly pending = new Map<string, number>();
  private pendingEpoch = 0;
  private tickets = 0;

  constructor() {
    this.realtime.activity.pipe(takeUntilDestroyed()).subscribe((event) => this.onActivity(event));
    this.realtime.resync.pipe(takeUntilDestroyed()).subscribe(() => {
      const cache = this.cache();
      if (cache.slug && this.realtime.slug === cache.slug) {
        this.cache.set({ slug: cache.slug, epoch: ++this.epochs, entries: new Map() });
      }
    });
    let wasLive = untracked(() => this.realtime.status()) === 'live';
    effect(() => {
      const live = this.realtime.status() === 'live';
      if (live && !wasLive) {
        untracked(() => this.retryFailed());
      }
      wasLive = live;
    });
    inject(DestroyRef).onDestroy(() => {
      if (this.timer !== null) {
        clearTimeout(this.timer);
      }
    });
  }

  /**
   * What is known about record `id`. Reads a signal (templates and `computed`s update when the
   * answer arrives) and, when nothing is known yet, asks the server in the next batch; ids that
   * are no record ids at all are "missing" at once.
   */
  get(id: string): RecordTitle {
    const cache = this.cache();
    const entry = cache.entries.get(id);
    if (entry && (entry.state !== 'failed' || Date.now() - entry.at < this.retryMs)) {
      return entry;
    }
    if (!cache.slug) {
      return entry ?? LOADING;
    }
    if (!UUID.test(id)) {
      return MISSING;
    }
    if (!this.pendingOf(cache.epoch).has(id)) {
      this.enqueue(id, cache.epoch);
    }
    return entry ?? LOADING;
  }

  /**
   * Asks for the titles of `ids` in the next batch (unknown ones only), without reading the
   * cache signal: for lists that know all ids they will show before rendering them.
   */
  prefetch(ids: Iterable<string>): void {
    untracked(() => {
      const cache = this.cache();
      if (!cache.slug) {
        return;
      }
      const pending = this.pendingOf(cache.epoch);
      for (const id of ids) {
        if (!cache.entries.has(id) && !pending.has(id) && UUID.test(id)) {
          this.enqueue(id, cache.epoch);
        }
      }
    });
  }

  /** Display title of `id`: the title, else "Nicht verfügbar", else the start of the id. */
  label(id: string): string {
    const entry = this.get(id);
    switch (entry.state) {
      case 'found':
        return entry.title || 'Ohne Titel';
      case 'missing':
        return 'Nicht verfügbar';
      default:
        return id.slice(0, 8);
    }
  }

  /**
   * Records the caller just got from the server (e.g. search results of the relation picker):
   * their titles are known without a lookup. Only fills ids the cache does not know yet.
   */
  prime(records: readonly RecordSummary[]): void {
    const cache = this.cache();
    if (!cache.slug) {
      return;
    }
    const fresh = records.filter((r) => !cache.entries.has(r.id));
    if (fresh.length === 0) {
      return;
    }
    const entries = new Map(cache.entries);
    for (const record of fresh) {
      entries.set(record.id, {
        state: 'found',
        title: record.title,
        archived: !!record.archivedAt,
        collectionId: record.collectionId,
        spaceId: record.spaceId,
      });
    }
    this.cache.set({ ...cache, entries });
  }

  private onActivity(event: ActivityEvent): void {
    const cache = this.cache();
    if (!cache.slug || this.realtime.slug !== cache.slug) {
      return;
    }
    if (event.targetType === 'record' && TITLE_EVENTS.includes(event.type)) {
      const id = event.targetId;
      const pending = this.pendingOf(cache.epoch);
      if (
        (cache.entries.has(id) || pending.has(id)) &&
        (event.type !== 'record.updated' || touchesTitle(event.diff))
      ) {
        if (pending.has(id)) {
          pending.set(id, STALE_TICKET); // its answer may predate the change
        }
        this.enqueue(id, cache.epoch);
      }
      return;
    }
    const container =
      event.type === 'collection.archived'
        ? (entry: RecordTitle) => entry.state === 'found' && entry.collectionId === event.targetId
        : event.type === 'space.archived'
          ? (entry: RecordTitle) => entry.state === 'found' && entry.spaceId === event.targetId
          : null;
    if (container) {
      for (const [id, entry] of cache.entries) {
        if (container(entry)) {
          this.enqueue(id, cache.epoch);
        }
      }
    }
  }

  /** Ids with a lookup on its way for the cache of `epoch`; forgets those of older caches. */
  private pendingOf(epoch: number): Map<string, number> {
    if (this.pendingEpoch !== epoch) {
      this.pending.clear();
      this.pendingEpoch = epoch;
    }
    return this.pending;
  }

  /** The realtime connection is back: ask again for what failed. */
  private retryFailed(): void {
    const cache = this.cache();
    if (!cache.slug) {
      return;
    }
    const pending = this.pendingOf(cache.epoch);
    for (const [id, entry] of cache.entries) {
      if (entry.state === 'failed' && !pending.has(id)) {
        this.enqueue(id, cache.epoch);
      }
    }
  }

  private enqueue(id: string, epoch: number): void {
    if (this.queueEpoch !== epoch) {
      this.queue.clear();
      this.queueEpoch = epoch;
    }
    this.queue.add(id);
    this.timer ??= setTimeout(() => this.flush(), this.batchMs);
  }

  private flush(): void {
    this.timer = null;
    const cache = this.cache();
    const ids = [...this.queue];
    this.queue.clear();
    if (!cache.slug || this.queueEpoch !== cache.epoch || ids.length === 0) {
      return; // started over meanwhile: readers ask again
    }
    const pending = this.pendingOf(cache.epoch);
    for (let start = 0; start < ids.length; start += LOOKUP_MAX_IDS) {
      const chunk = ids.slice(start, start + LOOKUP_MAX_IDS);
      const ticket = ++this.tickets;
      for (const id of chunk) {
        pending.set(id, ticket);
      }
      void this.lookup(cache.slug, cache.epoch, ticket, chunk);
    }
  }

  /** The answer of one lookup as cache entries; `null` when it failed. */
  private async fetch(slug: string, ids: string[]): Promise<Map<string, RecordTitle> | null> {
    try {
      const answer = await firstValueFrom(
        this.api.workspace(slug).post('/records/lookup', { body: { ids } }),
      );
      return new Map(
        answer.items.map((item) => [
          item.id,
          {
            state: 'found',
            title: item.title,
            archived: item.archived,
            collectionId: item.collectionId,
            spaceId: item.spaceId,
          } satisfies RecordTitle,
        ]),
      );
    } catch {
      return null;
    }
  }

  private async lookup(slug: string, epoch: number, ticket: number, ids: string[]): Promise<void> {
    const found = await this.fetch(slug, ids);
    const cache = this.cache();
    if (cache.epoch !== epoch) {
      return;
    }
    const pending = this.pendingOf(epoch);
    const entries = new Map(cache.entries);
    let changed = false;
    for (const id of ids) {
      if (pending.get(id) !== ticket) {
        continue; // a newer lookup of this id is on its way, or the id changed meanwhile
      }
      pending.delete(id);
      const previous = entries.get(id);
      // a failed refresh keeps what is shown (title or "not available")
      const next = found
        ? (found.get(id) ?? MISSING)
        : previous?.state === 'found' || previous?.state === 'missing'
          ? previous
          : ({ state: 'failed', at: Date.now() } satisfies RecordTitle);
      entries.set(id, next);
      changed = true;
    }
    if (changed) {
      this.cache.set({ ...cache, entries });
    }
  }
}
