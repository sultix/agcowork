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
import { liveReload } from '../../core/realtime/live-reload';
import type { ActivityEvent } from '../../core/realtime/realtime.service';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { describeError } from '../../shared/ui/api-error-text';
import { WorkspacesStore } from '../workspaces/workspaces.store';

export type ViewDto = components['schemas']['ViewDto'];
export type CreateViewBody = components['schemas']['CreateViewBody'];
export type UpdateViewBody = components['schemas']['UpdateViewBody'];
type Description = components['schemas']['CollectionDescriptionDto'];
type Collection = components['schemas']['CollectionDto'];

/** Record events that change the record count of a collection. */
const COUNTED_RECORD_EVENTS = ['record.created', 'record.archived', 'record.restored'];

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

function sameSource(a: Source | undefined, b: Source | undefined): boolean {
  return a?.slug === b?.slug && a?.collectionId === b?.collectionId;
}

function sameJson(a: unknown, b: unknown): boolean {
  return a === b || JSON.stringify(a) === JSON.stringify(b);
}

/**
 * One collection as the collection page and its views see it: description (schema, record
 * count) and the views (`GET …/views`) with the actions on them. Provided by the collection page;
 * the view pages below it share the instance.
 *
 * Live: the record count follows `record.created|archived|restored` of the collection, schema and
 * metadata follow `collection.*` of it, the views follow `view.*` of it; all also reload after a
 * resync (bundled, see {@link liveReload}). A failed live reload keeps what is shown. While a view
 * below edits ({@link holdWhile}: open cell editor, drag, running save), a new schema is held back
 * and shown once the edit is over, so no editor gets a changed schema under it.
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

  /** Edit locks of the views below (see {@link holdWhile}). */
  private readonly locks = signal<readonly (() => boolean)[]>([]);

  /** True while a view below edits; schema and metadata changes wait until it is over. */
  readonly editing = computed(() => this.locks().some((blocked) => blocked()));

  /** Ids of views deleted from this page (not elsewhere). */
  private readonly removedHere = new Set<string>();

  /**
   * Grow with every live fetch that starts and every local change of the views: a live answer
   * is only applied while its generation is current, so an older answer never replaces a newer
   * one, nor undoes a view created, changed or deleted here meanwhile.
   */
  private viewsGeneration = 0;
  private descriptionGeneration = 0;

  /**
   * Schema and metadata as shown: the loaded ones, except that while {@link editing} the shown
   * ones stay (for the same collection) and the newer ones follow once the edit is over.
   */
  private readonly shownCollection = linkedSignal<
    { readonly loaded: Collection | undefined; readonly editing: boolean },
    Collection | undefined
  >({
    source: () => ({
      loaded: this.description.hasValue() ? this.description.value().collection : undefined,
      editing: this.editing(),
    }),
    computation: ({ loaded, editing }, previous) =>
      editing && loaded && previous?.value?.id === loaded.id ? previous.value : loaded,
    equal: sameJson,
  });

  readonly collection: Signal<Collection | undefined> = this.shownCollection.asReadonly();
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

  constructor() {
    liveReload({
      slug: () => this.source()?.slug,
      matches: (event) => this.describes(event),
      reload: () => void this.refetchDescription(),
    });
    liveReload({
      slug: () => this.source()?.slug,
      matches: (event) =>
        event.targetType === 'view' && event.collectionId === this.source()?.collectionId,
      reload: () => void this.refetchViews(),
    });
  }

  connect(collectionId: string | null): void {
    this.collectionIdSignal.set(collectionId);
  }

  /**
   * Holds schema and metadata changes back while `blocked` is true (a view's edit lock, see
   * `liveRefresh`). Returns the release; call it when the view goes away.
   */
  holdWhile(blocked: () => boolean): () => void {
    this.locks.update((locks) => [...locks, blocked]);
    return () => this.locks.update((locks) => locks.filter((lock) => lock !== blocked));
  }

  /** Whether the view was deleted on this page (so its disappearance needs no notice). */
  wasRemovedHere(viewId: string): boolean {
    return this.removedHere.has(viewId);
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
    this.changeViews((list) => [
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
    this.changeViews((list) =>
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
    this.removedHere.add(viewId);
    this.changeViews((list) => list.filter((v) => v.id !== viewId));
    this.viewsResource.reload();
  }

  /** Record count (`record.*`), schema or metadata (`collection.*`) of the shown collection. */
  private describes(event: ActivityEvent): boolean {
    const collectionId = this.source()?.collectionId;
    if (!collectionId) {
      return false;
    }
    if (event.targetType === 'collection') {
      return event.targetId === collectionId;
    }
    return (
      event.targetType === 'record' &&
      event.collectionId === collectionId &&
      COUNTED_RECORD_EVENTS.includes(event.type)
    );
  }

  /** Fetches the description again; a failure keeps the shown one (no error page). */
  private async refetchDescription(): Promise<void> {
    const source = this.source();
    if (!source) {
      return;
    }
    const generation = ++this.descriptionGeneration;
    try {
      const description = await firstValueFrom(
        this.api.workspace(source.slug).get('/collections/{collectionId}/describe', {
          path: { collectionId: source.collectionId },
        }),
      );
      if (sameSource(this.source(), source) && generation === this.descriptionGeneration) {
        this.description.set(description);
      }
    } catch {
      // keep what is shown; the next event or resync tries again
    }
  }

  /** Fetches the views again; a failure keeps the shown ones. */
  private async refetchViews(): Promise<void> {
    const source = this.source();
    if (!source) {
      return;
    }
    const generation = ++this.viewsGeneration;
    try {
      const views = await firstValueFrom(
        this.api.workspace(source.slug).get('/collections/{collectionId}/views', {
          path: { collectionId: source.collectionId },
        }),
      );
      if (sameSource(this.source(), source) && generation === this.viewsGeneration) {
        this.viewsResource.set(views);
      }
    } catch {
      // keep what is shown; the next event or resync tries again
    }
  }

  private replace(view: ViewDto): void {
    this.changeViews((list) => list.map((v) => (v.id === view.id ? view : v)));
  }

  /** A local change of the views; live answers fetched before it are outdated. */
  private changeViews(change: (list: ViewDto[]) => ViewDto[]): void {
    this.viewsGeneration++;
    this.viewList.update(change);
  }

  private require(): Source {
    const source = this.source();
    if (!source) {
      throw new Error('CollectionStore is not connected.');
    }
    return source;
  }
}
