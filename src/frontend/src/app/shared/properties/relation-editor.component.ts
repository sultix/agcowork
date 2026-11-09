import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  InjectionToken,
  computed,
  inject,
  input,
  linkedSignal,
  output,
  resource,
  signal,
} from '@angular/core';
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
let nextId = 0;

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
  imports: [HlmInput, RelationChipsComponent],
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
        role="combobox"
        placeholder="Record suchen …"
        autocomplete="off"
        aria-autocomplete="list"
        data-testid="relation-search"
        [attr.aria-label]="labelledBy() ? null : definition().name"
        [attr.aria-labelledby]="labelledBy()"
        [aria-describedby]="describedBy()"
        [attr.aria-expanded]="listOpen()"
        [attr.aria-controls]="listOpen() ? listId : null"
        [attr.aria-activedescendant]="listOpen() ? optionId(active()) : null"
        [value]="query()"
        (input)="onInput($any($event.target).value)"
        (keydown)="onKeydown($event)"
        (blur)="dismissed.set(true)"
      />
      <div role="status" data-testid="relation-status">
        @if (listOpen()) {
          <span class="sr-only">{{ suggestions().length }} Treffer</span>
        } @else if (open()) {
          <div
            class="mt-1 rounded-md border bg-popover p-1 text-sm text-popover-foreground shadow-md"
            data-testid="relation-suggestions"
          >
            @if (searching()) {
              <p class="px-2 py-1 text-muted-foreground" data-testid="relation-searching">
                Sucht …
              </p>
            } @else if (search.error()) {
              <p class="px-2 py-1 text-destructive" data-testid="relation-search-error">
                Suche fehlgeschlagen.
              </p>
            } @else {
              <p class="px-2 py-1 text-muted-foreground">Keine Treffer.</p>
            }
          </div>
        }
      </div>
      @if (listOpen()) {
        <ul
          role="listbox"
          class="mt-1 rounded-md border bg-popover p-1 text-sm text-popover-foreground shadow-md"
          data-testid="relation-suggestions"
          [id]="listId"
          [attr.aria-label]="definition().name + ': Treffer'"
        >
          @for (candidate of suggestions(); track candidate.id; let i = $index) {
            <!-- Options are operated from the combobox input (arrow keys, Enter); click is for the mouse. -->
            <!-- eslint-disable-next-line @angular-eslint/template/click-events-have-key-events, @angular-eslint/template/interactive-supports-focus -->
            <li
              role="option"
              class="cursor-pointer truncate rounded-sm px-2 py-1.5 hover:bg-accent hover:text-accent-foreground"
              [class.bg-accent]="i === active()"
              [class.text-accent-foreground]="i === active()"
              data-testid="relation-suggestion"
              [id]="optionId(i)"
              [attr.aria-selected]="i === active()"
              (mousedown)="$event.preventDefault()"
              (mousemove)="active.set(i)"
              (click)="add(candidate.id)"
            >
              {{ candidate.title || 'Ohne Titel' }}
            </li>
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
  /** Id of the visible label element naming the property (`aria-labelledby`); the property name otherwise. */
  readonly labelledBy = input<string | null>(null);
  /** Ids of elements describing the property (`aria-describedby`). */
  readonly describedBy = input<string | null>(null);
  /** `string` for a single relation, `string[]` for `multiple`; `null` when empty. */
  readonly valueChange = output<string | string[] | null>();

  private readonly uid = `ag-relation-${nextId++}`;
  protected readonly listId = `${this.uid}-list`;
  protected optionId(index: number): string {
    return `${this.uid}-option-${index}`;
  }

  /** The list the user closed with Escape; it stays closed until the next keystroke. */
  protected readonly dismissed = signal(false);

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

  /** Something is to show below the field: options, "searching", an error or "no hits". */
  protected readonly open = computed(() => this.query().trim() !== '' && !this.dismissed());
  /** There are options to choose from (whether or not the list is dismissed). */
  private readonly hasOptions = computed(
    () =>
      this.query().trim() !== '' &&
      !this.searching() &&
      !this.search.error() &&
      this.suggestions().length > 0,
  );
  /** The listbox is rendered. */
  protected readonly listOpen = computed(() => this.hasOptions() && !this.dismissed());
  /** Index of the option Enter would choose. */
  protected readonly active = linkedSignal<readonly RecordSummary[], number>({
    source: () => this.suggestions(),
    computation: () => 0,
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.clearTimer());
  }

  protected onKeydown(event: KeyboardEvent): void {
    if (event.isComposing) {
      return; // an input method (IME) is composing: Enter and arrows belong to it
    }
    if (!this.listOpen()) {
      if ((event.key === 'ArrowDown' || event.key === 'ArrowUp') && this.hasOptions()) {
        // Reopens a list closed by Escape or by leaving the field.
        event.preventDefault();
        this.dismissed.set(false);
        return;
      }
      if (event.key === 'Escape' && this.open()) {
        // Closes the hint only; dialogs and editors around must not see this Escape.
        event.preventDefault();
        event.stopPropagation();
        this.dismissed.set(true);
      }
      return;
    }
    const count = this.suggestions().length;
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        this.active.set((this.active() + 1) % count);
        break;
      case 'ArrowUp':
        event.preventDefault();
        this.active.set((this.active() - 1 + count) % count);
        break;
      case 'Enter':
        event.preventDefault();
        event.stopPropagation();
        this.add(this.suggestions()[this.active()].id);
        break;
      case 'Escape':
        event.preventDefault();
        event.stopPropagation();
        this.dismissed.set(true);
        break;
    }
  }

  protected onInput(raw: string): void {
    this.dismissed.set(false);
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
    this.dismissed.set(false);
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
