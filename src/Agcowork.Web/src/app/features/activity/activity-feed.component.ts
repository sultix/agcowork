import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { ApiClient } from '../../core/api/api-client';
import { MembersStore } from '../../core/workspace/members.store';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { describeError } from '../../shared/ui/api-error-text';
import { WorkspaceTreeStore } from '../spaces/workspace-tree.store';
import {
  ACTIVITY_TYPE_LABELS,
  ActivityEntryComponent,
  ActivityEvent,
} from './activity-entry.component';
import { firstValueFrom } from 'rxjs';

const PAGE_SIZE = 30;

interface ActivityQuery {
  readonly spaceId?: string;
  readonly collectionId?: string;
  readonly actorId?: string;
  readonly since?: string;
  readonly types?: string[];
}

/** `/w/:slug/activity`: what happened in the workspace, newest first, filterable. */
@Component({
  selector: 'ag-activity-feed',
  imports: [ActivityEntryComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="mx-auto max-w-5xl p-6">
      <h1 class="text-xl font-semibold">Aktivität</h1>

      <form
        class="mt-4 flex flex-wrap items-end gap-3 text-sm"
        data-testid="activity-filters"
        (submit)="$event.preventDefault()"
      >
        <label class="flex flex-col gap-1">
          <span class="text-muted">Space</span>
          <select
            class="rounded-md border border-border bg-surface px-2 py-1"
            data-testid="filter-space"
            (change)="setSpace($any($event.target).value)"
          >
            <option value="">Alle</option>
            @for (space of tree.spaces(); track space.id) {
              <option [value]="space.id" [selected]="space.id === spaceId()">
                {{ space.name }}
              </option>
            }
          </select>
        </label>
        <label class="flex flex-col gap-1">
          <span class="text-muted">Collection</span>
          <select
            class="rounded-md border border-border bg-surface px-2 py-1"
            data-testid="filter-collection"
            (change)="collectionId.set($any($event.target).value)"
          >
            <option value="">Alle</option>
            @for (collection of collections(); track collection.id) {
              <option [value]="collection.id" [selected]="collection.id === collectionId()">
                {{ collection.name }}
              </option>
            }
          </select>
        </label>
        <label class="flex flex-col gap-1">
          <span class="text-muted">Actor</span>
          <select
            class="rounded-md border border-border bg-surface px-2 py-1"
            data-testid="filter-actor"
            (change)="actorId.set($any($event.target).value)"
          >
            <option value="">Alle</option>
            @for (member of members.members(); track member.actorId) {
              <option [value]="member.actorId" [selected]="member.actorId === actorId()">
                {{ member.name }} ({{ member.kind === 'agent' ? 'Agent' : 'Mensch' }})
              </option>
            }
          </select>
        </label>
        <label class="flex flex-col gap-1">
          <span class="text-muted">Seit</span>
          <input
            type="date"
            class="rounded-md border border-border bg-surface px-2 py-1"
            data-testid="filter-since"
            [value]="since()"
            (change)="since.set($any($event.target).value)"
          />
        </label>
        <details class="relative">
          <summary
            class="cursor-pointer rounded-md border border-border bg-surface px-2 py-1"
            data-testid="filter-types-summary"
          >
            Typen
            @if (types().length > 0) {
              ({{ types().length }})
            }
          </summary>
          <div
            class="absolute z-10 mt-1 w-64 rounded-md border border-border bg-surface p-2 shadow"
            data-testid="filter-types"
          >
            @for (type of typeOptions; track type.value) {
              <label class="flex items-center gap-2 py-0.5">
                <input
                  type="checkbox"
                  [value]="type.value"
                  [checked]="types().includes(type.value)"
                  (change)="toggleType(type.value)"
                />
                {{ type.label }}
              </label>
            }
          </div>
        </details>
        @if (filtered()) {
          <button
            type="button"
            class="rounded-md border border-border px-2 py-1 hover:bg-surface-hover"
            data-testid="filter-reset"
            (click)="reset()"
          >
            Zurücksetzen
          </button>
        }
      </form>

      @if (error(); as message) {
        <p class="mt-3 text-sm text-danger" role="alert" data-testid="activity-error">
          {{ message }}
        </p>
      }
      <ul
        class="mt-4 divide-y divide-border rounded-lg border border-border"
        data-testid="activity-list"
      >
        @for (event of events(); track event.id) {
          <li>
            <ag-activity-entry [event]="event" />
          </li>
        } @empty {
          @if (!loading() && !error()) {
            <li class="px-4 py-3 text-sm text-muted" data-testid="activity-empty">
              Keine Ereignisse.
            </li>
          }
        }
      </ul>
      @if (nextCursor()) {
        <button
          type="button"
          class="mt-3 rounded-md border border-border px-3 py-1.5 text-sm hover:bg-surface-hover disabled:opacity-60"
          data-testid="load-more"
          [disabled]="loading()"
          (click)="loadMore()"
        >
          Mehr laden
        </button>
      }
    </section>
  `,
})
export class ActivityFeedComponent {
  protected readonly tree = inject(WorkspaceTreeStore);
  protected readonly members = inject(MembersStore);
  private readonly context = inject(WorkspaceContext);
  private readonly api = inject(ApiClient);

  protected readonly typeOptions = Object.entries(ACTIVITY_TYPE_LABELS).map(([value, label]) => ({
    value,
    label,
  }));

  protected readonly spaceId = signal('');
  protected readonly collectionId = signal('');
  protected readonly actorId = signal('');
  /** `yyyy-mm-dd` from the date input; empty = no lower bound. */
  protected readonly since = signal('');
  protected readonly types = signal<readonly string[]>([]);

  protected readonly events = signal<readonly ActivityEvent[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  private token = 0;

  protected readonly collections = computed(() =>
    this.tree.collections().filter((c) => !this.spaceId() || c.spaceId === this.spaceId()),
  );

  protected readonly filtered = computed(
    () =>
      !!(this.spaceId() || this.collectionId() || this.actorId() || this.since()) ||
      this.types().length > 0,
  );

  /** Query parameters of the current filters. */
  private readonly query = computed<ActivityQuery>(() => {
    const since = this.since();
    return {
      ...(this.spaceId() ? { spaceId: this.spaceId() } : {}),
      ...(this.collectionId() ? { collectionId: this.collectionId() } : {}),
      ...(this.actorId() ? { actorId: this.actorId() } : {}),
      ...(since ? { since: new Date(`${since}T00:00:00`).toISOString() } : {}),
      ...(this.types().length > 0 ? { types: [...this.types()] } : {}),
    };
  });

  private lastSlug: string | null = null;

  constructor() {
    effect(() => {
      const slug = this.context.slug();
      const query = this.query();
      if (!slug) {
        return;
      }
      untracked(() => {
        // Filters belong to one workspace. After a switch the reset changes the query, which
        // re-runs this effect: it then loads once, without the old filters.
        const switched = this.lastSlug !== null && slug !== this.lastSlug;
        this.lastSlug = slug;
        if (switched && this.filtered()) {
          this.reset();
          return;
        }
        void this.load(slug, query, null);
      });
    });
  }

  protected setSpace(id: string): void {
    this.spaceId.set(id);
    const collectionId = this.collectionId();
    if (id && collectionId && this.tree.findCollection(collectionId)?.spaceId !== id) {
      this.collectionId.set('');
    }
  }

  protected toggleType(type: string): void {
    this.types.update((types) =>
      types.includes(type) ? types.filter((t) => t !== type) : [...types, type],
    );
  }

  protected reset(): void {
    this.spaceId.set('');
    this.collectionId.set('');
    this.actorId.set('');
    this.since.set('');
    this.types.set([]);
  }

  protected loadMore(): void {
    const slug = this.context.slug();
    const cursor = this.nextCursor();
    if (slug && cursor) {
      void this.load(slug, this.query(), cursor);
    }
  }

  private async load(slug: string, query: ActivityQuery, cursor: string | null): Promise<void> {
    const token = ++this.token;
    if (cursor === null) {
      this.events.set([]);
      this.nextCursor.set(null);
    }
    this.loading.set(true);
    this.error.set(null);
    try {
      const page = await firstValueFrom(
        this.api.workspace(slug).get('/activity', {
          query: { ...query, limit: PAGE_SIZE, ...(cursor ? { cursor } : {}) },
        }),
      );
      if (token !== this.token) {
        return;
      }
      this.events.update((current) => (cursor ? [...current, ...page.items] : page.items));
      this.nextCursor.set(page.nextCursor);
    } catch (error) {
      if (token === this.token) {
        this.error.set(describeError(error));
      }
    } finally {
      if (token === this.token) {
        this.loading.set(false);
      }
    }
  }
}
