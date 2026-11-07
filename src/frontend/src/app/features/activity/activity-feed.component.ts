import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideChevronDown, lucideCircleAlert, lucideX } from '@ng-icons/lucide';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCheckbox } from '@spartan-ng/helm/checkbox';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmLabel } from '@spartan-ng/helm/label';
import { HlmNativeSelectImports } from '@spartan-ng/helm/native-select';
import { HlmPopoverImports } from '@spartan-ng/helm/popover';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
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
  imports: [
    NgIcon,
    HlmAlertImports,
    HlmButton,
    HlmCheckbox,
    HlmFieldImports,
    HlmInput,
    HlmLabel,
    HlmNativeSelectImports,
    HlmPopoverImports,
    HlmSkeleton,
    ActivityEntryComponent,
  ],
  providers: [provideIcons({ lucideChevronDown, lucideCircleAlert, lucideX })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="mx-auto flex max-w-5xl flex-col gap-6 p-6">
      <div class="flex flex-col gap-1">
        <h1 class="text-2xl font-semibold tracking-tight">Aktivität</h1>
        <p class="text-sm text-muted-foreground">Was im Workspace passiert ist, neueste zuerst.</p>
      </div>

      <form
        class="flex flex-wrap items-end gap-4"
        data-testid="activity-filters"
        (submit)="$event.preventDefault()"
      >
        <div hlmField class="w-44 gap-2">
          <label hlmFieldLabel for="filter-space">Space</label>
          <hlm-native-select
            selectId="filter-space"
            class="w-full"
            data-testid="filter-space"
            [value]="spaceId()"
            (valueChange)="setSpace($any($event))"
          >
            <option hlmNativeSelectOption value="">Alle</option>
            @for (space of tree.spaces(); track space.id) {
              <option hlmNativeSelectOption [value]="space.id">{{ space.name }}</option>
            }
          </hlm-native-select>
        </div>
        <div hlmField class="w-44 gap-2">
          <label hlmFieldLabel for="filter-collection">Collection</label>
          <hlm-native-select
            selectId="filter-collection"
            class="w-full"
            data-testid="filter-collection"
            [value]="collectionId()"
            (valueChange)="collectionId.set($any($event))"
          >
            <option hlmNativeSelectOption value="">Alle</option>
            @for (collection of collections(); track collection.id) {
              <option hlmNativeSelectOption [value]="collection.id">{{ collection.name }}</option>
            }
          </hlm-native-select>
        </div>
        <div hlmField class="w-48 gap-2">
          <label hlmFieldLabel for="filter-actor">Actor</label>
          <hlm-native-select
            selectId="filter-actor"
            class="w-full"
            data-testid="filter-actor"
            [value]="actorId()"
            (valueChange)="actorId.set($any($event))"
          >
            <option hlmNativeSelectOption value="">Alle</option>
            @for (member of members.members(); track member.actorId) {
              <option hlmNativeSelectOption [value]="member.actorId">
                {{ member.name }} ({{ member.kind === 'agent' ? 'Agent' : 'Mensch' }})
              </option>
            }
          </hlm-native-select>
        </div>
        <div hlmField class="w-40 gap-2">
          <label hlmFieldLabel for="filter-since">Seit</label>
          <input
            hlmInput
            id="filter-since"
            type="date"
            data-testid="filter-since"
            [value]="since()"
            (change)="since.set($any($event.target).value)"
          />
        </div>
        <hlm-popover align="start">
          <button
            hlmBtn
            hlmPopoverTrigger
            type="button"
            variant="outline"
            data-testid="filter-types-summary"
          >
            Typen
            @if (types().length > 0) {
              ({{ types().length }})
            }
            <ng-icon name="lucideChevronDown" />
          </button>
          <div
            hlmPopoverContent
            *hlmPopoverPortal="let ctx"
            class="w-72"
            role="group"
            aria-label="Typen filtern"
            data-testid="filter-types"
          >
            <div class="flex max-h-80 flex-col gap-2 overflow-y-auto">
              @for (type of typeOptions; track type.value) {
                <div class="flex items-center gap-2">
                  <hlm-checkbox
                    [inputId]="'filter-type-' + type.value"
                    [attr.data-testid]="'filter-type-' + type.value"
                    [checked]="types().includes(type.value)"
                    (checkedChange)="toggleType(type.value)"
                  />
                  <label hlmLabel class="font-normal" [for]="'filter-type-' + type.value">{{
                    type.label
                  }}</label>
                </div>
              }
            </div>
          </div>
        </hlm-popover>
        @if (filtered()) {
          <button hlmBtn type="button" variant="ghost" data-testid="filter-reset" (click)="reset()">
            <ng-icon name="lucideX" />
            Zurücksetzen
          </button>
        }
      </form>

      @if (error(); as message) {
        <div hlmAlert variant="destructive" data-testid="activity-error">
          <ng-icon name="lucideCircleAlert" />
          <p hlmAlertDescription>{{ message }}</p>
        </div>
      }
      @if (events().length > 0) {
        <ul class="divide-y rounded-xl border" data-testid="activity-list">
          @for (event of events(); track event.id) {
            <li>
              <ag-activity-entry [event]="event" />
            </li>
          }
        </ul>
      } @else if (loading()) {
        <div class="flex flex-col gap-2" aria-busy="true" aria-label="Wird geladen">
          <hlm-skeleton class="h-10 rounded-lg" />
          <hlm-skeleton class="h-10 rounded-lg" />
          <hlm-skeleton class="h-10 rounded-lg" />
        </div>
      } @else if (!error()) {
        <div
          class="flex flex-col items-center gap-1 rounded-xl border border-dashed p-10 text-center"
        >
          <p class="font-medium" data-testid="activity-empty">Keine Ereignisse.</p>
          <p class="text-sm text-muted-foreground">
            Für diese Auswahl gibt es noch keine Aktivität.
          </p>
        </div>
      }
      @if (nextCursor()) {
        <div>
          <button
            hlmBtn
            type="button"
            variant="outline"
            data-testid="load-more"
            [disabled]="loading()"
            (click)="loadMore()"
          >
            Mehr laden
          </button>
        </div>
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
