import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { WorkspaceTreeStore } from './workspace-tree.store';

/** Sidebar tree: every space with its collections; the page being viewed is highlighted. */
@Component({
  selector: 'ag-workspace-tree',
  imports: [RouterLink, RouterLinkActive],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav
      aria-label="Spaces und Collections"
      class="flex flex-col gap-1 text-sm"
      data-testid="workspace-tree"
    >
      @for (node of tree.tree(); track node.space.id) {
        <a
          [routerLink]="['/w', context.slug(), 'spaces', node.space.id]"
          routerLinkActive="bg-surface-hover text-fg"
          class="truncate rounded-md px-2 py-1 font-medium hover:bg-surface-hover"
          data-testid="tree-space"
        >
          @if (node.space.icon) {
            <span aria-hidden="true">{{ node.space.icon }} </span>
          }
          {{ node.space.name }}
        </a>
        @for (collection of node.collections; track collection.id) {
          <a
            [routerLink]="['/w', context.slug(), 'collections', collection.id]"
            routerLinkActive="bg-surface-hover text-fg"
            class="truncate rounded-md py-1 pr-2 pl-6 text-muted hover:bg-surface-hover"
            data-testid="tree-collection"
            >{{ collection.name }}</a
          >
        }
      } @empty {
        @if (tree.error()) {
          <p class="px-2 text-danger" role="alert">Spaces konnten nicht geladen werden.</p>
        } @else if (!tree.isLoading()) {
          <p class="px-2 text-muted">Keine Spaces.</p>
        }
      }
    </nav>
  `,
})
export class WorkspaceTreeComponent {
  protected readonly tree = inject(WorkspaceTreeStore);
  protected readonly context = inject(WorkspaceContext);
}
