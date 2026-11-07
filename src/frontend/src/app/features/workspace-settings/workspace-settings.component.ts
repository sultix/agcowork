import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { FormField, disabled, form, maxLength, required, submit } from '@angular/forms/signals';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { describeError, mapFormErrors } from '../../shared/ui/api-error-text';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { ConfirmDialogComponent } from '../../shared/ui/confirm-dialog.component';
import { FieldErrorsComponent } from '../../shared/ui/field-errors.component';
import { ADMIN_ROLES, WorkspacesStore } from '../workspaces/workspaces.store';

interface SettingsModel {
  name: string;
  description: string;
}

/** `/w/:slug/settings`: name, description, own role and scopes, archiving. */
@Component({
  selector: 'ag-workspace-settings',
  imports: [FormField, RouterLink, BadgeComponent, ConfirmDialogComponent, FieldErrorsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="mx-auto max-w-2xl p-6">
      <h1 class="text-xl font-semibold">Einstellungen</h1>

      @if (workspace(); as ws) {
        <p class="mt-1 text-sm text-muted">
          Deine Rolle: <ag-badge tone="accent">{{ ws.role }}</ag-badge> · Scopes:
          <span data-testid="scopes">{{ ws.scopes.join(', ') }}</span>
        </p>
        @if (ws.archivedAt) {
          <p
            class="mt-4 rounded-md border border-danger px-3 py-2 text-sm whitespace-pre-line text-danger"
            data-testid="archived-notice"
          >
            Dieser Workspace ist archiviert und schreibgeschützt.
          </p>
        }
        @if (!canEdit() && !ws.archivedAt) {
          <p class="mt-4 text-sm text-muted" data-testid="readonly-notice">
            Nur Admins und Owner können die Einstellungen ändern.
          </p>
        }

        <form
          class="mt-6 rounded-lg border border-border bg-surface p-6"
          novalidate
          (submit)="onSubmit($event)"
        >
          @if (formError(); as message) {
            <p
              class="mb-4 rounded-md border border-danger px-3 py-2 text-sm text-danger"
              role="alert"
              data-testid="form-error"
            >
              {{ message }}
            </p>
          }
          @if (saved()) {
            <p class="mb-4 text-sm text-success" data-testid="saved">Gespeichert.</p>
          }

          <span class="block text-sm font-medium">Slug</span>
          <p class="mt-1 font-mono text-sm text-muted">{{ ws.slug }}</p>

          <label for="set-name" class="mt-4 block text-sm font-medium">Name</label>
          <input
            id="set-name"
            type="text"
            class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 text-sm disabled:opacity-60"
            [formField]="form.name"
          />
          <div data-testid="name-errors">
            @if (form.name().touched() && form.name().invalid()) {
              <ag-field-errors [errors]="form.name().errors()" />
            }
          </div>

          <label for="set-description" class="mt-4 block text-sm font-medium">Beschreibung</label>
          <textarea
            id="set-description"
            rows="3"
            class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 text-sm disabled:opacity-60"
            [formField]="form.description"
          ></textarea>
          <div data-testid="description-errors">
            @if (form.description().touched() && form.description().invalid()) {
              <ag-field-errors [errors]="form.description().errors()" />
            }
          </div>

          @if (canEdit()) {
            <button
              type="submit"
              class="mt-6 rounded-md bg-accent px-4 py-2 text-sm font-medium text-accent-fg disabled:opacity-60"
              data-testid="save"
              [disabled]="form().submitting()"
            >
              Speichern
            </button>
          }
        </form>

        <p class="mt-6 text-sm">
          <a routerLink="members" class="text-accent underline">Mitglieder verwalten</a>
        </p>

        @if (canArchive()) {
          <div class="mt-8 rounded-lg border border-danger p-6">
            <h2 class="font-semibold text-danger">Workspace archivieren</h2>
            <p class="mt-1 text-sm text-muted">
              Danach ist der Workspace schreibgeschützt. Das lässt sich über die API derzeit nicht
              rückgängig machen.
            </p>
            @if (archiveError(); as message) {
              <p class="mt-2 text-sm text-danger" role="alert" data-testid="archive-error">
                {{ message }}
              </p>
            }
            <button
              type="button"
              class="mt-4 rounded-md border border-danger px-3 py-1.5 text-sm text-danger hover:bg-surface-hover"
              data-testid="archive"
              (click)="confirming.set(true)"
            >
              Archivieren …
            </button>
          </div>
        }
      } @else if (store.error(); as error) {
        <p class="mt-4 text-sm text-danger" role="alert" data-testid="load-error">
          {{ describe(error) }}
        </p>
      } @else if (store.isLoading()) {
        <p class="mt-4 text-sm text-muted">Workspace wird geladen …</p>
      } @else {
        <p class="mt-4 text-sm text-muted" data-testid="not-found">
          Workspace nicht gefunden oder keine Mitgliedschaft.
        </p>
      }
    </section>

    <ag-confirm-dialog
      [open]="confirming()"
      title="Workspace archivieren?"
      message="Der Workspace wird schreibgeschützt. Das kann nicht rückgängig gemacht werden."
      confirmLabel="Archivieren"
      [busy]="archiving()"
      (confirmed)="archive()"
      (cancelled)="confirming.set(false)"
    />
  `,
})
export class WorkspaceSettingsComponent {
  private readonly api = inject(ApiClient);
  protected readonly store = inject(WorkspacesStore);
  private readonly context = inject(WorkspaceContext);

  protected readonly workspace = computed(() => this.store.find(this.context.slug()));
  /** Admins and owners edit; nobody edits an archived workspace. */
  protected readonly canEdit = computed(() => {
    const ws = this.workspace();
    return !!ws && !ws.archivedAt && ADMIN_ROLES.includes(ws.role);
  });
  /** The API allows archiving to owners only. */
  protected readonly canArchive = computed(() => {
    const ws = this.workspace();
    return !!ws && !ws.archivedAt && ws.role === 'owner';
  });

  private readonly model = signal<SettingsModel>({ name: '', description: '' });
  protected readonly form = form(this.model, (path) => {
    required(path.name, { message: 'Bitte einen Namen eingeben.' });
    maxLength(path.name, 200, { message: 'Der Name ist zu lang.' });
    disabled(path.name, () => !this.canEdit());
    disabled(path.description, () => !this.canEdit());
  });

  protected readonly formError = signal<string | null>(null);
  protected readonly saved = signal(false);
  protected readonly confirming = signal(false);
  protected readonly archiving = signal(false);
  protected readonly archiveError = signal<string | null>(null);

  constructor() {
    // Fill the form once per loaded workspace; edits are not overwritten by later list updates.
    let filledFor: string | null = null;
    effect(() => {
      const ws = this.workspace();
      if (ws && ws.id !== filledFor) {
        filledFor = ws.id;
        this.model.set({ name: ws.name, description: ws.description ?? '' });
      }
    });
  }

  protected describe(error: unknown): string {
    return describeError(error);
  }

  protected async onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    this.formError.set(null);
    this.saved.set(false);
    const slug = this.context.slug();
    if (!slug || !this.canEdit()) {
      return;
    }

    await submit(this.form, async (fields) => {
      const value = fields().value();
      try {
        const updated = await firstValueFrom(
          this.api.workspace(slug).patch('', {
            body: { name: value.name.trim(), description: value.description.trim() },
          }),
        );
        this.store.upsert(updated);
        this.saved.set(true);
        return undefined;
      } catch (error) {
        const { fieldErrors, formMessage } = mapFormErrors(error, {
          name: fields.name,
          description: fields.description,
        });
        this.formError.set(formMessage);
        return fieldErrors;
      }
    });
  }

  protected async archive(): Promise<void> {
    const slug = this.context.slug();
    if (!slug) {
      return;
    }
    this.archiving.set(true);
    this.archiveError.set(null);
    try {
      const archived = await firstValueFrom(this.api.workspace(slug).post('/archive'));
      this.store.upsert(archived);
    } catch (error) {
      this.archiveError.set(describeError(error));
    } finally {
      this.archiving.set(false);
      this.confirming.set(false);
    }
  }
}
