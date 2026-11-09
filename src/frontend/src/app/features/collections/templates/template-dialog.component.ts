import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  linkedSignal,
  output,
  signal,
  untracked,
  viewChild,
  viewChildren,
} from '@angular/core';
import { FieldTree, FormField, form, maxLength, required, submit } from '@angular/forms/signals';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCircleAlert, lucideInfo } from '@ng-icons/lucide';
import type { BrnDialogState } from '@spartan-ng/brain/dialog';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCheckbox } from '@spartan-ng/helm/checkbox';
import { HlmDialogImports } from '@spartan-ng/helm/dialog';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmLabel } from '@spartan-ng/helm/label';
import { HlmTextarea } from '@spartan-ng/helm/textarea';
import { SERVER_ERROR_KIND, ServerValidationError } from '../../../core/api/field-errors';
import { MarkdownEditorComponent } from '../../../shared/editor/markdown-editor.component';
import { MARKDOWN_EDITOR_LIMITS } from '../../../shared/editor/markdown-editor.adapter';
import { exceedsMarkdownLimits } from '../../../shared/markdown/markdown-limits';
import { PropertyEditorComponent } from '../../../shared/properties/property-editor.component';
import type { PropertyValue } from '../../../shared/properties/property-types';
import { ConfirmDialogComponent } from '../../../shared/ui/confirm-dialog.component';
import { FieldErrorsComponent } from '../../../shared/ui/field-errors.component';
import {
  EMPTY_SEED,
  Preset,
  TEMPLATE_BODY_MAX_LENGTH,
  TEMPLATE_DESCRIPTION_MAX_LENGTH,
  TEMPLATE_NAME_MAX_LENGTH,
  TemplateDto,
  TemplateSeed,
  cleanPreset,
  presetOfKeys,
  isEmptyValue,
  presettable,
  seedOf,
  toCreateBody,
  toUpdateBody,
  withPresetValue,
} from './template-draft';
import { mapTemplateErrors } from './template-errors';
import { TemplatesStore } from './templates.store';

interface Model {
  name: string;
  description: string;
  isDefault: boolean;
}

/**
 * Modal for one record template: with `original` it edits that template (`PATCH`, only the
 * changed fields), without it creates a new one (`POST`), optionally from a `seed` (a record
 * taken over as a template). Lives below the templates page and uses its {@link TemplatesStore}.
 *
 * - The preset uses the shared property editors; clearing a field (or "Leeren") removes its
 *   preset. Computed properties and the title cannot be preset.
 * - The body uses the Markdown editor; a text that is too large or complex for it (or on which
 *   it fails) is edited as plain text instead, and nothing is lost on the way.
 * - Field errors of the API land at their field; `propertiesPreset.<key>` at that property.
 * - Templates carry no concurrency token (last change wins). When the template changed on the
 *   server since the dialog opened, saving still goes through and says so afterwards.
 *
 * Emits `saved` with a status message on success; the parent removes the component on `saved`
 * and `cancelled`.
 */
