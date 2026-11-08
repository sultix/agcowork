import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { Router, RouterLink, RouterOutlet } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCircleAlert, lucidePlus, lucideSlidersHorizontal } from '@ng-icons/lucide';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { BreadcrumbsComponent, Crumb } from '../../shared/ui/breadcrumbs.component';
import { RecordCreateDialogComponent } from '../records/record-create-dialog.component';
import type { RecordDto } from '../records/record-save.service';
import { WorkspaceTreeStore } from '../spaces/workspace-tree.store';
import { CollectionStore } from '../views/collection.store';
import { ViewSwitcherComponent } from '../views/view-switcher.component';
import { WorkspacesStore } from '../workspaces/workspaces.store';

/**
 * `/w/:slug/collections/:collectionId`: collection header with a schema summary, the view tabs
 * and, below them, the active view (child route `views/:viewId`; without one the default view
 * is opened).
 */
@Component({
  selector: 'ag-collection-page',
  imports: [
    NgIcon,
    HlmAlertImports,
    HlmButton,
    HlmSkeleton,
    BadgeComponent,
    BreadcrumbsComponent,
    RecordCreateDialogComponent,
    RouterLink,
    RouterOutlet,
    ViewSwitcherComponent,
  ],
  providers: [
    CollectionStore,
    provideIcons({ lucideCircleAlert, lucidePlus, lucideSlidersHorizontal }),
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="flex flex-col gap-4 p-6">
      @if (store.loadError(); as message) {
        <div hlmAlert variant="destructive" data-testid="load-error">
          <ng-icon name="lucideCircleAlert" />
          <p hlmAlertDescription>{{ message }}</p>
        </div>
      } @else if (store.collection(); as collection) {
        <ag-breadcrumbs [crumbs]="crumbs()" />
        <div class="flex flex-wrap items-start justify-between gap-4">
          <div class="flex flex-col gap-1">
            <h1 class="text-2xl font-semibold tracking-tight" data-testid="collection-title">
              {{ collection.name }}
            </h1>
            <p class="text-sm text-muted-foreground" data-testid="collection-description">
              {{ collection.description }}
            </p>
            @if (store.recordCount() !== null) {
              <p class="text-xs text-muted-foreground" data-testid="record-count">
                {{ store.recordCount() }} Records
              </p>
            }
          </div>
          <div class="flex flex-wrap gap-2">
            @if (canEditSchema()) {
              <a
                hlmBtn
                variant="outline"
                data-testid="schema-edit-link"
                [routerLink]="schemaLink()"
              >
                <ng-icon name="lucideSlidersHorizontal" />
                Schema bearbeiten
              </a>
            }
            @if (store.canWrite()) {
              <button hlmBtn type="button" data-testid="record-new" (click)="creating.set(true)">
                <ng-icon name="lucidePlus" />
                Neuer Record
              </button>
            }
          </div>
        </div>

        <details class="text-sm">
          <summary class="cursor-pointer text-muted-foreground">Schema</summary>
          <ul class="mt-2 flex flex-wrap gap-1" aria-label="Schema" data-testid="schema-chips">
            @for (def of collection.schema; track def.key) {
              <li>
                <ag-badge>{{ def.key }}: {{ def.type }}</ag-badge>
              </li>
            }
          </ul>
        </details>

        <ag-view-switcher [activeId]="store.activeViewId()" />
        <router-outlet />
      } @else {
        <div class="flex flex-col gap-3" aria-busy="true" aria-label="Wird geladen">
          <hlm-skeleton class="h-4 w-48" />
          <hlm-skeleton class="h-8 w-72" />
          <hlm-skeleton class="h-4 w-96 max-w-full" />
        </div>
      }
    </section>

    @if (creating()) {
      <ag-record-create-dialog
        [collectionId]="collectionId()"
        [spaceId]="store.collection()?.spaceId ?? null"
        (created)="onCreated($event)"
        (cancelled)="creating.set(false)"
      />
    }
  `,
})
export class CollectionPageComponent {
  protected readonly store = inject(CollectionStore);
  private readonly context = inject(WorkspaceContext);
  private readonly workspaces = inject(WorkspacesStore);
  private readonly router = inject(Router);
  private readonly tree = inject(WorkspaceTreeStore);

  /** Route parameter `:collectionId`. */
  readonly collectionId = input.required<string>();

  protected readonly creating = signal(false);

  /** `schema` on an active workspace and collection: the schema editor is offered. */
  protected readonly canEditSchema = computed(() => {
    const ws = this.workspaces.find(this.context.slug() ?? '');
    return (
      !!ws && !ws.archivedAt && ws.scopes.includes('schema') && !this.store.collection()?.archivedAt
    );
  });

  protected readonly schemaLink = computed(() => [
    '/w',
    this.context.slug(),
    'settings',
    'collections',
    this.collectionId(),
    'schema',
  ]);

  protected readonly crumbs = computed<Crumb[]>(() => {
    const collection = this.store.collection();
    const slug = this.context.slug();
    if (!collection || !slug) {
      return [];
    }
    return [
      {
        label: this.tree.findSpace(collection.spaceId)?.name ?? 'Space',
        link: ['/w', slug, 'spaces', collection.spaceId],
      },
      { label: collection.name },
    ];
  });

  constructor() {
    effect(() => {
      const id = this.collectionId();
      untracked(() => {
        this.store.connect(id);
        this.creating.set(false);
      });
    });
  }

  protected onCreated(record: RecordDto): void {
    this.creating.set(false);
    void this.router.navigate(['/w', this.context.slug(), 'records', record.id]);
  }
}
