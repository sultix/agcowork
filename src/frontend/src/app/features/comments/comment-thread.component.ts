import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideChevronDown, lucideChevronUp, lucideCircleCheck } from '@ng-icons/lucide';
import { HlmButton } from '@spartan-ng/helm/button';
import { MembersStore } from '../../core/workspace/members.store';
import { CommentComposerComponent } from './comment-composer.component';
import { CommentItemComponent } from './comment-item.component';
import { Comment, CommentsStore, replyKey } from './comments.store';
import { absoluteTime, relativeTime } from './relative-time';

/** Plain-text start of a comment for the collapsed line of a resolved thread. */
export function excerpt(markdown: string | null, max = 120): string {
  if (!markdown) {
    return '';
  }
  const text = markdown
    .replace(/!?\[([^[\]]*)\]\([^)]*\)/g, '$1')
    .replace(/[`*_~#>]/g, '')
    .replace(/\s+/g, ' ')
    .trim();
  return text.length > max ? `${text.slice(0, max - 1).trimEnd()}…` : text;
}

/**
 * A thread: the root comment, its replies (oldest first) and the reply field. A resolved thread
 * is collapsed to "Erledigt von X" with the start of the root until it is expanded.
 */
@Component({
  selector: 'ag-comment-thread',
  imports: [CommentComposerComponent, CommentItemComponent, HlmButton, NgIcon],
  providers: [provideIcons({ lucideChevronDown, lucideChevronUp, lucideCircleCheck })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'block rounded-lg border bg-card text-card-foreground',
    role: 'article',
    'data-testid': 'comment-thread',
    '[attr.data-thread-id]': 'thread().id',
    '[attr.data-resolved]': 'thread().resolvedAt ? "true" : null',
    '[attr.aria-label]': 'label()',
  },
  template: `
    @let root = thread();
    @if (root.resolvedAt) {
      <div
        class="flex items-start justify-between gap-2 px-3 py-2 text-sm"
        [class.border-b]="expanded()"
      >
        <div class="flex min-w-0 flex-col gap-0.5">
          <p class="flex items-center gap-1.5 text-muted-foreground" data-testid="resolved-by">
            <ng-icon name="lucideCircleCheck" class="text-success" />
            <span
              >Erledigt von {{ members.nameOf(root.resolvedBy) }}
              <time
                class="text-xs"
                [attr.datetime]="root.resolvedAt"
                [attr.title]="absolute(root.resolvedAt)"
                >{{ relative(root.resolvedAt) }}</time
              ></span
            >
          </p>
          @if (!expanded()) {
            <p class="truncate" data-testid="resolved-excerpt">
              {{ root.deletedAt ? 'Gelöschter Kommentar' : summary() }}
              @if (replyCount() > 0) {
                <span class="text-muted-foreground">
                  · {{ replyCount() }} {{ replyCount() === 1 ? 'Antwort' : 'Antworten' }}</span
                >
              }
            </p>
          }
        </div>
        @if (store.hasDraftIn(root)) {
          <!-- Held open while a draft of it is open; collapsing would hide the draft. -->
          <span class="shrink-0 text-xs text-muted-foreground" data-testid="thread-held-open"
            >Offen, solange du schreibst</span
          >
        } @else {
          <button
            type="button"
            hlmBtn
            variant="ghost"
            size="sm"
            data-testid="thread-toggle"
            [attr.aria-expanded]="expanded()"
            (click)="store.toggleExpanded(root.id)"
          >
            <ng-icon [name]="expanded() ? 'lucideChevronUp' : 'lucideChevronDown'" />
            {{ expanded() ? 'Einklappen' : 'Anzeigen' }}
          </button>
        }
      </div>
    }
    @if (!root.resolvedAt || expanded()) {
      <div class="flex flex-col gap-1 p-2">
        <ag-comment-item [comment]="root" />
        @if (omittedReplies() > 0) {
          <p class="ml-4 px-2 text-xs text-muted-foreground" data-testid="omitted-replies">
            {{ omittedReplies() }} ältere
            {{ omittedReplies() === 1 ? 'Antwort' : 'Antworten' }} nicht angezeigt
          </p>
        }
        @if (replyCount() > 0) {
          <div class="ml-4 flex flex-col gap-1 border-l pl-2" data-testid="replies">
            @for (reply of root.replies; track reply.id) {
              <ag-comment-item [comment]="reply" />
            }
          </div>
        }
        @if (replying()) {
          <ag-comment-composer
            class="ml-4 px-2 pt-1"
            label="Antwort"
            placeholder="Antworten …"
            submitLabel="Antworten"
            [cancellable]="true"
            [autofocus]="true"
            [readonly]="!store.writable()"
            [value]="store.draft(replySlot())"
            [busy]="store.isBusy(replySlot())"
            [errors]="store.errorsOf(replySlot())"
            (valueChange)="store.setDraft(replySlot(), $event)"
            (submitted)="store.send(replySlot(), root)"
            (cancelled)="store.closeDraft(replySlot())"
          />
        } @else if (store.canReply(root)) {
          <div class="ml-4 px-2">
            <button
              type="button"
              hlmBtn
              variant="ghost"
              size="sm"
              data-testid="thread-reply"
              (click)="store.openDraft(replySlot())"
            >
              Antworten
            </button>
          </div>
        }
      </div>
    }
  `,
})
export class CommentThreadComponent {
  protected readonly store = inject(CommentsStore);
  protected readonly members = inject(MembersStore);

  readonly thread = input.required<Comment>();

  protected readonly replySlot = computed(() => replyKey(this.thread().id));
  protected readonly replying = computed(() => this.store.isOpen(this.replySlot()));
  /** Expanded by the user, or held open while a reply or edit draft of the thread is open. */
  protected readonly expanded = computed(
    () => this.store.expanded().has(this.thread().id) || this.store.hasDraftIn(this.thread()),
  );
  protected readonly replyCount = computed(() => this.thread().replies?.length ?? 0);
  protected readonly omittedReplies = computed(() => Number(this.thread().omittedReplies ?? 0));
  protected readonly summary = computed(() => excerpt(this.thread().markdown));
  protected readonly label = computed(() => {
    const root = this.thread();
    const author = root.deletedAt ? 'Gelöschter Kommentar' : `Thread von ${root.authorName}`;
    return root.resolvedAt ? `${author} (erledigt)` : author;
  });

  protected relative(iso: string): string {
    return relativeTime(iso, this.store.now());
  }

  protected absolute(iso: string): string {
    return absoluteTime(iso);
  }
}
