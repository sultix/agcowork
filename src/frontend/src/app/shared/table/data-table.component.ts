import { CdkDrag, CdkDragDrop, CdkDragHandle, CdkDropList } from '@angular/cdk/drag-drop';
import {
  CdkConnectedOverlay,
  ConnectedPosition,
  createCloseScrollStrategy,
} from '@angular/cdk/overlay';
import {
  CdkFixedSizeVirtualScroll,
  CdkVirtualForOf,
  CdkVirtualScrollViewport,
} from '@angular/cdk/scrolling';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { SortSpec, isSortable } from '../filter/filter-dsl';
import { PropertyEditorComponent } from '../properties/property-editor.component';
import type { PropertyDefinition, PropertyType } from '../properties/property-types';
import { CellValueComponent } from './cell-value.component';
import { CellEditor, TITLE_KEY, TableRecord, canEditCell } from './inline-edit';

/** Fixed row height of the virtual scroll (px). */
export const ROW_HEIGHT = 36;
export const SELECT_WIDTH = 40;
export const DEFAULT_TITLE_WIDTH = 280;
export const DEFAULT_COLUMN_WIDTH = 180;
export const MIN_COLUMN_WIDTH = 60;
/** Rows before the end at which the next page is requested while scrolling. */
const LOAD_AHEAD_ROWS = 20;

/** One property column of the table. */
export interface TableColumn {
  readonly key: string;
  readonly definition: PropertyDefinition;
}

/** A cell being edited: which one, its editor definition (`null` = title) and the draft value. */
interface EditingCell {
  readonly record: TableRecord;
  readonly key: string;
  readonly definition: PropertyDefinition | null;
  readonly initial: unknown;
  readonly origin: HTMLElement;
}

/** Types whose editor is a single choice: the value is saved as soon as it is picked. */
const COMMIT_ON_CHANGE: readonly PropertyType[] = ['select', 'status', 'checkbox', 'actor'];
/** Multi-line editors: Enter is a newline there, Ctrl/Cmd+Enter saves. */
const MULTILINE: readonly PropertyType[] = ['markdown', 'json'];

const EDITOR_POSITIONS: ConnectedPosition[] = [
  { originX: 'start', originY: 'top', overlayX: 'start', overlayY: 'top' },
  { originX: 'start', originY: 'bottom', overlayX: 'start', overlayY: 'bottom' },
];

function sameJson(a: unknown, b: unknown): boolean {
  return JSON.stringify(a ?? null) === JSON.stringify(b ?? null);
}

/**
 * The table of a collection view: virtual scrolling with a fixed row height, the title column
 * fixed on the left, property columns in the given order (drag to reorder, drag the edge to
 * resize), sorting by header click (Shift adds a criterion), checkbox selection and inline
 * editing with the property editors in an overlay (double-click or Enter; Escape cancels;
 * Enter or leaving the editor saves through {@link InlineEditService}).
 */
