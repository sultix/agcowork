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
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideArrowLeft, lucideCircleAlert } from '@ng-icons/lucide';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCardImports } from '@spartan-ng/helm/card';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmNativeSelectImports } from '@spartan-ng/helm/native-select';
import { HlmTextarea } from '@spartan-ng/helm/textarea';
import { mapFormErrors } from '../../shared/ui/api-error-text';
import { FieldErrorsComponent } from '../../shared/ui/field-errors.component';
import { SessionMenuComponent } from '../../shared/ui/session-menu.component';
import { ThemeToggleComponent } from '../../shared/ui/theme-toggle.component';
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
  imports: [
    FormField,
    RouterLink,
    NgIcon,
    HlmAlertImports,
    HlmButton,
    HlmCardImports,
    HlmFieldImports,
    HlmInput,
    HlmNativeSelectImports,
    HlmTextarea,
    SessionMenuComponent,
    ThemeToggleComponent,
    FieldErrorsComponent,
  ],
  providers: [provideIcons({ lucideArrowLeft, lucideCircleAlert })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="flex h-14 items-center gap-2 border-b px-4 sm:px-6">
      <span class="text-sm font-semibold tracking-tight">agcowork</span>
      <div class="ml-auto flex items-center gap-1">
        <ag-theme-toggle />
        <ag-session-menu />
      </div>
    </header>
    <main class="mx-auto flex max-w-xl flex-col gap-4 p-6">
      <a hlmBtn variant="ghost" size="sm" routerLink="/workspaces" class="self-start">
        <ng-icon name="lucideArrowLeft" />
        Workspaces
      </a>
      <section hlmCard>
        <header hlmCardHeader>
          <h1 hlmCardTitle class="text-xl">Neuer Workspace</h1>
          <p hlmCardDescription>Ein eigener Bereich mit Mitgliedern, Spaces und Collections.</p>
        </header>
        <div hlmCardContent>
          <form novalidate (submit)="onSubmit($event)">
            <div hlmFieldGroup class="gap-5">
              @if (formError(); as message) {
                <div hlmAlert variant="destructive" data-testid="form-error">
                  <ng-icon name="lucideCircleAlert" />
                  <p hlmAlertDescription class="whitespace-pre-line">{{ message }}</p>
                </div>
              }

              <div hlmField class="gap-2">
                <label hlmFieldLabel for="ws-name">Name</label>
                <input hlmInput id="ws-name" type="text" [formField]="form.name" />
                <div data-testid="name-errors">
                  @if (showErrors(form.name)) {
                    <ag-field-errors [errors]="form.name().errors()" />
                  }
                </div>
              </div>

              <div hlmField class="gap-2">
                <label hlmFieldLabel for="ws-slug">Slug</label>
                <input
                  hlmInput
                  id="ws-slug"
                  type="text"
                  autocomplete="off"
                  class="font-mono"
                  [formField]="form.slug"
                />
                <p hlmFieldDescription>
                  URL:
                  <span class="font-mono" data-testid="slug-preview"
                    >/w/{{ model().slug || '…' }}</span
                  >
                  . Der Slug lässt sich später nicht ändern.
                </p>
                <div data-testid="slug-errors">
                  @if (showErrors(form.slug)) {
                    <ag-field-errors [errors]="form.slug().errors()" />
                  }
                </div>
              </div>

              <div hlmField class="gap-2">
                <label hlmFieldLabel for="ws-description">Beschreibung</label>
                <textarea
                  hlmTextarea
                  id="ws-description"
                  rows="3"
                  [formField]="form.description"
                ></textarea>
                <div data-testid="description-errors">
                  @if (showErrors(form.description)) {
                    <ag-field-errors [errors]="form.description().errors()" />
                  }
                </div>
              </div>

              <div hlmField class="gap-2">
                <label hlmFieldLabel for="ws-template">Vorlage</label>
                <hlm-native-select
                  selectId="ws-template"
                  class="w-full"
                  [formField]="form.templateId"
                >
                  @for (template of templates(); track template.id) {
                    <option hlmNativeSelectOption [value]="template.id">{{ template.name }}</option>
                  }
                </hlm-native-select>
                @if (selectedTemplate()?.description; as description) {
                  <p hlmFieldDescription data-testid="template-description">{{ description }}</p>
                }
                @if (templatesFailed()) {
                  <p class="text-sm text-destructive" data-testid="template-fallback">
                    Die Vorlagen konnten nicht geladen werden; es steht nur die leere Vorlage zur
                    Verfügung.
                  </p>
                }
                <div data-testid="template-errors">
                  @if (showErrors(form.templateId)) {
                    <ag-field-errors [errors]="form.templateId().errors()" />
                  }
                </div>
              </div>

              <div class="flex justify-end">
                <button hlmBtn type="submit" [disabled]="form().submitting()">
                  {{ form().submitting() ? 'Wird angelegt …' : 'Workspace anlegen' }}
                </button>
              </div>
            </div>
          </form>
        </div>
      </section>
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
