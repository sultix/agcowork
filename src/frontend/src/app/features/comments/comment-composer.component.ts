import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterNextRender,
  computed,
  inject,
  input,
  model,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTextarea } from '@spartan-ng/helm/textarea';
import { MembersStore } from '../../core/workspace/members.store';
import { BadgeComponent } from '../../shared/ui/badge.component';
import {
  MentionCandidate,
  MentionTrigger,
  filterCandidates,
  insertMention,
  mentionTriggerAt,
} from './mention-picker';

let nextId = 0;

/**
 * Markdown text area for comments, replies and edits (plain text, no WYSIWYG). Typing `@` at
 * the start of a word lists matching members (humans and agents) below the field: arrow keys
 * move, Enter or Tab inserts `[@Name](actor:<id>)` at the caret, Escape closes the list (and
 * only the list); focus stays in the field. Ctrl/Cmd+Enter sends. The text is a two-way
 * `value`, so the caller keeps the draft (it outlives reloads and re-rendering).
 */
@Component({
  selector: 'ag-comment-composer',
  imports: [BadgeComponent, HlmButton, HlmTextarea],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex flex-col gap-2', 'data-testid': 'comment-composer' },
  template: `
    <div class="relative">
      <textarea
        #field
        hlmTextarea
        class="max-h-64 min-h-20 resize-y"
        data-testid="composer-input"
        aria-autocomplete="list"
        [id]="id + '-input'"
        [attr.aria-label]="label()"
        [attr.aria-describedby]="id + '-hint' + (errors().length ? ' ' + id + '-errors' : '')"
        [attr.aria-controls]="open() ? id + '-list' : null"
        [attr.aria-activedescendant]="open() ? optionId(active()) : null"
        [placeholder]="placeholder()"
        [value]="value()"
        [readOnly]="busy() || readonly()"
        (input)="onInput()"
        (click)="updateTrigger()"
        (keyup)="onKeyup($event)"
        (keydown)="onKeydown($event)"
        (blur)="onBlur()"
      ></textarea>
      @if (open()) {
        <ul
          role="listbox"
          class="absolute inset-x-0 top-full z-20 mt-1 max-h-60 overflow-y-auto rounded-md border bg-popover p-1 text-sm text-popover-foreground shadow-md"
          data-testid="mention-list"
          aria-label="Mitglied erwähnen"
          [id]="id + '-list'"
        >
          @for (candidate of candidates(); track candidate.actorId; let i = $index) {
            <li
              role="option"
              class="flex cursor-default items-center justify-between gap-2 rounded-sm px-2 py-1.5"
              data-testid="mention-option"
              [class.bg-accent]="i === active()"
              [class.text-accent-foreground]="i === active()"
              [id]="optionId(i)"
              [attr.aria-selected]="i === active()"
              tabindex="-1"
              (mousedown)="$event.preventDefault(); choose(i)"
            >
              <span class="truncate">{{ candidate.name }}</span>
              <ag-badge [tone]="candidate.kind === 'agent' ? 'accent' : 'neutral'">{{
                candidate.kind === 'agent' ? 'Agent' : 'Mensch'
              }}</ag-badge>
            </li>
          }
        </ul>
      }
    </div>
    <p class="text-xs text-muted-foreground" [id]="id + '-hint'">
      Markdown. &#64; erwähnt ein Mitglied, Strg/Cmd+Enter sendet.
    </p>
    @if (errors().length) {
      <div
        class="flex flex-col gap-1"
        role="alert"
        data-testid="composer-errors"
        [id]="id + '-errors'"
      >
        @for (message of errors(); track $index) {
          <p class="text-sm text-destructive">{{ message }}</p>
        }
      </div>
    }
    <div class="flex justify-end gap-2">
      @if (cancellable() || readonly()) {
        <button
          type="button"
          hlmBtn
          variant="ghost"
          size="sm"
          data-testid="composer-cancel"
          [disabled]="busy()"
          (click)="cancelled.emit()"
        >
          {{ readonly() ? 'Verwerfen' : 'Abbrechen' }}
        </button>
      }
      @if (!readonly()) {
        <button
          type="button"
          hlmBtn
          size="sm"
          data-testid="composer-submit"
          [disabled]="busy() || value().trim() === ''"
          (click)="submit()"
        >
          {{ submitLabel() }}
        </button>
      }
    </div>
  `,
})
export class CommentComposerComponent {
  private readonly members = inject(MembersStore);

