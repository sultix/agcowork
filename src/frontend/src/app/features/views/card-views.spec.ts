import { Component, signal, viewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { Router, Routes } from '@angular/router';
import { FakeRealtime, provideFakeRealtime } from '../../core/testing/fake-hub';
import {
  API,
  COLLECTION_ID,
  RECORD_ID,
  VIEW_ID,
  activityEvent,
  expectRequest,
  pageProviders,
  propertyDefinition,
  queryPage,
  recordDto,
  respondIfRequested,
  settle,
  viewDto,
} from '../../core/testing/test-utils';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import type { RecordDto } from '../records/record-save.service';
import { coverDisplay } from './card-cover.component';
import { gridColumns, nextIndex } from './card-view';
import type { ViewDto } from './collection.store';
import { GalleryViewComponent } from './gallery-view.component';
import { LIVE_REFRESH_DELAY_MS } from './live-refresh';
import { ListViewComponent } from './list-view.component';
import { ViewWorkingState, savedState } from './view-state';

const STATUS = propertyDefinition({
  key: 'status',
  name: 'Status',
  type: 'status',
  options: [
    { value: 'open', label: 'Offen', color: 'blue', group: 'todo' },
    { value: 'done', label: 'Erledigt', color: null, group: 'done' },
  ],
});
const TAGS = propertyDefinition({
  key: 'tags',
  name: 'Tags',
  type: 'multi_select',
  options: [
    { value: 'a', label: 'Alpha', color: 'red', group: null },
    { value: 'b', label: 'Beta', color: 'green', group: null },
  ],
});
const SCHEMA = [
  propertyDefinition({ key: 'title', name: 'Titel', type: 'text' }),
  STATUS,
  TAGS,
  propertyDefinition({ key: 'estimate', name: 'Schätzung', type: 'number', unit: 'h' }),
  propertyDefinition({ key: 'due', name: 'Fällig', type: 'date' }),
  propertyDefinition({ key: 'site', name: 'Seite', type: 'url' }),
  propertyDefinition({ key: 'done', name: 'Fertig', type: 'checkbox' }),
  propertyDefinition({ key: 'notes', name: 'Notiz', type: 'text' }),
  propertyDefinition({ key: 'f1', name: 'Eins', type: 'text' }),
  propertyDefinition({ key: 'f2', name: 'Zwei', type: 'text' }),
  propertyDefinition({ key: 'f3', name: 'Drei', type: 'text' }),
  propertyDefinition({
    key: 'links',
    name: 'Verweise',
    type: 'relation',
    targetCollectionId: COLLECTION_ID,
    multiple: true,
  }),
];
const GALLERY = viewDto({
  id: VIEW_ID,
  name: 'Karten',
  type: 'gallery',
  cardCoverProperty: 'status',
});
const LIST = viewDto({ id: VIEW_ID, name: 'Liste', type: 'list' });

const record = (id: string, extra: Record<string, unknown> = {}) =>
  recordDto({ id, title: `Karte ${id}`, revision: 2, properties: extra as never });

describe('coverDisplay', () => {
  const title = 'Zebra';

  it('coverDisplay_OptionWithColor_BandWithLabelAndSwatch', () => {
    expect(coverDisplay(STATUS, 'open', title)).toEqual({
      label: 'Offen',
      swatch: 'bg-blue-500',
      kind: 'band',
    });
    expect(coverDisplay(STATUS, 'done', title)).toEqual({
      label: 'Erledigt',
      swatch: null,
      kind: 'band',
    });
  });

  it('coverDisplay_MultiSelect_UsesFirstOption', () => {
    expect(coverDisplay(TAGS, ['b', 'a'], title)).toMatchObject({ label: 'Beta', kind: 'band' });
  });

  it('coverDisplay_Checkbox_DoneOrOpenBand', () => {
    const def = SCHEMA.find((d) => d.key === 'done')!;
    expect(coverDisplay(def, true, title).label).toBe('Erledigt');
    expect(coverDisplay(def, false, title).label).toBe('Offen');
    expect(coverDisplay(def, null, title).label).toBe('Offen');
  });

  it('coverDisplay_Values_AsLargeText', () => {
    const by = (key: string) => SCHEMA.find((d) => d.key === key)!;
    expect(coverDisplay(by('site'), 'https://docs.example.com/a/b?x=1', title)).toMatchObject({
      label: 'docs.example.com',
      kind: 'text',
    });
    expect(coverDisplay(by('estimate'), 3, title).label).toBe('3 h');
    expect(coverDisplay(by('due'), '2026-12-01', title).label).toBe('01.12.2026');
    expect(coverDisplay(by('notes'), 'Hallo', title).label).toBe('Hallo');
  });

  it('coverDisplay_NoCoverOrEmptyValue_InitialOfTitle', () => {
    expect(coverDisplay(null, 'x', title)).toMatchObject({ label: 'Z', kind: 'initial' });
    expect(coverDisplay(STATUS, null, 'abc')).toMatchObject({ label: 'A', kind: 'initial' });
    expect(coverDisplay(STATUS, '', '')).toMatchObject({ label: '?', kind: 'initial' });
  });
});

describe('card navigation helpers', () => {
  it('nextIndex_GridKeys_MoveByRowsAndColumnsAndClamp', () => {
    expect(nextIndex('ArrowRight', 0, 6, 3, true)).toBe(1);
    expect(nextIndex('ArrowLeft', 0, 6, 3, true)).toBe(0);
    expect(nextIndex('ArrowDown', 1, 6, 3, true)).toBe(4);
    expect(nextIndex('ArrowDown', 4, 6, 3, true)).toBe(5);
    expect(nextIndex('ArrowUp', 4, 6, 3, true)).toBe(1);
    expect(nextIndex('Home', 4, 6, 3, true)).toBe(0);
    expect(nextIndex('End', 1, 6, 3, true)).toBe(5);
    expect(nextIndex('ArrowRight', 0, 6, 1, false)).toBeNull();
    expect(nextIndex('a', 0, 6, 1, false)).toBeNull();
  });

  it('gridColumns_ItemsInFirstRow_AreCounted', () => {
    const items = [0, 0, 0, 50, 50].map((offsetTop) => ({ offsetTop }) as HTMLElement);
    expect(gridColumns(items)).toBe(3);
    expect(gridColumns(items.slice(0, 2))).toBe(2);
    expect(gridColumns([])).toBe(1);
  });
});

@Component({
  imports: [GalleryViewComponent, ListViewComponent],
  template: `
    @if (view().type === 'gallery') {
      <ag-gallery-view [view]="view()" [state]="state()" [schema]="schema" />
    } @else {
      <ag-list-view [view]="view()" [state]="state()" [schema]="schema" />
    }
  `,
})
class HostComponent {
  readonly view = signal<ViewDto>(GALLERY);
  readonly state = signal<ViewWorkingState>(savedState(GALLERY));
  readonly schema = SCHEMA;
  readonly gallery = viewChild(GalleryViewComponent);
}

describe('GalleryViewComponent and ListViewComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let http: HttpTestingController;
  let realtime: FakeRealtime;
  let router: Router;

  const ROUTES: Routes = [
    { path: 'w/:workspaceSlug', children: [{ path: 'records/:id', children: [] }] },
  ];
  const DELAY = 30;
  const QUERY_URL = `${API}/views/${VIEW_ID}/query`;

  async function render(
    view: ViewDto,
    items: RecordDto[],
    options: { nextCursor?: string | null; count?: number; state?: ViewWorkingState } = {},
  ): Promise<{ body: unknown }> {
    realtime = new FakeRealtime();
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({ routes: ROUTES }),
        provideFakeRealtime(realtime),
        { provide: LIVE_REFRESH_DELAY_MS, useValue: DELAY },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    await router.navigateByUrl('/w/demo');
    expect(TestBed.inject(WorkspaceContext).slug()).toBe('demo');
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    host.view.set(view);
    host.state.set(options.state ?? savedState(view));
    const query = await expectRequest(
      http,
      'POST',
      options.state ? `${API}/collections/${COLLECTION_ID}/query` : QUERY_URL,
    );
    query.flush(queryPage(items, options.nextCursor ?? null, options.count ?? items.length));
    await respondIfRequested(http, `${API}/members`, []);
    await settle(fixture);
    return { body: query.request.body };
  }

  afterEach(() => http.verify());

  const root = () => fixture.nativeElement as HTMLElement;
  const all = (id: string, scope: ParentNode = root()) =>
    Array.from(scope.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`));
  const text = (el: Element | null | undefined) =>
    el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const tabStops = (id: string) =>
    all(id)
      .filter((l) => l.getAttribute('tabindex') === '0')
      .map((l) => l.closest('li')!.dataset['id']);
  const key = (el: Element, k: string) =>
    el.dispatchEvent(new KeyboardEvent('keydown', { key: k, bubbles: true, cancelable: true }));

  describe('gallery', () => {
    it('render_Records_CardsWithCoverTitleAndAtMostFiveFields', async () => {
      const { body } = await render(GALLERY, [
        record(RECORD_ID, {
          status: 'open',
          estimate: 3,
          due: '2026-12-01',
          notes: 'n',
          f1: 'a',
          f2: 'b',
          f3: 'c',
        }),
        record('r2', {}),
      ]);

      expect(body).toEqual({ limit: 60, includeCount: true });
      const cards = all('gallery-card');
      expect(cards).toHaveLength(2);
      expect(text(cards[0].querySelector('[data-testid="card-cover"]'))).toBe('Offen');
      expect(text(all('gallery-card-link', cards[0])[0])).toBe(`Karte ${RECORD_ID}`);
      // five fields at most, the cover property is not repeated
      const fields = text(cards[0]);
      expect(fields).toContain('Schätzung');
      expect(fields).toContain('Notiz');
      expect(fields).toContain('Zwei');
      expect(fields).not.toContain('Drei');
      expect(fields).not.toContain('Status');
      // no value on the cover property: initial of the title
      expect(text(cards[1].querySelector('[data-testid="card-cover"]'))).toBe('K');
      expect(text(root().querySelector('[data-testid="match-count"]'))).toContain('2 Treffer');
    });

    it('render_AnyCover_NeverLoadsAnImage', async () => {
      await render(GALLERY, [
        record(RECORD_ID, { status: 'open', site: 'https://evil.example/x.png' }),
      ]);
      host.view.set({ ...GALLERY, cardCoverProperty: 'site' });
      await settle(fixture);

      expect(root().querySelectorAll('img, picture, video, [style*="url("]')).toHaveLength(0);
      expect(text(all('card-cover')[0])).toBe('evil.example');
    });

    it('render_Roles_ListWithNamedItemsAndLinkNamesTheCard', async () => {
      await render(GALLERY, [record(RECORD_ID, { status: 'open' })]);

      const list = root().querySelector('ul[data-testid="gallery"]')!;
      expect(list.getAttribute('aria-label')).toBe('Galerie');
      expect(list.getAttribute('role')).toBe('list');
      expect(list.querySelectorAll('li')).toHaveLength(1);
      const link = all('gallery-card-link')[0];
      expect(link.getAttribute('href')).toBe(`/w/demo/records/${RECORD_ID}`);
      expect(text(link)).toBe(`Karte ${RECORD_ID}`);
      // the cover label is visible text, the color is not the only carrier
      expect(text(all('card-cover-label')[0])).toBe('Offen');
    });

    it('render_RelationField_ChipsAreLinksAndTitlesLoadedInOneLookup', async () => {
      const target = '0199a000-0000-7000-8000-00000000bb01';
      await render(GALLERY, [record(RECORD_ID, { links: [target] })]);
      const lookup = await expectRequest(http, 'POST', `${API}/records/lookup`);
      expect(lookup.request.body).toEqual({ ids: [target] });
      lookup.flush({
        items: [
          {
            id: target,
            title: 'Spezifikation',
            collectionId: COLLECTION_ID,
            spaceId: 's',
            archived: false,
          },
        ],
      });
      await settle(fixture);

      const chip = all('relation-chip')[0];
      expect(text(chip)).toBe('Spezifikation');
      expect(chip.querySelector('a')!.getAttribute('href')).toBe(`/w/demo/records/${target}`);
    });

    it('render_Empty_ShowsEmptyText', async () => {
      await render(GALLERY, []);

      expect(text(all('gallery-empty')[0])).toBe('Noch keine Records.');
      expect(root().querySelector('ul')).toBeNull();
    });

    it('render_CoverNotInVisibleFields_AsksForVisibleFieldsPlusCover', async () => {
      const state = { ...savedState(GALLERY), visible: ['estimate'] };
      const { body } = await render(GALLERY, [record(RECORD_ID, { status: 'open' })], { state });

      expect(body).toEqual({
        sorts: [],
        fields: ['estimate', 'status'],
        limit: 60,
        includeArchived: false,
        includeCount: true,
      });
      expect(text(all('card-cover')[0])).toBe('Offen');
    });

    it('render_SavedFieldsWithoutCover_UsesStoredView', async () => {
      const view = { ...GALLERY, cardCoverProperty: null, visibleProperties: ['estimate'] };
      const { body } = await render(view, [record(RECORD_ID, { estimate: 1 })]);

      expect(body).toEqual({ limit: 60, includeCount: true });
    });

    it('render_UnsupportedCoverType_CountsAsNoCover', async () => {
      await render({ ...GALLERY, cardCoverProperty: 'links' }, [
        record(RECORD_ID, { links: ['x'] }),
      ]);

      expect(text(all('card-cover')[0])).toBe('K');
    });

    it('render_Sorted_SendsSortsInAdHocQuery', async () => {
      const state = { ...savedState(GALLERY), sorts: [{ prop: 'estimate', dir: 'desc' }] };
      const { body } = await render(GALLERY, [record(RECORD_ID)], {
        state: state as ViewWorkingState,
      });

      expect(body).toMatchObject({ sorts: [{ prop: 'estimate', dir: 'desc' }] });
    });

    it('keyboard_FilterLeavesFewerRecords_ExactlyOneTabStopOnTheFirst', async () => {
      await render(GALLERY, [record('a'), record('b'), record('c')]);
      const links = all('gallery-card-link');
      links[0].focus();
      key(links[0], 'End');
      await settle(fixture);
      expect(tabStops('gallery-card-link')).toEqual(['c']);

      host.state.set({
        ...savedState(GALLERY),
        filter: { prop: 'status', op: 'eq', value: 'open' },
      });
      const query = await expectRequest(http, 'POST', `${API}/collections/${COLLECTION_ID}/query`);
      query.flush(queryPage([record('a')]));
      await settle(fixture);

      expect(tabStops('gallery-card-link')).toEqual(['a']);
    });

    it('keyboard_NewRecordOnTop_DoesNotMoveTheTabStop', async () => {
      await render(GALLERY, [record('a'), record('b')]);
      const links = all('gallery-card-link');
      links[1].focus();
      await settle(fixture);

      realtime.emit(
        activityEvent({ seq: 1, id: 'e1', collectionId: COLLECTION_ID, targetId: 'n' }),
      );
      (await expectRequest(http, 'POST', QUERY_URL)).flush(
        queryPage([record('n'), record('a'), record('b')]),
      );
      await settle(fixture);

      expect(tabStops('gallery-card-link')).toEqual(['b']);
    });

    it('loadMore_Button_AppendsNextPage', async () => {
      await render(GALLERY, [record('a')], { nextCursor: 'cur', count: 2 });

      all('gallery-load-more')[0].click();
      const more = await expectRequest(http, 'POST', QUERY_URL);
      expect(more.request.body).toMatchObject({ cursor: 'cur' });
      more.flush(queryPage([record('b')]));
      await settle(fixture);

      expect(all('gallery-card')).toHaveLength(2);
      expect(all('gallery-load-more')).toHaveLength(0);
    });

    it('keyboard_Arrows_MoveFocusInGridAndEnterIsALinkActivation', async () => {
      await render(GALLERY, [record('a'), record('b'), record('c'), record('d')]);
      const cards = all('gallery-card');
      // two columns: cards 0,1 in the first row, 2,3 in the second
      cards.forEach((card, i) =>
        Object.defineProperty(card, 'offsetTop', { value: Math.floor(i / 2) * 100 }),
      );
      const links = all('gallery-card-link');
      expect(links.map((l) => l.getAttribute('tabindex'))).toEqual(['0', '-1', '-1', '-1']);

      links[0].focus();
      key(links[0], 'ArrowRight');
      expect(document.activeElement).toBe(links[1]);
      key(links[1], 'ArrowDown');
      expect(document.activeElement).toBe(links[3]);
      key(links[3], 'ArrowLeft');
      expect(document.activeElement).toBe(links[2]);
      key(links[2], 'ArrowUp');
      expect(document.activeElement).toBe(links[0]);
      key(links[0], 'End');
      expect(document.activeElement).toBe(links[3]);
      key(links[3], 'Home');
      expect(document.activeElement).toBe(links[0]);
      links[2].focus();
      await settle(fixture);
      expect(all('gallery-card-link').map((l) => l.getAttribute('tabindex'))).toEqual([
        '-1',
        '-1',
        '0',
        '-1',
      ]);
      // Enter on the focused title link follows the link to the record
      links[2].click();
      await settle(fixture);
      expect(router.url).toBe('/w/demo/records/c');
    });

    it('live_AgentCreatesRecord_AppearsAfterReload', async () => {
      await render(GALLERY, [record('a')]);

      realtime.emit(
        activityEvent({ seq: 1, id: 'e1', collectionId: COLLECTION_ID, targetId: 'b' }),
      );
      const query = await expectRequest(http, 'POST', QUERY_URL);
      query.flush(queryPage([record('a'), record('b')]));
      await settle(fixture);

      expect(all('gallery-card').map((c) => c.dataset['id'])).toEqual(['a', 'b']);
    });

    it('error_QueryFails_ShowsError', async () => {
      realtime = new FakeRealtime();
      TestBed.configureTestingModule({
        providers: [...pageProviders({ routes: ROUTES }), provideFakeRealtime(realtime)],
      });
      http = TestBed.inject(HttpTestingController);
      await TestBed.inject(Router).navigateByUrl('/w/demo');
      fixture = TestBed.createComponent(HostComponent);
      (await expectRequest(http, 'POST', QUERY_URL)).flush(
        { code: 'internal_error', status: 500 },
        { status: 500, statusText: 'Server Error' },
      );
      await respondIfRequested(http, `${API}/members`, []);
      await settle(fixture);

      expect(all('gallery-error')).toHaveLength(1);
    });
  });

  describe('list', () => {
    it('render_Records_RowsWithTitleLinkAndFields', async () => {
      const { body } = await render(LIST, [
        record(RECORD_ID, { status: 'open', estimate: 3 }),
        record('r2', {}),
      ]);

      expect(body).toEqual({ limit: 100, includeCount: true });
      const rows = all('list-row');
      expect(rows).toHaveLength(2);
      expect(text(all('list-row-link', rows[0])[0])).toBe(`Karte ${RECORD_ID}`);
      expect(text(rows[0])).toContain('Status');
      expect(text(rows[0])).toContain('Schätzung');
      expect(text(rows[1])).toBe('Karte r2');
      expect(root().querySelector('ul[data-testid="list"]')!.getAttribute('aria-label')).toBe(
        'Liste',
      );
      expect(root().querySelectorAll('img')).toHaveLength(0);
    });

    it('keyboard_ArrowsHomeEnd_MoveFocusAndLinkOpens', async () => {
      await render(LIST, [record('a'), record('b'), record('c')]);
      const links = all('list-row-link');

      links[0].focus();
      key(links[0], 'ArrowDown');
      expect(document.activeElement).toBe(links[1]);
      key(links[1], 'ArrowRight');
      expect(document.activeElement).toBe(links[1]);
      key(links[1], 'End');
      expect(document.activeElement).toBe(links[2]);
      key(links[2], 'ArrowUp');
      expect(document.activeElement).toBe(links[1]);
      key(links[1], 'Home');
      expect(document.activeElement).toBe(links[0]);

      links[1].click();
      await settle(fixture);
      expect(router.url).toBe('/w/demo/records/b');
    });

    it('keyboard_ListShrinks_ExactlyOneTabStopOnTheFirst', async () => {
      await render(LIST, [record('a'), record('b')]);
      const links = all('list-row-link');
      links[0].focus();
      key(links[0], 'End');
      await settle(fixture);
      expect(tabStops('list-row-link')).toEqual(['b']);
      expect(root().querySelector('ul')!.getAttribute('role')).toBe('list');

      host.state.set({ ...savedState(LIST), filter: { prop: 'status', op: 'eq', value: 'open' } });
      (await expectRequest(http, 'POST', `${API}/collections/${COLLECTION_ID}/query`)).flush(
        queryPage([record('a')]),
      );
      await settle(fixture);

      expect(tabStops('list-row-link')).toEqual(['a']);
    });

    it('loadMore_Button_AppendsNextPage', async () => {
      await render(LIST, [record('a')], { nextCursor: 'cur', count: 2 });

      all('list-load-more')[0].click();
      (await expectRequest(http, 'POST', QUERY_URL)).flush(queryPage([record('b')]));
      await settle(fixture);

      expect(all('list-row')).toHaveLength(2);
    });

    it('live_AgentCreatesRecord_AppearsAfterReload', async () => {
      await render(LIST, [record('a')]);

      realtime.emit(
        activityEvent({ seq: 1, id: 'e1', collectionId: COLLECTION_ID, targetId: 'b' }),
      );
      (await expectRequest(http, 'POST', QUERY_URL)).flush(queryPage([record('a'), record('b')]));
      await settle(fixture);

      expect(all('list-row').map((c) => c.dataset['id'])).toEqual(['a', 'b']);
    });

    it('render_FilteredEmpty_ShowsNoHitsText', async () => {
      const state = {
        ...savedState(LIST),
        filter: { prop: 'status', op: 'eq', value: 'open' },
      } as ViewWorkingState;
      await render(LIST, [], { state });

      expect(text(all('list-empty')[0])).toBe('Keine Treffer für diesen Filter.');
    });
  });
});
