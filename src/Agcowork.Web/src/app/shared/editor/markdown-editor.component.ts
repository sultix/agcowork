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
import {
  MARKDOWN_EDITOR_FACTORY,
  MarkdownCommand,
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
 */
@Component({
  selector: 'ag-markdown-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  styleUrl: './markdown-editor.component.css',
  host: {
    class: 'ag-md',
    '[class.ag-md-readonly]': 'readonly()',
    '[attr.aria-busy]': '!ready()',
  },
  template: `
    @if (!readonly()) {
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
    @if (loadFailed()) {
      <div class="ag-md-loading ag-md-error" role="alert" data-testid="load-error">
        Editor konnte nicht geladen werden.
      </div>
    } @else if (!ready()) {
      <div class="ag-md-loading">Editor wird geladen …</div>
    }
    <div #host class="ag-md-body" data-testid="markdown-editor-body"></div>
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
  /** Set when the engine chunk or its initialization fails (e.g. offline after a deploy). */
  protected readonly loadFailed = signal(false);

  private readonly host = viewChild.required<ElementRef<HTMLElement>>('host');
  private readonly loadFactory = inject(MARKDOWN_EDITOR_FACTORY);
  private readonly pendingTasks = inject(PendingTasks);

  private handle: MarkdownEditorHandle | null = null;
  private destroyed = false;
  private timer: ReturnType<typeof setTimeout> | null = null;
  /** Markdown as last agreed between editor and consumer (set from outside or emitted). */
  private lastMarkdown = '';
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
        this.handle?.setReadonly(readonly);
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

  private async safeMount(): Promise<void> {
    try {
      await this.mount();
    } catch (error) {
      if (!this.destroyed) {
        this.loadFailed.set(true);
      }
      console.error('Failed to load editor engine', error);
    }
  }

  private async mount(): Promise<void> {
    const factory = await this.loadFactory();
    if (this.destroyed) {
      return;
    }
    const handle = await factory({
      root: this.host().nativeElement,
      initialValue: untracked(this.value),
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
    this.handle = handle;
    this.lastMarkdown = handle.getMarkdown();
    this.ready.set(true);
  }

  private applyExternalValue(value: string): void {
    if (!this.handle || value === this.lastEmitted || value === this.lastMarkdown) {
      return;
    }
    // External value wins over unsent local edits.
    this.clearTimer();
    this.handle.setMarkdown(value);
    this.lastMarkdown = this.handle.getMarkdown();
    this.lastEmitted = null;
  }

  private scheduleFlush(): void {
    this.clearTimer();
    this.timer = setTimeout(() => this.flush(), this.debounceMs());
  }

  private flush(): void {
    this.clearTimer();
    if (!this.handle) {
      return;
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
