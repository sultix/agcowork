import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
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
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideCircleAlert,
  lucideInfo,
  lucideMessageSquare,
  lucideTriangleAlert,
} from '@ng-icons/lucide';
import type { BrnDialogState } from '@spartan-ng/brain/dialog';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmDialogImports } from '@spartan-ng/helm/dialog';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError } from '../../core/api/api-error';
import type { components } from '../../core/api/generated/schema';
import { ActivityEvent, RealtimeService } from '../../core/realtime/realtime.service';
import { MembersStore } from '../../core/workspace/members.store';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { MarkdownEditorComponent } from '../../shared/editor/markdown-editor.component';
import { PropertyEditorComponent } from '../../shared/properties/property-editor.component';
import { RawViewComponent } from '../../shared/raw-view/raw-view.component';
import { describeError } from '../../shared/ui/api-error-text';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { BreadcrumbsComponent, Crumb } from '../../shared/ui/breadcrumbs.component';
import { ConfirmDialogComponent } from '../../shared/ui/confirm-dialog.component';
import { CommentsPanelComponent } from '../comments/comments-panel.component';
import { CommentsStore } from '../comments/comments.store';
import { WorkspacesStore } from '../workspaces/workspaces.store';
import { WorkspaceTreeStore } from '../spaces/workspace-tree.store';
import { ConflictDialogComponent } from './conflict-dialog.component';
import { RecordBacklinksComponent } from './record-backlinks.component';
import { RecordTabsComponent } from './record-tabs.component';
import { RecordDto, RecordSaveService } from './record-save.service';

type Collection = components['schemas']['CollectionDto'];

/** Leave question when comment drafts would be lost. */
const UNSENT_COMMENTS = 'Nicht gesendete Kommentare gehen verloren.';

/** How long "Aktualisiert von …" stays after a silent reload. */
export const REMOTE_HINT_MS = 6000;

/** Someone else changed (or archived) the open record while it has local edits. */
interface RemoteChange {
  readonly kind: 'changed' | 'archived';
  readonly actorId: string;
  /** Server revision of that change; the banner goes once the view has caught up with it. */
  readonly revision: number;
}

let nextDocumentId = 0;

/**
 * `/w/:slug/records/:recordId`: a record as a document. Title, property panel and Markdown body
 * are edited in place; `RecordSaveService` saves them (button, Ctrl/Cmd+S, 2 s auto-save) with
 * the expected revision and reports conflicts, which this view lets the user resolve. Changes by
 * others (realtime events) reload the record silently when there are no local edits; with local
 * edits a banner offers to reload, and the edits are never replaced without the user's choice.
 */
