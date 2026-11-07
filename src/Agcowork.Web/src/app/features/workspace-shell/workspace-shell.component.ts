import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { BackendStatus, HealthService } from '../../core/api/health.service';
import { ThemeService } from '../../core/theme/theme.service';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { SessionMenuComponent } from '../../shared/ui/session-menu.component';

const STATUS_LABELS: Record<BackendStatus, string> = {
  checking: 'wird geprüft',
  ok: 'ok',
  unreachable: 'nicht erreichbar',
};

/** Layout for everything under `/w/:workspaceSlug`: sidebar, header and content outlet. */
@Component({
  selector: 'ag-workspace-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, SessionMenuComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex min-h-screen">
      <aside class="w-56 shrink-0 border-r border-border bg-surface p-4">
        <a routerLink="/workspaces" class="text-sm text-muted hover:text-fg">Workspaces</a>
        <div class="mt-1 truncate font-semibold" data-testid="workspace-slug">{{ slug() }}</div>
        <nav class="mt-6 flex flex-col gap-1 text-sm">
          @for (item of nav; track item.path) {
            <a
              [routerLink]="item.path"
              routerLinkActive="bg-surface-hover text-fg"
              [routerLinkActiveOptions]="{ exact: item.exact }"
              class="rounded-md px-2 py-1 text-muted hover:bg-surface-hover"
              >{{ item.label }}</a
            >
          }
        </nav>
      </aside>
      <div class="flex min-w-0 flex-1 flex-col">
        <header class="flex items-center justify-between border-b border-border px-6 py-3">
          <span class="text-sm text-muted">Workspace {{ slug() }}</span>
          <div class="flex items-center gap-4 text-sm">
            <span data-testid="backend-status" [class]="statusClass()">
              Backend: {{ statusLabel() }}
            </span>
            <button
              type="button"
              class="rounded-md border border-border px-2 py-1 hover:bg-surface-hover"
              (click)="theme.cycle()"
            >
              Theme: {{ theme.mode() }}
            </button>
            <ag-session-menu />
          </div>
        </header>
        <main class="min-w-0 flex-1">
          <router-outlet />
        </main>
      </div>
    </div>
  `,
})
export class WorkspaceShellComponent {
  private readonly workspace = inject(WorkspaceContext);
  private readonly health = inject(HealthService);
  protected readonly theme = inject(ThemeService);

  protected readonly slug = this.workspace.slug;

  protected readonly nav = [
    { path: '.', label: 'Start', exact: true },
    { path: 'search', label: 'Suche', exact: false },
    { path: 'activity', label: 'Aktivität', exact: false },
    { path: 'settings/members', label: 'Mitglieder', exact: false },
  ];

  protected readonly statusLabel = computed(() => STATUS_LABELS[this.health.status()]);
  protected readonly statusClass = computed(() => {
    switch (this.health.status()) {
      case 'ok':
        return 'text-success';
      case 'unreachable':
        return 'text-danger';
      default:
        return 'text-muted';
    }
  });
}
