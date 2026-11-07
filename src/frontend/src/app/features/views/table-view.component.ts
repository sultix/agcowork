import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { HlmButton } from '@spartan-ng/helm/button';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { sameFilter } from '../../shared/filter/filter-dsl';
import { toggleSort } from '../../shared/filter/sort-chips.component';
import type { PropertyDefinition } from '../../shared/properties/property-types';
import { DataTableComponent } from '../../shared/table/data-table.component';

import { describeError } from '../../shared/ui/api-error-text';
import { ConfirmDialogComponent } from '../../shared/ui/confirm-dialog.component';
import { ConflictDialogComponent } from '../records/conflict-dialog.component';
import type { ViewDto } from './collection.store';
import { InlineEditService } from './inline-edit.service';
import { QuerySource, createQueryPager } from './query-pager';
import { ViewWorkingState, normaliseVisible, tableColumns, viewQuery } from './view-state';

/** Page size of the table; 5 000 rows are 25 pages. */
export const TABLE_PAGE_SIZE = 200;

/**
 * A view rendered as a table: loads the records page by page through the query endpoints,
 * hands column, sort and width changes up as a new working state, saves inline edits and
 * archives selected records.
 */
@Component({
  selector: 'ag-table-view',
  imports: [ConfirmDialogComponent, ConflictDialogComponent, DataTableComponent, HlmButton],
  providers: [InlineEditService],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (selection().size > 0) {
      <div
        class="mb-2 flex items-center gap-3 rounded-lg border bg-card px-3 py-2 text-sm"
        data-testid="selection-bar"
      >
        <span>{{ selection().size }} ausgewählt</span>
        @if (canWrite()) {
          <button
            type="button"
            hlmBtn
            variant="destructive"
            size="sm"
            data-testid="archive-selected"
            (click)="confirmArchive.set(true)"
          >
            Archivieren
          </button>
        }
        <button
          type="button"
          hlmBtn
          variant="ghost"
          size="sm"
          data-testid="selection-clear"
          (click)="selection.set(emptySelection)"
        >
          Auswahl aufheben
        </button>
        @if (archiveError(); as message) {
          <span class="text-destructive" role="alert">{{ message }}</span>
        }
      </div>
    }
    @if (pager.count(); as count) {
      <p class="mb-1 text-xs text-muted-foreground" data-testid="match-count">
        {{ count >= 10000 ? 'mehr als 10 000' : count }} Treffer
      </p>
    }
    <ag-data-table
      [rows]="pager.items()"
      [columns]="columns()"
      [widths]="state().widths"
      [sorts]="state().sorts"
      [titleName]="titleName()"
      [canWrite]="canWrite()"
      [linkBase]="linkBase()"
      [edits]="edits"
      [selection]="selection()"
      [loading]="pager.isLoading()"
      [loadingMore]="pager.loadingMore()"
      [hasMore]="pager.hasMore()"
      [error]="pager.error()"
      [moreError]="pager.moreError()"
      [emptyText]="state().filter ? 'Keine Treffer für diesen Filter.' : 'Noch keine Records.'"
      (sortToggle)="onSort($event.prop, $event.multi)"
      (widthChange)="onWidth($event.key, $event.width)"
      (columnOrderChange)="onOrder($event)"
      (selectionChange)="selection.set($event)"
      (loadMore)="pager.loadMore()"
    />

    @if (edits.conflict(); as open) {
      <ag-conflict-dialog
        [conflict]="open.conflict"
        [saving]="edits.resolving()"
        (reload)="edits.reloadConflict()"
        (overwrite)="edits.overwriteConflict()"
      />
    }
    <ag-confirm-dialog
      [open]="confirmArchive()"
      title="Records archivieren?"
      [message]="selection().size + ' Records werden archiviert und sind dann schreibgeschützt.'"
      confirmLabel="Archivieren"
      [busy]="archiving()"
      (confirmed)="archiveSelected()"
      (cancelled)="confirmArchive.set(false)"
    />
  `,
})
export class TableViewComponent {
  private readonly api = inject(ApiClient);
  private readonly context = inject(WorkspaceContext);
  protected readonly edits = inject(InlineEditService);

  readonly view = input.required<ViewDto>();
  readonly state = input.required<ViewWorkingState>();
  readonly schema = input.required<readonly PropertyDefinition[]>();
  readonly canWrite = input(false);
  readonly stateChange = output<ViewWorkingState>();

  protected readonly emptySelection: ReadonlySet<string> = new Set();
  protected readonly selection = signal<ReadonlySet<string>>(this.emptySelection);
  protected readonly confirmArchive = signal(false);
  protected readonly archiving = signal(false);
  protected readonly archiveError = signal<string | null>(null);

  protected readonly columns = computed(() => tableColumns(this.schema(), this.state().visible));
  protected readonly titleName = computed(
    () => this.schema().find((d) => d.key === 'title')?.name ?? 'Titel',
  );
  protected readonly linkBase = computed(() => ['/w', this.context.slug() ?? '', 'records']);

  /** Only filter, sorts and columns decide the query; widths and the like must not reload. */
  private readonly source = computed<QuerySource | undefined>(
    () => {
      const slug = this.context.slug();
      return slug
        ? { slug, query: viewQuery(this.view(), this.state(), TABLE_PAGE_SIZE) }
        : undefined;
    },
    { equal: sameFilter },
  );

  protected readonly pager = createQueryPager(() => this.source());

  constructor() {
    this.edits.bind(() => this.context.slug(), this.pager);
    effect(() => {
      this.source();
      untracked(() => {
        this.selection.set(this.emptySelection);
        this.edits.reset();
      });
    });
  }

  protected onSort(prop: string, multi: boolean): void {
    this.emit({ sorts: toggleSort(this.state().sorts, prop, multi) });
  }

  protected onWidth(key: string, width: number): void {
    this.emit({ widths: { ...this.state().widths, [key]: width } });
  }

  protected onOrder(keys: string[]): void {
    this.emit({ visible: normaliseVisible(this.schema(), keys) });
  }

  protected async archiveSelected(): Promise<void> {
    const slug = this.context.slug();
    if (!slug) {
      return;
    }
    this.archiving.set(true);
    this.archiveError.set(null);
    const client = this.api.workspace(slug);
    const failed = new Set<string>();
    let lastError: unknown = null;
    for (const recordId of this.selection()) {
      try {
        await firstValueFrom(client.post('/records/{recordId}/archive', { path: { recordId } }));
      } catch (error) {
        failed.add(recordId);
        lastError = error;
      }
    }
    this.archiving.set(false);
    this.confirmArchive.set(false);
    this.selection.set(failed); // what could not be archived stays selected
    if (failed.size > 0) {
      this.archiveError.set(`${failed.size} nicht archiviert: ${describeError(lastError)}`);
    }
    this.pager.reload();
  }

  private emit(change: Partial<ViewWorkingState>): void {
    this.stateChange.emit({ ...this.state(), ...change });
  }
}
