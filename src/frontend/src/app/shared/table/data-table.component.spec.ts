import { CdkDropList } from '@angular/cdk/drag-drop';
import { CdkVirtualScrollViewport } from '@angular/cdk/scrolling';
import { Component, Provider, signal } from '@angular/core';
import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Router } from '@angular/router';
import {
  API,
  COLLECTION_ID,
  RECORD_ID,
  STATUS_DEFINITION,
  pageProviders,
  propertyDefinition,
  queryPage,
  recordDto,
  respond,
  respondPost,
  settle,
  typeInto,
} from '../../core/testing/test-utils';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import type { SortSpec } from '../filter/filter-dsl';
import { CHIP_CLICK_DELAY_MS } from '../properties/relation-chips.component';
import { RELATION_SEARCH_DEBOUNCE_MS } from '../properties/relation-editor.component';
import { DataTableComponent, TableColumn } from './data-table.component';
import { CellEditor, CellState, TableRecord } from './inline-edit';

type RecordDto = TableRecord;

const CLICK_DELAY = 30;
const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

/** Records the saves the table asks for; cell states are set by the test. */
class FakeEditor implements CellEditor {
  readonly saves: { id: string; key: string; value: unknown; revision: number }[] = [];
  readonly states = signal<Record<string, CellState>>({});
  result = true;

  cell(recordId: string, key: string): CellState | undefined {
    return this.states()[`${recordId}:${key}`];
  }

  save(record: RecordDto, key: string, value: unknown): Promise<boolean> {
    this.saves.push({ id: record.id, key, value, revision: Number(record.revision) });
    return Promise.resolve(this.result);
  }
}

const COLUMNS: TableColumn[] = [
  STATUS_DEFINITION,
  propertyDefinition({ key: 'estimate', name: 'Schätzung', type: 'number', unit: 'h' }),
  propertyDefinition({ key: 'tags', name: 'Tags', type: 'multi_select' }),
  propertyDefinition({ key: 'created', name: 'Angelegt', type: 'created_at', readOnly: true }),
].map((definition) => ({ key: definition.key, definition }));

@Component({
  imports: [DataTableComponent],
  template: `<ag-data-table
    [rows]="rows()"
    [columns]="columns()"
    [sorts]="sorts()"
    [canWrite]="canWrite()"
    [linkBase]="['/w', 'demo', 'records']"
    [edits]="edits"
    [hasMore]="hasMore()"
    [moreError]="moreError()"
    [selection]="selection()"
    (sortToggle)="sortEvents.push($event)"
    (widthChange)="widthEvents.push($event)"
    (columnOrderChange)="orderEvents.push($event)"
    (selectionChange)="selection.set($event)"
    (loadMore)="loadMoreCount = loadMoreCount + 1"
  />`,
})
class HostComponent {
  readonly edits = new FakeEditor();
  readonly rows = signal<RecordDto[]>([]);
  readonly columns = signal(COLUMNS);
  readonly sorts = signal<SortSpec[]>([]);
  readonly canWrite = signal(true);
  readonly hasMore = signal(false);
  readonly moreError = signal<string | null>(null);
  readonly selection = signal<ReadonlySet<string>>(new Set());
  readonly sortEvents: { prop: string; multi: boolean }[] = [];
  readonly widthEvents: { key: string; width: number }[] = [];
  readonly orderEvents: string[][] = [];
  loadMoreCount = 0;
}

