import { TestBed } from '@angular/core/testing';
import { THEME_STORAGE_KEY, ThemeService } from './theme.service';

describe('ThemeService', () => {
  const root = document.documentElement;

  beforeEach(() => {
    localStorage.clear();
    root.removeAttribute('data-theme');
  });

  afterEach(() => {
    vi.restoreAllMocks();
    localStorage.clear();
    root.removeAttribute('data-theme');
  });

  it('cycle_ThroughModes_SetsOrRemovesDataThemeAndPersists', () => {
    const theme = TestBed.inject(ThemeService);
    TestBed.tick();
    expect(theme.mode()).toBe('system');
    expect(root.hasAttribute('data-theme')).toBe(false);

    theme.cycle();
    TestBed.tick();
    expect(root.getAttribute('data-theme')).toBe('light');
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('light');

    theme.cycle();
    TestBed.tick();
    expect(root.getAttribute('data-theme')).toBe('dark');
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark');

    theme.cycle();
    TestBed.tick();
    expect(root.hasAttribute('data-theme')).toBe(false);
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBeNull();
  });

  it('mode_StoredValue_IsRestored', () => {
    localStorage.setItem(THEME_STORAGE_KEY, 'dark');
    const theme = TestBed.inject(ThemeService);
    TestBed.tick();
    expect(theme.mode()).toBe('dark');
    expect(root.getAttribute('data-theme')).toBe('dark');
  });

  it('mode_StorageThrows_FallsBackToSystemWithoutError', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('blocked', 'SecurityError');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('blocked', 'SecurityError');
    });

    const theme = TestBed.inject(ThemeService);
    TestBed.tick();
    expect(theme.mode()).toBe('system');

    theme.cycle();
    expect(() => TestBed.tick()).not.toThrow();
    expect(root.getAttribute('data-theme')).toBe('light');
  });

  it('isDark_ResolvesModeAndSystemPreference', () => {
    let listener: ((event: MediaQueryListEvent) => void) | undefined;
    const media = {
      matches: true,
      addEventListener: (_: string, fn: (event: MediaQueryListEvent) => void) => (listener = fn),
    };
    const view = document.defaultView as Window;
    Object.defineProperty(view, 'matchMedia', { configurable: true, value: () => media });
    onTestFinished(() => {
      delete (view as Partial<Window>).matchMedia;
    });

    const theme = TestBed.inject(ThemeService);
    expect(theme.isDark()).toBe(true);

    listener?.({ matches: false } as MediaQueryListEvent);
    expect(theme.isDark()).toBe(false);

    theme.mode.set('dark');
    expect(theme.isDark()).toBe(true);
    theme.mode.set('light');
    expect(theme.isDark()).toBe(false);
  });

  it('isDark_WithoutMatchMedia_FollowsExplicitModeOnly', () => {
    const theme = TestBed.inject(ThemeService);
    expect(theme.isDark()).toBe(false);
    theme.mode.set('dark');
    expect(theme.isDark()).toBe(true);
  });
});
