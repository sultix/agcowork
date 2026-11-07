import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideActivity,
  lucideHouse,
  lucideSearch,
  lucideSettings,
  lucideUsers,
} from '@ng-icons/lucide';
import { HlmBreadcrumbImports } from '@spartan-ng/helm/breadcrumb';
import { HlmSeparator } from '@spartan-ng/helm/separator';
import {
  HlmSidebarImports,
  HlmSidebarService,
  provideHlmSidebarConfig,
} from '@spartan-ng/helm/sidebar';
import { HlmTooltip } from '@spartan-ng/helm/tooltip';
import { BackendStatus, HealthService } from '../../core/api/health.service';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { ADMIN_ROLES, WorkspacesStore } from '../workspaces/workspaces.store';
import { WorkspaceTreeComponent } from '../spaces/workspace-tree.component';
import { WorkspaceSwitcherComponent } from '../workspaces/workspace-switcher.component';
import { SessionMenuComponent } from '../../shared/ui/session-menu.component';
import { ThemeToggleComponent } from '../../shared/ui/theme-toggle.component';

const STATUS_LABELS: Record<BackendStatus, string> = {
  checking: 'wird geprüft',
  ok: 'ok',
  unreachable: 'nicht erreichbar',
};

const STATUS_DOTS: Record<BackendStatus, string> = {
  checking: 'bg-muted-foreground/50',
  ok: 'bg-success',
  unreachable: 'bg-destructive',
};

interface NavItem {
  readonly path: string;
  readonly label: string;
  readonly icon: string;
  readonly exact: boolean;
}

/**
 * Layout for everything under `/w/:workspaceSlug`: collapsible Helm sidebar (workspace switcher,
 * navigation, space tree), header (sidebar toggle, breadcrumb, backend status, theme, session) and
 * the content outlet.
 */
@Component({
  selector: 'ag-workspace-shell',
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    NgIcon,
    HlmBreadcrumbImports,
    HlmSeparator,
    HlmSidebarImports,
    HlmTooltip,
    SessionMenuComponent,
    ThemeToggleComponent,
    WorkspaceSwitcherComponent,
    WorkspaceTreeComponent,
  ],
  providers: [
    provideIcons({ lucideActivity, lucideHouse, lucideSearch, lucideSettings, lucideUsers }),
    // Helm's sidebar service is root-provided and reads its config from the root injector, so the
    // shell provides its own instance together with the config. The keyboard shortcut is off:
    // the service listens for Mod+B on window without checking defaultPrevented, which would also
    // collapse the sidebar while Milkdown applies bold.
    HlmSidebarService,
    provideHlmSidebarConfig({ sidebarKeyboardShortcut: '' }),
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div hlmSidebarWrapper>
      <hlm-sidebar
        collapsible="icon"
        srOnlySheetTitle="Navigation"
        srOnlySheetDescription="Workspace-Navigation und Spaces"
      >
        <div hlmSidebarHeader>
          <ag-workspace-switcher [slug]="slug()" />
        </div>
        <div hlmSidebarContent>
          <div hlmSidebarGroup>
            <nav aria-label="Workspace">
              <ul hlmSidebarMenu>
                @for (item of nav(); track item.path) {
                  <li hlmSidebarMenuItem>
                    <a
                      hlmSidebarMenuButton
                      [routerLink]="item.path"
                      routerLinkActive
                      #link="routerLinkActive"
                      [routerLinkActiveOptions]="{ exact: item.exact }"
                      ariaCurrentWhenActive="page"
                      [isActive]="link.isActive"
                      [tooltip]="item.label"
                      data-testid="nav-link"
                    >
                      <ng-icon [name]="item.icon" />
                      <span>{{ item.label }}</span>
                    </a>
                  </li>
                }
              </ul>
            </nav>
          </div>
          <div hlmSidebarGroup>
            <div hlmSidebarGroupLabel>Spaces</div>
            <ag-workspace-tree />
          </div>
        </div>
      </hlm-sidebar>

      <main hlmSidebarInset class="min-w-0">
        <header
          class="sticky top-0 z-10 flex h-14 shrink-0 items-center gap-2 border-b bg-background px-4"
        >
          <!-- Icon and screen-reader text come from the Helm trigger's own template. -->
          <button
            hlmSidebarTrigger
            srOnlyText="Seitenleiste ein- oder ausklappen"
            aria-label="Seitenleiste ein- oder ausklappen"
          ></button>
          <hlm-separator orientation="vertical" class="mr-1 h-4 self-center" />
          <nav hlmBreadcrumb aria-label="Workspace-Pfad" class="min-w-0">
            <ol hlmBreadcrumbList>
              <li hlmBreadcrumbItem>
                <span hlmBreadcrumbPage class="truncate" data-testid="header-workspace">{{
                  workspaceName()
                }}</span>
              </li>
            </ol>
          </nav>
          <div class="ml-auto flex items-center gap-1">
            <span
              class="inline-flex size-9 items-center justify-center rounded-md outline-none focus-visible:ring-3 focus-visible:ring-ring/50"
              tabindex="0"
              role="status"
              data-testid="backend-status"
              [attr.data-status]="health.status()"
              [hlmTooltip]="statusText()"
            >
              <span class="size-2 rounded-full" [class]="statusDot()" aria-hidden="true"></span>
              <span class="sr-only">{{ statusText() }}</span>
            </span>
            <ag-theme-toggle />
            <ag-session-menu />
          </div>
        </header>
        <div class="min-w-0 flex-1">
          <router-outlet />
        </div>
      </main>
    </div>
  `,
})
export class WorkspaceShellComponent {
  private readonly workspace = inject(WorkspaceContext);
  protected readonly health = inject(HealthService);

  protected readonly slug = this.workspace.slug;

  private readonly workspaces = inject(WorkspacesStore);

  protected readonly workspaceName = computed(
    () => this.workspaces.find(this.slug())?.name ?? this.slug() ?? '',
  );

  /** "Einstellungen" is offered to admins and owners only. */
  protected readonly nav = computed<readonly NavItem[]>(() => {
    const role = this.workspaces.find(this.slug())?.role;
    return [
      { path: '.', label: 'Start', icon: 'lucideHouse', exact: true },
      { path: 'search', label: 'Suche', icon: 'lucideSearch', exact: false },
      { path: 'activity', label: 'Aktivität', icon: 'lucideActivity', exact: false },
      { path: 'settings/members', label: 'Mitglieder', icon: 'lucideUsers', exact: false },
      ...(role && ADMIN_ROLES.includes(role)
        ? [{ path: 'settings', label: 'Einstellungen', icon: 'lucideSettings', exact: true }]
        : []),
    ];
  });

  protected readonly statusText = computed(() => `Backend: ${STATUS_LABELS[this.health.status()]}`);
  protected readonly statusDot = computed(() => STATUS_DOTS[this.health.status()]);
}
