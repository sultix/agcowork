import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCircleAlert, lucideInfo, lucideX } from '@ng-icons/lucide';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { ConfirmDialogComponent } from '../../shared/ui/confirm-dialog.component';
import { CommentComposerComponent } from './comment-composer.component';
import { CommentRenderer } from './comment-renderer';
import { CommentThreadComponent } from './comment-thread.component';
import { CommentsStore, NEW_THREAD } from './comments.store';

/**
 * The comment sidebar of a record (`complementary` landmark "Kommentare"): threads in the
 * server's order, resolved ones hidden unless "Erledigte anzeigen" is on (then collapsed),
 * the field for a new thread, a live region for comments of others. `focusCommentId` (from
 * `?comment=`, e.g. a link from the mentions inbox) reveals that comment, highlights it and
 * scrolls it into view once the threads are loaded.
 */
@Component({
  selector: 'ag-comments-panel',
  imports: [
    CommentComposerComponent,
    CommentThreadComponent,
    ConfirmDialogComponent,
    HlmAlertImports,
    HlmButton,
    HlmSkeleton,
    NgIcon,
  ],
  providers: [CommentRenderer, provideIcons({ lucideCircleAlert, lucideInfo, lucideX })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    role: 'complementary',
    'aria-label': 'Kommentare',
    class: 'flex min-h-0 flex-col bg-background',
    'data-testid': 'comments-panel',
  },
  template: `
    <div class="flex items-center justify-between gap-2 border-b px-4 py-2">
      <h2 class="text-sm font-semibold">
        Kommentare
        @if (store.openCount() > 0) {
          <span class="font-normal text-muted-foreground">({{ store.openCount() }} offen)</span>
        }
      </h2>
      <div class="flex items-center gap-1">
        @if (resolvedCount() > 0) {
          <label class="flex items-center gap-1.5 text-xs text-muted-foreground">
            <input
              type="checkbox"
              class="size-3.5 accent-primary"
              data-testid="show-resolved"
              [checked]="store.showResolved()"
              (change)="store.showResolved.set($any($event.target).checked)"
            />
            Erledigte anzeigen ({{ resolvedCount() }})
          </label>
        }
        <button
          type="button"
          hlmBtn
          variant="ghost"
          size="icon-sm"
          aria-label="Kommentare ausblenden"
          data-testid="comments-close"
          (click)="closed.emit()"
        >
          <ng-icon name="lucideX" />
        </button>
      </div>
    </div>

    <p class="sr-only" role="status" aria-live="polite" data-testid="comments-live">
      {{ store.announcement() }}
    </p>

    <div class="flex min-h-0 flex-1 flex-col gap-3 overflow-y-auto p-3" data-testid="comments-list">
      @if (store.heldBack()) {
        <div hlmAlert role="status" data-testid="comments-held">
          <ng-icon name="lucideInfo" />
          <p hlmAlertDescription>
            Ein Kommentar, an dem du schreibst, wurde inzwischen gelöscht. Die Diskussion wird
            aktualisiert, sobald du den Entwurf sendest oder verwirfst.
          </p>
        </div>
      }
      @if (renderer.exhausted()) {
        <p class="text-xs text-muted-foreground" role="status" data-testid="comments-plain">
          Viele oder aufwendige Kommentare: weitere werden ohne Formatierung angezeigt.
        </p>
      }
      @if (missingFocus()) {
        <div hlmAlert role="status" data-testid="comment-missing">
          <ng-icon name="lucideInfo" />
          <p hlmAlertDescription>Der verlinkte Kommentar ist nicht mehr vorhanden.</p>
        </div>
      }
      @if (store.loadError(); as message) {
        <div hlmAlert variant="destructive" data-testid="comments-error">
          <ng-icon name="lucideCircleAlert" />
          <p hlmAlertDescription>{{ message }}</p>
          <button
            type="button"
            hlmAlertAction
            hlmBtn
            variant="outline"
            size="xs"
            (click)="store.retry()"
          >
            Erneut laden
          </button>
        </div>
      } @else if (!store.loaded()) {
        <div class="flex flex-col gap-2" data-testid="comments-loading">
          <hlm-skeleton class="h-16 w-full" />
          <hlm-skeleton class="h-16 w-full" />
        </div>
      } @else {
        @if (store.omittedThreads() > 0) {
          <p class="text-xs text-muted-foreground" data-testid="omitted-threads">
            {{ store.omittedThreads() }} ältere
            {{ store.omittedThreads() === 1 ? 'Thread' : 'Threads' }} nicht angezeigt
          </p>
        }
        @for (thread of visibleThreads(); track thread.id) {
          <ag-comment-thread [thread]="thread" />
        } @empty {
          <div class="rounded-xl border border-dashed p-6 text-center" data-testid="comments-empty">
            <p class="font-medium">
              {{ store.threads().length ? 'Keine offenen Threads' : 'Noch keine Kommentare' }}
            </p>
            <p class="text-sm text-muted-foreground">
              {{
                store.threads().length
                  ? 'Erledigte Threads lassen sich oben einblenden.'
                  : store.writable()
                    ? 'Starte unten eine Diskussion; mit @ erwähnst du Mitglieder.'
                    : 'Hier erscheinen Diskussionen zu diesem Eintrag.'
              }}
            </p>
          </div>
        }
      }
    </div>

    <div class="border-t p-3">
      @if (!store.writable()) {
        <p class="text-sm text-muted-foreground" data-testid="comments-readonly">
          Kommentieren ist hier nicht möglich (nur Leserechte oder archiviert).
        </p>
      }
      <!-- A draft typed before the record became read-only stays visible to copy. -->
      @if (store.writable() || store.draft(newThread) !== '') {
        <ag-comment-composer
          label="Neuer Kommentar"
          placeholder="Kommentieren …"
          [class.mt-2]="!store.writable()"
          [readonly]="!store.writable()"
          [value]="store.draft(newThread)"
          [busy]="store.isBusy(newThread)"
          [errors]="store.errorsOf(newThread)"
          (valueChange)="store.setDraft(newThread, $event)"
          (submitted)="store.send(newThread, null)"
          (cancelled)="store.setDraft(newThread, '')"
        />
      }
    </div>

    <ag-confirm-dialog
      [open]="store.deleting() !== null"
      title="Kommentar löschen?"
      [message]="deleteMessage()"
      confirmLabel="Löschen"
      [busy]="deleteBusy()"
      (confirmed)="confirmDelete()"
      (cancelled)="store.deleting.set(null)"
    />
  `,
})
export class CommentsPanelComponent {
  protected readonly store = inject(CommentsStore);
  protected readonly renderer = inject(CommentRenderer);
  private readonly host = inject(ElementRef<HTMLElement>).nativeElement as HTMLElement;
  private readonly injector = inject(Injector);

