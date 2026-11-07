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
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideArchive,
  lucideCheck,
  lucideCircleAlert,
  lucideInfo,
  lucideUsers,
} from '@ng-icons/lucide';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCardImports } from '@spartan-ng/helm/card';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { HlmTextarea } from '@spartan-ng/helm/textarea';
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
  imports: [
    FormField,
    RouterLink,
    NgIcon,
    HlmAlertImports,
    HlmButton,
    HlmCardImports,
    HlmFieldImports,
    HlmInput,
    HlmSkeleton,
    HlmTextarea,
    BadgeComponent,
    ConfirmDialogComponent,
    FieldErrorsComponent,
  ],
  providers: [
    provideIcons({ lucideArchive, lucideCheck, lucideCircleAlert, lucideInfo, lucideUsers }),
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="mx-auto flex max-w-2xl flex-col gap-6 p-6">
      <div class="flex flex-col gap-1">
        <h1 class="text-2xl font-semibold tracking-tight">Einstellungen</h1>
        @if (workspace(); as ws) {
          <p class="text-sm text-muted-foreground">
            Deine Rolle: <ag-badge tone="accent">{{ ws.role }}</ag-badge> · Scopes:
            <span data-testid="scopes">{{ ws.scopes.join(', ') }}</span>
          </p>
        }
      </div>

      @if (workspace(); as ws) {
        @if (ws.archivedAt) {
          <div hlmAlert variant="destructive" data-testid="archived-notice">
            <ng-icon name="lucideCircleAlert" />
            <p hlmAlertDescription class="whitespace-pre-line">
              Dieser Workspace ist archiviert und schreibgeschützt.
            </p>
          </div>
        }
        @if (!canEdit() && !ws.archivedAt) {
          <div hlmAlert role="status" data-testid="readonly-notice">
            <ng-icon name="lucideInfo" />
            <p hlmAlertDescription>Nur Admins und Owner können die Einstellungen ändern.</p>
          </div>
        }

        <form hlmCard novalidate (submit)="onSubmit($event)">
          <div hlmCardContent>
            <div hlmFieldGroup class="gap-5">
              @if (formError(); as message) {
                <div hlmAlert variant="destructive" data-testid="form-error">
                  <ng-icon name="lucideCircleAlert" />
                  <p hlmAlertDescription class="whitespace-pre-line">{{ message }}</p>
                </div>
              }
              @if (saved()) {
                <div hlmAlert role="status" data-testid="saved">
                  <ng-icon name="lucideCheck" />
                  <p hlmAlertDescription>Gespeichert.</p>
                </div>
              }

              <div hlmField class="gap-2">
                <span hlmFieldLabel>Slug</span>
                <p class="font-mono text-sm text-muted-foreground">{{ ws.slug }}</p>
              </div>

              <div hlmField class="gap-2">
                <label hlmFieldLabel for="set-name">Name</label>
                <input hlmInput id="set-name" type="text" [formField]="form.name" />
                <div data-testid="name-errors">
                  @if (form.name().touched() && form.name().invalid()) {
                    <ag-field-errors [errors]="form.name().errors()" />
                  }
                </div>
              </div>

              <div hlmField class="gap-2">
                <label hlmFieldLabel for="set-description">Beschreibung</label>
                <textarea
                  hlmTextarea
                  id="set-description"
                  rows="3"
                  [formField]="form.description"
                ></textarea>
                <div data-testid="description-errors">
                  @if (form.description().touched() && form.description().invalid()) {
                    <ag-field-errors [errors]="form.description().errors()" />
                  }
                </div>
              </div>

              @if (canEdit()) {
                <div class="flex justify-end">
                  <button hlmBtn type="submit" data-testid="save" [disabled]="form().submitting()">
                    Speichern
                  </button>
                </div>
              }
            </div>
          </div>
        </form>

        <div>
          <a hlmBtn variant="outline" routerLink="members">
            <ng-icon name="lucideUsers" />
            Mitglieder verwalten
          </a>
        </div>

        @if (canArchive()) {
          <section hlmCard class="ring-destructive/50">
            <header hlmCardHeader>
              <h2 hlmCardTitle class="text-destructive">Workspace archivieren</h2>
              <p hlmCardDescription>
                Danach ist der Workspace schreibgeschützt. Das lässt sich über die API derzeit nicht
                rückgängig machen.
              </p>
            </header>
            <div hlmCardContent class="flex flex-col gap-4">
              @if (archiveError(); as message) {
                <div hlmAlert variant="destructive" data-testid="archive-error">
                  <ng-icon name="lucideCircleAlert" />
                  <p hlmAlertDescription>{{ message }}</p>
                </div>
              }
              <div>
                <button
                  hlmBtn
                  type="button"
                  variant="destructive"
                  data-testid="archive"
                  (click)="confirming.set(true)"
                >
                  <ng-icon name="lucideArchive" />
                  Archivieren …
                </button>
              </div>
            </div>
          </section>
        }
      } @else if (store.error(); as error) {
        <div hlmAlert variant="destructive" data-testid="load-error">
          <ng-icon name="lucideCircleAlert" />
          <p hlmAlertDescription>{{ describe(error) }}</p>
        </div>
      } @else if (store.isLoading()) {
        <div class="flex flex-col gap-3" aria-busy="true" aria-label="Wird geladen">
          <hlm-skeleton class="h-48 rounded-xl" />
        </div>
      } @else {
        <p class="text-sm text-muted-foreground" data-testid="not-found">
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
