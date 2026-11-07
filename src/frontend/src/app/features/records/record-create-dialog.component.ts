import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  resource,
  signal,
  untracked,
} from '@angular/core';
import { FormField, form, required, submit } from '@angular/forms/signals';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCircleAlert } from '@ng-icons/lucide';
import type { BrnDialogState } from '@spartan-ng/brain/dialog';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmDialogImports } from '@spartan-ng/helm/dialog';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import type { components } from '../../core/api/generated/schema';
import { newUuid } from '../../core/util/uuid';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { mapFormErrors } from '../../shared/ui/api-error-text';
import { FieldErrorsComponent } from '../../shared/ui/field-errors.component';
import type { RecordDto } from './record-save.service';

type Template = components['schemas']['TemplateDto'];

interface CreateModel {
  title: string;
  templateId: string;
}

/**
 * Modal for a new record: title plus template (asked only when the collection has more than
 * one). With `collectionId` the record joins that collection, without it becomes a free page of
 * `spaceId`. Emits the created record; the parent navigates.
 */
@Component({
  selector: 'ag-record-create-dialog',
  imports: [
    FormField,
    NgIcon,
    FieldErrorsComponent,
    HlmAlertImports,
    HlmButton,
    HlmDialogImports,
    HlmFieldImports,
    HlmInput,
  ],
  providers: [provideIcons({ lucideCircleAlert })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-dialog
      state="open"
      [closeOnOutsidePointerEvents]="false"
      (stateChanged)="onStateChanged($event)"
    >
      <hlm-dialog-content *hlmDialogPortal [showCloseButton]="false">
        <hlm-dialog-header>
          <h2 hlmDialogTitle>
            {{ collectionId() ? 'Neuer Record' : 'Neue Seite' }}
          </h2>
        </hlm-dialog-header>
        <form novalidate data-testid="create-record-dialog" (submit)="onSubmit($event)">
          <div hlmFieldGroup class="gap-5">
            @if (formError(); as message) {
              <div hlmAlert variant="destructive" data-testid="form-error">
                <ng-icon name="lucideCircleAlert" />
                <p hlmAlertDescription class="whitespace-pre-line">{{ message }}</p>
              </div>
            }

            <div hlmField class="gap-2">
              <label hlmFieldLabel for="record-title">Titel</label>
              <input
                hlmInput
                id="record-title"
                type="text"
                autocomplete="off"
                data-testid="create-title"
                [formField]="form.title"
              />
              <div data-testid="title-errors">
                @if (form.title().touched() && form.title().invalid()) {
                  <ag-field-errors [errors]="form.title().errors()" />
                }
              </div>
            </div>

            @if (templates().length > 1) {
              <div hlmField class="gap-2">
                <label hlmFieldLabel for="record-template">Vorlage</label>
                <select
                  hlmInput
                  id="record-template"
                  data-testid="create-template"
                  [formField]="form.templateId"
                >
                  @for (template of templates(); track template.id) {
                    <option [value]="template.id">
                      {{ template.name }}{{ template.isDefault ? ' (Standard)' : '' }}
                    </option>
                  }
                </select>
              </div>
            }

            <hlm-dialog-footer>
              <button
                type="button"
                hlmBtn
                variant="outline"
                data-testid="create-cancel"
                (click)="cancelled.emit()"
              >
                Abbrechen
              </button>
              <button
                type="submit"
                hlmBtn
                data-testid="create-submit"
                [disabled]="form().submitting()"
              >
                Anlegen
              </button>
            </hlm-dialog-footer>
          </div>
        </form>
      </hlm-dialog-content>
    </hlm-dialog>
  `,
})
export class RecordCreateDialogComponent {
  private readonly api = inject(ApiClient);
  private readonly context = inject(WorkspaceContext);

  /** Collection of the new record; `null` creates a free page. */
  readonly collectionId = input<string | null>(null);
  readonly spaceId = input<string | null>(null);

  readonly created = output<RecordDto>();
  readonly cancelled = output();

  private readonly templatesResource = resource<
    Template[],
    { slug: string; collectionId: string } | undefined
  >({
    params: () => {
      const slug = this.context.slug();
      const collectionId = this.collectionId();
      return slug && collectionId ? { slug, collectionId } : undefined;
    },
    loader: ({ params }) =>
      firstValueFrom(
        this.api.workspace(params.slug).get('/collections/{collectionId}/templates', {
          path: { collectionId: params.collectionId },
        }),
      ),
  });
  protected readonly templates = computed(() =>
    this.templatesResource.hasValue() ? this.templatesResource.value() : [],
  );

  private readonly model = signal<CreateModel>({ title: '', templateId: '' });
  protected readonly form = form(this.model, (path) => {
    required(path.title, { message: 'Bitte einen Titel angeben.' });
  });
  protected readonly formError = signal<string | null>(null);

  /**
   * One key per intention: a retry after a failure (e.g. lost response) sends the same key, so
   * the server returns the first record instead of creating a second; a new one follows success.
   */
  private idempotencyKey: string = newUuid();

  constructor() {
    // Preselect the default template as soon as the templates are known.
    effect(() => {
      const list = this.templates();
      untracked(() => {
        if (list.length > 0 && !list.some((t) => t.id === this.model().templateId)) {
          const preferred = list.find((t) => t.isDefault) ?? list[0];
          this.model.update((m) => ({ ...m, templateId: preferred.id }));
        }
      });
    });
  }

  /** Brain closed the dialog (Escape): the parent removes the component. */
  protected onStateChanged(state: BrnDialogState): void {
    if (state === 'closed') {
      this.cancelled.emit();
    }
  }

  protected async onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    this.formError.set(null);
    const slug = this.context.slug();
    if (!slug) {
      return;
    }
    await submit(this.form, async (fields) => {
      const value = fields().value();
      const collectionId = this.collectionId();
      try {
        const record = await firstValueFrom(
          this.api.workspace(slug).post('/records', {
            headers: { 'Idempotency-Key': this.idempotencyKey },
            body: {
              title: value.title.trim(),
              ...(collectionId ? { collectionId } : {}),
              ...(this.spaceId() ? { spaceId: this.spaceId() } : {}),
              ...(this.templates().length > 1 && value.templateId
                ? { templateId: value.templateId }
                : {}),
            },
          }),
        );
        this.idempotencyKey = newUuid();
        this.created.emit(record);
        return undefined;
      } catch (error) {
        const { fieldErrors, formMessage } = mapFormErrors(error, { title: fields.title });
        this.formError.set(formMessage);
        return fieldErrors;
      }
    });
  }
}
