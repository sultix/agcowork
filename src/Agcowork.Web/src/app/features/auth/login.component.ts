import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';

/** Login placeholder. Real authentication follows in phase 1. */
@Component({
  selector: 'ag-login',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="flex min-h-screen items-center justify-center p-6">
      <div class="w-full max-w-sm rounded-lg border border-border bg-surface p-6">
        <h1 class="text-lg font-semibold">Anmelden</h1>
        <p class="mt-2 text-sm text-muted">Die Anmeldung folgt in Phase 1.</p>
        <a
          routerLink="/workspaces"
          class="mt-4 inline-block rounded-md bg-accent px-3 py-2 text-sm font-medium text-accent-fg"
          >Weiter zu den Workspaces</a
        >
      </div>
    </main>
  `,
})
export class LoginComponent {}
