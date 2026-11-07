/**
 * Turns CSS color values from the theme (`oklch(…)`, hex, `rgb(…)`) into `#rrggbb` for widgets
 * that only accept hex (Monaco's `defineTheme`). Pure functions, so the conversion is testable
 * without a browser; translucent colors are composited onto an opaque backdrop.
 */

/** sRGB color with channels and alpha in 0..1. */
export interface Rgba {
  readonly r: number;
  readonly g: number;
  readonly b: number;
  readonly a: number;
}

/** Hex value of `value` (composited onto `backdrop` when translucent), or `undefined` if unknown. */
export function cssColorToHex(value: string, backdrop?: string): string | undefined {
  const color = parseCssColor(value);
  if (!color) {
    return undefined;
  }
  const base = backdrop ? parseCssColor(backdrop) : undefined;
  return toHex(base && color.a < 1 ? composite(color, base) : color);
}

/** Parses hex (`#rgb`, `#rgba`, `#rrggbb`, `#rrggbbaa`), `rgb()`/`rgba()` and `oklch()`. */
export function parseCssColor(value: string): Rgba | undefined {
  const text = value.trim().toLowerCase();
  if (text.startsWith('#')) {
    return parseHex(text.slice(1));
  }
  const fn = /^(oklch|rgba?)\((.*)\)$/.exec(text);
  if (!fn) {
    return undefined;
  }
  const [channels, alpha] = splitArguments(fn[2]);
  if (channels.length !== 3) {
    return undefined;
  }
  const a = alpha === undefined ? 1 : parseNumber(alpha, 1);
  if (a === undefined) {
    return undefined;
  }
  return fn[1] === 'oklch' ? parseOklch(channels, a) : parseRgb(channels, a);
}

/** `#rrggbb`; alpha is dropped (composite first if it matters). */
export function toHex(color: Rgba): string {
  const channel = (v: number) =>
    Math.round(clamp(v) * 255)
      .toString(16)
      .padStart(2, '0');
  return `#${channel(color.r)}${channel(color.g)}${channel(color.b)}`;
}

function composite(color: Rgba, backdrop: Rgba): Rgba {
  const mix = (top: number, bottom: number) => top * color.a + bottom * (1 - color.a);
  return {
    r: mix(color.r, backdrop.r),
    g: mix(color.g, backdrop.g),
    b: mix(color.b, backdrop.b),
    a: 1,
  };
}

function parseHex(digits: string): Rgba | undefined {
  if (!/^[0-9a-f]+$/.test(digits) || ![3, 4, 6, 8].includes(digits.length)) {
    return undefined;
  }
  const full =
    digits.length <= 4
      ? digits
          .split('')
          .map((d) => d + d)
          .join('')
      : digits;
  const byte = (i: number) => parseInt(full.slice(i * 2, i * 2 + 2), 16) / 255;
  return { r: byte(0), g: byte(1), b: byte(2), a: full.length === 8 ? byte(3) : 1 };
}

/** Splits `a b c / d` or `a, b, c, d` into the three channels and the optional alpha. */
function splitArguments(args: string): [string[], string | undefined] {
  if (args.includes(',')) {
    const parts = args.split(',').map((p) => p.trim());
    return [parts.slice(0, 3), parts[3]];
  }
  const [channels, alpha] = args.split('/').map((p) => p.trim());
  return [channels.split(/\s+/).filter(Boolean), alpha];
}

/** Number or percentage (`percentOf` is the value of 100%); `none` counts as 0. */
function parseNumber(text: string, percentOf: number): number | undefined {
  if (text === 'none') {
    return 0;
  }
  const match = /^(-?(?:\d+\.?\d*|\.\d+)(?:e-?\d+)?)(%|deg)?$/.exec(text);
  if (!match) {
    return undefined;
  }
  const number = Number(match[1]);
  return match[2] === '%' ? (number / 100) * percentOf : number;
}

function parseRgb([r, g, b]: string[], a: number): Rgba | undefined {
  const values = [r, g, b].map((c) => parseNumber(c, 255));
  if (values.some((v) => v === undefined)) {
    return undefined;
  }
  const [red, green, blue] = values.map((v) => (v as number) / 255);
  return { r: red, g: green, b: blue, a };
}

/** OKLCH -> OKLab -> linear sRGB -> sRGB (Björn Ottosson's matrices), clipped to the sRGB gamut. */
function parseOklch([l, c, h]: string[], a: number): Rgba | undefined {
  const lightness = parseNumber(l, 1);
  const chroma = parseNumber(c, 0.4);
  const hue = parseNumber(h, 1);
  if (lightness === undefined || chroma === undefined || hue === undefined) {
    return undefined;
  }
  const radians = (hue * Math.PI) / 180;
  const labA = chroma * Math.cos(radians);
  const labB = chroma * Math.sin(radians);

  const lPrime = lightness + 0.3963377774 * labA + 0.2158037573 * labB;
  const mPrime = lightness - 0.1055613458 * labA - 0.0638541728 * labB;
  const sPrime = lightness - 0.0894841775 * labA - 1.291485548 * labB;
  const lCube = lPrime ** 3;
  const mCube = mPrime ** 3;
  const sCube = sPrime ** 3;

  return {
    r: gamma(4.0767416621 * lCube - 3.3077115913 * mCube + 0.2309699292 * sCube),
    g: gamma(-1.2684380046 * lCube + 2.6097574011 * mCube - 0.3413193965 * sCube),
    b: gamma(-0.0041960863 * lCube - 0.7034186147 * mCube + 1.707614701 * sCube),
    a,
  };
}

function gamma(linear: number): number {
  const v = clamp(linear);
  return v <= 0.0031308 ? 12.92 * v : 1.055 * v ** (1 / 2.4) - 0.055;
}

function clamp(v: number): number {
  return Math.min(1, Math.max(0, v));
}
