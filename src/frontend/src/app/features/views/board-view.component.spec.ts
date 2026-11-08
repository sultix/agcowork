import { Component, signal, viewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { Router, Routes } from '@angular/router';
import { FakeRealtime, provideFakeRealtime } from '../../core/testing/fake-hub';
import {
  API,
  BOARD_VIEW_ID,
  COLLECTION_ID,
  RECORD_ID,
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
import { BoardViewComponent, groupRecords } from './board-view.component';
import type { ViewDto } from './collection.store';
import { LIVE_REFRESH_DELAY_MS } from './live-refresh';
import { ViewWorkingState, savedState } from './view-state';

const STATUS = propertyDefinition({
  key: 'status',
  name: 'Status',
  type: 'status',
  options: [
    { value: 'open', label: 'Offen', color: null, group: 'todo' },
    { value: 'in_progress', label: 'In Arbeit', color: null, group: 'in_progress' },
    { value: 'review', label: 'Review', color: null, group: 'in_progress' },
    { value: 'done', label: 'Erledigt', color: null, group: 'done' },
  ],
});
const SCHEMA = [
  propertyDefinition({ key: 'title', name: 'Titel', type: 'text' }),
  STATUS,
  propertyDefinition({ key: 'estimate', name: 'Schätzung', type: 'number' }),
  propertyDefinition({ key: 'due', name: 'Fällig', type: 'date' }),
  propertyDefinition({ key: 'notes', name: 'Notiz', type: 'text' }),
  propertyDefinition({ key: 'extra', name: 'Extra', type: 'text' }),
  propertyDefinition({
    key: 'links',
    name: 'Verweise',
    type: 'relation',
    targetCollectionId: COLLECTION_ID,
    multiple: true,
  }),
];
const BOARD = viewDto({ id: BOARD_VIEW_ID, name: 'Nach Status', type: 'board', groupBy: 'status' });

const record = (id: string, status: string | null, extra: Record<string, unknown> = {}) =>
  recordDto({ id, title: `Karte ${id}`, revision: 2, properties: { status, ...extra } as never });

@Component({
  imports: [BoardViewComponent],
  template: `<ag-board-view
    [view]="view()"
    [state]="state()"
    [schema]="schema"
    [canWrite]="canWrite()"
  />`,
})
class HostComponent {
  readonly view = signal<ViewDto>(BOARD);
  readonly state = signal<ViewWorkingState>(savedState(BOARD));
  readonly schema = SCHEMA;
  readonly canWrite = signal(true);
  readonly board = viewChild.required(BoardViewComponent);
}

describe('groupRecords', () => {
  it('groupRecords_Status_ColumnsInOptionOrderWithGroupStartsAndNoValueColumn', () => {
    const columns = groupRecords(
      [
        record('a', 'done'),
        record('b', 'open'),
        record('c', null),
        record('d', 'gone'),
        record('e', 'open'),
      ],
      STATUS,
    );

    expect(columns.map((c) => [c.value, c.groupStart, c.records.map((r) => r.id)])).toEqual([
      [null, null, ['c', 'd']],
      ['open', 'todo', ['b', 'e']],
      ['in_progress', 'in_progress', []],
      ['review', null, []],
      ['done', 'done', ['a']],
    ]);
  });
});

describe('BoardViewComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let http: HttpTestingController;
  let realtime: FakeRealtime;

  const ROUTES: Routes = [{ path: 'w/:workspaceSlug', children: [] }];
  const DELAY = 30;

  async function render(
    items: RecordDto[],
    options: { canWrite?: boolean; nextCursor?: string | null; count?: number } = {},
  ): Promise<void> {
    realtime = new FakeRealtime();
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({ routes: ROUTES }),
        provideFakeRealtime(realtime),
        { provide: LIVE_REFRESH_DELAY_MS, useValue: DELAY },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    // the board reads the slug from the URL
    await TestBed.inject(Router).navigateByUrl('/w/demo');
    expect(TestBed.inject(WorkspaceContext).slug()).toBe('demo');
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    host.canWrite.set(options.canWrite ?? true);
    const query = await expectRequest(http, 'POST', `${API}/views/${BOARD_VIEW_ID}/query`);
    expect(query.request.body).toEqual({ limit: 500, includeCount: true });
    query.flush(queryPage(items, options.nextCursor ?? null, options.count ?? items.length));
    await respondIfRequested(http, `${API}/members`, []);
    await settle(fixture);
  }

  afterEach(() => http.verify());

  const root = () => fixture.nativeElement as HTMLElement;
  const all = (id: string, scope: ParentNode = root()) =>
    Array.from(scope.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`));
  const text = (el: Element | null | undefined) =>
    el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const column = (value: string) =>
    root().querySelector<HTMLElement>(`[data-testid="board-column"][data-value="${value}"]`)!;
  const cards = (value: string) => all('board-card', column(value)).map((c) => c.dataset['id']);

  it('render_Records_GroupedByStatusWithSeparatorsAndCardFields', async () => {
    await render([
      record(RECORD_ID, 'open', { estimate: 3, due: '2026-12-01', notes: 'n', extra: 'x' }),
      record('r2', 'done'),
      record('r3', null),
    ]);

    expect(all('board-column').map((c) => c.dataset['value'])).toEqual([
      '',
      'open',
      'in_progress',
      'review',
      'done',
    ]);
    expect(all('board-group').map(text)).toEqual(['Zu erledigen', 'In Arbeit', 'Abgeschlossen']);
    expect(cards('open')).toEqual([RECORD_ID]);
    expect(cards('done')).toEqual(['r2']);
    expect(cards('')).toEqual(['r3']);
    const card = all('board-card', column('open'))[0];
    expect(text(card.querySelector('[data-testid="board-card-link"]'))).toBe(`Karte ${RECORD_ID}`);
    // title + at most three properties (not the grouping property)
    expect(text(card)).toContain('Schätzung');
    expect(text(card)).toContain('Notiz');
    expect(text(card)).not.toContain('Extra');
    expect(text(card)).not.toContain('Offen');
    expect(all('board-truncated')).toHaveLength(0);
  });

  it('render_RelationField_CardShowsTitleChipsAsLinks', async () => {
    const target = '0199a000-0000-7000-8000-00000000bb01';
    await render([record(RECORD_ID, 'open', { links: [target] })]);
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

    const chip = all('relation-chip', column('open'))[0];
    expect(text(chip)).toBe('Spezifikation');
    expect(chip.querySelector('a')!.getAttribute('href')).toBe(`/w/demo/records/${target}`);
  });

  it('render_MoreThanLimit_ShowsHint', async () => {
    await render([record('a', 'open')], { nextCursor: 'cur', count: 812 });

    expect(text(all('board-truncated')[0])).toContain('ersten 500 von 812');
  });

  it('drop_OtherColumn_PatchesStatusOptimistically', async () => {
    await render([record(RECORD_ID, 'open'), record('r2', 'done')]);

    const moving = host.board().onDrop({
      previousContainer: { id: 'a' },
      container: { id: 'b', data: 'in_progress' },
      item: { data: record(RECORD_ID, 'open') },
    } as never);
    await new Promise((r) => setTimeout(r));
    fixture.detectChanges();
    expect(cards('in_progress')).toEqual([RECORD_ID]);
    expect(cards('open')).toEqual([]);

    const patch = await expectRequest(http, 'PATCH', `${API}/records/${RECORD_ID}`);
    expect(patch.request.body).toEqual({
      properties: { status: 'in_progress' },
      expectedRevision: 2,
    });
    patch.flush(recordDto({ revision: 3, properties: { status: 'in_progress' } as never }));
    await moving;
    await settle(fixture);

    expect(cards('in_progress')).toEqual([RECORD_ID]);
  });

  it('drop_SaveFails_RollsBackAndShowsError', async () => {
    await render([record(RECORD_ID, 'open')]);

    const moving = host.board().move(record(RECORD_ID, 'open'), 'done');
    (await expectRequest(http, 'PATCH', `${API}/records/${RECORD_ID}`)).flush(
      { code: 'internal_error', status: 500 },
      { status: 500, statusText: 'Server Error' },
    );
    expect(await moving).toBe(false);
    await settle(fixture);

    expect(cards('open')).toEqual([RECORD_ID]);
    expect(cards('done')).toEqual([]);
    expect(text(all('board-move-error')[0])).toContain('konnte nicht verschoben werden');
  });

  it('drop_Conflict_RollsBackAndOpensDialog', async () => {
    await render([record(RECORD_ID, 'open')]);

    const moving = host.board().move(record(RECORD_ID, 'open'), 'done');
    (await expectRequest(http, 'PATCH', `${API}/records/${RECORD_ID}`)).flush(
      { code: 'revision_mismatch', status: 409, currentRevision: 4, changedKeys: ['status'] },
      { status: 409, statusText: 'Conflict' },
    );
    await moving;
    await settle(fixture);

    expect(cards('open')).toEqual([RECORD_ID]);
    expect(all('conflict-dialog', document.body)).toHaveLength(1);
  });

  it('drop_SameColumn_SendsNothing', async () => {
    await render([record(RECORD_ID, 'open')]);
    const list = { id: 'a', data: 'open' };

    await host.board().onDrop({
      previousContainer: list,
      container: list,
      item: { data: record(RECORD_ID, 'open') },
    } as never);

    http.expectNone(`${API}/records/${RECORD_ID}`);
  });

  it('move_Viewer_IsRefused', async () => {
    await render([record(RECORD_ID, 'open')], { canWrite: false });

    expect(await host.board().move(record(RECORD_ID, 'open'), 'done')).toBe(false);
    http.expectNone(`${API}/records/${RECORD_ID}`);
  });

  describe('live updates', () => {
    const QUERY_URL = `${API}/views/${BOARD_VIEW_ID}/query`;
    const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
    const recordEvent = (seq: number) =>
      activityEvent({ seq, id: `e${seq}`, collectionId: COLLECTION_ID, targetId: 'r9' });

    it('live_SeveralEvents_ReloadOnce', async () => {
      await render([record(RECORD_ID, 'open')]);

      realtime.emit(recordEvent(1));
      realtime.emit(recordEvent(2));
      const query = await expectRequest(http, 'POST', QUERY_URL);
      query.flush(queryPage([record(RECORD_ID, 'open'), record('r9', 'done')]));
      await settle(fixture);
      await wait(DELAY * 2);

      http.expectNone(QUERY_URL);
      expect(cards('done')).toEqual(['r9']);
    });

    it('live_DuringDragAndMoveSave_WaitsThenReloadsOnce', async () => {
      await render([record(RECORD_ID, 'open')]);

      host.board().onDragStarted();
      realtime.emit(recordEvent(1));
      await wait(DELAY * 3);
      http.expectNone(QUERY_URL);

      const moving = host.board().onDrop({
        previousContainer: { id: 'a' },
        container: { id: 'b', data: 'done' },
        item: { data: record(RECORD_ID, 'open') },
      } as never);
      expect(host.board().dragging()).toBe(false);
      const patch = await expectRequest(http, 'PATCH', `${API}/records/${RECORD_ID}`);
      await wait(DELAY * 2);
      http.expectNone(QUERY_URL); // the save runs: the optimistic card stays
      fixture.detectChanges();
      expect(cards('done')).toEqual([RECORD_ID]);

      patch.flush(recordDto({ revision: 3, properties: { status: 'done' } as never }));
      await moving;
      (await expectRequest(http, 'POST', QUERY_URL)).flush(
        queryPage([record(RECORD_ID, 'done'), record('r9', 'open')]),
      );
      await settle(fixture);

      expect(cards('done')).toEqual([RECORD_ID]);
      expect(cards('open')).toEqual(['r9']);
    });

    it('live_MoveStartedAndEndedWhileReloading_DiscardsOutdatedCards', async () => {
      await render([record(RECORD_ID, 'open')]);

      realtime.emit(recordEvent(1));
      const outdated = await expectRequest(http, 'POST', QUERY_URL);
      host.board().onDragStarted();
      const moving = host.board().onDrop({
        previousContainer: { id: 'a' },
        container: { id: 'b', data: 'done' },
        item: { data: record(RECORD_ID, 'open') },
      } as never);
      (await expectRequest(http, 'PATCH', `${API}/records/${RECORD_ID}`)).flush(
        recordDto({ revision: 3, properties: { status: 'done' } as never }),
      );
      await moving;
      await settle(fixture);

      outdated.flush(queryPage([record(RECORD_ID, 'open')]));
      await settle(fixture);
      expect(cards('done')).toEqual([RECORD_ID]);

      (await expectRequest(http, 'POST', QUERY_URL)).flush(
        queryPage([recordDto({ revision: 3, properties: { status: 'done' } as never })]),
      );
      await settle(fixture);
      expect(cards('done')).toEqual([RECORD_ID]);
    });
  });
});
