import { CanDeactivateFn } from '@angular/router';
import type { RecordDocumentComponent } from './record-document.component';

/** Saves pending edits before the document view is left; asks before discarding what cannot be saved. */
export const recordLeaveGuard: CanDeactivateFn<RecordDocumentComponent> = (component) =>
  component.canLeave();
