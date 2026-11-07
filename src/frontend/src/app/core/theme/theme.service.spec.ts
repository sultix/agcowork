import { TestBed } from '@angular/core/testing';
import { DARK_CLASS, THEME_STORAGE_KEY, ThemeMode, ThemeService } from './theme.service';

describe('ThemeService', () => {
  const root = document.documentElement;
  const view = document.defaultView as Window;

  /** Fake `matchMedia` for `(prefers-color-scheme: dark)`; `change(…)` simulates a system switch. */
  function fakeSystem(dark: boolean): { change(dark: boolean): void } {
    let listener: ((event: MediaQueryListEvent) => void) | undefined;
    const media = {
      matches: dark,
      addEventListener: (_: string, fn: (event: MediaQueryListEvent) => void) => (listener = fn),
    };
    Object.defineProperty(view, 'matchMedia', { configurable: true, value: () => media });
    onTestFinished(() => {
      delete (view as Partial<Window>).matchMedia;
    });
    return { change: (value) => listener?.({ matches: value } as MediaQueryListEvent) };
  }

  const isDarkClass = () => root.classList.contains(DARK_CLASS);

  beforeEach(() => {
    localStorage.clear();
    root.classList.remove(DARK_CLASS);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    localStorage.clear();
    root.classList.remove(DARK_CLASS);
  });

  it.each<[ThemeMode, boolean, boolean]>([
    ['system', false, false],
    ['system', true, true],
    ['light', false, false],
    ['light', true, false],
    ['dark', false, true],
    ['dark', true, true],
  ])('setMode_%s_SystemDark_%s_DarkClassIs_%s', (mode, systemDark, expected) => {
    fakeSystem(systemDark);
    const theme = TestBed.inject(ThemeService);

    theme.setMode(mode);
    TestBed.tick();

    expect(isDarkClass()).toBe(expected);
    expect(theme.isDark()).toBe(expected);
  });

  it('setMode_Explicit_PersistsAndSystemRemovesStoredValue', () => {
    const theme = TestBed.inject(ThemeService);

    theme.setMode('dark');
    TestBed.tick();
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark');

    theme.setMode('light');
    TestBed.tick();
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('light');

    theme.setMode('system');
    TestBed.tick();
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBeNull();
  });

  it('systemChange_InSystemMode_TogglesDarkClassAtRuntime', () => {
    const system = fakeSystem(false);
    const theme = TestBed.inject(ThemeService);
    TestBed.tick();
    expect(isDarkClass()).toBe(false);

    system.change(true);
    TestBed.tick();
    expect(isDarkClass()).toBe(true);
    expect(theme.isDark()).toBe(true);

    system.change(false);
    TestBed.tick();
    expect(isDarkClass()).toBe(false);
  });

  it('systemChange_InExplicitMode_IsIgnored', () => {
    const system = fakeSystem(false);
    const theme = TestBed.inject(ThemeService);
    theme.setMode('light');
    TestBed.tick();

    system.change(true);
    TestBed.tick();

    expect(isDarkClass()).toBe(false);
    expect(theme.isDark()).toBe(false);
  });

  it('mode_StoredValue_IsRestoredAndApplied', () => {
    localStorage.setItem(THEME_STORAGE_KEY, 'dark');
    const theme = TestBed.inject(ThemeService);
    TestBed.tick();

    expect(theme.mode()).toBe('dark');
    expect(isDarkClass()).toBe(true);
  });

  it('mode_StoredGarbage_FallsBackToSystem', () => {
    localStorage.setItem(THEME_STORAGE_KEY, 'purple');
    const theme = TestBed.inject(ThemeService);

    expect(theme.mode()).toBe('system');
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

    theme.setMode('dark');
    expect(() => TestBed.tick()).not.toThrow();
    expect(isDarkClass()).toBe(true);
  });

  it('isDark_WithoutMatchMedia_FollowsExplicitModeOnly', () => {
    const theme = TestBed.inject(ThemeService);
    expect(theme.isDark()).toBe(false);
    theme.setMode('dark');
    expect(theme.isDark()).toBe(true);
  });
});
