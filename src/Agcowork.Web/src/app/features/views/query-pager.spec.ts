import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { apiErrorInterceptor } from '../../core/api/api-error.interceptor';
import {
  API,
  COLLECTION_ID,
  VIEW_ID,
  expectRequest,
  queryPage,
  recordDto,
} from '../../core/testing/test-utils';
import { QueryPager, QuerySource, createQueryPager } from './query-pager';

const VIEW_SOURCE: QuerySource = {
  slug: 'demo',
  query: { kind: 'view', viewId: VIEW_ID, body: { limit: 2, includeCount: true } },
};
const COLLECTION_SOURCE: QuerySource = {
  slug: 'demo',
  query: {
    kind: 'collection',
    collectionId: COLLECTION_ID,
    body: {
      sorts: [{ prop: 'due', dir: 'asc' }],
      limit: 2,
      includeArchived: false,
      includeCount: true,
    },
  },
};
const VIEW_URL = `${API}/views/${VIEW_ID}/query`;
const COLLECTION_URL = `${API}/collections/${COLLECTION_ID}/query`;

describe('createQueryPager', () => {
  let http: HttpTestingController;
  let pager: QueryPager;
  const source = signal<QuerySource | undefined>(VIEW_SOURCE);

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    source.set(VIEW_SOURCE);
    pager = TestBed.runInInjectionContext(() => createQueryPager(() => source()));
  });

  afterEach(() => http.verify());

  const ids = () => pager.items().map((r) => r.id);
  const flushTicks = () => new Promise((resolve) => setTimeout(resolve));

  it('loadMore_NextCursor_AppendsPageAndAdoptsItsCursor', async () => {
    const first = await expectRequest(http, 'POST', VIEW_URL);
    expect(first.request.body).toEqual({ limit: 2, includeCount: true });
    first.flush(queryPage([recordDto({ id: 'a' }), recordDto({ id: 'b' })], 'c1', 5));
    await flushTicks();
    expect(ids()).toEqual(['a', 'b']);
    expect(pager.count()).toBe(5);
    expect(pager.hasMore()).toBe(true);

    const more = pager.loadMore();
    const second = http.expectOne(VIEW_URL);
    expect(second.request.body).toEqual({ limit: 2, includeCount: false, cursor: 'c1' });
    second.flush(queryPage([recordDto({ id: 'c' }), recordDto({ id: 'd' })], 'c2'));
    await more;
    expect(ids()).toEqual(['a', 'b', 'c', 'd']);

    const last = pager.loadMore();
    const third = http.expectOne(VIEW_URL);
    expect(third.request.body.cursor).toBe('c2');
    third.flush(queryPage([recordDto({ id: 'e' })], null));
    await last;
    expect(ids()).toEqual(['a', 'b', 'c', 'd', 'e']);
    expect(pager.hasMore()).toBe(false);

    await pager.loadMore(); // nothing left: no request
  });

  it('loadMore_QueryChangedMeanwhile_DropsTheStalePage', async () => {
    (await expectRequest(http, 'POST', VIEW_URL)).flush(queryPage([recordDto({ id: 'a' })], 'c1'));
    await flushTicks();

    const more = pager.loadMore();
    const stale = http.expectOne(VIEW_URL);
    source.set(COLLECTION_SOURCE);
    const fresh = await expectRequest(http, 'POST', COLLECTION_URL);
    expect(fresh.request.body).toEqual({
      sorts: [{ prop: 'due', dir: 'asc' }],
      limit: 2,
      includeArchived: false,
      includeCount: true,
    });
    fresh.flush(queryPage([recordDto({ id: 'x' })], null));
    await flushTicks();
    stale.flush(queryPage([recordDto({ id: 'old' })], 'c9'));
    await more;

    expect(ids()).toEqual(['x']);
    expect(pager.hasMore()).toBe(false);
  });

  it('loadMore_Fails_KeepsRowsAndReportsError', async () => {
    (await expectRequest(http, 'POST', VIEW_URL)).flush(queryPage([recordDto({ id: 'a' })], 'c1'));
    await flushTicks();

    const more = pager.loadMore();
    http
      .expectOne(VIEW_URL)
      .flush(
        { code: 'invalid_cursor', status: 400, detail: 'Cursor passt nicht.' },
        { status: 400, statusText: 'Bad Request' },
      );
    await more;

    expect(ids()).toEqual(['a']);
    expect(pager.moreError()).toBe('Cursor passt nicht.');
    expect(pager.hasMore()).toBe(true);
  });

  it('replace_LoadedRecord_UpdatesInPlace', async () => {
    (await expectRequest(http, 'POST', VIEW_URL)).flush(
      queryPage([recordDto({ id: 'a', title: 'Alt' }), recordDto({ id: 'b' })]),
    );
    await flushTicks();

    pager.replace(recordDto({ id: 'a', title: 'Neu' }));

    expect(pager.items().map((r) => r.title)).toEqual(['Neu', 'Login bauen']);
    expect(pager.find('a')?.title).toBe('Neu');
  });

  it('firstPage_Error_IsReported', async () => {
    (await expectRequest(http, 'POST', VIEW_URL)).flush(
      {
        code: 'validation_failed',
        status: 400,
        errors: [
          { field: 'view.filter.and[0].op', code: 'operator_not_applicable', message: 'nope' },
        ],
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await flushTicks();

    expect(pager.items()).toEqual([]);
    expect(pager.error()).toContain('view.filter.and[0].op');
  });
});
