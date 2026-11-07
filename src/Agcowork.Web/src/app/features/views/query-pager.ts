import { Signal, computed, inject, linkedSignal, resource, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import type { components } from '../../core/api/generated/schema';
import { describeError } from '../../shared/ui/api-error-text';
import type { RecordDto } from '../records/record-save.service';
import type { ViewQuery } from './view-state';

type QueryResult = components['schemas']['QueryResultOfRecordDto'];

export interface QuerySource {
  readonly slug: string;
  readonly query: ViewQuery;
}

export interface QueryPager {
  readonly items: Signal<readonly RecordDto[]>;
  readonly count: Signal<number | null>;
  readonly isLoading: Signal<boolean>;
  readonly error: Signal<string | null>;
  readonly hasMore: Signal<boolean>;
  readonly loadingMore: Signal<boolean>;
  readonly moreError: Signal<string | null>;
  loadMore(): Promise<void>;
  reload(): void;
  find(id: string): RecordDto | undefined;
  /** Replaces a loaded record (after an inline edit). */
  replace(record: RecordDto): void;
}

/**
 * Cursor pagination over the query endpoints (`POST /views/{id}/query` or
 * `POST /collections/{id}/query`): the first page loads whenever `source` changes, `loadMore()`
 * appends the next one. Loaded records can be replaced in place. Call in an injection context.
 */
export function createQueryPager(source: () => QuerySource | undefined): QueryPager {
  const api = inject(ApiClient);

  const fetchPage = (src: QuerySource, cursor: string | undefined): Promise<QueryResult> => {
    const client = api.workspace(src.slug);
    const { query } = src;
    if (query.kind === 'view') {
      return firstValueFrom(
        client.post('/views/{viewId}/query', {
          path: { viewId: query.viewId },
          body: cursor ? { ...query.body, cursor, includeCount: false } : query.body,
        }),
      );
    }
    return firstValueFrom(
      client.post('/collections/{collectionId}/query', {
        path: { collectionId: query.collectionId },
        body: cursor ? { ...query.body, cursor, includeCount: false } : query.body,
      }),
    );
  };

  const first = resource<QueryResult, QuerySource | undefined>({
    params: source,
    loader: ({ params }) => fetchPage(params, undefined),
  });

  const state = linkedSignal<
    QueryResult | undefined,
    { items: readonly RecordDto[]; cursor: string | null }
  >({
    source: () => (first.hasValue() ? first.value() : undefined),
    computation: (page) => ({ items: page?.items ?? [], cursor: page?.nextCursor ?? null }),
  });
  const loadingMore = signal(false);
  const moreError = signal<string | null>(null);

  return {
    items: computed(() => state().items),
    count: computed(() => {
      const count = first.hasValue() ? first.value().count : null;
      return count === null || count === undefined ? null : Number(count);
    }),
    isLoading: computed(() => first.isLoading()),
    error: computed(() => (first.error() ? describeError(first.error()) : null)),
    hasMore: computed(() => state().cursor !== null),
    loadingMore: loadingMore.asReadonly(),
    moreError: moreError.asReadonly(),
    reload: () => first.reload(),
    find: (id) => state().items.find((r) => r.id === id),
    replace(record) {
      state.update((s) => ({
        ...s,
        items: s.items.map((r) => (r.id === record.id ? record : r)),
      }));
    },
    async loadMore() {
      const cursor = state().cursor;
      const src = source();
      if (!cursor || !src || loadingMore()) {
        return;
      }
      loadingMore.set(true);
      moreError.set(null);
      const key = JSON.stringify(src);
      try {
        const page = await fetchPage(src, cursor);
        if (JSON.stringify(source()) !== key || state().cursor !== cursor) {
          return; // switched to another query (or reloaded) meanwhile
        }
        state.update((s) => ({ items: [...s.items, ...page.items], cursor: page.nextCursor }));
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
