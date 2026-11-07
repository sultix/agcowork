import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideChevronsUpDown, lucideLayoutGrid } from '@ng-icons/lucide';
import { HlmDropdownMenuImports } from '@spartan-ng/helm/dropdown-menu';
import { HlmSidebarImports } from '@spartan-ng/helm/sidebar';
import { WorkspacesStore } from './workspaces.store';

/** Sidebar workspace switcher: current workspace, dropdown menu with the others and the full list. */
@Component({
  selector: 'ag-workspace-switcher',
  imports: [RouterLink, NgIcon, HlmDropdownMenuImports, HlmSidebarImports],
  providers: [provideIcons({ lucideChevronsUpDown, lucideLayoutGrid })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ul hlmSidebarMenu>
      <li hlmSidebarMenuItem>
        <button
          hlmSidebarMenuButton
          size="lg"
          class="aria-expanded:bg-sidebar-accent aria-expanded:text-sidebar-accent-foreground"
          data-testid="switcher-toggle"
          [hlmDropdownMenuTrigger]="menu"
          align="start"
        >
          <span
            class="flex aspect-square size-8 shrink-0 items-center justify-center rounded-lg bg-sidebar-primary text-sm font-semibold text-sidebar-primary-foreground"
            aria-hidden="true"
            >{{ initial() }}</span
          >
          <span class="grid min-w-0 flex-1 text-start leading-tight">
            <span class="truncate font-medium" data-testid="workspace-slug">{{ label() }}</span>
            <span class="truncate text-xs text-muted-foreground">/w/{{ slug() }}</span>
          </span>
          <ng-icon name="lucideChevronsUpDown" class="ms-auto" />
        </button>
      </li>
    </ul>

    <ng-template #menu>
      <hlm-dropdown-menu class="w-60" data-testid="switcher-menu">
        <div hlmDropdownMenuLabel>Workspaces</div>
        @for (workspace of others(); track workspace.id) {
          <a hlmDropdownMenuItem [routerLink]="['/w', workspace.slug]" data-testid="switcher-item">
            <span
              class="flex size-6 shrink-0 items-center justify-center rounded-md border text-xs font-medium"
              aria-hidden="true"
              >{{ workspace.name.charAt(0).toUpperCase() }}</span
            >
            <span class="truncate">{{ workspace.name }}</span>
          </a>
        }
        <hlm-dropdown-menu-separator />
        <a hlmDropdownMenuItem routerLink="/workspaces" data-testid="switcher-all">
          <ng-icon name="lucideLayoutGrid" />
          Alle Workspaces
        </a>
      </hlm-dropdown-menu>
    </ng-template>
  `,
})
export class WorkspaceSwitcherComponent {
  private readonly store = inject(WorkspacesStore);

  /** Slug of the active workspace. */
  readonly slug = input.required<string | null>();

  protected readonly label = computed(() => this.store.find(this.slug())?.name ?? this.slug());
  protected readonly initial = computed(() => (this.label() ?? '?').charAt(0).toUpperCase());
  protected readonly others = computed(() =>
    this.store.workspaces().filter((w) => w.slug !== this.slug()),
  );
}
