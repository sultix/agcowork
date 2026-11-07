import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { FormField, form, required, submit } from '@angular/forms/signals';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { mapFormErrors } from '../../shared/ui/api-error-text';
import { BreadcrumbsComponent } from '../../shared/ui/breadcrumbs.component';
import { FieldErrorsComponent } from '../../shared/ui/field-errors.component';
import { RecordCreateDialogComponent } from '../records/record-create-dialog.component';
import { createRecordPager } from '../records/record-pager';
import type { RecordDto } from '../records/record-save.service';
import { WorkspacesStore } from '../workspaces/workspaces.store';
import { WorkspaceTreeStore } from './workspace-tree.store';

interface SpaceModel {
  name: string;
  description: string;
}

/** `/w/:slug/spaces/:spaceId`: space header, its collections and free pages. */
@Component({
  selector: 'ag-space-page',
  imports: [
    DatePipe,
    FormField,
    RouterLink,
    BreadcrumbsComponent,
    FieldErrorsComponent,
    RecordCreateDialogComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="mx-auto max-w-4xl p-6">
      @if (space(); as space) {
        <ag-breadcrumbs [crumbs]="[{ label: space.name }]" />

        @if (editing()) {
          <form
            class="mt-2 rounded-lg border border-border bg-surface p-4"
            novalidate
            data-testid="space-form"
            (submit)="onSaveSpace($event)"
          >
            @if (spaceError(); as message) {
              <p
                class="mb-3 rounded-md border border-danger px-3 py-2 text-sm text-danger"
                role="alert"
                data-testid="space-form-error"
              >
                {{ message }}
              </p>
            }
            <label for="space-name" class="block text-sm font-medium">Name</label>
            <input
              id="space-name"
              type="text"
              class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 text-sm"
              data-testid="space-name"
              [formField]="spaceForm.name"
            />
            @if (spaceForm.name().touched() && spaceForm.name().invalid()) {
              <ag-field-errors [errors]="spaceForm.name().errors()" />
            }
            <label for="space-description" class="mt-3 block text-sm font-medium"
              >Beschreibung</label
            >
            <textarea
              id="space-description"
              rows="3"
              class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 text-sm"
              data-testid="space-description"
              [formField]="spaceForm.description"
            ></textarea>
            <div class="mt-3 flex gap-2">
              <button
                type="submit"
                class="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60"
                data-testid="space-save"
                [disabled]="spaceForm().submitting()"
              >
                Speichern
              </button>
              <button
                type="button"
                class="rounded-md border border-border px-3 py-1.5 text-sm"
                (click)="editing.set(false)"
              >
                Abbrechen
              </button>
            </div>
          </form>
        } @else {
          <div class="mt-2 flex items-start justify-between gap-4">
            <div>
              <h1 class="text-xl font-semibold" data-testid="space-title">
                @if (space.icon) {
                  <span aria-hidden="true">{{ space.icon }} </span>
                }
                {{ space.name }}
              </h1>
              @if (space.description) {
                <p class="mt-1 text-sm text-muted" data-testid="space-description-text">
                  {{ space.description }}
                </p>
              }
            </div>
            @if (canWrite()) {
              <button
                type="button"
                class="rounded-md border border-border px-3 py-1.5 text-sm hover:bg-surface-hover"
                data-testid="space-edit"
                (click)="startEdit()"
              >
                Bearbeiten
              </button>
            }
          </div>
        }

        <div class="mt-8 flex items-center justify-between">
          <h2 class="text-lg font-semibold">Collections</h2>
          @if (canWrite()) {
            <button
              type="button"
              class="rounded-md border border-border px-3 py-1.5 text-sm hover:bg-surface-hover"
              data-testid="collection-new"
              (click)="creatingCollection.set(!creatingCollection())"
            >
              Neue Collection
            </button>
          }
        </div>
        @if (creatingCollection()) {
          <form
            class="mt-3 rounded-lg border border-border bg-surface p-4"
            novalidate
            data-testid="collection-form"
            (submit)="onCreateCollection($event)"
          >
            @if (collectionError(); as message) {
              <p
                class="mb-3 rounded-md border border-danger px-3 py-2 text-sm whitespace-pre-line text-danger"
                role="alert"
                data-testid="collection-form-error"
              >
                {{ message }}
              </p>
            }
            <label for="collection-name" class="block text-sm font-medium">Name</label>
            <input
              id="collection-name"
              type="text"
              class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 text-sm"
              data-testid="collection-name"
              [formField]="collectionForm.name"
            />
            <div data-testid="collection-name-errors">
              @if (collectionForm.name().touched() && collectionForm.name().invalid()) {
                <ag-field-errors [errors]="collectionForm.name().errors()" />
              }
            </div>
            <label for="collection-description" class="mt-3 block text-sm font-medium"
              >Beschreibung (für Agenten: was gehört hier hinein?)</label
            >
            <textarea
              id="collection-description"
              rows="3"
              class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 text-sm"
              data-testid="collection-description"
              [formField]="collectionForm.description"
            ></textarea>
            <div data-testid="collection-description-errors">
              @if (
                collectionForm.description().touched() && collectionForm.description().invalid()
              ) {
                <ag-field-errors [errors]="collectionForm.description().errors()" />
              }
            </div>
            <button
              type="submit"
              class="mt-3 rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60"
              data-testid="collection-submit"
              [disabled]="collectionForm().submitting()"
            >
              Anlegen
            </button>
          </form>
        }
        <ul
          class="mt-3 divide-y divide-border rounded-lg border border-border"
          data-testid="collection-list"
        >
          @for (collection of collections(); track collection.id) {
            <li>
              <a
                [routerLink]="['/w', context.slug(), 'collections', collection.id]"
                class="block px-4 py-2 hover:bg-surface-hover"
                data-testid="collection-item"
              >
                <span class="font-medium">{{ collection.name }}</span>
                <span class="block text-sm text-muted">{{ collection.description }}</span>
              </a>
            </li>
          } @empty {
            <li class="px-4 py-3 text-sm text-muted">Noch keine Collections.</li>
          }
        </ul>

        <div class="mt-8 flex items-center justify-between">
          <h2 class="text-lg font-semibold">Seiten</h2>
          @if (canWrite()) {
            <button
              type="button"
              class="rounded-md border border-border px-3 py-1.5 text-sm hover:bg-surface-hover"
              data-testid="page-new"
              (click)="creatingPage.set(true)"
            >
              Neue Seite
            </button>
          }
        </div>
        @if (pages.error(); as message) {
          <p class="mt-3 text-sm text-danger" role="alert" data-testid="pages-error">
            {{ message }}
          </p>
        }
        <ul
          class="mt-3 divide-y divide-border rounded-lg border border-border"
          data-testid="page-list"
        >
          @for (page of pages.items(); track page.id) {
            <li>
              <a
                [routerLink]="['/w', context.slug(), 'records', page.id]"
                class="flex items-baseline justify-between gap-4 px-4 py-2 hover:bg-surface-hover"
                data-testid="page-item"
              >
                <span>{{ page.title }}</span>
                <span class="text-sm text-muted">{{
                  page.updatedAt | date: 'dd.MM.yyyy HH:mm'
                }}</span>
              </a>
            </li>
          } @empty {
            <li class="px-4 py-3 text-sm text-muted">Noch keine freien Seiten.</li>
          }
        </ul>
        @if (pages.hasMore()) {
          <button
            type="button"
            class="mt-3 rounded-md border border-border px-3 py-1.5 text-sm hover:bg-surface-hover disabled:opacity-60"
            data-testid="pages-more"
            [disabled]="pages.loadingMore()"
            (click)="pages.loadMore()"
          >
            Mehr laden
          </button>
        }
      } @else if (tree.error()) {
        <p class="text-sm text-danger" role="alert" data-testid="load-error">
          Der Space konnte nicht geladen werden.
        </p>
      } @else if (!tree.isLoading()) {
        <p class="text-sm text-muted" data-testid="not-found">Space nicht gefunden.</p>
      }
    </section>

    @if (creatingPage()) {
      <ag-record-create-dialog
        [spaceId]="spaceId()"
        (created)="onCreated($event)"
        (cancelled)="creatingPage.set(false)"
      />
    }
  `,
})
export class SpacePageComponent {
  protected readonly tree = inject(WorkspaceTreeStore);
  protected readonly context = inject(WorkspaceContext);
  private readonly workspaces = inject(WorkspacesStore);
  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);

  /** Route parameter `:spaceId`. */
  readonly spaceId = input.required<string>();

  protected readonly space = computed(() => this.tree.findSpace(this.spaceId()));
  protected readonly collections = computed(() =>
    this.tree.collections().filter((c) => c.spaceId === this.spaceId()),
  );
  protected readonly canWrite = computed(() => {
    const ws = this.workspaces.find(this.context.slug());
    return !!ws && !ws.archivedAt && ws.scopes.includes('write');
  });

  protected readonly pages = createRecordPager(() => {
    const slug = this.context.slug();
    return slug && this.spaceId() ? { slug, spaceId: this.spaceId() } : undefined;
  });

  protected readonly editing = signal(false);
  protected readonly spaceError = signal<string | null>(null);
  private readonly spaceModel = signal<SpaceModel>({ name: '', description: '' });
  protected readonly spaceForm = form(this.spaceModel, (path) => {
    required(path.name, { message: 'Bitte einen Namen angeben.' });
  });

  protected readonly creatingCollection = signal(false);
  protected readonly collectionError = signal<string | null>(null);
  private readonly collectionModel = signal<SpaceModel>({ name: '', description: '' });
  protected readonly collectionForm = form(this.collectionModel, (path) => {
    required(path.name, { message: 'Bitte einen Namen angeben.' });
    required(path.description, { message: 'Bitte beschreiben, was in diese Collection gehört.' });
  });

  protected readonly creatingPage = signal(false);

  constructor() {
    // Another space (same component instance) closes open forms.
    effect(() => {
      this.spaceId();
      untracked(() => {
        this.editing.set(false);
        this.creatingCollection.set(false);
        this.creatingPage.set(false);
      });
    });
  }

  protected startEdit(): void {
    const space = this.space();
    if (!space) {
      return;
    }
    this.spaceError.set(null);
    this.spaceModel.set({ name: space.name, description: space.description ?? '' });
    this.spaceForm().reset();
    this.editing.set(true);
  }

  protected async onSaveSpace(event: Event): Promise<void> {
    event.preventDefault();
    this.spaceError.set(null);
    const slug = this.context.slug();
    if (!slug) {
      return;
    }
    await submit(this.spaceForm, async (fields) => {
      const value = fields().value();
      try {
        await firstValueFrom(
          this.api.workspace(slug).patch('/spaces/{spaceId}', {
            path: { spaceId: this.spaceId() },
            body: { name: value.name.trim(), description: value.description.trim() },
          }),
        );
        this.tree.reload();
        this.editing.set(false);
        return undefined;
      } catch (error) {
        const { fieldErrors, formMessage } = mapFormErrors(error, {
          name: fields.name,
          description: fields.description,
        });
        this.spaceError.set(formMessage);
        return fieldErrors;
      }
    });
  }

  protected async onCreateCollection(event: Event): Promise<void> {
    event.preventDefault();
    this.collectionError.set(null);
    const slug = this.context.slug();
    if (!slug) {
      return;
    }
    await submit(this.collectionForm, async (fields) => {
      const value = fields().value();
      try {
        const created = await firstValueFrom(
          this.api.workspace(slug).post('/collections', {
            body: {
              spaceId: this.spaceId(),
              name: value.name.trim(),
              description: value.description.trim(),
            },
          }),
        );
        this.tree.reload();
        this.collectionModel.set({ name: '', description: '' });
        this.collectionForm().reset();
        this.creatingCollection.set(false);
        await this.router.navigate(['/w', slug, 'collections', created.id]);
        return undefined;
      } catch (error) {
        const { fieldErrors, formMessage } = mapFormErrors(error, {
          name: fields.name,
          description: fields.description,
        });
        this.collectionError.set(formMessage);
        return fieldErrors;
      }
    });
  }

  protected onCreated(record: RecordDto): void {
    this.creatingPage.set(false);
    void this.router.navigate(['/w', this.context.slug(), 'records', record.id]);
  }
}
