import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  resource,
  signal,
  untracked,
  viewChild,
  viewChildren,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError } from '../../core/api/api-error';
import type { components } from '../../core/api/generated/schema';
import { MembersStore } from '../../core/workspace/members.store';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { MarkdownEditorComponent } from '../../shared/editor/markdown-editor.component';
import { PropertyEditorComponent } from '../../shared/properties/property-editor.component';
import { RawViewComponent } from '../../shared/raw-view/raw-view.component';
import { describeError } from '../../shared/ui/api-error-text';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { BreadcrumbsComponent, Crumb } from '../../shared/ui/breadcrumbs.component';
import { ConfirmDialogComponent } from '../../shared/ui/confirm-dialog.component';
import { WorkspacesStore } from '../workspaces/workspaces.store';
import { WorkspaceTreeStore } from '../spaces/workspace-tree.store';
import { RecordSaveService } from './record-save.service';

type Collection = components['schemas']['CollectionDto'];

/**
 * `/w/:slug/records/:recordId`: a record as a document. Title, property panel and Markdown body
 * are edited in place; `RecordSaveService` saves them (button, Ctrl/Cmd+S, 2 s auto-save) with
 * the expected revision and reports conflicts, which this view lets the user resolve.
 */
@Component({
  selector: 'ag-record-document',
  imports: [
    DatePipe,
    BadgeComponent,
    BreadcrumbsComponent,
    ConfirmDialogComponent,
    MarkdownEditorComponent,
    PropertyEditorComponent,
    RawViewComponent,
  ],
  providers: [RecordSaveService],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '(document:keydown)': 'onKeydown($event)',
    '(window:beforeunload)': 'onBeforeUnload($event)',
  },
  template: `
    <section class="mx-auto max-w-4xl p-6">
      @if (saver.loadError(); as message) {
        <p class="text-sm text-danger" role="alert" data-testid="load-error">{{ message }}</p>
      } @else if (saver.record(); as record) {
        <ag-breadcrumbs [crumbs]="crumbs()" />

        <input
          type="text"
          class="mt-2 w-full border-b border-transparent bg-transparent text-2xl font-semibold focus:border-border focus:outline-none"
          aria-label="Titel"
          data-testid="record-title"
          [value]="saver.title()"
          [readOnly]="readonly()"
          (input)="saver.setTitle($any($event.target).value)"
        />
        @for (message of saver.fieldErrors()['title'] ?? []; track $index) {
          <p class="mt-1 text-sm text-danger" data-testid="title-error">{{ message }}</p>
        }

        <div
          class="mt-2 flex flex-wrap items-center gap-3 text-sm text-muted"
          data-testid="record-meta"
        >
          <span data-testid="record-revision">Revision {{ record.revision }}</span>
          <span>
            aktualisiert {{ record.updatedAt | date: 'dd.MM.yyyy HH:mm' }} von
            {{ members.nameOf(record.updatedBy) }}
          </span>
          @if (record.archivedAt) {
            <ag-badge tone="danger"><span data-testid="archived-badge">archiviert</span></ag-badge>
          }
          <span data-testid="save-status" role="status">{{ saveStatus() }}</span>
        </div>

        <div class="mt-3 flex flex-wrap gap-2">
          @if (!readonly()) {
            <button
              type="button"
              class="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60"
              data-testid="save"
              [disabled]="saver.saving() || !saver.isDirty()"
              (click)="saveNow()"
            >
              Speichern
            </button>
          }
          <button
            type="button"
            class="rounded-md border border-border px-3 py-1.5 text-sm hover:bg-surface-hover"
            data-testid="show-markdown"
            (click)="showMarkdown()"
          >
            Als Markdown anzeigen
          </button>
          @if (canWrite()) {
            <button
              type="button"
              class="rounded-md border border-border px-3 py-1.5 text-sm hover:bg-surface-hover"
              data-testid="archive-toggle"
              (click)="confirming.set(true)"
            >
              {{ record.archivedAt ? 'Wiederherstellen' : 'Archivieren' }}
            </button>
          }
        </div>

        @if (saver.saveError() ?? actionError(); as message) {
          <p
            class="mt-3 rounded-md border border-danger px-3 py-2 text-sm whitespace-pre-line text-danger"
            role="alert"
            data-testid="save-error"
          >
            {{ message }}
            @if (saver.bodyStale() !== null) {
              <button
                type="button"
                class="ml-2 rounded-md border border-danger px-2 py-0.5"
                data-testid="reload-stale"
                (click)="saver.reload()"
              >
                Neu laden
              </button>
            }
          </p>
        }
        @if (readonly() && record.archivedAt) {
          <p class="mt-3 text-sm text-muted" data-testid="readonly-notice">
            Archiviert und schreibgeschützt.
          </p>
        } @else if (readonly()) {
          <p class="mt-3 text-sm text-muted" data-testid="readonly-notice">
            Du hast hier nur Leserechte.
          </p>
        }

        @for (version of [saver.loadCount()]; track version) {
          @if (visibleDefinitions().length > 0) {
            <dl
              class="mt-6 grid grid-cols-[10rem_1fr] gap-x-4 gap-y-3"
              data-testid="properties-panel"
            >
              @for (def of visibleDefinitions(); track def.key) {
                <dt class="pt-1 text-sm text-muted" [attr.title]="def.description">
                  {{ def.name }}
                  @if (def.required) {
                    <span aria-hidden="true"> *</span>
                  }
                </dt>
                <dd class="min-w-0" data-testid="property-row" [attr.data-key]="def.key">
                  <ag-property-editor
                    [definition]="def"
                    [value]="saver.properties()[def.key]"
                    [readonly]="readonly()"
                    [errors]="saver.fieldErrors()[def.key] ?? []"
                    (valueChange)="saver.setProperty(def.key, $event)"
                  />
                </dd>
              }
            </dl>
          }
        }

        <div class="mt-6" data-testid="record-body" (focusin)="captureBaseline()">
          @for (version of [saver.loadCount()]; track version) {
            <ag-markdown-editor
              class="block min-h-64 rounded-md border border-border"
              [value]="saver.editorValue()"
              [readonly]="readonly()"
              (valueChange)="onBody($event)"
            />
          }
        </div>
      } @else {
        <p class="text-sm text-muted">Wird geladen …</p>
      }
    </section>

    @if (saver.conflict(); as conflict) {
      <div class="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
        <div
          class="w-full max-w-md rounded-lg border border-border bg-surface p-6"
          role="alertdialog"
          aria-modal="true"
          aria-labelledby="conflict-title"
          data-testid="conflict-dialog"
        >
          <h2 id="conflict-title" class="text-lg font-semibold">Von jemand anderem geändert</h2>
          <p class="mt-2 text-sm text-muted">
            Der Record wurde gespeichert, seit du ihn geöffnet hast
            @if (conflict.currentRevision !== null) {
              (jetzt Revision {{ conflict.currentRevision }})
            }
            .
          </p>
          @if (conflict.changedKeys?.length) {
            <p class="mt-2 text-sm">Geändert wurde:</p>
            <ul class="mt-1 flex flex-wrap gap-1" data-testid="conflict-keys">
              @for (key of conflict.changedKeys; track key) {
                <li>
                  <ag-badge>{{ key }}</ag-badge>
                </li>
              }
            </ul>
          }
          <p class="mt-3 text-sm text-muted">
            „Neu laden“ verwirft deine Änderungen. „Überschreiben“ speichert sie über den neuen
            Stand.
          </p>
          <div class="mt-6 flex justify-end gap-2">
            <button
              type="button"
              class="rounded-md border border-border px-3 py-1.5 text-sm hover:bg-surface-hover"
              data-testid="conflict-reload"
              (click)="saver.reload()"
            >
              Neu laden
            </button>
            <button
              type="button"
              class="rounded-md bg-danger px-3 py-1.5 text-sm font-medium text-white disabled:opacity-60"
              data-testid="conflict-overwrite"
              [disabled]="saver.saving()"
              (click)="saver.overwrite()"
            >
              Überschreiben
            </button>
          </div>
        </div>
      </div>
    }

    <ag-confirm-dialog
      [open]="confirming()"
      [title]="saver.record()?.archivedAt ? 'Record wiederherstellen?' : 'Record archivieren?'"
      [message]="
        saver.record()?.archivedAt
          ? 'Der Record ist danach wieder bearbeitbar und erscheint in Listen.'
          : 'Der Record wird schreibgeschützt und aus Listen ausgeblendet. Er kann wiederhergestellt werden.'
      "
      [confirmLabel]="saver.record()?.archivedAt ? 'Wiederherstellen' : 'Archivieren'"
      [busy]="busy()"
      (confirmed)="toggleArchived()"
      (cancelled)="confirming.set(false)"
    />

    <ag-confirm-dialog
      [open]="leaveRequest() !== null"
      title="Änderungen verwerfen und verlassen?"
      [message]="leaveRequest()?.message ?? ''"
      confirmLabel="Verwerfen und verlassen"
      (confirmed)="answerLeave(true)"
      (cancelled)="answerLeave(false)"
    />

    @if (markdownView() !== null) {
      <div class="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
        <div
          class="flex max-h-full w-full max-w-3xl flex-col rounded-lg border border-border bg-surface p-4"
          role="dialog"
          aria-modal="true"
          aria-label="Markdown"
          data-testid="markdown-dialog"
        >
          <ag-raw-view language="markdown" [readonly]="true" [value]="markdownView() ?? ''" />
          <button
            type="button"
            class="mt-3 self-end rounded-md border border-border px-3 py-1.5 text-sm hover:bg-surface-hover"
            data-testid="markdown-close"
            (click)="markdownView.set(null)"
          >
            Schließen
          </button>
        </div>
      </div>
    }
  `,
})
export class RecordDocumentComponent {
  protected readonly saver = inject(RecordSaveService);
  protected readonly members = inject(MembersStore);
  private readonly api = inject(ApiClient);
  private readonly http = inject(HttpClient);
  private readonly context = inject(WorkspaceContext);
  private readonly workspaces = inject(WorkspacesStore);
  private readonly tree = inject(WorkspaceTreeStore);

