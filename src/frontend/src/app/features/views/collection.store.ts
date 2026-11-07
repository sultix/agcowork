import {
  Injectable,
  Signal,
  computed,
  inject,
  linkedSignal,
  resource,
  signal,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import type { components } from '../../core/api/generated/schema';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { describeError } from '../../shared/ui/api-error-text';
import { WorkspacesStore } from '../workspaces/workspaces.store';

export type ViewDto = components['schemas']['ViewDto'];
export type CreateViewBody = components['schemas']['CreateViewBody'];
export type UpdateViewBody = components['schemas']['UpdateViewBody'];
type Description = components['schemas']['CollectionDescriptionDto'];

/** View types a user can create; gallery and list render as a table for now. */
export const CREATABLE_VIEW_TYPES = ['table', 'board', 'list', 'gallery'] as const;

export const VIEW_TYPE_LABELS: Readonly<Record<string, string>> = {
  table: 'Tabelle',
  board: 'Board',
  list: 'Liste',
  gallery: 'Galerie',
  raw: 'Rohdaten',
  document: 'Dokument',
};

/** Lucide icon name per view type (register the icons where they are rendered). */
export const VIEW_TYPE_ICONS: Readonly<Record<string, string>> = {
  table: 'lucideTable',
  board: 'lucideKanban',
  list: 'lucideList',
  gallery: 'lucideLayoutGrid',
  raw: 'lucideBraces',
  document: 'lucideFileText',
};

interface Source {
  readonly slug: string;
  readonly collectionId: string;
}

function byOrder(a: ViewDto, b: ViewDto): number {
  return Number(a.sortOrder) - Number(b.sortOrder) || a.name.localeCompare(b.name);
}

/**
 * One collection as the collection page and its views see it: description (schema, record
 * count) and the views (`GET …/views`) with the actions on them. Provided by the collection page;
 * the view pages below it share the instance.
 */
@Injectable()
export class CollectionStore {
  private readonly api = inject(ApiClient);
  private readonly context = inject(WorkspaceContext);
  private readonly workspaces = inject(WorkspacesStore);

  private readonly collectionIdSignal = signal<string | null>(null);

  readonly source: Signal<Source | undefined> = computed(() => {
    const slug = this.context.slug();
    const collectionId = this.collectionIdSignal();
    return slug && collectionId ? { slug, collectionId } : undefined;
  });

  readonly description = resource<Description, Source | undefined>({
    params: () => this.source(),
    loader: ({ params }) =>
      firstValueFrom(
        this.api.workspace(params.slug).get('/collections/{collectionId}/describe', {
          path: { collectionId: params.collectionId },
        }),
      ),
  });

  private readonly viewsResource = resource<ViewDto[], Source | undefined>({
    params: () => this.source(),
    loader: ({ params }) =>
      firstValueFrom(
        this.api.workspace(params.slug).get('/collections/{collectionId}/views', {
          path: { collectionId: params.collectionId },
        }),
      ),
  });

  /** The views; replaced locally after each action, reset whenever the list loads. */
  private readonly viewList = linkedSignal<ViewDto[] | undefined, ViewDto[]>({
    source: () => (this.viewsResource.hasValue() ? this.viewsResource.value() : undefined),
    computation: (list) => list ?? [],
  });

  readonly collection = computed(() =>
    this.description.hasValue() ? this.description.value().collection : undefined,
  );
  readonly schema = computed(() => this.collection()?.schema ?? []);
  readonly recordCount = computed(() =>
    this.description.hasValue() ? Number(this.description.value().statistics.recordCount) : null,
  );
  readonly loadError = computed(() =>
    this.description.error() ? describeError(this.description.error()) : null,
  );

  readonly views: Signal<readonly ViewDto[]> = computed(() => [...this.viewList()].sort(byOrder));
  readonly viewsLoaded = computed(() => this.viewsResource.hasValue());
  readonly viewsError = computed(() =>
    this.viewsResource.error() ? describeError(this.viewsResource.error()) : null,
  );
  readonly defaultView = computed(
    () => this.views().find((v) => v.isDefault) ?? this.views()[0] ?? null,
  );

  /** `write` on an active workspace and collection: edit records and views. */
  readonly canWrite = computed(() => {
    const ws = this.workspaces.find(this.context.slug() ?? '');
    return !!ws && !ws.archivedAt && ws.scopes.includes('write') && !this.collection()?.archivedAt;
  });

  /** The view being shown below the collection page (set by the view page). */
  readonly activeViewId = signal<string | null>(null);

  connect(collectionId: string | null): void {
    this.collectionIdSignal.set(collectionId);
  }

  find(viewId: string | null | undefined): ViewDto | undefined {
    return viewId ? this.views().find((v) => v.id === viewId) : undefined;
  }

  async create(body: CreateViewBody): Promise<ViewDto> {
    const source = this.require();
    const view = await firstValueFrom(
      this.api.workspace(source.slug).post('/collections/{collectionId}/views', {
        path: { collectionId: source.collectionId },
        body,
      }),
    );
    this.viewList.update((list) => [
      ...list.map((v) => (view.isDefault ? { ...v, isDefault: false } : v)),
      view,
    ]);
    return view;
  }

  async update(viewId: string, body: UpdateViewBody): Promise<ViewDto> {
    const source = this.require();
    const view = await firstValueFrom(
      this.api.workspace(source.slug).patch('/views/{viewId}', { path: { viewId }, body }),
    );
    this.replace(view);
    return view;
  }

  async setDefault(viewId: string): Promise<ViewDto> {
    const source = this.require();
    const view = await firstValueFrom(
      this.api.workspace(source.slug).post('/views/{viewId}/default', { path: { viewId } }),
    );
    this.viewList.update((list) =>
      list.map((v) => (v.id === view.id ? view : { ...v, isDefault: false })),
    );
    return view;
  }

  /** Deletes a view; the list is reloaded because the default may have moved. */
  async remove(viewId: string): Promise<void> {
    const source = this.require();
    await firstValueFrom(
      this.api.workspace(source.slug).delete('/views/{viewId}', { path: { viewId } }),
    );
    this.viewList.update((list) => list.filter((v) => v.id !== viewId));
    this.viewsResource.reload();
  }

  private replace(view: ViewDto): void {
    this.viewList.update((list) => list.map((v) => (v.id === view.id ? view : v)));
  }

  private require(): Source {
    const source = this.source();
    if (!source) {
      throw new Error('CollectionStore is not connected.');
    }
    return source;
  }
}