@Component({
  selector: 'ag-record-document',
  imports: [
    DatePipe,
    BadgeComponent,
    HlmAlertImports,
    HlmButton,
    HlmDialogImports,
    HlmInput,
    HlmSkeleton,
    NgIcon,
    BreadcrumbsComponent,
    CommentsPanelComponent,
    ConfirmDialogComponent,
    ConflictDialogComponent,
    MarkdownEditorComponent,
    PropertyEditorComponent,
    RawViewComponent,
    RecordBacklinksComponent,
    RecordTabsComponent,
    RouterLink,
  ],
  providers: [
    RecordSaveService,
    CommentsStore,
    provideIcons({ lucideCircleAlert, lucideInfo, lucideMessageSquare, lucideTriangleAlert }),
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '(document:keydown)': 'onKeydown($event)',
    '(window:beforeunload)': 'onBeforeUnload($event)',
  },
  template: `
    <div class="flex min-w-0 flex-col lg:flex-row lg:items-start">
      <section class="mx-auto w-full max-w-4xl min-w-0 flex-1 p-6">
        @if (saver.loadError(); as message) {
          <div hlmAlert variant="destructive" data-testid="load-error">
            <ng-icon name="lucideCircleAlert" />
            <p hlmAlertDescription>{{ message }}</p>
          </div>
        } @else if (saver.record(); as record) {
          <ag-breadcrumbs [crumbs]="crumbs()" />
          <ag-record-tabs [recordId]="recordId()" />

          <input
            type="text"
            hlmInput
            class="mt-2 h-auto border-transparent bg-transparent px-1 py-1 text-2xl font-semibold shadow-none md:text-2xl dark:bg-transparent"
            aria-label="Titel"
            data-testid="record-title"
            [value]="saver.title()"
            [readOnly]="readonly()"
            (input)="saver.setTitle($any($event.target).value)"
          />
          @for (message of saver.fieldErrors()['title'] ?? []; track $index) {
            <p class="mt-1 text-sm text-destructive" data-testid="title-error">{{ message }}</p>
          }

          <div
            class="mt-2 flex flex-wrap items-center gap-3 text-sm text-muted-foreground"
            data-testid="record-meta"
          >
            <span data-testid="record-revision">Revision {{ record.revision }}</span>
            <span>
              aktualisiert {{ record.updatedAt | date: 'dd.MM.yyyy HH:mm' }} von
              {{ members.nameOf(record.updatedBy) }}
            </span>
            @if (record.archivedAt) {
              <ag-badge tone="danger"
                ><span data-testid="archived-badge">archiviert</span></ag-badge
              >
            }
            <span data-testid="save-status" role="status">{{ saveStatus() }}</span>
          </div>

          <div class="mt-3 flex flex-wrap gap-2">
            @if (!readonly()) {
              <button
                type="button"
                hlmBtn
                data-testid="save"
                [disabled]="saver.saving() || !saver.isDirty()"
                (click)="saveNow()"
              >
                Speichern
              </button>
            }
            <button
              type="button"
              hlmBtn
              variant="outline"
              data-testid="show-markdown"
              (click)="showMarkdown()"
            >
              Als Markdown anzeigen
            </button>
            @if (canWrite()) {
              <button
                type="button"
                hlmBtn
                variant="outline"
                data-testid="archive-toggle"
                (click)="confirming.set(true)"
              >
                {{ record.archivedAt ? 'Wiederherstellen' : 'Archivieren' }}
              </button>
            }
            @if (canSaveAsTemplate() && record.collectionId; as collectionId) {
              <a
                hlmBtn
                variant="outline"
                data-testid="save-as-template"
                [routerLink]="templatesLink(collectionId)"
                [queryParams]="{ fromRecord: record.id }"
              >
                Als Vorlage speichern
              </a>
            }
            <button
              type="button"
              hlmBtn
              variant="outline"
              class="ml-auto"
              data-testid="comments-toggle"
              aria-controls="record-comments"
              [attr.aria-expanded]="comments.open()"
              (click)="comments.setOpen(!comments.open())"
            >
              <ng-icon name="lucideMessageSquare" />
              Kommentare
              @if (comments.openCount() > 0) {
                <span
                  class="rounded-full bg-primary px-1.5 text-xs leading-5 text-primary-foreground"
                  data-testid="comments-count"
                  >{{ comments.openCount() }}</span
                >
                <span class="sr-only">offene Threads</span>
              }
            </button>
          </div>

          @if (saver.saveError() ?? actionError(); as message) {
            <div hlmAlert variant="destructive" class="mt-3" data-testid="save-error">
              <ng-icon name="lucideCircleAlert" />
              <p hlmAlertDescription class="whitespace-pre-line">{{ message }}</p>
              @if (saver.bodyStale() !== null) {
                <button
                  type="button"
                  hlmAlertAction
                  hlmBtn
                  variant="outline"
                  size="xs"
                  data-testid="reload-stale"
                  (click)="saver.reload()"
                >
                  Neu laden
                </button>
              }
            </div>
          }
          @if (remoteBanner(); as change) {
            <div hlmAlert class="mt-3" role="status" data-testid="remote-change">
              <ng-icon name="lucideTriangleAlert" />
              <p hlmAlertTitle data-testid="remote-change-text">
                {{ members.nameOf(change.actorId) }} hat diesen Eintrag
                {{ change.kind === 'archived' ? 'archiviert' : 'geändert' }}.
              </p>
              <div hlmAlertDescription>
                <p>
                  @if (change.kind === 'archived') {
                    Du kannst weiter bearbeiten, gespeichert werden kann erst nach dem
                    Wiederherstellen.
                  } @else if (localEdits()) {
                    Deine ungespeicherten Änderungen sind noch da. Beim Speichern kannst du
                    entscheiden, welche Fassung gilt.
                  } @else {
                    Neu laden, um die aktuelle Fassung zu sehen.
                  }
                </p>
                <div class="mt-2 flex flex-wrap gap-2">
                  <button
                    type="button"
                    hlmBtn
                    variant="outline"
                    size="sm"
                    data-testid="remote-reload"
                    (click)="reloadRemote()"
                  >
                    {{ localEdits() ? 'Neu laden (eigene Änderungen verwerfen)' : 'Neu laden' }}
                  </button>
                  <button
                    type="button"
                    hlmBtn
                    variant="ghost"
                    size="sm"
                    data-testid="remote-dismiss"
                    (click)="dismissRemote()"
                  >
                    Weiter bearbeiten
                  </button>
                </div>
              </div>
            </div>
          } @else if (remoteHint(); as actorId) {
            <div hlmAlert class="mt-3" role="status" data-testid="remote-hint">
              <ng-icon name="lucideInfo" />
              <p hlmAlertDescription>Aktualisiert von {{ members.nameOf(actorId) }}</p>
            </div>
          }
          @if (readonly() && record.archivedAt) {
            <p class="mt-3 text-sm text-muted-foreground" data-testid="readonly-notice">
              Archiviert und schreibgeschützt.
            </p>
          } @else if (readonly()) {
            <p class="mt-3 text-sm text-muted-foreground" data-testid="readonly-notice">
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
                  <dt
                    class="pt-1 text-sm text-muted-foreground"
                    [id]="labelId(def.key)"
                    [attr.title]="def.description"
                  >
                    {{ def.name }}
                    @if (def.required) {
                      <span aria-hidden="true"> *</span>
                    }
                  </dt>
                  <dd class="min-w-0" data-testid="property-row" [attr.data-key]="def.key">
                    <ag-property-editor
                      [definition]="def"
                      [labelledBy]="labelId(def.key)"
                      [value]="saver.properties()[def.key]"
                      [readonly]="readonly()"
                      [errors]="saver.fieldErrors()[def.key] ?? []"
                      [rawLink]="rawLink()"
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
              >
                <a
                  agEditorFallbackAction
                  hlmBtn
                  variant="outline"
                  size="sm"
                  data-testid="body-raw-link"
                  [routerLink]="rawLink()"
                  >{{ readonly() ? 'Im Raw-View ansehen' : 'Im Raw-View bearbeiten' }}</a
                >
              </ag-markdown-editor>
            }
          </div>

          @if (record.collectionId; as collectionId) {
            <ag-record-backlinks
              class="mt-8 block"
              [recordId]="record.id"
              [collectionId]="collectionId"
            />
          }
        } @else {
          <div class="flex flex-col gap-3" data-testid="record-loading">
            <hlm-skeleton class="h-6 w-48" />
            <hlm-skeleton class="h-10 w-full" />
            <hlm-skeleton class="h-64 w-full" />
          </div>
        }
      </section>
      @if (saver.record()) {
        <ag-comments-panel
          id="record-comments"
          class="border-t lg:sticky lg:top-14 lg:h-[calc(100svh-3.5rem)] lg:w-96 lg:shrink-0 lg:border-t-0 lg:border-l"
          [hidden]="!comments.open()"
          [focusCommentId]="comment()"
          (closed)="closeComments()"
        />
      }
    </div>

    @if (saver.conflict(); as conflict) {
      <ag-conflict-dialog
        [conflict]="conflict"
        [saving]="saver.saving()"
        (reload)="saver.reload()"
        (overwrite)="saver.overwrite()"
      />
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

    <hlm-dialog
      [state]="markdownView() !== null ? 'open' : 'closed'"
      (stateChanged)="onMarkdownState($event)"
    >
      <hlm-dialog-content
        *hlmDialogPortal
        class="max-h-full sm:max-w-3xl"
        [showCloseButton]="false"
        data-testid="markdown-dialog"
      >
        <hlm-dialog-header>
          <h2 hlmDialogTitle>Markdown</h2>
        </hlm-dialog-header>
        <ag-raw-view language="markdown" [readonly]="true" [value]="markdownView() ?? ''" />
        <hlm-dialog-footer>
          <button
            type="button"
            hlmBtn
            variant="outline"
            data-testid="markdown-close"
            (click)="markdownView.set(null)"
          >
            Schließen
          </button>
        </hlm-dialog-footer>
      </hlm-dialog-content>
    </hlm-dialog>
  `,
})
export class RecordDocumentComponent {
  private readonly uid = `ag-record-document-${nextDocumentId++}`;