describe('DataTableComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;

  const ROWS = [
    recordDto({
      properties: { status: 'todo', estimate: 3, created: '2026-11-07T10:00:00Z' } as never,
    }),
    recordDto({ id: 'r2', title: 'Zweiter', revision: 1, properties: { status: 'done' } as never }),
  ];

  async function render(
    setup: (h: HostComponent) => void = () => undefined,
    extraProviders: Provider[] = [],
  ): Promise<void> {
    TestBed.configureTestingModule({
      providers: [...pageProviders({ routes: [] }), ...extraProviders],
    });
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    host.rows.set(ROWS);
    setup(host);
    await settle(fixture);
  }

  const root = () => fixture.nativeElement as HTMLElement;
  const all = (id: string, scope: ParentNode = root()) =>
    Array.from(scope.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`));
  const text = (el: Element | null | undefined) =>
    el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const row = (i: number) => all('table-row')[i];
  const cell = (i: number, key: string) =>
    row(i).querySelector<HTMLElement>(`[data-testid="table-cell"][data-key="${key}"]`)!;
  const editor = () => document.querySelector<HTMLElement>('[data-testid="cell-editor"]');

  async function openEditor(i: number, key: string): Promise<HTMLElement> {
    cell(i, key).dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    await settle(fixture);
    return editor()!;
  }

  function key(target: Element, name: string): void {
    target.dispatchEvent(new KeyboardEvent('keydown', { key: name, bubbles: true }));
  }

  it('render_Columns_TitleFirstThenGivenOrderWithDisplayValues', async () => {
    await render();

    expect(all('column-header').map((h) => h.dataset['key'])).toEqual([
      'title',
      'status',
      'estimate',
      'tags',
      'created',
    ]);
    expect(all('table-row')).toHaveLength(2);
    expect(text(row(0).querySelector('[data-testid="row-link"]'))).toBe('Login bauen');
    expect(row(0).querySelector('[data-testid="row-link"]')!.getAttribute('href')).toBe(
      `/w/demo/records/${RECORD_ID}`,
    );
    expect(text(cell(0, 'status'))).toBe('Offen');
    expect(text(cell(0, 'estimate'))).toBe('3 h');
    expect(text(cell(0, 'created'))).toMatch(/^07\.11\.2026 \d\d:00$/); // local time
    expect(text(cell(1, 'status'))).toBe('Erledigt');
  });

  it('render_NoRows_ShowsEmptyState', async () => {
    await render((h) => h.rows.set([]));

    expect(text(all('table-empty')[0])).toBe('Keine Records.');
  });

  it('sort_HeaderClick_EmitsToggleShiftForMulti_NotForUnsortableTypes', async () => {
    await render((h) => h.sorts.set([{ prop: 'estimate', dir: 'desc' }]));
    const button = (k: string) =>
      root().querySelector<HTMLElement>(`[data-key="${k}"] [data-testid="sort-button"]`)!;

    button('status').click();
    button('title').dispatchEvent(new MouseEvent('click', { shiftKey: true, bubbles: true }));
    button('tags').click();

    expect(host.sortEvents).toEqual([
      { prop: 'status', multi: false },
      { prop: 'title', multi: true },
    ]);
    expect(
      root()
        .querySelector('[data-key="estimate"] [data-testid="sort-indicator"]')!
        .getAttribute('data-dir'),
    ).toBe('desc');

    expect(root().querySelector('[data-key="estimate"]')!.getAttribute('aria-sort')).toBe(
      'descending',
    );
  });

  it('resize_DragEdge_EmitsNewWidth', async () => {
    await render();
    const handle = root().querySelector<HTMLElement>(
      '[data-key="estimate"] [data-testid="resize-handle"]',
    )!;

    handle.dispatchEvent(new MouseEvent('pointerdown', { clientX: 100, bubbles: true }));
    document.dispatchEvent(new MouseEvent('pointermove', { clientX: 160 }));
    await settle(fixture);
    expect(root().querySelector<HTMLElement>('[data-key="estimate"]')!.style.width).toBe('240px');
    document.dispatchEvent(new MouseEvent('pointerup', { clientX: 160 }));

    expect(host.widthEvents).toEqual([{ key: 'estimate', width: 240 }]);
  });

  it('reorder_DropColumn_EmitsNewKeyOrder', async () => {
    await render();
    const list = fixture.debugElement.query(By.directive(CdkDropList)).injector.get(CdkDropList);

    list.dropped.emit({ previousIndex: 0, currentIndex: 2 } as never);

    expect(host.orderEvents).toEqual([['estimate', 'tags', 'status', 'created']]);
  });

  it('select_RowsAndAll_EmitSelection', async () => {
    await render();

    all('row-select')[1].click();
    await settle(fixture);
    expect([...host.selection()]).toEqual(['r2']);

    all('select-all')[0].click();
    await settle(fixture);
    expect([...host.selection()].sort()).toEqual([RECORD_ID, 'r2'].sort());
  });

  describe('inline edit', () => {
    const saves = () => host.edits.saves;

    it('edit_SelectWithPointer_SavesAtOnceWithTheRowRecord', async () => {
      await render();

      const pane = await openEditor(0, 'status');
      const select = pane.querySelector<HTMLSelectElement>('[data-testid="editor-select"]')!;
      select.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true }));
      typeInto(select, 'done');
      await settle(fixture);

      expect(editor()).toBeNull();
      expect(saves()).toEqual([{ id: RECORD_ID, key: 'status', value: 'done', revision: 3 }]);
    });

    it('edit_SelectWithKeyboard_WaitsForEnter', async () => {
      await render();

      const pane = await openEditor(0, 'status');
      const select = pane.querySelector<HTMLSelectElement>('[data-testid="editor-select"]')!;
      key(select, 'ArrowDown');
      typeInto(select, 'done');
      await settle(fixture);
      expect(editor()).not.toBeNull();
      expect(saves()).toEqual([]);

      key(select, 'Enter');
      await settle(fixture);
      expect(editor()).toBeNull();
      expect(saves().map((s) => s.value)).toEqual(['done']);
    });

    it('edit_Number_EnterSaves', async () => {
      await render();

      const pane = await openEditor(0, 'estimate');
      const input = pane.querySelector<HTMLInputElement>('[data-testid="editor-input"]')!;
      typeInto(input, '8');
      key(input, 'Enter');
      await settle(fixture);

      expect(saves()).toEqual([{ id: RECORD_ID, key: 'estimate', value: 8, revision: 3 }]);
    });

    it('edit_TitleWithEnterKey_SavesTitle', async () => {
      await render();

      key(cell(1, 'title'), 'Enter');
      await settle(fixture);
      const input = editor()!.querySelector<HTMLInputElement>('[data-testid="cell-title-input"]')!;
      expect(input.value).toBe('Zweiter');
      typeInto(input, 'Umbenannt');
      key(input, 'Enter');
      await settle(fixture);

      expect(saves()).toEqual([{ id: 'r2', key: 'title', value: 'Umbenannt', revision: 1 }]);
    });

    it('edit_Escape_ClosesWithoutSaving', async () => {
      await render();

      const pane = await openEditor(0, 'estimate');
      typeInto(pane.querySelector<HTMLInputElement>('[data-testid="editor-input"]')!, '99');
      key(pane, 'Escape');
      await settle(fixture);

      expect(editor()).toBeNull();
      expect(saves()).toEqual([]);
    });

    it('edit_SelectWithKeyboardThenEscape_ClosesWithoutSaving', async () => {
      await render();

      const pane = await openEditor(0, 'status');
      const select = pane.querySelector<HTMLSelectElement>('[data-testid="editor-select"]')!;
      key(select, 'ArrowDown');
      typeInto(select, 'done');
      key(select, 'Escape');
      await settle(fixture);

      expect(editor()).toBeNull();
      expect(saves()).toEqual([]);
    });

    describe('relation editor with its suggestion list', () => {
      const RELATION = propertyDefinition({
        key: 'parent',
        name: 'Eltern',
        type: 'relation',
        targetCollectionId: COLLECTION_ID,
      });
      const C2 = '0199a000-0000-7000-8000-0000000000c2';

      async function openRelation(): Promise<{ pane: HTMLElement; search: HTMLInputElement }> {
        // with a workspace slug the members store loads: answer it, or the app never settles
        const rendering = render(
          (h) => h.columns.set([{ key: RELATION.key, definition: RELATION }]),
          [
            { provide: WorkspaceContext, useValue: { slug: () => 'demo' } },
            { provide: RELATION_SEARCH_DEBOUNCE_MS, useValue: 10 },
          ],
        );
        const http = TestBed.inject(HttpTestingController);
        await respond(http, 'GET', `${API}/members`, []);
        await rendering;
        cell(0, 'parent').dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
        await settle(fixture);
        const pane = editor()!;
        const search = pane.querySelector<HTMLInputElement>('[data-testid="relation-search"]')!;
        typeInto(search, 'logo');
        // the picker searches the target collection on the server
        await respondPost(
          http,
          `${API}/collections/${COLLECTION_ID}/query`,
          queryPage([recordDto({ id: C2, title: 'Logout bauen' })]),
        );
        await settle(fixture);
        pane.querySelector<HTMLElement>('[data-testid="relation-suggestion"]')!.click();
        await settle(fixture);
        return { pane, search };
      }

      it('edit_RelationPickedFromSuggestions_EnterSaves', async () => {
        const { search } = await openRelation();
        expect(editor()).not.toBeNull(); // picking a suggestion alone does not save

        key(search, 'Enter');
        await settle(fixture);

        expect(editor()).toBeNull();
        expect(saves()).toEqual([{ id: RECORD_ID, key: 'parent', value: C2, revision: 3 }]);
      });

      it('edit_RelationPickedFromSuggestions_EscapeDiscards', async () => {
        const { search } = await openRelation();

        key(search, 'Escape');
        await settle(fixture);

        expect(editor()).toBeNull();
        expect(saves()).toEqual([]);
      });
    });

    it('edit_UnchangedValue_SavesNothing', async () => {
      await render();

      const pane = await openEditor(0, 'estimate');
      key(pane.querySelector('input')!, 'Enter');
      await settle(fixture);

      expect(saves()).toEqual([]);
    });

    it('edit_FocusLeavesEditor_SavesExactlyOnce', async () => {
      await render();
      const outside = document.createElement('button');
      document.body.appendChild(outside);

      const pane = await openEditor(0, 'estimate');
      const input = pane.querySelector<HTMLInputElement>('[data-testid="editor-input"]')!;
      typeInto(input, '5');
      input.dispatchEvent(new FocusEvent('focusout', { bubbles: true, relatedTarget: outside }));
      outside.focus();
      await settle(fixture);

      expect(editor()).toBeNull();
      expect(saves().map((s) => s.value)).toEqual([5]);
      outside.remove();
    });

    it('edit_ClickOutside_SavesExactlyOnce', async () => {
      await render();

      const pane = await openEditor(0, 'estimate');
      typeInto(pane.querySelector<HTMLInputElement>('[data-testid="editor-input"]')!, '6');
      document.body.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true }));
      document.body.dispatchEvent(new MouseEvent('click', { bubbles: true }));
      await settle(fixture);

      expect(editor()).toBeNull();
      expect(saves().map((s) => s.value)).toEqual([6]);
    });

    it('cellState_ConflictAndError_AreShownAtTheCell', async () => {
      await render();

      host.edits.states.set({
        [`${RECORD_ID}:status`]: { status: 'conflict', message: 'Von jemand anderem geändert.' },
        [`${RECORD_ID}:estimate`]: {
          status: 'error',
          message: 'Wert liegt außerhalb des erlaubten Bereichs.',
          draft: -4,
        },
      });
      await settle(fixture);

      expect(cell(0, 'status').dataset['status']).toBe('conflict');
      expect(cell(0, 'estimate').dataset['status']).toBe('error');
      expect(cell(0, 'estimate').getAttribute('title')).toBe(
        'Wert liegt außerhalb des erlaubten Bereichs.',
      );
      expect(text(cell(0, 'estimate').querySelector('[data-testid="cell-error"]'))).toBe(
        'Wert liegt außerhalb des erlaubten Bereichs.',
      );
    });

    it('edit_AfterRejectedValue_OffersTheRejectedDraftAgain', async () => {
      await render();
      host.edits.states.set({
        [`${RECORD_ID}:estimate`]: { status: 'error', message: 'Zu groß.', draft: 999 },
      });
      await settle(fixture);

      const pane = await openEditor(0, 'estimate');
      const input = pane.querySelector<HTMLInputElement>('[data-testid="editor-input"]')!;
      expect(input.value).toBe('999');
      typeInto(input, '9');
      key(input, 'Enter');
      await settle(fixture);

      expect(saves().map((s) => s.value)).toEqual([9]);
    });

    it.each([
      ['viewer', 'status', (h: HostComponent) => h.canWrite.set(false)],
      [
        'archived record',
        'status',
        (h: HostComponent) =>
          h.rows.set([{ ...ROWS[0], archivedAt: '2026-11-07T12:00:00Z' }, ROWS[1]]),
      ],
      ['computed column', 'created', () => undefined],
      ['viewer on title', 'title', (h: HostComponent) => h.canWrite.set(false)],
    ])('edit_ReadOnly_%s_OpensNoEditor', async (_, column, setup) => {
      await render(setup);

      await openEditor(0, column);

      expect(editor()).toBeNull();
      if (column !== 'title') {
        expect(cell(0, column).getAttribute('aria-readonly')).toBe('true');
      }
    });
  });

  it('loadMore_ButtonAndRowsNearEnd_EmitLoadMore', async () => {
    await render((h) => h.hasMore.set(true));

    // few rows: the rendered range already reaches the end
    expect(host.loadMoreCount).toBeGreaterThanOrEqual(1);
    const before = host.loadMoreCount;
    all('table-more')[0].click();
    expect(host.loadMoreCount).toBe(before + 1);
  });

  it('loadMore_AfterFailedPage_OnlyTheButtonRetries', async () => {
    await render((h) => {
      h.hasMore.set(true);
      h.moreError.set('Server nicht erreichbar.');
    });

    expect(host.loadMoreCount).toBe(0);
    all('table-more')[0].click();
    expect(host.loadMoreCount).toBe(1);
  });

  it('loadMore_NoMorePages_NoButtonNoEmit', async () => {
    await render();

    expect(all('table-more')).toHaveLength(0);
    expect(host.loadMoreCount).toBe(0);
  });

  describe('relation cells', () => {
    const RELATION = propertyDefinition({
      key: 'links',
      name: 'Verweise',
      type: 'relation',
      targetCollectionId: COLLECTION_ID,
      multiple: true,
    });
    const target = (n: number) => `0199a000-0000-7000-8000-${String(n).padStart(12, '0')}`;
    const LOOKUP = `${API}/records/lookup`;

    /** Targets with titles "Ziel <last four digits>". */
    const titled = (ids: string[]) => ({
      items: ids.map((id) => ({
        id,
        title: `Ziel ${id.slice(-4)}`,
        collectionId: COLLECTION_ID,
        spaceId: 'space',
        archived: false,
      })),
    });
    const idsOf = (request: { request: { body: unknown } }) =>
      (request.request.body as { ids: string[] }).ids;

    /** 100 loaded rows; row i points at targets 2i and 2i+1: 200 distinct ids. */
    async function renderHundred(): Promise<HttpTestingController> {
      const rows = Array.from({ length: 100 }, (_, i) =>
        recordDto({
          id: `0199a000-0000-7000-8000-${String(5000 + i).padStart(12, '0')}`,
          title: `Zeile ${i}`,
          properties: { links: [target(2 * i), target(2 * i + 1)] } as never,
        }),
      );
      const rendering = render(
        (h) => {
          h.columns.set([{ key: RELATION.key, definition: RELATION }]);
          h.rows.set(rows);
        },
        [
          { provide: WorkspaceContext, useValue: { slug: () => 'demo' } },
          { provide: CHIP_CLICK_DELAY_MS, useValue: CLICK_DELAY },
        ],
      );
      const http = TestBed.inject(HttpTestingController);
      await respond(http, 'GET', `${API}/members`, []);
      await rendering;
      return http;
    }

    /** Waits for the one lookup of the hundred rows and answers it. */
    async function answerHundred(http: HttpTestingController): Promise<string[]> {
      await wait(60);
      const requests = http.match({ method: 'POST', url: LOOKUP });
      expect(requests).toHaveLength(1);
      requests[0].flush(titled(idsOf(requests[0])));
      await settle(fixture);
      return idsOf(requests[0]);
    }

    it('render_HundredRowsWith200Ids_ExactlyOneLookupAlsoAfterScrolling', async () => {
      const http = await renderHundred();

      const ids = await answerHundred(http);
      expect(ids).toHaveLength(200);
      expect(new Set(ids).size).toBe(200);
      expect(all('table-row').length).toBeLessThan(100); // virtual scroll renders a part only

      const chips = all('relation-chip', cell(0, 'links'));
      expect(chips.map((c) => c.textContent!.trim())).toEqual(['Ziel 0000', 'Ziel 0001']);
      expect(chips[0].querySelector('a')!.getAttribute('href')).toBe(
        `/w/demo/records/${target(0)}`,
      );
      expect(text(cell(0, 'links'))).not.toContain('Verknüpfung');

      const viewport = fixture.debugElement
        .query(By.directive(CdkVirtualScrollViewport))
        .injector.get(CdkVirtualScrollViewport);
      // what scrolling to the end does (jsdom has no layout to scroll in)
      viewport.setRenderedRange({ start: 80, end: 100 });
      for (let i = 0; i < 20 && !text(all('table-row').at(-1)).includes('Zeile 99'); i++) {
        await wait(20);
        await settle(fixture);
      }

      const last = all('table-row').at(-1)!;
      expect(text(last.querySelector('[data-testid="row-link"]'))).toBe('Zeile 99');
      const lastCell = last.querySelector<HTMLElement>(
        '[data-testid="table-cell"][data-key="links"]',
      )!;
      expect(all('relation-chip', lastCell).map((c) => text(c))).toEqual([
        'Ziel 0198',
        'Ziel 0199',
      ]);
      await wait(60);
      http.expectNone({ method: 'POST', url: LOOKUP });
    });

    it('render_MoreThanThreeTargets_ShowsThreeChipsAndPlusN', async () => {
      const rendering = render(
        (h) => {
          h.columns.set([{ key: RELATION.key, definition: RELATION }]);
          h.rows.set([recordDto({ properties: { links: [1, 2, 3, 4, 5].map(target) } as never })]);
        },
        [{ provide: WorkspaceContext, useValue: { slug: () => 'demo' } }],
      );
      const http = TestBed.inject(HttpTestingController);
      await respond(http, 'GET', `${API}/members`, []);
      await rendering;
      // the table asks for all relation ids of its loaded rows, also those behind "+N"
      const request = await respondPost(http, LOOKUP, { items: [] });
      await settle(fixture);

      expect(idsOf(request)).toHaveLength(5);
      expect(all('relation-chip', cell(0, 'links'))).toHaveLength(3);
      expect(text(cell(0, 'links'))).toContain('+2 weitere');
    });

    it('chipLink_SingleClick_NavigatesAfterTheDoubleClickWindow', async () => {
      const http = await renderHundred();
      await answerHundred(http);
      const router = TestBed.inject(Router);
      const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);

      const link = cell(0, 'links').querySelector<HTMLElement>('a')!;
      expect(link.getAttribute('tabindex')).toBe('-1');
      link.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
      expect(navigate).not.toHaveBeenCalled();
      await wait(CLICK_DELAY * 2);

      expect(navigate).toHaveBeenCalledWith(['/w', 'demo', 'records', target(0)]);
      expect(editor()).toBeNull();
    });

    it('chipLink_DoubleClick_OpensTheCellEditorWithoutNavigating', async () => {
      const http = await renderHundred();
      await answerHundred(http);
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

      const link = cell(0, 'links').querySelector<HTMLElement>('a')!;
      link.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
      link.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 2 }));
      link.dispatchEvent(
        new MouseEvent('dblclick', { bubbles: true, cancelable: true, detail: 2 }),
      );
      await settle(fixture);
      await wait(CLICK_DELAY * 2);

      expect(navigate).not.toHaveBeenCalled();
      const pane = editor()!;
      expect(pane.querySelector('[data-testid="relation-search"]')).not.toBeNull();
      // inside the edit pane the chips are plain text: no link that would leave the edit
      const chips = Array.from(pane.querySelectorAll<HTMLElement>('[data-testid="relation-chip"]'));
      expect(chips.map((c) => c.textContent!.trim())).toEqual(['Ziel 0000', 'Ziel 0001']);
      expect(pane.querySelector('a')).toBeNull();
    });

    it('chipLink_ModifiedClick_IsLeftToTheBrowser', async () => {
      const http = await renderHundred();
      await answerHundred(http);
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

      const link = cell(0, 'links').querySelector<HTMLElement>('a')!;
      const click = new MouseEvent('click', { bubbles: true, cancelable: true, ctrlKey: true });
      let preventedByChip: boolean | null = null;
      link.addEventListener('click', (e) => {
        preventedByChip = e.defaultPrevented;
        e.preventDefault(); // keep the test page where it is
      });
      link.dispatchEvent(click);
      await wait(CLICK_DELAY * 2);

      expect(preventedByChip).toBe(false);
      expect(navigate).not.toHaveBeenCalled();
      expect(link.getAttribute('href')).toBe(`/w/demo/records/${target(0)}`);
    });
  });
});
