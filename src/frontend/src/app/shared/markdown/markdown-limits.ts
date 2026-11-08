/**
 * Pre-check for Markdown before it reaches remark/micromark (comments, editor). The parser
 * recurses on nesting and is quadratic in several places, and Milkdown's serializer is quadratic
 * in the inline nodes of one paragraph: crafted input of a few kilobytes can overflow the stack
 * or block the tab for seconds, and many cheap nodes add up as well (cells, links, blocks).
 * {@link exceedsMarkdownLimits} counts, in one linear pass and without parsing, what makes the
 * editor slow, estimates its work and says whether a text stays within the given
 * {@link MarkdownLimits}. Callers show text beyond them as plain text.
 *
 * The check over-approximates on purpose: it does not know code blocks, HTML blocks or
 * containers, so `#` lines in code count as headings and so on. Recognising those would mean
 * deciding exactly like micromark (an opening fence the parser does not see would hide the rest
 * of the text from the check); counting too much only costs a plain-text view of very unusual
 * texts.
 */

/** Limits of one caller; leave a limit out (or `Infinity`) to not check it. */
export interface MarkdownLimits {
  /** Longest text in characters. */
  readonly maxLength: number;
  /** Most lines of the whole text. */
  readonly maxLines?: number;
  /** Longest run of one delimiter character (`*`, `>`, `[`, `` ` `` …): stack depth and time. */
  readonly maxDelimiterRun: number;
  /** Most container markers (`>`, list bullets) at the start of one line: stack depth. */
  readonly maxLineNesting: number;
  /** Most leading columns of a line (deeper indentation nests lists). */
  readonly maxIndent: number;
  /** Most emphasis, strike, bracket and code delimiters (`* _ ~ [ ] ``) in the whole text. */
  readonly maxDelimiters?: number;
  /**
   * Most of Σ lines² over the blocks (lines between blank lines). Lazy continuation lines of a
   * quote or list item are quadratic in the parser, and a paragraph's soft breaks in the
   * serializer.
   */
  readonly maxBlockLineCost?: number;
  /**
   * Most of Σ inline-markers² over the blocks. Inline markers are delimiter runs (`**` counts
   * once) plus what makes autolinks and inline HTML (`<`, `@`, `://`, `www.`): resolving
   * delimiters is quadratic in the parser, serializing many inline nodes of one paragraph in
   * Milkdown.
   */
  readonly maxBlockInlineCost?: number;
  /** Most blocks (serializing a document is quadratic in its top-level blocks). */
  readonly maxBlocks?: number;
  /** Most ATX headings and setext underlines (or rules) in the whole text. */
  readonly maxHeadings?: number;
  /**
   * Estimated work of the editor in µs ({@link markdownWork}) that a text may cost at most:
   * bounds the totals (cells, inline nodes, blocks …) that are cheap one by one but add up.
   */
  readonly maxWork?: number;
}

/** What one pass over a Markdown text counted (see {@link countMarkdown}). */
export interface MarkdownCounts {
  /** Characters. */
  readonly length: number;
  /** Lines that are not blank. */
  readonly lines: number;
  /** Blocks: runs of non-blank lines between blank lines. */
  readonly blocks: number;
  /** Blocks whose first line starts with a container marker (a new quote or list item). */
  readonly containerBlocks: number;
  /** Container markers (`>`, list bullets) at line starts, nested ones each. */
  readonly markers: number;
  /** ATX headings and setext underlines (or rules). */
  readonly headings: number;
  /** Inline markers: delimiter runs plus `<`, `@`, `://`, `www.`. */
  readonly inline: number;
  /** Pipes (`|`): table cells. */
  readonly cells: number;
  /** Footnote references and definitions (`[^`). */
  readonly footnotes: number;
  /** Entities and escapes (`&` and backslashes). */
  readonly entities: number;
  /** Σ leading columns of the lines (indentation nests lists). */
  readonly indent: number;
  /** Delimiter characters `* _ ~ [ ] `` (each, also inside runs). */
  readonly delimiters: number;
  /** Σ lines² over the blocks. */
  readonly blockLineCost: number;
  /** Σ inline markers² over the blocks. */
  readonly blockInlineCost: number;
}

/** Characters whose long runs hurt the parser (the literal must match the class below). */
const DELIMITER_CLASS = String.raw`[*_~>[\]()<${'`'}!\\]`;
const runPatterns = new Map<number, RegExp>();

function delimiterRun(max: number): RegExp {
  let pattern = runPatterns.get(max);
  if (!pattern) {
    pattern = new RegExp(String.raw`(${DELIMITER_CLASS})\1{${max},}`);
    runPatterns.set(max, pattern);
  }
  return pattern;
}

