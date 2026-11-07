import { DOCUMENT, Injectable, computed, effect, inject, signal } from '@angular/core';

export type ThemeMode = 'system' | 'light' | 'dark';

/** The three modes in menu order. */
export const THEME_MODES: readonly ThemeMode[] = ['system', 'light', 'dark'];

/** Also read by the inline script in `index.html` (applies the theme before the first paint). */
export const THEME_STORAGE_KEY = 'ag-theme';

/** Class on `<html>` that switches the shadcn/spartan theme to dark (`@custom-variant dark`). */
export const DARK_CLASS = 'dark';

const DARK_QUERY = '(prefers-color-scheme: dark)';

/**
 * Theme mode `system | light | dark`. Sets the `dark` class on the root element from the
 * effective theme (`isDark()`), follows system changes at runtime while in `system`, and persists
 * explicit choices (`system` removes the stored value).
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly document = inject(DOCUMENT);

  readonly mode = signal<ThemeMode>(this.readStored());

  private readonly systemDark = signal(false);

  /**
   * Effective theme after resolving `system`. Also for widgets that cannot read CSS tokens
   * directly (e.g. Monaco needs a named theme).
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
      this.document.documentElement.classList.toggle(DARK_CLASS, this.isDark());
    });

    effect(() => {
      const mode = this.mode();
      this.writeStored(mode === 'system' ? null : mode);
    });
  }

  setMode(mode: ThemeMode): void {
    this.mode.set(mode);
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
      // Persisting is best effort; the class is already applied.
    }
  }
}
