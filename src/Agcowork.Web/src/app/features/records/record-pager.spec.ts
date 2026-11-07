import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { expectRequest, recordDto } from '../../core/testing/test-utils';
import { RecordListSource, createRecordPager } from './record-pager';

describe('createRecordPager', () => {
  let http: HttpTestingController;
  const source = signal<RecordListSource | undefined>({ slug: 'demo', collectionId: 'a' });

  beforeEach(() => {
    source.set({ slug: 'demo', collectionId: 'a' });
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('loadMore_SourceSwitchedMeanwhile_DiscardsLateAnswer', async () => {
    const pager = TestBed.runInInjectionContext(() => createRecordPager(() => source()));
    (await expectRequest(http, 'GET', '/api/v1/w/demo/records?collectionId=a&limit=50')).flush({
      items: [recordDto({ id: 'a1' })],
      nextCursor: 'c1',
    });
    await vi.waitFor(() => expect(pager.items()).toHaveLength(1));

    const loading = pager.loadMore();
    const late = await expectRequest(
      http,
      'GET',
      '/api/v1/w/demo/records?collectionId=a&limit=50&cursor=c1',
    );
    source.set({ slug: 'demo', collectionId: 'b' });
    (await expectRequest(http, 'GET', '/api/v1/w/demo/records?collectionId=b&limit=50')).flush({
      items: [recordDto({ id: 'b1' })],
      nextCursor: null,
    });
    await vi.waitFor(() => expect(pager.items().map((r) => r.id)).toEqual(['b1']));
    late.flush({ items: [recordDto({ id: 'a2' })], nextCursor: null });
    await loading;

    expect(pager.items().map((r) => r.id)).toEqual(['b1']);
    expect(pager.hasMore()).toBe(false);
  });
});
