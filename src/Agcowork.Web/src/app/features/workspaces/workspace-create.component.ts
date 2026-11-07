import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  resource,
  signal,
  untracked,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import type { components } from '../../core/api/generated/schema';
import { Router, RouterLink } from '@angular/router';
import {
  FieldTree,
  FormField,
  form,
  maxLength,
  pattern,
  required,
  submit,
} from '@angular/forms/signals';
import { mapFormErrors } from '../../shared/ui/api-error-text';
import { FieldErrorsComponent } from '../../shared/ui/field-errors.component';
import { SessionMenuComponent } from '../../shared/ui/session-menu.component';
import { WorkspacesStore } from './workspaces.store';

/** Same rule as the backend: lowercase letters and digits, single hyphens between, max. 63. */
export const SLUG_PATTERN = /^[a-z0-9]+(-[a-z0-9]+)*$/;
export const SLUG_MAX_LENGTH = 63;

interface CreateModel {
  slug: string;
  name: string;
  description: string;
  templateId: string;
}

type Template = components['schemas']['WorkspaceTemplateDto'];

/** Used when the template list cannot be loaded: the empty workspace always exists. */
const FALLBACK_TEMPLATE: Template = {
  id: 'empty',
  name: 'Leer',
  description: 'Leerer Workspace ohne Spaces und Collections.',
  isDefault: true,
};

/** `/workspaces/new`: create a workspace; on success continue in it. */
@Component({
  selector: 'ag-workspace-create',
  imports: [FormField, RouterLink, SessionMenuComponent, FieldErrorsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="flex items-center justify-end border-b border-border px-6 py-3">
      <ag-session-menu />
    </header>
    <main class="mx-auto max-w-xl p-6">
      <a routerLink="/workspaces" class="text-sm text-muted hover:text-fg">‹ Workspaces</a>
      <form
        class="mt-2 rounded-lg border border-border bg-surface p-6"
        novalidate
        (submit)="onSubmit($event)"
      >
        <h1 class="text-lg font-semibold">Neuer Workspace</h1>

        @if (formError(); as message) {
          <p
            class="mt-4 rounded-md border border-danger px-3 py-2 text-sm whitespace-pre-line text-danger"
            role="alert"
            data-testid="form-error"
          >
            {{ message }}
          </p>
        }

        <label for="ws-name" class="mt-4 block text-sm font-medium">Name</label>
        <input
          id="ws-name"
          type="text"
          class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 text-sm"
          [formField]="form.name"
        />
        <div data-testid="name-errors">
          @if (showErrors(form.name)) {
            <ag-field-errors [errors]="form.name().errors()" />
          }
        </div>

        <label for="ws-slug" class="mt-4 block text-sm font-medium">Slug</label>
        <input
          id="ws-slug"
          type="text"
          autocomplete="off"
          class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 font-mono text-sm"
          [formField]="form.slug"
        />
        <p class="mt-1 text-sm text-muted">
          URL:
          <span class="font-mono" data-testid="slug-preview">/w/{{ model().slug || '…' }}</span>
          . Der Slug lässt sich später nicht ändern.
        </p>
        <div data-testid="slug-errors">
          @if (showErrors(form.slug)) {
            <ag-field-errors [errors]="form.slug().errors()" />
          }
        </div>

        <label for="ws-description" class="mt-4 block text-sm font-medium">Beschreibung</label>
        <textarea
          id="ws-description"
          rows="3"
          class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 text-sm"
          [formField]="form.description"
        ></textarea>
        <div data-testid="description-errors">
          @if (showErrors(form.description)) {
            <ag-field-errors [errors]="form.description().errors()" />
          }
        </div>

        <label for="ws-template" class="mt-4 block text-sm font-medium">Vorlage</label>
        <select
          id="ws-template"
          class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 text-sm"
          [formField]="form.templateId"
        >
          @for (template of templates(); track template.id) {
            <option [value]="template.id">{{ template.name }}</option>
          }
        </select>
        @if (selectedTemplate()?.description; as description) {
          <p class="mt-1 text-sm text-muted" data-testid="template-description">
            {{ description }}
          </p>
        }
        @if (templatesFailed()) {
          <p class="mt-1 text-sm text-danger" data-testid="template-fallback">
            Die Vorlagen konnten nicht geladen werden; es steht nur die leere Vorlage zur Verfügung.
          </p>
        }
        <div data-testid="template-errors">
          @if (showErrors(form.templateId)) {
            <ag-field-errors [errors]="form.templateId().errors()" />
          }
        </div>

        <button
          type="submit"
          class="mt-6 rounded-md bg-accent px-4 py-2 text-sm font-medium text-accent-fg disabled:opacity-60"
          [disabled]="form().submitting()"
        >
          {{ form().submitting() ? 'Wird angelegt …' : 'Workspace anlegen' }}
        </button>
      </form>
    </main>
  `,
})
export class WorkspaceCreateComponent {
  private readonly api = inject(ApiClient);
  private readonly store = inject(WorkspacesStore);
  private readonly router = inject(Router);

  protected readonly model = signal<CreateModel>({
    slug: '',
    name: '',
    description: '',
    templateId: '',
  });

  protected readonly form = form(this.model, (path) => {
    required(path.name, { message: 'Bitte einen Namen eingeben.' });
    required(path.slug, { message: 'Bitte einen Slug eingeben.' });
    maxLength(path.slug, SLUG_MAX_LENGTH, {
      message: `Der Slug darf höchstens ${SLUG_MAX_LENGTH} Zeichen lang sein.`,
    });
    pattern(path.slug, SLUG_PATTERN, {
      message: 'Nur Kleinbuchstaben, Ziffern und einzelne Bindestriche (z. B. mein-projekt).',
    });
  });

  private readonly templatesResource = resource<Template[], true>({
    params: () => true,
    loader: () => firstValueFrom(this.api.get('/workspace-templates')),
  });
  protected readonly templatesFailed = computed(() => this.templatesResource.status() === 'error');
  protected readonly templates = computed<readonly Template[]>(() => {
    if (this.templatesResource.hasValue()) {
      return this.templatesResource.value();
    }
    return this.templatesFailed() ? [FALLBACK_TEMPLATE] : [];
  });
  protected readonly selectedTemplate = computed(() =>
    this.templates().find((t) => t.id === this.model().templateId),
  );
  protected readonly formError = signal<string | null>(null);

  constructor() {
    // Preselect the default template once the list is known (or the fallback applies).
    effect(() => {
      const list = this.templates();
      if (list.length > 0 && !list.some((t) => t.id === untracked(this.model).templateId)) {
        const preferred = list.find((t) => t.isDefault) ?? list[0];
        this.model.update((m) => ({ ...m, templateId: preferred.id }));
      }
    });
  }

  protected showErrors(field: FieldTree<string>): boolean {
    return field().touched() && field().invalid();
  }

  protected async onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    this.formError.set(null);

    await submit(this.form, async (fields) => {
      const value = fields().value();
      try {
        const created = await this.store.create({
          slug: value.slug,
          name: value.name.trim(),
          description: value.description.trim() || null,
          ...(value.templateId ? { templateId: value.templateId } : {}),
        });
        await this.router.navigate(['/w', created.slug]);
        return undefined;
      } catch (error) {
        const { fieldErrors, formMessage } = mapFormErrors(error, {
          slug: fields.slug,
          name: fields.name,
          description: fields.description,
          templateId: fields.templateId,
        });
        this.formError.set(formMessage);
        return fieldErrors;
      }
    });
  }
}
