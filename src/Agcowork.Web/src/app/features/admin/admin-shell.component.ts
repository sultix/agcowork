import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { SessionMenuComponent } from '../../shared/ui/session-menu.component';

/** Layout of `/admin/*`: header with navigation between the administration pages. */
@Component({
  selector: 'ag-admin-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, SessionMenuComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="flex items-center justify-between border-b border-border px-6 py-3">
      <nav class="flex items-center gap-4 text-sm">
        <a routerLink="/workspaces" class="text-muted hover:text-fg">‹ Workspaces</a>
        <span class="font-semibold">Administration</span>
        <a
          routerLink="/admin/workspaces"
          routerLinkActive="bg-surface-hover text-fg"
          class="rounded-md px-2 py-1 text-muted hover:bg-surface-hover"
          >Workspaces</a
        >
        <a
          routerLink="/admin/actors"
          routerLinkActive="bg-surface-hover text-fg"
          class="rounded-md px-2 py-1 text-muted hover:bg-surface-hover"
          >Actors</a
        >
      </nav>
      <ag-session-menu />
    </header>
    <router-outlet />
  `,
})
export class AdminShellComponent {}
