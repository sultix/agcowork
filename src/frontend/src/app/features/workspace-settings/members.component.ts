import { DatePipe, NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  resource,
  signal,
} from '@angular/core';
import { FormField, form, required, submit } from '@angular/forms/signals';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCircleAlert, lucideEllipsis, lucidePencil, lucideTrash2 } from '@ng-icons/lucide';
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
import type { components } from '../../core/api/generated/schema';
import { SessionStore } from '../../core/auth/session.store';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { describeError, mapFormErrors } from '../../shared/ui/api-error-text';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { ConfirmDialogComponent } from '../../shared/ui/confirm-dialog.component';
import { FieldErrorsComponent } from '../../shared/ui/field-errors.component';
import { ADMIN_ROLES, WorkspacesStore } from '../workspaces/workspaces.store';

type Member = components['schemas']['MemberDto'];
type Actor = components['schemas']['ActorDto'];

const ROLES = ['owner', 'admin', 'editor', 'viewer'] as const;
const SCOPES = ['read', 'write', 'schema', 'admin'] as const;
const KIND_LABELS: Record<string, string> = { human: 'Mensch', agent: 'Agent' };

interface AddModel {
  actorId: string;
  role: string;
}

interface Editing {
  member: Member;
  role: string;
  scopes: readonly string[];
}

function sameSet(a: readonly string[], b: readonly string[]): boolean {
  return a.length === b.length && a.every((x) => b.includes(x));
}

