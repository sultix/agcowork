import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  resource,
  signal,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import type { components } from '../../core/api/generated/schema';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { PropertyDefinition, PropertyValue, asStringList } from './property-types';

type RecordSummary = components['schemas']['RecordDto'];

const MAX_SUGGESTIONS = 8;

/**
 * Editor for `relation` properties: the selected records as chips plus a title search over the
 * first page of the target collection (client-side filter, until the query endpoint exists).
 * A single relation holds one id (`string`), a `multiple` relation a list.
 */
@Component({
  selector: 'ag-relation-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex flex-wrap items-center gap-1" data-testid="relation-selected">
      @for (id of selected(); track id) {
        <span
          class="inline-flex items-center gap-1 rounded-full border border-border px-2 py-0.5 text-xs"
          data-testid="relation-chip"
        >
          {{ titleOf(id) }}
          @if (!readonly()) {
            <button
              type="button"
              class="text-muted hover:text-danger"
              [attr.aria-label]="'Entfernen: ' + titleOf(id)"
              data-testid="relation-remove"
              (click)="remove(id)"
            >
              ×
            </button>
          }
        </span>
      } @empty {
        @if (readonly()) {
          <span class="text-sm text-muted">–</span>
        }
      }
    </div>
    @if (!readonly()) {
      <input
        type="search"
        class="mt-1 w-full rounded-md border border-border bg-bg px-2 py-1 text-sm"
        placeholder="Record suchen …"
        data-testid="relation-search"
        [attr.aria-label]="'Relation suchen'"
        [value]="query()"
        (input)="query.set($any($event.target).value)"
      />
      @if (query() !== '') {
        <ul
          class="mt-1 rounded-md border border-border bg-surface text-sm"
          data-testid="relation-suggestions"
        >
          @for (candidate of suggestions(); track candidate.id) {
            <li>
              <button
                type="button"
                class="block w-full px-2 py-1 text-left hover:bg-surface-hover"
                data-testid="relation-suggestion"
                (click)="add(candidate.id)"
              >
                {{ candidate.title }}
              </button>
            </li>
          } @empty {
            <li class="px-2 py-1 text-muted">Keine Treffer.</li>
          }
        </ul>
      }
    }
  `,
})
export class RelationEditorComponent {
  private readonly api = inject(ApiClient);
  private readonly context = inject(WorkspaceContext);

  readonly definition = input.required<PropertyDefinition>();
  readonly value = input<PropertyValue>(null);
  readonly readonly = input(false);
  /** `string` for a single relation, `string[]` for `multiple`; `null` when empty. */
  readonly valueChange = output<string | string[] | null>();

  protected readonly query = signal('');

  private readonly candidates = resource<
    RecordSummary[],
    { slug: string; collectionId: string } | undefined
  >({
    params: () => {
      const slug = this.context.slug();
      const collectionId = this.definition().targetCollectionId;
      return slug && collectionId ? { slug, collectionId } : undefined;
    },
    loader: async ({ params }) =>
      (
        await firstValueFrom(
          this.api.workspace(params.slug).get('/records', {
            query: { collectionId: params.collectionId, limit: 200 },
          }),
        )
      ).items,
  });

  protected readonly selected = computed(() => asStringList(this.value()));
  protected readonly suggestions = computed(() => {
    const needle = this.query().trim().toLowerCase();
    const taken = new Set(this.selected());
    const list = this.candidates.hasValue() ? this.candidates.value() : [];
    return list
      .filter((r) => !taken.has(r.id) && r.title.toLowerCase().includes(needle))
      .slice(0, MAX_SUGGESTIONS);
  });

  protected titleOf(id: string): string {
    const list = this.candidates.hasValue() ? this.candidates.value() : [];
    return list.find((r) => r.id === id)?.title ?? id.slice(0, 8);
  }

  protected add(id: string): void {
    this.query.set('');
    this.valueChange.emit(this.definition().multiple ? [...this.selected(), id] : id);
  }

  protected remove(id: string): void {
    const rest = this.selected().filter((s) => s !== id);
    this.valueChange.emit(this.definition().multiple ? (rest.length ? rest : null) : null);
  }
}
