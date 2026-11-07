import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { describeError } from '../../shared/ui/api-error-text';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { SessionMenuComponent } from '../../shared/ui/session-menu.component';
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
  imports: [RouterLink, SessionMenuComponent, BadgeComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (!embedded()) {
      <header class="flex items-center justify-end border-b border-border px-6 py-3">
        <ag-session-menu />
      </header>
    }
    <main class="mx-auto max-w-2xl p-6">
      @if (noticeText(); as text) {
        <p
          class="mb-4 rounded-md border border-danger px-3 py-2 text-sm text-danger"
          role="alert"
          data-testid="notice"
        >
          {{ text }}
        </p>
      }
      <div class="flex items-center justify-between">
        <h1 class="text-xl font-semibold">Workspaces</h1>
        @if (store.canCreate()) {
          <a
            routerLink="/workspaces/new"
            class="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg"
            data-testid="create-workspace"
            >Neuer Workspace</a
          >
        }
      </div>
      @if (embedded()) {
        <p class="mt-1 text-sm text-muted" data-testid="membership-hint">
          Angezeigt werden die Workspaces, in denen du Mitglied bist; die API liefert auch
          Instanz-Admins keine fremden Workspaces.
        </p>
      }

      @if (store.error(); as error) {
        <p class="mt-4 text-sm text-danger" role="alert" data-testid="load-error">
          {{ describe(error) }}
          <button type="button" class="underline" (click)="store.reload()">Erneut laden</button>
        </p>
      } @else if (store.isLoading() && store.workspaces().length === 0) {
        <p class="mt-4 text-sm text-muted">Wird geladen …</p>
      } @else if (store.workspaces().length === 0) {
        <p class="mt-4 text-sm text-muted" data-testid="empty">
          Du bist noch in keinem Workspace Mitglied. Bitte einen Admin, dich hinzuzufügen
          @if (store.canCreate()) {
            <span> oder lege einen neuen Workspace an</span>
          }
          .
        </p>
      } @else {
        <ul class="mt-4 divide-y divide-border rounded-lg border border-border bg-surface">
          @for (workspace of store.workspaces(); track workspace.id) {
            <li data-testid="workspace-row">
              <a
                [routerLink]="['/w', workspace.slug]"
                class="flex items-center justify-between gap-3 px-4 py-3 hover:bg-surface-hover"
              >
                <span class="min-w-0">
                  <span class="block truncate">{{ workspace.name }}</span>
                  <span class="block text-sm text-muted">{{ workspace.slug }}</span>
                </span>
                <span class="flex shrink-0 items-center gap-2">
                  @if (workspace.archivedAt) {
                    <ag-badge tone="danger">archiviert</ag-badge>
                  }
                  <ag-badge tone="accent">{{ workspace.role }}</ag-badge>
                </span>
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

  protected describe(error: unknown): string {
    return describeError(error);
  }
}
