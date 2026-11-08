import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  PendingTasks,
  ViewEncapsulation,
  afterNextRender,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { exceedsMarkdownLimits } from '../markdown/markdown-limits';
import {
  MARKDOWN_EDITOR_FACTORY,
  MARKDOWN_EDITOR_LIMITS,
  MarkdownCommand,
  MarkdownEditorFactory,
  MarkdownEditorHandle,
} from './markdown-editor.adapter';

interface ToolbarItem {
  command: MarkdownCommand;
  label: string;
  title: string;
}

const TOOLBAR: readonly ToolbarItem[] = [
  { command: 'bold', label: 'B', title: 'Fett (Strg/Cmd+B)' },
  { command: 'italic', label: 'I', title: 'Kursiv (Strg/Cmd+I)' },
  { command: 'strike', label: 'S', title: 'Durchgestrichen' },
  { command: 'code', label: '</>', title: 'Code (Strg/Cmd+E)' },
  { command: 'h1', label: 'H1', title: 'Überschrift 1 (Strg/Cmd+Alt+1)' },
  { command: 'h2', label: 'H2', title: 'Überschrift 2 (Strg/Cmd+Alt+2)' },
  { command: 'h3', label: 'H3', title: 'Überschrift 3 (Strg/Cmd+Alt+3)' },
  { command: 'bulletList', label: '•', title: 'Liste' },
  { command: 'orderedList', label: '1.', title: 'Nummerierte Liste' },
  { command: 'blockquote', label: '❝', title: 'Zitat' },
  { command: 'codeBlock', label: '{ }', title: 'Code-Block' },
  { command: 'rule', label: '―', title: 'Trennlinie' },
];

/**
 * Markdown WYSIWYG editor. The Markdown string is the source of truth (E2); the editor is a view
 * on it. The engine (Milkdown) is loaded lazily via {@link MARKDOWN_EDITOR_FACTORY}.
 *
 * - `value` set from outside never triggers `valueChange`.
 * - `valueChange` fires debounced and only when the serialized Markdown actually changed.
 * - `editorBlur` fires when the editor loses focus; pending changes are flushed first.
 * - A value beyond {@link MARKDOWN_EDITOR_LIMITS} (too large or crafted to make the parser slow
 *   or overflow its stack), or one the engine overflows its stack on (`RangeError`), is shown as
 *   plain text with a notice instead; content marked `agEditorFallbackAction` (e.g. a link to
 *   another way of editing) is shown with the notice. The fallback never emits `valueChange`:
 *   what is shown stays the value. Other engine failures show "Editor konnte nicht geladen
 *   werden".
 */
@Component({
  selector: 'ag-markdown-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  styleUrl: './markdown-editor.component.css',
  host: {
    class: 'ag-md',
    '[class.ag-md-readonly]': 'readonly()',
    '[attr.aria-busy]': '!ready() && !fallback() && !loadFailed()',
  },
  template: `
    @if (!readonly() && !fallback()) {
      <div class="ag-md-toolbar" role="toolbar" aria-label="Formatierung">
        @for (item of toolbar; track item.command) {
          <button
            type="button"
            class="ag-md-tool"
            [title]="item.title"
            [attr.aria-label]="item.title"
            [attr.data-command]="item.command"
            [disabled]="!ready()"
            (mousedown)="$event.preventDefault()"
            (click)="run(item.command)"
          >
            {{ item.label }}
          </button>
        }
      </div>
    }
    @if (fallback()) {
      <div
        class="flex flex-wrap items-center gap-x-3 gap-y-2 border-b px-4 py-2 text-sm text-muted-foreground"
        role="note"
        data-testid="editor-fallback-notice"
      >
        <span
          >Dieser Text ist zu groß oder zu komplex für den Editor. Er wird als reiner Text
          angezeigt.</span
        >
        <ng-content select="[agEditorFallbackAction]" />
      </div>
      <div
        class="min-h-32 px-4 py-3 font-mono text-sm break-words whitespace-pre-wrap"
        data-testid="editor-fallback-text"
        [textContent]="value()"
      ></div>
    } @else if (loadFailed()) {
      <div class="ag-md-loading ag-md-error" role="alert" data-testid="load-error">
        Editor konnte nicht geladen werden.
      </div>
    } @else if (!ready()) {
      <div class="ag-md-loading">Editor wird geladen …</div>
    }
    <div #host class="ag-md-body" data-testid="markdown-editor-body" [hidden]="fallback()"></div>
  `,
})
export class MarkdownEditorComponent {
  readonly value = input('');
  readonly readonly = input(false);
  readonly placeholder = input('');
  /** Debounce for `valueChange` in milliseconds. */
  readonly debounceMs = input(300);

  readonly valueChange = output<string>();
  readonly editorBlur = output<void>();

  protected readonly toolbar = TOOLBAR;
  protected readonly ready = signal(false);
  /** Set when the engine chunk fails to load (e.g. offline after a deploy) or the engine breaks. */
  protected readonly loadFailed = signal(false);
  /**
   * The value is shown as plain text: it exceeds {@link MARKDOWN_EDITOR_LIMITS} or the engine
   * failed on it. Stays set for this instance (a new record view creates a new editor).
   */
  private readonly fallbackShown = signal(false);
  readonly fallback = this.fallbackShown.asReadonly();

  private readonly host = viewChild.required<ElementRef<HTMLElement>>('host');
  private readonly loadFactory = inject(MARKDOWN_EDITOR_FACTORY);
  private readonly limits = inject(MARKDOWN_EDITOR_LIMITS);
  private readonly pendingTasks = inject(PendingTasks);

