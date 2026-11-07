import { DatePipe, NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmLabel } from '@spartan-ng/helm/label';
import { MembersStore } from '../../core/workspace/members.store';
import { MarkdownEditorComponent } from '../editor/markdown-editor.component';
import { RawViewComponent } from '../raw-view/raw-view.component';
import { ActorEditorComponent } from './actor-editor.component';
import {
  COMPUTED_TYPES,
  PropertyDefinition,
  PropertyValue,
  asStringList,
  asText,
} from './property-types';
import { RelationEditorComponent } from './relation-editor.component';

/** `2026-11-07T10:30:00Z` → `2026-11-07T11:30` (local time, for `datetime-local`). */
function toLocalInput(value: PropertyValue): string {
  const date = new Date(asText(value));
  if (Number.isNaN(date.getTime())) {
    return '';
  }
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function sameJson(a: unknown, b: unknown): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

/**
 * Editor for one property value, chosen by `definition().type`. Used by the document view and
 * (later) the table. `value` is the JSON value of the property; `valueChange` emits the new JSON
 * value (`null` = cleared) and never fires for values set from outside. Computed properties
 * (`created_at`, …) and `readonly` render the value without inputs.
 */
@Component({
  selector: 'ag-property-editor',
  imports: [
    DatePipe,
    HlmInput,
    HlmLabel,
    NgTemplateOutlet,
    ActorEditorComponent,
    MarkdownEditorComponent,
    RawViewComponent,
    RelationEditorComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[attr.data-property-type]': 'definition().type' },
  template: `
    @switch (definition().type) {
      @case ('markdown') {
        <ag-markdown-editor
          class="block rounded-md border border-border"
          [value]="text()"
          [readonly]="locked()"
          (valueChange)="emitText($event)"
        />
      }
      @case ('number') {
        <input
          type="number"
          hlmInput
          data-testid="editor-input"
          [attr.aria-label]="definition().name"
          [attr.step]="step()"
          [value]="text()"
          [readOnly]="locked()"
          (input)="onNumber($event)"
        />
      }
      @case ('checkbox') {
        <input
          type="checkbox"
          class="size-4 accent-primary"
          data-testid="editor-input"
          [attr.aria-label]="definition().name"
          [checked]="value() === true"
          [disabled]="locked()"
          (change)="valueChange.emit($any($event.target).checked)"
        />
      }
      @case ('select') {
        <ng-container [ngTemplateOutlet]="single" />
      }
      @case ('status') {
        <ng-container [ngTemplateOutlet]="single" />
      }
      @case ('multi_select') {
        <div class="flex flex-wrap gap-2" data-testid="editor-multi">
          @for (option of definition().options ?? []; track option.value) {
            <label hlmLabel class="gap-1.5 font-normal">
              <input
                type="checkbox"
                class="size-4 accent-primary"
                [attr.data-option]="option.value"
                [checked]="list().includes(option.value)"
                [disabled]="locked()"
                (change)="toggle(option.value)"
              />{{ option.label }}
            </label>
          } @empty {
            <span class="text-sm text-muted-foreground">Keine Optionen.</span>
          }
        </div>
      }
      @case ('date') {
        <input
          type="date"
          hlmInput
          class="w-auto"
          data-testid="editor-input"
          [attr.aria-label]="definition().name"
          [value]="text().slice(0, 10)"
          [readOnly]="locked()"
          (input)="onDate($event)"
        />
      }
      @case ('datetime') {
        <input
          type="datetime-local"
          hlmInput
          class="w-auto"
          data-testid="editor-input"
          [attr.aria-label]="definition().name"
          [value]="localDateTime()"
          [readOnly]="locked()"
          (input)="onDateTime($event)"
        />
      }
      @case ('url') {
        <input
          type="url"
          hlmInput
          data-testid="editor-input"
          [attr.aria-label]="definition().name"
          [value]="text()"
          [readOnly]="locked()"
          (input)="emitText($any($event.target).value)"
        />
      }
      @case ('email') {
        <input
          type="email"
          hlmInput
          data-testid="editor-input"
          [attr.aria-label]="definition().name"
          [value]="text()"
          [readOnly]="locked()"
          (input)="emitText($any($event.target).value)"
        />
      }
      @case ('relation') {
        <ag-relation-editor
          [definition]="definition()"
          [value]="value()"
          [readonly]="locked()"
          (valueChange)="valueChange.emit($event)"
        />
      }
      @case ('actor') {
        <ag-actor-editor
          [value]="value()"
          [readonly]="locked()"
          [label]="definition().name"
          (valueChange)="valueChange.emit($event)"
        />
      }
      @case ('json') {
        <ag-raw-view
          class="block"
          language="json"
          [value]="jsonText()"
          [readonly]="locked()"
          (valueChange)="onJson($event)"
        />
      }
      @case ('created_at') {
        <span class="text-sm" data-testid="editor-computed">{{
          text() | date: 'dd.MM.yyyy HH:mm'
        }}</span>
      }
      @case ('updated_at') {
        <span class="text-sm" data-testid="editor-computed">{{
          text() | date: 'dd.MM.yyyy HH:mm'
        }}</span>
      }
      @case ('created_by') {
        <span class="text-sm" data-testid="editor-computed">{{ actorName() }}</span>
      }
      @case ('updated_by') {
        <span class="text-sm" data-testid="editor-computed">{{ actorName() }}</span>
      }
      @default {
        <input
          type="text"
          hlmInput
          data-testid="editor-input"
          [attr.aria-label]="definition().name"
          [value]="text()"
          [readOnly]="locked()"
          (input)="emitText($any($event.target).value)"
        />
      }
    }

    <ng-template #single>
      <select
        hlmInput
        data-testid="editor-select"
        [attr.aria-label]="definition().name"
        [disabled]="locked()"
        (change)="emitText($any($event.target).value)"
      >
        <option value="" [selected]="text() === ''">–</option>
        @for (option of definition().options ?? []; track option.value) {
          <option [value]="option.value" [selected]="option.value === text()">
            {{ option.label }}
          </option>
        }
      </select>
    </ng-template>

    @if (errors().length > 0) {
      <div data-testid="editor-errors" role="alert">
        @for (message of errors(); track $index) {
          <p class="mt-1 text-sm text-destructive">{{ message }}</p>
        }
      </div>
    }
  `,
})
export class PropertyEditorComponent {
  private readonly members = inject(MembersStore);

  readonly definition = input.required<PropertyDefinition>();
  readonly value = input<PropertyValue>(null);
  readonly readonly = input(false);
  /** Validation messages for this property (from the API's `errors[]`). */
  readonly errors = input<readonly string[]>([]);

  readonly valueChange = output<PropertyValue>();

  protected readonly locked = computed(
    () =>
      this.readonly() ||
      this.definition().readOnly ||
      COMPUTED_TYPES.includes(this.definition().type),
  );
  protected readonly text = computed(() => asText(this.value()));
  protected readonly list = computed(() => asStringList(this.value()));
  protected readonly localDateTime = computed(() => toLocalInput(this.value()));
  protected readonly actorName = computed(() => this.members.nameOf(this.text()));
  protected readonly step = computed(() => {
    const precision = Number(this.definition().precision ?? 0);
    return precision > 0 ? String(10 ** -precision) : 'any';
  });

  /**
   * Text shown in the JSON view. Follows `value`, except for the echo of what the user just
   * typed (same JSON, different formatting), which would reset the cursor.
   */
  protected readonly jsonText = linkedSignal<PropertyValue, string>({
    source: () => this.value(),
    computation: (value, previous) => {
      if (previous) {
        try {
          if (sameJson(value, JSON.parse(previous.value))) {
            return previous.value;
          }
        } catch {
          // previous text was not valid JSON: show the new value
        }
      }
      return value == null ? '' : JSON.stringify(value, null, 2);
    },
  });

  protected emitText(raw: string): void {
    this.valueChange.emit(raw === '' ? null : raw);
  }

  /**
   * Whether the control holds input that cannot be sent (unparsable JSON, a half-typed number or
   * date). The document view warns before leaving with such input, because it is not saved.
   */
  readonly invalid = signal(false);

  private readonly markdown = viewChild(MarkdownEditorComponent);

  /** Emits a still debounced change of the Markdown editor now (before saving or leaving). */
  flush(): void {
    this.markdown()?.flush();
  }

  /** A cleared segment of a number/date input reports `''` with `badInput`: not a real clearing. */
  private isPartial(element: HTMLInputElement): boolean {
    const partial = element.value === '' && element.validity.badInput;
    this.invalid.set(partial);
    return partial;
  }

  protected onNumber(event: Event): void {
    const element = event.target as HTMLInputElement;
    if (!this.isPartial(element)) {
      this.emitNumber(element.value);
    }
  }

  protected onDate(event: Event): void {
    const element = event.target as HTMLInputElement;
    if (!this.isPartial(element)) {
      this.emitText(element.value);
    }
  }

  protected onDateTime(event: Event): void {
    const element = event.target as HTMLInputElement;
    if (!this.isPartial(element)) {
      this.emitDateTime(element.value);
    }
  }

  protected emitNumber(raw: string): void {
    if (raw === '') {
      this.valueChange.emit(null);
      return;
    }
    const number = Number(raw);
    if (!Number.isNaN(number)) {
      this.valueChange.emit(number);
    }
  }

  protected emitDateTime(raw: string): void {
    const date = new Date(raw);
    this.valueChange.emit(raw === '' || Number.isNaN(date.getTime()) ? null : date.toISOString());
  }

  protected toggle(option: string): void {
    const current = this.list();
    const next = current.includes(option)
      ? current.filter((o) => o !== option)
      : [...current, option];
    this.valueChange.emit(next.length > 0 ? next : null);
  }

  /** Emits parsed JSON; invalid text is only flagged by the view and never sent on. */
  protected onJson(text: string): void {
    this.jsonText.set(text);
    if (text.trim() === '') {
      this.invalid.set(false);
      this.valueChange.emit(null);
      return;
    }
    try {
      const parsed: unknown = JSON.parse(text);
      this.invalid.set(false);
      this.valueChange.emit(parsed);
    } catch {
      this.invalid.set(true); // the raw view shows "ungültig"
    }
  }
}
