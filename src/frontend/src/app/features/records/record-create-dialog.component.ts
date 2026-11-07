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
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import type { components } from '../../core/api/generated/schema';
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
  imports: [FormField, FieldErrorsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:keydown.escape)': 'cancelled.emit()' },
  template: `
    <div class="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <form
        class="w-full max-w-md rounded-lg border border-border bg-surface p-6"
        role="dialog"
        aria-modal="true"
        aria-labelledby="create-record-title"
        novalidate
        data-testid="create-record-dialog"
        (submit)="onSubmit($event)"
      >
        <h2 id="create-record-title" class="text-lg font-semibold">
          {{ collectionId() ? 'Neuer Record' : 'Neue Seite' }}
        </h2>
        @if (formError(); as message) {
          <p
            class="mt-3 rounded-md border border-danger px-3 py-2 text-sm whitespace-pre-line text-danger"
            role="alert"
            data-testid="form-error"
          >
            {{ message }}
          </p>
        }

        <label for="record-title" class="mt-4 block text-sm font-medium">Titel</label>
        <input
          id="record-title"
          type="text"
          autocomplete="off"
          class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 text-sm"
          data-testid="create-title"
          [formField]="form.title"
        />
        <div data-testid="title-errors">
          @if (form.title().touched() && form.title().invalid()) {
            <ag-field-errors [errors]="form.title().errors()" />
          }
        </div>

        @if (templates().length > 1) {
          <label for="record-template" class="mt-4 block text-sm font-medium">Vorlage</label>
          <select
            id="record-template"
            class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 text-sm"
            data-testid="create-template"
            [formField]="form.templateId"
          >
            @for (template of templates(); track template.id) {
              <option [value]="template.id">
                {{ template.name }}{{ template.isDefault ? ' (Standard)' : '' }}
              </option>
            }
          </select>
        }

        <div class="mt-6 flex justify-end gap-2">
          <button
            type="button"
            class="rounded-md border border-border px-3 py-1.5 text-sm hover:bg-surface-hover"
            data-testid="create-cancel"
            (click)="cancelled.emit()"
          >
            Abbrechen
          </button>
          <button
            type="submit"
            class="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60"
            data-testid="create-submit"
            [disabled]="form().submitting()"
          >
            Anlegen
          </button>
        </div>
      </form>
    </div>
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
