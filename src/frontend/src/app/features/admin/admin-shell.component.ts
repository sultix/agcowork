import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideArrowLeft } from '@ng-icons/lucide';
import { HlmButton } from '@spartan-ng/helm/button';
import { SessionMenuComponent } from '../../shared/ui/session-menu.component';
import { ThemeToggleComponent } from '../../shared/ui/theme-toggle.component';

/** Layout of `/admin/*`: header with navigation between the administration pages. */
@Component({
  selector: 'ag-admin-shell',
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    NgIcon,
    HlmButton,
    SessionMenuComponent,
    ThemeToggleComponent,
  ],
  providers: [provideIcons({ lucideArrowLeft })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="flex h-14 items-center gap-2 border-b px-4 sm:px-6">
      <nav class="flex items-center gap-1 text-sm">
        <a hlmBtn variant="ghost" size="sm" routerLink="/workspaces">
          <ng-icon name="lucideArrowLeft" />
          Workspaces
        </a>
        <span class="px-2 font-semibold tracking-tight">Administration</span>
        <a
          hlmBtn
          variant="ghost"
          size="sm"
          routerLink="/admin/workspaces"
          routerLinkActive="bg-muted"
          >Workspaces</a
        >
        <a hlmBtn variant="ghost" size="sm" routerLink="/admin/actors" routerLinkActive="bg-muted"
          >Actors</a
        >
      </nav>
      <div class="ml-auto flex items-center gap-1">
        <ag-theme-toggle />
        <ag-session-menu />
      </div>
    </header>
    <router-outlet />
  `,
})
export class AdminShellComponent {}
