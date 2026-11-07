import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCircleAlert, lucideInfo, lucidePlus, lucideRefreshCw } from '@ng-icons/lucide';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCardImports } from '@spartan-ng/helm/card';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { describeError } from '../../shared/ui/api-error-text';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { SessionMenuComponent } from '../../shared/ui/session-menu.component';
import { ThemeToggleComponent } from '../../shared/ui/theme-toggle.component';
import { WorkspacesStore } from './workspaces.store';

const NOTICES: Record<string, string> = {
  admin_required: 'Der Administrationsbereich ist nur für Instanz-Admins zugänglich.',
};

/**
 * `/workspaces` (and `/admin/workspaces`, embedded in the admin layout): the workspaces of the
 * signed-in actor with role and archived marker.
 */
@Component({
  selector: 'ag-workspace-list',
  imports: [
    RouterLink,
    NgIcon,
    HlmAlertImports,
    HlmButton,
    HlmCardImports,
    HlmSkeleton,
    SessionMenuComponent,
    ThemeToggleComponent,
    BadgeComponent,
  ],
  providers: [provideIcons({ lucideCircleAlert, lucideInfo, lucidePlus, lucideRefreshCw })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (!embedded()) {
      <header class="flex h-14 items-center gap-2 border-b px-4 sm:px-6">
        <span class="text-sm font-semibold tracking-tight">agcowork</span>
        <div class="ml-auto flex items-center gap-1">
          <ag-theme-toggle />
          <ag-session-menu />
        </div>
      </header>
    }
    <main class="mx-auto flex max-w-4xl flex-col gap-6 p-6">
      @if (noticeText(); as text) {
        <div hlmAlert data-testid="notice">
          <ng-icon name="lucideInfo" />
          <p hlmAlertDescription>{{ text }}</p>
        </div>
      }
      <div class="flex flex-wrap items-start justify-between gap-4">
        <div class="flex flex-col gap-1">
          <h1 class="text-2xl font-semibold tracking-tight">Workspaces</h1>
          @if (embedded()) {
            <p class="text-sm text-muted-foreground" data-testid="membership-hint">
              Angezeigt werden die Workspaces, in denen du Mitglied bist; die API liefert auch
              Instanz-Admins keine fremden Workspaces.
            </p>
          } @else {
            <p class="text-sm text-muted-foreground">Workspaces, in denen du Mitglied bist.</p>
          }
        </div>
        @if (store.canCreate()) {
          <a hlmBtn routerLink="/workspaces/new" data-testid="create-workspace">
            <ng-icon name="lucidePlus" />
            Neuer Workspace
          </a>
        }
      </div>

      @if (store.error(); as error) {
        <div hlmAlert variant="destructive" data-testid="load-error">
          <ng-icon name="lucideCircleAlert" />
          <p hlmAlertDescription>{{ describe(error) }}</p>
          <button hlmAlertAction hlmBtn variant="outline" size="sm" (click)="store.reload()">
            <ng-icon name="lucideRefreshCw" />
            Erneut laden
          </button>
        </div>
      } @else if (store.isLoading() && store.workspaces().length === 0) {
        <div class="grid gap-4 sm:grid-cols-2" aria-busy="true" aria-label="Wird geladen">
          @for (row of skeletonRows; track row) {
            <hlm-skeleton class="h-24 rounded-xl" />
          }
        </div>
      } @else if (store.workspaces().length === 0) {
        <div
          class="flex flex-col items-center gap-1 rounded-xl border border-dashed p-10 text-center"
        >
          <p class="font-medium">Noch keine Workspaces</p>
          <p class="text-sm text-muted-foreground" data-testid="empty">
            Du bist noch in keinem Workspace Mitglied. Bitte einen Admin, dich hinzuzufügen
            @if (store.canCreate()) {
              <span> oder lege einen neuen Workspace an</span>
            }
            .
          </p>
        </div>
      } @else {
        <ul class="grid gap-4 sm:grid-cols-2">
          @for (workspace of store.workspaces(); track workspace.id) {
            <li data-testid="workspace-row">
              <a
                hlmCard
                [routerLink]="['/w', workspace.slug]"
                class="h-full outline-none transition-colors hover:bg-muted/50 focus-visible:ring-3 focus-visible:ring-ring/50"
              >
                <div hlmCardHeader>
                  <h2 hlmCardTitle class="truncate">{{ workspace.name }}</h2>
                  <p hlmCardDescription class="truncate font-mono text-xs">
                    /w/{{ workspace.slug }}
                  </p>
                  <div hlmCardAction class="flex items-center gap-1.5">
                    @if (workspace.archivedAt) {
                      <ag-badge tone="danger">archiviert</ag-badge>
                    }
                    <ag-badge tone="neutral">{{ workspace.role }}</ag-badge>
                  </div>
                </div>
                @if (workspace.description) {
                  <p hlmCardContent class="line-clamp-2 text-muted-foreground">
                    {{ workspace.description }}
                  </p>
                }
              </a>
            </li>
          }
        </ul>
      }
    </main>
  `,
})
export class WorkspaceListComponent {
  protected readonly store = inject(WorkspacesStore);

  /** Route data: rendered inside the admin layout (no own header). */
  readonly embedded = input(false);
  /** Query parameter `notice` (set by guards that redirect here). */
  readonly notice = input<string>();

  protected readonly noticeText = computed(() => NOTICES[this.notice() ?? ''] ?? null);

  protected readonly skeletonRows = [1, 2];

  protected describe(error: unknown): string {
    return describeError(error);
  }
}
