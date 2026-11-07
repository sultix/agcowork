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
import { Router, RouterOutlet } from '@angular/router';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { BreadcrumbsComponent, Crumb } from '../../shared/ui/breadcrumbs.component';
import { RecordCreateDialogComponent } from '../records/record-create-dialog.component';
import type { RecordDto } from '../records/record-save.service';
import { WorkspaceTreeStore } from '../spaces/workspace-tree.store';
import { CollectionStore } from '../views/collection.store';
import { ViewSwitcherComponent } from '../views/view-switcher.component';

/**
 * `/w/:slug/collections/:collectionId`: collection header with a schema summary, the view tabs
 * and, below them, the active view (child route `views/:viewId`; without one the default view
 * is opened).
 */
@Component({
  selector: 'ag-collection-page',
  imports: [
    BadgeComponent,
    BreadcrumbsComponent,
    RecordCreateDialogComponent,
    RouterOutlet,
    ViewSwitcherComponent,
  ],
  providers: [CollectionStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="p-6">
      @if (store.loadError(); as message) {
        <p class="text-sm text-danger" role="alert" data-testid="load-error">{{ message }}</p>
      } @else if (store.collection(); as collection) {
        <ag-breadcrumbs [crumbs]="crumbs()" />
        <div class="mt-2 flex items-start justify-between gap-4">
          <div>
            <h1 class="text-xl font-semibold" data-testid="collection-title">
              {{ collection.name }}
            </h1>
            <p class="mt-1 text-sm text-muted" data-testid="collection-description">
              {{ collection.description }}
            </p>
            @if (store.recordCount() !== null) {
              <p class="mt-1 text-xs text-muted" data-testid="record-count">
                {{ store.recordCount() }} Records
              </p>
            }
          </div>
          @if (store.canWrite()) {
            <button
              type="button"
              class="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg"
              data-testid="record-new"
              (click)="creating.set(true)"
            >
              Neuer Record
            </button>
          }
        </div>

        <details class="mt-3 text-sm">
          <summary class="cursor-pointer text-muted">Schema</summary>
          <ul class="mt-2 flex flex-wrap gap-1" aria-label="Schema" data-testid="schema-chips">
            @for (def of collection.schema; track def.key) {
              <li>
                <ag-badge>{{ def.key }}: {{ def.type }}</ag-badge>
              </li>
            }
          </ul>
        </details>

        <div class="mt-4">
          <ag-view-switcher [activeId]="store.activeViewId()" />
        </div>
        <router-outlet />
      } @else {
        <p class="text-sm text-muted">Wird geladen …</p>
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
  private readonly router = inject(Router);
  private readonly tree = inject(WorkspaceTreeStore);

  /** Route parameter `:collectionId`. */
  readonly collectionId = input.required<string>();

  protected readonly creating = signal(false);

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
