import { formatDate } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  LOCALE_ID,
  computed,
  inject,
  input,
} from '@angular/core';
import { MembersStore } from '../../core/workspace/members.store';
import { ChipNavigation, RelationChipsComponent } from '../properties/relation-chips.component';
import {
  PropertyDefinition,
  PropertyValue,
  asStringList,
  asText,
  optionLabel,
} from '../properties/property-types';

/** One display value of a cell: text plus, for options, chips. */
export interface CellDisplay {
  readonly text: string;
  readonly chips: readonly string[];
}

const EMPTY: CellDisplay = { text: '', chips: [] };

function dateText(value: PropertyValue, format: string, locale: string): string {
  const text = asText(value);
  if (text === '') {
    return '';
  }
  try {
    return formatDate(text, format, locale);
  } catch {
    return text;
  }
}

/** Read-only text of a property value as the table shows it (no editor, cheap to compute). */
export function cellDisplay(
  definition: PropertyDefinition,
  value: PropertyValue,
  names: (id: string) => string,
  locale = 'en-US',
): CellDisplay {
  if (value === null || value === undefined || value === '') {
    return EMPTY;
  }
  switch (definition.type) {
    case 'select':
    case 'status':
      return { text: '', chips: [optionLabel(definition, value)] };
    case 'multi_select':
      return { text: '', chips: asStringList(value).map((v) => optionLabel(definition, v)) };
    case 'checkbox':
      return { text: value === true ? '✓' : '', chips: [] };
    case 'number':
      return {
        text: definition.unit ? `${asText(value)} ${definition.unit}` : asText(value),
        chips: [],
      };
    case 'date':
      return { text: dateText(value, 'dd.MM.yyyy', locale), chips: [] };
    case 'datetime':
    case 'created_at':
    case 'updated_at':
      return { text: dateText(value, 'dd.MM.yyyy HH:mm', locale), chips: [] };
    case 'actor':
    case 'created_by':
    case 'updated_by':
      return { text: names(asText(value).replace(/^actor:/, '')), chips: [] };
    case 'relation':
      return EMPTY; // rendered as title chips by the component (see RelationChipsComponent)
    case 'markdown':
      return { text: asText(value).split('\n', 1)[0].slice(0, 200), chips: [] };
    case 'json':
      return { text: JSON.stringify(value).slice(0, 200), chips: [] };
    default:
      return { text: asText(value), chips: [] };
  }
}

/** Relation chips shown in one cell or card field; the rest is summed up as "+N". */
export const CELL_RELATION_CHIPS = 3;

/**
 * Display of one table cell or card field. Relation values are title chips (links to the
 * records, see {@link RelationChipsComponent}); their titles come from the shared title cache,
 * so a table full of relations costs one lookup per 200 ids, not one request per cell.
 */
@Component({
  selector: 'ag-cell-value',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RelationChipsComponent],
  host: { class: 'block min-w-0 truncate' },
  template: `@if (relationIds(); as ids) {
      <ag-relation-chips
        class="overflow-hidden"
        [ids]="ids"
        [max]="maxChips"
        [navigation]="chipNavigation()"
      />
    } @else {
      @for (chip of display().chips; track $index) {
        <span class="mr-1 inline-block rounded-full border border-border px-2 text-xs">{{
          chip
        }}</span>
      }
      {{ display().text }}
    }`,
})
export class CellValueComponent {
  private readonly members = inject(MembersStore);
  private readonly locale = inject(LOCALE_ID);

  readonly definition = input.required<PropertyDefinition>();
  readonly value = input<PropertyValue>(null);
  /** Relation chips: `cell` in the table (a double click edits the cell), `link` on cards. */
  readonly chipNavigation = input<ChipNavigation>('cell');

  protected readonly maxChips = CELL_RELATION_CHIPS;

  /** Ids of a relation value (`null` for other types, and for an empty relation). */
  protected readonly relationIds = computed(() => {
    if (this.definition().type !== 'relation') {
      return null;
    }
    const ids = asStringList(this.value());
    return ids.length > 0 ? ids : null;
  });

  protected readonly display = computed(() =>
    cellDisplay(this.definition(), this.value(), (id) => this.members.nameOf(id), this.locale),
  );
}
