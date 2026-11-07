import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  resource,
  signal,
} from '@angular/core';
import { FieldTree, FormField, form, minLength, required, submit } from '@angular/forms/signals';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import type { components } from '../../core/api/generated/schema';
import { describeError, mapFormErrors } from '../../shared/ui/api-error-text';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { FieldErrorsComponent } from '../../shared/ui/field-errors.component';

type Actor = components['schemas']['ActorDto'];

const INSTANCE_SCOPES = ['instance:admin', 'workspaces:create'] as const;
const KIND_LABELS: Record<string, string> = { human: 'Mensch', agent: 'Agent' };
export const MIN_PASSWORD_LENGTH = 12;

interface CreateModel {
  kind: 'human' | 'agent';
  name: string;
  role: string;
  email: string;
  password: string;
}

interface Editing {
  actor: Actor;
  name: string;
  role: string;
  scopes: readonly string[];
  isActive: boolean;
}

/** `/admin/actors`: humans and agents of the installation; create, edit, link to agent keys. */
@Component({
  selector: 'ag-actors',
  imports: [NgTemplateOutlet, FormField, RouterLink, BadgeComponent, FieldErrorsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="mx-auto max-w-5xl p-6">
      <div class="flex items-center justify-between">
        <h1 class="text-xl font-semibold">Actors</h1>
        <button
          type="button"
          class="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg"
          data-testid="new-actor"
          (click)="creating.set(!creating())"
        >
          Neuer Actor
        </button>
      </div>

      @if (actionError(); as message) {
        <p
          class="mt-4 rounded-md border border-danger px-3 py-2 text-sm whitespace-pre-line text-danger"
          role="alert"
          data-testid="action-error"
        >
          {{ message }}
        </p>
      }

      @if (creating()) {
        <form
          class="mt-4 rounded-lg border border-border bg-surface p-6"
          novalidate
          (submit)="onCreate($event)"
          data-testid="create-form"
        >
          <h2 class="font-semibold">Neuer Actor</h2>
          @if (formError(); as message) {
            <p
              class="mt-3 rounded-md border border-danger px-3 py-2 text-sm whitespace-pre-line text-danger"
              role="alert"
              data-testid="form-error"
            >
              {{ message }}
            </p>
          }

          <label for="actor-kind" class="mt-4 block text-sm font-medium">Art</label>
          <select
            id="actor-kind"
            class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 text-sm"
            [formField]="form.kind"
          >
            <option value="human">Mensch</option>
            <option value="agent">Agent</option>
          </select>

          <label for="actor-name" class="mt-4 block text-sm font-medium">Name</label>
          <input
            id="actor-name"
            type="text"
            class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 text-sm"
            [formField]="form.name"
          />
          <div data-testid="name-errors">
            @if (shown(form.name)) {
              <ag-field-errors [errors]="form.name().errors()" />
            }
          </div>

          <label for="actor-role" class="mt-4 block text-sm font-medium">Rolle (Freitext)</label>
          <input
            id="actor-role"
            type="text"
            placeholder="z. B. Tech-Lead"
            class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 text-sm"
            [formField]="form.role"
          />
          <div data-testid="role-errors">
            @if (shown(form.role)) {
              <ag-field-errors [errors]="form.role().errors()" />
            }
          </div>

          @if (model().kind === 'human') {
            <label for="actor-email" class="mt-4 block text-sm font-medium">E-Mail</label>
            <input
              id="actor-email"
              type="email"
              autocomplete="off"
              class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 text-sm"
              [formField]="form.email"
            />
            <div data-testid="email-errors">
              @if (shown(form.email)) {
                <ag-field-errors [errors]="form.email().errors()" />
              }
            </div>

            <label for="actor-password" class="mt-4 block text-sm font-medium"
              >Initialpasswort (mind. {{ minPasswordLength }} Zeichen)</label
            >
            <input
              id="actor-password"
              type="password"
              autocomplete="new-password"
              class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 text-sm"
              [formField]="form.password"
            />
            <div data-testid="password-errors">
              @if (shown(form.password)) {
                <ag-field-errors [errors]="form.password().errors()" />
              }
            </div>
          }

          <fieldset class="mt-4">
            <legend class="text-sm font-medium">Instanz-Scopes</legend>
            @for (scope of instanceScopes; track scope) {
              <label class="mr-3 inline-flex items-center gap-1 text-sm">
                <input
                  type="checkbox"
                  [attr.data-testid]="'create-scope-' + scope"
                  [checked]="createScopes().includes(scope)"
                  (change)="toggleCreateScope(scope)"
                />{{ scope }}
              </label>
            }
          </fieldset>

          <button
            type="submit"
            class="mt-6 rounded-md bg-accent px-4 py-2 text-sm font-medium text-accent-fg disabled:opacity-60"
            data-testid="create-submit"
            [disabled]="form().submitting()"
          >
            Anlegen
          </button>
        </form>
      }

      @if (loadError(); as message) {
        <p class="mt-4 text-sm text-danger" role="alert" data-testid="load-error">{{ message }}</p>
      } @else {
        <table class="mt-4 w-full text-left text-sm" data-testid="actors-table">
          <thead class="border-b border-border text-muted">
            <tr>
              <th class="py-2 pr-3 font-medium">Name</th>
              <th class="py-2 pr-3 font-medium">Art</th>
              <th class="py-2 pr-3 font-medium">Rolle</th>
              <th class="py-2 pr-3 font-medium">E-Mail</th>
              <th class="py-2 pr-3 font-medium">Instanz-Scopes</th>
              <th class="py-2 pr-3 font-medium">Status</th>
              <th class="py-2"></th>
            </tr>
          </thead>
          <tbody>
            @for (actor of actors(); track actor.id) {
              <tr class="border-b border-border" data-testid="actor-row">
                @if (editing(); as edit) {
                  @if (edit.actor.id === actor.id) {
                    <td class="py-2 pr-3">
                      <input
                        type="text"
                        class="w-full rounded-md border border-border bg-bg px-2 py-1"
                        data-testid="edit-name"
                        [value]="edit.name"
                        (input)="patchEdit({ name: $any($event.target).value })"
                      />
                    </td>
                    <td class="py-2 pr-3">
                      <ag-badge>{{ kindLabel(actor.kind) }}</ag-badge>
                    </td>
                    <td class="py-2 pr-3">
                      <input
                        type="text"
                        class="w-full rounded-md border border-border bg-bg px-2 py-1"
                        data-testid="edit-role"
                        [value]="edit.role"
                        (input)="patchEdit({ role: $any($event.target).value })"
                      />
                    </td>
                    <td class="py-2 pr-3">{{ actor.email }}</td>
                    <td class="py-2 pr-3">
                      @for (scope of instanceScopes; track scope) {
                        <label class="mr-2 inline-flex items-center gap-1">
                          <input
                            type="checkbox"
                            [attr.data-testid]="'edit-scope-' + scope"
                            [checked]="edit.scopes.includes(scope)"
                            (change)="toggleEditScope(scope)"
                          />{{ scope }}
                        </label>
                      }
                    </td>
                    <td class="py-2 pr-3">
                      <label class="inline-flex items-center gap-1">
                        <input
                          type="checkbox"
                          data-testid="edit-active"
                          [checked]="edit.isActive"
                          (change)="patchEdit({ isActive: !edit.isActive })"
                        />aktiv
                      </label>
                    </td>
                    <td class="py-2 text-right whitespace-nowrap">
                      <button
                        type="button"
                        class="rounded-md bg-accent px-2 py-1 text-accent-fg"
                        data-testid="edit-save"
                        (click)="saveEdit()"
                      >
                        Speichern
                      </button>
                      <button
                        type="button"
                        class="ml-1 rounded-md border border-border px-2 py-1"
                        (click)="editing.set(null)"
                      >
                        Abbrechen
                      </button>
                    </td>
                  } @else {
                    <ng-container
                      [ngTemplateOutlet]="readCells"
                      [ngTemplateOutletContext]="{ $implicit: actor }"
                    />
                  }
                } @else {
                  <ng-container
                    [ngTemplateOutlet]="readCells"
                    [ngTemplateOutletContext]="{ $implicit: actor }"
                  />
                }
              </tr>
            } @empty {
              <tr>
                <td colspan="7" class="py-3 text-muted">Keine Actors.</td>
              </tr>
            }
          </tbody>
        </table>
      }
    </section>

    <ng-template #readCells let-actor>
      <td class="py-2 pr-3">{{ actor.name }}</td>
      <td class="py-2 pr-3">
        <ag-badge>{{ kindLabel(actor.kind) }}</ag-badge>
      </td>
      <td class="py-2 pr-3">{{ actor.role }}</td>
      <td class="py-2 pr-3">{{ actor.email }}</td>
      <td class="py-2 pr-3 text-muted">{{ actor.instanceScopes.join(', ') }}</td>
      <td class="py-2 pr-3">
        <ag-badge [tone]="actor.isActive ? 'success' : 'danger'">{{
          actor.isActive ? 'aktiv' : 'inaktiv'
        }}</ag-badge>
      </td>
      <td class="py-2 text-right whitespace-nowrap">
        @if (actor.kind === 'agent') {
          <a
            [routerLink]="['/admin/actors', actor.id, 'keys']"
            class="rounded-md border border-border px-2 py-1 hover:bg-surface-hover"
            data-testid="actor-keys"
            >Keys</a
          >
        }
        <button
          type="button"
          class="ml-1 rounded-md border border-border px-2 py-1 hover:bg-surface-hover"
          data-testid="actor-edit"
          (click)="startEdit(actor)"
        >
          Bearbeiten
        </button>
      </td>
    </ng-template>
  `,
})
export class ActorsComponent {
  private readonly api = inject(ApiClient);

  protected readonly instanceScopes = INSTANCE_SCOPES;
  protected readonly minPasswordLength = MIN_PASSWORD_LENGTH;

  private readonly actorsResource = resource<Actor[], true>({
    params: () => true,
    loader: () => firstValueFrom(this.api.get('/actors')),
  });
  protected readonly actors = computed(() =>
    this.actorsResource.hasValue() ? this.actorsResource.value() : [],
  );
  protected readonly loadError = computed(() =>
    this.actorsResource.error() ? describeError(this.actorsResource.error()) : null,
  );

  protected readonly model = signal<CreateModel>({
    kind: 'agent',
    name: '',
    role: '',
    email: '',
    password: '',
  });
  protected readonly form = form(this.model, (path) => {
    required(path.name, { message: 'Bitte einen Namen eingeben.' });
    required(path.email, {
      message: 'Bitte eine E-Mail-Adresse eingeben.',
      when: ({ valueOf }) => valueOf(path.kind) === 'human',
    });
    required(path.password, {
      message: 'Bitte ein Initialpasswort vergeben.',
      when: ({ valueOf }) => valueOf(path.kind) === 'human',
    });
    minLength(path.password, MIN_PASSWORD_LENGTH, {
      message: `Das Passwort braucht mindestens ${MIN_PASSWORD_LENGTH} Zeichen.`,
      when: ({ valueOf }) => valueOf(path.kind) === 'human',
    });
  });
  protected readonly createScopes = signal<readonly string[]>([]);
  protected readonly creating = signal(false);
  protected readonly formError = signal<string | null>(null);

  protected readonly actionError = signal<string | null>(null);
  protected readonly editing = signal<Editing | null>(null);

  protected kindLabel(kind: string): string {
    return KIND_LABELS[kind] ?? kind;
  }

  protected shown(field: FieldTree<string>): boolean {
    return field().touched() && field().invalid();
  }

  protected toggleCreateScope(scope: string): void {
    this.createScopes.update((list) =>
      list.includes(scope) ? list.filter((s) => s !== scope) : [...list, scope],
    );
  }

  protected async onCreate(event: Event): Promise<void> {
    event.preventDefault();
    this.formError.set(null);

    await submit(this.form, async (fields) => {
      const value = fields().value();
      const human = value.kind === 'human';
      try {
        await firstValueFrom(
          this.api.post('/actors', {
            body: {
              kind: value.kind,
              name: value.name.trim(),
              role: value.role.trim() || null,
              email: human ? value.email.trim() : null,
              password: human ? value.password : null,
              instanceScopes: [...this.createScopes()],
            },
          }),
        );
      } catch (error) {
        const { fieldErrors, formMessage } = mapFormErrors(error, {
          kind: fields.kind,
          name: fields.name,
          role: fields.role,
          email: fields.email,
          password: fields.password,
        });
        this.formError.set(formMessage);
        return fieldErrors;
      }
      this.actorsResource.reload();
      this.model.set({ kind: value.kind, name: '', role: '', email: '', password: '' });
      this.createScopes.set([]);
      this.form().reset();
      this.creating.set(false);
      return undefined;
    });
  }

  protected startEdit(actor: Actor): void {
    this.actionError.set(null);
    this.editing.set({
      actor,
      name: actor.name,
      role: actor.role ?? '',
      scopes: actor.instanceScopes,
      isActive: actor.isActive,
    });
  }

  protected patchEdit(change: Partial<Omit<Editing, 'actor'>>): void {
    this.editing.update((e) => (e ? { ...e, ...change } : e));
  }

  protected toggleEditScope(scope: string): void {
    const edit = this.editing();
    if (edit) {
      this.patchEdit({
        scopes: edit.scopes.includes(scope)
          ? edit.scopes.filter((s) => s !== scope)
          : [...edit.scopes, scope],
      });
    }
  }

  protected async saveEdit(): Promise<void> {
    const edit = this.editing();
    if (!edit) {
      return;
    }
    this.actionError.set(null);
    try {
      await firstValueFrom(
        this.api.patch('/actors/{id}', {
          path: { id: edit.actor.id },
          body: {
            name: edit.name.trim(),
            role: edit.role.trim(),
            instanceScopes: [...edit.scopes],
            isActive: edit.isActive,
          },
        }),
      );
      this.editing.set(null);
      this.actorsResource.reload();
    } catch (error) {
      this.actionError.set(describeError(error));
    }
  }
}