  /** Route parameter `:recordId`. */
  readonly recordId = input.required<string>();

  private readonly editor = viewChild(MarkdownEditorComponent);
  private readonly propertyEditors = viewChildren(PropertyEditorComponent);

  protected readonly confirming = signal(false);
  protected readonly busy = signal(false);
  protected readonly actionError = signal<string | null>(null);
  protected readonly markdownView = signal<string | null>(null);
  /** Open "discard and leave?" question; `resolve` answers the pending `canDeactivate`. */
  protected readonly leaveRequest = signal<{
    message: string;
    resolve: (leave: boolean) => void;
  } | null>(null);

  /**
   * Markdown of the editor as of the user's last interaction (focus or reported change). Text
   * the editor has not reported yet (debounce window) differs from it. `null` until the user
   * focuses the editor: before that nothing can have been typed, and the editor's own
   * serialization of an unchanged body must not count as an edit.
   */
  private baseline: string | null = null;

  /** Stable id of the record's collection: the resource below must not reload on every save. */
  private readonly collectionId = computed(() => this.saver.record()?.collectionId ?? null);

  private readonly collection = resource<Collection, { slug: string; id: string } | undefined>({
    params: () => {
      const slug = this.context.slug();
      const id = this.collectionId();
      return slug && id ? { slug, id } : undefined;
    },
    loader: ({ params }) =>
      firstValueFrom(
        this.api
          .workspace(params.slug)
          .get('/collections/{collectionId}', { path: { collectionId: params.id } }),
      ),
  });

