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
import { ApiClient } from '../../../core/api/api-client';
import type { components } from '../../../core/api/generated/schema';
import { liveReload } from '../../../core/realtime/live-reload';
import { WorkspaceContext } from '../../../core/workspace/workspace-context.service';
import type { PropertyDefinition } from '../../../shared/properties/property-types';
import { describeError } from '../../../shared/ui/api-error-text';
import { WorkspacesStore } from '../../workspaces/workspaces.store';
import { Loss, lossFilter } from './schema-draft';

type Collection = components['schemas']['CollectionDto'];
export type UpdateSchemaBody = components['schemas']['UpdateSchemaBody'];
export type CollectionSchemaDto = components['schemas']['CollectionSchemaDto'];

interface Source {
  readonly slug: string;
  readonly collectionId: string;
}

function sameSource(a: Source | undefined, b: Source | undefined): boolean {
  return a?.slug === b?.slug && a?.collectionId === b?.collectionId;
}

/**
 * The collection behind the schema editor (`GET /collections/{id}`) and the schema changes on it
 * (`PATCH …/schema`). Provided by the schema editor page; its dialogs share the instance.
 *
 * Live: `collection.*` events of the collection (e.g. `collection.schema_changed` by someone else)
 * and resyncs fetch it again (bundled, see {@link liveReload}). The list follows at once; open
 * dialogs keep their own draft and the schema version they were opened with, so a change by
 * someone else surfaces as 409 on save instead of being overwritten.
 */
@Injectable()
export class SchemaEditorStore {
  private readonly api = inject(ApiClient);
  private readonly context = inject(WorkspaceContext);
  private readonly workspaces = inject(WorkspacesStore);

  private readonly collectionIdSignal = signal<string | null>(null);

  readonly source: Signal<Source | undefined> = computed(() => {
    const slug = this.context.slug();
    const collectionId = this.collectionIdSignal();
    return slug && collectionId ? { slug, collectionId } : undefined;
  });

  private readonly collectionResource = resource<Collection, Source | undefined>({
    params: () => this.source(),
    loader: ({ params }) =>
      firstValueFrom(
        this.api.workspace(params.slug).get('/collections/{collectionId}', {
          path: { collectionId: params.collectionId },
        }),
      ),
  });

  /** The loaded collection, replaced locally by the answer of each schema change. */
  private readonly shown = linkedSignal<Collection | undefined>(() =>
    this.collectionResource.hasValue() ? this.collectionResource.value() : undefined,
  );

  readonly collection: Signal<Collection | undefined> = this.shown.asReadonly();
  readonly schema: Signal<readonly PropertyDefinition[]> = computed(
    () => this.collection()?.schema ?? [],
  );
  readonly schemaVersion = computed(() => Number(this.collection()?.schemaVersion ?? 0));
  readonly loadError = computed(() =>
    this.collectionResource.error() ? describeError(this.collectionResource.error()) : null,
  );

  private readonly workspace = computed(() => this.workspaces.find(this.context.slug() ?? ''));

  /** Why the schema can only be viewed; `null` when it can be edited (or is not loaded yet). */
  readonly readOnlyReason = computed<string | null>(() => {
    const ws = this.workspace();
    const collection = this.collection();
    if (!ws || !collection) {
      return null;
    }
    if (ws.archivedAt) {
      return 'Der Workspace ist archiviert, das Schema lässt sich nur ansehen.';
    }
    if (collection.archivedAt) {
      return 'Die Collection ist archiviert, ihr Schema lässt sich nur ansehen.';
    }
    if (!ws.scopes.includes('schema')) {
      return 'Du kannst das Schema ansehen, aber nicht ändern: dafür fehlt dir das Recht „schema“ (Admins und Owner haben es).';
    }
    return null;
  });

  /** `schema` scope on an active workspace and collection. */
  readonly canEdit = computed(
    () => !!this.workspace() && !!this.collection() && this.readOnlyReason() === null,
  );

  /**
   * Grows with every fetch that starts and every local change: a fetched answer is applied only
   * while its generation is current, so it never undoes a newer change made here.
   */
  private generation = 0;

  constructor() {
    liveReload({
      slug: () => this.source()?.slug,
      matches: (event) =>
        event.targetType === 'collection' && event.targetId === this.source()?.collectionId,
      reload: () => void this.refresh(),
    });
  }

  connect(collectionId: string | null): void {
    this.collectionIdSignal.set(collectionId);
  }

  find(key: string): PropertyDefinition | undefined {
    return this.schema().find((p) => p.key === key);
  }

  /** Sends a schema change and shows the resulting schema; errors are thrown as `ApiError`. */
  async update(body: UpdateSchemaBody): Promise<CollectionSchemaDto> {
    const source = this.require();
    const result = await firstValueFrom(
      this.api.workspace(source.slug).patch('/collections/{collectionId}/schema', {
        path: { collectionId: source.collectionId },
        body,
      }),
    );
    if (sameSource(this.source(), source)) {
      this.generation++;
      this.shown.update((collection) =>
        collection && collection.id === result.collectionId
          ? { ...collection, schema: result.properties, schemaVersion: result.schemaVersion }
          : collection,
      );
    }
    return result;
  }

  /** The latest fetch started by {@link refresh}, so superseded callers can wait for it. */
  private newestRefresh: { readonly generation: number; readonly done: Promise<boolean> } | null =
    null;

  /**
   * Fetches the collection again; a failure keeps what is shown. Resolves whether the newest
   * state is shown afterwards. When a later fetch (e.g. a live reload) overtakes this one, it
   * resolves with the outcome of that later fetch; when a local change overtakes it, with true.
   */
  refresh(): Promise<boolean> {
    const source = this.source();
    if (!source) {
      return Promise.resolve(false);
    }
    const generation = ++this.generation;
    const done = this.fetchInto(source, generation);
    this.newestRefresh = { generation, done };
    return done;
  }

  private async fetchInto(source: Source, generation: number): Promise<boolean> {
    let collection: Collection | null = null;
    try {
      collection = await firstValueFrom(
        this.api.workspace(source.slug).get('/collections/{collectionId}', {
          path: { collectionId: source.collectionId },
        }),
      );
    } catch {
      // keep what is shown; the next event or resync tries again
    }
    if (!sameSource(this.source(), source)) {
      return false;
    }
    if (generation === this.generation) {
      if (!collection) {
        return false;
      }
      this.collectionResource.set(collection);
      return true;
    }
    const newer = this.newestRefresh;
    return newer && newer.generation > generation ? newer.done : true;
  }

  /**
   * How many records (archived ones included) hold values a loss affects; `null` when that
   * cannot be counted cheaply. One query with `includeCount`, capped by the API at 10 000.
   */
  async countAffected(loss: Loss): Promise<number | null> {
    const source = this.source();
    const filter = lossFilter(loss);
    if (!source || !filter) {
      return null;
    }
    try {
      const page = await firstValueFrom(
        this.api.workspace(source.slug).post('/collections/{collectionId}/query', {
          path: { collectionId: source.collectionId },
          body: { filter, limit: 1, includeArchived: true, includeCount: true },
        }),
      );
      return page.count == null ? null : Number(page.count);
    } catch {
      return null;
    }
  }

  private require(): Source {
    const source = this.source();
    if (!source) {
      throw new Error('SchemaEditorStore is not connected.');
    }
    return source;
  }
}
