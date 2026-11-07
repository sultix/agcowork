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
    case 'relation': {
      const count = asStringList(value).length;
      return { text: count === 1 ? '1 Verknüpfung' : `${count} Verknüpfungen`, chips: [] };
    }
    case 'markdown':
      return { text: asText(value).split('\n', 1)[0].slice(0, 200), chips: [] };
    case 'json':
      return { text: JSON.stringify(value).slice(0, 200), chips: [] };
    default:
      return { text: asText(value), chips: [] };
  }
}

/** Display of one table cell or card field. */
@Component({
  selector: 'ag-cell-value',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0 truncate' },
  template: `@for (chip of display().chips; track $index) {
      <span class="mr-1 inline-block rounded-full border border-border px-2 text-xs">{{
        chip
      }}</span>
    }
    {{ display().text }}`,
})
export class CellValueComponent {
  private readonly members = inject(MembersStore);
  private readonly locale = inject(LOCALE_ID);

  readonly definition = input.required<PropertyDefinition>();
  readonly value = input<PropertyValue>(null);

  protected readonly display = computed(() =>
    cellDisplay(this.definition(), this.value(), (id) => this.members.nameOf(id), this.locale),
  );
}
