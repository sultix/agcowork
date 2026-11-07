import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { BadgeComponent } from '../../shared/ui/badge.component';
import type { RevisionConflict } from './record-save.service';

/** "Von jemand anderem geändert": the dialog for a 409 `revision_mismatch` (document and raw view). */
@Component({
  selector: 'ag-conflict-dialog',
  imports: [BadgeComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div
        class="w-full max-w-md rounded-lg border border-border bg-surface p-6"
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="conflict-title"
        data-testid="conflict-dialog"
      >
        <h2 id="conflict-title" class="text-lg font-semibold">Von jemand anderem geändert</h2>
        <p class="mt-2 text-sm text-muted">
          Der Record wurde gespeichert, seit du ihn geöffnet hast
          @if (conflict().currentRevision !== null) {
            (jetzt Revision {{ conflict().currentRevision }})
          }
          .
        </p>
        @if (conflict().changedKeys?.length) {
          <p class="mt-2 text-sm">Geändert wurde:</p>
          <ul class="mt-1 flex flex-wrap gap-1" data-testid="conflict-keys">
            @for (key of conflict().changedKeys; track key) {
              <li>
                <ag-badge>{{ key }}</ag-badge>
              </li>
            }
          </ul>
        }
        <p class="mt-3 text-sm text-muted">
          „Neu laden“ verwirft deine Änderungen. „Überschreiben“ speichert sie über den neuen Stand.
        </p>
        <div class="mt-6 flex justify-end gap-2">
          <button
            type="button"
            class="rounded-md border border-border px-3 py-1.5 text-sm hover:bg-surface-hover"
            data-testid="conflict-reload"
            (click)="reload.emit()"
          >
            Neu laden
          </button>
          <button
            type="button"
            class="rounded-md bg-danger px-3 py-1.5 text-sm font-medium text-white disabled:opacity-60"
            data-testid="conflict-overwrite"
            [disabled]="saving()"
            (click)="overwrite.emit()"
          >
            Überschreiben
          </button>
        </div>
      </div>
    </div>
  `,
})
export class ConflictDialogComponent {
  readonly conflict = input.required<RevisionConflict>();
  readonly saving = input(false);

  readonly reload = output();
  readonly overwrite = output();
}
