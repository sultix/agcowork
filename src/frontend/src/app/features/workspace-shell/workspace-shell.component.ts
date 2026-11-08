import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  inject,
  viewChild,
} from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideActivity,
  lucideAtSign,
  lucideHouse,
  lucideRefreshCw,
  lucideSearch,
  lucideSettings,
  lucideUsers,
  lucideWifiOff,
} from '@ng-icons/lucide';
import { HlmBreadcrumbImports } from '@spartan-ng/helm/breadcrumb';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmSeparator } from '@spartan-ng/helm/separator';
import {
  HlmSidebarImports,
  HlmSidebarService,
  provideHlmSidebarConfig,
} from '@spartan-ng/helm/sidebar';
import { HlmTooltip } from '@spartan-ng/helm/tooltip';
import { BackendStatus, HealthService } from '../../core/api/health.service';
import { RealtimeService, RealtimeStatus } from '../../core/realtime/realtime.service';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { ADMIN_ROLES, WorkspacesStore } from '../workspaces/workspaces.store';
import { BADGE_LIMIT, MentionsStore, badgeLabel } from '../mentions/mentions.store';
import { WorkspaceTreeComponent } from '../spaces/workspace-tree.component';
import { WorkspaceSwitcherComponent } from '../workspaces/workspace-switcher.component';
import { SessionMenuComponent } from '../../shared/ui/session-menu.component';
import { ThemeToggleComponent } from '../../shared/ui/theme-toggle.component';

const STATUS_LABELS: Record<BackendStatus, string> = {
  checking: 'wird geprüft',
  ok: 'ok',
  degraded: 'eingeschränkt',
  unreachable: 'nicht erreichbar',
};

const STATUS_DOTS: Record<BackendStatus, string> = {
  checking: 'bg-muted-foreground/50',
  ok: 'bg-success',
  degraded: 'bg-amber-500',
  unreachable: 'bg-destructive',
};

/** Text of the live-update indicator; `null`: nothing to show (connected, or about to be). */
const REALTIME_LABELS: Record<RealtimeStatus, string | null> = {
  idle: null,
  connecting: null,
  live: null,
  reconnecting:
    'Live-Aktualisierung unterbrochen, Verbindung wird wiederhergestellt (Klick verbindet sofort neu)',
  offline: 'Live-Aktualisierung getrennt (Klick zum erneuten Verbinden)',
};

/** Whether the shortcut modifier is Cmd (macOS, iOS) rather than Ctrl. */
export function isApplePlatform(): boolean {
  return /Mac|iPhone|iPad/i.test(navigator.platform || navigator.userAgent);
}

interface NavItem {
  readonly path: string;
  readonly label: string;
  readonly icon: string;
  readonly exact: boolean;
}

/**
 * Layout for everything under `/w/:workspaceSlug`: collapsible Helm sidebar (workspace switcher,
 * navigation, space tree), header (sidebar toggle, breadcrumb, live-update and backend status,
 * theme, session) and the content outlet. Keeps the realtime connection of the open workspace.
 */