/** Container markers at the start of a line: `>`, `-`/`*`/`+` and `1.`/`1)` before a space. */
const LINE_PREFIX = /^(?:[ \t]*(?:>|[*+-](?=[ \t])|\d{1,9}[.)](?=[ \t])))+/;
/** After the container markers: an ATX heading or a setext underline (also matches rules). */
const HEADING = /^[ \t]*(?:#{1,6}(?:[ \t]|$)|(?:=+|-+)[ \t]*$)/;
/** Line endings as CommonMark knows them (a lone `\r` is one, too). */
const LINE_END = /\r\n?|\n/;

/** Whether `markdown` exceeds one of `limits` (and must not be given to the parser). */
export function exceedsMarkdownLimits(markdown: string, limits: MarkdownLimits): boolean {
  if (markdown.length > limits.maxLength || delimiterRun(limits.maxDelimiterRun).test(markdown)) {
    return true;
  }
  const counts = scan(markdown, limits);
  if (counts === null) {
    return true;
  }
  return (
    counts.blockLineCost > (limits.maxBlockLineCost ?? Infinity) ||
    counts.blockInlineCost > (limits.maxBlockInlineCost ?? Infinity) ||
    counts.blocks > (limits.maxBlocks ?? Infinity) ||
    counts.headings > (limits.maxHeadings ?? Infinity) ||
    markdownWork(counts) > (limits.maxWork ?? Infinity)
  );
}

/** Counts what makes `markdown` costly for the parser and the editor (no limits applied). */
export function countMarkdown(markdown: string): MarkdownCounts {
  return scan(markdown, NO_LIMITS)!;
}

/**
 * Estimated time (µs) Milkdown needs to open a read-only editor on a text with these counts:
 * create (parse, document, DOM) plus layout, in Chrome on a current laptop (AP-034). Each weight
 * is the cost of one unit measured where it dominates, rounded up; the quadratic terms are what
 * is left of lazy continuation and delimiter matching inside one block. It over-estimates
 * normal texts by about half and stayed above every measured text within the other limits
 * (runs, nesting and indentation are bounded separately: below them it under-estimates).
 */
export function markdownWork(counts: MarkdownCounts): number {
  return (
    WORK.char * counts.length +
    WORK.line * counts.lines +
    WORK.block * counts.blocks +
    WORK.containerBlock * counts.containerBlocks +
    WORK.marker * counts.markers +
    WORK.heading * counts.headings +
    // a heading costs more in a larger document (1 000 among 10 000 paragraphs: +0.3 s)
    WORK.headingPerNode * counts.headings * (counts.blocks + counts.markers) +
    WORK.inline * counts.inline +
    WORK.cell * counts.cells +
    WORK.footnote * counts.footnotes +
    WORK.entity * counts.entities +
    WORK.indentColumn * counts.indent +
    WORK.blockLine2 * counts.blockLineCost +
    WORK.blockInline2 * counts.blockInlineCost
  );
}

/** µs per unit, see {@link markdownWork}. */
const WORK = {
  char: 0.5,
  line: 15,
  block: 20,
  containerBlock: 80,
  marker: 60,
  heading: 20,
  headingPerNode: 0.03,
  inline: 14,
  cell: 25,
  footnote: 100,
  entity: 5,
  indentColumn: 1,
  blockLine2: 0.02,
  blockInline2: 0.01,
} as const;

const NO_LIMITS: MarkdownLimits = {
  maxLength: Infinity,
  maxDelimiterRun: Infinity,
  maxLineNesting: Infinity,
  maxIndent: Infinity,
};

/** One pass over the lines; `null` as soon as a per-line limit or the delimiter count is hit. */
function scan(markdown: string, limits: MarkdownLimits): MarkdownCounts | null {
  const maxLines = limits.maxLines ?? Infinity;
  const maxDelimiters = limits.maxDelimiters ?? Infinity;

  const lines = markdown.split(LINE_END);
  if (lines.length > maxLines) {
    return null;
  }
  let nonBlank = 0;
  let delimiters = 0;
  let blocks = 0;
  let containerBlocks = 0;
  let headings = 0;
  let markers = 0;
  let inline = 0;
  let cells = 0;
  let footnotes = 0;
  let entities = 0;
  let indent = 0;
  let lineCost = 0;
  let inlineCost = 0;
  let blockLines = 0;
  let blockInline = 0;
  const endBlock = () => {
    if (blockLines > 0) {
      blocks++;
      lineCost += blockLines * blockLines;
      inlineCost += blockInline * blockInline;
      blockLines = 0;
      blockInline = 0;
    }
  };

  for (const line of lines) {
    let columns = 0;
    let blank = true;
    for (let i = 0; i < line.length; i++) {
      const c = line.charCodeAt(i);
      if (c === 32) {
        columns++;
      } else if (c === 9) {
        columns += 4 - (columns % 4);
      } else {
        blank = false;
        break;
      }
    }
    if (blank) {
      endBlock();
      continue;
    }
    if (columns > limits.maxIndent) {
      return null;
    }
    nonBlank++;
    blockLines++;
    indent += columns;

    const prefix = LINE_PREFIX.exec(line)?.[0] ?? '';
    if (prefix) {
      const nesting = prefix.replace(/\d{1,9}[.)]/g, '#').replace(/[ \t]/g, '').length;
      if (nesting > limits.maxLineNesting) {
        return null;
      }
      markers += nesting;
      if (blockLines === 1) {
        containerBlocks++;
      }
    }
    if (HEADING.test(prefix ? line.slice(prefix.length) : line)) {
      headings++;
    }

    for (let i = 0; i < line.length; i++) {
      const c = line.charCodeAt(i);
      // * _ ~ [ ] `
      if (c === 42 || c === 95 || c === 126 || c === 91 || c === 93 || c === 96) {
        delimiters++;
        // a run of one character is one delimiter for the parser (`**` is one, not two)
        if (line.charCodeAt(i - 1) !== c) {
          blockInline++;
          inline++;
        }
        if (c === 91 && line.charCodeAt(i + 1) === 94) {
          footnotes++; // [^
        }
      } else if (c === 124) {
        cells++; // |
      } else if (c === 38 || c === 92) {
        entities++; // & or backslash
      } else if (
        c === 60 || // <
        c === 64 || // @
        (c === 58 && line.startsWith('//', i + 1)) || // ://
        ((c === 119 || c === 87) && isWww(line, i))
      ) {
        blockInline++;
        inline++;
      }
    }
    if (delimiters > maxDelimiters) {
      return null;
    }
  }
  endBlock();

  return {
    length: markdown.length,
    lines: nonBlank,
    blocks,
    containerBlocks,
    markers,
    headings,
    inline,
    cells,
    footnotes,
    entities,
    indent,
    delimiters,
    blockLineCost: lineCost,
    blockInlineCost: inlineCost,
  };
}

