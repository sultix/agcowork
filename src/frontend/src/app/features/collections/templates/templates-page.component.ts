import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { Router } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideCircleAlert,
  lucideCircleCheck,
  lucideEllipsis,
  lucideInfo,
  lucidePencil,
  lucidePlus,
  lucideStar,
  lucideTrash2,
} from '@ng-icons/lucide';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmDropdownMenuImports } from '@spartan-ng/helm/dropdown-menu';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../../core/api/api-client';
import { WorkspaceContext } from '../../../core/workspace/workspace-context.service';
import type { CanLeave } from '../../records/record-leave.guard';
import { BadgeComponent } from '../../../shared/ui/badge.component';
import { BreadcrumbsComponent, Crumb } from '../../../shared/ui/breadcrumbs.component';
import { ConfirmDialogComponent } from '../../../shared/ui/confirm-dialog.component';
import { describeError } from '../../../shared/ui/api-error-text';
import { WorkspaceTreeStore } from '../../spaces/workspace-tree.store';
import { CollectionSettingsNavComponent } from '../collection-settings-nav.component';
import { TemplateDialogComponent } from './template-dialog.component';
import {
  TemplateDto,
  TemplateSeed,
  bodyExcerpt,
  presetNames,
  seedFromRecord,
} from './template-draft';
import { describeTemplateError } from './template-errors';
import { TemplatesStore } from './templates.store';

/** The open editor: the template (`null` = new) or a draft taken over from a record. */
interface OpenDialog {
  readonly original: TemplateDto | null;
  readonly seed: TemplateSeed | null;
}

/**
 * `/w/:slug/settings/collections/:collectionId/templates`: the record templates of a collection
 * (name, description, preset fields, start of the body). With the `schema` scope (active
 * workspace and collection) templates are created and edited ({@link TemplateDialogComponent}),
 * made the default and deleted (after a confirmation); without it the page is read-only.
 *
 * Query parameter `fromRecord`: the record's values and body open as a draft for a new template
 * ("Als Vorlage speichern" in the document view).
 */
