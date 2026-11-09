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
import { ApiError } from '../../../core/api/api-error';
import { ApiClient } from '../../../core/api/api-client';
import type { components } from '../../../core/api/generated/schema';
import { liveReload } from '../../../core/realtime/live-reload';
import { WorkspaceContext } from '../../../core/workspace/workspace-context.service';
import type { PropertyDefinition } from '../../../shared/properties/property-types';
import { describeError } from '../../../shared/ui/api-error-text';
import { WorkspacesStore } from '../../workspaces/workspaces.store';
import type { CreateTemplateBody, TemplateDto, UpdateTemplateBody } from './template-draft';

type Collection = components['schemas']['CollectionDto'];

interface Source {
  readonly slug: string;
  readonly collectionId: string;
}

function sameSource(a: Source | undefined, b: Source | undefined): boolean {
  return a?.slug === b?.slug && a?.collectionId === b?.collectionId;
}

/**
 * The collection (for its schema) and the record templates of the templates page
 * (`GET /collections/{id}`, `…/templates`). Provided by the page; its dialog shares the instance.
 *
 * There is no live channel for templates. The list reloads after every own change and, together
 * with the collection, on `collection.*` events and resyncs (a schema change can clean up
 * presets on the server). Open dialogs keep their own draft.
 */
@Injectable()
export class TemplatesStore {
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
    loader: ({ params }) => this.fetchCollection(params),
  });

  private readonly templatesResource = resource<TemplateDto[], Source | undefined>({
    params: () => this.source(),
    loader: ({ params }) => this.fetchTemplates(params),
  });

  private readonly shownCollection = linkedSignal<Collection | undefined>(() =>
    this.collectionResource.hasValue() ? this.collectionResource.value() : undefined,
  );
  private readonly shownTemplates = linkedSignal<TemplateDto[] | undefined>(() =>
    this.templatesResource.hasValue() ? this.templatesResource.value() : undefined,
  );

  readonly collection: Signal<Collection | undefined> = this.shownCollection.asReadonly();
  readonly schema: Signal<readonly PropertyDefinition[]> = computed(
    () => this.collection()?.schema ?? [],
  );
  /** `undefined` until the first answer. */
  readonly templates: Signal<TemplateDto[] | undefined> = this.shownTemplates.asReadonly();

  readonly loadError = computed(() => {
    const error = this.collectionResource.error() ?? this.templatesResource.error();
    return error ? describeError(error) : null;
  });

  private readonly workspace = computed(() => this.workspaces.find(this.context.slug() ?? ''));

  /** Why the templates can only be viewed; `null` when they can be edited (or are not loaded). */
  readonly readOnlyReason = computed<string | null>(() => {
    const ws = this.workspace();
    const collection = this.collection();
    if (!ws || !collection) {
      return null;
    }
    if (ws.archivedAt) {
      return 'Der Workspace ist archiviert, die Vorlagen lassen sich nur ansehen.';
    }
    if (collection.archivedAt) {
      return 'Die Collection ist archiviert, ihre Vorlagen lassen sich nur ansehen.';
    }
    if (!ws.scopes.includes('schema')) {
      return 'Du kannst die Vorlagen ansehen, aber nicht ändern: dafür fehlt dir das Recht „schema“ (Admins und Owner haben es).';
    }
    return null;
  });

  /** `schema` scope on an active workspace and collection. */
  readonly canEdit = computed(
    () => !!this.workspace() && !!this.collection() && this.readOnlyReason() === null,
  );

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

  private fetchCollection(source: Source): Promise<Collection> {
    return firstValueFrom(
      this.api.workspace(source.slug).get('/collections/{collectionId}', {
        path: { collectionId: source.collectionId },
      }),
    );
  }

  private fetchTemplates(source: Source): Promise<TemplateDto[]> {
    return firstValueFrom(
      this.api.workspace(source.slug).get('/collections/{collectionId}/templates', {
        path: { collectionId: source.collectionId },
      }),
    );
  }

  /** A fresh copy of the templates from the server; the shown list stays as it is. */
  fetchFresh(): Promise<TemplateDto[]> {
    return this.fetchTemplates(this.require());
  }

  /** Fetches collection and templates again; a failure keeps what is shown. */
  async refresh(): Promise<void> {
    const source = this.source();
    if (!source) {
      return;
    }
    const [collection, templates] = await Promise.all([
      this.fetchCollection(source).catch(() => null),
      this.fetchTemplates(source).catch(() => null),
    ]);
    if (!sameSource(this.source(), source)) {
      return;
    }
    if (collection) {
      this.collectionResource.set(collection);
    }
    if (templates) {
      this.templatesResource.set(templates);
    }
  }

  /** Reloads only the templates (after an own change); errors of the change are thrown as `ApiError`. */
  private async refreshTemplates(source: Source): Promise<void> {
    try {
      const templates = await this.fetchTemplates(source);
      if (sameSource(this.source(), source)) {
        this.templatesResource.set(templates);
      }
    } catch {
      // keep what is shown; the next event or resync tries again
    }
  }

  async create(body: CreateTemplateBody): Promise<TemplateDto> {
    const source = this.require();
    const created = await firstValueFrom(
      this.api.workspace(source.slug).post('/collections/{collectionId}/templates', {
        path: { collectionId: source.collectionId },
        body,
      }),
    );
    await this.refreshTemplates(source);
    return created;
  }

  async update(templateId: string, body: UpdateTemplateBody): Promise<TemplateDto> {
    const source = this.require();
    try {
      const updated = await firstValueFrom(
        this.api
          .workspace(source.slug)
          .patch('/collections/{collectionId}/templates/{templateId}', {
            path: { collectionId: source.collectionId, templateId },
            body,
          }),
      );
      await this.refreshTemplates(source);
      return updated;
    } catch (error) {
      await this.refreshAfterFailure(source, error);
      throw error;
    }
  }

  async remove(templateId: string): Promise<void> {
    const source = this.require();
    try {
      await firstValueFrom(
        this.api
          .workspace(source.slug)
          .delete('/collections/{collectionId}/templates/{templateId}', {
            path: { collectionId: source.collectionId, templateId },
          }),
      );
      await this.refreshTemplates(source);
    } catch (error) {
      await this.refreshAfterFailure(source, error);
      throw error;
    }
  }

  /** After a failed change: 404 and 409 mean the list is outdated (template gone, default moved). */
  private async refreshAfterFailure(source: Source, error: unknown): Promise<void> {
    const status = ApiError.from(error).status;
    if (status === 404 || status === 409) {
      await this.refreshTemplates(source);
    }
  }

  private require(): Source {
    const source = this.source();
    if (!source) {
      throw new Error('TemplatesStore is not connected.');
    }
    return source;
  }
}
