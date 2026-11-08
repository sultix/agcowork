import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  InjectionToken,
  computed,
  inject,
  input,
  output,
  resource,
  signal,
} from '@angular/core';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmInput } from '@spartan-ng/helm/input';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import type { components } from '../../core/api/generated/schema';
import { RecordTitleStore } from '../../core/records/record-title.store';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { PropertyDefinition, PropertyValue, asStringList } from './property-types';
import { RelationChipsComponent } from './relation-chips.component';

type RecordSummary = components['schemas']['RecordDto'];
type QueryDefinition = components['schemas']['QueryDefinition'];

/** Quiet time after the last keystroke before the picker asks the server. */
export const RELATION_SEARCH_DEBOUNCE_MS = new InjectionToken<number>(
  'RELATION_SEARCH_DEBOUNCE_MS',
  { providedIn: 'root', factory: () => 250 },
);

/** Records one search asks for (some may be selected already and are left out). */
export const RELATION_SEARCH_LIMIT = 20;
const MAX_SUGGESTIONS = 8;

interface SearchParams {
  readonly slug: string;
  readonly collectionId: string;
  readonly term: string;
}

/** The query of one picker search: active records of the target whose title contains `term`. */
export function relationSearchQuery(term: string): QueryDefinition {
  return {
    filter: { prop: 'title', op: 'contains', value: term } as QueryDefinition['filter'],
    sorts: [{ prop: 'created_at', dir: 'desc' }],
    limit: RELATION_SEARCH_LIMIT,
    includeArchived: false,
    includeCount: false,
  };
}

/**
 * Editor for `relation` properties: the selected records as chips (titles from the shared title
 * cache) plus a title search in the target collection on the server (`title contains`, active
 * records, newest first, debounced), so every record of the target can be found, not only the
 * first page. A single relation holds one id (`string`), a `multiple` relation a list.
 */
@Component({
  selector: 'ag-relation-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, HlmInput, RelationChipsComponent],
  template: `
    <div data-testid="relation-selected">
      @if (selected().length > 0) {
        <ag-relation-chips
          class="flex-wrap"
          [ids]="selected()"
          [removable]="!readonly()"
          [navigation]="chipLinks() ? 'link' : 'none'"
          (remove)="remove($event)"
        />
      } @else if (readonly()) {
        <span class="text-sm text-muted-foreground">–</span>
      }
    </div>
    @if (!readonly()) {
      <input
        type="search"
        hlmInput
        class="mt-1"
        placeholder="Record suchen …"
        data-testid="relation-search"
        [attr.aria-label]="'Relation suchen'"
        [value]="query()"
        (input)="onInput($any($event.target).value)"
      />
      @if (query().trim() !== '') {
        <ul
          class="mt-1 rounded-md border bg-popover p-1 text-sm text-popover-foreground shadow-md"
          data-testid="relation-suggestions"
          aria-live="polite"
        >
          @if (searching()) {
            <li class="px-2 py-1 text-muted-foreground" data-testid="relation-searching">
              Sucht …
            </li>
          } @else if (search.error()) {
            <li class="px-2 py-1 text-destructive" data-testid="relation-search-error">
              Suche fehlgeschlagen.
            </li>
          } @else {
            @for (candidate of suggestions(); track candidate.id) {
              <li>
                <button
                  type="button"
                  hlmBtn
                  variant="ghost"
                  size="sm"
                  class="w-full min-w-0 justify-start font-normal"
                  data-testid="relation-suggestion"
                  (click)="add(candidate.id)"
                >
                  <span class="truncate">{{ candidate.title || 'Ohne Titel' }}</span>
                </button>
              </li>
            } @empty {
              <li class="px-2 py-1 text-muted-foreground">Keine Treffer.</li>
            }
          }
        </ul>
      }
    }
  `,
})
export class RelationEditorComponent {
  private readonly api = inject(ApiClient);
  private readonly context = inject(WorkspaceContext);
  private readonly titles = inject(RecordTitleStore);
  private readonly debounceMs = inject(RELATION_SEARCH_DEBOUNCE_MS);

  readonly definition = input.required<PropertyDefinition>();
  readonly value = input<PropertyValue>(null);
  readonly readonly = input(false);
  /** Selected chips link to their records (`false`: plain text, e.g. in the table's edit pane). */
  readonly chipLinks = input(true);
  /** `string` for a single relation, `string[]` for `multiple`; `null` when empty. */
  readonly valueChange = output<string | string[] | null>();

  /** What is typed. */
  protected readonly query = signal('');
  /** What is searched for: {@link query} once typing paused. */
  private readonly term = signal('');
  private timer: ReturnType<typeof setTimeout> | null = null;

  protected readonly search = resource<RecordSummary[], SearchParams | undefined>({
    params: () => {
      const slug = this.context.slug();
      const collectionId = this.definition().targetCollectionId;
      const term = this.term();
      return slug && collectionId && term !== '' ? { slug, collectionId, term } : undefined;
    },
    loader: async ({ params }) => {
      const page = await firstValueFrom(
        this.api.workspace(params.slug).post('/collections/{collectionId}/query', {
          path: { collectionId: params.collectionId },
          body: relationSearchQuery(params.term),
        }),
      );
      this.titles.prime(page.items);
      return page.items;
    },
  });

  protected readonly selected = computed(() => asStringList(this.value()));
  /** Typing has not paused yet, or the answer to the current term is still out. */
  protected readonly searching = computed(
    () => this.query().trim() !== this.term() || this.search.isLoading(),
  );
  protected readonly suggestions = computed(() => {
    const taken = new Set(this.selected());
    const list = this.search.hasValue() ? this.search.value() : [];
    return list.filter((r) => !taken.has(r.id)).slice(0, MAX_SUGGESTIONS);
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.clearTimer());
  }

  protected onInput(raw: string): void {
    this.query.set(raw);
    this.clearTimer();
    const term = raw.trim();
    if (term === '') {
      this.term.set('');
      return;
    }
    this.timer = setTimeout(() => {
      this.timer = null;
      this.term.set(term);
    }, this.debounceMs);
  }

  protected add(id: string): void {
    this.clearTimer();
    this.query.set('');
    this.term.set('');
    this.valueChange.emit(this.definition().multiple ? [...this.selected(), id] : id);
  }

  protected remove(id: string): void {
    const rest = this.selected().filter((s) => s !== id);
    this.valueChange.emit(this.definition().multiple ? (rest.length ? rest : null) : null);
  }

  private clearTimer(): void {
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }
}