  /** Comment to reveal and highlight (`?comment=` of the record URL). */
  readonly focusCommentId = input<string | null | undefined>(null);
  /** "Kommentare ausblenden". */
  readonly closed = output();

  protected readonly newThread = NEW_THREAD;
  protected readonly deleteBusy = signal(false);
  /** The linked comment is not among the loaded threads. */
  protected readonly missingFocus = signal(false);

  protected readonly resolvedCount = computed(
    () => this.store.threads().filter((t) => !!t.resolvedAt).length,
  );
  /** Open threads; resolved ones with "Erledigte anzeigen" or while a draft of them is open. */
  protected readonly visibleThreads = computed(() =>
    this.store.showResolved()
      ? this.store.threads()
      : this.store.threads().filter((t) => !t.resolvedAt || this.store.hasDraftIn(t)),
  );
  protected readonly deleteMessage = computed(() => {
    const comment = this.store.deleting();
    if (!comment) {
      return '';
    }
    const replies = comment.parentCommentId === null ? (comment.replies?.length ?? 0) : 0;
    return replies > 0
      ? 'Der Text wird entfernt; der Thread bleibt mit seinen Antworten als Platzhalter erhalten.'
      : 'Der Kommentar wird entfernt. Erwähnungen darin verschwinden aus den Posteingängen.';
  });

  /** Focus id already handled (scrolled to); a new link handles again. */
  private handled: string | null = null;

  constructor() {
    effect(() => {
      this.store.recordId();
      untracked(() => this.renderer.reset());
    });
    effect(() => {
      const id = this.focusCommentId() ?? null;
      const loaded = this.store.loaded();
      untracked(() => this.applyFocus(id, loaded));
    });
  }

  private applyFocus(id: string | null, loaded: boolean): void {
    if (id === null) {
      this.handled = null;
      this.missingFocus.set(false);
      this.store.highlightId.set(null);
      return;
    }
    if (!loaded || this.handled === id) {
      return;
    }
    this.handled = id;
    const root = this.store.reveal(id);
    if (!root) {
      this.missingFocus.set(true);
      this.store.highlightId.set(null);
      return;
    }
    this.missingFocus.set(false);
    this.store.highlightId.set(id);
    afterNextRender(
      () => {
        const element = this.host.querySelector<HTMLElement>(
          `[data-testid="comment"][data-comment-id="${CSS.escape(id)}"]`,
        );
        element?.scrollIntoView?.({ block: 'center', behavior: 'smooth' });
        element?.focus({ preventScroll: true });
      },
      { injector: this.injector },
    );
  }

  protected async confirmDelete(): Promise<void> {
    const comment = this.store.deleting();
    if (!comment) {
      return;
    }
    this.deleteBusy.set(true);
    try {
      await this.store.remove(comment);
    } finally {
      this.deleteBusy.set(false);
      this.store.deleting.set(null);
    }
  }
}
