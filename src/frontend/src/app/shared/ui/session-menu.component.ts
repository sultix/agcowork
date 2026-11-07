import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideChevronsUpDown, lucideLogOut, lucideShield } from '@ng-icons/lucide';
import { HlmAvatarImports } from '@spartan-ng/helm/avatar';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmDropdownMenuImports } from '@spartan-ng/helm/dropdown-menu';
import { SessionStore } from '../../core/auth/session.store';

const KIND_LABELS: Record<string, string> = { human: 'Mensch', agent: 'Agent' };

/** Up to two initials from a display name ("Ada Lovelace" -> "AL", "Coder" -> "C"). */
export function initialsOf(name: string): string {
  const words = name.trim().split(/\s+/).filter(Boolean);
  if (words.length === 0) {
    return '?';
  }
  const first = words[0][0];
  const last = words.length > 1 ? words[words.length - 1][0] : '';
  return (first + last).toUpperCase();
}

/**
 * Signed-in actor as a dropdown menu: avatar with initials, name and kind on the trigger;
 * administration link (instance admins) and logout in the menu.
 */
@Component({
  selector: 'ag-session-menu',
  imports: [RouterLink, NgIcon, HlmAvatarImports, HlmButton, HlmDropdownMenuImports],
  providers: [provideIcons({ lucideChevronsUpDown, lucideLogOut, lucideShield })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (session.me(); as me) {
      <button
        hlmBtn
        variant="ghost"
        class="h-9 gap-2 px-1.5"
        data-testid="session-menu-trigger"
        [hlmDropdownMenuTrigger]="menu"
        align="end"
      >
        <span hlmAvatar size="sm">
          <span hlmAvatarFallback class="text-xs">{{ initials() }}</span>
        </span>
        <span class="hidden text-sm sm:inline" data-testid="session-actor">
          <span class="font-medium">{{ me.name }}</span>
          <span class="text-muted-foreground"> · {{ kindLabel() }}</span>
        </span>
        <ng-icon name="lucideChevronsUpDown" class="text-muted-foreground" />
      </button>

      <ng-template #menu>
        <hlm-dropdown-menu class="w-56" data-testid="session-menu">
          <div hlmDropdownMenuLabel class="text-foreground">
            <span class="block truncate text-sm font-medium">{{ me.name }}</span>
            <span class="block truncate text-xs font-normal text-muted-foreground">
              {{ kindLabel() }}{{ me.email ? ' · ' + me.email : '' }}
            </span>
          </div>
          <hlm-dropdown-menu-separator />
          @if (isInstanceAdmin()) {
            <a hlmDropdownMenuItem routerLink="/admin" data-testid="admin-link">
              <ng-icon name="lucideShield" />
              Administration
            </a>
            <hlm-dropdown-menu-separator />
          }
          <button
            hlmDropdownMenuItem
            data-testid="logout"
            [disabled]="loggingOut()"
            (triggered)="logout()"
          >
            <ng-icon name="lucideLogOut" />
            Abmelden
          </button>
        </hlm-dropdown-menu>
      </ng-template>
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
  protected readonly initials = computed(() => initialsOf(this.session.me()?.name ?? ''));

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
