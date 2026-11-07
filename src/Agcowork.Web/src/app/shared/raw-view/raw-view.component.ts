import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  PendingTasks,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  linkedSignal,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { ThemeService } from '../../core/theme/theme.service';
import { CODE_EDITOR_FACTORY, CodeEditorHandle, RawLanguage } from './code-editor.adapter';

type JsonCheck = { valid: true } | { valid: false; error: string };

function checkJson(text: string): JsonCheck {
  try {
    JSON.parse(text);
    return { valid: true };
  } catch (error) {
    return { valid: false, error: error instanceof Error ? error.message : String(error) };
  }
}

/**
 * Source view with syntax highlighting for JSON and Markdown (Monaco, loaded lazily via
 * {@link CODE_EDITOR_FACTORY}).
 *
 * - `readonly` locks the view; otherwise the user can switch between reading and editing.
 * - `valueChange` fires on every user edit, never for `value` set from outside.
 * - For `json`, `isValid` and `error` reflect whether the current text parses.
 */
@Component({
  selector: 'ag-raw-view',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'flex flex-col rounded-lg border border-border bg-surface',
    '[attr.data-language]': 'language()',
  },
  template: `
    <div class="flex items-center justify-between gap-2 border-b border-border px-3 py-1.5 text-xs">
      <span class="font-mono uppercase text-muted">{{ language() }}</span>
      <div class="flex items-center gap-3">
        @if (language() === 'json') {
          @if (isValid()) {
            <span class="text-success" data-testid="raw-status">gültig</span>
          } @else {
            <span class="text-danger" data-testid="raw-status" role="alert"
              >ungültig: {{ error() }}</span
            >
          }
        }
        @if (!readonly()) {
          <button
            type="button"
            class="rounded border border-border px-2 py-0.5 hover:bg-surface-hover"
            data-testid="raw-mode-toggle"
            [attr.aria-pressed]="editing()"
            (click)="editing.set(!editing())"
          >
            {{ editing() ? 'Lesen' : 'Bearbeiten' }}
          </button>
        }
      </div>
    </div>
    @if (loadFailed()) {
      <div class="px-3 py-2 text-sm text-danger" role="alert" data-testid="load-error">
        Ansicht konnte nicht geladen werden.
      </div>
    } @else if (!ready()) {
      <div class="px-3 py-2 text-sm text-muted">Ansicht wird geladen …</div>
    }
    <div #host class="min-h-48 flex-1" data-testid="raw-view-body"></div>
  `,
})
export class RawViewComponent {
  readonly value = input('');
  readonly language = input<RawLanguage>('json');
  readonly readonly = input(false);

  readonly valueChange = output<string>();

  /** Whether the view currently accepts edits (only switchable when not `readonly`). */
  readonly editing = linkedSignal(() => !this.readonly());

  /** Current text including unsent local edits. */
  readonly text = linkedSignal(() => this.value());

  private readonly jsonCheck = computed<JsonCheck>(() =>
    this.language() === 'json' ? checkJson(this.text()) : { valid: true },
  );
  readonly isValid = computed(() => this.jsonCheck().valid);
  readonly error = computed(() => {
    const check = this.jsonCheck();
    return check.valid ? null : check.error;
  });

  protected readonly ready = signal(false);
  /** Set when the engine chunk or its initialization fails (e.g. offline after a deploy). */
  protected readonly loadFailed = signal(false);

  private readonly host = viewChild.required<ElementRef<HTMLElement>>('host');
  private readonly loadFactory = inject(CODE_EDITOR_FACTORY);
  private readonly pendingTasks = inject(PendingTasks);
  private readonly theme = inject(ThemeService);

  private handle: CodeEditorHandle | null = null;
  private destroyed = false;

  constructor() {
    afterNextRender(() => {
      void this.pendingTasks.run(() => this.safeMount());
    });

    effect(() => {
      const value = this.value();
      if (this.ready() && this.handle && value !== this.handle.getValue()) {
        this.handle.setValue(value);
      }
    });

    effect(() => {
      const language = this.language();
      if (this.ready()) {
        this.handle?.setLanguage(language);
      }
    });

    effect(() => {
      const locked = this.readonly() || !this.editing();
      if (this.ready()) {
        this.handle?.setReadonly(locked);
      }
    });

    effect(() => {
      const dark = this.theme.isDark();
      if (this.ready()) {
        this.handle?.setDark(dark);
      }
    });

    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.handle?.destroy();
      this.handle = null;
    });
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
      value: untracked(this.text),
      language: untracked(this.language),
      readonly: untracked(() => this.readonly() || !this.editing()),
      dark: untracked(this.theme.isDark),
      onChange: (value) => {
        this.text.set(value);
        this.valueChange.emit(value);
      },
    });
    if (this.destroyed) {
      handle.destroy();
      return;
    }
    this.handle = handle;
    this.ready.set(true);
  }
}
