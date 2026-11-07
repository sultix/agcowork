import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

/** Modal confirmation for destructive actions. The parent owns visibility via `open`. */
@Component({
  selector: 'ag-confirm-dialog',
  host: { '(document:keydown.escape)': 'onEscape()' },
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (open()) {
      <div class="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
        <div
          class="w-full max-w-md rounded-lg border border-border bg-surface p-6"
          role="alertdialog"
          aria-modal="true"
          aria-labelledby="confirm-title"
          data-testid="confirm-dialog"
        >
          <h2 id="confirm-title" class="text-lg font-semibold">{{ title() }}</h2>
          <p class="mt-2 text-sm text-muted">{{ message() }}</p>
          <div class="mt-6 flex justify-end gap-2">
            <button
              type="button"
              class="rounded-md border border-border px-3 py-1.5 text-sm hover:bg-surface-hover"
              data-testid="confirm-cancel"
              (click)="cancelled.emit()"
            >
              Abbrechen
            </button>
            <button
              type="button"
              class="rounded-md bg-danger px-3 py-1.5 text-sm font-medium text-white disabled:opacity-60"
              data-testid="confirm-ok"
              [disabled]="busy()"
              (click)="confirmed.emit()"
            >
              {{ confirmLabel() }}
            </button>
          </div>
        </div>
      </div>
    }
  `,
})
export class ConfirmDialogComponent {
  readonly open = input(false);
  readonly title = input('Bist du sicher?');
  readonly message = input('');
  readonly confirmLabel = input('Bestätigen');
  readonly busy = input(false);
  readonly confirmed = output();
  readonly cancelled = output();

  protected onEscape(): void {
    if (this.open()) {
      this.cancelled.emit();
    }
  }
}