  protected readonly canWrite = computed(() => {
    const ws = this.workspaces.find(this.context.slug());
    return !!ws && !ws.archivedAt && ws.scopes.includes('write');
  });
  protected readonly readonly = computed(
    () => !this.canWrite() || !!this.saver.record()?.archivedAt,
  );

  protected readonly visibleDefinitions = computed(() => {
    const collection = this.collection.hasValue() ? this.collection.value() : undefined;
    return (collection?.schema ?? []).filter((d) => d.key !== collection?.titlePropertyKey);
  });

  protected readonly crumbs = computed<Crumb[]>(() => {
    const record = this.saver.record();
    const slug = this.context.slug();
    if (!record || !slug) {
      return [];
    }
    const crumbs: Crumb[] = [
      {
        label: this.tree.findSpace(record.spaceId)?.name ?? 'Space',
        link: ['/w', slug, 'spaces', record.spaceId],
      },
    ];
    if (record.collectionId) {
      crumbs.push({
        label: this.tree.findCollection(record.collectionId)?.name ?? 'Collection',
        link: ['/w', slug, 'collections', record.collectionId],
      });
    }
    crumbs.push({ label: this.saver.title() || 'Ohne Titel' });
    return crumbs;
  });

  protected readonly saveStatus = computed(() => {
    if (this.saver.saving()) {
      return 'Speichert …';
    }
    if (this.saver.isDirty()) {
      return 'Ungespeicherte Änderungen';
    }
    return this.saver.savedAt() ? 'Gespeichert' : '';
  });

  constructor() {
    this.saver.beforeSave = () => this.pullPending();

    effect(() => {
      const slug = this.context.slug();
      const id = this.recordId();
      if (slug && id) {
        untracked(() => void this.saver.load(slug, id));
      }
    });
    effect(() => {
      this.saver.loadCount();
      this.baseline = null;
    });
  }

