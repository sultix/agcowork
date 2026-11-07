import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { SessionMenuComponent } from '../../shared/ui/session-menu.component';

interface WorkspaceEntry {
  slug: string;
  name: string;
}

/** Workspace switcher. Static list until memberships come from the backend (AP-014/016). */
@Component({
  selector: 'ag-workspace-switcher',
  imports: [RouterLink, SessionMenuComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="flex items-center justify-end border-b border-border px-6 py-3">
      <ag-session-menu />
    </header>
    <main class="mx-auto max-w-xl p-6">
      <h1 class="text-xl font-semibold">Workspaces</h1>
      <ul class="mt-4 divide-y divide-border rounded-lg border border-border bg-surface">
        @for (workspace of workspaces; track workspace.slug) {
          <li>
            <a
              [routerLink]="['/w', workspace.slug]"
              class="flex items-center justify-between px-4 py-3 hover:bg-surface-hover"
            >
              <span>{{ workspace.name }}</span>
              <span class="text-sm text-muted">{{ workspace.slug }}</span>
            </a>
          </li>
        }
      </ul>
    </main>
  `,
})
export class WorkspaceSwitcherComponent {
  protected readonly workspaces: readonly WorkspaceEntry[] = [
    { slug: 'demo', name: 'Demo' },
    { slug: 'agcowork', name: 'agcowork' },
  ];
}
