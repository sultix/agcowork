import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import { LIVE_RELOAD_DELAY_MS, LIVE_RELOAD_MAX_WAIT_MS } from '../../core/realtime/live-reload';
import { FakeRealtime, provideFakeRealtime } from '../../core/testing/fake-hub';
import {
  API,
  COLLECTION_ID,
  RECORD_ID,
  SPACE_ID,
  activityEvent,
  collection,
  expectRequest,
  pageProviders,
  propertyDefinition,
  respond,
  settle,
} from '../../core/testing/test-utils';
import { WorkspaceTreeStore } from '../spaces/workspace-tree.store';
import { RecordBacklinksComponent } from './record-backlinks.component';

@Component({ template: '' })
class BlankComponent {}

@Component({
  imports: [RecordBacklinksComponent],
  template: `<ag-record-backlinks [recordId]="recordId()" [collectionId]="collectionId" />`,
})
class HostComponent {
  readonly recordId = signal(RECORD_ID);
  readonly collectionId = COLLECTION_ID;
}

const DELAY = 20;
const SOURCE_ID = '0199a000-0000-7000-8000-0000000000c7';
const OTHER_COLLECTION = '0199a000-0000-7000-8000-0000000000c8';
const URL = `${API}/records/${RECORD_ID}/backlinks`;
const FIRST = `${URL}?limit=10`;
const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
const id = (n: number) => `0199a000-0000-7000-8000-${String(n).padStart(12, '0')}`;

/** The collection "Entscheidungen" whose relation `supersedes` points at the record's collection. */
const SOURCE = collection({
  id: SOURCE_ID,
  name: 'Entscheidungen',
  schema: [
    propertyDefinition({ key: 'title', name: 'Titel', type: 'text', required: true }),
    propertyDefinition({
      key: 'supersedes',
      name: 'Ersetzt',
      type: 'relation',
      targetCollectionId: COLLECTION_ID,
      multiple: true,
    }),
    propertyDefinition({ key: 'note', name: 'Notiz', type: 'text' }),
  ],
});

function group(overrides: Record<string, unknown> = {}) {
  return {
    collectionId: SOURCE_ID,
    collectionName: 'Entscheidungen',
    spaceId: SPACE_ID,
    propertyKey: 'supersedes',
    propertyName: 'Ersetzt',
    items: [
      { id: id(1), title: 'ADR 2', archived: false },
      { id: id(2), title: 'ADR 1', archived: true },
    ],
    nextCursor: null,
    more: null,
    ...overrides,
  };
}

const answer = (groups: unknown[], nextGroupsCursor: string | null = null) => ({
  recordId: RECORD_ID,
  groups,
  nextGroupsCursor,
});

