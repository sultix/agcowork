import { formatDate } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  LOCALE_ID,
  computed,
  inject,
  input,
} from '@angular/core';
import { optionSwatch } from '../../shared/properties/property-labels';
import {
  PropertyDefinition,
  asStringList,
  asText,
  optionLabel,
} from '../../shared/properties/property-types';

/** What the cover of a card shows: a label and optionally an option color. */
export interface CoverDisplay {
  readonly label: string;
  /** Tailwind background class of the option color, `null` for the neutral band. */
  readonly swatch: string | null;
  /** Large text (values) or a band label (options, checkbox). */
  readonly kind: 'band' | 'text' | 'initial';
}

function hostname(value: string): string {
  try {
    return new URL(value).hostname || value;
  } catch {
    return value;
  }
}

/**
 * Cover of a gallery card, built from a property value only (never an image): option color band
 * with its label, checkbox band, value as large text, or the initial of the title when there is
 * nothing to show. The label is always visible, the color is not the only carrier.
 */
export function coverDisplay(
  definition: PropertyDefinition | null | undefined,
  value: unknown,
  title: string,
  locale = 'en-US',
): CoverDisplay {
  const initial: CoverDisplay = {
    label: (title.trim().charAt(0) || '?').toLocaleUpperCase(),
    swatch: null,
    kind: 'initial',
  };
  if (!definition) {
    return initial;
  }
  if (definition.type === 'checkbox') {
    const done = value === true;
    return {
      label: done ? 'Erledigt' : 'Offen',
      swatch: done ? 'bg-green-500' : null,
      kind: 'band',
    };
  }
  if (value === null || value === undefined || value === '') {
    return initial;
  }
  switch (definition.type) {
    case 'select':
    case 'status':
    case 'multi_select': {
      const first = definition.type === 'multi_select' ? asStringList(value)[0] : asText(value);
      if (!first) {
        return initial;
      }
      const option = definition.options?.find((o) => o.value === first);
      return {
        label: optionLabel(definition, first),
        swatch: optionSwatch(option?.color),
        kind: 'band',
      };
    }
    case 'url':
      return { label: hostname(asText(value)), swatch: null, kind: 'text' };
    case 'number':
      return {
        label: definition.unit ? `${asText(value)} ${definition.unit}` : asText(value),
        swatch: null,
        kind: 'text',
      };
    case 'date': {
      let label = asText(value);
      try {
        label = formatDate(label, 'dd.MM.yyyy', locale);
      } catch {
        // keep the raw text
      }
      return { label, swatch: null, kind: 'text' };
    }
    default:
      return { label: asText(value), swatch: null, kind: 'text' };
  }
}

/** The cover area on top of a gallery card. */
@Component({
  selector: 'ag-card-cover',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block', 'data-testid': 'card-cover' },
  template: `
    <div
      class="flex h-20 items-center justify-center overflow-hidden px-3"
      [class]="cover().swatch ?? 'bg-muted'"
      [attr.data-kind]="cover().kind"
    >
      @switch (cover().kind) {
        @case ('band') {
          <span
            class="max-w-full truncate rounded-md bg-background/90 px-2 py-0.5 text-sm font-medium text-foreground"
            data-testid="card-cover-label"
            >{{ cover().label }}</span
          >
        }
        @case ('text') {
          <span
            class="line-clamp-2 break-words text-center text-xl font-semibold text-foreground"
            data-testid="card-cover-label"
            >{{ cover().label }}</span
          >
        }
        @default {
          <span
            class="text-3xl font-semibold text-muted-foreground"
            aria-hidden="true"
            data-testid="card-cover-label"
            >{{ cover().label }}</span
          >
        }
      }
    </div>
  `,
})
export class CardCoverComponent {
  private readonly locale = inject(LOCALE_ID);

  readonly definition = input<PropertyDefinition | null>(null);
  readonly value = input<unknown>(null);
  readonly title = input('');

  protected readonly cover = computed(() =>
    coverDisplay(this.definition(), this.value(), this.title(), this.locale),
  );
}
