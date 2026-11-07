import { Signal, computed, inject, linkedSignal, resource, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import type { components } from '../../core/api/generated/schema';
import { describeError } from '../../shared/ui/api-error-text';
import type { RecordDto } from './record-save.service';

type RecordPage = components['schemas']['PageOfRecordDto'];

export interface RecordListSource {
  readonly slug: string;
  /** Exactly one of both: a collection's records or the free pages of a space. */
  readonly collectionId?: string;
  readonly spaceId?: string;
}

export interface RecordPager {
  readonly items: Signal<readonly RecordDto[]>;
  readonly isLoading: Signal<boolean>;
  readonly error: Signal<string | null>;
  readonly hasMore: Signal<boolean>;
  readonly loadingMore: Signal<boolean>;
  readonly moreError: Signal<string | null>;
  loadMore(): Promise<void>;
  reload(): void;
}

const PAGE_SIZE = 50;

/**
 * Cursor-paginated record list (`GET /records`): the first page loads when `source` yields a
 * value, `loadMore()` appends the next one. Starts over when `source` changes. Call it in an
 * injection context (field initializer).
 */
export function createRecordPager(source: () => RecordListSource | undefined): RecordPager {
  const api = inject(ApiClient);

  const fetchPage = (src: RecordListSource, cursor: string | undefined): Promise<RecordPage> =>
    firstValueFrom(
      api.workspace(src.slug).get('/records', {
        query: {
          collectionId: src.collectionId,
          spaceId: src.spaceId,
          limit: PAGE_SIZE,
          cursor,
        },
      }),
    );

  const first = resource<RecordPage, RecordListSource | undefined>({
    params: source,
    loader: ({ params }) => fetchPage(params, undefined),
  });

  /** Pages after the first; reset whenever the first page (re)loads. */
  const more = linkedSignal<unknown, { items: RecordDto[]; cursor: string | null | undefined }>({
    source: () => first.value(),
    computation: () => ({ items: [], cursor: undefined }),
  });
  const loadingMore = signal(false);
  const moreError = signal<string | null>(null);

  const cursor = computed(() => {
    const extra = more();
    if (extra.cursor !== undefined) {
      return extra.cursor;
    }
    return first.hasValue() ? first.value().nextCursor : null;
  });

  return {
    items: computed(() => [...(first.hasValue() ? first.value().items : []), ...more().items]),
    isLoading: computed(() => first.isLoading()),
    error: computed(() => (first.error() ? describeError(first.error()) : null)),
    hasMore: computed(() => cursor() !== null),
    loadingMore: loadingMore.asReadonly(),
    moreError: moreError.asReadonly(),
    reload: () => first.reload(),
    async loadMore() {
      const next = cursor();
      const src = source();
      if (!next || !src || loadingMore()) {
        return;
      }
      loadingMore.set(true);
      moreError.set(null);
      const key = JSON.stringify(src);
      try {
        const page = await fetchPage(src, next);
        if (JSON.stringify(source()) !== key) {
          return; // the list was switched to another source meanwhile
        }
        more.update((m) => ({ items: [...m.items, ...page.items], cursor: page.nextCursor }));
      } catch (error) {
        if (JSON.stringify(source()) === key) {
          moreError.set(describeError(error));
        }
      } finally {
        loadingMore.set(false);
      }
    },
  };
}