  /**
   * For the `canDeactivate` guard: saves pending edits and lets the user leave. When saving
   * cannot succeed (offline, server error, archived meanwhile) or input is invalid and thus not
   * saved, the user is asked whether to discard; a revision conflict keeps the page (its dialog
   * is showing).
   */
  async canLeave(): Promise<boolean> {
    if (this.readonly()) {
      return true;
    }
    let saved = await this.saver.save();
    // Text typed while saving (or still debounced) needs its own save; bounded, then ask.
    for (let round = 0; round < 3 && saved; round++) {
      this.pullPending();
      if (!this.saver.isDirty()) {
        break;
      }
      saved = await this.saver.save();
    }
    this.pullPending();
    saved = saved && !this.saver.isDirty();
    const invalid = this.propertyEditors().some((e) => e.invalid());
    if (saved && !invalid) {
      return true;
    }
    if (this.saver.conflict()) {
      return false;
    }
    const message = saved
      ? 'Ungültige Eingaben (z. B. ungültiges JSON) werden nicht gespeichert.'
      : 'Die Änderungen konnten nicht gespeichert werden.';
    return new Promise<boolean>((resolve) => this.leaveRequest.set({ message, resolve }));
  }

  protected answerLeave(leave: boolean): void {
    this.leaveRequest()?.resolve(leave);
    this.leaveRequest.set(null);
  }

  /** Warns before closing or reloading the tab while there are unsaved edits. */
  protected onBeforeUnload(event: BeforeUnloadEvent): void {
    this.pullPending();
    if (
      !this.readonly() &&
      (this.saver.isDirty() || this.propertyEditors().some((e) => e.invalid()))
    ) {
      event.preventDefault();
    }
  }

  /** Saves pending edits (no questions asked); `false` when that failed. */
  async flushPending(): Promise<boolean> {
    if (this.readonly()) {
      return true;
    }
    return this.saver.save();
  }

  protected async saveNow(): Promise<void> {
    if (!this.readonly()) {
      await this.saver.save();
    }
  }

  protected onKeydown(event: KeyboardEvent): void {
    if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 's') {
      event.preventDefault();
      void this.saveNow();
    }
  }

  protected captureBaseline(): void {
    if (this.baseline === null) {
      this.baseline = this.editor()?.getMarkdown() ?? null;
    }
  }

  protected onBody(markdown: string): void {
    this.baseline = markdown;
    this.saver.setBody(markdown);
  }

  /** Pulls text still within an editor's debounce window (body, Markdown properties) into the save. */
  private pullPending(): void {
    if (this.readonly()) {
      return;
    }
    this.propertyEditors().forEach((e) => e.flush());
    this.syncBody();
  }

  private syncBody(): void {
    const editor = this.editor();
    if (!editor || this.readonly() || this.baseline === null) {
      return;
    }
    const markdown = editor.getMarkdown();
    if (markdown !== this.baseline) {
      this.onBody(markdown);
    }
  }

  protected async showMarkdown(): Promise<void> {
    const slug = this.context.slug();
    if (!slug) {
      return;
    }
    this.actionError.set(null);
    if (!(await this.flushPending())) {
      return;
    }
    try {
      const url = this.api
        .workspace(slug)
        .url('/records/{recordId}', { recordId: this.recordId() });
      this.markdownView.set(
        await firstValueFrom(
          this.http.get(url, {
            params: { format: 'markdown' },
            headers: { Accept: 'text/markdown' },
            responseType: 'text',
            withCredentials: true,
          }),
        ),
      );
    } catch (error) {
      this.actionError.set(describeError(error));
    }
  }

  protected async toggleArchived(): Promise<void> {
    const slug = this.context.slug();
    const record = this.saver.record();
    if (!slug || !record) {
      return;
    }
    this.busy.set(true);
    this.actionError.set(null);
    try {
      if (!record.archivedAt && !(await this.flushPending())) {
        return;
      }
      const path = { recordId: record.id };
      const updated = record.archivedAt
        ? await firstValueFrom(
            this.api.workspace(slug).post('/records/{recordId}/restore', { path }),
          )
        : await firstValueFrom(
            this.api.workspace(slug).post('/records/{recordId}/archive', { path }),
          );
      this.saver.adopt(updated);
    } catch (error) {
      this.actionError.set(describeError(ApiError.from(error)));
    } finally {
      this.busy.set(false);
      this.confirming.set(false);
    }
  }
}
