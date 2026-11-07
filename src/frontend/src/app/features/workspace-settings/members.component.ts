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
    BadgeComponent,
    ConfirmDialogComponent,
    FieldErrorsComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="mx-auto max-w-4xl p-6">
      <h1 class="text-xl font-semibold">Mitglieder</h1>

      @if (!canManage()) {
        <p class="mt-1 text-sm text-muted" data-testid="readonly-notice">
          Nur Admins und Owner können Mitglieder verwalten.
        </p>
      }
      @if (actionError(); as message) {
        <p
          class="mt-4 rounded-md border border-danger px-3 py-2 text-sm whitespace-pre-line text-danger"
          role="alert"
          data-testid="action-error"
        >
          {{ message }}
        </p>
      }

      @if (loadError(); as message) {
        <p class="mt-4 text-sm text-danger" role="alert" data-testid="load-error">{{ message }}</p>
      } @else {
        <table class="mt-4 w-full text-left text-sm" data-testid="members-table">
          <thead class="border-b border-border text-muted">
            <tr>
              <th class="py-2 pr-3 font-medium">Name</th>
              <th class="py-2 pr-3 font-medium">Art</th>
              <th class="py-2 pr-3 font-medium">Rolle</th>
              <th class="py-2 pr-3 font-medium">Scopes</th>
              <th class="py-2 pr-3 font-medium">Seit</th>
              <th class="py-2"></th>
            </tr>
          </thead>
          <tbody>
            @for (member of members(); track member.actorId) {
              <tr class="border-b border-border" data-testid="member-row">
                <td class="py-2 pr-3">
                  {{ member.name }}
                  @if (member.actorId === myId()) {
                    <span class="text-muted">(du)</span>
                  }
                </td>
                <td class="py-2 pr-3">
                  <ag-badge>{{ kindLabel(member.kind) }}</ag-badge>
                </td>
                @if (editing(); as edit) {
                  @if (edit.member.actorId === member.actorId) {
                    <td class="py-2 pr-3">
                      <select
                        class="rounded-md border border-border bg-bg px-2 py-1"
                        data-testid="edit-role"
                        [value]="edit.role"
                        (change)="setEditRole($any($event.target).value)"
                      >
                        @for (role of assignableRoles(); track role) {
                          <option [value]="role" [selected]="role === edit.role">{{ role }}</option>
                        }
                      </select>
                    </td>
                    <td class="py-2 pr-3">
                      @for (scope of scopes; track scope) {
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
                    <td class="py-2 pr-3">{{ member.createdAt | date: 'dd.MM.yyyy' }}</td>
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
              <tr>
                <td colspan="6" class="py-3 text-muted">Keine Mitglieder.</td>
              </tr>
            }
          </tbody>
        </table>
      }

      @if (canManage()) {
        <form
          class="mt-8 rounded-lg border border-border bg-surface p-6"
          novalidate
          (submit)="onAdd($event)"
          data-testid="add-form"
        >
          <h2 class="font-semibold">Mitglied hinzufügen</h2>
          @if (formError(); as message) {
            <p
              class="mt-3 rounded-md border border-danger px-3 py-2 text-sm whitespace-pre-line text-danger"
              role="alert"
              data-testid="form-error"
            >
              {{ message }}
            </p>
          }

          <label for="add-actor" class="mt-4 block text-sm font-medium">Actor</label>
          @if (actors(); as list) {
            <select
              id="add-actor"
              class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 text-sm"
              [formField]="form.actorId"
            >
              <option value="">Bitte wählen …</option>
              @for (actor of list; track actor.id) {
                <option [value]="actor.id">{{ actor.name }} ({{ kindLabel(actor.kind) }})</option>
              }
            </select>
          } @else {
            <input
              id="add-actor"
              type="text"
              placeholder="Actor-ID (UUID)"
              autocomplete="off"
              class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 font-mono text-sm"
              [formField]="form.actorId"
            />
          }
          <div data-testid="actorId-errors">
            @if (form.actorId().touched() && form.actorId().invalid()) {
              <ag-field-errors [errors]="form.actorId().errors()" />
            }
          </div>

          <label for="add-role" class="mt-4 block text-sm font-medium">Rolle</label>
          <select
            id="add-role"
            class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 text-sm"
            [formField]="form.role"
          >
            @for (role of assignableRoles(); track role) {
              <option [value]="role">{{ role }}</option>
            }
          </select>
          <div data-testid="role-errors">
            @if (form.role().touched() && form.role().invalid()) {
              <ag-field-errors [errors]="form.role().errors()" />
            }
          </div>

          <fieldset class="mt-4">
            <legend class="text-sm font-medium">Scopes (optional, leer = Rechte der Rolle)</legend>
            @for (scope of scopes; track scope) {
              <label class="mr-3 inline-flex items-center gap-1 text-sm">
                <input
                  type="checkbox"
                  [attr.data-testid]="'add-scope-' + scope"
                  [checked]="addScopes().includes(scope)"
                  (change)="toggleAddScope(scope)"
                />{{ scope }}
              </label>
            }
          </fieldset>

          <button
            type="submit"
            class="mt-6 rounded-md bg-accent px-4 py-2 text-sm font-medium text-accent-fg disabled:opacity-60"
            data-testid="add-submit"
            [disabled]="form().submitting()"
          >
            Hinzufügen
          </button>
        </form>
      }
    </section>

    <ng-template #readCells let-member>
      <td class="py-2 pr-3">
        <ag-badge tone="accent">{{ member.role }}</ag-badge>
      </td>
      <td class="py-2 pr-3 text-muted">{{ member.scopes.join(', ') }}</td>
      <td class="py-2 pr-3">{{ member.createdAt | date: 'dd.MM.yyyy' }}</td>
      <td class="py-2 text-right whitespace-nowrap">
        @if (canChange(member)) {
          <button
            type="button"
            class="rounded-md border border-border px-2 py-1 hover:bg-surface-hover"
            data-testid="member-edit"
            (click)="startEdit(member)"
          >
            Ändern
          </button>
          <button
            type="button"
            class="ml-1 rounded-md border border-danger px-2 py-1 text-danger hover:bg-surface-hover"
            data-testid="member-remove"
            (click)="removing.set(member)"
          >
            Entfernen
          </button>
        }
      </td>
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
