import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { Router } from '@angular/router';
import { FakeRealtime, provideFakeRealtime } from '../../core/testing/fake-hub';
import {
  API,
  COLLECTION_ID,
  RECORD_ID,
  VIEW_ID,
  activityEvent,
  collection,
  expectRequest,
  pageProviders,
  queryPage,
  recordDto,
  respondIfRequested,
  settle,
  typeInto,
  viewDto,
} from '../../core/testing/test-utils';
import type { RecordDto } from '../records/record-save.service';
import type { ViewDto } from './collection.store';
import { LIVE_REFRESH_DELAY_MS } from './live-refresh';
import { TableViewComponent } from './table-view.component';
import { ViewWorkingState, savedState } from './view-state';

const VIEW = viewDto();
const SCHEMA = collection().schema;
const QUERY_URL = `${API}/views/${VIEW_ID}/query`;
const DELAY = 30;

@Component({
  imports: [TableViewComponent],
  template: `<ag-table-view
    [view]="view()"
    [state]="state()"
    [schema]="schema"
    [canWrite]="true"
  />`,
})
class HostComponent {
  readonly view = signal<ViewDto>(VIEW);
  readonly state = signal<ViewWorkingState>(savedState(VIEW));
  readonly schema = SCHEMA;
}

const row = (id: string, estimate: number, revision = 2): RecordDto =>
  recordDto({
    id,
    title: `Zeile ${id}`,
    revision,
    properties: { status: 'todo', estimate } as never,
  });

