import { cssColorToHex, parseCssColor } from './css-color';
import { monacoThemeColors } from './monaco-theme';

describe('cssColorToHex', () => {
  it.each([
    // shadcn "neutral" values; expected hex as listed in the Tailwind v4 palette.
    ['oklch(1 0 0)', '#ffffff'],
    ['oklch(0.145 0 0)', '#0a0a0a'],
    ['oklch(0.205 0 0)', '#171717'],
    ['oklch(0.556 0 0)', '#737373'],
    ['oklch(0.577 0.245 27.325)', '#e7000b'],
    ['oklch(0.527 0.154 150.069)', '#008236'],
    ['oklch(55.6% 0 none)', '#737373'],
  ])('oklch_%s_Is_%s', (value, expected) => {
    expect(cssColorToHex(value)).toBe(expected);
  });

  it.each([
    ['#4F46E5', '#4f46e5'],
    ['#fff', '#ffffff'],
    ['  #0b1120 ', '#0b1120'],
    ['rgb(255, 0, 0)', '#ff0000'],
    ['rgb(0 128 255 / 50%)', '#0080ff'],
  ])('hexOrRgb_%s_Is_%s', (value, expected) => {
    expect(cssColorToHex(value)).toBe(expected);
  });

  it('translucent_WithBackdrop_IsCompositedOntoIt', () => {
    // Dark theme border: 10 % white over the dark card color.
    expect(cssColorToHex('oklch(1 0 0 / 10%)', 'oklch(0.205 0 0)')).toBe('#2e2e2e');
    expect(cssColorToHex('#ffffff80', '#000000')).toBe('#808080');
  });

  it.each(['', 'var(--x)', 'color-mix(in oklab, red, blue)', '#12', 'oklch(1 0)', 'oklch(a b c)'])(
    'unknown_%s_IsUndefined',
    (value) => {
      expect(cssColorToHex(value)).toBeUndefined();
    },
  );

  it('parseCssColor_OklchAlpha_IsKept', () => {
    expect(parseCssColor('oklch(1 0 0 / 0.15)')?.a).toBeCloseTo(0.15);
  });
});

describe('monacoThemeColors', () => {
  it('variables_OklchTheme_AreConvertedToHexForMonaco', () => {
    const vars: Record<string, string> = {
      '--card': 'oklch(0.205 0 0)',
      '--card-foreground': 'oklch(0.985 0 0)',
      '--muted-foreground': 'oklch(0.708 0 0)',
      '--primary': 'oklch(0.922 0 0)',
      '--border': 'oklch(1 0 0 / 10%)',
      '--accent': 'oklch(0.269 0 0)',
    };

    const colors = monacoThemeColors((name) => vars[name] ?? '');

    expect(colors['editor.background']).toBe('#171717');
    expect(colors['editorWidget.border']).toBe('#2e2e2e');
    expect(Object.keys(colors)).toHaveLength(6);
    expect(Object.values(colors).every((c) => /^#[0-9a-f]{6}$/.test(c))).toBe(true);
  });

  it('variables_Missing_AreLeftToTheBaseTheme', () => {
    expect(monacoThemeColors(() => '')).toEqual({});
  });
});
