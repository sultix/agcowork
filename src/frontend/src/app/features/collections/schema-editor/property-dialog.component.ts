import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  linkedSignal,
  output,
  signal,
  untracked,
} from '@angular/core';
import {
  FieldTree,
  FormField,
  applyEach,
  disabled,
  form,
  hidden,
  max,
  maxLength,
  min,
  pattern,
  required,
  submit,
  validate,
} from '@angular/forms/signals';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCircleAlert, lucideInfo, lucidePlus, lucideX } from '@ng-icons/lucide';
import type { BrnDialogState } from '@spartan-ng/brain/dialog';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCheckbox } from '@spartan-ng/helm/checkbox';
import { HlmDialogImports } from '@spartan-ng/helm/dialog';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmLabel } from '@spartan-ng/helm/label';
import { HlmNativeSelectImports } from '@spartan-ng/helm/native-select';
import { HlmTextarea } from '@spartan-ng/helm/textarea';
import { ApiError } from '../../../core/api/api-error';
import {
  OPTION_COLORS,
  PROPERTY_TYPE_GROUPS,
  STATUS_GROUPS,
  optionSwatch,
  propertyTypeLabel,
} from '../../../shared/properties/property-labels';
import { PropertyEditorComponent } from '../../../shared/properties/property-editor.component';
import type {
  PropertyDefinition,
  PropertyType,
  PropertyValue,
} from '../../../shared/properties/property-types';
import { ConfirmDialogComponent } from '../../../shared/ui/confirm-dialog.component';
import { FieldErrorsComponent } from '../../../shared/ui/field-errors.component';
import { WorkspaceTreeStore } from '../../spaces/workspace-tree.store';
import {
  AffectedCount,
  DESCRIPTION_MAX_LENGTH,
  KEY_PATTERN,
  Loss,
  MAX_PRECISION,
  NAME_MAX_LENGTH,
  OPTION_LABEL_MAX_LENGTH,
  OPTION_VALUE_MAX_LENGTH,
  PropertyDraft,
  RESERVED_KEYS,
  TITLE_KEY,
  UNIT_MAX_LENGTH,
  allowedTypes,
  allowsDefault,
  describeLoss,
  draftOf,
  draftOptions,
  emptyDraft,
  hasOptions,
  isComputed,
  isEmptyValue,
  lossesOf,
  newOption,
  rebaseDraft,
  suggestKey,
  toChangeInput,
  toDefinitionInput,
} from './schema-draft';
import { REMOVAL_NOT_CONFIRMED, SCHEMA_VERSION_CONFLICT, mapSchemaErrors } from './schema-errors';
import { SchemaEditorStore, UpdateSchemaBody } from './schema-editor.store';

/** A pending confirmation of a change that loses values. */
interface PendingConfirm {
  readonly losses: readonly Loss[];
  readonly counts: readonly AffectedCount[];
}

const KEY_LOCKED =
  'Keys sind unveränderlich: Records, Ansichten, Filter und Agenten verweisen darauf. Lege bei Bedarf eine neue Property an.';
const TARGET_LOCKED =
  'Das Ziel einer Relation lässt sich nicht ändern. Lege bei Bedarf eine neue Relation an.';
const MULTIPLE_LOCKED =
  'Eine Relation mit mehreren Verweisen lässt sich nicht wieder auf einen beschränken: gespeicherte Werte würden nicht mehr passen.';
const OPTION_VALUE_LOCKED = 'Records speichern diesen Wert; ändere stattdessen die Beschriftung.';

/**
 * Modal for one property of a collection schema (`PATCH …/schema`): with `original` it edits that
 * property (`update`), without it adds a new one (`add`). Lives below the schema editor page and
 * uses its {@link SchemaEditorStore}.
 *
 * - Only what the user changed is sent; key, relation target and a widened `multiple` are locked
 *   with an explanation, the type offers only the allowed changes.
 * - Changes that lose values (removed options, lower precision, markdown → text) ask first, with
 *   the number of affected records, and then send `confirmRemoval: true`.
 * - `expectedSchemaVersion` is the version the dialog was opened with. On 409 the schema is
 *   reloaded, the user's input is kept (fields the user did not touch take the new values) and the
 *   next save is checked against the new version.
 *
 * Emits `saved` with a status message on success; the parent removes the component on `saved`
 * and `cancelled`.
 */
