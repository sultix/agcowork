import { Injectable, Signal, computed, inject, resource } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError } from '../../core/api/api-error';
import type { components } from '../../core/api/generated/schema';
import { SessionStore } from '../../core/auth/session.store';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';

export type Space = components['schemas']['SpaceDto'];
export type Collection = components['schemas']['CollectionDto'];

export interface SpaceNode {
  readonly space: Space;
  readonly collections: readonly Collection[];
}

/**
 * Spaces and their collections of the active workspace (`GET /spaces`, `GET /collections`):
 * the data behind the sidebar tree and the space and collection pages. Reloads when the
 * workspace slug changes and stays idle while nobody is signed in.
 */
@Injectable({ providedIn: 'root' })
export class WorkspaceTreeStore {
  private readonly api = inject(ApiClient);
  private readonly context = inject(WorkspaceContext);
  private readonly session = inject(SessionStore);

  private readonly slug = computed(
    () => (this.session.status() === 'authenticated' ? this.context.slug() : null) ?? undefined,
  );

  private readonly spacesResource = resource<Space[], string | undefined>({
    params: () => this.slug(),
    loader: ({ params: slug }) => firstValueFrom(this.api.workspace(slug).get('/spaces')),
  });
  private readonly collectionsResource = resource<Collection[], string | undefined>({
    params: () => this.slug(),
    loader: ({ params: slug }) => firstValueFrom(this.api.workspace(slug).get('/collections')),
  });

  readonly spaces: Signal<readonly Space[]> = computed(() =>
    this.spacesResource.hasValue() ? this.spacesResource.value() : [],
  );
  readonly collections: Signal<readonly Collection[]> = computed(() =>
    this.collectionsResource.hasValue() ? this.collectionsResource.value() : [],
  );
  readonly tree: Signal<readonly SpaceNode[]> = computed(() =>
    this.spaces().map((space) => ({
      space,
      collections: this.collections().filter((c) => c.spaceId === space.id),
    })),
  );
  /** True until both lists have answered for the current workspace. */
  readonly isLoading: Signal<boolean> = computed(
    () => this.spacesResource.isLoading() || this.collectionsResource.isLoading(),
  );
  readonly error: Signal<ApiError | null> = computed(() => {
    const error = this.spacesResource.error() ?? this.collectionsResource.error();
    return error ? ApiError.from(error) : null;
  });

  findSpace(id: string | null | undefined): Space | undefined {
    return id ? this.spaces().find((s) => s.id === id) : undefined;
  }

  findCollection(id: string | null | undefined): Collection | undefined {
    return id ? this.collections().find((c) => c.id === id) : undefined;
  }

  reload(): void {
    this.spacesResource.reload();
    this.collectionsResource.reload();
  }
}
