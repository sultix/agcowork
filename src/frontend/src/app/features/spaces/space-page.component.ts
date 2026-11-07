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
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCircleAlert, lucidePencil, lucidePlus } from '@ng-icons/lucide';
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
    NgIcon,
    HlmAlertImports,
    HlmButton,
    HlmCardImports,
    HlmFieldImports,
    HlmInput,
    HlmSkeleton,
    HlmTextarea,
    BreadcrumbsComponent,
    FieldErrorsComponent,
    RecordCreateDialogComponent,
  ],
  providers: [provideIcons({ lucideCircleAlert, lucidePencil, lucidePlus })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="mx-auto flex max-w-4xl flex-col gap-6 p-6">
      @if (space(); as space) {
        <ag-breadcrumbs [crumbs]="[{ label: space.name }]" />

        @if (editing()) {
          <form hlmCard novalidate data-testid="space-form" (submit)="onSaveSpace($event)">
            <div hlmCardContent>
              <div hlmFieldGroup class="gap-5">
                @if (spaceError(); as message) {
                  <div hlmAlert variant="destructive" data-testid="space-form-error">
                    <ng-icon name="lucideCircleAlert" />
                    <p hlmAlertDescription>{{ message }}</p>
                  </div>
                }
                <div hlmField class="gap-2">
                  <label hlmFieldLabel for="space-name">Name</label>
                  <input
                    hlmInput
                    id="space-name"
                    type="text"
                    data-testid="space-name"
                    [formField]="spaceForm.name"
                  />
                  @if (spaceForm.name().touched() && spaceForm.name().invalid()) {
                    <ag-field-errors [errors]="spaceForm.name().errors()" />
                  }
                </div>
                <div hlmField class="gap-2">
                  <label hlmFieldLabel for="space-description">Beschreibung</label>
                  <textarea
                    hlmTextarea
                    id="space-description"
                    rows="3"
                    data-testid="space-description"
                    [formField]="spaceForm.description"
                  ></textarea>
                </div>
                <div class="flex justify-end gap-2">
                  <button hlmBtn type="button" variant="outline" (click)="editing.set(false)">
                    Abbrechen
                  </button>
                  <button
                    hlmBtn
                    type="submit"
                    data-testid="space-save"
                    [disabled]="spaceForm().submitting()"
                  >
                    Speichern
                  </button>
                </div>
              </div>
            </div>
          </form>
        } @else {
          <div class="flex flex-wrap items-start justify-between gap-4">
            <div class="flex flex-col gap-1">
              <h1 class="text-2xl font-semibold tracking-tight" data-testid="space-title">
                @if (space.icon) {
                  <span aria-hidden="true">{{ space.icon }} </span>
                }
                {{ space.name }}
              </h1>
              @if (space.description) {
                <p class="text-sm text-muted-foreground" data-testid="space-description-text">
                  {{ space.description }}
                </p>
              }
            </div>
            @if (canWrite()) {
              <button
                hlmBtn
                type="button"
                variant="outline"
                data-testid="space-edit"
                (click)="startEdit()"
              >
                <ng-icon name="lucidePencil" />
                Bearbeiten
              </button>
            }
          </div>
        }

        <div class="flex flex-col gap-3">
          <div class="flex flex-wrap items-center justify-between gap-4">
            <h2 class="text-lg font-semibold tracking-tight">Collections</h2>
            @if (canWrite()) {
              <button
                hlmBtn
                type="button"
                variant="outline"
                size="sm"
                data-testid="collection-new"
                (click)="creatingCollection.set(!creatingCollection())"
              >
                <ng-icon name="lucidePlus" />
                Neue Collection
              </button>
            }
          </div>
          @if (creatingCollection()) {
            <form
              hlmCard
              novalidate
              data-testid="collection-form"
              (submit)="onCreateCollection($event)"
            >
              <div hlmCardContent>
                <div hlmFieldGroup class="gap-5">
                  @if (collectionError(); as message) {
                    <div hlmAlert variant="destructive" data-testid="collection-form-error">
                      <ng-icon name="lucideCircleAlert" />
                      <p hlmAlertDescription class="whitespace-pre-line">{{ message }}</p>
                    </div>
                  }
                  <div hlmField class="gap-2">
                    <label hlmFieldLabel for="collection-name">Name</label>
                    <input
                      hlmInput
                      id="collection-name"
                      type="text"
                      data-testid="collection-name"
                      [formField]="collectionForm.name"
                    />
                    <div data-testid="collection-name-errors">
                      @if (collectionForm.name().touched() && collectionForm.name().invalid()) {
                        <ag-field-errors [errors]="collectionForm.name().errors()" />
                      }
                    </div>
                  </div>
                  <div hlmField class="gap-2">
                    <label hlmFieldLabel for="collection-description"
                      >Beschreibung (für Agenten: was gehört hier hinein?)</label
                    >
                    <textarea
                      hlmTextarea
                      id="collection-description"
                      rows="3"
                      data-testid="collection-description"
                      [formField]="collectionForm.description"
                    ></textarea>
                    <div data-testid="collection-description-errors">
                      @if (
                        collectionForm.description().touched() &&
                        collectionForm.description().invalid()
                      ) {
                        <ag-field-errors [errors]="collectionForm.description().errors()" />
                      }
                    </div>
                  </div>
                  <div class="flex justify-end">
                    <button
                      hlmBtn
                      type="submit"
                      data-testid="collection-submit"
                      [disabled]="collectionForm().submitting()"
                    >
                      Anlegen
                    </button>
                  </div>
                </div>
              </div>
            </form>
          }
          @if (collections().length > 0) {
            <ul class="divide-y rounded-xl border" data-testid="collection-list">
              @for (collection of collections(); track collection.id) {
                <li>
                  <a
                    [routerLink]="['/w', context.slug(), 'collections', collection.id]"
                    class="block px-4 py-3 outline-none transition-colors hover:bg-muted/50 focus-visible:bg-muted/50"
                    data-testid="collection-item"
                  >
                    <span class="font-medium">{{ collection.name }}</span>
                    <span class="block text-sm text-muted-foreground">{{
                      collection.description
                    }}</span>
                  </a>
                </li>
              }
            </ul>
          } @else {
            <div
              class="flex flex-col items-center gap-1 rounded-xl border border-dashed p-10 text-center"
              data-testid="collection-list"
            >
              <p class="font-medium">Noch keine Collections</p>
              <p class="text-sm text-muted-foreground">
                Collections bündeln Records mit gleichem Aufbau.
              </p>
            </div>
          }
        </div>

        <div class="flex flex-col gap-3">
          <div class="flex flex-wrap items-center justify-between gap-4">
            <h2 class="text-lg font-semibold tracking-tight">Seiten</h2>
            @if (canWrite()) {
              <button
                hlmBtn
                type="button"
                variant="outline"
                size="sm"
                data-testid="page-new"
                (click)="creatingPage.set(true)"
              >
                <ng-icon name="lucidePlus" />
                Neue Seite
              </button>
            }
          </div>
          @if (pages.error(); as message) {
            <div hlmAlert variant="destructive" data-testid="pages-error">
              <ng-icon name="lucideCircleAlert" />
              <p hlmAlertDescription>{{ message }}</p>
            </div>
          }
          @if (pages.items().length > 0) {
            <ul class="divide-y rounded-xl border" data-testid="page-list">
              @for (page of pages.items(); track page.id) {
                <li>
                  <a
                    [routerLink]="['/w', context.slug(), 'records', page.id]"
                    class="flex items-baseline justify-between gap-4 px-4 py-3 outline-none transition-colors hover:bg-muted/50 focus-visible:bg-muted/50"
                    data-testid="page-item"
                  >
                    <span class="font-medium">{{ page.title }}</span>
                    <span class="text-sm text-muted-foreground">{{
                      page.updatedAt | date: 'dd.MM.yyyy HH:mm'
                    }}</span>
                  </a>
                </li>
              }
            </ul>
          } @else {
            <div
              class="flex flex-col items-center gap-1 rounded-xl border border-dashed p-10 text-center"
              data-testid="page-list"
            >
              <p class="font-medium">Noch keine freien Seiten</p>
              <p class="text-sm text-muted-foreground">
                Freie Seiten gehören direkt zum Space, ohne Collection.
              </p>
            </div>
          }
          @if (pages.hasMore()) {
            <div>
              <button
                hlmBtn
                type="button"
                variant="outline"
                data-testid="pages-more"
                [disabled]="pages.loadingMore()"
                (click)="pages.loadMore()"
              >
                Mehr laden
              </button>
            </div>
          }
        </div>
      } @else if (tree.error()) {
        <div hlmAlert variant="destructive" data-testid="load-error">
          <ng-icon name="lucideCircleAlert" />
          <p hlmAlertDescription>Der Space konnte nicht geladen werden.</p>
        </div>
      } @else if (!tree.isLoading()) {
        <p class="text-sm text-muted-foreground" data-testid="not-found">Space nicht gefunden.</p>
      } @else {
        <div class="flex flex-col gap-3" aria-busy="true" aria-label="Wird geladen">
          <hlm-skeleton class="h-4 w-48" />
          <hlm-skeleton class="h-8 w-72" />
        </div>
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