@Component({
  selector: 'ag-templates-page',
  imports: [
    NgIcon,
    HlmAlertImports,
    HlmButton,
    HlmDropdownMenuImports,
    HlmSkeleton,
    HlmTableImports,
    BadgeComponent,
    BreadcrumbsComponent,
    CollectionSettingsNavComponent,
    ConfirmDialogComponent,
    TemplateDialogComponent,
  ],
  providers: [
    TemplatesStore,
    provideIcons({
      lucideCircleAlert,
      lucideCircleCheck,
      lucideEllipsis,
      lucideInfo,
      lucidePencil,
      lucidePlus,
      lucideStar,
      lucideTrash2,
    }),
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(window:beforeunload)': 'onBeforeUnload($event)' },
  template: `
    <section class="mx-auto flex max-w-6xl flex-col gap-6 p-6">
      @if (store.loadError(); as message) {
        <div hlmAlert variant="destructive" data-testid="load-error">
          <ng-icon name="lucideCircleAlert" />
          <p hlmAlertDescription>{{ message }}</p>
        </div>
      } @else if (store.collection(); as collection) {
        <ag-breadcrumbs [crumbs]="crumbs()" />
        <div class="flex flex-wrap items-start justify-between gap-4">
          <div class="flex flex-col gap-1">
            <h1 class="text-2xl font-semibold tracking-tight" data-testid="templates-title">
              Vorlagen: {{ collection.name }}
            </h1>
            <p class="text-sm text-muted-foreground">
              Neue Records starten mit den vorbelegten Werten und dem Text einer Vorlage. Ohne
              Auswahl gilt die Standardvorlage, auch für Agenten.
            </p>
          </div>
          @if (store.canEdit()) {
            <button hlmBtn type="button" data-testid="template-new" (click)="openCreate()">
              <ng-icon name="lucidePlus" />
              Vorlage anlegen
            </button>
          }
        </div>
        <ag-collection-settings-nav [collectionId]="collectionId()" active="templates" />

        @if (store.readOnlyReason(); as reason) {
          <div hlmAlert role="status" data-testid="readonly-notice">
            <ng-icon name="lucideInfo" />
            <p hlmAlertDescription>{{ reason }}</p>
          </div>
        }
        @if (notice(); as message) {
          <div hlmAlert role="status" data-testid="notice">
            <ng-icon name="lucideCircleCheck" />
            <p hlmAlertDescription>{{ message }}</p>
          </div>
        }
        @if (actionError(); as message) {
          <div hlmAlert variant="destructive" data-testid="action-error">
            <ng-icon name="lucideCircleAlert" />
            <p hlmAlertDescription class="whitespace-pre-line">{{ message }}</p>
          </div>
        }

        @if (store.templates(); as templates) {
          <div class="rounded-xl border">
            <div hlmTableContainer>
              <table hlmTable data-testid="templates-table">
                <caption class="sr-only">
                  Vorlagen der Collection
                  {{
                    collection.name
                  }}
                </caption>
                <thead hlmTHead>
                  <tr hlmTr>
                    <th hlmTh class="px-4">Name</th>
                    <th hlmTh>Beschreibung</th>
                    <th hlmTh>Vorbelegt</th>
                    <th hlmTh>Text</th>
                    @if (store.canEdit()) {
                      <th hlmTh class="w-12"><span class="sr-only">Aktionen</span></th>
                    }
                  </tr>
                </thead>
                <tbody hlmTBody>
                  @for (template of templates; track template.id) {
                    <tr hlmTr data-testid="template-row" [attr.data-id]="template.id">
                      <td hlmTd class="px-4 align-top font-medium whitespace-normal">
                        <div class="flex flex-wrap items-center gap-2">
                          <span data-testid="template-row-name">{{ template.name }}</span>
                          @if (template.isDefault) {
                            <ag-badge tone="accent"
                              ><span data-testid="default-badge">Standard</span></ag-badge
                            >
                          }
                        </div>
                      </td>
                      <td
                        hlmTd
                        class="max-w-xs align-top whitespace-normal text-muted-foreground"
                        data-testid="template-row-description"
                      >
                        {{ template.description || '–' }}
                      </td>
                      <td hlmTd class="align-top whitespace-normal">
                        @if (presetOf(template); as names) {
                          @if (names.length > 0) {
                            <span data-testid="preset-count" [attr.title]="names.join(', ')">{{
                              names.length === 1 ? '1 Feld' : names.length + ' Felder'
                            }}</span>
                            <span class="sr-only">: {{ names.join(', ') }}</span>
                          } @else {
                            <span class="text-muted-foreground" data-testid="preset-count">–</span>
                          }
                        }
                      </td>
                      <td
                        hlmTd
                        class="max-w-sm align-top whitespace-normal text-muted-foreground"
                        data-testid="template-excerpt"
                      >
                        {{ excerpt(template) || '–' }}
                      </td>
                      @if (store.canEdit()) {
                        <td hlmTd class="text-right align-top">
                          <button
                            hlmBtn
                            type="button"
                            variant="ghost"
                            size="icon-sm"
                            data-testid="template-actions"
                            [attr.aria-label]="'Aktionen für ' + template.name"
                            [hlmDropdownMenuTrigger]="actionMenu"
                            [hlmDropdownMenuTriggerData]="{ template }"
                            align="end"
                          >
                            <ng-icon name="lucideEllipsis" />
                          </button>
                        </td>
                      }
                    </tr>
                  } @empty {
                    <tr hlmTr>
                      <td
                        hlmTd
                        class="px-4 py-6 text-center text-muted-foreground"
                        [attr.colspan]="store.canEdit() ? 5 : 4"
                        data-testid="templates-empty"
                      >
                        Noch keine Vorlagen. Neue Records starten dann leer.
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          </div>
        }
      } @else {
        <div class="flex flex-col gap-3" aria-busy="true" aria-label="Wird geladen">
          <hlm-skeleton class="h-4 w-48" />
          <hlm-skeleton class="h-8 w-72" />
          <hlm-skeleton class="h-10 rounded-lg" />
          <hlm-skeleton class="h-10 rounded-lg" />
        </div>
      }
    </section>

    <ng-template #actionMenu let-template="template">
      <hlm-dropdown-menu class="w-52" data-testid="template-menu">
        <button hlmDropdownMenuItem data-testid="template-edit" (triggered)="openEdit(template)">
          <ng-icon name="lucidePencil" />
          Bearbeiten
        </button>
        @if (!template.isDefault) {
          <button
            hlmDropdownMenuItem
            data-testid="template-make-default"
            (triggered)="makeDefault(template)"
          >
            <ng-icon name="lucideStar" />
            Als Standard setzen
          </button>
        }
        <hlm-dropdown-menu-separator />
        <button
          hlmDropdownMenuItem
          variant="destructive"
          data-testid="template-remove"
          (triggered)="askRemove(template)"
        >
          <ng-icon name="lucideTrash2" />
          Löschen
        </button>
      </hlm-dropdown-menu>
    </ng-template>

    @if (dialog(); as open) {
      <ag-template-dialog
        [original]="open.original"
        [seed]="open.seed"
        (saved)="onSaved($event)"
        (cancelled)="closeDialog()"
      />
    }

    <ag-confirm-dialog
      [open]="leaveRequest() !== null"
      title="Änderungen verwerfen und verlassen?"
      message="Die Vorlage im offenen Editor wurde geändert, aber nicht gespeichert."
      confirmLabel="Verwerfen und verlassen"
      (confirmed)="answerLeave(true)"
      (cancelled)="answerLeave(false)"
    />

    <ag-confirm-dialog
      [open]="removal() !== null"
      title="Vorlage löschen?"
      [message]="removeMessage()"
      confirmLabel="Löschen"
      [busy]="busy()"
      (confirmed)="confirmRemove()"
      (cancelled)="removal.set(null)"
    />
  `,
})
export class TemplatesPageComponent implements CanLeave {
  protected readonly store = inject(TemplatesStore);
  private readonly context = inject(WorkspaceContext);
  private readonly tree = inject(WorkspaceTreeStore);
  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);

  /** Route parameter `:collectionId`. */
  readonly collectionId = input.required<string>();
  /** Query parameter `fromRecord`: a record whose values and body start a new template. */
  readonly fromRecord = input<string | undefined>();

  protected readonly dialog = signal<OpenDialog | null>(null);
  protected readonly removal = signal<TemplateDto | null>(null);
  private readonly editor = viewChild(TemplateDialogComponent);
  /** Open "discard and leave?" question; `resolve` answers the pending `canDeactivate`. */
  protected readonly leaveRequest = signal<{ resolve: (leave: boolean) => void } | null>(null);
  protected readonly busy = signal(false);
  /** Status line after a change (announced politely). */
  protected readonly notice = signal<string | null>(null);
  protected readonly actionError = signal<string | null>(null);

  /** The record whose draft was opened already, so a reload does not open it a second time. */
  private takenOver: string | null = null;

  protected readonly crumbs = computed<Crumb[]>(() => {
    const collection = this.store.collection();
    const slug = this.context.slug();
    if (!collection || !slug) {
      return [];
    }
    return [
      {
        label: this.tree.findSpace(collection.spaceId)?.name ?? 'Space',
        link: ['/w', slug, 'spaces', collection.spaceId],
      },
      { label: collection.name, link: ['/w', slug, 'collections', collection.id] },
      { label: 'Vorlagen' },
    ];
  });

  protected readonly removeMessage = computed(() => {
    const template = this.removal();
    return template
      ? `Die Vorlage „${template.name}“ wird gelöscht${template.isDefault ? ' und die Collection hat danach keine Standardvorlage mehr' : ''}. Records, die aus ihr entstanden sind, behalten ihren Inhalt.`
      : '';
  });

  constructor() {
    effect(() => {
      const id = this.collectionId();
      untracked(() => {
        this.store.connect(id);
        this.dialog.set(null);
        this.removal.set(null);
        this.notice.set(null);
        this.actionError.set(null);
        this.takenOver = null;
      });
    });
    effect(() => {
      const recordId = this.fromRecord();
      const ready = this.store.canEdit();
      const loaded = !!this.store.collection();
      untracked(() => {
        if (!recordId) {
          this.takenOver = null;
        } else if (!ready && loaded && !this.takenOver) {
          // read-only view (no right, archived): there is nothing to take over
          void this.clearQuery();
        } else if (ready && this.takenOver !== recordId) {
          this.takenOver = recordId;
          void this.openFromRecord(recordId);
        }
      });
    });
  }

  /** Asks before leaving with a changed, unsaved template in the editor. */
  canLeave(): Promise<boolean> {
    if (!this.editor()?.isDirty()) {
      return Promise.resolve(true);
    }
    return new Promise((resolve) => this.leaveRequest.set({ resolve }));
  }

  protected answerLeave(leave: boolean): void {
    this.leaveRequest()?.resolve(leave);
    this.leaveRequest.set(null);
  }

  protected onBeforeUnload(event: BeforeUnloadEvent): void {
    if (this.editor()?.isDirty()) {
      event.preventDefault();
    }
  }

  protected presetOf(template: TemplateDto): string[] {
    return presetNames(template, this.store.schema());
  }

  protected excerpt(template: TemplateDto): string {
    return bodyExcerpt(template.bodyMarkdown);
  }

  protected openCreate(): void {
    this.clearMessages();
    this.dialog.set({ original: null, seed: null });
  }

  protected openEdit(template: TemplateDto): void {
    this.clearMessages();
    this.dialog.set({ original: template, seed: null });
  }

  private async openFromRecord(recordId: string): Promise<void> {
    const slug = this.context.slug();
    const collection = this.store.collection();
    if (!slug || !collection) {
      return;
    }
    this.clearMessages();
    try {
      const record = await firstValueFrom(
        this.api.workspace(slug).get('/records/{recordId}', {
          path: { recordId },
          query: { includeBody: true },
        }),
      );
      if (this.fromRecord() !== recordId) {
        return; // the user went elsewhere meanwhile
      }
      if (record.collectionId !== collection.id) {
        this.actionError.set(
          'Der Record gehört zu einer anderen Collection; es wurde nichts übernommen.',
        );
        void this.clearQuery();
        return;
      }
      this.dialog.set({
        original: null,
        seed: seedFromRecord(collection.schema, record),
      });
    } catch (error) {
      this.actionError.set(describeError(error));
      void this.clearQuery();
    }
  }

  protected onSaved(message: string): void {
    this.closeDialog();
    this.notice.set(message);
  }

  protected closeDialog(): void {
    this.dialog.set(null);
    if (this.fromRecord()) {
      void this.clearQuery();
    }
  }

  /** Drops `fromRecord` so a reload or "back" does not open the draft again. */
  private clearQuery(): Promise<boolean> {
    return this.router.navigate([], {
      queryParams: { fromRecord: null },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  protected async makeDefault(template: TemplateDto): Promise<void> {
    this.clearMessages();
    try {
      await this.store.update(template.id, { isDefault: true });
      this.notice.set(`„${template.name}“ ist jetzt die Standardvorlage.`);
    } catch (error) {
      this.actionError.set(describeTemplateError(error));
    }
  }

  protected askRemove(template: TemplateDto): void {
    this.clearMessages();
    this.removal.set(template);
  }

  protected async confirmRemove(): Promise<void> {
    const template = this.removal();
    if (!template || this.busy()) {
      return;
    }
    this.busy.set(true);
    try {
      await this.store.remove(template.id);
      this.notice.set(`Vorlage „${template.name}“ gelöscht.`);
    } catch (error) {
      this.actionError.set(describeTemplateError(error));
    } finally {
      this.busy.set(false);
      this.removal.set(null);
    }
  }

  private clearMessages(): void {
    this.notice.set(null);
    this.actionError.set(null);
  }
}