@Component({
  selector: 'ag-workspace-shell',
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    NgIcon,
    HlmBreadcrumbImports,
    HlmInput,
    HlmSeparator,
    HlmSidebarImports,
    HlmTooltip,
    SessionMenuComponent,
    ThemeToggleComponent,
    WorkspaceSwitcherComponent,
    WorkspaceTreeComponent,
  ],
  providers: [
    provideIcons({
      lucideActivity,
      lucideAtSign,
      lucideHouse,
      lucideRefreshCw,
      lucideSearch,
      lucideSettings,
      lucideUsers,
      lucideWifiOff,
    }),
    // Helm's sidebar service is root-provided and reads its config from the root injector, so the
    // shell provides its own instance together with the config. The keyboard shortcut is off:
    // the service listens for Mod+B on window without checking defaultPrevented, which would also
    // collapse the sidebar while Milkdown applies bold.
    HlmSidebarService,
    provideHlmSidebarConfig({ sidebarKeyboardShortcut: '' }),
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:keydown)': 'onKeydown($event)' },
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
                      @if (item.path === 'mentions' && mentions.unreadCount() > 0) {
                        <span class="sr-only" data-testid="mentions-unread-text">{{
                          mentionsText()
                        }}</span>
                      }
                    </a>
                    @if (item.path === 'mentions' && mentions.unreadCount() > 0) {
                      <span hlmSidebarMenuBadge aria-hidden="true" data-testid="mentions-badge">{{
                        mentionsBadge()
                      }}</span>
                    }
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
          <form
            class="relative ml-auto w-full max-w-64"
            role="search"
            data-testid="search-form-header"
            (submit)="search($event, searchField.value)"
          >
            <ng-icon
              name="lucideSearch"
              class="pointer-events-none absolute top-1/2 left-2.5 -translate-y-1/2 text-muted-foreground"
              aria-hidden="true"
            />
            <input
              #searchField
              hlmInput
              type="search"
              class="h-9 pl-8"
              placeholder="Suchen (Strg/Cmd+K)"
              aria-label="Im Workspace suchen"
              aria-keyshortcuts="Control+K Meta+K"
              autocomplete="off"
              data-testid="search-field"
            />
          </form>
          <div class="flex items-center gap-1">
            @if (realtimeText(); as label) {
              <button
                type="button"
                class="inline-flex size-9 items-center justify-center rounded-md text-muted-foreground outline-none focus-visible:ring-3 focus-visible:ring-ring/50"
                data-testid="realtime-status"
                [attr.data-status]="realtime.status()"
                [attr.aria-label]="label"
                [hlmTooltip]="label"
                (click)="realtime.retry()"
              >
                @if (realtime.status() === 'offline') {
                  <ng-icon name="lucideWifiOff" class="text-destructive" />
                } @else {
                  <ng-icon
                    name="lucideRefreshCw"
                    class="motion-safe:animate-spin motion-safe:[animation-duration:2s]"
                  />
                }
              </button>
            }
            <span class="sr-only" role="status" data-testid="realtime-status-live">{{
              realtimeText() ?? ''
            }}</span>
            <button
              type="button"
              class="inline-flex size-9 items-center justify-center rounded-md outline-none focus-visible:ring-3 focus-visible:ring-ring/50"
              data-testid="backend-status"
              [attr.data-status]="health.status()"
              [attr.aria-label]="statusText()"
              [hlmTooltip]="statusText()"
              (click)="health.reload()"
            >
              <span class="size-2 rounded-full" [class]="statusDot()" aria-hidden="true"></span>
            </button>
            <!-- Live region beside the button: the button's children are presentational. -->
            <span class="sr-only" role="status" data-testid="backend-status-live">{{
              statusText()
            }}</span>
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
  private readonly router = inject(Router);
  private readonly searchField = viewChild.required<ElementRef<HTMLInputElement>>('searchField');
  protected readonly health = inject(HealthService);
  protected readonly realtime = inject(RealtimeService);
  protected readonly mentions = inject(MentionsStore);

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
      { path: 'mentions', label: 'Erwähnungen', icon: 'lucideAtSign', exact: false },
      { path: 'settings/members', label: 'Mitglieder', icon: 'lucideUsers', exact: false },
      ...(role && ADMIN_ROLES.includes(role)
        ? [{ path: 'settings', label: 'Einstellungen', icon: 'lucideSettings', exact: true }]
        : []),
    ];
  });

  protected readonly statusText = computed(() => {
    const status = this.health.status();
    const hint = status === 'ok' || status === 'checking' ? '' : ' (Klick zum erneuten Prüfen)';
    return `Backend: ${STATUS_LABELS[status]}${hint}`;
  });
  protected readonly mentionsBadge = computed(() => badgeLabel(this.mentions.unreadCount()));
  protected readonly mentionsText = computed(() => {
    const count = this.mentions.unreadCount();
    if (count > BADGE_LIMIT) {
      return `Mehr als ${BADGE_LIMIT} ungelesene Erwähnungen`;
    }
    return count === 1 ? '1 ungelesene Erwähnung' : `${count} ungelesene Erwähnungen`;
  });
  protected readonly statusDot = computed(() => STATUS_DOTS[this.health.status()]);
  protected readonly realtimeText = computed(() => REALTIME_LABELS[this.realtime.status()]);

  /**
   * Ctrl/Cmd+K focuses the search field from anywhere in the workspace. Events another handler
   * already used (e.g. an editor) are left alone.
   */
  protected onKeydown(event: KeyboardEvent): void {
    // Cmd+K on macOS, Ctrl+K elsewhere (on macOS Ctrl+K deletes to the end of the line).
    const modifier = isApplePlatform()
      ? event.metaKey && !event.ctrlKey
      : event.ctrlKey && !event.metaKey;
    if (
      !modifier ||
      event.defaultPrevented ||
      event.altKey ||
      event.shiftKey ||
      typeof event.key !== 'string' ||
      event.key.toLowerCase() !== 'k'
    ) {
      return;
    }
    event.preventDefault();
    const field = this.searchField().nativeElement;
    field.focus();
    field.select();
  }

  /** Enter in the search field: open the results page for the text (empty: the page's hint). */
  protected search(event: Event, text: string): void {
    event.preventDefault();
    const slug = this.slug();
    const q = text.trim();
    if (slug) {
      void this.router.navigate(['/w', slug, 'search'], { queryParams: q ? { q } : {} });
    }
  }

  constructor() {
    // One hub connection per tab while a workspace is open; the service follows the slug.
    this.realtime.start();
    inject(DestroyRef).onDestroy(() => this.realtime.stop());
  }
}
