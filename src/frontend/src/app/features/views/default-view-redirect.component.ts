import { ChangeDetectionStrategy, Component, effect, inject, untracked } from '@angular/core';
import { Router } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCircleAlert } from '@ng-icons/lucide';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { CollectionStore } from './collection.store';

/** `/w/:slug/collections/:collectionId`: forwards to the collection's default view. */
@Component({
  selector: 'ag-default-view-redirect',
  imports: [HlmAlertImports, HlmSkeleton, NgIcon],
  providers: [provideIcons({ lucideCircleAlert })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (store.viewsError(); as message) {
      <div hlmAlert variant="destructive" class="mt-4" data-testid="views-error">
        <ng-icon name="lucideCircleAlert" />
        <p hlmAlertDescription>{{ message }}</p>
      </div>
    } @else if (store.viewsLoaded() && !store.defaultView()) {
      <p class="mt-4 text-sm text-muted-foreground" data-testid="views-none">
        Diese Collection hat keine Views.
      </p>
    } @else {
      <hlm-skeleton class="mt-4 h-8 w-64" />
    }
  `,
})
export class DefaultViewRedirectComponent {
  protected readonly store = inject(CollectionStore);
  private readonly context = inject(WorkspaceContext);
  private readonly router = inject(Router);

  constructor() {
    effect(() => {
      const view = this.store.viewsLoaded() ? this.store.defaultView() : null;
      const slug = this.context.slug();
      if (view && slug && view.collectionId === this.store.source()?.collectionId) {
        untracked(
          () =>
            void this.router.navigate(
              ['/w', slug, 'collections', view.collectionId, 'views', view.id],
              { replaceUrl: true },
            ),
        );
      }
    });
  }
}
