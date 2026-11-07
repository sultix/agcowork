import { Injectable, Signal, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRouteSnapshot, NavigationEnd, Router } from '@angular/router';
import { filter, map } from 'rxjs';

/** Name of the route parameter carrying the workspace slug (`/w/:workspaceSlug`). */
export const WORKSPACE_SLUG_PARAM = 'workspaceSlug';

/**
 * Holds the active workspace, derived from the URL. The URL is the single source of truth so
 * links stay shareable; components only read from this service.
 */
@Injectable({ providedIn: 'root' })
export class WorkspaceContext {
  private readonly router = inject(Router);

  readonly slug: Signal<string | null> = toSignal(
    this.router.events.pipe(
      filter((event) => event instanceof NavigationEnd),
      map(() => this.currentSlug()),
    ),
    { initialValue: this.currentSlug() },
  );

  private currentSlug(): string | null {
    return findParam(this.router.routerState.snapshot.root, WORKSPACE_SLUG_PARAM);
  }
}

function findParam(route: ActivatedRouteSnapshot, name: string): string | null {
  let current: ActivatedRouteSnapshot | null = route;
  while (current) {
    const value = current.paramMap.get(name);
    if (value !== null) {
      return value;
    }
    current = current.firstChild;
  }
  return null;
}