/** `www.` (any case) at `index` of `line`. */
function isWww(line: string, index: number): boolean {
  return (
    line.charCodeAt(index + 3) === 46 &&
    (line.charCodeAt(index + 1) | 32) === 119 &&
    (line.charCodeAt(index + 2) | 32) === 119
  );
}

/**
 * Limits for the Markdown editor (record body, Markdown properties), set by measuring Milkdown
 * in Chrome (AP-034). Goal: every text the editor gets opens read-only (create + layout) within
 * one second; editable it is serialized once more (another 10–70 %).
 *
 * - Work budget 1 s ({@link markdownWork}): the totals. Without it, texts below every other limit
 *   took 2–8 s: a table of 250 × 1 000 cells 7.9 s, 10 000 paragraphs of 25 `a*` 6 s,
 *   1 000 quotes of 63 lazy lines 2 s, 4 999 footnotes with their definitions 1.3 s. At the
 *   budget the slowest measured texts open in 0.6–0.9 s (cells, inline nodes, lazy lines,
 *   separate quotes, nested lists, footnotes, combinations).
 * - 512 KiB: 500 KB of prose with headings and lists (estimate ~0.6 s) opens in ~0.34 s.
 *   Dense Markdown (a table, code block, quote and three lists every 700 characters) reaches the
 *   budget at about 250 KB (200 KB: 0.45 s; 500 KB: 1.4 s, shown as text).
 * - Σ lines² 4 M (one block of 2 000 lines): lazy continuation is quadratic in one block
 *   (`> a` + 3 900 lines 0.35 s, 39 000 lines 26 s); a paragraph of 20 000 lines serializes in
 *   2 s.
 * - Σ inline markers² 16 M (one paragraph with 4 000): delimiter matching and serializing are
 *   quadratic in one paragraph (`a](b ` × 8 000: 0.85 s; 20 000 links: 2 s to serialize).
 * - 10 000 blocks: serializing is quadratic in the top-level blocks (40 000 paragraphs 2.1 s).
 * - 2 000 headings: a heading costs more the larger the document (10 000 headings: 2.8 s).
 * - Runs, nesting and indentation as for comments: they overflow the stack (`>` × 10 000,
 *   `- ` × 5 000) or take seconds before (`*` × 10 000 + `x` + `*` × 10 000: 2.3 s).
 */
export const EDITOR_MARKDOWN_LIMITS: MarkdownLimits = {
  maxLength: 512 * 1024,
  maxDelimiterRun: 100,
  maxLineNesting: 16,
  maxIndent: 64,
  maxBlockLineCost: 4_000_000,
  maxBlockInlineCost: 16_000_000,
  maxBlocks: 10_000,
  maxHeadings: 2_000,
  maxWork: 1_000_000,
};