describe('TableViewComponent live updates', () => {
  let fixture: ComponentFixture<HostComponent>;
  let http: HttpTestingController;
  let realtime: FakeRealtime;

  async function render(items: RecordDto[]): Promise<void> {
    realtime = new FakeRealtime();
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({ routes: [{ path: 'w/:workspaceSlug', children: [] }] }),
        provideFakeRealtime(realtime),
        { provide: LIVE_REFRESH_DELAY_MS, useValue: DELAY },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    await TestBed.inject(Router).navigateByUrl('/w/demo');
    fixture = TestBed.createComponent(HostComponent);
    (await expectRequest(http, 'POST', QUERY_URL)).flush(queryPage(items));
    await respondIfRequested(http, `${API}/members`, []);
    await settle(fixture);
  }

  afterEach(() => http.verify());

  const root = () => fixture.nativeElement as HTMLElement;
  const all = (id: string, scope: ParentNode = root()) =>
    Array.from(scope.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`));
  const text = (el: Element | null | undefined) =>
    el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const cell = (i: number, key: string) =>
    all('table-row')[i].querySelector<HTMLElement>(
      `[data-testid="table-cell"][data-key="${key}"]`,
    )!;
  const editor = () => document.querySelector<HTMLElement>('[data-testid="cell-editor"]');
  const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
  const recordEvent = (seq: number, collectionId = COLLECTION_ID) =>
    activityEvent({ seq, id: `e${seq}`, collectionId, targetId: RECORD_ID });

  it('live_SeveralEventsInShortSuccession_ReloadsOnce', async () => {
    await render([row(RECORD_ID, 1)]);

    realtime.emit(recordEvent(1));
    realtime.emit(recordEvent(2));
    await wait(DELAY / 3);
    realtime.emit(recordEvent(3));

    const query = await expectRequest(http, 'POST', QUERY_URL);
    expect(query.request.body).toEqual({ limit: 200, includeCount: true });
    query.flush(queryPage([row(RECORD_ID, 5, 3), row('r2', 2)]));
    await settle(fixture);
    await wait(DELAY * 2);

    http.expectNone(QUERY_URL);
    expect(all('table-row')).toHaveLength(2);
    expect(text(cell(0, 'estimate'))).toBe('5');
  });

  it('live_EventOfAnotherCollection_DoesNotReload', async () => {
    await render([row(RECORD_ID, 1)]);

    realtime.emit(recordEvent(1, 'other-collection'));
    realtime.emit(activityEvent({ seq: 2, targetType: 'collection', targetId: COLLECTION_ID }));
    await wait(DELAY * 3);

    http.expectNone(QUERY_URL);
  });

  it('live_Resync_Reloads', async () => {
    await render([row(RECORD_ID, 1)]);

    realtime.emitResync();

    (await expectRequest(http, 'POST', QUERY_URL)).flush(queryPage([row(RECORD_ID, 1)]));
    await settle(fixture);
  });

  it('live_DuringInlineEdit_WaitsKeepsInputAndReloadsOnceAfterwards', async () => {
    await render([row(RECORD_ID, 1)]);

    cell(0, 'estimate').dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    await settle(fixture);
    const input = editor()!.querySelector<HTMLInputElement>('[data-testid="editor-input"]')!;
    typeInto(input, '8');
    realtime.emit(recordEvent(1));
    realtime.emit(recordEvent(2));
    await wait(DELAY * 3);
    await settle(fixture);

    // no reload while the editor is open; the typed value is still there
    http.expectNone(QUERY_URL);
    expect(editor()).not.toBeNull();
    expect(input.value).toBe('8');

    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await settle(fixture);
    const patch = await expectRequest(http, 'PATCH', `${API}/records/${RECORD_ID}`);
    expect(patch.request.body).toEqual({ properties: { estimate: 8 }, expectedRevision: 2 });
    // nor while the save runs
    await wait(DELAY * 2);
    http.expectNone(QUERY_URL);
    patch.flush(row(RECORD_ID, 8, 4));
    await settle(fixture);

    const query = await expectRequest(http, 'POST', QUERY_URL);
    query.flush(queryPage([row(RECORD_ID, 8, 4)]));
    await settle(fixture);
    await wait(DELAY * 2);
    http.expectNone(QUERY_URL);
    expect(text(cell(0, 'estimate'))).toBe('8');
  });

  it('live_EditStartsWhileReloading_DoesNotReplaceRowsAndReloadsAgainAfterwards', async () => {
    await render([row(RECORD_ID, 1)]);

    realtime.emit(recordEvent(1));
    const query = await expectRequest(http, 'POST', QUERY_URL);
    cell(0, 'estimate').dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    await settle(fixture);
    query.flush(queryPage([row(RECORD_ID, 7, 3)]));
    await settle(fixture);

    // the answer came while editing: not taken over
    expect(text(cell(0, 'estimate'))).toBe('1');
    const input = editor()!.querySelector<HTMLInputElement>('[data-testid="editor-input"]')!;
    expect(input.value).toBe('1');

    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await settle(fixture);
    (await expectRequest(http, 'POST', QUERY_URL)).flush(queryPage([row(RECORD_ID, 7, 3)]));
    await settle(fixture);

    expect(text(cell(0, 'estimate'))).toBe('7');
  });

  it('live_RecordLeavesResult_DropsItFromSelection', async () => {
    await render([row(RECORD_ID, 1), row('r2', 2)]);
    all('row-select')[0].click();
    await settle(fixture);
    all('row-select')[1].click();
    await settle(fixture);
    expect(text(all('selection-bar')[0])).toContain('2 ausgewählt');

    realtime.emit(recordEvent(1));
    (await expectRequest(http, 'POST', QUERY_URL)).flush(queryPage([row('r2', 2)]));
    await settle(fixture);

    expect(text(all('selection-bar')[0])).toContain('1 ausgewählt');
  });

  it('live_EditStartedAndEndedWhileReloading_DiscardsOutdatedRowsAndReloadsAgain', async () => {
    await render([row(RECORD_ID, 1)]);

    realtime.emit(recordEvent(1));
    const outdated = await expectRequest(http, 'POST', QUERY_URL);
    // while those rows are on their way, the user edits and saves a cell
    cell(0, 'estimate').dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    await settle(fixture);
    const input = editor()!.querySelector<HTMLInputElement>('[data-testid="editor-input"]')!;
    typeInto(input, '8');
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await settle(fixture);
    (await expectRequest(http, 'PATCH', `${API}/records/${RECORD_ID}`)).flush(row(RECORD_ID, 8, 3));
    await settle(fixture);
    expect(text(cell(0, 'estimate'))).toBe('8');

    // the rows fetched before the edit arrive now: thrown away, fetched once more
    outdated.flush(queryPage([row(RECORD_ID, 1, 2)]));
    await settle(fixture);
    expect(text(cell(0, 'estimate'))).toBe('8');
    (await expectRequest(http, 'POST', QUERY_URL)).flush(queryPage([row(RECORD_ID, 8, 3)]));
    await settle(fixture);

    // a second edit of the row is based on the saved revision, not on the outdated one
    cell(0, 'estimate').dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    await settle(fixture);
    const again = editor()!.querySelector<HTMLInputElement>('[data-testid="editor-input"]')!;
    typeInto(again, '9');
    again.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await settle(fixture);
    const patch = await expectRequest(http, 'PATCH', `${API}/records/${RECORD_ID}`);
    expect(patch.request.body).toEqual({ properties: { estimate: 9 }, expectedRevision: 3 });
    patch.flush(row(RECORD_ID, 9, 4));
    await settle(fixture);
  });
});
