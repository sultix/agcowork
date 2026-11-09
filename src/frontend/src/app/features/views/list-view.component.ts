import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCircleAlert } from '@ng-icons/lucide';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { RecordTitleStore, relationIdsOf } from '../../core/records/record-title.store';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { sameFilter } from '../../shared/filter/filter-dsl';
import { PropertyDefinition } from '../../shared/properties/property-types';
import { CellValueComponent } from '../../shared/table/cell-value.component';
import type { RecordDto } from '../records/record-save.service';
import { fieldsWithValue, nextIndex, propertyValue } from './card-view';
import type { ViewDto } from './collection.store';
import { liveRefresh } from './live-refresh';
import { QuerySource, createQueryPager } from './query-pager';
import { ViewWorkingState, tableColumns, viewQuery } from './view-state';

/** Records per page of a list (more are loaded on demand). */
export const LIST_LIMIT = 100;
/** Properties shown right of the title; the rest is left out. */
export const LIST_FIELDS = 6;

/**
 * A view rendered as a compact list: one row per record with the title as a link on the left and
 * the visible properties as short values on the right (wrapping on narrow screens). Arrow keys,
 * Home/End move between the title links, Enter opens. Changes by others reload the rows (see
 * {@link liveRefresh}). Read-only: no inline editing.
 */
@Component({
  selector: 'ag-list-view',
  imports: [CellValueComponent, HlmAlertImports, HlmButton, HlmSkeleton, NgIcon, RouterLink],
  providers: [provideIcons({ lucideCircleAlert })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(keydown)': 'onKeydown($event)' },
  template: `
    @if (pager.error(); as message) {
      <div hlmAlert variant="destructive" data-testid="list-error">
        <ng-icon name="lucideCircleAlert" />
        <p hlmAlertDescription>{{ message }}</p>
      </div>
    } @else if (pager.isLoading() && pager.items().length === 0) {
      <div class="flex flex-col gap-2" data-testid="list-loading">
        <hlm-skeleton class="h-9 w-full" />
        <hlm-skeleton class="h-9 w-full" />
        <hlm-skeleton class="h-9 w-full" />
      </div>
    } @else if (pager.items().length === 0) {
      <p
        class="rounded-xl border border-dashed p-4 text-sm text-muted-foreground"
        data-testid="list-empty"
      >
        {{ state().filter ? 'Keine Treffer für diesen Filter.' : 'Noch keine Records.' }}
      </p>
    }
    @if (pager.count(); as count) {
      <p class="mb-1 text-xs text-muted-foreground" data-testid="match-count">
        {{ count >= 10000 ? 'mehr als 10 000' : count }} Treffer
      </p>
    }
    @if (pager.items().length > 0) {
      <ul
        role="list"
        class="divide-y rounded-xl border bg-card text-card-foreground"
        aria-label="Liste"
        data-testid="list"
      >
        @for (record of pager.items(); track record.id) {
          <li
            class="relative flex cursor-pointer flex-wrap items-center gap-x-4 gap-y-1 px-3 py-2 text-sm hover:bg-muted/50 focus-within:bg-muted/50"
            data-testid="list-row"
            [attr.data-id]="record.id"
          >
            <a
              class="min-w-0 max-w-full shrink-0 font-medium after:absolute after:inset-0 underline-offset-4 outline-none hover:underline focus-visible:underline focus-visible:ring-3 focus-visible:ring-ring/50 sm:w-64 sm:truncate"
              data-testid="list-row-link"
              data-nav
              [tabindex]="record.id === tabId() ? 0 : -1"
              [routerLink]="['/w', context.slug(), 'records', record.id]"
              (focus)="activeId.set(record.id)"
              >{{ record.title || 'Ohne Titel' }}</a
            >
            <div
              class="flex min-w-0 flex-1 flex-wrap gap-x-4 gap-y-1 text-xs [&_a]:relative [&_a]:z-10"
            >
              @for (field of fieldsOf(record); track field.key) {
                <span class="flex min-w-0 gap-1.5">
                  <span class="shrink-0 text-muted-foreground">{{ field.name }}</span>
                  <ag-cell-value
                    chipNavigation="link"
                    [definition]="field"
                    [value]="valueOf(record, field.key)"
                  />
                </span>
              }
            </div>
          </li>
        }
      </ul>
    }
    @if (pager.moreError(); as message) {
      <p class="mt-2 text-sm text-destructive" role="alert" data-testid="list-more-error">
        {{ message }}
      </p>
    }
    @if (pager.hasMore()) {
      <div class="mt-3 flex justify-center">
        <button
          type="button"
          hlmBtn
          variant="outline"
          size="sm"
          data-testid="list-load-more"
          [disabled]="pager.loadingMore()"
          (click)="pager.loadMore()"
        >
          {{ pager.loadingMore() ? 'Lädt …' : 'Mehr laden' }}
        </button>
      </div>
    }
  `,
})
export class ListViewComponent {
  protected readonly context = inject(WorkspaceContext);
  private readonly titles = inject(RecordTitleStore);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly view = input.required<ViewDto>();
  readonly state = input.required<ViewWorkingState>();
  readonly schema = input.required<readonly PropertyDefinition[]>();

  /** Record whose title link is in the tab order (roving focus). */
  protected readonly activeId = signal<string | null>(null);

  private readonly rowFields = computed(() =>
    tableColumns(this.schema(), this.state().visible).map((c) => c.definition),
  );

  private readonly source = computed<QuerySource | undefined>(
    () => {
      const slug = this.context.slug();
      return slug ? { slug, query: viewQuery(this.view(), this.state(), LIST_LIMIT) } : undefined;
    },
    { equal: sameFilter },
  );

  protected readonly pager = createQueryPager(() => this.source());

  /** The tab stop: the remembered record, or the first one when it is not loaded (any more). */
  protected readonly tabId = computed(() => {
    const items = this.pager.items();
    const id = this.activeId();
    return items.some((r) => r.id === id) ? id : (items[0]?.id ?? null);
  });

  constructor() {
    effect(() => this.titles.prefetch(relationIdsOf(this.pager.items(), this.rowFields())));
    liveRefresh({
      collectionId: () => this.view().collectionId,
      blocked: () => false,
      generation: () => 0,
      refresh: (canApply) => this.pager.refresh(canApply),
      holdSchema: true,
    });
  }

  protected fieldsOf(record: RecordDto): PropertyDefinition[] {
    return fieldsWithValue(record, this.rowFields(), LIST_FIELDS);
  }

  protected valueOf(record: RecordDto, key: string): unknown {
    return propertyValue(record, key);
  }

  protected onKeydown(event: KeyboardEvent): void {
    const links = Array.from(this.host.nativeElement.querySelectorAll<HTMLElement>('a[data-nav]'));
    const index = links.indexOf(event.target as HTMLElement);
    if (index === -1) {
      return;
    }
    const target = nextIndex(event.key, index, links.length, 1, false);
    if (target !== null) {
      event.preventDefault();
      this.activeId.set(this.pager.items()[target]?.id ?? null);
      links[target].focus();
    }
  }
}
