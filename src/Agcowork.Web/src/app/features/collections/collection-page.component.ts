import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  resource,
  signal,
  untracked,
} from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import type { components } from '../../core/api/generated/schema';
import { MembersStore } from '../../core/workspace/members.store';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { optionLabel } from '../../shared/properties/property-types';
import { describeError } from '../../shared/ui/api-error-text';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { BreadcrumbsComponent, Crumb } from '../../shared/ui/breadcrumbs.component';
import { RecordCreateDialogComponent } from '../records/record-create-dialog.component';
import { createRecordPager } from '../records/record-pager';
import type { RecordDto } from '../records/record-save.service';
import { WorkspaceTreeStore } from '../spaces/workspace-tree.store';
import { WorkspacesStore } from '../workspaces/workspaces.store';

type Description = components['schemas']['CollectionDescriptionDto'];

/**
 * `/w/:slug/collections/:collectionId`: collection header with a schema summary and a plain
 * list of its records (the real table view comes with AP-015).
 */
@Component({
  selector: 'ag-collection-page',
  imports: [
    DatePipe,
    RouterLink,
    BadgeComponent,
    BreadcrumbsComponent,
    RecordCreateDialogComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="mx-auto max-w-5xl p-6">
      @if (description.error()) {
        <p class="text-sm text-danger" role="alert" data-testid="load-error">{{ loadError() }}</p>
      } @else if (collection(); as collection) {
        <ag-breadcrumbs [crumbs]="crumbs()" />
        <div class="mt-2 flex items-start justify-between gap-4">
          <div>
            <h1 class="text-xl font-semibold" data-testid="collection-title">
              {{ collection.name }}
            </h1>
            <p class="mt-1 text-sm text-muted" data-testid="collection-description">
              {{ collection.description }}
            </p>
            @if (recordCount() !== null) {
              <p class="mt-1 text-xs text-muted" data-testid="record-count">
                {{ recordCount() }} Records
              </p>
            }
          </div>
          @if (canWrite()) {
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

        <ul class="mt-4 flex flex-wrap gap-1" aria-label="Schema" data-testid="schema-chips">
          @for (def of collection.schema; track def.key) {
            <li>
              <ag-badge>{{ def.key }}: {{ def.type }}</ag-badge>
            </li>
          }
        </ul>

        @if (pager.error(); as message) {
          <p class="mt-4 text-sm text-danger" role="alert" data-testid="records-error">
            {{ message }}
          </p>
        }
        <table class="mt-6 w-full text-left text-sm" data-testid="record-table">
          <thead class="border-b border-border text-muted">
            <tr>
              <th class="py-2 pr-3 font-medium">Titel</th>
              @if (statusDefinition(); as status) {
                <th class="py-2 pr-3 font-medium">{{ status.name }}</th>
              }
              <th class="py-2 pr-3 font-medium">Geändert</th>
              <th class="py-2 font-medium">Von</th>
            </tr>
          </thead>
          <tbody>
            @for (record of pager.items(); track record.id) {
              <tr class="border-b border-border" data-testid="record-row">
                <td class="py-2 pr-3">
                  <a
                    [routerLink]="['/w', context.slug(), 'records', record.id]"
                    class="text-accent hover:underline"
                    data-testid="record-link"
                    >{{ record.title }}</a
                  >
                </td>
                @if (statusDefinition(); as status) {
                  <td class="py-2 pr-3" data-testid="record-status">
                    @if (statusLabel(record); as label) {
                      <ag-badge tone="accent">{{ label }}</ag-badge>
                    }
                  </td>
                }
                <td class="py-2 pr-3 text-muted">
                  {{ record.updatedAt | date: 'dd.MM.yyyy HH:mm' }}
                </td>
                <td class="py-2 text-muted">{{ members.nameOf(record.updatedBy) }}</td>
              </tr>
            } @empty {
              @if (!pager.isLoading()) {
                <tr>
                  <td colspan="4" class="py-3 text-muted">Noch keine Records.</td>
                </tr>
              }
            }
          </tbody>
        </table>
        @if (pager.moreError(); as message) {
          <p class="mt-2 text-sm text-danger" role="alert">{{ message }}</p>
        }
        @if (pager.hasMore()) {
          <button
            type="button"
            class="mt-3 rounded-md border border-border px-3 py-1.5 text-sm hover:bg-surface-hover disabled:opacity-60"
            data-testid="records-more"
            [disabled]="pager.loadingMore()"
            (click)="pager.loadMore()"
          >
            Mehr laden
          </button>
        }
      } @else {
        <p class="text-sm text-muted">Wird geladen …</p>
      }
    </section>

    @if (creating()) {
      <ag-record-create-dialog
        [collectionId]="collectionId()"
        [spaceId]="collection()?.spaceId ?? null"
        (created)="onCreated($event)"
        (cancelled)="creating.set(false)"
      />
    }
  `,
})
export class CollectionPageComponent {
  protected readonly context = inject(WorkspaceContext);
  protected readonly members = inject(MembersStore);
  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);
  private readonly tree = inject(WorkspaceTreeStore);
  private readonly workspaces = inject(WorkspacesStore);

  /** Route parameter `:collectionId`. */
  readonly collectionId = input.required<string>();

  protected readonly description = resource<Description, { slug: string; id: string } | undefined>({
    params: () => {
      const slug = this.context.slug();
      return slug && this.collectionId() ? { slug, id: this.collectionId() } : undefined;
    },
    loader: ({ params }) =>
      firstValueFrom(
        this.api
          .workspace(params.slug)
          .get('/collections/{collectionId}/describe', { path: { collectionId: params.id } }),
      ),
  });

  protected readonly pager = createRecordPager(() => {
    const slug = this.context.slug();
    return slug && this.collectionId() ? { slug, collectionId: this.collectionId() } : undefined;
  });

  protected readonly creating = signal(false);

  protected readonly collection = computed(() =>
    this.description.hasValue() ? this.description.value().collection : undefined,
  );
  protected readonly loadError = computed(() =>
    this.description.error() ? describeError(this.description.error()) : null,
  );
  protected readonly recordCount = computed(() =>
    this.description.hasValue() ? Number(this.description.value().statistics.recordCount) : null,
  );
  protected readonly statusDefinition = computed(() =>
    this.collection()?.schema.find((d) => d.type === 'status'),
  );
  protected readonly canWrite = computed(() => {
    const ws = this.workspaces.find(this.context.slug());
    return !!ws && !ws.archivedAt && ws.scopes.includes('write') && !this.collection()?.archivedAt;
  });

  protected readonly crumbs = computed<Crumb[]>(() => {
    const collection = this.collection();
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
      this.collectionId();
      untracked(() => this.creating.set(false));
    });
  }

  protected statusLabel(record: RecordDto): string {
    const status = this.statusDefinition();
    const value = status ? (record.properties as Record<string, unknown>)[status.key] : null;
    return status && value ? optionLabel(status, value) : '';
  }

  protected onCreated(record: RecordDto): void {
    this.creating.set(false);
    void this.router.navigate(['/w', this.context.slug(), 'records', record.id]);
  }
}