  /** Id of the visible name of a property; unique per document instance. */
  protected labelId(key: string): string {
    return `${this.uid}-label-${key}`;
  }

  protected readonly saver = inject(RecordSaveService);
  protected readonly members = inject(MembersStore);
  private readonly api = inject(ApiClient);
  private readonly http = inject(HttpClient);
  private readonly context = inject(WorkspaceContext);
  private readonly workspaces = inject(WorkspacesStore);
  private readonly tree = inject(WorkspaceTreeStore);
  private readonly realtime = inject(RealtimeService);
  private readonly host = inject(ElementRef<HTMLElement>).nativeElement as HTMLElement;
  /** The running archive or restore of this view; remote checks wait for it. */
  private archiveRun: Promise<void> | null = null;

  /** Route parameter `:recordId`. */
  readonly recordId = input.required<string>();
  /** Query parameter `comment`: a comment to show in the sidebar (link from the inbox). */
  readonly comment = input<string | undefined>();

  protected readonly comments = inject(CommentsStore);

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

  /** Open banner about a change by someone else; `null` after reload or "Weiter bearbeiten". */
  private readonly remoteChange = signal<RemoteChange | null>(null);
  /** Actor of the last silent reload, shown for {@link REMOTE_HINT_MS}. */
  protected readonly remoteHint = signal<string | null>(null);
  private hintTimer: ReturnType<typeof setTimeout> | null = null;
  /** A remote check is running; `again` asks for one more after it (events kept coming). */
  private checking = false;
  private checkAgain = false;
  private lastRemoteActor: string | null = null;