@Component({
  selector: 'ag-property-dialog',
  imports: [
    FormField,
    NgIcon,
    ConfirmDialogComponent,
    FieldErrorsComponent,
    HlmAlertImports,
    HlmButton,
    HlmCheckbox,
    HlmDialogImports,
    HlmFieldImports,
    HlmInput,
    HlmLabel,
    HlmNativeSelectImports,
    HlmTextarea,
    PropertyEditorComponent,
  ],
  providers: [provideIcons({ lucideCircleAlert, lucideInfo, lucidePlus, lucideX })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-dialog
      state="open"
      [closeOnOutsidePointerEvents]="false"
      (stateChanged)="onStateChanged($event)"
    >
      <hlm-dialog-content
        *hlmDialogPortal
        class="max-h-[calc(100svh-2rem)] overflow-y-auto sm:max-w-2xl"
        [showCloseButton]="false"
      >
        <hlm-dialog-header>
          <h2 hlmDialogTitle>
            {{ isNew() ? 'Property hinzufügen' : 'Property „' + base()?.name + '“ bearbeiten' }}
          </h2>
          <p hlmDialogDescription>
            Menschen sehen Name und Beschriftungen; Agenten lesen zusätzlich Key und Beschreibung,
            um Records richtig zu füllen.
          </p>
        </hlm-dialog-header>
        <form novalidate data-testid="property-dialog" (submit)="onSubmit($event)">
          <div hlmFieldGroup class="gap-5">
            @if (conflict()) {
              <div hlmAlert role="status" data-testid="conflict-notice">
                <ng-icon name="lucideInfo" />
                <p hlmAlertDescription>
                  Das Schema wurde inzwischen geändert und neu geladen. Deine Eingaben sind
                  erhalten; prüfe sie und speichere erneut.
                </p>
              </div>
            } @else if (changedElsewhere()) {
              <div hlmAlert role="status" data-testid="changed-notice">
                <ng-icon name="lucideInfo" />
                <p hlmAlertDescription>
                  Diese Property wurde inzwischen von jemand anderem geändert. Beim Speichern wird
                  geprüft, ob deine Änderung noch passt.
                </p>
              </div>
            }
            @if (removedElsewhere()) {
              <div hlmAlert variant="destructive" data-testid="removed-notice">
                <ng-icon name="lucideCircleAlert" />
                <p hlmAlertDescription>
                  Diese Property wurde inzwischen entfernt; Änderungen lassen sich nicht mehr
                  speichern.
                </p>
              </div>
            }
            @if (formError(); as message) {
              <div hlmAlert variant="destructive" data-testid="form-error">
                <ng-icon name="lucideCircleAlert" />
                <p hlmAlertDescription class="whitespace-pre-line">{{ message }}</p>
              </div>
            }

            <div hlmField class="gap-2">
              <label hlmFieldLabel for="property-name">Name</label>
              <input
                hlmInput
                id="property-name"
                type="text"
                autocomplete="off"
                data-testid="property-name"
                [formField]="form.name"
              />
              <div data-testid="name-errors">
                @if (shown(form.name)) {
                  <ag-field-errors [errors]="form.name().errors()" />
                }
              </div>
            </div>

            <div hlmField class="gap-2">
              <label hlmFieldLabel for="property-key">Key</label>
              <input
                hlmInput
                id="property-key"
                type="text"
                autocomplete="off"
                spellcheck="false"
                class="font-mono"
                data-testid="property-key"
                [formField]="form.key"
              />
              @if (isNew()) {
                <p hlmFieldDescription>
                  Kennung in Records, Filtern und für Agenten: Kleinbuchstaben, Ziffern und _, z. B.
                  due_date. Wird aus dem Namen vorgeschlagen und lässt sich später nicht ändern.
                </p>
              } @else {
                @for (reason of form.key().disabledReasons(); track $index) {
                  <p hlmFieldDescription data-testid="key-locked">{{ reason.message }}</p>
                }
              }
              <div data-testid="key-errors">
                @if (shown(form.key)) {
                  <ag-field-errors [errors]="form.key().errors()" />
                }
              </div>
            </div>

            <div hlmField class="gap-2">
              <label hlmFieldLabel for="property-type">Typ</label>
              <hlm-native-select
                selectId="property-type"
                class="w-full"
                data-testid="property-type"
                [formField]="form.type"
              >
                @if (isNew()) {
                  @for (group of typeGroups; track group.label) {
                    <optgroup hlmNativeSelectOptGroup [label]="group.label">
                      @for (type of group.types; track type) {
                        <option hlmNativeSelectOption [value]="type">{{ typeLabel(type) }}</option>
                      }
                    </optgroup>
                  }
                } @else {
                  @for (type of typeChoices(); track type) {
                    <option hlmNativeSelectOption [value]="type">{{ typeLabel(type) }}</option>
                  }
                }
              </hlm-native-select>
              @for (reason of form.type().disabledReasons(); track $index) {
                <p hlmFieldDescription data-testid="type-locked">{{ reason.message }}</p>
              }
              @if (typeHint(); as hint) {
                <p hlmFieldDescription data-testid="type-hint">{{ hint }}</p>
              }
              <div data-testid="type-errors">
                @if (shown(form.type)) {
                  <ag-field-errors [errors]="form.type().errors()" />
                }
              </div>
            </div>

            <div hlmField class="gap-2">
              <label hlmFieldLabel for="property-description">Beschreibung (für Agenten)</label>
              <textarea
                hlmTextarea
                id="property-description"
                rows="3"
                data-testid="property-description"
                [formField]="form.description"
              ></textarea>
              <p hlmFieldDescription>
                Was gehört hinein, in welchem Format, wann wird es gesetzt? Beispiel: „Geschätzter
                Aufwand in Stunden; beim Anlegen grob schätzen.“ Agenten richten sich danach.
              </p>
              <div data-testid="description-errors">
                @if (shown(form.description)) {
                  <ag-field-errors [errors]="form.description().errors()" />
                }
              </div>
            </div>

            <div hlmField class="gap-2">
              <div class="flex items-center gap-2">
                <hlm-checkbox
                  inputId="property-required"
                  data-testid="property-required"
                  [formField]="form.required"
                />
                <label hlmLabel class="font-normal" for="property-required"
                  >Pflicht: jeder Record braucht einen Wert</label
                >
              </div>
              @for (reason of form.required().disabledReasons(); track $index) {
                <p hlmFieldDescription data-testid="required-locked">{{ reason.message }}</p>
              }
              <div data-testid="required-errors">
                @if (shown(form.required)) {
                  <ag-field-errors [errors]="form.required().errors()" />
                }
              </div>
            </div>

            @if (hasOptions(model().type)) {
              <fieldset hlmFieldSet class="gap-3" data-testid="options">
                <legend hlmFieldLegend variant="label" class="mb-0">Optionen</legend>
                <p hlmFieldDescription>
                  Der Wert wird in Records gespeichert und von Agenten gesendet, die Beschriftung
                  sehen Menschen (leer = Wert).
                  @if (hasExistingOptions()) {
                    Werte bestehender Optionen sind unveränderlich. {{ optionValueLocked }}
                  }
                </p>
                <ul class="flex flex-col gap-2" aria-label="Optionen">
                  @for (option of form.options; track option; let i = $index) {
                    <li class="flex flex-wrap items-start gap-2" data-testid="option-row">
                      <div class="flex min-w-28 flex-1 flex-col">
                        <label class="sr-only" [for]="'option-value-' + i"
                          >Wert der Option {{ i + 1 }}</label
                        >
                        <input
                          hlmInput
                          type="text"
                          autocomplete="off"
                          spellcheck="false"
                          class="font-mono"
                          placeholder="Wert"
                          data-testid="option-value"
                          [id]="'option-value-' + i"
                          [formField]="option.value"
                        />
                        @if (shown(option.value)) {
                          <ag-field-errors [errors]="option.value().errors()" />
                        }
                      </div>
                      <div class="flex min-w-28 flex-1 flex-col">
                        <label class="sr-only" [for]="'option-label-' + i"
                          >Beschriftung der Option {{ i + 1 }}</label
                        >
                        <input
                          hlmInput
                          type="text"
                          autocomplete="off"
                          data-testid="option-label"
                          [id]="'option-label-' + i"
                          [placeholder]="option.value().value() || 'Beschriftung'"
                          [formField]="option.label"
                        />
                        @if (shown(option.label)) {
                          <ag-field-errors [errors]="option.label().errors()" />
                        }
                      </div>
                      <div class="flex items-center gap-2">
                        <span
                          aria-hidden="true"
                          class="size-3 shrink-0 rounded-full border"
                          [class]="swatch(option.color().value())"
                        ></span>
                        <label class="sr-only" [for]="'option-color-' + i"
                          >Farbe der Option {{ i + 1 }}</label
                        >
                        <hlm-native-select
                          size="sm"
                          class="w-28"
                          data-testid="option-color"
                          [selectId]="'option-color-' + i"
                          [formField]="option.color"
                        >
                          <option hlmNativeSelectOption value="">Keine Farbe</option>
                          @for (color of colors; track color.value) {
                            <option hlmNativeSelectOption [value]="color.value">
                              {{ color.label }}
                            </option>
                          }
                        </hlm-native-select>
                      </div>
                      @if (model().type === 'status') {
                        <div class="flex flex-col">
                          <label class="sr-only" [for]="'option-group-' + i"
                            >Gruppe der Option {{ i + 1 }}</label
                          >
                          <hlm-native-select
                            size="sm"
                            class="w-32"
                            data-testid="option-group"
                            [selectId]="'option-group-' + i"
                            [formField]="option.group"
                          >
                            <option hlmNativeSelectOption value="">Gruppe …</option>
                            @for (group of statusGroups; track group.value) {
                              <option hlmNativeSelectOption [value]="group.value">
                                {{ group.label }}
                              </option>
                            }
                          </hlm-native-select>
                          @if (shown(option.group)) {
                            <ag-field-errors [errors]="option.group().errors()" />
                          }
                        </div>
                      }
                      <button
                        hlmBtn
                        type="button"
                        variant="ghost"
                        size="icon-sm"
                        data-testid="option-remove"
                        [attr.aria-label]="'Option ' + (i + 1) + ' entfernen'"
                        (click)="removeOption(i)"
                      >
                        <ng-icon name="lucideX" />
                      </button>
                      @if (model().options[i]?.conflict; as hint) {
                        <p
                          class="basis-full text-sm text-muted-foreground"
                          data-testid="option-conflict"
                        >
                          {{ hint }}
                        </p>
                      }
                    </li>
                  }
                </ul>
                <div>
                  <button
                    hlmBtn
                    type="button"
                    variant="outline"
                    size="sm"
                    data-testid="option-add"
                    (click)="addOption()"
                  >
                    <ng-icon name="lucidePlus" />
                    Option hinzufügen
                  </button>
                </div>
                <div data-testid="options-errors">
                  @if (shown(form.options)) {
                    <ag-field-errors [errors]="form.options().errors()" />
                  }
                </div>
              </fieldset>
            }

            @if (model().type === 'number') {
              <div class="grid gap-5 sm:grid-cols-2">
                <div hlmField class="gap-2">
                  <label hlmFieldLabel for="property-unit">Einheit (optional)</label>
                  <input
                    hlmInput
                    id="property-unit"
                    type="text"
                    autocomplete="off"
                    placeholder="z. B. h oder EUR"
                    data-testid="property-unit"
                    [formField]="form.unit"
                  />
                  <div data-testid="unit-errors">
                    @if (shown(form.unit)) {
                      <ag-field-errors [errors]="form.unit().errors()" />
                    }
                  </div>
                </div>
                <div hlmField class="gap-2">
                  <label hlmFieldLabel for="property-precision">Nachkommastellen (optional)</label>
                  <input
                    hlmInput
                    id="property-precision"
                    type="number"
                    step="1"
                    data-testid="property-precision"
                    [formField]="form.precision"
                  />
                  <p hlmFieldDescription>
                    0 = ganze Zahlen, leer = beliebig. Weniger Stellen runden vorhandene Werte.
                  </p>
                  <div data-testid="precision-errors">
                    @if (shown(form.precision)) {
                      <ag-field-errors [errors]="form.precision().errors()" />
                    }
                  </div>
                </div>
              </div>
            }

            @if (model().type === 'relation') {
              <div hlmField class="gap-2">
                <label hlmFieldLabel for="property-target">Ziel-Collection</label>
                <hlm-native-select
                  selectId="property-target"
                  class="w-full"
                  data-testid="property-target"
                  [formField]="form.targetCollectionId"
                >
                  <option hlmNativeSelectOption value="">Bitte wählen …</option>
                  @if (ownCollection(); as own) {
                    <option hlmNativeSelectOption [value]="own.id">
                      Diese Collection ({{ own.name }})
                    </option>
                  }
                  @for (group of targetGroups(); track group.label) {
                    <optgroup hlmNativeSelectOptGroup [label]="group.label">
                      @for (target of group.collections; track target.id) {
                        <option hlmNativeSelectOption [value]="target.id">{{ target.name }}</option>
                      }
                    </optgroup>
                  }
                  @if (unlistedTarget(); as target) {
                    <option hlmNativeSelectOption [value]="target">
                      Archivierte oder unbekannte Collection
                    </option>
                  }
                </hlm-native-select>
                @if (isNew()) {
                  <p hlmFieldDescription>
                    Records dieser Collection verweisen auf Records der Ziel-Collection. Archivierte
                    Collections stehen nicht zur Wahl. Das Ziel lässt sich später nicht ändern.
                  </p>
                } @else {
                  @for (reason of form.targetCollectionId().disabledReasons(); track $index) {
                    <p hlmFieldDescription data-testid="target-locked">{{ reason.message }}</p>
                  }
                }
                <div data-testid="target-errors">
                  @if (shown(form.targetCollectionId)) {
                    <ag-field-errors [errors]="form.targetCollectionId().errors()" />
                  }
                </div>
              </div>

              <div hlmField class="gap-2">
                <div class="flex items-center gap-2">
                  <hlm-checkbox
                    inputId="property-multiple"
                    data-testid="property-multiple"
                    [formField]="form.multiple"
                  />
                  <label hlmLabel class="font-normal" for="property-multiple"
                    >Mehrere Verweise erlauben</label
                  >
                </div>
                @for (reason of form.multiple().disabledReasons(); track $index) {
                  <p hlmFieldDescription data-testid="multiple-locked">{{ reason.message }}</p>
                } @empty {
                  <p hlmFieldDescription>
                    Einmal erlaubt, lässt sich das nicht mehr auf einen Verweis zurücknehmen.
                  </p>
                }
                <div data-testid="multiple-errors">
                  @if (shown(form.multiple)) {
                    <ag-field-errors [errors]="form.multiple().errors()" />
                  }
                </div>
              </div>
            }

            @if (defaultEditable()) {
              <div hlmField class="gap-2" data-testid="default-field">
                @switch (model().type) {
                  @case ('markdown') {
                    <label hlmFieldLabel for="property-default">Standardwert (optional)</label>
                    <textarea
                      hlmTextarea
                      id="property-default"
                      rows="3"
                      data-testid="default-text"
                      [value]="defaultText()"
                      (input)="onDefaultText($any($event.target).value)"
                    ></textarea>
                  }
                  @case ('json') {
                    <label hlmFieldLabel for="property-default"
                      >Standardwert (optional, JSON)</label
                    >
                    <textarea
                      hlmTextarea
                      id="property-default"
                      rows="3"
                      spellcheck="false"
                      class="font-mono"
                      data-testid="default-text"
                      [value]="defaultText()"
                      (input)="onDefaultJson($any($event.target).value)"
                    ></textarea>
                    @if (defaultJsonInvalid()) {
                      <p class="text-sm text-destructive" data-testid="default-json-invalid">
                        Kein gültiges JSON.
                      </p>
                    }
                  }
                  @default {
                    <span hlmFieldLabel>Standardwert (optional)</span>
                    <ag-property-editor
                      data-testid="default-editor"
                      [definition]="defaultDefinition()"
                      [value]="model().default"
                      (valueChange)="setDefault($event)"
                    />
                  }
                }
                <p hlmFieldDescription>
                  Neue Records ohne eigenen Wert bekommen diesen Wert, auch von Agenten.
                </p>
                @if (!isDefaultEmpty()) {
                  <div>
                    <button
                      hlmBtn
                      type="button"
                      variant="ghost"
                      size="sm"
                      data-testid="default-clear"
                      (click)="clearDefault()"
                    >
                      Standardwert entfernen
                    </button>
                  </div>
                }
                <div data-testid="default-errors">
                  @if (shown(defaultField)) {
                    <ag-field-errors [errors]="defaultField().errors()" />
                  }
                </div>
              </div>
            }

            <hlm-dialog-footer>
              <button
                type="button"
                hlmBtn
                variant="outline"
                data-testid="property-cancel"
                [disabled]="form().submitting()"
                (click)="cancelled.emit()"
              >
                Abbrechen
              </button>
              <button
                type="submit"
                hlmBtn
                data-testid="property-submit"
                [disabled]="form().submitting() || removedElsewhere()"
              >
                {{ isNew() ? 'Hinzufügen' : 'Speichern' }}
              </button>
            </hlm-dialog-footer>
          </div>
        </form>
      </hlm-dialog-content>
    </hlm-dialog>

    <ag-confirm-dialog
      [open]="pending() !== null"
      title="Gespeicherte Werte gehen verloren"
      [message]="confirmMessage()"
      confirmLabel="Trotzdem speichern"
      [busy]="form().submitting()"
      (confirmed)="onConfirmed()"
      (cancelled)="pending.set(null)"
    />
  `,
})
export class PropertyDialogComponent {
  private readonly store = inject(SchemaEditorStore);
  private readonly tree = inject(WorkspaceTreeStore);
  private readonly injector = inject(Injector);
  private readonly document = inject(DOCUMENT);

  /** The property as it was when the dialog opened; `null` adds a new property. */
  readonly original = input<PropertyDefinition | null>(null);
  /** Schema version the dialog works against (sent as `expectedSchemaVersion`). */
  readonly baseVersion = input.required<number>();

  /** Emits a status message for the page after a successful change. */
  readonly saved = output<string>();
  readonly cancelled = output();

  protected readonly typeGroups = PROPERTY_TYPE_GROUPS;
  protected readonly colors = OPTION_COLORS;
  protected readonly statusGroups = STATUS_GROUPS;
  protected readonly optionValueLocked = OPTION_VALUE_LOCKED;
  protected readonly hasOptions = hasOptions;
  protected readonly typeLabel = propertyTypeLabel;
  protected readonly swatch = (color: string) => optionSwatch(color) ?? 'bg-transparent';

  protected readonly isNew = computed(() => this.original() === null);
  /** The property the draft is based on: `original`, after a 409 the reloaded one. */
  protected readonly base = linkedSignal(() => this.original());
  /** The version the next save expects: `baseVersion`, after a 409 the reloaded one. */
  private readonly version = linkedSignal(() => this.baseVersion());

  /** The edited property as the schema has it now (`undefined`: removed meanwhile). */
  private readonly current = computed(() => {
    const base = this.base();
    return base ? this.store.find(base.key) : undefined;
  });
  protected readonly removedElsewhere = computed(
    () => !this.isNew() && this.store.collection() !== undefined && this.current() === undefined,
  );
  protected readonly changedElsewhere = computed(() => {
    const current = this.current();
    return !!current && JSON.stringify(current) !== JSON.stringify(this.base());
  });

  /** True after a 409: the schema was reloaded under the open dialog. */
  protected readonly conflict = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly pending = signal<PendingConfirm | null>(null);

  protected readonly model = linkedSignal<PropertyDefinition | null, PropertyDraft>({
    source: () => this.original(),
    computation: (original) => (original ? draftOf(original) : emptyDraft()),
  });

  private readonly isTitle = computed(() => this.original()?.key === TITLE_KEY);

  protected readonly form = form(this.model, (path) => {
    disabled(path.key, () => (this.isNew() ? false : KEY_LOCKED));
    required(path.key, { message: 'Bitte einen Key angeben.' });
    pattern(path.key, KEY_PATTERN, {
      message:
        'Nur Kleinbuchstaben, Ziffern und _, beginnend mit einem Buchstaben, höchstens 63 Zeichen (z. B. due_date).',
    });
    validate(path.key, ({ value }) => {
      const key = value();
      if (!this.isNew()) {
        return undefined;
      }
      if (RESERVED_KEYS.includes(key)) {
        return {
          kind: 'reserved',
          message: `„${key}“ ist reserviert (id, collection, space, revision, body, title). Bitte einen anderen Key wählen.`,
        };
      }
      return this.store.find(key)
        ? { kind: 'taken', message: 'Diesen Key gibt es in der Collection schon.' }
        : undefined;
    });

    required(path.name, { message: 'Bitte einen Namen angeben.' });
    maxLength(path.name, NAME_MAX_LENGTH, {
      message: `Der Name darf höchstens ${NAME_MAX_LENGTH} Zeichen haben.`,
    });
    maxLength(path.description, DESCRIPTION_MAX_LENGTH, {
      message: `Die Beschreibung darf höchstens ${DESCRIPTION_MAX_LENGTH} Zeichen haben.`,
    });

    disabled(path.type, () => this.typeLock());
    disabled(path.required, ({ valueOf }) =>
      this.isTitle()
        ? 'Der Titel ist immer Pflicht.'
        : isComputed(valueOf(path.type))
          ? 'Berechnete Properties füllt das System; sie können nicht Pflicht sein.'
          : false,
    );

    hidden(path.options, ({ valueOf }) => !hasOptions(valueOf(path.type)));
    validate(path.options, ({ value, valueOf }) =>
      valueOf(path.required) && value().length === 0
        ? {
            kind: 'required',
            message: 'Eine Pflicht-Auswahl braucht mindestens eine Option.',
          }
        : undefined,
    );
    applyEach(path.options, (option) => {
      disabled(option.value, ({ valueOf }) =>
        valueOf(option.existing) ? OPTION_VALUE_LOCKED : false,
      );
      required(option.value, { message: 'Bitte einen Wert angeben.' });
      maxLength(option.value, OPTION_VALUE_MAX_LENGTH, {
        message: `Höchstens ${OPTION_VALUE_MAX_LENGTH} Zeichen.`,
      });
      maxLength(option.label, OPTION_LABEL_MAX_LENGTH, {
        message: `Höchstens ${OPTION_LABEL_MAX_LENGTH} Zeichen.`,
      });
      validate(option.value, ({ value, valueOf }) => {
        const own = value().trim();
        const same = valueOf(path.options).filter((o) => o.value.trim() === own).length;
        return own !== '' && same > 1
          ? { kind: 'duplicate', message: 'Diesen Wert gibt es schon.' }
          : undefined;
      });
      required(option.group, {
        message: 'Bitte eine Gruppe wählen.',
        when: ({ valueOf }) => valueOf(path.type) === 'status',
      });
    });

    hidden(path.unit, ({ valueOf }) => valueOf(path.type) !== 'number');
    maxLength(path.unit, UNIT_MAX_LENGTH, {
      message: `Die Einheit darf höchstens ${UNIT_MAX_LENGTH} Zeichen haben.`,
    });
    hidden(path.precision, ({ valueOf }) => valueOf(path.type) !== 'number');
    min(path.precision, 0, { message: 'Mindestens 0.' });
    max(path.precision, MAX_PRECISION, { message: `Höchstens ${MAX_PRECISION}.` });
    validate(path.precision, ({ value }) => {
      const precision = value();
      if (precision !== null && !Number.isInteger(precision)) {
        return { kind: 'integer', message: 'Bitte eine ganze Zahl angeben.' };
      }
      return precision === null && this.base()?.precision != null
        ? {
            kind: 'required',
            message: 'Die Nachkommastellen lassen sich ändern, aber nicht mehr entfernen.',
          }
        : undefined;
    });

    hidden(path.targetCollectionId, ({ valueOf }) => valueOf(path.type) !== 'relation');
    disabled(path.targetCollectionId, () => (this.isNew() ? false : TARGET_LOCKED));
    required(path.targetCollectionId, { message: 'Bitte die Ziel-Collection wählen.' });
    hidden(path.multiple, ({ valueOf }) => valueOf(path.type) !== 'relation');
    disabled(path.multiple, () => (this.base()?.multiple ? MULTIPLE_LOCKED : false));
  });

  /** Types offered when editing: the current one plus the allowed changes. */
  protected readonly typeChoices = computed(() => {
    const base = this.base();
    return base ? allowedTypes(base.type) : [];
  });

  private readonly typeLock = computed<string | false>(() => {
    if (this.isNew()) {
      return false;
    }
    if (this.isTitle()) {
      return 'Der Titel ist immer vom Typ Text.';
    }
    return this.typeChoices().length > 1
      ? false
      : 'Der Typ lässt sich nicht ändern: gespeicherte Werte würden nicht passen. Erlaubt sind nur Text ↔ Markdown und Auswahl → Mehrfachauswahl; sonst eine neue Property anlegen.';
  });

  protected readonly typeHint = computed(() => {
    const base = this.base();
    const type = this.model().type;
    if (!base || type === base.type) {
      return null;
    }
    if (base.type === 'markdown' && type === 'text') {
      return 'Markdown → Text: lange Werte werden gekürzt; das fragt beim Speichern nach.';
    }
    if (base.type === 'select' && type === 'multi_select') {
      return 'Auswahl → Mehrfachauswahl lässt sich nicht zurücknehmen; vorhandene Werte bleiben erhalten.';
    }
    return null;
  });

  protected readonly hasExistingOptions = computed(() =>
    this.model().options.some((o) => o.existing),
  );

  // ---- relation target -------------------------------------------------------------------

  private readonly ownId = computed(() => this.store.collection()?.id ?? null);
  protected readonly ownCollection = computed(() => this.store.collection() ?? null);
  /** Active collections of the workspace by space, without this one (offered first). */
  protected readonly targetGroups = computed(() =>
    this.tree
      .tree()
      .map((node) => ({
        label: node.space.name,
        collections: node.collections.filter((c) => c.id !== this.ownId() && !c.archivedAt),
      }))
      .filter((group) => group.collections.length > 0),
  );
  /** The stored target when it is not offered (archived or unknown); only when editing. */
  protected readonly unlistedTarget = computed(() => {
    const target = this.model().targetCollectionId;
    if (!target || target === this.ownId()) {
      return null;
    }
    return this.targetGroups().some((g) => g.collections.some((c) => c.id === target))
      ? null
      : target;
  });

  // ---- default ---------------------------------------------------------------------------

  protected readonly defaultEditable = computed(
    () => !this.isTitle() && allowsDefault(this.model().type),
  );
  protected readonly isDefaultEmpty = computed(() => isEmptyValue(this.model().default));
  /** The draft as a definition, so the shared property editor can edit the default. */
  protected readonly defaultDefinition = computed<PropertyDefinition>(() => {
    const draft = this.model();
    return {
      key: 'default',
      name: 'Standardwert',
      type: draft.type,
      description: null,
      required: false,
      readOnly: false,
      options: hasOptions(draft.type)
        ? (draftOptions(draft).filter((o) => o.value !== '') as PropertyDefinition['options'])
        : null,
      targetCollectionId: null,
      multiple: false,
      default: null,
      unit: draft.type === 'number' ? draft.unit : null,
      precision: draft.type === 'number' ? draft.precision : null,
    };
  });
  /** Text of the markdown/json default editor; follows the model except while typing JSON. */
  protected readonly defaultText = linkedSignal(() => {
    const value = this.model().default;
    if (value == null) {
      return '';
    }
    return typeof value === 'string' && this.model().type !== 'json'
      ? value
      : JSON.stringify(value, null, 2);
  });
  protected readonly defaultJsonInvalid = signal(false);

  // ---- confirmation ----------------------------------------------------------------------

  protected readonly confirmMessage = computed(() => {
    const pending = this.pending();
    if (!pending) {
      return '';
    }
    if (pending.losses.length === 0) {
      return 'Diese Änderung entfernt gespeicherte Werte aus Records.';
    }
    return pending.losses.map((loss, i) => describeLoss(loss, pending.counts[i])).join(' ');
  });
  /** Grows with every confirmation, so late counts of an earlier one are dropped. */
  private confirmGeneration = 0;

  constructor() {
    // Propose the key from the name until the user writes one of their own.
    let suggested = '';
    const name = computed(() => this.model().name);
    effect(() => {
      const value = name();
      untracked(() => {
        if (!this.isNew()) {
          return;
        }
        const key = this.model().key;
        if (key === '' || key === suggested) {
          suggested = suggestKey(
            value,
            this.store.schema().map((p) => p.key),
          );
          this.model.update((m) => ({ ...m, key: suggested }));
        }
      });
    });

    // A new type starts without a default; status options need a group.
    const type = computed(() => this.model().type);
    let previousType: PropertyType | null = null;
    effect(() => {
      const next = type();
      untracked(() => {
        if (previousType !== null && previousType !== next && this.isNew()) {
          this.model.update((m) => ({
            ...m,
            default: null,
            options:
              next === 'status'
                ? m.options.map((o) => (o.group ? o : { ...o, group: 'todo' }))
                : m.options,
          }));
          this.defaultJsonInvalid.set(false);
        }
        previousType = next;
      });
    });
  }

  /** The default is any JSON value; its field is always there (`null` = no default). */
  protected get defaultField(): FieldTree<unknown> {
    return this.form.default as FieldTree<unknown>;
  }

  protected shown(field: FieldTree<unknown>): boolean {
    return field().touched() && field().invalid();
  }

  protected addOption(): void {
    const index = this.model().options.length;
    this.model.update((m) => ({ ...m, options: [...m.options, newOption(m.type)] }));
    // keyboard users continue in the new row
    afterNextRender(() => this.document.getElementById(`option-value-${index}`)?.focus(), {
      injector: this.injector,
    });
  }

  protected removeOption(index: number): void {
    this.model.update((m) => ({ ...m, options: m.options.filter((_, i) => i !== index) }));
  }

  protected setDefault(value: PropertyValue): void {
    this.model.update((m) => ({ ...m, default: value ?? null }));
  }

  protected clearDefault(): void {
    this.defaultJsonInvalid.set(false);
    this.model.update((m) => ({ ...m, default: null }));
  }

  protected onDefaultText(text: string): void {
    this.model.update((m) => ({ ...m, default: text === '' ? null : text }));
  }

  protected onDefaultJson(text: string): void {
    this.defaultText.set(text);
    if (text.trim() === '') {
      this.defaultJsonInvalid.set(false);
      this.model.update((m) => ({ ...m, default: null }));
      return;
    }
    try {
      const value: unknown = JSON.parse(text);
      this.defaultJsonInvalid.set(false);
      this.model.update((m) => ({ ...m, default: value }));
    } catch {
      this.defaultJsonInvalid.set(true);
    }
  }

  /** Brain closed the dialog (Escape): the parent removes the component. */
  protected onStateChanged(state: BrnDialogState): void {
    // While the PATCH runs, Escape must not look like a cancel: the change may still be applied.
    if (state === 'closed' && !this.form().submitting()) {
      this.cancelled.emit();
    }
  }

  protected onConfirmed(): void {
    void this.save(true);
  }

  protected onSubmit(event: Event): void {
    event.preventDefault();
    void this.save(false);
  }

  private async save(confirmed: boolean): Promise<void> {
    this.formError.set(null);
    if (this.removedElsewhere()) {
      return;
    }
    if (this.defaultEditable() && this.model().type === 'json' && this.defaultJsonInvalid()) {
      this.formError.set('Der Standardwert ist kein gültiges JSON.');
      return;
    }
    const entry = this.isNew() ? 'add[0]' : 'update[0]';

    await submit(this.form, async (fields) => {
      const draft = fields().value();
      const base = this.base();
      let body: UpdateSchemaBody;
      let losses: Loss[] = [];
      if (base === null) {
        body = { add: [toDefinitionInput(draft)] };
      } else {
        const change = toChangeInput(base, draft);
        if (change === null) {
          this.pending.set(null);
          this.cancelled.emit(); // nothing changed: nothing to save
          return undefined;
        }
        losses = lossesOf(this.current() ?? base, change);
        body = { update: [change] };
      }
      if (losses.length > 0 && !confirmed) {
        this.askConfirmation(losses);
        return undefined;
      }
      try {
        await this.store.update({
          ...body,
          expectedSchemaVersion: this.version(),
          ...(confirmed ? { confirmRemoval: true } : {}),
        });
      } catch (error) {
        this.pending.set(null);
        const apiError = ApiError.from(error);
        if (apiError.code === SCHEMA_VERSION_CONFLICT) {
          await this.adoptReloadedSchema();
          return undefined;
        }
        if (!confirmed && apiError.errors.some((e) => e.code === REMOVAL_NOT_CONFIRMED)) {
          // the server knows of a loss the client did not see (e.g. schema changed meanwhile)
          this.askConfirmation(losses);
          return undefined;
        }
        const { fieldErrors, formMessage } = mapSchemaErrors(apiError, entry, fields);
        this.formError.set(formMessage);
        return fieldErrors;
      }
      this.pending.set(null);
      const name = draft.name.trim();
      this.saved.emit(
        base === null ? `Property „${name}“ hinzugefügt.` : `Property „${name}“ gespeichert.`,
      );
      return undefined;
    });
  }

  /** Opens the confirmation and counts the affected records per loss in the background. */
  private askConfirmation(losses: readonly Loss[]): void {
    const generation = ++this.confirmGeneration;
    this.pending.set({ losses, counts: losses.map(() => undefined) });
    losses.forEach((loss, i) => {
      void this.store.countAffected(loss).then((count) => {
        const pending = this.pending();
        if (pending && generation === this.confirmGeneration) {
          this.pending.set({
            ...pending,
            counts: pending.counts.map((c, j) => (j === i ? count : c)),
          });
        }
      });
    });
  }

  /** 409: load the schema again, keep the input and work against the new version from now on. */
  private async adoptReloadedSchema(): Promise<void> {
    if (!(await this.store.refresh())) {
      this.formError.set(
        'Das Schema wurde inzwischen geändert, konnte aber nicht neu geladen werden. Bitte gleich erneut speichern.',
      );
      return;
    }
    const base = this.base();
    const current = this.current();
    if (base && current) {
      this.model.set(rebaseDraft(this.model(), base, current));
      this.base.set(current);
    }
    this.version.set(this.store.schemaVersion());
    this.conflict.set(true);
  }
}