@Component({
  selector: 'ag-data-table',
  imports: [
    CdkConnectedOverlay,
    CdkDrag,
    CdkDragHandle,
    CdkDropList,
    CdkFixedSizeVirtualScroll,
    CdkVirtualForOf,
    CdkVirtualScrollViewport,
    CellValueComponent,
    PropertyEditorComponent,
    RouterLink,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block text-sm', '[attr.data-row-count]': 'rows().length' },
  template: `
    <div
      class="rounded-md border border-border bg-surface"
      role="grid"
      aria-label="Records"
      [attr.aria-rowcount]="rows().length + 1"
      [attr.aria-colcount]="columns().length + 2"
      data-testid="data-table"
    >
      <div class="overflow-hidden border-b border-border" #header>
        <div class="flex" role="row" [style.width.px]="totalWidth()" data-testid="table-header">
          <div class="sticky left-0 z-10 flex bg-surface">
            <div
              class="flex items-center justify-center"
              role="columnheader"
              [style.width.px]="selectWidth"
            >
              <input
                type="checkbox"
                aria-label="Alle geladenen Records auswählen"
                data-testid="select-all"
                [checked]="allSelected()"
                [disabled]="rows().length === 0"
                (change)="toggleAll()"
              />
            </div>
            <div
              class="relative flex items-center"
              role="columnheader"
              data-testid="column-header"
              data-key="title"
              [attr.aria-sort]="ariaSort(titleKey)"
              [style.width.px]="widthOf(titleKey)"
            >
              <button
                type="button"
                class="flex h-9 min-w-0 flex-1 items-center gap-1 px-2 text-left font-medium text-muted hover:text-fg"
                data-testid="sort-button"
                (click)="onSortClick(titleKey, $event)"
              >
                <span class="truncate">{{ titleName() }}</span>
                <span data-testid="sort-indicator">{{ sortIndicator(titleKey) }}</span>
              </button>
              <span
                class="absolute top-0 right-0 h-full w-1.5 cursor-col-resize hover:bg-accent/40"
                data-testid="resize-handle"
                (pointerdown)="startResize(titleKey, $event)"
              ></span>
            </div>
          </div>
          <div
            class="flex"
            cdkDropList
            cdkDropListOrientation="horizontal"
            [cdkDropListDisabled]="!reorderable()"
            (cdkDropListDropped)="onColumnDrop($event)"
          >
            @for (column of columns(); track column.key) {
              <div
                class="relative flex items-center bg-surface"
                role="columnheader"
                cdkDrag
                cdkDragLockAxis="x"
                data-testid="column-header"
                [attr.data-key]="column.key"
                [attr.aria-sort]="ariaSort(column.key)"
                [style.width.px]="widthOf(column.key)"
                (cdkDragStarted)="dragging = true"
              >
                <button
                  type="button"
                  cdkDragHandle
                  class="flex h-9 min-w-0 flex-1 items-center gap-1 px-2 text-left font-medium text-muted hover:text-fg"
                  data-testid="sort-button"
                  [attr.title]="column.definition.description ?? column.definition.name"
                  [class.cursor-default]="!sortable(column)"
                  (click)="sortable(column) && onSortClick(column.key, $event)"
                >
                  <span class="truncate">{{ column.definition.name }}</span>
                  <span data-testid="sort-indicator">{{ sortIndicator(column.key) }}</span>
                </button>
                <span
                  class="absolute top-0 right-0 h-full w-1.5 cursor-col-resize hover:bg-accent/40"
                  data-testid="resize-handle"
                  (pointerdown)="startResize(column.key, $event)"
                ></span>
              </div>
            }
          </div>
        </div>
      </div>

      @if (rows().length === 0) {
        <div class="p-6 text-center text-muted" data-testid="table-state">
          @if (loading()) {
            Wird geladen …
          } @else if (error()) {
            <span class="text-danger" role="alert" data-testid="table-error">{{ error() }}</span>
          } @else {
            <span data-testid="table-empty">{{ emptyText() }}</span>
          }
        </div>
      } @else {
        <cdk-virtual-scroll-viewport
          class="block"
          data-testid="table-viewport"
          [itemSize]="rowHeight"
          [minBufferPx]="rowHeight * 10"
          [maxBufferPx]="rowHeight * 20"
          [style.height]="viewportHeight()"
        >
          <div
            *cdkVirtualFor="let row of rows(); trackBy: trackById"
            class="flex border-b border-border hover:bg-surface-hover"
            role="row"
            data-testid="table-row"
            [attr.data-id]="row.id"
            [class.opacity-60]="!!row.archivedAt"
            [style.height.px]="rowHeight"
            [style.width.px]="totalWidth()"
          >
            <div class="sticky left-0 z-[1] flex bg-surface">
              <div
                class="flex items-center justify-center"
                role="gridcell"
                [style.width.px]="selectWidth"
              >
                <input
                  type="checkbox"
                  data-testid="row-select"
                  [attr.aria-label]="'Auswählen: ' + row.title"
                  [checked]="selection().has(row.id)"
                  (change)="toggleRow(row.id)"
                />
              </div>
              <div
                #titleCell
                class="flex min-w-0 items-center px-2 outline-none focus:ring-2 focus:ring-accent focus:ring-inset"
                role="gridcell"
                tabindex="0"
                data-testid="table-cell"
                data-key="title"
                [attr.data-status]="cellStatus(row.id, titleKey)"
                [attr.title]="cellMessage(row.id, titleKey)"
                [class.ring-2]="!!cellStatus(row.id, titleKey)"
                [class.ring-danger]="
                  cellStatus(row.id, titleKey) === 'error' ||
                  cellStatus(row.id, titleKey) === 'conflict'
                "
                [style.width.px]="widthOf(titleKey)"
                (dblclick)="startEdit(row, titleKey, null, titleCell)"
                (keydown.enter)="startEdit(row, titleKey, null, titleCell)"
              >
                <a
                  class="truncate text-accent hover:underline"
                  tabindex="-1"
                  data-testid="row-link"
                  [routerLink]="[...linkBase(), row.id]"
                  (dblclick)="$event.stopPropagation()"
                  >{{ row.title || 'Ohne Titel' }}</a
                >
              </div>
            </div>
            @for (column of columns(); track column.key) {
              <div
                #cell
                class="flex min-w-0 items-center px-2 outline-none focus:ring-2 focus:ring-accent focus:ring-inset"
                role="gridcell"
                tabindex="0"
                data-testid="table-cell"
                [attr.data-key]="column.key"
                [attr.data-status]="cellStatus(row.id, column.key)"
                [attr.title]="cellMessage(row.id, column.key)"
                [attr.aria-readonly]="!editable(row, column.definition)"
                [class.ring-2]="!!cellStatus(row.id, column.key)"
                [class.ring-danger]="
                  cellStatus(row.id, column.key) === 'error' ||
                  cellStatus(row.id, column.key) === 'conflict'
                "
                [style.width.px]="widthOf(column.key)"
                (dblclick)="startEdit(row, column.key, column.definition, cell)"
                (keydown.enter)="startEdit(row, column.key, column.definition, cell)"
              >
                <ag-cell-value
                  [definition]="column.definition"
                  [value]="valueOf(row, column.key)"
                />
                @if (cellStatus(row.id, column.key) === 'error') {
                  <span class="sr-only" role="alert" data-testid="cell-error">{{
                    cellMessage(row.id, column.key)
                  }}</span>
                }
              </div>
            }
          </div>
        </cdk-virtual-scroll-viewport>
      }

      <div class="flex items-center gap-3 border-t border-border px-3 py-2 text-xs text-muted">
        <span data-testid="table-count">{{ rows().length }} geladen</span>
        @if (moreError(); as message) {
          <span class="text-danger" role="alert" data-testid="table-more-error">{{ message }}</span>
        }
        @if (hasMore()) {
          <button
            type="button"
            class="rounded-md border border-border px-2 py-1 hover:bg-surface-hover disabled:opacity-60"
            data-testid="table-more"
            [disabled]="loadingMore()"
            (click)="loadMore.emit()"
          >
            {{ loadingMore() ? 'Lädt …' : 'Mehr laden' }}
          </button>
        }
      </div>
    </div>

    <ng-template
      cdkConnectedOverlay
      [cdkConnectedOverlayOrigin]="editing()?.origin ?? host"
      [cdkConnectedOverlayOpen]="editing() !== null"
      [cdkConnectedOverlayPositions]="positions"
      [cdkConnectedOverlayScrollStrategy]="scrollStrategy"
      [cdkConnectedOverlayMinWidth]="288"
      (attach)="focusEditor()"
      (detach)="commit()"
      (overlayOutsideClick)="onOutsideClick($event)"
    >
      @if (editing(); as cell) {
        <div
          class="w-72 rounded-md border border-border bg-surface p-2 shadow-lg"
          role="dialog"
          aria-label="Wert bearbeiten"
          tabindex="-1"
          data-testid="cell-editor"
          [attr.data-key]="cell.key"
          (keydown)="onEditorKey($event)"
          (pointerdown)="lastInput = 'pointer'"
          (focusout)="onEditorFocusOut($event)"
          #editorPane
        >
          @if (cell.definition; as definition) {
            <ag-property-editor
              [definition]="definition"
              [value]="draft()"
              (valueChange)="onDraft($event)"
            />
          } @else {
            <input
              type="text"
              class="w-full rounded-md border border-border bg-bg px-2 py-1"
              aria-label="Titel"
              data-testid="cell-title-input"
              [value]="draft() ?? ''"
              (input)="draft.set($any($event.target).value)"
            />
          }
          <p class="mt-1 text-xs text-muted">
            {{ multiline(cell) ? 'Strg+Enter' : 'Enter' }} speichert · Esc bricht ab
          </p>
        </div>
      }
    </ng-template>
  `,
})
export class DataTableComponent {
  private readonly injector = inject(Injector);
  protected readonly host = inject(ElementRef<HTMLElement>).nativeElement as HTMLElement;

  readonly rows = input.required<readonly TableRecord[]>();
  readonly columns = input.required<readonly TableColumn[]>();
  readonly widths = input<Readonly<Record<string, number>>>({});
  readonly sorts = input<readonly SortSpec[]>([]);
  /** Name of the title column (the schema's title property). */
  readonly titleName = input('Titel');
  /** `write` scope on a writable workspace/collection; without it every cell is read-only. */
  readonly canWrite = input(false);
  /** Router link prefix of the document view; the record id is appended. */
  readonly linkBase = input.required<readonly string[]>();
  /** Saves cells and reports their state; without it every cell is read-only. */
  readonly edits = input<CellEditor | null>(null);
  readonly selection = input<ReadonlySet<string>>(new Set());
  readonly reorderable = input(true);
  readonly loading = input(false);
  readonly loadingMore = input(false);
  readonly hasMore = input(false);
  readonly error = input<string | null>(null);
  readonly moreError = input<string | null>(null);
  readonly emptyText = input('Keine Records.');
  readonly viewportHeight = input('calc(100vh - 20rem)');

  readonly sortToggle = output<{ prop: string; multi: boolean }>();
  readonly widthChange = output<{ key: string; width: number }>();
  readonly columnOrderChange = output<string[]>();
  readonly selectionChange = output<ReadonlySet<string>>();
  readonly loadMore = output();

  protected readonly titleKey = TITLE_KEY;
  protected readonly rowHeight = ROW_HEIGHT;
  protected readonly selectWidth = SELECT_WIDTH;
  protected readonly positions = EDITOR_POSITIONS;
  protected readonly scrollStrategy = createCloseScrollStrategy(this.injector);
  protected dragging = false;

  private readonly viewport = viewChild(CdkVirtualScrollViewport);
  private readonly header = viewChild<ElementRef<HTMLElement>>('header');
  private readonly editorPane = viewChild<ElementRef<HTMLElement>>('editorPane');
  private readonly propertyEditor = viewChild(PropertyEditorComponent);

  /** Width while a column edge is dragged (before `widthChange` is emitted). */
  private readonly liveWidth = signal<{ key: string; width: number } | null>(null);

  protected readonly editing = signal<EditingCell | null>(null);
  /**
   * How the draft was last changed. A choice made with the pointer is saved at once; with the
   * keyboard (arrow keys, type-ahead in a native select) only on Enter or when leaving.
   */
  protected lastInput: 'pointer' | 'keyboard' = 'keyboard';
  protected readonly draft = signal<unknown>(null);

  protected readonly totalWidth = computed(() => {
    let total = SELECT_WIDTH + this.widthOf(TITLE_KEY);
    for (const column of this.columns()) {
      total += this.widthOf(column.key);
    }
    return total;
  });

  protected readonly allSelected = computed(
    () => this.rows().length > 0 && this.rows().every((r) => this.selection().has(r.id)),
  );

  constructor() {
    effect((onCleanup) => {
      const viewport = this.viewport();
      if (viewport) {
        this.syncHeader(viewport, onCleanup);
      } else {
        // empty state: the rows are gone, a later viewport starts unscrolled
        const header = this.header()?.nativeElement;
        if (header) {
          header.scrollLeft = 0;
        }
      }
    });
    inject(DestroyRef).onDestroy(() => this.stopResize?.());
  }

  protected readonly trackById = (_: number, row: TableRecord) => row.id;

  protected widthOf(key: string): number {
    const live = this.liveWidth();
    if (live?.key === key) {
      return live.width;
    }
    return this.widths()[key] ?? (key === TITLE_KEY ? DEFAULT_TITLE_WIDTH : DEFAULT_COLUMN_WIDTH);
  }

  protected valueOf(row: TableRecord, key: string): unknown {
    return (row.properties as Record<string, unknown>)[key];
  }

  protected sortable(column: TableColumn): boolean {
    return isSortable(column.definition.type);
  }

  protected sortIndicator(key: string): string {
    const sorts = this.sorts();
    const index = sorts.findIndex((s) => s.prop === key);
    if (index < 0) {
      return '';
    }
    const arrow = sorts[index].dir === 'asc' ? '↑' : '↓';
    return sorts.length > 1 ? `${arrow}${index + 1}` : arrow;
  }

  protected ariaSort(key: string): string | null {
    const sort = this.sorts().find((s) => s.prop === key);
    return sort ? (sort.dir === 'asc' ? 'ascending' : 'descending') : null;
  }

  protected onSortClick(key: string, event: MouseEvent): void {
    if (this.dragging) {
      return;
    }
    this.sortToggle.emit({ prop: key, multi: event.shiftKey });
  }

  protected onColumnDrop(event: CdkDragDrop<unknown>): void {
    setTimeout(() => (this.dragging = false));
    if (event.previousIndex === event.currentIndex) {
      return;
    }
    const keys = this.columns().map((c) => c.key);
    const [moved] = keys.splice(event.previousIndex, 1);
    keys.splice(event.currentIndex, 0, moved);
    this.columnOrderChange.emit(keys);
  }

  private stopResize: (() => void) | null = null;

  protected startResize(key: string, event: PointerEvent): void {
    event.preventDefault();
    event.stopPropagation();
    const startX = event.clientX;
    const startWidth = this.widthOf(key);
    const move = (e: PointerEvent) =>
      this.liveWidth.set({
        key,
        width: Math.max(MIN_COLUMN_WIDTH, startWidth + e.clientX - startX),
      });
    const up = () => {
      this.stopResize?.();
      const live = this.liveWidth();
      this.liveWidth.set(null);
      if (live && live.width !== startWidth) {
        this.widthChange.emit(live);
      }
    };
    this.stopResize = () => {
      document.removeEventListener('pointermove', move);
      document.removeEventListener('pointerup', up);
      this.stopResize = null;
    };
    document.addEventListener('pointermove', move);
    document.addEventListener('pointerup', up);
  }

  protected toggleRow(id: string): void {
    const next = new Set(this.selection());
    if (!next.delete(id)) {
      next.add(id);
    }
    this.selectionChange.emit(next);
  }

  protected toggleAll(): void {
    this.selectionChange.emit(
      this.allSelected() ? new Set() : new Set(this.rows().map((r) => r.id)),
    );
  }

  // --- status of saved cells -------------------------------------------------------------------

  protected cellStatus(recordId: string, key: string): string | null {
    return this.edits()?.cell(recordId, key)?.status ?? null;
  }

  protected cellMessage(recordId: string, key: string): string | null {
    return this.edits()?.cell(recordId, key)?.message ?? null;
  }

  protected editable(row: TableRecord, definition: PropertyDefinition | null): boolean {
    return !!this.edits() && canEditCell(definition, row, this.canWrite());
  }

  // --- inline editing ---------------------------------------------------------------------------

  protected multiline(cell: EditingCell): boolean {
    return !!cell.definition && MULTILINE.includes(cell.definition.type);
  }

  /** Opens the editor of a cell (double-click, Enter) unless the cell is read-only. */
  startEdit(
    row: TableRecord,
    key: string,
    definition: PropertyDefinition | null,
    origin: HTMLElement,
  ): void {
    if (!this.editable(row, definition) || this.editing()) {
      return;
    }
    const initial = key === TITLE_KEY ? row.title : this.valueOf(row, key);
    // a value the server rejected is offered again, so the user can correct it
    const failed = this.edits()?.cell(row.id, key);
    this.draft.set(
      failed?.status === 'error' && failed.draft !== undefined ? failed.draft : (initial ?? null),
    );
    this.lastInput = 'keyboard';
    this.editing.set({ record: row, key, definition, initial: initial ?? null, origin });
  }

  protected onDraft(value: unknown): void {
    this.draft.set(value);
    const definition = this.editing()?.definition;
    if (definition && COMMIT_ON_CHANGE.includes(definition.type) && this.lastInput === 'pointer') {
      this.commit();
    }
  }

  /** Escape: closes the editor without saving. */
  cancel(): void {
    const cell = this.editing();
    this.editing.set(null);
    this.refocus(cell);
  }

  /** Enter, leaving the editor, scrolling: saves the draft if it changed. */
  commit(): void {
    const cell = this.editing();
    if (!cell) {
      return;
    }
    this.propertyEditor()?.flush(); // Markdown editors report with a delay
    const value = this.draft();
    this.editing.set(null);
    const edits = this.edits();
    if (edits && !sameJson(value, cell.initial)) {
      void edits.save(cell.record, cell.key, value);
    }
    this.refocus(cell);
  }

  protected onEditorKey(event: KeyboardEvent): void {
    const cell = this.editing();
    if (!cell) {
      return;
    }
    if (event.key !== 'Enter') {
      this.lastInput = 'keyboard';
    }
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      this.cancel();
      return;
    }
    if (event.key === 'Enter' && !event.shiftKey) {
      if (this.multiline(cell) && !(event.ctrlKey || event.metaKey)) {
        return;
      }
      event.preventDefault();
      this.commit();
    }
  }

  protected onEditorFocusOut(event: FocusEvent): void {
    const next = event.relatedTarget as Node | null;
    const pane = this.editorPane()?.nativeElement;
    if (next && pane && !pane.contains(next)) {
      this.commit();
    }
  }

  protected onOutsideClick(event: MouseEvent): void {
    if (this.editing()?.origin.contains(event.target as Node)) {
      return;
    }
    this.commit();
  }

  protected focusEditor(): void {
    afterNextRender(
      () => {
        const target = this.editorPane()?.nativeElement.querySelector<HTMLElement>(
          'input, select, textarea, [contenteditable="true"]',
        );
        target?.focus();
      },
      { injector: this.injector },
    );
  }

  private refocus(cell: EditingCell | null): void {
    if (cell?.origin.isConnected) {
      cell.origin.focus();
    }
  }

  // --- scrolling --------------------------------------------------------------------------------

  protected checkLoadMore(): void {
    const viewport = this.viewport();
    // after a failed page only the button retries, not every scroll step
    if (!viewport || !this.hasMore() || this.loadingMore() || this.moreError()) {
      return;
    }
    if (viewport.getRenderedRange().end >= this.rows().length - LOAD_AHEAD_ROWS) {
      this.loadMore.emit();
    }
  }

  /** Keeps the header in step with the horizontal scroll of the rows; loads ahead. */
  private syncHeader(
    viewport: CdkVirtualScrollViewport,
    onCleanup: (fn: () => void) => void,
  ): void {
    const sync = () => {
      const header = this.header()?.nativeElement;
      if (header) {
        header.scrollLeft = viewport.measureScrollOffset('left');
      }
    };
    sync(); // a re-created viewport starts at 0, the header may still be scrolled
    const scrolled = viewport.elementScrolled().subscribe(sync);
    // the next page is requested when the rendered rows approach the end (also on first render)
    const range = viewport.renderedRangeStream.subscribe(() => this.checkLoadMore());
    onCleanup(() => {
      scrolled.unsubscribe();
      range.unsubscribe();
    });
  }
}
