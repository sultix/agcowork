import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { BreadcrumbsComponent, Crumb } from '../../shared/ui/breadcrumbs.component';
import { WorkspaceTreeStore } from '../spaces/workspace-tree.store';
import { RecordTabsComponent } from './record-tabs.component';

/** Breadcrumb (Space › Collection › Record), title and tab bar above the raw and history views. */
@Component({
  selector: 'ag-record-header',
  imports: [BreadcrumbsComponent, RecordTabsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ag-breadcrumbs [crumbs]="crumbs()" />
    <h1 class="mt-2 text-2xl font-semibold" data-testid="record-heading">
      {{ title() || 'Ohne Titel' }}
    </h1>
    <ag-record-tabs [recordId]="recordId()" />
  `,
})
export class RecordHeaderComponent {
  private readonly context = inject(WorkspaceContext);
  private readonly tree = inject(WorkspaceTreeStore);

  readonly recordId = input.required<string>();
  readonly title = input('');
  readonly spaceId = input<string | null>(null);
  readonly collectionId = input<string | null>(null);

  protected readonly crumbs = computed<Crumb[]>(() => {
    const slug = this.context.slug();
    const spaceId = this.spaceId();
    if (!slug || !spaceId) {
      return [];
    }
    const crumbs: Crumb[] = [
      {
        label: this.tree.findSpace(spaceId)?.name ?? 'Space',
        link: ['/w', slug, 'spaces', spaceId],
      },
    ];
    const collectionId = this.collectionId();
    if (collectionId) {
      crumbs.push({
        label: this.tree.findCollection(collectionId)?.name ?? 'Collection',
        link: ['/w', slug, 'collections', collectionId],
      });
    }
    crumbs.push({ label: this.title() || 'Ohne Titel' });
    return crumbs;
  });
}
