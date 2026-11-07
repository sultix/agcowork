import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { BrnDialogState } from '@spartan-ng/brain/dialog';
import { HlmAlertDialogImports } from '@spartan-ng/helm/alert-dialog';
import { HlmButton } from '@spartan-ng/helm/button';

/**
 * Modal confirmation for destructive actions (Helm alert dialog). The parent owns visibility via
 * `open`; Brain provides the focus trap and closes on Escape. Closing from inside (Escape,
 * "Abbrechen") emits `cancelled`, the parent then sets `open` to false. Confirming does not close
 * by itself, so the parent can keep the dialog open with `busy` while the action runs.
 * The dialog renders into the CDK overlay container (end of `<body>`), not inside the host;
 * Brain links title and description via `aria-labelledby`/`aria-describedby`.
 */
@Component({
  selector: 'ag-confirm-dialog',
  imports: [HlmAlertDialogImports, HlmButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-alert-dialog [state]="open() ? 'open' : 'closed'" (stateChanged)="onStateChanged($event)">
      <hlm-alert-dialog-content *hlmAlertDialogPortal data-testid="confirm-dialog">
        <hlm-alert-dialog-header>
          <h2 hlmAlertDialogTitle>{{ title() }}</h2>
          <p hlmAlertDialogDescription>{{ message() }}</p>
        </hlm-alert-dialog-header>
        <hlm-alert-dialog-footer>
          <button hlmAlertDialogCancel data-testid="confirm-cancel">Abbrechen</button>
          <button
            hlmBtn
            type="button"
            variant="destructive"
            data-testid="confirm-ok"
            [disabled]="busy()"
            (click)="confirmed.emit()"
          >
            {{ confirmLabel() }}
          </button>
        </hlm-alert-dialog-footer>
      </hlm-alert-dialog-content>
    </hlm-alert-dialog>
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

  /** Brain closed the dialog itself (Escape, cancel button) while the parent still wants it open. */
  protected onStateChanged(state: BrnDialogState): void {
    if (state === 'closed' && this.open()) {
      this.cancelled.emit();
    }
  }
}
