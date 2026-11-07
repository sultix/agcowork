import { NgTemplateOutlet } from '@angular/common';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideX } from '@ng-icons/lucide';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmInput } from '@spartan-ng/helm/input';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  linkedSignal,
  output,
  signal,
} from '@angular/core';
import type { PropertyDefinition } from '../properties/property-types';
import {
  BuilderState,
  ConditionDraft,
  FilterField,
  FilterNode,
  FilterOp,
  GroupDraft,
  GroupKind,
  OPERATOR_LABELS,
  convertValue,
  draftId,
  emptyCondition,
  filterFields,
  isComplete,
  operatorsFor,
  sameFilter,
  toBuilder,
  toDsl,
} from './filter-dsl';
import { FilterValueEditorComponent } from './filter-value-editor.component';

interface BuilderModel {
  /** `null`: the filter cannot be shown in the builder (e.g. `not`, deep nesting). */
  readonly state: BuilderState | null;
  /** The DSL this state stands for; an incoming filter equal to it keeps the drafts. */
  readonly dsl: FilterNode | null;
}

/**
 * Filter builder that produces exactly the filter DSL of architecture §3: conditions
 * `{ prop, op, value }` with the operators the type matrix allows for the property, a root group
 * (`and`/`or`) and one level of subgroups. Emits the DSL (`null` = no filter) on every change;
 * incomplete conditions stay in the builder but are left out of the DSL. The generated JSON can be
 * shown and copied, for agents.
 */
