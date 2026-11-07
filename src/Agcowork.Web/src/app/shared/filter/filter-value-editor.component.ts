import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
  output,
} from '@angular/core';
import { MembersStore } from '../../core/workspace/members.store';
import { PropertyEditorComponent } from '../properties/property-editor.component';
import type { PropertyDefinition, PropertyType } from '../properties/property-types';
import { FieldType, FilterField, FilterOp, arityOf } from './filter-dsl';

const ACTOR_TYPES: readonly FieldType[] = ['actor', 'created_by', 'updated_by'];
const DATETIME_TYPES: readonly FieldType[] = ['datetime', 'created_at', 'updated_at'];
const CHOICE_TYPES: readonly FieldType[] = ['select', 'status', 'multi_select'];

/** How the value of one condition is edited. */
type ValueEditor =
  | { readonly kind: 'none' }
  | { readonly kind: 'boolean' }
  | { readonly kind: 'list'; readonly numeric: boolean }
  | { readonly kind: 'property'; readonly definition: PropertyDefinition };

/**
 * Value input of one filter condition. Reuses the property editors with a definition that fits
 * the operator: a single option for `eq`, the option checkboxes for `in`/`nin`, a multiple
 * relation for `in`, members as options for actor lists. Free lists (text, number with `in`) are
 * typed comma-separated.
 */
@Component({
  selector: 'ag-filter-value-editor',
  imports: [PropertyEditorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @switch (editor().kind) {
      @case ('boolean') {
        <select
          class="rounded-md border border-border bg-bg px-2 py-1 text-sm"
          data-testid="filter-value-boolean"
          aria-label="Wert"
          [disabled]="readonly()"
          (change)="onBoolean($any($event.target).value)"
        >
          <option value="" [selected]="value() === null">–</option>
          <option value="true" [selected]="value() === true">ja</option>
          <option value="false" [selected]="value() === false">nein</option>
        </select>
      }
      @case ('list') {
        <input
          type="text"
          class="w-full rounded-md border border-border bg-bg px-2 py-1 text-sm"
          data-testid="filter-value-list"
          aria-label="Werte, durch Komma getrennt"
          placeholder="Wert 1, Wert 2"
          [value]="listText()"
          [readOnly]="readonly()"
          (input)="onList($any($event.target).value)"
        />
      }
      @case ('property') {
        <ag-property-editor
          data-testid="filter-value-property"
          [definition]="propertyDefinition()!"
          [value]="value()"
          [readonly]="readonly()"
          (valueChange)="valueChange.emit($event)"
        />
      }
    }
  `,
})
export class FilterValueEditorComponent {
  private readonly members = inject(MembersStore);

  readonly field = input.required<FilterField>();
  readonly op = input.required<FilterOp>();
  readonly value = input<unknown>(null);
  readonly readonly = input(false);
  readonly valueChange = output<unknown>();

  protected readonly editor = computed<ValueEditor>(() => {
    const field = this.field();
    const arity = arityOf(this.op());
    if (arity === 'none') {
      return { kind: 'none' };
    }
    if (field.type === 'checkbox') {
      return { kind: 'boolean' };
    }
    const many = arity === 'many';
    const base: PropertyDefinition = { ...field.definition, readOnly: false, required: false };
    const as = (type: PropertyType, extra: Partial<PropertyDefinition> = {}): ValueEditor => ({
      kind: 'property',
      definition: { ...base, type, ...extra },
    });
    if (CHOICE_TYPES.includes(field.type)) {
      return as(many ? 'multi_select' : 'select');
    }
    if (ACTOR_TYPES.includes(field.type)) {
      return many
        ? as('multi_select', {
            options: this.members.members().map((m) => ({
              value: m.actorId,
              label: m.name,
              color: null,
              group: null,
            })),
          })
        : as('actor');
    }
    if (field.type === 'relation') {
      return as('relation', { multiple: many });
    }
    if (many) {
      return { kind: 'list', numeric: field.type === 'number' };
    }
    if (DATETIME_TYPES.includes(field.type)) {
      return as('datetime');
    }
    if (field.type === 'date' || field.type === 'number') {
      return as(field.type);
    }
    return as('text');
  });

  protected readonly propertyDefinition = computed(() => {
    const editor = this.editor();
    return editor.kind === 'property' ? editor.definition : null;
  });

  /** Keeps what the user typed ("a, ") while the value list itself is normalised. */
  protected readonly listText = linkedSignal<unknown, string>({
    source: () => this.value(),
    computation: (value, previous) => {
      const text = Array.isArray(value) ? value.join(', ') : '';
      if (previous && sameList(parseList(previous.value, false), value)) {
        return previous.value;
      }
      return text;
    },
  });

  protected onBoolean(raw: string): void {
    this.valueChange.emit(raw === '' ? null : raw === 'true');
  }

  protected onList(raw: string): void {
    this.listText.set(raw);
    const editor = this.editor();
    const numeric = editor.kind === 'list' && editor.numeric;
    this.valueChange.emit(parseList(raw, numeric));
  }
}

function parseList(raw: string, numeric: boolean): unknown[] {
  const parts = raw
    .split(',')
    .map((p) => p.trim())
    .filter((p) => p !== '');
  if (!numeric) {
    return parts;
  }
  return parts.map(Number).filter((n) => !Number.isNaN(n));
}

function sameList(a: unknown[], b: unknown): boolean {
  return Array.isArray(b) && a.map(String).join('\u0000') === b.map(String).join('\u0000');
}