  private handle: MarkdownEditorHandle | null = null;
  private destroyed = false;
  private timer: ReturnType<typeof setTimeout> | null = null;
  /** Markdown as last agreed between editor and consumer (set from outside or emitted). */
  private lastMarkdown = '';
  /**
   * `lastMarkdown` is the engine's serialization. While read-only it is the value as given
   * instead: nothing can change the document, and serializing costs up to a third of opening a
   * large text. It is serialized when the editor becomes writable.
   */
  private serialized = true;
  /** Last emitted value; an echo of it via `value` must not reset the document. */
  private lastEmitted: string | null = null;

  constructor() {
    afterNextRender(() => {
      void this.pendingTasks.run(() => this.safeMount());
    });

    effect(() => {
      const value = this.value();
      if (!this.ready()) {
        return;
      }
      untracked(() => this.applyExternalValue(value));
    });

    effect(() => {
      const readonly = this.readonly();
      if (this.ready()) {
        untracked(() => this.applyReadonly(readonly));
      }
    });

    effect(() => {
      const placeholder = this.placeholder();
      if (this.ready()) {
        this.handle?.setPlaceholder(placeholder);
      }
    });

    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.clearTimer();
      this.handle?.destroy();
      this.handle = null;
    });
  }

  /** Current Markdown of the editor, or the last known value while the engine loads. */
  getMarkdown(): string {
    return this.handle?.getMarkdown() ?? untracked(this.value);
  }

  focus(): void {
    this.handle?.focus();
  }

  protected run(command: MarkdownCommand): void {
    this.handle?.run(command);
  }

  /** Whether the engine must not see `markdown` (checked before every parse). */
  private tooComplex(markdown: string): boolean {
    return exceedsMarkdownLimits(markdown, this.limits);
  }

  private async safeMount(): Promise<void> {
    if (this.tooComplex(untracked(this.value))) {
      this.fallbackShown.set(true);
      return;
    }
    let factory: MarkdownEditorFactory;
    try {
      factory = await this.loadFactory();
    } catch (error) {
      if (!this.destroyed) {
        this.loadFailed.set(true);
      }
      console.error('Failed to load editor engine', error);
      return;
    }
    if (this.destroyed) {
      return;
    }
    const value = untracked(this.value);
    if (this.tooComplex(value)) {
      this.fallbackShown.set(true); // the value changed while the engine loaded
      return;
    }
    let handle: MarkdownEditorHandle | null = null;
    try {
      handle = await factory({
        root: this.host().nativeElement,
        initialValue: value,
        readonly: untracked(this.readonly),
        placeholder: untracked(this.placeholder),
        onDocChange: () => this.scheduleFlush(),
        onBlur: () => {
          this.flush();
          this.editorBlur.emit();
        },
      });
      if (this.destroyed) {
        handle.destroy();
        return;
      }
      this.agree(handle, value);
    } catch (error) {
      handle?.destroy();
      this.engineFailed(error);
      return;
    }
    this.handle = handle;
    this.ready.set(true);
  }

  private applyExternalValue(value: string): void {
    if (!this.handle || value === this.lastEmitted || value === this.lastMarkdown) {
      return;
    }
    // External value wins over unsent local edits.
    this.clearTimer();
    this.lastEmitted = null;
    if (this.tooComplex(value)) {
      this.dropEngine();
      this.fallbackShown.set(true);
      return;
    }
    try {
      this.handle.setMarkdown(value);
      this.agree(this.handle, value);
    } catch (error) {
      this.dropEngine();
      this.engineFailed(error);
    }
  }

  /** `value` is in the editor now: remember it (serialized, unless read-only). */
  private agree(handle: MarkdownEditorHandle, value: string): void {
    this.serialized = !untracked(this.readonly);
    this.lastMarkdown = this.serialized ? handle.getMarkdown() : value;
  }

  private applyReadonly(readonly: boolean): void {
    const handle = this.handle;
    if (!handle) {
      return;
    }
    handle.setReadonly(readonly);
    if (!readonly && !this.serialized) {
      try {
        this.lastMarkdown = handle.getMarkdown();
        this.serialized = true;
      } catch (error) {
        this.dropEngine();
        this.engineFailed(error);
      }
    }
  }

  /** Destroys the engine: from now on the value is shown as plain text. */
  private dropEngine(): void {
    this.handle?.destroy();
    this.handle = null;
    this.ready.set(false);
  }

  /**
   * The engine failed on the text. A `RangeError` (stack overflow on nesting, or a too large
   * structure) is the text's fault: shown as plain text. Anything else is a broken editor and
   * reported as such.
   */
  private engineFailed(error: unknown): void {
    if (this.destroyed) {
      return;
    }
    this.host().nativeElement.replaceChildren();
    if (error instanceof RangeError) {
      this.fallbackShown.set(true);
      // not console.error: the text is shown, nothing is broken for the user
      console.warn('Markdown editor failed on this text, showing it as plain text', error);
    } else {
      this.loadFailed.set(true);
      console.error('Markdown editor failed', error);
    }
  }

  private scheduleFlush(): void {
    this.clearTimer();
    this.timer = setTimeout(() => this.flush(), this.debounceMs());
  }

  /**
   * Emits a pending (still debounced) change right away. Callers that are about to save or leave
   * the page use it; emitting while the component is being destroyed is not possible (NG0953).
   */
  flush(): void {
    this.clearTimer();
    if (!this.handle || !this.serialized) {
      return; // not serialized: read-only since the value was set, nothing to emit
    }
    const markdown = this.handle.getMarkdown();
    if (markdown === this.lastMarkdown) {
      return;
    }
    this.lastMarkdown = markdown;
    this.lastEmitted = markdown;
    this.valueChange.emit(markdown);
  }

  private clearTimer(): void {
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }
}
