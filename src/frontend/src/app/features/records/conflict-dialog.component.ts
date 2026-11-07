import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { HlmAlertDialogImports } from '@spartan-ng/helm/alert-dialog';
import { HlmButton } from '@spartan-ng/helm/button';
import { BadgeComponent } from '../../shared/ui/badge.component';
import type { RevisionConflict } from './record-save.service';

/** "Von jemand anderem geändert": the dialog for a 409 `revision_mismatch` (document and raw view). */
@Component({
  selector: 'ag-conflict-dialog',
  imports: [BadgeComponent, HlmAlertDialogImports, HlmButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-alert-dialog state="open" [disableClose]="true" autoFocus="first-heading">
      <hlm-alert-dialog-content *hlmAlertDialogPortal data-testid="conflict-dialog">
        <hlm-alert-dialog-header>
          <h2 hlmAlertDialogTitle>Von jemand anderem geändert</h2>
          <p hlmAlertDialogDescription>
            Der Record wurde gespeichert, seit du ihn geöffnet hast
            @if (conflict().currentRevision !== null) {
              (jetzt Revision {{ conflict().currentRevision }})
            }
            .
          </p>
        </hlm-alert-dialog-header>
        @if (conflict().changedKeys?.length) {
          <div class="flex flex-col gap-1">
            <p class="text-sm">Geändert wurde:</p>
            <ul class="flex flex-wrap gap-1" data-testid="conflict-keys">
              @for (key of conflict().changedKeys; track key) {
                <li>
                  <ag-badge>{{ key }}</ag-badge>
                </li>
              }
            </ul>
          </div>
        }
        <p class="text-sm text-muted-foreground">
          „Neu laden“ verwirft deine Änderungen. „Überschreiben“ speichert sie über den neuen Stand.
        </p>
        <hlm-alert-dialog-footer>
          <button
            type="button"
            hlmBtn
            variant="outline"
            data-testid="conflict-reload"
            (click)="reload.emit()"
          >
            Neu laden
          </button>
          <button
            type="button"
            hlmBtn
            variant="destructive"
            data-testid="conflict-overwrite"
            [disabled]="saving()"
            (click)="overwrite.emit()"
          >
            Überschreiben
          </button>
        </hlm-alert-dialog-footer>
      </hlm-alert-dialog-content>
    </hlm-alert-dialog>
  `,
})
export class ConflictDialogComponent {
  readonly conflict = input.required<RevisionConflict>();
  readonly saving = input(false);

  readonly reload = output();
  readonly overwrite = output();
}
