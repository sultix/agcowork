import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  resource,
  signal,
} from '@angular/core';
import { FieldTree, FormField, form, required, submit } from '@angular/forms/signals';
import { RouterLink } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideCircleAlert,
  lucideEllipsis,
  lucideInfo,
  lucideKeyRound,
  lucidePencil,
  lucidePlus,
} from '@ng-icons/lucide';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCardImports } from '@spartan-ng/helm/card';
import { HlmCheckbox } from '@spartan-ng/helm/checkbox';
import { HlmDropdownMenuImports } from '@spartan-ng/helm/dropdown-menu';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmLabel } from '@spartan-ng/helm/label';
import { HlmNativeSelectImports } from '@spartan-ng/helm/native-select';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { MIN_PASSWORD_LENGTH, applyPasswordPolicy } from '../../core/auth/password-policy';
import { SessionStore } from '../../core/auth/session.store';
import type { components } from '../../core/api/generated/schema';
import { describeError, mapFormErrors } from '../../shared/ui/api-error-text';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { FieldErrorsComponent } from '../../shared/ui/field-errors.component';
import { ResetPasswordDialogComponent } from './reset-password-dialog.component';

type Actor = components['schemas']['ActorDto'];

const INSTANCE_SCOPES = ['instance:admin', 'workspaces:create'] as const;
const KIND_LABELS: Record<string, string> = { human: 'Mensch', agent: 'Agent' };

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
  imports: [
    NgTemplateOutlet,
    FormField,
    RouterLink,
    NgIcon,
    HlmAlertImports,
    HlmButton,
    HlmCardImports,
    HlmCheckbox,
    HlmDropdownMenuImports,
    HlmFieldImports,
    HlmInput,
    HlmLabel,
    HlmNativeSelectImports,
    HlmSkeleton,
    HlmTableImports,
    BadgeComponent,
    FieldErrorsComponent,
    ResetPasswordDialogComponent,
  ],
  providers: [
    provideIcons({
      lucideCircleAlert,
      lucideEllipsis,
      lucideInfo,
      lucideKeyRound,
      lucidePencil,
      lucidePlus,
    }),
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="mx-auto flex max-w-5xl flex-col gap-6 p-6">
      <div class="flex flex-wrap items-start justify-between gap-4">
        <div class="flex flex-col gap-1">
          <h1 class="text-2xl font-semibold tracking-tight">Actors</h1>
          <p class="text-sm text-muted-foreground">Menschen und Agenten dieser Installation.</p>
        </div>
        <button hlmBtn type="button" data-testid="new-actor" (click)="creating.set(!creating())">
          <ng-icon name="lucidePlus" />
          Neuer Actor
        </button>
      </div>

      @if (actionError(); as message) {
        <div hlmAlert variant="destructive" data-testid="action-error">
          <ng-icon name="lucideCircleAlert" />
          <p hlmAlertDescription class="whitespace-pre-line">{{ message }}</p>
        </div>
      }

      @if (notice(); as message) {
        <div hlmAlert role="status" data-testid="action-notice">
          <ng-icon name="lucideInfo" />
          <p hlmAlertDescription>{{ message }}</p>
        </div>
      }

      @if (creating()) {
        <form hlmCard novalidate data-testid="create-form" (submit)="onCreate($event)">
          <header hlmCardHeader>
            <h2 hlmCardTitle>Neuer Actor</h2>
          </header>
          <div hlmCardContent>
            <div hlmFieldGroup class="gap-5">
              @if (formError(); as message) {
                <div hlmAlert variant="destructive" data-testid="form-error">
                  <ng-icon name="lucideCircleAlert" />
                  <p hlmAlertDescription class="whitespace-pre-line">{{ message }}</p>
                </div>
              }

              <div hlmField class="gap-2">
                <label hlmFieldLabel for="actor-kind">Art</label>
                <hlm-native-select selectId="actor-kind" class="w-full" [formField]="form.kind">
                  <option hlmNativeSelectOption value="human">Mensch</option>
                  <option hlmNativeSelectOption value="agent">Agent</option>
                </hlm-native-select>
              </div>

              <div hlmField class="gap-2">
                <label hlmFieldLabel for="actor-name">Name</label>
                <input hlmInput id="actor-name" type="text" [formField]="form.name" />
                <div data-testid="name-errors">
                  @if (shown(form.name)) {
                    <ag-field-errors [errors]="form.name().errors()" />
                  }
                </div>
              </div>

              <div hlmField class="gap-2">
                <label hlmFieldLabel for="actor-role">Rolle (Freitext)</label>
                <input
                  hlmInput
                  id="actor-role"
                  type="text"
                  placeholder="z. B. Tech-Lead"
                  [formField]="form.role"
                />
                <div data-testid="role-errors">
                  @if (shown(form.role)) {
                    <ag-field-errors [errors]="form.role().errors()" />
                  }
                </div>
              </div>

              @if (model().kind === 'human') {
                <div hlmField class="gap-2">
                  <label hlmFieldLabel for="actor-email">E-Mail</label>
                  <input
                    hlmInput
                    id="actor-email"
                    type="email"
                    autocomplete="off"
                    [formField]="form.email"
                  />
                  <div data-testid="email-errors">
                    @if (shown(form.email)) {
                      <ag-field-errors [errors]="form.email().errors()" />
                    }
                  </div>
                </div>

                <div hlmField class="gap-2">
                  <label hlmFieldLabel for="actor-password"
                    >Initialpasswort (mind. {{ minPasswordLength }} Zeichen)</label
                  >
                  <input
                    hlmInput
                    id="actor-password"
                    type="password"
                    autocomplete="new-password"
                    [formField]="form.password"
                  />
                  <div data-testid="password-errors">
                    @if (shown(form.password)) {
                      <ag-field-errors [errors]="form.password().errors()" />
                    }
                  </div>
                </div>
              }

              <fieldset hlmFieldSet class="gap-2">
                <legend hlmFieldLegend variant="label" class="mb-0">Instanz-Scopes</legend>
                <div class="flex flex-wrap gap-x-4 gap-y-2">
                  @for (scope of instanceScopes; track scope) {
                    <div class="flex items-center gap-2">
                      <hlm-checkbox
                        [inputId]="'create-scope-' + scope"
                        [attr.data-testid]="'create-scope-' + scope"
                        [checked]="createScopes().includes(scope)"
                        (checkedChange)="toggleCreateScope(scope)"
                      />
                      <label hlmLabel class="font-normal" [for]="'create-scope-' + scope">{{
                        scope
                      }}</label>
                    </div>
                  }
                </div>
              </fieldset>

              <div class="flex justify-end">
                <button
                  hlmBtn
                  type="submit"
                  data-testid="create-submit"
                  [disabled]="form().submitting()"
                >
                  Anlegen
                </button>
              </div>
            </div>
          </div>
        </form>
      }

      @if (loadError(); as message) {
        <div hlmAlert variant="destructive" data-testid="load-error">
          <ng-icon name="lucideCircleAlert" />
          <p hlmAlertDescription>{{ message }}</p>
        </div>
      } @else if (loading()) {
        <div class="flex flex-col gap-2" aria-busy="true" aria-label="Wird geladen">
          <hlm-skeleton class="h-10 rounded-lg" />
          <hlm-skeleton class="h-10 rounded-lg" />
          <hlm-skeleton class="h-10 rounded-lg" />
        </div>
      } @else {
        <div class="rounded-xl border">
          <div hlmTableContainer>
            <table hlmTable data-testid="actors-table">
              <thead hlmTHead>
                <tr hlmTr>
                  <th hlmTh class="px-4">Name</th>
                  <th hlmTh>Art</th>
                  <th hlmTh>Rolle</th>
                  <th hlmTh>E-Mail</th>
                  <th hlmTh>Instanz-Scopes</th>
                  <th hlmTh>Status</th>
                  <th hlmTh class="w-12"><span class="sr-only">Aktionen</span></th>
                </tr>
              </thead>
              <tbody hlmTBody>
                @for (actor of actors(); track actor.id) {
                  <tr hlmTr data-testid="actor-row">
                    @if (editing(); as edit) {
                      @if (edit.actor.id === actor.id) {
                        <td hlmTd class="px-4">
                          <input
                            hlmInput
                            type="text"
                            aria-label="Name"
                            data-testid="edit-name"
                            [value]="edit.name"
                            (input)="patchEdit({ name: $any($event.target).value })"
                          />
                        </td>
                        <td hlmTd>
                          <ag-badge>{{ kindLabel(actor.kind) }}</ag-badge>
                        </td>
                        <td hlmTd>
                          <input
                            hlmInput
                            type="text"
                            aria-label="Rolle"
                            data-testid="edit-role"
                            [value]="edit.role"
                            (input)="patchEdit({ role: $any($event.target).value })"
                          />
                        </td>
                        <td hlmTd>{{ actor.email }}</td>
                        <td hlmTd>
                          <div class="flex flex-col gap-1">
                            @for (scope of instanceScopes; track scope) {
                              <div class="flex items-center gap-2">
                                <hlm-checkbox
                                  [inputId]="'edit-scope-' + scope"
                                  [attr.data-testid]="'edit-scope-' + scope"
                                  [checked]="edit.scopes.includes(scope)"
                                  (checkedChange)="toggleEditScope(scope)"
                                />
                                <label hlmLabel class="font-normal" [for]="'edit-scope-' + scope">{{
                                  scope
                                }}</label>
                              </div>
                            }
                          </div>
                        </td>
                        <td hlmTd>
                          <div class="flex items-center gap-2">
                            <hlm-checkbox
                              [inputId]="'edit-active'"
                              data-testid="edit-active"
                              [checked]="edit.isActive"
                              (checkedChange)="patchEdit({ isActive: !edit.isActive })"
                            />
                            <label hlmLabel class="font-normal" [for]="'edit-active'">aktiv</label>
                          </div>
                        </td>
                        <td hlmTd class="text-right whitespace-nowrap">
                          <button
                            hlmBtn
                            type="button"
                            size="sm"
                            data-testid="edit-save"
                            (click)="saveEdit()"
                          >
                            Speichern
                          </button>
                          <button
                            hlmBtn
                            type="button"
                            variant="ghost"
                            size="sm"
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
                  <tr hlmTr>
                    <td hlmTd colspan="7" class="px-4 text-muted-foreground">Keine Actors.</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </div>
      }
    </section>

    <ng-template #readCells let-actor>
      <td hlmTd class="px-4 font-medium">{{ actor.name }}</td>
      <td hlmTd>
        <ag-badge>{{ kindLabel(actor.kind) }}</ag-badge>
      </td>
      <td hlmTd>{{ actor.role }}</td>
      <td hlmTd>{{ actor.email }}</td>
      <td hlmTd class="text-muted-foreground">{{ actor.instanceScopes.join(', ') }}</td>
      <td hlmTd>
        <ag-badge [tone]="actor.isActive ? 'success' : 'danger'">{{
          actor.isActive ? 'aktiv' : 'inaktiv'
        }}</ag-badge>
      </td>
      <td hlmTd class="text-right">
        <button
          hlmBtn
          type="button"
          variant="ghost"
          size="icon-sm"
          data-testid="actor-actions"
          [attr.aria-label]="'Aktionen für ' + actor.name"
          [hlmDropdownMenuTrigger]="actionMenu"
          [hlmDropdownMenuTriggerData]="{ actor }"
          align="end"
        >
          <ng-icon name="lucideEllipsis" />
        </button>
      </td>
    </ng-template>

    <ng-template #actionMenu let-actor="actor">
      <hlm-dropdown-menu class="w-44" data-testid="actor-menu">
        @if (actor.kind === 'agent') {
          <a
            hlmDropdownMenuItem
            [routerLink]="['/admin/actors', actor.id, 'keys']"
            data-testid="actor-keys"
          >
            <ng-icon name="lucideKeyRound" />
            Keys
          </a>
        }
        <button hlmDropdownMenuItem data-testid="actor-edit" (triggered)="startEdit(actor)">
          <ng-icon name="lucidePencil" />
          Bearbeiten
        </button>
        @if (actor.kind === 'human') {
          @if (actor.id === session.me()?.id) {
            <!-- A reset would sign the admin out without a new cookie; own password: self-service. -->
            <a
              hlmDropdownMenuItem
              routerLink="/account/password"
              [queryParams]="{ returnUrl: '/admin/actors' }"
              data-testid="actor-change-own-password"
            >
              <ng-icon name="lucideKeyRound" />
              Eigenes Passwort ändern
            </a>
          } @else {
            <button
              hlmDropdownMenuItem
              data-testid="actor-reset-password"
              (triggered)="startReset(actor)"
            >
              <ng-icon name="lucideKeyRound" />
              Passwort zurücksetzen
            </button>
          }
        }
      </hlm-dropdown-menu>
    </ng-template>

    @if (resetting(); as actor) {
      <ag-reset-password-dialog
        [actor]="actor"
        (passwordSet)="onResetDone(actor)"
        (cancelled)="resetting.set(null)"
      />
    }
  `,
})
export class ActorsComponent {
  private readonly api = inject(ApiClient);
  protected readonly session = inject(SessionStore);

  protected readonly instanceScopes = INSTANCE_SCOPES;
  protected readonly minPasswordLength = MIN_PASSWORD_LENGTH;

  private readonly actorsResource = resource<Actor[], true>({
    params: () => true,
    loader: () => firstValueFrom(this.api.get('/actors')),
  });
  protected readonly actors = computed(() =>
    this.actorsResource.hasValue() ? this.actorsResource.value() : [],
  );
  protected readonly loading = computed(
    () => this.actorsResource.isLoading() && !this.actorsResource.hasValue(),
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
    applyPasswordPolicy(path.password, {
      requiredMessage: 'Bitte ein Initialpasswort vergeben.',
      when: ({ valueOf }) => valueOf(path.kind) === 'human',
    });
  });
  protected readonly createScopes = signal<readonly string[]>([]);
  protected readonly creating = signal(false);
  protected readonly formError = signal<string | null>(null);

  protected readonly actionError = signal<string | null>(null);
  protected readonly editing = signal<Editing | null>(null);
  protected readonly resetting = signal<Actor | null>(null);
  protected readonly notice = signal<string | null>(null);

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

  protected startReset(actor: Actor): void {
    this.actionError.set(null);
    this.notice.set(null);
    this.resetting.set(actor);
  }

  protected onResetDone(actor: Actor): void {
    this.resetting.set(null);
    this.notice.set(`Das Passwort von ${actor.name} wurde gesetzt.`);
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
