import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { WorkspacesStore } from './workspaces.store';

/** Sidebar workspace switcher: current workspace, dropdown with the others and the full list. */
@Component({
  selector: 'ag-workspace-switcher',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="relative">
      <button
        type="button"
        class="flex w-full items-center justify-between gap-2 rounded-md border border-border px-2 py-1 text-left hover:bg-surface-hover"
        aria-haspopup="true"
        [attr.aria-expanded]="open()"
        data-testid="switcher-toggle"
        (click)="open.set(!open())"
      >
        <span class="truncate font-semibold" data-testid="workspace-slug">{{ label() }}</span>
        <span class="text-muted" aria-hidden="true">▾</span>
      </button>
      @if (open()) {
        <ul
          class="absolute left-0 right-0 z-10 mt-1 rounded-md border border-border bg-surface py-1 text-sm shadow"
          data-testid="switcher-menu"
        >
          @for (workspace of others(); track workspace.id) {
            <li>
              <a
                [routerLink]="['/w', workspace.slug]"
                class="block truncate px-3 py-1 hover:bg-surface-hover"
                data-testid="switcher-item"
                (click)="open.set(false)"
                >{{ workspace.name }}</a
              >
            </li>
          }
          <li>
            <a
              routerLink="/workspaces"
              class="block border-t border-border px-3 py-1 text-muted hover:bg-surface-hover"
              data-testid="switcher-all"
              (click)="open.set(false)"
              >Alle Workspaces</a
            >
          </li>
        </ul>
      }
    </div>
  `,
})
export class WorkspaceSwitcherComponent {
  private readonly store = inject(WorkspacesStore);

  /** Slug of the active workspace. */
  readonly slug = input.required<string | null>();

  protected readonly open = signal(false);
  protected readonly label = computed(() => this.store.find(this.slug())?.name ?? this.slug());
  protected readonly others = computed(() =>
    this.store.workspaces().filter((w) => w.slug !== this.slug()),
  );
}
