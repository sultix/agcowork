import { ChangeDetectionStrategy, Component, computed, inject, resource } from '@angular/core';
import { RouterLink } from '@angular/router';
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
  imports: [ActivityEntryComponent, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="mx-auto max-w-5xl p-6">
      <h1 class="text-xl font-semibold">Spaces</h1>
      @if (tree.error()) {
        <p class="mt-3 text-sm text-danger" role="alert" data-testid="load-error">
          Spaces konnten nicht geladen werden.
        </p>
      }
      <div class="mt-4 grid gap-4 sm:grid-cols-2 lg:grid-cols-3" data-testid="space-cards">
        @for (space of tree.spaces(); track space.id) {
          <a
            [routerLink]="['/w', context.slug(), 'spaces', space.id]"
            class="rounded-lg border border-border bg-surface p-4 hover:bg-surface-hover"
            data-testid="space-card"
          >
            <h2 class="font-semibold">
              @if (space.icon) {
                <span aria-hidden="true">{{ space.icon }} </span>
              }
              {{ space.name }}
            </h2>
            @if (space.description) {
              <p class="mt-1 text-sm text-muted">{{ space.description }}</p>
            }
          </a>
        } @empty {
          @if (!tree.isLoading() && !tree.error()) {
            <p class="text-sm text-muted">Dieser Workspace hat noch keine Spaces.</p>
          }
        }
      </div>

      <h2 class="mt-10 text-lg font-semibold">Zuletzt geändert</h2>
      @if (activityError(); as message) {
        <p class="mt-3 text-sm text-danger" role="alert" data-testid="activity-error">
          {{ message }}
        </p>
      }
      <ul
        class="mt-3 divide-y divide-border rounded-lg border border-border"
        data-testid="recent-activity"
      >
        @for (event of events(); track event.id) {
          <li>
            <ag-activity-entry [event]="event" />
          </li>
        } @empty {
          <li class="px-4 py-3 text-sm text-muted">Noch keine Änderungen.</li>
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
