import { ChangeDetectionStrategy, Component, effect, inject, untracked } from '@angular/core';
import { Router } from '@angular/router';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { CollectionStore } from './collection.store';

/** `/w/:slug/collections/:collectionId`: forwards to the collection's default view. */
@Component({
  selector: 'ag-default-view-redirect',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (store.viewsError(); as message) {
      <p class="mt-4 text-sm text-danger" role="alert" data-testid="views-error">{{ message }}</p>
    } @else if (store.viewsLoaded() && !store.defaultView()) {
      <p class="mt-4 text-sm text-muted" data-testid="views-none">
        Diese Collection hat keine Views.
      </p>
    } @else {
      <p class="mt-4 text-sm text-muted">Wird geladen …</p>
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
