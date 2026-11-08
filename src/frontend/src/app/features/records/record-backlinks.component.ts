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
import { RouterLink } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCircleAlert } from '@ng-icons/lucide';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import type { components } from '../../core/api/generated/schema';
import { liveReload } from '../../core/realtime/live-reload';
import type { ActivityEvent } from '../../core/realtime/realtime.service';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { describeError } from '../../shared/ui/api-error-text';
import { WorkspaceTreeStore } from '../spaces/workspace-tree.store';

type Backlinks = components['schemas']['RecordBacklinksDto'];
type BacklinkGroup = components['schemas']['BacklinkGroupDto'];

/** Referencing records loaded per group at first and with every "Mehr laden". */
export const BACKLINKS_PAGE = 10;
/** The API's upper bound of `limit`. */
const MAX_LIMIT = 200;

/** Record events of a source collection that can change the backlinks. */
const SOURCE_EVENTS = ['record.created', 'record.updated', 'record.archived', 'record.restored'];

interface GroupState {
  readonly group: BacklinkGroup;
  readonly loadingMore: boolean;
  readonly moreError: string | null;
}

interface State {
  readonly groups: readonly GroupState[];
  readonly nextGroupsCursor: string | null;
}

function groupKey(group: BacklinkGroup): string {
  return `${group.collectionId}:${group.propertyKey}`;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Property keys a `record.updated` diff names (`properties: { key: … }` or `properties.key`). */
function changedProperties(diff: Record<string, unknown>): string[] {
  const keys: string[] = [];
  for (const [key, value] of Object.entries(diff)) {
    if (key === 'properties' && isObject(value)) {
      keys.push(...Object.keys(value));
    } else if (key.startsWith('properties.')) {
      keys.push(key.slice('properties.'.length));
    }
  }
  return keys;
}

/**
 * "Verweise auf diesen Eintrag" below a record's body: the records that point at it through a
 * relation (`GET /records/{id}/backlinks`), grouped by source collection and property, titles as
 * links (text only). Each group continues with its own cursor ("Mehr laden"), more groups with
 * the groups cursor. Live: record events of a source collection (one whose schema has a relation
 * to this record's collection, or that already shows up here) and schema changes of such a
 * collection reload the list, bundled; an update
 * counts only when it touches such a relation or the title of a listed record. A reload keeps as
 * many records per group as were loaded; answers of an older load are dropped.
 */
@Component({
  selector: 'ag-record-backlinks',
  imports: [HlmAlertImports, HlmButton, HlmSkeleton, NgIcon, RouterLink],
  providers: [provideIcons({ lucideCircleAlert })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section
      class="flex flex-col gap-3"
      aria-labelledby="backlinks-heading"
      data-testid="backlinks"
    >
      <h2 id="backlinks-heading" class="text-base font-semibold">Verweise auf diesen Eintrag</h2>
      @if (error(); as message) {
        <div hlmAlert variant="destructive" data-testid="backlinks-error">
          <ng-icon name="lucideCircleAlert" />
          <p hlmAlertDescription>{{ message }}</p>
          <button
            type="button"
            hlmAlertAction
            hlmBtn
            variant="outline"
            size="xs"
            data-testid="backlinks-retry"
            (click)="retry()"
          >
            Erneut laden
          </button>
        </div>
      } @else if (state(); as current) {
        @for (entry of current.groups; track key(entry.group)) {
          <div class="flex flex-col gap-1" data-testid="backlink-group">
            <h3 class="text-sm text-muted-foreground" data-testid="backlink-group-title">
              <a
                class="font-medium text-foreground underline underline-offset-4"
                [routerLink]="['/w', slug(), 'collections', entry.group.collectionId]"
                >{{ entry.group.collectionName }}</a
              >
              · {{ entry.group.propertyName }}
            </h3>
            <ul class="flex flex-col gap-1 text-sm">
              @for (item of entry.group.items; track item.id) {
                <li data-testid="backlink-item" [attr.data-id]="item.id">
                  <a
                    class="underline underline-offset-4"
                    [class.line-through]="item.archived"
                    [class.text-muted-foreground]="item.archived"
                    [routerLink]="['/w', slug(), 'records', item.id]"
                    >{{ item.title || 'Ohne Titel' }}</a
                  >
                  @if (item.archived) {
                    <span class="sr-only"> (archiviert)</span>
                  }
                </li>
              }
            </ul>
            @if (entry.moreError; as message) {
              <p class="text-sm text-destructive" role="alert" data-testid="backlink-more-error">
                {{ message }}
              </p>
            }
            @if (entry.group.nextCursor) {
              <button
                type="button"
                hlmBtn
                variant="ghost"
                size="sm"
                class="self-start"
                data-testid="backlink-more"
                [disabled]="entry.loadingMore"
                (click)="loadMore(entry.group)"
              >
                {{ entry.loadingMore ? 'Lädt …' : 'Mehr laden' }}
              </button>
            }
          </div>
        } @empty {
          <div
            class="rounded-xl border border-dashed p-6 text-center"
            data-testid="backlinks-empty"
          >
            <p class="font-medium">Noch keine Verweise</p>
            <p class="text-sm text-muted-foreground">
              Records, die über eine Relation auf diesen Eintrag zeigen, erscheinen hier.
            </p>
          </div>
        }
        @if (groupsError(); as message) {
          <p class="text-sm text-destructive" role="alert" data-testid="backlink-groups-error">
            {{ message }}
          </p>
        }
        @if (current.nextGroupsCursor) {
          <button
            type="button"
            hlmBtn
            variant="outline"
            size="sm"
            class="self-start"
            data-testid="backlink-more-groups"
            [disabled]="loadingGroups()"
            (click)="loadMoreGroups()"
          >
            {{ loadingGroups() ? 'Lädt …' : 'Weitere Verweise laden' }}
          </button>
        }
      } @else {
        <div class="flex flex-col gap-2" data-testid="backlinks-loading">
          <hlm-skeleton class="h-4 w-48" />
          <hlm-skeleton class="h-4 w-64" />
        </div>
      }
    </section>
  `,
})
export class RecordBacklinksComponent {
  private readonly api = inject(ApiClient);
  private readonly context = inject(WorkspaceContext);
  private readonly tree = inject(WorkspaceTreeStore);

  /** The record whose backlinks are shown. */
  readonly recordId = input.required<string>();
  /** Its collection: collections with a relation to it are the sources. */
  readonly collectionId = input.required<string>();

  protected readonly slug = computed(() => this.context.slug() ?? '');
  protected readonly state = signal<State | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly loadingGroups = signal(false);
  protected readonly groupsError = signal<string | null>(null);

  /** Grows with every (re)load; answers of older loads and their "more" requests are dropped. */
  private generation = 0;

  /**
   * Source collections and their relation keys pointing at {@link collectionId}: from the
   * schemas of the workspace tree, plus whatever the answer already lists (archived sources).
   */
  private readonly sources = computed(() => {
    const target = this.collectionId();
    const sources = new Map<string, Set<string>>();
    const add = (collectionId: string, key: string) =>
      sources.set(collectionId, (sources.get(collectionId) ?? new Set()).add(key));
    for (const collection of this.tree.collections()) {
      for (const definition of collection.schema) {
        if (definition.type === 'relation' && definition.targetCollectionId === target) {
          add(collection.id, definition.key);
        }
      }
    }
    for (const entry of this.state()?.groups ?? []) {
      add(entry.group.collectionId, entry.group.propertyKey);
    }
    return sources;
  });

  constructor() {
    effect(() => {
      const slug = this.context.slug();
      const recordId = this.recordId();
      if (slug && recordId) {
        untracked(() => {
          this.state.set(null);
          this.error.set(null);
          void this.load(slug, recordId, BACKLINKS_PAGE, false);
        });
      }
    });
    liveReload({
      slug: () => this.context.slug(),
      matches: (event) => this.concerns(event),
      reload: () => this.refresh(),
    });
  }

  protected key(group: BacklinkGroup): string {
    return groupKey(group);
  }

  protected retry(): void {
    const slug = this.context.slug();
    if (slug) {
      this.error.set(null);
      void this.load(slug, this.recordId(), BACKLINKS_PAGE, false);
    }
  }

  /**
   * Live reload: as many records per group as are loaded, without a loading state. While nothing
   * is shown yet (first load still out or failed), a failure shows the error like a first load.
   */
  private refresh(): void {
    const slug = this.context.slug();
    if (!slug) {
      return;
    }
    const silent = this.state() !== null;
    const loaded = Math.max(0, ...(this.state()?.groups ?? []).map((g) => g.group.items.length));
    const limit = Math.min(MAX_LIMIT, Math.max(BACKLINKS_PAGE, loaded));
    void this.load(slug, this.recordId(), limit, silent);
  }

  private async load(slug: string, recordId: string, limit: number, silent: boolean) {
    const generation = ++this.generation;
    try {
      const answer = await firstValueFrom(
        this.api
          .workspace(slug)
          .get('/records/{recordId}/backlinks', { path: { recordId }, query: { limit } }),
      );
      if (this.current(generation, slug, recordId)) {
        this.state.set(toState(answer));
        this.error.set(null);
        this.groupsError.set(null);
        this.loadingGroups.set(false);
      }
    } catch (error) {
      if (this.current(generation, slug, recordId) && !silent) {
        this.error.set(describeError(error));
      }
      // a failed live reload keeps what is shown; the next change or resync tries again
    }
  }

  protected async loadMore(group: BacklinkGroup): Promise<void> {
    const slug = this.context.slug();
    const cursor = group.nextCursor;
    if (!slug || !cursor) {
      return;
    }
    const recordId = this.recordId();
    const generation = this.generation;
    const key = groupKey(group);
    this.patchGroup(key, (g) => ({ ...g, loadingMore: true, moreError: null }));
    try {
      const answer = await firstValueFrom(
        this.api.workspace(slug).get('/records/{recordId}/backlinks', {
          path: { recordId },
          query: { limit: BACKLINKS_PAGE, cursor },
        }),
      );
      if (!this.current(generation, slug, recordId)) {
        this.settleGroup(slug, recordId, key);
        return;
      }
      const page = answer.groups.find((g) => groupKey(g) === key);
      this.patchGroup(key, (g) => ({
        group: {
          ...g.group,
          items: [
            ...g.group.items,
            ...(page?.items ?? []).filter((i) => !g.group.items.some((o) => o.id === i.id)),
          ],
          nextCursor: page?.nextCursor ?? null,
        },
        loadingMore: false,
        moreError: null,
      }));
    } catch (error) {
      if (this.current(generation, slug, recordId)) {
        this.patchGroup(key, (g) => ({
          ...g,
          loadingMore: false,
          moreError: describeError(error),
        }));
      } else {
        this.settleGroup(slug, recordId, key);
      }
    }
  }

  /** A "more" request outlived its load: the button must not stay busy. */
  private settleGroup(slug: string, recordId: string, key: string): void {
    if (this.context.slug() === slug && this.recordId() === recordId) {
      this.patchGroup(key, (g) => ({ ...g, loadingMore: false }));
    }
  }

  protected async loadMoreGroups(): Promise<void> {
    const slug = this.context.slug();
    const cursor = this.state()?.nextGroupsCursor;
    if (!slug || !cursor || this.loadingGroups()) {
      return;
    }
    const recordId = this.recordId();
    const generation = this.generation;
    this.loadingGroups.set(true);
    this.groupsError.set(null);
    try {
      const answer = await firstValueFrom(
        this.api.workspace(slug).get('/records/{recordId}/backlinks', {
          path: { recordId },
          query: { limit: BACKLINKS_PAGE, cursor },
        }),
      );
      if (!this.current(generation, slug, recordId)) {
        return;
      }
      const more = toState(answer);
      this.state.update((s) => {
        const known = new Set((s?.groups ?? []).map((g) => groupKey(g.group)));
        return {
          groups: [
            ...(s?.groups ?? []),
            ...more.groups.filter((g) => !known.has(groupKey(g.group))),
          ],
          nextGroupsCursor: more.nextGroupsCursor,
        };
      });
    } catch (error) {
      if (this.current(generation, slug, recordId)) {
        this.groupsError.set(describeError(error));
      }
    } finally {
      if (this.context.slug() === slug && this.recordId() === recordId) {
        this.loadingGroups.set(false);
      }
    }
  }

  private current(generation: number, slug: string, recordId: string): boolean {
    return (
      generation === this.generation && this.context.slug() === slug && this.recordId() === recordId
    );
  }

  private patchGroup(key: string, change: (g: GroupState) => GroupState): void {
    this.state.update((s) =>
      s ? { ...s, groups: s.groups.map((g) => (groupKey(g.group) === key ? change(g) : g)) } : s,
    );
  }

  /** Whether a live event can change what this list shows. */
  private concerns(event: ActivityEvent): boolean {
    if (event.type === 'collection.schema_changed') {
      // a source dropped or changed its relation (its values are cleaned without record events)
      return this.sources().has(event.targetId);
    }
    if (event.targetType !== 'record' || !SOURCE_EVENTS.includes(event.type)) {
      return false;
    }
    const keys = event.collectionId ? this.sources().get(event.collectionId) : undefined;
    if (!keys) {
      return false;
    }
    if (event.type !== 'record.updated' || !isObject(event.diff)) {
      return true;
    }
    const diff = event.diff as Record<string, unknown>;
    if ('title' in diff && this.lists(event.targetId)) {
      return true;
    }
    return changedProperties(diff).some((key) => keys.has(key));
  }

  private lists(recordId: string): boolean {
    return (this.state()?.groups ?? []).some((g) => g.group.items.some((i) => i.id === recordId));
  }
}

function toState(answer: Backlinks): State {
  return {
    groups: answer.groups.map((group) => ({ group, loadingMore: false, moreError: null })),
    nextGroupsCursor: answer.nextGroupsCursor ?? null,
  };
}
