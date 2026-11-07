import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { HttpTestingController } from '@angular/common/http/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import {
  API,
  COLLECTION_ID,
  RECORD_ID,
  SPACE_ID,
  collection,
  expectRequest,
  pageProviders,
  recordDto,
  respond,
  serve,
  settle,
  space,
  typeInto,
  workspace,
} from '../../core/testing/test-utils';
import { SpacePageComponent } from './space-page.component';

@Component({ template: 'stub' })
class StubComponent {}

const PAGE = recordDto({ id: RECORD_ID, collectionId: null, title: 'Willkommen' });
const SPACE_URL = `/w/demo/spaces/${SPACE_ID}`;

describe('SpacePageComponent', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;

  /** Renders the page and answers the initial requests. */
  async function render(
    options: { scopes?: string[]; pages?: unknown; spaces?: unknown[]; url?: string } = {},
  ): Promise<void> {
    TestBed.configureTestingModule({
      providers: pageProviders({
        ws: workspace({ scopes: options.scopes ?? ['read', 'write'] }),
        routes: [
          {
            path: 'w/:workspaceSlug',
            children: [
              { path: 'spaces/:spaceId', component: SpacePageComponent },
              { path: 'collections/:collectionId', component: StubComponent },
              { path: 'records/:recordId', component: StubComponent },
            ],
          },
        ],
      }),
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl(options.url ?? SPACE_URL);
    await serve(http, [
      [`${API}/spaces`, options.spaces ?? [space()]],
      [
        `${API}/collections`,
        [collection(), collection({ id: 'other', spaceId: 'sp2', name: 'Fremd' })],
      ],
      [
        `${API}/records?spaceId=${SPACE_ID}&limit=50`,
        options.pages ?? { items: [PAGE], nextCursor: null },
      ],
    ]);
    await navigation;
    await settle(harness.fixture);
  }

  afterEach(() => http.verify());

  const root = () => harness.routeNativeElement as HTMLElement;
  // The record create dialog renders into the CDK overlay container in <body>.
  const q = (id: string) =>
    root().querySelector<HTMLElement>(`[data-testid="${id}"]`) ??
    document.body.querySelector<HTMLElement>(`[data-testid="${id}"]`);
  const all = (id: string) =>
    Array.from(root().querySelectorAll<HTMLElement>(`[data-testid="${id}"]`));
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const click = async (id: string) => {
    q(id)!.click();
    await settle(harness.fixture);
  };

  it('render_Space_ShowsHeaderCollectionsOfThisSpaceAndFreePages', async () => {
    await render();

    expect(text(q('space-title'))).toBe('Produkt');
    expect(text(q('space-description-text'))).toBe('Alles rund um das Produkt');
    expect(all('collection-item')).toHaveLength(1);
    expect(text(all('collection-item')[0])).toContain('Arbeitspakete des Teams');
    expect(all('collection-item')[0].getAttribute('href')).toBe(
      `/w/demo/collections/${COLLECTION_ID}`,
    );
    expect(all('page-item').map((e) => text(e).startsWith('Willkommen'))).toEqual([true]);
    expect(all('page-item')[0].getAttribute('href')).toBe(`/w/demo/records/${RECORD_ID}`);
  });

  it('render_UnknownSpace_ShowsNotFound', async () => {
    await render({ spaces: [] });

    expect(q('not-found')).not.toBeNull();
    expect(q('space-title')).toBeNull();
  });

  it('render_Viewer_HidesAllWriteActions', async () => {
    await render({ scopes: ['read'] });

    for (const id of ['space-edit', 'collection-new', 'page-new']) {
      expect(q(id), id).toBeNull();
    }
  });

  it('pages_MoreAvailable_LoadMoreAppendsNextPage', async () => {
    await render({ pages: { items: [PAGE], nextCursor: 'c1' } });

    await click('pages-more');
    await respond(http, 'GET', `${API}/records?spaceId=${SPACE_ID}&limit=50&cursor=c1`, {
      items: [recordDto({ id: 'p2', collectionId: null, title: 'Zweite' })],
      nextCursor: null,
    });
    await settle(harness.fixture);

    expect(all('page-item')).toHaveLength(2);
    expect(q('pages-more')).toBeNull();
  });

  describe('edit space', () => {
    it('save_ChangedNameAndDescription_PatchesAndReloadsTree', async () => {
      await render();
      await click('space-edit');

      typeInto(q('space-name') as HTMLInputElement, 'Produkt neu');
      typeInto(q('space-description') as HTMLTextAreaElement, '');
      (q('space-save') as HTMLButtonElement).click();
      const patch = await expectRequest(http, 'PATCH', `${API}/spaces/${SPACE_ID}`);
      expect(patch.request.body).toEqual({ name: 'Produkt neu', description: '' });
      patch.flush(space({ name: 'Produkt neu' }));
      await serve(http, [
        [`${API}/spaces`, [space({ name: 'Produkt neu' })]],
        [`${API}/collections`, [collection()]],
      ]);
      await settle(harness.fixture);

      expect(text(q('space-title'))).toBe('Produkt neu');
      expect(q('space-form')).toBeNull();
    });

    it('save_EmptyName_ShowsFieldErrorWithoutRequest', async () => {
      await render();
      await click('space-edit');

      typeInto(q('space-name') as HTMLInputElement, '');
      (q('space-save') as HTMLButtonElement).click();
      await settle(harness.fixture);

      expect(text(q('space-form'))).toContain('Bitte einen Namen angeben.');
    });

    it('save_Forbidden_ShowsMessage', async () => {
      await render();
      await click('space-edit');

      (q('space-save') as HTMLButtonElement).click();
      (await expectRequest(http, 'PATCH', `${API}/spaces/${SPACE_ID}`)).flush(
        { code: 'forbidden', status: 403 },
        { status: 403, statusText: 'Forbidden' },
      );
      await settle(harness.fixture);

      expect(text(q('space-form-error'))).toContain('Dafür fehlen die nötigen Rechte');
    });
  });

  describe('new collection', () => {
    it('submit_NameAndDescription_PostsAndOpensCollection', async () => {
      await render();
      await click('collection-new');

      typeInto(q('collection-name') as HTMLInputElement, 'Entscheidungen');
      typeInto(q('collection-description') as HTMLTextAreaElement, 'Architekturentscheidungen');
      (q('collection-submit') as HTMLButtonElement).click();
      const post = await expectRequest(http, 'POST', `${API}/collections`);
      expect(post.request.body).toEqual({
        spaceId: SPACE_ID,
        name: 'Entscheidungen',
        description: 'Architekturentscheidungen',
      });
      post.flush(collection({ id: 'new', name: 'Entscheidungen' }), {
        status: 201,
        statusText: 'Created',
      });
      await serve(http, [
        [`${API}/spaces`, [space()]],
        [`${API}/collections`, [collection()]],
      ]);
      await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/w/demo/collections/new'));
    });

    it('submit_MissingDescription_ShowsFieldErrorWithoutRequest', async () => {
      await render();
      await click('collection-new');

      typeInto(q('collection-name') as HTMLInputElement, 'X');
      (q('collection-submit') as HTMLButtonElement).click();
      await settle(harness.fixture);

      expect(text(q('collection-description-errors'))).toContain('Bitte beschreiben');
    });

    it('submit_NameTaken_ShowsServerMessage', async () => {
      await render();
      await click('collection-new');
      typeInto(q('collection-name') as HTMLInputElement, 'Aufgaben');
      typeInto(q('collection-description') as HTMLTextAreaElement, 'doppelt');

      (q('collection-submit') as HTMLButtonElement).click();
      (await expectRequest(http, 'POST', `${API}/collections`)).flush(
        { code: 'name_taken', status: 409, detail: 'Name ist vergeben.' },
        { status: 409, statusText: 'Conflict' },
      );
      await settle(harness.fixture);

      expect(text(q('collection-form-error'))).toBe('Name ist vergeben.');
    });
  });

  describe('new page', () => {
    it('create_Title_PostsFreePageInSpaceAndOpensIt', async () => {
      await render();
      await click('page-new');

      typeInto(q('create-title') as HTMLInputElement, 'Notizen');
      (q('create-submit') as HTMLButtonElement).click();
      const post = await expectRequest(http, 'POST', `${API}/records`);
      expect(post.request.body).toEqual({ title: 'Notizen', spaceId: SPACE_ID });
      post.flush(recordDto({ id: 'np', collectionId: null, title: 'Notizen' }), {
        status: 201,
        statusText: 'Created',
      });

      await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/w/demo/records/np'));
    });

    it('cancel_ClosesDialogWithoutRequest', async () => {
      await render();
      await click('page-new');

      await click('create-cancel');

      expect(q('create-record-dialog')).toBeNull();
    });
  });
});
