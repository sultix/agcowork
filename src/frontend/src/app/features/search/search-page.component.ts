import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  linkedSignal,
  signal,
  untracked,
} from '@angular/core';
import { FormField, form, maxLength, validate } from '@angular/forms/signals';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCircleAlert, lucideSearch } from '@ng-icons/lucide';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCheckbox } from '@spartan-ng/helm/checkbox';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmLabel } from '@spartan-ng/helm/label';
import { HlmNativeSelectImports } from '@spartan-ng/helm/native-select';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError } from '../../core/api/api-error';
import { fieldErrorMessage } from '../../core/api/field-errors';
import type { components } from '../../core/api/generated/schema';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { describeError } from '../../shared/ui/api-error-text';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { WorkspaceTreeStore } from '../spaces/workspace-tree.store';
import { SnippetPart, splitSnippet } from './snippet';

export type SearchHit = components['schemas']['SearchHitDto'];

const PAGE_SIZE = 20;
const MAX_QUERY_LENGTH = 200;

/** Texts for the field errors of `q` that need more than the generic message. */
const Q_ERROR_TEXTS: Record<string, string> = {
  empty_query:
    'Die Suche enthält keine Suchwörter. Gib mindestens ein Wort ein (nicht nur Operatoren wie „-“ oder Satzzeichen).',
  invalid_value: 'Die Suchanfrage ist ungültig. Bitte formuliere sie anders.',
  too_long: `Höchstens ${MAX_QUERY_LENGTH} Zeichen.`,
};

interface Row {
  readonly hit: SearchHit;
  readonly parts: SnippetPart[];
  readonly link: string[];
  /** "Space / Collection"; archived or unknown parents are named as such; empty while loading. */
  readonly context: string;
}

/**
 * `/w/:slug/search?q=…&spaceId=…&collectionId=…&includeArchived=true`: full-text search results.
 * The URL is the state (shareable, Back restores it); the page reads it through route inputs and
 * writes it when the query or a filter changes. Snippets are shown as text with the hits in
 * `<mark>`; nothing of the content is ever rendered as markup.
 */
