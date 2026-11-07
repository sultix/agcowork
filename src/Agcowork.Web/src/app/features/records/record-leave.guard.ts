import { CanDeactivateFn } from '@angular/router';

/** A page that may hold unsaved edits and decides whether it can be left. */
export interface CanLeave {
  canLeave(): Promise<boolean>;
}

/** Saves pending edits before the document view is left; asks before discarding what cannot be saved. */
export const recordLeaveGuard: CanDeactivateFn<CanLeave> = (component) => component.canLeave();
