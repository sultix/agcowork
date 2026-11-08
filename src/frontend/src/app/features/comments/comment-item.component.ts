import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideCheck,
  lucideEllipsis,
  lucidePencil,
  lucideRotateCcw,
  lucideTrash2,
} from '@ng-icons/lucide';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmDropdownMenuImports } from '@spartan-ng/helm/dropdown-menu';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { CommentComposerComponent } from './comment-composer.component';
import { CommentMarkdownComponent } from './comment-markdown.component';
import { Comment, CommentsStore, actionKey, editKey } from './comments.store';
import { absoluteTime, relativeTime } from './relative-time';

/**
 * One comment: author (name, human/agent badge), relative time with the exact time as tooltip,
 * "bearbeitet", the rendered Markdown or the placeholder of a deleted root, and the actions the
 * actor may take: edit (own), delete (own; with admin also others'), resolve/reopen (roots).
 */
@Component({
  selector: 'ag-comment-item',
  imports: [
    BadgeComponent,
    CommentComposerComponent,
    CommentMarkdownComponent,
    HlmButton,
    HlmDropdownMenuImports,
    NgIcon,
  ],
  providers: [
    provideIcons({ lucideCheck, lucideEllipsis, lucidePencil, lucideRotateCcw, lucideTrash2 }),
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'block rounded-md px-2 py-1.5 outline-none transition-shadow',
    tabindex: '-1',
    'data-testid': 'comment',
    '[attr.data-comment-id]': 'comment().id',
    '[attr.aria-current]': 'highlighted() ? "true" : null',
    '[class]': 'highlighted() ? "ring-2 ring-primary bg-primary/5" : ""',
  },
  template: `
    @let c = comment();
    <div class="flex items-start justify-between gap-2">
      <div class="flex min-w-0 flex-wrap items-baseline gap-x-1.5 gap-y-0.5 text-sm">
        @if (c.deletedAt) {
          <span class="text-muted-foreground italic" data-testid="comment-deleted"
            >Gelöschter Kommentar</span
          >
        } @else {
          <span class="font-medium" data-testid="comment-author">{{ c.authorName }}</span>
          <ag-badge [tone]="c.authorKind === 'agent' ? 'accent' : 'neutral'"
            ><span data-testid="comment-author-kind">{{
              c.authorKind === 'agent' ? 'Agent' : 'Mensch'
            }}</span></ag-badge
          >
        }
        <time
          class="text-xs text-muted-foreground"
          data-testid="comment-time"
          [attr.datetime]="c.createdAt"
          [attr.title]="absolute(c.createdAt)"
          >{{ relative(c.createdAt) }}</time
        >
        @if (c.editedAt && !c.deletedAt) {
          <span
            class="text-xs text-muted-foreground"
            data-testid="comment-edited"
            [attr.title]="'Bearbeitet ' + absolute(c.editedAt)"
            >(bearbeitet)</span
          >
        }
      </div>
      <div class="flex shrink-0 items-center gap-0.5">
        @if (store.canResolve(c)) {
          <button
            type="button"
            hlmBtn
            variant="ghost"
            size="icon-sm"
            data-testid="comment-resolve"
            [attr.aria-label]="c.resolvedAt ? 'Thread wieder öffnen' : 'Thread erledigen'"
            [title]="c.resolvedAt ? 'Wieder öffnen' : 'Erledigen'"
            [disabled]="store.isBusy(actionSlot())"
            (click)="store.setResolved(c, !c.resolvedAt)"
          >
            <ng-icon [name]="c.resolvedAt ? 'lucideRotateCcw' : 'lucideCheck'" />
          </button>
        }
        @if ((store.canEdit(c) || store.canDelete(c)) && !editing()) {
          <button
            type="button"
            hlmBtn
            variant="ghost"
            size="icon-sm"
            data-testid="comment-actions"
            align="end"
            [attr.aria-label]="'Aktionen für den Kommentar von ' + c.authorName"
            [hlmDropdownMenuTrigger]="menu"
          >
            <ng-icon name="lucideEllipsis" />
          </button>
        }
      </div>
    </div>

    @if (editing()) {
      <ag-comment-composer
        class="mt-1.5"
        label="Kommentar bearbeiten"
        submitLabel="Speichern"
        [cancellable]="true"
        [autofocus]="true"
        [readonly]="!store.writable()"
        [value]="store.draft(editSlot())"
        [busy]="store.isBusy(editSlot())"
        [errors]="store.errorsOf(editSlot())"
        (valueChange)="store.setDraft(editSlot(), $event)"
        (submitted)="store.saveEdit(c)"
        (cancelled)="store.closeDraft(editSlot())"
      />
    } @else if (c.deletedAt || c.markdown === null) {
      <p class="mt-1 text-sm text-muted-foreground italic" data-testid="comment-placeholder">
        Dieser Kommentar wurde gelöscht.
      </p>
    } @else {
      <ag-comment-markdown class="mt-1" [markdown]="c.markdown" />
    }
    @for (message of store.errorsOf(actionSlot()); track $index) {
      <p class="mt-1 text-sm text-destructive" role="alert" data-testid="comment-action-error">
        {{ message }}
      </p>
    }

    <ng-template #menu>
      <hlm-dropdown-menu class="w-40" data-testid="comment-menu">
        @if (store.canEdit(c)) {
          <button hlmDropdownMenuItem data-testid="comment-edit" (triggered)="startEdit()">
            <ng-icon name="lucidePencil" />
            Bearbeiten
          </button>
        }
        @if (store.canDelete(c)) {
          <button
            hlmDropdownMenuItem
            variant="destructive"
            data-testid="comment-delete"
            (triggered)="store.deleting.set(c)"
          >
            <ng-icon name="lucideTrash2" />
            Löschen
          </button>
        }
      </hlm-dropdown-menu>
    </ng-template>
  `,
})
export class CommentItemComponent {
  protected readonly store = inject(CommentsStore);

  readonly comment = input.required<Comment>();

  protected readonly editSlot = computed(() => editKey(this.comment().id));
  protected readonly actionSlot = computed(() => actionKey(this.comment().id));
  protected readonly editing = computed(() => this.store.isOpen(this.editSlot()));
  protected readonly highlighted = computed(() => this.store.highlightId() === this.comment().id);

  protected relative(iso: string): string {
    return relativeTime(iso, this.store.now());
  }

  protected absolute(iso: string): string {
    return absoluteTime(iso);
  }

  protected startEdit(): void {
    this.store.openDraft(this.editSlot(), this.comment().markdown ?? '');
  }
}
