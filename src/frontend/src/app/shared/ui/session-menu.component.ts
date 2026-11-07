import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { SessionStore } from '../../core/auth/session.store';

const KIND_LABELS: Record<string, string> = { human: 'Mensch', agent: 'Agent' };

/** Header block with the signed-in actor (name, kind) and a logout button. */
@Component({
  selector: 'ag-session-menu',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (session.me(); as me) {
      <div class="flex items-center gap-3 text-sm">
        <span data-testid="session-actor">
          <span class="font-medium">{{ me.name }}</span>
          <span class="text-muted"> · {{ kindLabel() }}</span>
        </span>
        @if (isInstanceAdmin()) {
          <a
            routerLink="/admin"
            class="rounded-md border border-border px-2 py-1 hover:bg-surface-hover"
            data-testid="admin-link"
            >Administration</a
          >
        }
        <button
          type="button"
          class="rounded-md border border-border px-2 py-1 hover:bg-surface-hover disabled:opacity-60"
          data-testid="logout"
          [disabled]="loggingOut()"
          (click)="logout()"
        >
          Abmelden
        </button>
      </div>
    }
  `,
})
export class SessionMenuComponent {
  protected readonly session = inject(SessionStore);
  private readonly router = inject(Router);

  protected readonly loggingOut = signal(false);
  protected readonly isInstanceAdmin = computed(
    () => this.session.me()?.instanceScopes.includes('instance:admin') ?? false,
  );
  protected readonly kindLabel = computed(() => {
    const kind = this.session.me()?.kind;
    return kind ? (KIND_LABELS[kind] ?? kind) : '';
  });

  protected async logout(): Promise<void> {
    this.loggingOut.set(true);
    try {
      await this.session.logout();
    } finally {
      this.loggingOut.set(false);
      await this.router.navigateByUrl('/login');
    }
  }
}