@Component({
  selector: 'ag-search-page',
  imports: [
    DatePipe,
    NgIcon,
    RouterLink,
    HlmAlertImports,
    HlmButton,
    HlmCheckbox,
    HlmFieldImports,
    FormField,
    HlmInput,
    HlmLabel,
    HlmNativeSelectImports,
    HlmSkeleton,
    BadgeComponent,
  ],
  providers: [provideIcons({ lucideCircleAlert, lucideSearch })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="mx-auto flex max-w-4xl flex-col gap-6 p-6">
      <div class="flex flex-col gap-1">
        <h1 class="text-2xl font-semibold tracking-tight">Suche</h1>
        <p class="text-sm text-muted-foreground">
          Volltextsuche über Titel, Eigenschaften und Text.
        </p>
      </div>

      <form
        class="flex flex-col gap-4"
        role="search"
        novalidate
        data-testid="search-form"
        (submit)="submit($event)"
      >
        <div hlmField class="gap-2">
          <label hlmFieldLabel for="search-q">Suchbegriff</label>
          <div class="flex gap-2">
            <input
              hlmInput
              id="search-q"
              type="search"
              class="flex-1"
              autocomplete="off"
              data-testid="search-q"
              aria-describedby="search-q-errors"
              [formField]="form.q"
            />
            <button hlmBtn type="submit" data-testid="search-submit">
              <ng-icon name="lucideSearch" />
              Suchen
            </button>
          </div>
          <div id="search-q-errors" role="status" aria-live="polite" data-testid="search-q-errors">
            @if (showQueryErrors()) {
              @for (error of form.q().errors(); track $index) {
                <p class="text-sm text-destructive" data-testid="search-q-error">
                  {{ error.message }}
                </p>
              }
            }
          </div>
        </div>

        <div class="flex flex-wrap items-end gap-4" data-testid="search-filters">
          <div hlmField class="w-44 gap-2">
            <label hlmFieldLabel for="filter-space">Space</label>
            <hlm-native-select
              selectId="filter-space"
              class="w-full"
              data-testid="filter-space"
              [value]="selectedSpaceId()"
              (valueChange)="setSpace($any($event))"
            >
              <option hlmNativeSelectOption value="" [selected]="!selectedSpaceId()">Alle</option>
              @for (space of tree.spaces(); track space.id) {
                <option
                  hlmNativeSelectOption
                  [value]="space.id"
                  [selected]="space.id === selectedSpaceId()"
                >
                  {{ space.name }}
                </option>
              }
            </hlm-native-select>
          </div>
          <div hlmField class="w-44 gap-2">
            <label hlmFieldLabel for="filter-collection">Collection</label>
            <hlm-native-select
              selectId="filter-collection"
              class="w-full"
              data-testid="filter-collection"
              [value]="selectedCollectionId()"
              (valueChange)="setCollection($any($event))"
            >
              <option hlmNativeSelectOption value="" [selected]="!selectedCollectionId()">
                Alle
              </option>
              @for (collection of collections(); track collection.id) {
                <option
                  hlmNativeSelectOption
                  [value]="collection.id"
                  [selected]="collection.id === selectedCollectionId()"
                >
                  {{ collection.name }}
                </option>
              }
            </hlm-native-select>
          </div>
          <div class="flex items-center gap-2 pb-2">
            <hlm-checkbox
              inputId="filter-archived"
              data-testid="filter-archived"
              [checked]="archived()"
              (checkedChange)="setArchived($event === true)"
            />
            <label hlmLabel class="font-normal" for="filter-archived"
              >Archivierte einbeziehen</label
            >
          </div>
        </div>
      </form>

      @if (filterMissing()) {
        <div hlmAlert role="status" data-testid="search-filter-missing">
          <ng-icon name="lucideCircleAlert" />
          <p hlmAlertTitle>Filter nicht gefunden</p>
          <p hlmAlertDescription>
            Der gewählte Space oder die gewählte Collection existiert in diesem Workspace nicht
            (mehr).
          </p>
          <div hlmAlertAction>
            <button
              hlmBtn
              type="button"
              variant="outline"
              size="sm"
              data-testid="search-filter-clear"
              (click)="clearFilters()"
            >
              Filter entfernen
            </button>
          </div>
        </div>
      }
      @if (error(); as message) {
        <div hlmAlert variant="destructive" data-testid="search-error">
          <ng-icon name="lucideCircleAlert" />
          <p hlmAlertDescription>{{ message }}</p>
        </div>
      }

      @if (rows().length > 0) {
        <ul class="divide-y rounded-xl border" data-testid="search-results">
          @for (row of rows(); track row.hit.recordId) {
            <li class="flex flex-col gap-1 px-4 py-3" data-testid="search-hit">
              <div class="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
                <span class="flex flex-wrap items-center gap-2">
                  <a
                    [routerLink]="row.link"
                    class="font-medium underline underline-offset-4"
                    data-testid="hit-title"
                    >{{ row.hit.title || 'Ohne Titel' }}</a
                  >
                  @if (row.hit.archived) {
                    <ag-badge data-testid="hit-archived">Archiviert</ag-badge>
                  }
                </span>
                <span class="text-xs text-muted-foreground" data-testid="hit-updated">{{
                  row.hit.updatedAt | date: 'dd.MM.yyyy HH:mm'
                }}</span>
              </div>
              @if (row.context) {
                <p class="text-xs text-muted-foreground" data-testid="hit-context">
                  {{ row.context }}
                </p>
              }
              @if (row.parts.length > 0) {
                <p class="text-sm break-words" data-testid="hit-snippet">
                  @for (part of row.parts; track $index) {
                    @if (part.match) {
                      <mark
                        class="rounded-sm bg-amber-200/70 px-0.5 text-foreground dark:bg-amber-400/30"
                        >{{ part.text }}</mark
                      >
                    } @else {
                      <ng-container>{{ part.text }}</ng-container>
                    }
                  }
                </p>
              }
            </li>
          }
        </ul>
      } @else if (loading()) {
        <div class="flex flex-col gap-2" aria-busy="true" aria-label="Wird geladen">
          <hlm-skeleton class="h-16 rounded-lg" />
          <hlm-skeleton class="h-16 rounded-lg" />
          <hlm-skeleton class="h-16 rounded-lg" />
        </div>
      } @else if (!query()) {
        <div
          class="flex flex-col items-center gap-2 rounded-xl border border-dashed p-10 text-center"
          data-testid="search-hint"
        >
          <p class="font-medium">Wonach suchst du?</p>
          <p class="text-sm text-muted-foreground">
            Mehrere Wörter müssen alle vorkommen, <code>"genaue Phrase"</code> in Anführungszeichen,
            <code>-Wort</code> schließt ein Wort aus, <code>or</code> verbindet Alternativen. Das
            letzte Wort wird ab zwei Zeichen auch als Wortanfang gefunden.
          </p>
        </div>
      } @else if (searched() && !error() && !queryError() && !filterMissing()) {
        <div
          class="flex flex-col items-center gap-1 rounded-xl border border-dashed p-10 text-center"
        >
          <p class="font-medium" role="status" data-testid="search-empty">Keine Treffer.</p>
          <p class="text-sm text-muted-foreground">
            Prüfe die Schreibweise, nimm weniger Wörter oder beziehe Archivierte ein.
          </p>
        </div>
      }
      @if (nextCursor()) {
        <div>
          <button
            hlmBtn
            type="button"
            variant="outline"
            data-testid="load-more"
            [disabled]="loading()"
            (click)="loadMore()"
          >
            Mehr laden
          </button>
        </div>
      }
    </section>
  `,
})
export class SearchPageComponent {
  protected readonly tree = inject(WorkspaceTreeStore);
  private readonly context = inject(WorkspaceContext);
  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  // Query parameters, bound by the router (withComponentInputBinding).
  readonly q = input<string | undefined>();
  readonly spaceId = input<string | undefined>();
  readonly collectionId = input<string | undefined>();
  readonly includeArchived = input<string | undefined>();

  /** Model of the search field; follows the URL whenever it changes. */
  private readonly model = linkedSignal(() => ({ q: this.q() ?? '' }));

  protected readonly form = form(this.model, (path) => {
    maxLength(path.q, MAX_QUERY_LENGTH, { message: Q_ERROR_TEXTS['too_long'] });
    // The server's complaint about the query of the URL keeps the field invalid until the next search.
    validate(path.q, () => {
      const message = this.queryError();
      return message ? { kind: 'server', message } : undefined;
    });
  });

  protected readonly query = computed(() => (this.q() ?? '').trim());
  protected readonly selectedSpaceId = computed(() => this.spaceId() ?? '');
  protected readonly selectedCollectionId = computed(() => this.collectionId() ?? '');
  protected readonly archived = computed(() => this.includeArchived() === 'true');

  protected readonly collections = computed(() =>
    this.tree
      .collections()
      .filter((c) => !this.selectedSpaceId() || c.spaceId === this.selectedSpaceId()),
  );

  private readonly hits = signal<readonly SearchHit[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(false);
  /** True once a search for the current URL has answered (also with an error). */
  protected readonly searched = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly queryError = signal<string | null>(null);
  /** The search answered 404: the space or collection of the URL does not exist. */
  protected readonly filterMissing = signal(false);
  private token = 0;

  protected readonly showQueryErrors = computed(
    () => !!this.queryError() || (this.form.q().touched() && this.form.q().invalid()),
  );

  protected readonly rows = computed<Row[]>(() => {
    const slug = this.context.slug() ?? '';
    const known = !this.tree.isLoading();
    return this.hits().map((hit) => ({
      hit,
      parts: splitSnippet(hit.snippet),
      link: ['/w', slug, 'records', hit.recordId],
      context: known ? this.contextOf(hit) : '',
    }));
  });

  /** The navigation tree lists active spaces and collections only; the rest is archived. */
  private contextOf(hit: SearchHit): string {
    const space = this.tree.findSpace(hit.spaceId)?.name ?? 'archivierter Space';
    if (!hit.collectionId) {
      return space;
    }
    const collection = this.tree.findCollection(hit.collectionId)?.name ?? 'archivierte Collection';
    return `${space} / ${collection}`;
  }

  /** What is searched; a new value (also a changed filter) starts a new search. */
  private readonly request = computed(() => ({
    slug: this.context.slug(),
    query: this.query(),
    spaceId: this.selectedSpaceId(),
    collectionId: this.selectedCollectionId(),
    archived: this.archived(),
  }));

  constructor() {
    effect(() => {
      const { slug, query } = this.request();
      untracked(() => {
        if (slug && query) {
          void this.load(slug, null);
        } else {
          this.reset();
        }
      });
    });
  }

  protected submit(event: Event): void {
    event.preventDefault();
    this.navigate({ q: this.form.q().value().trim() || null });
  }

  protected setSpace(id: string): void {
    const collectionId = this.selectedCollectionId();
    const keep = !id || !collectionId || this.tree.findCollection(collectionId)?.spaceId === id;
    this.navigate({ spaceId: id || null, ...(keep ? {} : { collectionId: null }) });
  }

  protected setCollection(id: string): void {
    this.navigate({ collectionId: id || null });
  }

  protected clearFilters(): void {
    this.navigate({ spaceId: null, collectionId: null });
  }

  protected setArchived(value: boolean): void {
    this.navigate({ includeArchived: value ? 'true' : null });
  }

  protected loadMore(): void {
    const slug = this.context.slug();
    const cursor = this.nextCursor();
    if (slug && cursor) {
      void this.load(slug, cursor);
    }
  }

  /** Writes the changed parameters into the URL (a new history entry); `null` removes one. */
  private navigate(params: Record<string, string | null>): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: params,
      queryParamsHandling: 'merge',
    });
  }

  private reset(): void {
    this.token++;
    this.hits.set([]);
    this.nextCursor.set(null);
    this.loading.set(false);
    this.searched.set(false);
    this.error.set(null);
    this.queryError.set(null);
    this.filterMissing.set(false);
  }

  private async load(slug: string, cursor: string | null): Promise<void> {
    const token = ++this.token;
    if (cursor === null) {
      this.hits.set([]);
      this.nextCursor.set(null);
      this.searched.set(false);
    }
    this.loading.set(true);
    this.error.set(null);
    this.queryError.set(null);
    this.filterMissing.set(false);
    try {
      const page = await firstValueFrom(
        this.api.workspace(slug).get('/search', {
          query: {
            q: this.query(),
            ...(this.selectedSpaceId() ? { spaceId: this.selectedSpaceId() } : {}),
            ...(this.selectedCollectionId() ? { collectionId: this.selectedCollectionId() } : {}),
            ...(this.archived() ? { includeArchived: true } : {}),
            limit: PAGE_SIZE,
            ...(cursor ? { cursor } : {}),
          },
        }),
      );
      if (token !== this.token) {
        return;
      }
      this.hits.update((current) => {
        const listed = new Set(current.map((h) => h.recordId));
        return [...current, ...page.items.filter((h) => !listed.has(h.recordId))];
      });
      this.nextCursor.set(page.nextCursor ?? null);
      this.searched.set(true);
    } catch (error) {
      if (token === this.token) {
        this.searched.set(true);
        this.showError(error);
      }
    } finally {
      if (token === this.token) {
        this.loading.set(false);
      }
    }
  }

  private showError(error: unknown): void {
    const apiError = ApiError.from(error);
    if (apiError.status === 404 && (this.selectedSpaceId() || this.selectedCollectionId())) {
      this.filterMissing.set(true);
      return;
    }
    const field = apiError.errors.find((e) => e.field === 'q');
    if (apiError.code === 'validation_failed' && field) {
      this.queryError.set(Q_ERROR_TEXTS[field.code] ?? fieldErrorMessage(field));
    } else {
      this.error.set(describeError(apiError));
    }
  }
}
