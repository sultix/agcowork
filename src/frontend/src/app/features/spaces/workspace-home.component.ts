import { ChangeDetectionStrategy, Component, computed, inject, resource } from '@angular/core';
import { RouterLink } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCircleAlert } from '@ng-icons/lucide';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmCardImports } from '@spartan-ng/helm/card';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { MembersStore } from '../../core/workspace/members.store';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { describeError } from '../../shared/ui/api-error-text';
import { ActivityEntryComponent, ActivityEvent } from '../activity/activity-entry.component';
import { WorkspaceTreeStore } from './workspace-tree.store';

/** `/w/:slug`: the spaces as cards and the ten most recent record changes. */
@Component({
  selector: 'ag-workspace-home',
  imports: [
    ActivityEntryComponent,
    RouterLink,
    NgIcon,
    HlmAlertImports,
    HlmCardImports,
    HlmSkeleton,
  ],
  providers: [provideIcons({ lucideCircleAlert })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="mx-auto flex max-w-5xl flex-col gap-6 p-6">
      <div class="flex flex-col gap-1">
        <h1 class="text-2xl font-semibold tracking-tight">Spaces</h1>
        <p class="text-sm text-muted-foreground">Die Bereiche dieses Workspaces.</p>
      </div>
      @if (tree.error()) {
        <div hlmAlert variant="destructive" data-testid="load-error">
          <ng-icon name="lucideCircleAlert" />
          <p hlmAlertDescription>Spaces konnten nicht geladen werden.</p>
        </div>
      }
      <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3" data-testid="space-cards">
        @for (space of tree.spaces(); track space.id) {
          <a
            hlmCard
            [routerLink]="['/w', context.slug(), 'spaces', space.id]"
            class="outline-none transition-colors hover:bg-muted/50 focus-visible:ring-3 focus-visible:ring-ring/50"
            data-testid="space-card"
          >
            <div hlmCardHeader>
              <h2 hlmCardTitle>
                @if (space.icon) {
                  <span aria-hidden="true">{{ space.icon }} </span>
                }
                {{ space.name }}
              </h2>
              @if (space.description) {
                <p hlmCardDescription class="line-clamp-2">{{ space.description }}</p>
              }
            </div>
          </a>
        }
      </div>
      @if (tree.spaces().length === 0) {
        @if (tree.isLoading()) {
          <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3" aria-busy="true">
            <hlm-skeleton class="h-24 rounded-xl" />
            <hlm-skeleton class="h-24 rounded-xl" />
            <hlm-skeleton class="hidden h-24 rounded-xl lg:block" />
          </div>
        } @else if (!tree.error()) {
          <div
            class="flex flex-col items-center gap-1 rounded-xl border border-dashed p-10 text-center"
            data-testid="spaces-empty"
          >
            <p class="font-medium">Noch keine Spaces</p>
            <p class="text-sm text-muted-foreground">Dieser Workspace hat noch keine Spaces.</p>
          </div>
        }
      }

      <h2 class="text-lg font-semibold tracking-tight">Zuletzt geändert</h2>
      @if (activityError(); as message) {
        <div hlmAlert variant="destructive" data-testid="activity-error">
          <ng-icon name="lucideCircleAlert" />
          <p hlmAlertDescription>{{ message }}</p>
        </div>
      }
      <ul class="divide-y rounded-xl border" data-testid="recent-activity">
        @for (event of events(); track event.id) {
          <li>
            <ag-activity-entry [event]="event" />
          </li>
        } @empty {
          <li class="px-4 py-6 text-center text-sm text-muted-foreground">
            Noch keine Änderungen.
          </li>
        }
      </ul>
    </section>
  `,
})
export class WorkspaceHomeComponent {
  protected readonly tree = inject(WorkspaceTreeStore);
  protected readonly context = inject(WorkspaceContext);
  /** Loads the names of the actors in the activity entries early. */
  private readonly members = inject(MembersStore);
  private readonly api = inject(ApiClient);

  private readonly activity = resource<ActivityEvent[], string | undefined>({
    params: () => this.context.slug() ?? undefined,
    loader: async ({ params: slug }) =>
      (await firstValueFrom(this.api.workspace(slug).get('/activity', { query: { limit: 10 } })))
        .items,
  });

  /** Only record events: this list shows what was edited recently. */
  protected readonly events = computed(() =>
    (this.activity.hasValue() ? this.activity.value() : []).filter(
      (e) => e.targetType === 'record',
    ),
  );
  protected readonly activityError = computed(() =>
    this.activity.error() ? describeError(this.activity.error()) : null,
  );
}