@Component({
  selector: 'ag-template-dialog',
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
    HlmTextarea,
    MarkdownEditorComponent,
    PropertyEditorComponent,
  ],
  providers: [provideIcons({ lucideCircleAlert, lucideInfo })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-dialog
      state="open"
      [closeOnOutsidePointerEvents]="false"
      [disableClose]="true"
      (stateChanged)="onStateChanged($event)"
    >
      <hlm-dialog-content
        *hlmDialogPortal
        class="max-h-[calc(100svh-2rem)] overflow-y-auto sm:max-w-3xl"
        [showCloseButton]="false"
        (keydown.escape)="requestCancel()"
      >
        <hlm-dialog-header>
          <h2 hlmDialogTitle>
            {{ isNew() ? 'Vorlage anlegen' : 'Vorlage „' + original()?.name + '“ bearbeiten' }}
          </h2>
          <p hlmDialogDescription>
            Neue Records dieser Collection starten mit den vorbelegten Werten und dem Text der
            Vorlage. Agenten sehen Name und Beschreibung, um die passende Vorlage zu wählen.
          </p>
        </hlm-dialog-header>
        <form novalidate data-testid="template-dialog" (submit)="onSubmit($event)">
          <div hlmFieldGroup class="gap-5">
            @if (removedElsewhere()) {
              <div hlmAlert variant="destructive" data-testid="removed-notice">
                <ng-icon name="lucideCircleAlert" />
                <p hlmAlertDescription>
                  Diese Vorlage wurde inzwischen gelöscht; Änderungen lassen sich nicht mehr
                  speichern.
                </p>
              </div>
            } @else if (changedElsewhere()) {
              <div hlmAlert role="status" data-testid="changed-notice">
                <ng-icon name="lucideInfo" />
                <p hlmAlertDescription>
                  Diese Vorlage wurde inzwischen von jemand anderem geändert. Vorlagen haben keine
                  Versionsprüfung: Speichern überschreibt die Änderung.
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
              <label hlmFieldLabel for="template-name">Name</label>
              <input
                hlmInput
                id="template-name"
                type="text"
                autocomplete="off"
                data-testid="template-name"
                [formField]="form.name"
              />
              <div data-testid="name-errors">
                @if (shown(form.name)) {
                  <ag-field-errors [errors]="form.name().errors()" />
                }
              </div>
            </div>

            <div hlmField class="gap-2">
              <label hlmFieldLabel for="template-description">Beschreibung (für Agenten)</label>
              <textarea
                hlmTextarea
                id="template-description"
                rows="2"
                data-testid="template-description"
                [formField]="form.description"
              ></textarea>
              <p hlmFieldDescription>
                Wann ist diese Vorlage die richtige? Beispiel: „Für Entscheidungen mit Folgen für
                mehrere Teams.“ Agenten wählen anhand von Name und Beschreibung.
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
                  inputId="template-default"
                  data-testid="template-default"
                  [formField]="form.isDefault"
                />
                <label hlmLabel class="font-normal" for="template-default"
                  >Standardvorlage: neue Records ohne Vorlagenwahl nutzen sie</label
                >
              </div>
              <p hlmFieldDescription>
                Es gibt genau eine Standardvorlage je Collection; eine neue löst die bisherige ab.
              </p>
              <div data-testid="default-errors">
                @if (shown(form.isDefault)) {
                  <ag-field-errors [errors]="form.isDefault().errors()" />
                }
              </div>
            </div>

            <fieldset hlmFieldSet class="gap-3" data-testid="preset">
              <legend hlmFieldLegend variant="label" class="mb-0">Vorbelegte Werte</legend>
              <p hlmFieldDescription>
                Nur Properties mit Wert werden vorbelegt. „Leeren“ entfernt die Vorbelegung.
                Berechnete Properties lassen sich nicht vorbelegen. Ein vorbelegter Titel gilt für
                Records, die ohne Titel angelegt werden (z. B. von Agenten).
              </p>
              @for (definition of definitions(); track definition.key) {
                <div
                  class="grid gap-1"
                  role="group"
                  data-testid="preset-row"
                  [attr.data-key]="definition.key"
                  [attr.aria-labelledby]="'preset-label-' + definition.key"
                >
                  <div class="flex items-center justify-between gap-2">
                    <span
                      class="text-sm font-medium"
                      [id]="'preset-label-' + definition.key"
                      [attr.title]="definition.description"
                      >{{ definition.name }}</span
                    >
                    @if (hasValue(definition.key)) {
                      <button
                        type="button"
                        hlmBtn
                        variant="ghost"
                        size="xs"
                        data-testid="preset-clear"
                        [attr.aria-label]="'Vorbelegung von ' + definition.name + ' leeren'"
                        (click)="setValue(definition.key, null)"
                      >
                        Leeren
                      </button>
                    }
                  </div>
                  <ag-property-editor
                    [definition]="definition"
                    [value]="preset()[definition.key]"
                    [chipLinks]="false"
                    [errors]="presetErrors()[definition.key] ?? []"
                    (valueChange)="setValue(definition.key, $event)"
                  />
                </div>
              } @empty {
                <p class="text-sm text-muted-foreground" data-testid="preset-empty">
                  Das Schema hat keine vorbelegbaren Properties.
                </p>
              }
            </fieldset>

            <div hlmField class="gap-2">
              <span hlmFieldLabel id="template-body-label">Text (Markdown)</span>
              @if (plainBody()) {
                <p hlmFieldDescription data-testid="body-plain-notice">
                  Dieser Text ist zu groß oder zu komplex für den Editor und wird als reiner Text
                  bearbeitet.
                </p>
                <textarea
                  hlmTextarea
                  rows="12"
                  class="font-mono"
                  aria-labelledby="template-body-label"
                  data-testid="template-body-text"
                  [value]="body()"
                  (input)="body.set($any($event.target).value)"
                ></textarea>
              } @else {
                <ag-markdown-editor
                  class="block min-h-40 rounded-md border border-border"
                  role="group"
                  aria-labelledby="template-body-label"
                  data-testid="template-body"
                  [value]="body()"
                  (valueChange)="body.set($event)"
                />
              }
              @if (bodyLengthError(); as message) {
                <p class="text-sm text-destructive" role="alert" data-testid="body-length-error">
                  {{ message }}
                </p>
              }
              <div data-testid="body-errors">
                @for (message of bodyErrors(); track $index) {
                  <p class="mt-1.5 text-sm text-destructive">{{ message }}</p>
                }
              </div>
            </div>

            <hlm-dialog-footer>
              <button
                type="button"
                hlmBtn
                variant="outline"
                data-testid="template-cancel"
                (click)="requestCancel()"
              >
                Abbrechen
              </button>
              <button
                type="submit"
                hlmBtn
                data-testid="template-submit"
                [disabled]="form().submitting() || removedElsewhere()"
              >
                {{ isNew() ? 'Anlegen' : 'Speichern' }}
              </button>
            </hlm-dialog-footer>
          </div>
        </form>
      </hlm-dialog-content>
    </hlm-dialog>

    <ag-confirm-dialog
      [open]="discarding()"
      title="Änderungen verwerfen?"
      message="Deine Eingaben in dieser Vorlage gehen verloren."
      confirmLabel="Verwerfen"
      (confirmed)="cancelled.emit()"
      (cancelled)="discarding.set(false)"
    />
  `,
})
export class TemplateDialogComponent {
  private readonly store = inject(TemplatesStore);
  private readonly limits = inject(MARKDOWN_EDITOR_LIMITS);

  /** The template to edit; `null` creates a new one. */
  readonly original = input<TemplateDto | null>(null);
  /** Start values of a new template (e.g. from a record); ignored when editing. */
  readonly seed = input<TemplateSeed | null>(null);

  /** Emits a status message for the page after a successful save. */
  readonly saved = output<string>();
  readonly cancelled = output();

  protected readonly nameMax = TEMPLATE_NAME_MAX_LENGTH;
  protected readonly isNew = computed(() => this.original() === null);

  /** What the dialog was opened with: the comparison for "only changes" and "changed elsewhere". */
  private readonly base = computed<TemplateSeed>(() => {
    const original = this.original();
    return original ? seedOf(original) : (this.seed() ?? EMPTY_SEED);
  });

  protected readonly model = linkedSignal<TemplateSeed, Model>({
    source: this.base,
    computation: (base) => ({
      name: base.name,
      description: base.description,
      isDefault: base.isDefault,
    }),
  });
  protected readonly preset = linkedSignal<TemplateSeed, Preset>({
    source: this.base,
    computation: (base) => ({ ...base.preset }),
  });
  protected readonly body = linkedSignal<TemplateSeed, string>({
    source: this.base,
    computation: (base) => base.body,
  });
  /** The body is edited as plain text: too large or complex for the editor, or it failed on it. */
  protected readonly plainBody = linkedSignal<TemplateSeed, boolean>({
    source: this.base,
    computation: (base) => exceedsMarkdownLimits(base.body, this.limits),
  });

  protected readonly form = form(this.model, (path) => {
    required(path.name, { message: 'Bitte einen Namen angeben.' });
    maxLength(path.name, TEMPLATE_NAME_MAX_LENGTH, {
      message: `Der Name darf höchstens ${TEMPLATE_NAME_MAX_LENGTH} Zeichen haben.`,
    });
    maxLength(path.description, TEMPLATE_DESCRIPTION_MAX_LENGTH, {
      message: `Die Beschreibung darf höchstens ${TEMPLATE_DESCRIPTION_MAX_LENGTH} Zeichen haben.`,
    });
  });

  protected readonly formError = signal<string | null>(null);
  protected readonly presetErrors = signal<Record<string, readonly string[]>>({});
  protected readonly bodyErrors = signal<readonly string[]>([]);

  protected readonly definitions = computed(() => presettable(this.store.schema()));

  /** The edited template as the server list has it now (`undefined`: gone, or not loaded). */
  private readonly current = computed(() => {
    const original = this.original();
    return original ? this.store.templates()?.find((t) => t.id === original.id) : undefined;
  });
  protected readonly removedElsewhere = computed(
    () => !this.isNew() && this.store.templates() !== undefined && this.current() === undefined,
  );
  /** Property keys of the current schema; presets are compared and sent limited to them. */
  private readonly knownKeys = computed(() => new Set(this.store.schema().map((p) => p.key)));
  /**
   * Only differences the user could have made count: the server cleans presets of removed
   * properties by itself, which must not read as "changed by someone else".
   */
  private comparable(template: TemplateDto): string {
    return JSON.stringify({
      ...template,
      propertiesPreset: presetOfKeys(template.propertiesPreset as Preset, this.knownKeys()),
    });
  }
  protected readonly changedElsewhere = computed(() => {
    const current = this.current();
    const original = this.original();
    return !!current && !!original && this.comparable(current) !== this.comparable(original);
  });

  /** {@link base} limited to the properties the schema still has, like the draft. */
  private readonly baseKnown = computed<TemplateSeed>(() => ({
    ...this.base(),
    preset: presetOfKeys(this.base().preset, this.knownKeys()),
  }));

  protected readonly bodyLengthError = computed(() => {
    const length = this.body().length;
    return length > TEMPLATE_BODY_MAX_LENGTH
      ? `Der Text hat ${length.toLocaleString('de-DE')} Zeichen; erlaubt sind höchstens ${TEMPLATE_BODY_MAX_LENGTH.toLocaleString('de-DE')}. Bitte kürzen, sonst lässt sich die Vorlage nicht speichern.`
      : null;
  });

  /** Asking "discard changes?" before the dialog closes. */
  protected readonly discarding = signal(false);

  private readonly editor = viewChild(MarkdownEditorComponent);
  private readonly propertyEditors = viewChildren(PropertyEditorComponent);

  constructor() {
    // The editor fell back to plain text for this body: continue with a text area, same text.
    effect(() => {
      const editor = this.editor();
      if (editor?.fallback() || editor?.loadFailed()) {
        untracked(() => this.plainBody.set(true));
      }
    });
  }

  /** Cancel button and Escape: asks first when the draft differs from what was opened. */
  protected requestCancel(): void {
    if (this.discarding()) {
      return;
    }
    if (this.isDirty()) {
      this.discarding.set(true);
    } else {
      this.cancelled.emit();
    }
  }

  /** Whether the user changed anything since the dialog opened (pending editor input counts). */
  isDirty(): boolean {
    this.editor()?.flush();
    for (const editor of this.propertyEditors()) {
      editor.flush();
    }
    return toUpdateBody(this.baseKnown(), this.draft()) !== null;
  }

  /** The draft as it would be saved: preset limited to the current schema, without empty entries. */
  private draft(): TemplateSeed {
    const value = this.model();
    return {
      name: value.name,
      description: value.description,
      isDefault: value.isDefault,
      preset: cleanPreset(presetOfKeys(this.preset(), this.knownKeys())),
      body: this.body(),
    };
  }

  /** Brain closed the dialog (not reachable by Escape any more): the parent removes the component. */
  protected onStateChanged(state: BrnDialogState): void {
    if (state === 'closed') {
      this.cancelled.emit();
    }
  }

  protected shown(field: () => { touched(): boolean; invalid(): boolean }): boolean {
    const state = field();
    return state.touched() && state.invalid();
  }

  protected hasValue(key: string): boolean {
    return !isEmptyValue(this.preset()[key]);
  }

  protected setValue(key: string, value: PropertyValue): void {
    this.preset.update((preset) => withPresetValue(preset, key, value));
    this.presetErrors.update((errors) =>
      Object.fromEntries(Object.entries(errors).filter(([k]) => k !== key)),
    );
  }

  protected onSubmit(event: Event): void {
    event.preventDefault();
    void this.save();
  }

  private async save(): Promise<void> {
    this.formError.set(null);
    this.presetErrors.set({});
    this.bodyErrors.set([]);
    if (this.removedElsewhere()) {
      return;
    }
    // pending (debounced) edits of the Markdown editors reach the draft first
    this.editor()?.flush();
    for (const editor of this.propertyEditors()) {
      editor.flush();
    }
    if (this.bodyLengthError()) {
      return;
    }
    if (this.propertyEditors().some((e) => e.invalid())) {
      this.formError.set(
        'Ein vorbelegtes Feld enthält eine unvollständige oder ungültige Eingabe (z. B. JSON, Zahl oder Datum). Bitte korrigieren oder leeren.',
      );
      return;
    }

    await submit(this.form, async (fields) => {
      // a property removed from the schema meanwhile cannot be preset any more
      const draft = this.draft();
      const original = this.original();
      let overwritten = false;
      try {
        if (original === null) {
          await this.store.create(toCreateBody(draft));
        } else {
          const change = toUpdateBody(this.baseKnown(), draft);
          if (change === null) {
            this.cancelled.emit(); // nothing changed: nothing to save
            return undefined;
          }
          overwritten = await this.changedOnServer(original);
          await this.store.update(original.id, change);
        }
      } catch (error) {
        return this.showErrors(error, fields);
      }
      const name = draft.name.trim();
      this.saved.emit(
        overwritten
          ? `Vorlage „${name}“ gespeichert. Hinweis: Sie war inzwischen von jemand anderem geändert worden; deren Änderungen sind überschrieben.`
          : `Vorlage „${name}“ ${original ? 'gespeichert' : 'angelegt'}.`,
      );
      return undefined;
    });
  }

  /** Compares with a fresh copy from the server; informative only, a failure counts as "no". */
  private async changedOnServer(original: TemplateDto): Promise<boolean> {
    try {
      const fresh = (await this.store.fetchFresh()).find((t) => t.id === original.id);
      return !!fresh && this.comparable(fresh) !== this.comparable(original);
    } catch {
      return false;
    }
  }

  /** Puts each error where it belongs; returns the ones for form fields. */
  private showErrors(error: unknown, fields: FieldTree<Model>): ServerValidationError[] {
    const fieldErrors: ServerValidationError[] = [];
    const formMessages: string[] = [];
    const presetErrors: Record<string, string[]> = {};
    const bodyErrors: string[] = [];
    const shownKeys = new Set(this.definitions().map((d) => d.key));
    const fieldTrees: Record<string, FieldTree<unknown>> = {
      name: fields.name,
      description: fields.description,
      isDefault: fields.isDefault,
    };
    for (const e of mapTemplateErrors(error)) {
      if (e.kind === 'preset' && shownKeys.has(e.target)) {
        (presetErrors[e.target] ??= []).push(e.message);
      } else if (e.kind === 'preset') {
        formMessages.push(`${e.target}: ${e.message}`);
      } else if (e.kind === 'field' && e.target === 'bodyMarkdown') {
        bodyErrors.push(e.message);
      } else if (e.kind === 'field') {
        fieldErrors.push({
          kind: SERVER_ERROR_KIND,
          code: 'server',
          message: e.message,
          fieldTree: fieldTrees[e.target],
        });
      } else {
        formMessages.push(e.message);
      }
    }
    this.presetErrors.set(presetErrors);
    this.bodyErrors.set(bodyErrors);
    this.formError.set(formMessages.length > 0 ? formMessages.join('\n') : null);
    return fieldErrors;
  }
}
