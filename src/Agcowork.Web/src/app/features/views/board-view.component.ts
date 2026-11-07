import { CdkDrag, CdkDragDrop, CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
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
import { RouterLink } from '@angular/router';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { sameFilter } from '../../shared/filter/filter-dsl';
import { PropertyDefinition, asText } from '../../shared/properties/property-types';
import { CellValueComponent } from '../../shared/table/cell-value.component';
import { canEditCell } from '../../shared/table/inline-edit';
import { ConflictDialogComponent } from '../records/conflict-dialog.component';
import type { RecordDto } from '../records/record-save.service';
import type { ViewDto } from './collection.store';
import { InlineEditService } from './inline-edit.service';
import { QuerySource, createQueryPager } from './query-pager';
import { ViewWorkingState, tableColumns, viewQuery } from './view-state';

/** The board loads one query page of this size and groups it in the browser. */
export const BOARD_LIMIT = 500;
const CARD_FIELDS = 3;

const STATUS_GROUP_LABELS: Readonly<Record<string, string>> = {
  todo: 'Zu erledigen',
  in_progress: 'In Arbeit',
  done: 'Abgeschlossen',
};

/** One board column: the records with one option value (`null`: without a value). */
export interface BoardColumn {
  readonly value: string | null;
  readonly label: string;
  /** Status group (`todo`, `in_progress`, `done`) when the column starts a new group. */
  readonly groupStart: string | null;
  readonly records: readonly RecordDto[];
}

/**
 * Groups records by the value of `definition` in the order of its options, plus a column for
 * records without a (known) value. Status columns mark where a new status group begins.
 */
export function groupRecords(
  records: readonly RecordDto[],
  definition: PropertyDefinition,
): BoardColumn[] {
  const options = definition.options ?? [];
  const buckets = new Map<string | null, RecordDto[]>(options.map((o) => [o.value, []]));
  buckets.set(null, []);
  for (const record of records) {
    const value = asText((record.properties as Record<string, unknown>)[definition.key]);
    (buckets.get(value) ?? buckets.get(null)!).push(record);
  }
  let lastGroup: string | null = null;
  const columns: BoardColumn[] = options.map((option) => {
    const group = definition.type === 'status' ? (option.group ?? null) : null;
    const groupStart = group !== null && group !== lastGroup ? group : null;
    lastGroup = group;
    return {
      value: option.value,
      label: option.label,
      groupStart,
      records: buckets.get(option.value)!,
    };
  });
  return [
    { value: null, label: 'Ohne Wert', groupStart: null, records: buckets.get(null)! },
    ...columns,
  ];
}

/**
 * A view rendered as a board: columns are the options of the view's `groupBy` (select/status),
 * cards show the title and up to three properties. Dragging a card into another column saves the
 * new value at once (optimistic, rolled back when the save fails).
 */
@Component({
  selector: 'ag-board-view',
  imports: [
    CdkDrag,
    CdkDropList,
    CdkDropListGroup,
    CellValueComponent,
    ConflictDialogComponent,
    RouterLink,
  ],
  providers: [InlineEditService],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (!groupDefinition()) {
      <p
        class="rounded-md border border-border p-4 text-sm text-muted"
        data-testid="board-no-group"
      >
        Dieses Board hat keine gültige Gruppierung (select- oder status-Property).
        @if (canWrite() && groupChoices().length > 0) {
          <select
            class="ml-2 rounded-md border border-border bg-bg px-2 py-1"
            data-testid="board-group-select"
            aria-label="Gruppieren nach"
            (change)="groupByChange.emit($any($event.target).value)"
          >
            <option value="">Gruppieren nach …</option>
            @for (choice of groupChoices(); track choice.key) {
              <option [value]="choice.key">{{ choice.name }}</option>
            }
          </select>
        }
      </p>
    } @else {
      @if (pager.error(); as message) {
        <p class="text-sm text-danger" role="alert" data-testid="board-error">{{ message }}</p>
      } @else if (pager.isLoading() && pager.items().length === 0) {
        <p class="text-sm text-muted">Wird geladen …</p>
      }
      @if (pager.hasMore()) {
        <p class="mb-2 text-xs text-muted" data-testid="board-truncated">
          Es werden die ersten {{ limit }} von
          {{ (pager.count() ?? 0) >= 10000 ? 'mehr als 10 000' : pager.count() }} Records gezeigt.
          Ein Filter grenzt die Auswahl ein.
        </p>
      }
      @if (moveError(); as message) {
        <p class="mb-2 text-sm text-danger" role="alert" data-testid="board-move-error">
          {{ message }}
        </p>
      }
      <div class="flex gap-3 overflow-x-auto pb-4" cdkDropListGroup data-testid="board">
        @for (column of columns(); track column.value) {
          @if (column.groupStart) {
            <div
              class="flex w-6 shrink-0 items-center justify-start pt-2 text-xs font-medium text-muted [writing-mode:vertical-rl]"
              data-testid="board-group"
            >
              {{ groupLabel(column.groupStart) }}
            </div>
          }
          <section
            class="flex w-72 shrink-0 flex-col rounded-md border border-border bg-surface"
            data-testid="board-column"
            [attr.data-value]="column.value ?? ''"
          >
            <h3
              class="flex items-center justify-between border-b border-border px-3 py-2 text-sm font-medium"
            >
              <span>{{ column.label }}</span>
              <span class="text-xs text-muted" data-testid="board-column-count">{{
                column.records.length
              }}</span>
            </h3>
            <div
              class="flex min-h-24 flex-1 flex-col gap-2 p-2"
              cdkDropList
              [cdkDropListData]="column.value"
              [cdkDropListDisabled]="!canWrite()"
              (cdkDropListDropped)="onDrop($event)"
            >
              @for (record of column.records; track record.id) {
                <article
                  class="cursor-grab rounded-md border border-border bg-bg p-2 text-sm"
                  cdkDrag
                  data-testid="board-card"
                  [attr.data-id]="record.id"
                  [cdkDragData]="record"
                  [cdkDragDisabled]="!canMove(record)"
                >
                  <a
                    class="font-medium text-accent hover:underline"
                    data-testid="board-card-link"
                    [routerLink]="['/w', context.slug(), 'records', record.id]"
                    >{{ record.title || 'Ohne Titel' }}</a
                  >
                  @for (field of fieldsOf(record); track field.key) {
                    <div class="mt-1 flex gap-2 text-xs">
                      <span class="shrink-0 text-muted">{{ field.name }}</span>
                      <ag-cell-value [definition]="field" [value]="valueOf(record, field.key)" />
                    </div>
                  }
                </article>
              }
            </div>
          </section>
        }
      </div>
    }

    @if (edits.conflict(); as open) {
      <ag-conflict-dialog
        [conflict]="open.conflict"
        [saving]="edits.resolving()"
        (reload)="edits.reloadConflict()"
        (overwrite)="edits.overwriteConflict()"
      />
    }
  `,
})
export class BoardViewComponent {
  protected readonly context = inject(WorkspaceContext);
  protected readonly edits = inject(InlineEditService);

  readonly view = input.required<ViewDto>();
  readonly state = input.required<ViewWorkingState>();
  readonly schema = input.required<readonly PropertyDefinition[]>();
  readonly canWrite = input(false);
  readonly groupByChange = output<string>();

  protected readonly limit = BOARD_LIMIT;
  protected readonly moveError = signal<string | null>(null);

  protected readonly groupDefinition = computed(() => {
    const key = this.view().groupBy;
    const definition = this.schema().find((d) => d.key === key);
    return definition && (definition.type === 'select' || definition.type === 'status')
      ? definition
      : null;
  });
  protected readonly groupChoices = computed(() =>
    this.schema().filter((d) => d.type === 'select' || d.type === 'status'),
  );

  /** Candidate card fields: the visible columns without the grouping property. */
  protected readonly cardFields = computed(() => {
    const groupKey = this.groupDefinition()?.key;
    return tableColumns(this.schema(), this.state().visible)
      .map((c) => c.definition)
      .filter((d) => d.key !== groupKey);
  });

  /** The first three card fields that have a value on this record. */
  protected fieldsOf(record: RecordDto): PropertyDefinition[] {
    return this.cardFields()
      .filter((field) => this.hasValue(record, field.key))
      .slice(0, CARD_FIELDS);
  }

  private readonly source = computed<QuerySource | undefined>(
    () => {
      const slug = this.context.slug();
      return slug && this.groupDefinition()
        ? { slug, query: viewQuery(this.view(), this.state(), BOARD_LIMIT, true) }
        : undefined;
    },
    { equal: sameFilter },
  );

  protected readonly pager = createQueryPager(() => this.source());

  protected readonly columns = computed(() => {
    const definition = this.groupDefinition();
    return definition ? groupRecords(this.pager.items(), definition) : [];
  });

  constructor() {
    this.edits.bind(() => this.context.slug(), this.pager);
    effect(() => {
      this.source();
      untracked(() => {
        this.edits.reset();
        this.moveError.set(null);
      });
    });
  }

  protected groupLabel(group: string): string {
    return STATUS_GROUP_LABELS[group] ?? group;
  }

  protected valueOf(record: RecordDto, key: string): unknown {
    return (record.properties as Record<string, unknown>)[key];
  }

  protected hasValue(record: RecordDto, key: string): boolean {
    const value = this.valueOf(record, key);
    return (
      value !== null &&
      value !== undefined &&
      value !== '' &&
      !(Array.isArray(value) && value.length === 0)
    );
  }

  protected canMove(record: RecordDto): boolean {
    return canEditCell(this.groupDefinition(), record, this.canWrite());
  }

  /** A card dropped into another column: save its new value, roll back on failure. */
  async onDrop(event: CdkDragDrop<string | null, string | null, RecordDto>): Promise<void> {
    const definition = this.groupDefinition();
    if (!definition || event.previousContainer === event.container) {
      return;
    }
    await this.move(event.item.data, event.container.data);
  }

  /** Moves a card to the column of `value` (`null`: without a value). */
  async move(record: RecordDto, value: string | null): Promise<boolean> {
    const definition = this.groupDefinition();
    if (!definition || !this.canMove(record)) {
      return false;
    }
    this.moveError.set(null);
    const saved = await this.edits.save(record, definition.key, value, { rollback: true });
    if (!saved && !this.edits.conflict()) {
      const message = this.edits.cell(record.id, definition.key)?.message;
      this.moveError.set(
        `„${record.title}“ konnte nicht verschoben werden${message ? `: ${message}` : '.'}`,
      );
    }
    return saved;
  }
}