@Component({
  selector: 'ag-filter-builder',
  imports: [NgTemplateOutlet, NgIcon, HlmButton, HlmInput, FilterValueEditorComponent],
  providers: [provideIcons({ lucideX })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div
      class="rounded-xl border bg-card p-3 text-sm text-card-foreground"
      data-testid="filter-builder"
    >
      @if (model().state; as state) {
        <div class="flex flex-wrap items-center gap-2">
          <span class="text-muted-foreground">Treffer müssen</span>
          <select
            hlmInput
            class="w-auto"
            data-testid="filter-root-kind"
            aria-label="Verknüpfung der Bedingungen"
            [disabled]="readonly()"
            (change)="setRootKind($any($event.target).value)"
          >
            <option value="and" [selected]="state.kind === 'and'">alle Bedingungen erfüllen</option>
            <option value="or" [selected]="state.kind === 'or'">
              mindestens eine Bedingung erfüllen
            </option>
          </select>
        </div>

        <ul class="mt-3 flex flex-col gap-2" data-testid="filter-items">
          @for (item of state.items; track item.id) {
            @if (item.kind === 'condition') {
              <li>
                <ng-container
                  *ngTemplateOutlet="conditionRow; context: { $implicit: item, groupId: null }"
                />
              </li>
            } @else {
              <li
                class="rounded-md border border-dashed border-border p-2"
                data-testid="filter-group"
              >
                <div class="flex flex-wrap items-center gap-2">
                  <span class="text-muted-foreground">Gruppe:</span>
                  <select
                    hlmInput
                    class="w-auto"
                    data-testid="filter-group-kind"
                    aria-label="Verknüpfung in der Gruppe"
                    [disabled]="readonly()"
                    (change)="setGroupKind(item.id, $any($event.target).value)"
                  >
                    <option value="and" [selected]="item.kind === 'and'">alle (und)</option>
                    <option value="or" [selected]="item.kind === 'or'">eine davon (oder)</option>
                  </select>
                  @if (!readonly()) {
                    <button
                      type="button"
                      hlmBtn
                      variant="ghost"
                      size="sm"
                      class="ml-auto text-muted-foreground hover:text-destructive"
                      data-testid="filter-group-remove"
                      (click)="remove(item.id)"
                    >
                      Gruppe entfernen
                    </button>
                  }
                </div>
                <ul class="mt-2 flex flex-col gap-2 pl-4">
                  @for (condition of item.items; track condition.id) {
                    <li>
                      <ng-container
                        *ngTemplateOutlet="
                          conditionRow;
                          context: { $implicit: condition, groupId: item.id }
                        "
                      />
                    </li>
                  }
                </ul>
                @if (!readonly()) {
                  <button
                    type="button"
                    hlmBtn
                    variant="ghost"
                    size="sm"
                    class="mt-2"
                    data-testid="filter-group-add"
                    (click)="addCondition(item.id)"
                  >
                    + Bedingung in der Gruppe
                  </button>
                }
              </li>
            }
          } @empty {
            <li class="text-muted-foreground" data-testid="filter-empty">
              Kein Filter: alle Records.
            </li>
          }
        </ul>

        @if (!readonly()) {
          <div class="mt-3 flex flex-wrap gap-3">
            <button
              type="button"
              hlmBtn
              variant="ghost"
              size="sm"
              data-testid="filter-add"
              (click)="addCondition(null)"
            >
              + Bedingung
            </button>
            <button
              type="button"
              hlmBtn
              variant="ghost"
              size="sm"
              data-testid="filter-add-group"
              (click)="addGroup()"
            >
              + Gruppe
            </button>
            <button
              type="button"
              hlmBtn
              variant="ghost"
              size="sm"
              class="ml-auto text-muted-foreground hover:text-destructive"
              data-testid="filter-clear"
              (click)="clear()"
            >
              Filter leeren
            </button>
          </div>
        }
      } @else {
        <p class="text-muted-foreground" data-testid="filter-unsupported">
          Dieser Filter (z. B. mit „not“ oder tiefer Verschachtelung) lässt sich hier nicht
          bearbeiten. Er bleibt aktiv; das JSON steht unten.
        </p>
        @if (!readonly()) {
          <button
            type="button"
            hlmBtn
            variant="ghost"
            size="sm"
            class="mt-2 text-muted-foreground hover:text-destructive"
            data-testid="filter-clear"
            (click)="clear()"
          >
            Filter leeren
          </button>
        }
      }

      <details class="mt-3" data-testid="filter-json">
        <summary class="cursor-pointer text-muted-foreground">
          Filter als JSON (für Agenten)
        </summary>
        <pre
          class="mt-2 max-h-64 overflow-auto rounded-md bg-muted p-2 font-mono text-xs"
          data-testid="filter-json-text"
          >{{ json() }}</pre>
        <button
          type="button"
          hlmBtn
          variant="ghost"
          size="sm"
          class="mt-1"
          data-testid="filter-json-copy"
          (click)="copy()"
        >
          {{ copied() ? 'Kopiert' : 'Kopieren' }}
        </button>
      </details>
    </div>

    <ng-template #conditionRow let-condition let-groupId="groupId">
      <div class="flex flex-wrap items-start gap-2" data-testid="filter-condition">
        <select
          hlmInput
          class="w-auto"
          data-testid="filter-prop"
          aria-label="Feld"
          [disabled]="readonly()"
          (change)="setProp(condition.id, $any($event.target).value)"
        >
          <option value="" [selected]="condition.prop === ''">Feld …</option>
          @for (field of fields(); track field.key) {
            <option [value]="field.key" [selected]="field.key === condition.prop">
              {{ field.name }}
            </option>
          }
        </select>
        @if (fieldOf(condition.prop); as field) {
          <select
            hlmInput
            class="w-auto"
            data-testid="filter-op"
            aria-label="Operator"
            [disabled]="readonly()"
            (change)="setOp(condition.id, $any($event.target).value)"
          >
            @for (op of operatorsOf(field); track op) {
              <option [value]="op" [selected]="op === condition.op">{{ labels[op] }}</option>
            }
          </select>
          @if (condition.op !== '') {
            <ag-filter-value-editor
              class="min-w-48 flex-1"
              [field]="field"
              [op]="condition.op"
              [value]="condition.value"
              [readonly]="readonly()"
              (valueChange)="setValue(condition.id, $event)"
            />
          }
        }
        @if (!complete(condition)) {
          <span class="self-center text-xs text-muted-foreground" data-testid="filter-incomplete"
            >unvollständig, wird ignoriert</span
          >
        }
        @if (!readonly()) {
          <button
            type="button"
            hlmBtn
            variant="ghost"
            size="icon-sm"
            class="self-center text-muted-foreground hover:text-destructive"
            data-testid="filter-remove"
            [attr.aria-label]="'Bedingung entfernen'"
            (click)="remove(condition.id)"
          >
            <ng-icon name="lucideX" />
          </button>
        }
      </div>
    </ng-template>
  `,
})
export class FilterBuilderComponent {
  readonly schema = input.required<readonly PropertyDefinition[]>();
  readonly filter = input<FilterNode | null>(null);
  readonly readonly = input(false);
  readonly filterChange = output<FilterNode | null>();

  protected readonly labels = OPERATOR_LABELS;
  protected readonly copied = signal(false);

  protected readonly fields = computed(() => filterFields(this.schema()));

  protected readonly model = linkedSignal<FilterNode | null, BuilderModel>({
    source: () => this.filter(),
    computation: (filter, previous) => {
      if (previous && sameFilter(filter, previous.value.dsl)) {
        return previous.value; // our own emission came back: keep incomplete drafts
      }
      return { state: toBuilder(filter), dsl: filter };
    },
  });

  protected readonly json = computed(() => {
    const model = this.model();
    return JSON.stringify(model.dsl ?? {}, null, 2);
  });

  protected fieldOf(key: string): FilterField | undefined {
    return this.fields().find((f) => f.key === key);
  }

  protected operatorsOf(field: FilterField): readonly FilterOp[] {
    return operatorsFor(field.type);
  }

  protected complete(draft: ConditionDraft): boolean {
    return isComplete(draft, this.fields());
  }

  protected setRootKind(kind: GroupKind): void {
    this.mutate((state) => ({ ...state, kind }));
  }

  protected setGroupKind(id: number, kind: GroupKind): void {
    this.mutate((state) => ({
      ...state,
      items: state.items.map((item) =>
        item.id === id && item.kind !== 'condition' ? { ...item, kind } : item,
      ),
    }));
  }

  protected addCondition(groupId: number | null): void {
    this.mutate((state) =>
      groupId === null
        ? { ...state, items: [...state.items, emptyCondition()] }
        : {
            ...state,
            items: state.items.map((item) =>
              item.id === groupId && item.kind !== 'condition'
                ? { ...item, items: [...item.items, emptyCondition()] }
                : item,
            ),
          },
    );
  }

  protected addGroup(): void {
    const group: GroupDraft = {
      kind: this.model().state?.kind === 'or' ? 'and' : 'or',
      id: draftId(),
      items: [emptyCondition()],
    };
    this.mutate((state) => ({ ...state, items: [...state.items, group] }));
  }

  protected remove(id: number): void {
    this.mutate((state) => ({
      ...state,
      items: state.items
        .filter((item) => item.id !== id)
        .map((item) =>
          item.kind === 'condition'
            ? item
            : { ...item, items: item.items.filter((c) => c.id !== id) },
        ),
    }));
  }

  protected setProp(id: number, prop: string): void {
    const field = this.fieldOf(prop);
    const op = field ? (operatorsFor(field.type)[0] ?? '') : '';
    this.updateCondition(id, () => ({
      prop,
      op,
      value: op === '' ? null : convertValue(null, '', op),
    }));
  }

  protected setOp(id: number, op: FilterOp): void {
    this.updateCondition(id, (c) => ({ op, value: convertValue(c.value, c.op, op) }));
  }

  protected setValue(id: number, value: unknown): void {
    this.updateCondition(id, () => ({ value }));
  }

  protected clear(): void {
    this.model.set({ state: { kind: 'and', items: [] }, dsl: null });
    this.filterChange.emit(null);
  }

  protected async copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.json());
      this.copied.set(true);
      setTimeout(() => this.copied.set(false), 1500);
    } catch {
      // clipboard unavailable (permissions, insecure context): the text stays selectable
    }
  }

  private updateCondition(
    id: number,
    change: (c: ConditionDraft) => Partial<Omit<ConditionDraft, 'kind' | 'id'>>,
  ): void {
    const apply = (c: ConditionDraft): ConditionDraft => (c.id === id ? { ...c, ...change(c) } : c);
    this.mutate((state) => ({
      ...state,
      items: state.items.map((item) =>
        item.kind === 'condition' ? apply(item) : { ...item, items: item.items.map(apply) },
      ),
    }));
  }

  private mutate(change: (state: BuilderState) => BuilderState): void {
    const state = this.model().state;
    if (!state || this.readonly()) {
      return;
    }
    const next = change(state);
    const dsl = toDsl(next, this.fields());
    const changed = !sameFilter(dsl, this.model().dsl);
    this.model.set({ state: next, dsl });
    if (changed) {
      this.filterChange.emit(dsl);
    }
  }
}
