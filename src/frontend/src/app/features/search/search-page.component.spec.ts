import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import {
  API,
  COLLECTION_ID,
  RECORD_ID,
  SPACE_ID,
  collection,
  expectRequest,
  pageProviders,
  respond,
  settle,
  space,
} from '../../core/testing/test-utils';
import { FakeRealtime, provideFakeRealtime } from '../../core/testing/fake-hub';
import { SearchHit, SearchPageComponent } from './search-page.component';

const OTHER_SPACE_ID = '0199a000-0000-7000-8000-0000000000b2';
const OTHER_COLLECTION_ID = '0199a000-0000-7000-8000-0000000000c2';

function hit(overrides: Partial<SearchHit> = {}): SearchHit {
  return {
    recordId: RECORD_ID,
    title: 'Release plan',
    collectionId: COLLECTION_ID,
    spaceId: SPACE_ID,
    snippet: 'The \u27E6release\u27E7 is due',
    rank: 0.5,
    updatedAt: '2026-11-07T10:00:00Z',
    archived: false,
    ...overrides,
  };
}

describe('SearchPageComponent', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;

  /** Opens `url`; with `first` the search request of the page is answered. */
  async function render(
    url: string,
    first?: { items: SearchHit[]; nextCursor?: string | null } | { status: number; body: unknown },
  ): Promise<void> {
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({
          routes: [{ path: 'w/:workspaceSlug/search', component: SearchPageComponent }],
        }),
        provideFakeRealtime(new FakeRealtime()),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl(url);
    await respond(http, 'GET', `${API}/spaces`, [
      space(),
      space({ id: OTHER_SPACE_ID, name: 'Wissen' }),
    ]);
    await respond(http, 'GET', `${API}/collections`, [
      collection(),
      collection({ id: OTHER_COLLECTION_ID, name: 'Notizen', spaceId: OTHER_SPACE_ID }),
    ]);
    if (first) {
      await answer(`${API}/search?${url.split('?')[1]}&limit=20`, first);
    }
    await navigation;
    await settle(harness.fixture);
  }

  async function answer(
    url: string,
    reply: { items: SearchHit[]; nextCursor?: string | null } | { status: number; body: unknown },
  ): Promise<void> {
    if ('status' in reply) {
      await respond(http, 'GET', url, reply.body, {
        status: reply.status,
        statusText: 'Error',
      });
    } else {
      await respond(http, 'GET', url, { items: reply.items, nextCursor: reply.nextCursor ?? null });
    }
  }

  afterEach(() => http.verify());

  const root = () => harness.routeNativeElement as HTMLElement;
  const q = <T extends HTMLElement = HTMLElement>(id: string) =>
    root().querySelector<T>(`[data-testid="${id}"]`);
  const all = (id: string) =>
    Array.from(root().querySelectorAll<HTMLElement>(`[data-testid="${id}"]`));
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const url = () => TestBed.inject(Router).url;

  async function choose(id: string, value: string): Promise<void> {
    const select = q(id)!.querySelector('select')!;
    select.value = value;
    select.dispatchEvent(new Event('change', { bubbles: true }));
    harness.fixture.detectChanges();
  }

  it('render_NoQuery_ShowsSyntaxHintAndSendsNoRequest', async () => {
    await render('/w/demo/search');

    expect(text(q('search-hint'))).toContain('"genaue Phrase"');
    expect(text(q('search-hint'))).toContain('-Wort');
    expect(q('search-hit')).toBeNull();
  });

  it('render_Hits_ShowsTitleLinkContextBadgeAndDate', async () => {
    await render('/w/demo/search?q=release', {
      items: [
        hit(),
        hit({ recordId: 'r2', title: 'Old plan', archived: true, collectionId: null }),
      ],
    });

    const hits = all('search-hit');
    expect(hits).toHaveLength(2);
    const link = hits[0].querySelector<HTMLAnchorElement>('[data-testid="hit-title"]')!;
    expect(text(link)).toBe('Release plan');
    expect(link.getAttribute('href')).toBe(`/w/demo/records/${RECORD_ID}`);
    expect(text(hits[0].querySelector('[data-testid="hit-context"]'))).toBe('Produkt / Aufgaben');
    expect(hits[0].querySelector('[data-testid="hit-archived"]')).toBeNull();
    expect(text(hits[1].querySelector('[data-testid="hit-context"]'))).toBe('Produkt');
    expect(hits[1].querySelector('[data-testid="hit-archived"]')).not.toBeNull();
    expect(text(hits[0].querySelector('[data-testid="hit-updated"]'))).toMatch(
      /^07\.11\.2026 \d\d:\d\d$/,
    );
    expect(q('load-more')).toBeNull();
    expect((q('search-q') as HTMLInputElement).value).toBe('release');
  });

  it('render_Snippet_MarksTheHits', async () => {
    await render('/w/demo/search?q=release', {
      items: [hit({ snippet: '\u27E6Release\u27E7 and the next \u27E6release\u27E7 notes' })],
    });

    const marks = Array.from(root().querySelectorAll('[data-testid="hit-snippet"] mark'));
    expect(marks.map((m) => m.textContent)).toEqual(['Release', 'release']);
    expect(text(q('hit-snippet'))).toBe('Release and the next release notes');
  });

  it('render_SnippetWithMarkup_ShowsItAsText', async () => {
    await render('/w/demo/search?q=img', {
      items: [hit({ snippet: 'before <img src=x onerror=alert(1)> \u27E6<b>img</b>\u27E7 after' })],
    });

    const snippet = q('hit-snippet')!;
    expect(snippet.querySelector('img')).toBeNull();
    expect(snippet.querySelector('b')).toBeNull();
    expect(text(snippet)).toBe('before <img src=x onerror=alert(1)> <b>img</b> after');
    expect(snippet.querySelector('mark')!.textContent).toBe('<b>img</b>');
  });

  it('render_SnippetWithoutMarker_ShowsPlainText', async () => {
    await render('/w/demo/search?q=release', { items: [hit({ snippet: 'only title matched' })] });

    expect(snippetCount()).toBe(0);
    expect(text(q('hit-snippet'))).toBe('only title matched');

    function snippetCount(): number {
      return root().querySelectorAll('[data-testid="hit-snippet"] mark').length;
    }
  });

  it('loadMore_Cursor_AppendsNextPageWithoutDuplicates', async () => {
    await render('/w/demo/search?q=release', {
      items: [hit({ recordId: 'r1' })],
      nextCursor: 'c1',
    });

    q('load-more')!.click();
    await respond(http, 'GET', `${API}/search?q=release&limit=20&cursor=c1`, {
      items: [hit({ recordId: 'r1' }), hit({ recordId: 'r2', title: 'Second' })],
      nextCursor: null,
    });
    await settle(harness.fixture);

    expect(all('hit-title').map((a) => text(a))).toEqual(['Release plan', 'Second']);
    expect(q('load-more')).toBeNull();
  });

  it('render_NoHits_ShowsEmptyState', async () => {
    await render('/w/demo/search?q=nothing', { items: [] });

    expect(text(q('search-empty'))).toBe('Keine Treffer.');
  });

  it('render_Loading_ShowsSkeletonUntilAnswered', async () => {
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({
          routes: [{ path: 'w/:workspaceSlug/search', component: SearchPageComponent }],
        }),
        provideFakeRealtime(new FakeRealtime()),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl('/w/demo/search?q=slow');
    await respond(http, 'GET', `${API}/spaces`, []);
    await respond(http, 'GET', `${API}/collections`, []);
    const request = await expectRequest(http, 'GET', `${API}/search?q=slow&limit=20`);
    await navigation;
    harness.fixture.detectChanges();

    expect(root().querySelector('[aria-busy="true"]')).not.toBeNull();
    expect(q('search-empty')).toBeNull();

    request.flush({ items: [], nextCursor: null });
    await settle(harness.fixture);

    expect(root().querySelector('[aria-busy="true"]')).toBeNull();
    expect(text(q('search-empty'))).toBe('Keine Treffer.');
  });

  it('render_EmptyQueryError_ShowsHintAtTheField', async () => {
    await render('/w/demo/search?q=-', {
      status: 400,
      body: {
        status: 400,
        code: 'validation_failed',
        title: 'Validation failed',
        errors: [{ field: 'q', code: 'empty_query', message: 'Query is empty.' }],
      },
    });

    expect(text(q('search-q-error'))).toContain('keine Suchwörter');
    expect(q('search-q')!.getAttribute('aria-invalid')).toBe('true');
    expect(q('search-error')).toBeNull();
    expect(q('search-empty')).toBeNull();
  });

  it('render_ServerError_ShowsAlert', async () => {
    await render('/w/demo/search?q=boom', {
      status: 500,
      body: { status: 500, code: 'internal_error', title: 'Server error' },
    });

    expect(text(q('search-error'))).toContain('Server');
    expect(q('search-q-error')).toBeNull();
  });

  it('submit_Text_WritesQueryIntoUrlAndSearches', async () => {
    await render('/w/demo/search');

    const input = q<HTMLInputElement>('search-q')!;
    input.value = 'new words';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    q('search-form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await respond(http, 'GET', `${API}/search?q=new%20words&limit=20`, {
      items: [hit()],
      nextCursor: null,
    });
    await settle(harness.fixture);

    expect(url()).toBe('/w/demo/search?q=new%20words');
    expect(all('search-hit')).toHaveLength(1);
  });

  it('filter_SpaceCollectionAndArchived_WriteUrlAndRequest', async () => {
    await render('/w/demo/search?q=release', { items: [] });

    choose('filter-space', SPACE_ID);
    await respond(http, 'GET', `${API}/search?q=release&spaceId=${SPACE_ID}&limit=20`, {
      items: [],
      nextCursor: null,
    });
    await settle(harness.fixture);
    expect(url()).toBe(`/w/demo/search?q=release&spaceId=${SPACE_ID}`);

    choose('filter-collection', COLLECTION_ID);
    await respond(
      http,
      'GET',
      `${API}/search?q=release&spaceId=${SPACE_ID}&collectionId=${COLLECTION_ID}&limit=20`,
      { items: [], nextCursor: null },
    );
    await settle(harness.fixture);

    q('filter-archived')!.querySelector('button')!.click();
    await respond(
      http,
      'GET',
      `${API}/search?q=release&spaceId=${SPACE_ID}&collectionId=${COLLECTION_ID}&includeArchived=true&limit=20`,
      { items: [hit({ archived: true })], nextCursor: null },
    );
    await settle(harness.fixture);

    expect(url()).toBe(
      `/w/demo/search?q=release&spaceId=${SPACE_ID}&collectionId=${COLLECTION_ID}&includeArchived=true`,
    );
    expect(all('hit-archived')).toHaveLength(1);
  });

  it('render_SpaceInUrl_OffersOnlyItsCollections', async () => {
    await render(`/w/demo/search?q=release&spaceId=${OTHER_SPACE_ID}`, { items: [] });

    const options = Array.from(q('filter-collection')!.querySelectorAll('option')).map((o) =>
      text(o),
    );
    expect(options).toEqual(['Alle', 'Notizen']);
  });

  it('filter_OtherSpaceThanCollection_ClearsCollection', async () => {
    await render(`/w/demo/search?q=release&collectionId=${COLLECTION_ID}`, { items: [] });

    choose('filter-space', OTHER_SPACE_ID);
    await respond(http, 'GET', `${API}/search?q=release&spaceId=${OTHER_SPACE_ID}&limit=20`, {
      items: [],
      nextCursor: null,
    });
    await settle(harness.fixture);

    expect(url()).toBe(`/w/demo/search?q=release&spaceId=${OTHER_SPACE_ID}`);
  });

  it('url_EarlierUrlRestored_RestoresFilterAndResult', async () => {
    await render('/w/demo/search?q=release', { items: [hit({ recordId: 'r1' })] });

    choose('filter-space', SPACE_ID);
    await respond(http, 'GET', `${API}/search?q=release&spaceId=${SPACE_ID}&limit=20`, {
      items: [],
      nextCursor: null,
    });
    await settle(harness.fixture);
    expect(all('search-hit')).toHaveLength(0);

    // What Back does: the router goes to the earlier URL; the page holds no state beside it.
    void TestBed.inject(Router).navigateByUrl('/w/demo/search?q=release');
    await respond(http, 'GET', `${API}/search?q=release&limit=20`, {
      items: [hit({ recordId: 'r1' })],
      nextCursor: null,
    });
    await settle(harness.fixture);

    expect(url()).toBe('/w/demo/search?q=release');
    expect(all('search-hit')).toHaveLength(1);
    expect(q('filter-space')!.querySelector('select')!.value).toBe('');
  });
  it('render_TreeAnswersAfterSearch_SelectsFilterOptionsAndAllowsAlle', async () => {
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({
          routes: [{ path: 'w/:workspaceSlug/search', component: SearchPageComponent }],
        }),
        provideFakeRealtime(new FakeRealtime()),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    const filters = `spaceId=${OTHER_SPACE_ID}&collectionId=${OTHER_COLLECTION_ID}`;
    const navigation = harness.navigateByUrl(`/w/demo/search?q=release&${filters}`);
    await respond(http, 'GET', `${API}/search?q=release&${filters}&limit=20`, {
      items: [],
      nextCursor: null,
    });
    await respond(http, 'GET', `${API}/spaces`, [
      space(),
      space({ id: OTHER_SPACE_ID, name: 'Wissen' }),
    ]);
    await respond(http, 'GET', `${API}/collections`, [
      collection(),
      collection({ id: OTHER_COLLECTION_ID, name: 'Notizen', spaceId: OTHER_SPACE_ID }),
    ]);
    await navigation;
    await settle(harness.fixture);

    const spaceSelect = q('filter-space')!.querySelector('select')!;
    const collectionSelect = q('filter-collection')!.querySelector('select')!;
    expect(spaceSelect.value).toBe(OTHER_SPACE_ID);
    expect(collectionSelect.value).toBe(OTHER_COLLECTION_ID);

    choose('filter-collection', '');
    await respond(http, 'GET', `${API}/search?q=release&spaceId=${OTHER_SPACE_ID}&limit=20`, {
      items: [],
      nextCursor: null,
    });
    await settle(harness.fixture);
    expect(url()).toBe(`/w/demo/search?q=release&spaceId=${OTHER_SPACE_ID}`);
    expect(q('filter-collection')!.querySelector('select')!.value).toBe('');
  });

  it('render_SnippetNextToPunctuation_AddsNoWhitespace', async () => {
    await render('/w/demo/search?q=release', {
      items: [hit({ snippet: '(\u27E6release\u27E7), foo-\u27E6bar\u27E7.' })],
    });

    expect(q('hit-snippet')!.textContent).toBe('(release), foo-bar.');
  });

  it('render_ArchivedOrUnknownParents_AreNamedHonestly', async () => {
    await render('/w/demo/search?q=release&includeArchived=true', {
      items: [
        hit({ recordId: 'r1', spaceId: 'gone', collectionId: 'gone-too' }),
        hit({ recordId: 'r2', spaceId: 'gone', collectionId: null }),
      ],
    });

    expect(all('hit-context').map((c) => text(c))).toEqual([
      'archivierter Space / archivierte Collection',
      'archivierter Space',
    ]);
  });

  it('render_Form_HasSearchRoleAndLiveRegions', async () => {
    await render('/w/demo/search?q=nothing', { items: [] });

    expect(q('search-form')!.getAttribute('role')).toBe('search');
    expect(q('search-q')!.getAttribute('maxlength')).toBe('200');
    expect(q('search-q-errors')!.getAttribute('aria-live')).toBe('polite');
    expect(q('search-empty')!.getAttribute('role')).toBe('status');
  });

  it('render_TooLongError_NamesTheLimit', async () => {
    await render('/w/demo/search?q=long', {
      status: 400,
      body: {
        status: 400,
        code: 'validation_failed',
        title: 'Validation failed',
        errors: [{ field: 'q', code: 'too_long', message: 'Too long.' }],
      },
    });

    expect(text(q('search-q-error'))).toBe('Höchstens 200 Zeichen.');
  });

  it('render_UnknownFilter_OffersToRemoveIt', async () => {
    await render(`/w/demo/search?q=release&spaceId=${OTHER_SPACE_ID}x`, {
      status: 404,
      body: { status: 404, code: 'not_found', title: 'Not found' },
    });

    expect(text(q('search-filter-missing'))).toContain('Filter nicht gefunden');
    expect(q('search-empty')).toBeNull();

    q('search-filter-clear')!.click();
    await respond(http, 'GET', `${API}/search?q=release&limit=20`, {
      items: [hit()],
      nextCursor: null,
    });
    await settle(harness.fixture);

    expect(url()).toBe('/w/demo/search?q=release');
    expect(q('search-filter-missing')).toBeNull();
    expect(all('search-hit')).toHaveLength(1);
  });

  it('loadMore_FilterChangesMeanwhile_IgnoresTheStaleAnswer', async () => {
    await render('/w/demo/search?q=release', {
      items: [hit({ recordId: 'r1' })],
      nextCursor: 'c1',
    });

    q('load-more')!.click();
    const stale = await expectRequest(http, 'GET', `${API}/search?q=release&limit=20&cursor=c1`);
    choose('filter-space', SPACE_ID);
    await respond(http, 'GET', `${API}/search?q=release&spaceId=${SPACE_ID}&limit=20`, {
      items: [hit({ recordId: 'fresh', title: 'Fresh' })],
      nextCursor: null,
    });
    await settle(harness.fixture);
    stale.flush({ items: [hit({ recordId: 'stale', title: 'Stale' })], nextCursor: 'c2' });
    await settle(harness.fixture);

    expect(all('hit-title').map((a) => text(a))).toEqual(['Fresh']);
    expect(q('load-more')).toBeNull();
  });
});
