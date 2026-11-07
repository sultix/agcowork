import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideFolder } from '@ng-icons/lucide';
import { HlmSidebarImports } from '@spartan-ng/helm/sidebar';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { WorkspaceTreeStore } from './workspace-tree.store';

/**
 * Sidebar tree: every space with its collections (Helm sidebar menu with sub-menu); the page being
 * viewed is marked active (`data-active`, `aria-current="page"`).
 */
@Component({
  selector: 'ag-workspace-tree',
  imports: [RouterLink, RouterLinkActive, NgIcon, HlmSidebarImports],
  providers: [provideIcons({ lucideFolder })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav aria-label="Spaces und Collections" data-testid="workspace-tree">
      <ul hlmSidebarMenu>
        @for (node of tree.tree(); track node.space.id) {
          <li hlmSidebarMenuItem>
            <a
              hlmSidebarMenuButton
              [routerLink]="['/w', context.slug(), 'spaces', node.space.id]"
              routerLinkActive
              #spaceLink="routerLinkActive"
              ariaCurrentWhenActive="page"
              [isActive]="spaceLink.isActive"
              [tooltip]="node.space.name"
              data-testid="tree-space"
            >
              @if (node.space.icon) {
                <span class="w-4 shrink-0 text-center leading-none" aria-hidden="true">{{
                  node.space.icon
                }}</span>
              } @else {
                <ng-icon name="lucideFolder" />
              }
              <span>{{ node.space.name }}</span>
            </a>
            @if (node.collections.length > 0) {
              <ul hlmSidebarMenuSub>
                @for (collection of node.collections; track collection.id) {
                  <li hlmSidebarMenuSubItem>
                    <a
                      hlmSidebarMenuSubButton
                      [routerLink]="['/w', context.slug(), 'collections', collection.id]"
                      routerLinkActive
                      #collectionLink="routerLinkActive"
                      ariaCurrentWhenActive="page"
                      [isActive]="collectionLink.isActive"
                      data-testid="tree-collection"
                      ><span>{{ collection.name }}</span></a
                    >
                  </li>
                }
              </ul>
            }
          </li>
        } @empty {
          @if (tree.error()) {
            <li class="px-2 text-sm text-destructive" role="alert">
              Spaces konnten nicht geladen werden.
            </li>
          } @else if (tree.isLoading()) {
            @for (row of skeletonRows; track row) {
              <li hlmSidebarMenuItem><hlm-sidebar-menu-skeleton /></li>
            }
          } @else {
            <li class="px-2 text-sm text-muted-foreground group-data-[collapsible=icon]:hidden">
              Keine Spaces.
            </li>
          }
        }
      </ul>
    </nav>
  `,
})
export class WorkspaceTreeComponent {
  protected readonly tree = inject(WorkspaceTreeStore);
  protected readonly context = inject(WorkspaceContext);

  protected readonly skeletonRows = [1, 2, 3];
}