  /** Unsaved edits, including invalid text the editors keep to themselves. */
  protected readonly localEdits = computed(
    () => this.saver.isDirty() || this.propertyEditors().some((e) => e.invalid()),
  );

  /** The banner, as long as the view has not caught up with the change (reload, overwrite). */
  protected readonly remoteBanner = computed(() => {
    const change = this.remoteChange();
    const record = this.saver.record();
    if (!change || !record) {
      return null;
    }
    if (change.kind === 'archived') {
      return record.archivedAt ? null : change;
    }
    return Number(record.revision) < change.revision ? change : null;
  });

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

  /** The raw view of this record: the way out when the body or a Markdown value is too large. */
  protected readonly rawLink = computed(() => [
    '/w',
    this.context.slug() ?? '',
    'records',
    this.recordId(),
    'raw',
  ]);

  protected readonly canWrite = computed(() => {
    const ws = this.workspaces.find(this.context.slug());
    return !!ws && !ws.archivedAt && ws.scopes.includes('write');
  });
  /** Taking a record over as a template needs `schema` (templates are schema), on an active workspace. */
  protected readonly canSaveAsTemplate = computed(() => {
    const ws = this.workspaces.find(this.context.slug());
    const collection = this.collection.hasValue() ? this.collection.value() : undefined;
    return !!ws && !ws.archivedAt && ws.scopes.includes('schema') && !collection?.archivedAt;
  });

  protected templatesLink(collectionId: string): string[] {
    return ['/w', this.context.slug() ?? '', 'settings', 'collections', collectionId, 'templates'];
  }

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
    this.comments.bind({ recordId: () => this.recordId(), writable: () => !this.readonly() });
    effect(() => {
      if (this.comment()) {
        untracked(() => this.comments.setOpen(true));
      }
    });
    this.saver.beforeSave = () => this.pullPending();
    this.saver.hasLocalInput = () => this.hasLocalInput();

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
    effect(() => {
      this.recordId();
      untracked(() => {
        this.remoteChange.set(null);
        this.hideHint();
      });
    });

