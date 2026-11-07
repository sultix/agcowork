import { cssColorToHex } from './css-color';

/** Theme variable per Monaco color; translucent values are composited onto the editor background. */
const THEME_COLORS: readonly [monacoKey: string, cssVariable: string][] = [
  ['editor.background', '--card'],
  ['editor.foreground', '--card-foreground'],
  ['editorLineNumber.foreground', '--muted-foreground'],
  ['editorCursor.foreground', '--primary'],
  ['editorWidget.border', '--border'],
  ['editor.lineHighlightBackground', '--accent'],
];

/**
 * Monaco colors from the current shadcn theme variables. Monaco only accepts hex, the theme uses
 * oklch, so every value goes through `cssColorToHex`; unknown values are left to the base theme.
 */
export function monacoThemeColors(variable: (name: string) => string): Record<string, string> {
  const backdrop = variable('--card');
  const colors: Record<string, string> = {};
  for (const [key, name] of THEME_COLORS) {
    const hex = cssColorToHex(variable(name), backdrop);
    if (hex) {
      colors[key] = hex;
    }
  }
  return colors;
}
