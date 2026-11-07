import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideArrowDown, lucideArrowUp, lucideX } from '@ng-icons/lucide';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import type { PropertyDefinition } from '../properties/property-types';
import { SortSpec, filterFields } from './filter-dsl';

/** The active sorting as removable chips (in priority order); set by clicking column headers. */
@Component({
  selector: 'ag-sort-chips',
  imports: [NgIcon, HlmBadge, HlmButton],
  providers: [provideIcons({ lucideArrowDown, lucideArrowUp, lucideX })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (chips().length > 0) {
      <ul
        class="flex flex-wrap items-center gap-1 text-xs"
        aria-label="Sortierung"
        data-testid="sort-chips"
      >
        <li class="text-muted-foreground">Sortiert nach</li>
        @for (chip of chips(); track chip.prop; let i = $index) {
          <li
            hlmBadge
            variant="outline"
            class="gap-1"
            data-testid="sort-chip"
            [attr.data-prop]="chip.prop"
          >
            <span>{{ chip.name }}</span>
            <ng-icon
              [name]="chip.dir === 'asc' ? 'lucideArrowUp' : 'lucideArrowDown'"
              [attr.data-dir]="chip.dir"
              data-testid="sort-chip-dir"
            />
            <span class="sr-only">{{ chip.dir === 'asc' ? 'aufsteigend' : 'absteigend' }}</span>
            <button
              type="button"
              hlmBtn
              variant="ghost"
              size="icon-xs"
              class="size-4 rounded-full hover:text-destructive"
              data-testid="sort-chip-remove"
              [attr.aria-label]="'Sortierung entfernen: ' + chip.name"
              (click)="remove(i)"
            >
              <ng-icon name="lucideX" />
            </button>
          </li>
        }
      </ul>
    }
  `,
})
export class SortChipsComponent {
  readonly sorts = input.required<readonly SortSpec[]>();
  readonly schema = input.required<readonly PropertyDefinition[]>();
  readonly sortsChange = output<SortSpec[]>();

  protected readonly chips = computed(() => {
    const fields = filterFields(this.schema());
    return this.sorts().map((s) => ({
      ...s,
      name: fields.find((f) => f.key === s.prop)?.name ?? s.prop,
    }));
  });

  protected remove(index: number): void {
    this.sortsChange.emit(this.sorts().filter((_, i) => i !== index));
  }
}

/**
 * Sorting after a click on a column header: a plain click sorts by that column alone (again:
 * flips the direction), Shift adds it as a further criterion (or flips it if already there).
 */
export function toggleSort(sorts: readonly SortSpec[], prop: string, multi: boolean): SortSpec[] {
  const existing = sorts.find((s) => s.prop === prop);
  const flipped: SortSpec = { prop, dir: existing?.dir === 'asc' ? 'desc' : 'asc' };
  if (multi) {
    return existing
      ? sorts.map((s) => (s.prop === prop ? flipped : s))
      : [...sorts, { prop, dir: 'asc' }];
  }
  return [existing && sorts.length === 1 ? flipped : { prop, dir: 'asc' }];
}