    this.realtime.activity.pipe(takeUntilDestroyed()).subscribe((event) => this.onActivity(event));
    this.realtime.resync.pipe(takeUntilDestroyed()).subscribe(() => void this.checkRemote(null));
    inject(DestroyRef).onDestroy(() => this.hideHint());
  }

  private onActivity(event: ActivityEvent): void {
    if (
      event.targetType === 'record' &&
      event.targetId === this.recordId() &&
      this.realtime.slug === this.context.slug()
    ) {
      void this.checkRemote(event.actorId);
    }
  }

  /**
   * Input a silent reload would destroy although it is not in the saver's edits: invalid text
   * (JSON, number, date) the editors keep to themselves, the open "discard and leave?" question,
   * and focus in title, properties or body — re-creating the editors would take the cursor away
   * mid-sentence, so a focused editor gets the banner instead.
   */
  private hasLocalInput(): boolean {
    if (this.readonly()) {
      return false;
    }
    if (this.leaveRequest() !== null || this.propertyEditors().some((e) => e.invalid())) {
      return true;
    }
    const active = this.host.ownerDocument.activeElement;
    return (
      !!active &&
      this.host.contains(active) &&
      !!active.closest(
        '[data-testid="record-title"], [data-testid="properties-panel"], [data-testid="record-body"]',
      )
    );
  }

  /** Asks the server whether the record changed and reacts (see `RecordSaveService.checkRemote`). */
  private async checkRemote(actorId: string | null): Promise<void> {
    if (actorId) {
      this.lastRemoteActor = actorId;
    }
    if (this.checking) {
      this.checkAgain = true;
      return;
    }
    this.checking = true;
    try {
      do {
        this.checkAgain = false;
        while (this.archiveRun) {
          await this.archiveRun; // the event of an own archive must not read as someone else's
        }
        const result = await this.saver.checkRemote();
        if (result.kind === 'unchanged' || result.kind === 'failed') {
          continue;
        }
        const actor = this.actorOf(result.record);
        if (result.kind === 'reloaded') {
          this.remoteChange.set(null);
          this.showHint(actor);
        } else {
          this.hideHint();
          this.remoteChange.set({
            kind: result.kind,
            actorId: actor,
            revision: Number(result.record.revision),
          });
        }
      } while (this.checkAgain);
    } finally {
      this.checking = false;
      this.lastRemoteActor = null;
    }
  }

  private actorOf(record: RecordDto): string {
    return this.lastRemoteActor ?? record.updatedBy;
  }

  /** "Neu laden" (and with local edits: discard them). */
  protected async reloadRemote(): Promise<void> {
    this.remoteChange.set(null);
    await this.saver.reload();
  }

  /** "Weiter bearbeiten": keeps the edits; saving later shows the conflict dialog. */
  protected dismissRemote(): void {
    this.remoteChange.set(null);
  }

  private showHint(actorId: string): void {
    this.hideHint();
    this.remoteHint.set(actorId);
    this.hintTimer = setTimeout(() => {
      this.hintTimer = null;
      this.remoteHint.set(null);
    }, REMOTE_HINT_MS);
  }

  private hideHint(): void {
    if (this.hintTimer !== null) {
      clearTimeout(this.hintTimer);
      this.hintTimer = null;
    }
    this.remoteHint.set(null);
  }

  /**
   * For the `canDeactivate` guard: saves pending edits and lets the user leave. When saving
   * cannot succeed (offline, server error, archived meanwhile) or input is invalid and thus not
   * saved, the user is asked whether to discard; a revision conflict keeps the page (its dialog
   * is showing).
   */
  async canLeave(): Promise<boolean> {
    if (this.readonly()) {
      return this.askAboutCommentDrafts();
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
      return this.askAboutCommentDrafts();
    }
    if (this.saver.conflict()) {
      return false;
    }
    const message = saved
      ? 'Ungültige Eingaben (z. B. ungültiges JSON) werden nicht gespeichert.'
      : 'Die Änderungen konnten nicht gespeichert werden.';
    return this.askToLeave(
      this.comments.hasUnsentDrafts() ? `${message} ${UNSENT_COMMENTS}` : message,
    );
  }

  /** Comment drafts are never sent on leaving; the user decides whether to drop them. */
  private askAboutCommentDrafts(): Promise<boolean> {
    return this.comments.hasUnsentDrafts()
      ? this.askToLeave(UNSENT_COMMENTS)
      : Promise.resolve(true);
  }

  private askToLeave(message: string): Promise<boolean> {
    return new Promise<boolean>((resolve) => this.leaveRequest.set({ message, resolve }));
  }

  protected answerLeave(leave: boolean): void {
    this.leaveRequest()?.resolve(leave);
    this.leaveRequest.set(null);
  }

  /** Warns before closing or reloading the tab while there are unsaved edits or comment drafts. */
  protected onBeforeUnload(event: BeforeUnloadEvent): void {
    this.pullPending();
    if (
      (!this.readonly() &&
        (this.saver.isDirty() || this.propertyEditors().some((e) => e.invalid()))) ||
      this.comments.hasUnsentDrafts()
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

  /** Hides the sidebar and gives the focus back to its toggle. */
  protected closeComments(): void {
    this.comments.setOpen(false);
    this.host.querySelector<HTMLElement>('[data-testid="comments-toggle"]')?.focus();
  }

  /** Brain closed the Markdown dialog itself (Escape). */
  protected onMarkdownState(state: BrnDialogState): void {
    if (state === 'closed') {
      this.markdownView.set(null);
    }
  }

  protected captureBaseline(): void {
    // read-only: nothing can be typed, and serializing a large body just for a click is costly
    if (this.baseline === null && !this.readonly()) {
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
    const run = this.runArchiveToggle();
    this.archiveRun = run;
    try {
      await run;
    } finally {
      if (this.archiveRun === run) {
        this.archiveRun = null;
      }
    }
  }

  private async runArchiveToggle(): Promise<void> {
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