  /** The draft. */
  readonly value = model('');
  readonly label = input('Kommentar');
  readonly placeholder = input('');
  readonly submitLabel = input('Kommentieren');
  readonly busy = input(false);
  /**
   * Nothing can be sent any more (record archived meanwhile, rights gone): the text stays
   * visible to copy, "Verwerfen" discards it.
   */
  readonly readonly = input(false);
  readonly errors = input<readonly string[]>([]);
  /** Shows "Abbrechen" (replies, edits). */
  readonly cancellable = input(false);
  /** Focuses the field once it is shown (reply and edit drafts). */
  readonly autofocus = input(false);

  readonly submitted = output();
  readonly cancelled = output();

  protected readonly id = `ag-composer-${nextId++}`;
  private readonly field = viewChild.required<ElementRef<HTMLTextAreaElement>>('field');

  private readonly trigger = signal<MentionTrigger | null>(null);
  /** The `@` whose list the user closed with Escape; it stays closed. */
  private dismissedAt: number | null = null;
  protected readonly active = signal(0);

  protected readonly candidates = computed<MentionCandidate[]>(() => {
    const trigger = this.trigger();
    if (!trigger || this.readonly()) {
      return [];
    }
    return filterCandidates(
      this.members.members().map((m) => ({ actorId: m.actorId, name: m.name, kind: m.kind })),
      trigger.query,
    );
  });
  protected readonly open = computed(() => this.candidates().length > 0);

  constructor() {
    afterNextRender(() => {
      if (this.autofocus()) {
        const field = this.field().nativeElement;
        field.focus();
        field.setSelectionRange(field.value.length, field.value.length);
      }
    });
  }

  /** Focuses the text field. */
  focus(): void {
    this.field().nativeElement.focus();
  }

  protected optionId(index: number): string {
    return `${this.id}-option-${index}`;
  }

  protected onInput(): void {
    this.value.set(this.field().nativeElement.value);
    this.updateTrigger();
  }

  protected onKeyup(event: KeyboardEvent): void {
    if (['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
      this.updateTrigger();
    }
  }

  protected onBlur(): void {
    this.trigger.set(null);
  }

  /** Reads the `@` at the caret. */
  protected updateTrigger(): void {
    const field = this.field().nativeElement;
    const caret = field.selectionStart ?? field.value.length;
    const trigger = mentionTriggerAt(field.value, caret);
    if (this.dismissedAt !== null && trigger?.start !== this.dismissedAt) {
      this.dismissedAt = null;
    }
    const shown = trigger && trigger.start !== this.dismissedAt ? trigger : null;
    if (shown?.start !== this.trigger()?.start || shown?.query !== this.trigger()?.query) {
      this.active.set(0);
    }
    this.trigger.set(shown);
  }

  protected onKeydown(event: KeyboardEvent): void {
    if (event.isComposing) {
      return; // an input method (IME) is composing: Enter and arrows belong to it
    }
    if (this.open()) {
      const count = this.candidates().length;
      switch (event.key) {
        case 'ArrowDown':
          event.preventDefault();
          this.active.update((i) => (i + 1) % count);
          return;
        case 'ArrowUp':
          event.preventDefault();
          this.active.update((i) => (i - 1 + count) % count);
          return;
        case 'Enter':
        case 'Tab':
          if (!event.ctrlKey && !event.metaKey && !event.shiftKey && !event.altKey) {
            event.preventDefault();
            this.choose(this.active());
            return;
          }
          break;
        case 'Escape':
          // Closes the list only; dialogs and editors around must not see this Escape.
          event.preventDefault();
          event.stopPropagation();
          this.dismissedAt = this.trigger()?.start ?? null;
          this.trigger.set(null);
          return;
      }
    }
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      this.submit();
    }
  }

  protected choose(index: number): void {
    const trigger = this.trigger();
    const candidate = this.candidates()[index];
    if (!trigger || !candidate) {
      return;
    }
    const field = this.field().nativeElement;
    const caret = field.selectionStart ?? field.value.length;
    const result = insertMention(field.value, trigger, caret, candidate);
    field.value = result.text;
    field.focus();
    field.setSelectionRange(result.caret, result.caret);
    this.value.set(result.text);
    this.trigger.set(null);
  }

  protected submit(): void {
    if (!this.busy() && !this.readonly() && this.value().trim() !== '') {
      this.trigger.set(null);
      this.submitted.emit();
    }
  }
}