/** `/w/:slug/settings/members`: members with role and scopes; admins add, change and remove. */
@Component({
  selector: 'ag-members',
  imports: [
    DatePipe,
    NgTemplateOutlet,
    FormField,
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
    ConfirmDialogComponent,
    FieldErrorsComponent,
  ],
  providers: [provideIcons({ lucideCircleAlert, lucideEllipsis, lucidePencil, lucideTrash2 })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="mx-auto flex max-w-4xl flex-col gap-6 p-6">
      <div class="flex flex-col gap-1">
        <h1 class="text-2xl font-semibold tracking-tight">Mitglieder</h1>
        @if (!canManage()) {
          <p class="text-sm text-muted-foreground" data-testid="readonly-notice">
            Nur Admins und Owner können Mitglieder verwalten.
          </p>
        }
      </div>
      @if (actionError(); as message) {
        <div hlmAlert variant="destructive" data-testid="action-error">
          <ng-icon name="lucideCircleAlert" />
          <p hlmAlertDescription class="whitespace-pre-line">{{ message }}</p>
        </div>
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
            <table hlmTable data-testid="members-table">
              <thead hlmTHead>
                <tr hlmTr>
                  <th hlmTh class="px-4">Name</th>
                  <th hlmTh>Art</th>
                  <th hlmTh>Rolle</th>
                  <th hlmTh>Scopes</th>
                  <th hlmTh>Seit</th>
                  <th hlmTh class="w-12"><span class="sr-only">Aktionen</span></th>
                </tr>
              </thead>
              <tbody hlmTBody>
                @for (member of members(); track member.actorId) {
                  <tr hlmTr data-testid="member-row">
                    <td hlmTd class="px-4 font-medium">
                      {{ member.name }}
                      @if (member.actorId === myId()) {
                        <span class="font-normal text-muted-foreground">(du)</span>
                      }
                    </td>
                    <td hlmTd>
                      <ag-badge>{{ kindLabel(member.kind) }}</ag-badge>
                    </td>
                    @if (editing(); as edit) {
                      @if (edit.member.actorId === member.actorId) {
                        <td hlmTd>
                          <hlm-native-select
                            size="sm"
                            data-testid="edit-role"
                            [value]="edit.role"
                            (valueChange)="setEditRole($any($event))"
                          >
                            @for (role of assignableRoles(); track role) {
                              <option hlmNativeSelectOption [value]="role">{{ role }}</option>
                            }
                          </hlm-native-select>
                        </td>
                        <td hlmTd>
                          <div class="flex flex-wrap gap-x-3 gap-y-1">
                            @for (scope of scopes; track scope) {
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
                        <td hlmTd>{{ member.createdAt | date: 'dd.MM.yyyy' }}</td>
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
                          [ngTemplateOutletContext]="{ $implicit: member }"
                        />
                      }
                    } @else {
                      <ng-container
                        [ngTemplateOutlet]="readCells"
                        [ngTemplateOutletContext]="{ $implicit: member }"
                      />
                    }
                  </tr>
                } @empty {
                  <tr hlmTr>
                    <td hlmTd colspan="6" class="px-4 text-muted-foreground">Keine Mitglieder.</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </div>
      }

      @if (canManage()) {
        <form hlmCard novalidate data-testid="add-form" (submit)="onAdd($event)">
          <header hlmCardHeader>
            <h2 hlmCardTitle>Mitglied hinzufügen</h2>
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
                <label hlmFieldLabel for="add-actor">Actor</label>
                @if (actors(); as list) {
                  <hlm-native-select selectId="add-actor" class="w-full" [formField]="form.actorId">
                    <option hlmNativeSelectOption value="">Bitte wählen …</option>
                    @for (actor of list; track actor.id) {
                      <option hlmNativeSelectOption [value]="actor.id">
                        {{ actor.name }} ({{ kindLabel(actor.kind) }})
                      </option>
                    }
                  </hlm-native-select>
                } @else {
                  <input
                    hlmInput
                    id="add-actor"
                    type="text"
                    placeholder="Actor-ID (UUID)"
                    autocomplete="off"
                    class="font-mono"
                    [formField]="form.actorId"
                  />
                }
                <div data-testid="actorId-errors">
                  @if (form.actorId().touched() && form.actorId().invalid()) {
                    <ag-field-errors [errors]="form.actorId().errors()" />
                  }
                </div>
              </div>

              <div hlmField class="gap-2">
                <label hlmFieldLabel for="add-role">Rolle</label>
                <hlm-native-select selectId="add-role" class="w-full" [formField]="form.role">
                  @for (role of assignableRoles(); track role) {
                    <option hlmNativeSelectOption [value]="role">{{ role }}</option>
                  }
                </hlm-native-select>
                <div data-testid="role-errors">
                  @if (form.role().touched() && form.role().invalid()) {
                    <ag-field-errors [errors]="form.role().errors()" />
                  }
                </div>
              </div>

              <fieldset hlmFieldSet class="gap-2">
                <legend hlmFieldLegend variant="label" class="mb-0">
                  Scopes (optional, leer = Rechte der Rolle)
                </legend>
                <div class="flex flex-wrap gap-x-4 gap-y-2">
                  @for (scope of scopes; track scope) {
                    <div class="flex items-center gap-2">
                      <hlm-checkbox
                        [inputId]="'add-scope-' + scope"
                        [attr.data-testid]="'add-scope-' + scope"
                        [checked]="addScopes().includes(scope)"
                        (checkedChange)="toggleAddScope(scope)"
                      />
                      <label hlmLabel class="font-normal" [for]="'add-scope-' + scope">{{
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
                  data-testid="add-submit"
                  [disabled]="form().submitting()"
                >
                  Hinzufügen
                </button>
              </div>
            </div>
          </div>
        </form>
      }
    </section>

    <ng-template #readCells let-member>
      <td hlmTd>
        <ag-badge tone="accent">{{ member.role }}</ag-badge>
      </td>
      <td hlmTd class="text-muted-foreground">{{ member.scopes.join(', ') }}</td>
      <td hlmTd>{{ member.createdAt | date: 'dd.MM.yyyy' }}</td>
      <td hlmTd class="text-right">
        @if (canChange(member)) {
          <button
            hlmBtn
            type="button"
            variant="ghost"
            size="icon-sm"
            data-testid="member-actions"
            [attr.aria-label]="'Aktionen für ' + member.name"
            [hlmDropdownMenuTrigger]="actionMenu"
            [hlmDropdownMenuTriggerData]="{ member }"
            align="end"
          >
            <ng-icon name="lucideEllipsis" />
          </button>
        }
      </td>
    </ng-template>

    <ng-template #actionMenu let-member="member">
      <hlm-dropdown-menu class="w-40" data-testid="member-menu">
        <button hlmDropdownMenuItem data-testid="member-edit" (triggered)="startEdit(member)">
          <ng-icon name="lucidePencil" />
          Ändern
        </button>
        <hlm-dropdown-menu-separator />
        <button
          hlmDropdownMenuItem
          variant="destructive"
          data-testid="member-remove"
          (triggered)="removing.set(member)"
        >
          <ng-icon name="lucideTrash2" />
          Entfernen
        </button>
      </hlm-dropdown-menu>
    </ng-template>

    <ag-confirm-dialog
      [open]="removing() !== null"
      title="Mitglied entfernen?"
      [message]="removeMessage()"
      confirmLabel="Entfernen"
      [busy]="busy()"
      (confirmed)="confirmRemove()"
      (cancelled)="removing.set(null)"
    />
  `,
})
export class MembersComponent {
  private readonly api = inject(ApiClient);
  private readonly session = inject(SessionStore);
  private readonly context = inject(WorkspaceContext);
  private readonly workspaces = inject(WorkspacesStore);

  protected readonly scopes = SCOPES;

  private readonly workspace = computed(() => this.workspaces.find(this.context.slug()));
  protected readonly myId = computed(() => this.session.me()?.id);
  private readonly myRole = computed(() => this.workspace()?.role);
  private readonly isInstanceAdmin = computed(
    () => this.session.me()?.instanceScopes.includes('instance:admin') ?? false,
  );

  /** Admins and owners manage members; nobody changes an archived workspace. */
  protected readonly canManage = computed(() => {
    const ws = this.workspace();
    return !!ws && !ws.archivedAt && ADMIN_ROLES.includes(ws.role);
  });
  /** Only owners can hand out the owner role. */
  protected readonly assignableRoles = computed(() =>
    this.myRole() === 'owner' ? ROLES : ROLES.filter((r) => r !== 'owner'),
  );

  private readonly membersResource = resource<Member[], string | undefined>({
    params: () => this.context.slug() ?? undefined,
    loader: ({ params: slug }) => firstValueFrom(this.api.workspace(slug).get('/members')),
  });
  protected readonly members = computed(() =>
    this.membersResource.hasValue() ? this.membersResource.value() : [],
  );
  protected readonly loading = computed(
    () => this.membersResource.isLoading() && !this.membersResource.hasValue(),
  );
  protected readonly loadError = computed(() =>
    this.membersResource.error() ? describeError(this.membersResource.error()) : null,
  );

  /** Actor choices for instance admins (`GET /actors`); others type the actor ID. */
  private readonly actorsResource = resource<Actor[], true | undefined>({
    params: () => (this.isInstanceAdmin() ? true : undefined),
    loader: () => firstValueFrom(this.api.get('/actors')),
  });
  protected readonly actors = computed(() => {
    if (!this.actorsResource.hasValue()) {
      return null;
    }
    const memberIds = new Set(this.members().map((m) => m.actorId));
    return this.actorsResource.value().filter((a) => a.isActive && !memberIds.has(a.id));
  });

  private readonly addModel = signal<AddModel>({ actorId: '', role: 'editor' });
  protected readonly form = form(this.addModel, (path) => {
    required(path.actorId, { message: 'Bitte einen Actor angeben.' });
    required(path.role, { message: 'Bitte eine Rolle wählen.' });
  });
  protected readonly addScopes = signal<readonly string[]>([]);
  protected readonly formError = signal<string | null>(null);

  protected readonly actionError = signal<string | null>(null);
  protected readonly editing = signal<Editing | null>(null);
  protected readonly removing = signal<Member | null>(null);
  protected readonly busy = signal(false);
  protected readonly removeMessage = computed(
    () => `${this.removing()?.name ?? ''} verliert den Zugriff auf diesen Workspace.`,
  );

  protected kindLabel(kind: string): string {
    return KIND_LABELS[kind] ?? kind;
  }

  /** Owners can only be changed by owners. */
  protected canChange(member: Member): boolean {
    return this.canManage() && (member.role !== 'owner' || this.myRole() === 'owner');
  }

  protected toggleAddScope(scope: string): void {
    this.addScopes.update((list) =>
      list.includes(scope) ? list.filter((s) => s !== scope) : [...list, scope],
    );
  }

  protected async onAdd(event: Event): Promise<void> {
    event.preventDefault();
    this.formError.set(null);
    this.actionError.set(null);
    const slug = this.context.slug();
    if (!slug) {
      return;
    }

    await submit(this.form, async (fields) => {
      const value = fields().value();
      try {
        await firstValueFrom(
          this.api.workspace(slug).post('/members', {
            body: {
              actorId: value.actorId.trim(),
              role: value.role,
              scopes: this.addScopes().length > 0 ? [...this.addScopes()] : null,
            },
          }),
        );
        this.membersResource.reload();
        this.addModel.set({ actorId: '', role: 'editor' });
        this.addScopes.set([]);
        this.form().reset();
        return undefined;
      } catch (error) {
        const { fieldErrors, formMessage } = mapFormErrors(error, {
          actorId: fields.actorId,
          role: fields.role,
        });
        this.formError.set(formMessage);
        return fieldErrors;
      }
    });
  }

  protected startEdit(member: Member): void {
    this.actionError.set(null);
    this.editing.set({ member, role: member.role, scopes: member.scopes });
  }

  protected setEditRole(role: string): void {
    this.editing.update((e) => (e ? { ...e, role } : e));
  }

  protected toggleEditScope(scope: string): void {
    this.editing.update((e) =>
      e
        ? {
            ...e,
            scopes: e.scopes.includes(scope)
              ? e.scopes.filter((s) => s !== scope)
              : [...e.scopes, scope],
          }
        : e,
    );
  }

  /** Sends only what changed: a new role alone resets the scopes to the role's defaults. */
  protected async saveEdit(): Promise<void> {
    const edit = this.editing();
    const slug = this.context.slug();
    if (!edit || !slug) {
      return;
    }
    const roleChanged = edit.role !== edit.member.role;
    const scopesChanged = !sameSet(edit.scopes, edit.member.scopes);
    if (!roleChanged && !scopesChanged) {
      this.editing.set(null);
      return;
    }
    this.actionError.set(null);
    try {
      await firstValueFrom(
        this.api.workspace(slug).patch('/members/{actorId}', {
          path: { actorId: edit.member.actorId },
          body: {
            role: roleChanged ? edit.role : null,
            scopes: scopesChanged ? [...edit.scopes] : null,
          },
        }),
      );
      this.editing.set(null);
      this.membersResource.reload();
    } catch (error) {
      this.actionError.set(describeError(error));
    }
  }

  protected async confirmRemove(): Promise<void> {
    const member = this.removing();
    const slug = this.context.slug();
    if (!member || !slug) {
      return;
    }
    this.busy.set(true);
    this.actionError.set(null);
    try {
      await firstValueFrom(
        this.api
          .workspace(slug)
          .delete('/members/{actorId}', { path: { actorId: member.actorId } }),
        { defaultValue: undefined },
      );
      this.membersResource.reload();
    } catch (error) {
      this.actionError.set(describeError(error));
    } finally {
      this.busy.set(false);
      this.removing.set(null);
    }
  }
}
