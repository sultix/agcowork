import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  linkedSignal,
  signal,
  untracked,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCircleAlert } from '@ng-icons/lucide';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCheckbox } from '@spartan-ng/helm/checkbox';
import { HlmLabel } from '@spartan-ng/helm/label';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { FilterBuilderComponent } from '../../shared/filter/filter-builder.component';
import { FilterNode, SortSpec, countConditions } from '../../shared/filter/filter-dsl';
import { SortChipsComponent } from '../../shared/filter/sort-chips.component';
import { describeError } from '../../shared/ui/api-error-text';
import { BoardViewComponent } from './board-view.component';
import { CollectionStore, VIEW_TYPE_LABELS } from './collection.store';
import { TableViewComponent } from './table-view.component';
import {
  ViewWorkingState,
  columnDefinitions,
  isModified,
  loadSessionState,
  savedState,
  storeSessionState,
  tableColumns,
  toViewPatch,
  toggleColumn,
} from './view-state';

/**
 * `/w/:slug/collections/:collectionId/views/:viewId`: one view of the collection with its filter
 * bar (filter builder, sort chips, columns) and the table or board. Changes to filter, sorting and
 * columns stay in the session until "View speichern" (`write` only) stores them in the view.
 */
@Component({
  selector: 'ag-view-page',
  imports: [
    BoardViewComponent,
    FilterBuilderComponent,
    HlmAlertImports,
    HlmBadge,
    HlmButton,
    HlmCheckbox,
    HlmLabel,
    NgIcon,
    RouterLink,
    SortChipsComponent,
    TableViewComponent,
  ],
  providers: [provideIcons({ lucideCircleAlert })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (view(); as view) {
      @if (state(); as state) {
        <div class="mt-3 flex flex-wrap items-center gap-2 text-sm" data-testid="view-toolbar">
          <button
            type="button"
            hlmBtn
            variant="outline"
            size="sm"
            data-testid="filter-toggle"
            [attr.aria-expanded]="filterOpen()"
            (click)="filterOpen.set(!filterOpen())"
          >
            Filter
            @if (conditionCount() > 0) {
              <span hlmBadge class="h-4 min-w-4 px-1">{{ conditionCount() }}</span>
            }
          </button>
          @if (isTable()) {
            <button
              type="button"
              hlmBtn
              variant="outline"
              size="sm"
              data-testid="columns-toggle"
              [attr.aria-expanded]="columnsOpen()"
              (click)="columnsOpen.set(!columnsOpen())"
            >
              Spalten
            </button>
          }
          <ag-sort-chips
            [sorts]="state.sorts"
            [schema]="store.schema()"
            (sortsChange)="setSorts($event)"
          />
          @if (modified()) {
            <span class="ml-auto text-xs text-muted-foreground" data-testid="view-modified"
              >Geändert (nur in dieser Sitzung)</span
            >
            <button
              type="button"
              hlmBtn
              variant="outline"
              size="sm"
              data-testid="view-reset"
              (click)="reset()"
            >
              Zurücksetzen
            </button>
            @if (store.canWrite()) {
              <button
                type="button"
                hlmBtn
                size="sm"
                data-testid="view-save"
                [disabled]="saving()"
                (click)="save()"
              >
                View speichern
              </button>
            }
          }
        </div>
        @if (saveError(); as message) {
          <div hlmAlert variant="destructive" class="mt-2" data-testid="view-save-error">
            <ng-icon name="lucideCircleAlert" />
            <p hlmAlertDescription class="whitespace-pre-line">{{ message }}</p>
          </div>
        }
        @if (filterOpen()) {
          <ag-filter-builder
            class="mt-2 block"
            [schema]="store.schema()"
            [filter]="state.filter"
            (filterChange)="setFilter($event)"
          />
        }
        @if (columnsOpen() && isTable()) {
          <fieldset
            class="mt-2 flex flex-wrap gap-3 rounded-xl border bg-card p-3 text-sm text-card-foreground"
            data-testid="columns-panel"
          >
            <legend class="px-1 text-muted-foreground">Sichtbare Spalten</legend>
            @for (definition of columnChoices(); track definition.key) {
              <label hlmLabel class="gap-1.5 font-normal" [for]="'column-' + definition.key">
                <hlm-checkbox
                  [inputId]="'column-' + definition.key"
                  [attr.data-key]="definition.key"
                  [checked]="visibleKeys().includes(definition.key)"
                  (checkedChange)="toggleColumn(definition.key)"
                />
                {{ definition.name }}
              </label>
            }
          </fieldset>
        }
        @if (typeHint(); as hint) {
          <p class="mt-2 text-xs text-muted-foreground" data-testid="view-type-hint">{{ hint }}</p>
        }
        <div class="mt-3">
          @if (view.type === 'board') {
            <ag-board-view
              [view]="view"
              [state]="state"
              [schema]="store.schema()"
              [canWrite]="store.canWrite()"
              (groupByChange)="setGroupBy($event)"
            />
          } @else {
            <ag-table-view
              [view]="view"
              [state]="state"
              [schema]="store.schema()"
              [canWrite]="store.canWrite()"
              (stateChange)="workingState.set($event)"
            />
          }
        </div>
      }
    } @else if (store.viewsLoaded()) {
      <p class="mt-4 text-sm text-muted-foreground" data-testid="view-missing">
        Diese View gibt es nicht (mehr).
        @if (store.defaultView(); as fallback) {
          <a
            class="font-medium underline underline-offset-4"
            [routerLink]="[
              '/w',
              context.slug(),
              'collections',
              fallback.collectionId,
              'views',
              fallback.id,
            ]"
            >Zur Standard-View</a
          >
        }
      </p>
    }
  `,
})
export class ViewPageComponent {
  protected readonly store = inject(CollectionStore);
  protected readonly context = inject(WorkspaceContext);

  /** Route parameter `:viewId`. */
  readonly viewId = input.required<string>();

  protected readonly view = computed(() => this.store.find(this.viewId()));

  /** Working state; starts from the session (same tab) or the stored view when the view changes. */
  protected readonly workingState = linkedSignal<string | null, ViewWorkingState | null>({
    source: () => this.view()?.id ?? null,
    computation: (id) => {
      const view = untracked(() => this.view());
      return id && view ? (loadSessionState(id) ?? savedState(view)) : null;
    },
  });
  protected readonly state = this.workingState.asReadonly();

  protected readonly filterOpen = signal(false);
  protected readonly columnsOpen = signal(false);
  protected readonly saving = signal(false);
  protected readonly saveError = signal<string | null>(null);

  protected readonly modified = computed(() => {
    const view = this.view();
    const state = this.state();
    return !!view && !!state && isModified(state, view);
  });
  protected readonly conditionCount = computed(() => countConditions(this.state()?.filter ?? null));
  protected readonly isTable = computed(() => this.view()?.type !== 'board');
  protected readonly columnChoices = computed(() => columnDefinitions(this.store.schema()));
  protected readonly visibleKeys = computed(() =>
    tableColumns(this.store.schema(), this.state()?.visible ?? []).map((c) => c.key),
  );
  protected readonly typeHint = computed(() => {
    const type = this.view()?.type;
    return type && type !== 'table' && type !== 'board'
      ? `Die Darstellung „${VIEW_TYPE_LABELS[type] ?? type}“ gibt es noch nicht; die View wird als Tabelle gezeigt.`
      : null;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      if (this.store.activeViewId() === this.viewId()) {
        this.store.activeViewId.set(null);
      }
    });
    effect(() => {
      const view = this.view();
      const state = this.state();
      if (!view || !state) {
        return;
      }
      const keep = isModified(state, view) || Object.keys(state.widths).length > 0;
      storeSessionState(view.id, keep ? state : null);
    });
    effect(() => {
      const viewId = this.viewId();
      untracked(() => {
        this.store.activeViewId.set(viewId);
        this.saveError.set(null);
        this.columnsOpen.set(false);
      });
    });
  }

  protected setFilter(filter: FilterNode | null): void {
    this.update({ filter });
  }

  protected setSorts(sorts: SortSpec[]): void {
    this.update({ sorts });
  }

  protected toggleColumn(key: string): void {
    const state = this.state();
    if (state) {
      this.update({ visible: toggleColumn(this.store.schema(), state.visible, key) });
    }
  }

  protected reset(): void {
    const view = this.view();
    const state = this.state();
    if (view && state) {
      this.workingState.set({ ...savedState(view), widths: state.widths });
      this.saveError.set(null);
    }
  }

  /** "View speichern": stores filter, sorting and columns in the view (`PATCH /views/{id}`). */
  async save(): Promise<void> {
    const view = this.view();
    const state = this.state();
    if (!view || !state || !this.store.canWrite()) {
      return;
    }
    this.saving.set(true);
    this.saveError.set(null);
    try {
      await this.store.update(view.id, toViewPatch(state));
    } catch (error) {
      this.saveError.set(describeError(error));
    } finally {
      this.saving.set(false);
    }
  }

  protected async setGroupBy(key: string): Promise<void> {
    const view = this.view();
    if (!view || key === '') {
      return;
    }
    try {
      await this.store.update(view.id, { groupBy: key });
    } catch (error) {
      this.saveError.set(describeError(error));
    }
  }

  private update(change: Partial<ViewWorkingState>): void {
    this.workingState.update((state) => (state ? { ...state, ...change } : state));
  }
}
