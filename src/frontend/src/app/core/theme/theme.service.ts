import { DOCUMENT, Injectable, computed, effect, inject, signal } from '@angular/core';

export type ThemeMode = 'system' | 'light' | 'dark';

export const THEME_STORAGE_KEY = 'ag-theme';

const DARK_QUERY = '(prefers-color-scheme: dark)';

/**
 * Manual theme override. `system` removes `data-theme` so `prefers-color-scheme` applies;
 * `light`/`dark` set `data-theme` on the root element.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly document = inject(DOCUMENT);

  readonly mode = signal<ThemeMode>(this.readStored());

  private readonly systemDark = signal(false);

  /**
   * Effective theme after resolving `system`. For widgets that cannot read CSS tokens directly
   * (e.g. Monaco needs a named theme).
   */
  readonly isDark = computed(
    () => this.mode() === 'dark' || (this.mode() === 'system' && this.systemDark()),
  );

  constructor() {
    const media = this.document.defaultView?.matchMedia?.(DARK_QUERY);
    if (media) {
      this.systemDark.set(media.matches);
      media.addEventListener?.('change', (event) => this.systemDark.set(event.matches));
    }

    effect(() => {
      const mode = this.mode();
      const root = this.document.documentElement;
      if (mode === 'system') {
        root.removeAttribute('data-theme');
        this.writeStored(null);
      } else {
        root.setAttribute('data-theme', mode);
        this.writeStored(mode);
      }
    });
  }

  cycle(): void {
    const order: ThemeMode[] = ['system', 'light', 'dark'];
    this.mode.update((mode) => order[(order.indexOf(mode) + 1) % order.length]);
  }

  private readStored(): ThemeMode {
    try {
      const stored = this.document.defaultView?.localStorage.getItem(THEME_STORAGE_KEY);
      return stored === 'light' || stored === 'dark' ? stored : 'system';
    } catch {
      // Storage can be blocked (privacy modes, sandboxed iframes); fall back to the system theme.
      return 'system';
    }
  }

  private writeStored(mode: ThemeMode | null): void {
    try {
      const storage = this.document.defaultView?.localStorage;
      if (mode === null) {
        storage?.removeItem(THEME_STORAGE_KEY);
      } else {
        storage?.setItem(THEME_STORAGE_KEY, mode);
      }
    } catch {
      // Persisting is best effort; the attribute is already applied.
    }
  }
}
