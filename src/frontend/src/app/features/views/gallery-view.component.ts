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
import { CardCoverComponent } from './card-cover.component';
import { fieldsWithValue, gridColumns, isCoverType, nextIndex, propertyValue } from './card-view';
import type { ViewDto } from './collection.store';
import { liveRefresh } from './live-refresh';
import { QuerySource, createQueryPager } from './query-pager';
import { ViewWorkingState, tableColumns, viewQuery } from './view-state';

/** Records per page of a gallery (more are loaded on demand). */
export const GALLERY_LIMIT = 60;
/** Properties shown below the title of a card; the rest is left out. */
export const GALLERY_FIELDS = 5;

/**
 * A view rendered as a gallery: a responsive grid of cards with a cover built from the
 * `cardCoverProperty` (no images), the title as a link and the visible properties. The whole card is
 * the click target of the title link (stretched link); the title links form a roving-focus grid (arrow keys, Home/End, Enter).
 * Changes by others reload the cards (see {@link liveRefresh}). Read-only: no inline editing.
 */
@Component({
  selector: 'ag-gallery-view',
  imports: [
    CardCoverComponent,
    CellValueComponent,
    HlmAlertImports,
    HlmButton,
    HlmSkeleton,
    NgIcon,
    RouterLink,
  ],
  providers: [provideIcons({ lucideCircleAlert })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(keydown)': 'onKeydown($event)' },
  template: `
    @if (pager.error(); as message) {
      <div hlmAlert variant="destructive" data-testid="gallery-error">
        <ng-icon name="lucideCircleAlert" />
        <p hlmAlertDescription>{{ message }}</p>
      </div>
    } @else if (pager.isLoading() && pager.items().length === 0) {
      <div
        class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4"
        data-testid="gallery-loading"
      >
        <hlm-skeleton class="h-48" />
        <hlm-skeleton class="h-48" />
        <hlm-skeleton class="h-48" />
        <hlm-skeleton class="h-48" />
      </div>
    } @else if (pager.items().length === 0) {
      <p
        class="rounded-xl border border-dashed p-4 text-sm text-muted-foreground"
        data-testid="gallery-empty"
      >
        {{ state().filter ? 'Keine Treffer für diesen Filter.' : 'Noch keine Records.' }}
      </p>
    }
    @if (pager.count(); as count) {
      <p class="mb-2 text-xs text-muted-foreground" data-testid="match-count">
        {{ count >= 10000 ? 'mehr als 10 000' : count }} Treffer
      </p>
    }
    @if (pager.items().length > 0) {
      <ul
        role="list"
        class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4"
        aria-label="Galerie"
        data-testid="gallery"
      >
        @for (record of pager.items(); track record.id) {
          <li
            class="relative flex cursor-pointer flex-col overflow-hidden rounded-xl border bg-card text-card-foreground shadow-xs hover:border-ring focus-within:border-ring focus-within:ring-3 focus-within:ring-ring/50"
            data-testid="gallery-card"
            [attr.data-id]="record.id"
          >
            <ag-card-cover
              [definition]="coverDefinition()"
              [value]="coverDefinition() ? valueOf(record, coverDefinition()!.key) : null"
              [title]="record.title"
            />
            <div
              class="flex flex-col gap-1 p-3 [&_a:not([data-nav])]:relative [&_a:not([data-nav])]:z-10"
            >
              <a
                class="font-medium underline-offset-4 outline-none after:absolute after:inset-0 hover:underline focus-visible:underline"
                data-testid="gallery-card-link"
                data-nav
                [tabindex]="record.id === tabId() ? 0 : -1"
                [routerLink]="['/w', context.slug(), 'records', record.id]"
                (focus)="activeId.set(record.id)"
                >{{ record.title || 'Ohne Titel' }}</a
              >
              @for (field of fieldsOf(record); track field.key) {
                <div class="flex gap-2 text-xs">
                  <span class="shrink-0 text-muted-foreground">{{ field.name }}</span>
                  <ag-cell-value
                    chipNavigation="link"
                    [definition]="field"
                    [value]="valueOf(record, field.key)"
                  />
                </div>
              }
            </div>
          </li>
        }
      </ul>
    }
    @if (pager.moreError(); as message) {
      <p class="mt-2 text-sm text-destructive" role="alert" data-testid="gallery-more-error">
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
          data-testid="gallery-load-more"
          [disabled]="pager.loadingMore()"
          (click)="pager.loadMore()"
        >
          {{ pager.loadingMore() ? 'Lädt …' : 'Mehr laden' }}
        </button>
      </div>
    }
  `,
})
export class GalleryViewComponent {
  protected readonly context = inject(WorkspaceContext);
  private readonly titles = inject(RecordTitleStore);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly view = input.required<ViewDto>();
  readonly state = input.required<ViewWorkingState>();
  readonly schema = input.required<readonly PropertyDefinition[]>();

  /** Record whose title link is in the tab order (roving focus). */
  protected readonly activeId = signal<string | null>(null);

  protected readonly coverDefinition = computed(() => {
    const key = this.view().cardCoverProperty;
    const definition = key ? this.schema().find((d) => d.key === key) : undefined;
    // a cover of a type without a cover display counts as no cover
    return definition && isCoverType(definition) ? definition : null;
  });

  /** Candidate card fields: the visible columns without the cover property. */
  private readonly cardFields = computed(() => {
    const coverKey = this.coverDefinition()?.key;
    return tableColumns(this.schema(), this.state().visible)
      .map((c) => c.definition)
      .filter((d) => d.key !== coverKey);
  });

  private readonly source = computed<QuerySource | undefined>(
    () => {
      const slug = this.context.slug();
      return slug
        ? {
            slug,
            query: viewQuery(
              this.view(),
              this.state(),
              GALLERY_LIMIT,
              false,
              this.coverDefinition()?.key ?? null,
            ),
          }
        : undefined;
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
    effect(() => this.titles.prefetch(relationIdsOf(this.pager.items(), this.cardFields())));
    liveRefresh({
      collectionId: () => this.view().collectionId,
      blocked: () => false,
      generation: () => 0,
      refresh: (canApply) => this.pager.refresh(canApply),
      holdSchema: true,
    });
  }

  protected fieldsOf(record: RecordDto): PropertyDefinition[] {
    return fieldsWithValue(record, this.cardFields(), GALLERY_FIELDS);
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
    const cards = links.map((l) => l.closest('li') as HTMLElement);
    const target = nextIndex(event.key, index, links.length, gridColumns(cards), true);
    if (target !== null) {
      event.preventDefault();
      this.activeId.set(this.pager.items()[target]?.id ?? null);
      links[target].focus();
    }
  }
}