describe('RecordBacklinksComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let http: HttpTestingController;
  let realtime: FakeRealtime;

  async function render(
    first: unknown,
    init: { status?: number; statusText?: string } = {},
  ): Promise<void> {
    realtime = new FakeRealtime();
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({ routes: [{ path: 'w/:workspaceSlug', component: BlankComponent }] }),
        provideFakeRealtime(realtime),
        { provide: LIVE_RELOAD_DELAY_MS, useValue: DELAY },
        { provide: LIVE_RELOAD_MAX_WAIT_MS, useValue: DELAY * 4 },
        {
          provide: WorkspaceTreeStore,
          useValue: { collections: signal([collection(), SOURCE]) },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/w/demo');
    fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    await respond(http, 'GET', FIRST, first, init);
    await settle(fixture);
  }

  afterEach(() => http.verify());

  const root = () => fixture.nativeElement as HTMLElement;
  const all = (testId: string, scope: ParentNode = root()) =>
    Array.from(scope.querySelectorAll<HTMLElement>(`[data-testid="${testId}"]`));
  const one = (testId: string) => root().querySelector<HTMLElement>(`[data-testid="${testId}"]`);
  const text = (el: Element | null | undefined) =>
    el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const items = () => all('backlink-item').map((li) => text(li));

  it('render_Groups_ShowCollectionPropertyAndTitlesAsLinks', async () => {
    await render(
      answer([
        group(),
        group({
          collectionId: OTHER_COLLECTION,
          collectionName: 'Notizen',
          propertyKey: 'about',
          propertyName: 'Betrifft',
          items: [{ id: id(3), title: '<script>alert(1)</script>', archived: false }],
        }),
      ]),
    );

    expect(text(one('backlinks')!.querySelector('h2'))).toBe('Verweise auf diesen Eintrag');
    expect(all('backlink-group-title').map((h) => text(h))).toEqual([
      'Entscheidungen · Ersetzt',
      'Notizen · Betrifft',
    ]);
    expect(items()).toEqual(['ADR 2', 'ADR 1 (archiviert)', '<script>alert(1)</script>']);
    const links = all('backlink-item').map((li) => li.querySelector('a')!);
    expect(links[0].getAttribute('href')).toBe(`/w/demo/records/${id(1)}`);
    expect(links[1].classList).toContain('line-through');
    expect(links[2].querySelector('script')).toBeNull();
    expect(all('backlink-group-title')[0].querySelector('a')!.getAttribute('href')).toBe(
      `/w/demo/collections/${SOURCE_ID}`,
    );
  });

  it('render_NoBacklinks_ShowsEmptyState', async () => {
    await render(answer([]));

    expect(text(one('backlinks-empty'))).toContain('Noch keine Verweise');
  });

  it('render_LoadFails_ShowsErrorAndRetries', async () => {
    await render(answer([]), { status: 500, statusText: 'Server Error' });

    expect(one('backlinks-error')).not.toBeNull();
    one('backlinks-retry')!.click();
    await respond(http, 'GET', FIRST, answer([group()]));
    await settle(fixture);

    expect(one('backlinks-error')).toBeNull();
    expect(items()).toEqual(['ADR 2', 'ADR 1 (archiviert)']);
  });

  it('render_FirstLoadOvertakenByAFailingLiveReload_ShowsErrorNotSkeleton', async () => {
    realtime = new FakeRealtime();
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({ routes: [{ path: 'w/:workspaceSlug', component: BlankComponent }] }),
        provideFakeRealtime(realtime),
        { provide: LIVE_RELOAD_DELAY_MS, useValue: DELAY },
        { provide: LIVE_RELOAD_MAX_WAIT_MS, useValue: DELAY * 4 },
        { provide: WorkspaceTreeStore, useValue: { collections: signal([collection(), SOURCE]) } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/w/demo');
    fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const first = await expectRequest(http, 'GET', FIRST);

    realtime.emitResync();
    const reload = await expectRequest(http, 'GET', FIRST);
    first.flush(answer([group()])); // overtaken: dropped
    reload.flush({ code: 'internal_error' }, { status: 500, statusText: 'Server Error' });
    await settle(fixture);

    expect(one('backlinks-loading')).toBeNull();
    expect(one('backlinks-error')).not.toBeNull();
    one('backlinks-retry')!.click();
    await respond(http, 'GET', FIRST, answer([group()]));
    await settle(fixture);
    expect(items()).toEqual(['ADR 2', 'ADR 1 (archiviert)']);
  });

  it('loadMore_GroupCursor_AppendsOnlyThatGroup', async () => {
    await render(
      answer([
        group({ nextCursor: 'c-1' }),
        group({
          collectionId: OTHER_COLLECTION,
          collectionName: 'Notizen',
          items: [{ id: id(5), title: 'N', archived: false }],
        }),
      ]),
    );

    all('backlink-more')[0].click();
    const request = await expectRequest(http, 'GET', `${URL}?limit=10&cursor=c-1`);
    request.flush(
      answer([
        group({ items: [{ id: id(3), title: 'ADR 0', archived: false }], nextCursor: null }),
      ]),
    );
    await settle(fixture);

    expect(items()).toEqual(['ADR 2', 'ADR 1 (archiviert)', 'ADR 0', 'N']);
    expect(all('backlink-more')).toHaveLength(0);
  });

  it('loadMore_Fails_ShowsTheErrorAtTheGroup', async () => {
    await render(answer([group({ nextCursor: 'c-1' })]));

    all('backlink-more')[0].click();
    (await expectRequest(http, 'GET', `${URL}?limit=10&cursor=c-1`)).flush(
      { code: 'invalid_format' },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle(fixture);

    expect(one('backlink-more-error')).not.toBeNull();
    expect(items()).toEqual(['ADR 2', 'ADR 1 (archiviert)']);
    expect(all('backlink-more')).toHaveLength(1);
  });

  it('loadMoreGroups_GroupsCursor_AppendsTheNextGroups', async () => {
    await render(answer([group()], 'g-1'));

    one('backlink-more-groups')!.click();
    (await expectRequest(http, 'GET', `${URL}?limit=10&cursor=g-1`)).flush(
      answer([
        group({
          collectionId: OTHER_COLLECTION,
          collectionName: 'Notizen',
          items: [{ id: id(9), title: 'Später', archived: false }],
        }),
      ]),
    );
    await settle(fixture);

    expect(all('backlink-group')).toHaveLength(2);
    expect(items()).toContain('Später');
    expect(one('backlink-more-groups')).toBeNull();
  });

  describe('live', () => {
    const event = (overrides: Parameters<typeof activityEvent>[0]) =>
      activityEvent({ collectionId: SOURCE_ID, targetId: id(7), ...overrides });

    it('live_NewReferenceInASourceCollection_ReloadsOnce', async () => {
      await render(answer([]));

      realtime.emit(event({ seq: 1, type: 'record.created' }));
      realtime.emit(event({ seq: 2, type: 'record.created', targetId: id(8) }));
      await respond(http, 'GET', FIRST, answer([group()]));
      await settle(fixture);

      expect(items()).toEqual(['ADR 2', 'ADR 1 (archiviert)']);
      await wait(DELAY * 3);
      http.expectNone(FIRST);
    });

    it('live_RelationChanged_Reloads', async () => {
      await render(answer([group()]));

      realtime.emit(
        event({ diff: { properties: { supersedes: { old: null, new: [RECORD_ID] } } } }),
      );
      await respond(http, 'GET', FIRST, answer([group()]));
    });

    it('live_TitleOfAListedRecordChanged_Reloads', async () => {
      await render(answer([group()]));

      realtime.emit(event({ targetId: id(1), diff: { title: { old: 'ADR 2', new: 'ADR 2b' } } }));
      await respond(
        http,
        'GET',
        FIRST,
        answer([group({ items: [{ id: id(1), title: 'ADR 2b', archived: false }] })]),
      );
      await settle(fixture);

      expect(items()).toEqual(['ADR 2b']);
    });

    it('live_SchemaOfASourceChanged_Reloads', async () => {
      await render(answer([group()]));

      realtime.emit(
        activityEvent({
          type: 'collection.schema_changed',
          targetType: 'collection',
          targetId: SOURCE_ID,
          collectionId: SOURCE_ID,
        }),
      );
      await respond(http, 'GET', FIRST, answer([]));
      await settle(fixture);

      expect(one('backlinks-empty')).not.toBeNull();
    });

    it('live_SchemaOfAnotherCollectionChanged_DoesNotReload', async () => {
      await render(answer([group()]));

      realtime.emit(
        activityEvent({
          type: 'collection.schema_changed',
          targetType: 'collection',
          targetId: OTHER_COLLECTION,
          collectionId: OTHER_COLLECTION,
        }),
      );
      await wait(DELAY * 3);

      http.expectNone(FIRST);
    });

    it('live_OtherChanges_DoNotReload', async () => {
      await render(answer([group()]));

      // another property of a source record, the title of a record that is not listed,
      // and a record of a collection without a relation to this one
      realtime.emit(event({ seq: 1, diff: { properties: { note: { old: 'a', new: 'b' } } } }));
      realtime.emit(event({ seq: 2, diff: { title: { old: 'x', new: 'y' } } }));
      realtime.emit(activityEvent({ seq: 3, type: 'record.created', collectionId: COLLECTION_ID }));
      await wait(DELAY * 3);

      http.expectNone(FIRST);
    });

    it('live_ReloadAfterMoreLoaded_KeepsAsManyRecords', async () => {
      await render(answer([group({ nextCursor: 'c-1' })]));
      all('backlink-more')[0].click();
      (await expectRequest(http, 'GET', `${URL}?limit=10&cursor=c-1`)).flush(
        answer([
          group({
            items: Array.from({ length: 10 }, (_, i) => ({
              id: id(100 + i),
              title: `Mehr ${i}`,
              archived: false,
            })),
            nextCursor: null,
          }),
        ]),
      );
      await settle(fixture);

      realtime.emitResync();
      await respond(http, 'GET', `${URL}?limit=12`, answer([group()]));
    });

    it('live_ReloadFails_KeepsWhatIsShown', async () => {
      await render(answer([group()]));

      realtime.emitResync();
      (await expectRequest(http, 'GET', FIRST)).flush(
        { code: 'internal_error' },
        { status: 500, statusText: 'Server Error' },
      );
      await settle(fixture);

      expect(items()).toEqual(['ADR 2', 'ADR 1 (archiviert)']);
      expect(one('backlinks-error')).toBeNull();
    });
  });

  it('switch_OtherRecord_DropsTheOlderAnswer', async () => {
    await render(answer([group()]));

    fixture.componentInstance.recordId.set(id(50));
    fixture.detectChanges();
    const next = await expectRequest(http, 'GET', `${API}/records/${id(50)}/backlinks?limit=10`);
    next.flush({ recordId: id(50), groups: [], nextGroupsCursor: null });
    await settle(fixture);

    expect(one('backlinks-empty')).not.toBeNull();
  });
});
