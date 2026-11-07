import { ComponentFixture, TestBed } from '@angular/core/testing';
import { DARK_CLASS, THEME_STORAGE_KEY, ThemeService } from '../../core/theme/theme.service';
import { ThemeToggleComponent } from './theme-toggle.component';

describe('ThemeToggleComponent', () => {
  let fixture: ComponentFixture<ThemeToggleComponent>;

  // The menu renders into the CDK overlay container in <body>.
  const inBody = (id: string) => document.body.querySelector<HTMLElement>(`[data-testid="${id}"]`);
  const trigger = () =>
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      '[data-testid="theme-toggle"]',
    )!;

  async function openMenu(): Promise<void> {
    trigger().click();
    await fixture.whenStable();
  }

  beforeEach(async () => {
    localStorage.clear();
    document.documentElement.classList.remove(DARK_CLASS);
    fixture = TestBed.createComponent(ThemeToggleComponent);
    await fixture.whenStable();
  });

  afterEach(() => {
    localStorage.clear();
    document.documentElement.classList.remove(DARK_CLASS);
  });

  it('render_IsIconButtonNamedAfterTheCurrentMode', () => {
    expect(trigger().getAttribute('aria-label')).toBe('Farbschema: System');
    expect(trigger().getAttribute('aria-haspopup')).toBe('menu');
  });

  it('open_ShowsThreeModesWithCurrentChecked', async () => {
    await openMenu();

    const options = ['system', 'light', 'dark'].map((mode) => inBody(`theme-option-${mode}`)!);
    expect(options.map((o) => o.textContent?.trim())).toEqual(['System', 'Hell', 'Dunkel']);
    expect(options.map((o) => o.getAttribute('role'))).toEqual([
      'menuitemradio',
      'menuitemradio',
      'menuitemradio',
    ]);
    expect(options.map((o) => o.getAttribute('aria-checked'))).toEqual(['true', 'false', 'false']);
  });

  it.each([
    ['dark', true, 'Dunkel'],
    ['light', false, 'Hell'],
  ] as const)('select_%s_SetsModeAndClosesMenu', async (mode, dark, label) => {
    await openMenu();

    inBody(`theme-option-${mode}`)!.click();
    await fixture.whenStable();

    expect(TestBed.inject(ThemeService).mode()).toBe(mode);
    expect(document.documentElement.classList.contains(DARK_CLASS)).toBe(dark);
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe(mode);
    expect(trigger().getAttribute('aria-label')).toBe(`Farbschema: ${label}`);
    await vi.waitFor(() => expect(inBody('theme-menu')).toBeNull());
  });

  it('select_System_RemovesStoredMode', async () => {
    TestBed.inject(ThemeService).setMode('dark');
    await fixture.whenStable();
    await openMenu();

    inBody('theme-option-system')!.click();
    await fixture.whenStable();

    expect(TestBed.inject(ThemeService).mode()).toBe('system');
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBeNull();
  });
});
