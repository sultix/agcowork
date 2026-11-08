import { COMMENT_MARKDOWN_LIMITS } from '../../features/comments/comment-markdown';
import {
  EDITOR_MARKDOWN_LIMITS,
  MarkdownLimits,
  countMarkdown,
  exceedsMarkdownLimits,
  markdownWork,
} from './markdown-limits';

const exceeds = (markdown: string, limits: MarkdownLimits = EDITOR_MARKDOWN_LIMITS) =>
  exceedsMarkdownLimits(markdown, limits);

/** Prose with headings and lists, as people and agents write it, of about `bytes` characters. */
function prose(bytes: number): string {
  const paragraph =
    'Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ' +
    'ut labore et **dolore magna** aliqua. Ut enim ad minim veniam, quis nostrud exercitation ' +
    'ullamco laboris nisi ut aliquip ex ea commodo consequat. Siehe [Doku](https://example.com).\n\n';
  const parts: string[] = [];
  let size = 0;
  for (let n = 1; size < bytes; n++) {
    const section =
      `## Kapitel ${n}\n\n` +
      paragraph.repeat(3) +
      '- erster Punkt\n- zweiter Punkt mit *Betonung*\n  - verschachtelt\n- `code`\n\n';
    parts.push(section);
    size += section.length;
  }
  return parts.join('');
}

/** Dense Markdown: a table, code block, quote and lists every ~700 characters. */
function dense(bytes: number): string {
  const section =
    '## Abschnitt\n\nEin Absatz mit **fett**, *kursiv* und `code` sowie [Link](https://x.y).\n\n' +
    '- eins\n- zwei mit **Betonung**\n  - verschachtelt\n\n1. Eins\n2. Zwei\n\n> Ein Zitat.\n\n' +
    '```ts\nconst x = 1;\n```\n\n| Spalte | Wert |\n| --- | ---: |\n| a | 1 |\n| b | 2 |\n\n';
  return section.repeat(Math.ceil(bytes / section.length));
}

/** The largest `n` for which `make(n)` stays within the editor limits. */
function largestAccepted(make: (n: number) => string): number {
  let low = 0;
  let high = 1;
  while (!exceeds(make(high))) {
    low = high;
    high *= 2;
  }
  while (high - low > 1) {
    const mid = (low + high) >> 1;
    if (exceeds(make(mid))) {
      high = mid;
    } else {
      low = mid;
    }
  }
  return low;
}

describe('exceedsMarkdownLimits (editor)', () => {
  it('exceeds_PatternsOfTheCommentReview_AreCaughtQuickly', () => {
    for (const markdown of [
      '>'.repeat(10000), // stack overflow
      '*'.repeat(10000) + 'x' + '*'.repeat(10000), // seconds, then stack overflow
      '> a\n' + 'b\n'.repeat(3900), // lazy continuation, quadratic
      '- '.repeat(5000) + 'x', // stack overflow
      'a*'.repeat(8000), // quadratic emphasis matching
      'a](b '.repeat(8000), // quadratic bracket matching
    ]) {
      const started = performance.now();
      expect(exceeds(markdown), markdown.slice(0, 12)).toBe(true);
      expect(performance.now() - started, markdown.slice(0, 12)).toBeLessThan(50);
    }
  });

  it('exceeds_NormalBodyOf500KB_StaysInTheEditor', () => {
    const markdown = prose(500 * 1024);
    const started = performance.now();

    expect(exceeds(markdown)).toBe(false);
    // measured ~3 ms in Chrome; the bound leaves room for jsdom on slow CI machines
    expect(performance.now() - started).toBeLessThan(200);
  });

  it('exceeds_NormalBodyOf2MiB_IsTooLarge', () => {
    expect(exceeds(prose(2 * 1024 * 1024))).toBe(true);
  });

  it('exceeds_Length_JustBelowAndAbove', () => {
    const max = EDITOR_MARKDOWN_LIMITS.maxLength;
    const text = 'Ein Satz ohne Auszeichnung. '.repeat(Math.ceil(max / 28)).slice(0, max);

    expect(exceeds(text)).toBe(false);
    expect(exceeds(text + 'x')).toBe(true);
  });

  it('exceeds_LinesOfOneBlock_JustBelowAndAbove', () => {
    // lazy continuation: one block of 2 000 lines is the most; blank lines split blocks
    expect(exceeds('> a\n' + 'b\n'.repeat(1998))).toBe(false);
    expect(exceeds('> a\n' + 'b\n'.repeat(2000))).toBe(true);
    expect(exceeds(('> a\n' + 'b\n'.repeat(998) + '\n').repeat(4))).toBe(false);
    expect(exceeds(('> a\n' + 'b\n'.repeat(1998) + '\n').repeat(2))).toBe(true);
  });

  it('exceeds_LoneCarriageReturns_CountAsLineEndings', () => {
    expect(exceeds('> a\r' + 'b\r'.repeat(3900))).toBe(true);
    expect(exceeds('> a\r\n' + 'b\r\n'.repeat(3900))).toBe(true);
  });

  it('exceeds_InlineMarkersOfOneBlock_JustBelowAndAbove', () => {
    expect(exceeds('a*'.repeat(4000))).toBe(false);
    expect(exceeds('a*'.repeat(4001))).toBe(true);
    // runs count once: `**` is one delimiter for the parser
    expect(exceeds('**a** '.repeat(2000))).toBe(false);
    for (const unit of ['a] ', '<b> ', 'a@b.c ', 'https://x.y ', 'www.x.y ']) {
      expect(exceeds(unit.repeat(4000)), unit).toBe(false);
      expect(exceeds(unit.repeat(4001)), unit).toBe(true);
    }
    // the same markers spread over paragraphs are cheap
    expect(exceeds(('a* '.repeat(400) + '\n\n').repeat(20))).toBe(false);
  });

  it('exceeds_Blocks_JustBelowAndAbove', () => {
    expect(exceeds('a\n\n'.repeat(10000))).toBe(false);
    expect(exceeds('a\n\n'.repeat(10001))).toBe(true);
  });

  it('exceeds_Headings_JustBelowAndAbove', () => {
    expect(exceeds('## a\n\n'.repeat(2000))).toBe(false);
    expect(exceeds('## a\n\n'.repeat(2001))).toBe(true);
    expect(exceeds('a\n=\n\n'.repeat(2001))).toBe(true); // setext
  });

  it('exceeds_TotalsBelowEveryOtherLimit_AreBoundedByTheWorkBudget', () => {
    // measured in Chrome below all other limits: 7.9 s, 7.5 s, 7.4 s, 6.0 s, 5.0 s, 3.8 s, 2.0 s
    const row = (cells: number) => '|a'.repeat(cells) + '|\n';
    const table = (cols: number, rows: number) =>
      row(cols) + '|-'.repeat(cols) + '|\n' + row(cols).repeat(rows - 2);
    for (const markdown of [
      table(250, 1000),
      table(500, 500),
      (table(100, 60) + '\n').repeat(40),
      ('a*'.repeat(25) + '\n\n').repeat(10000),
      ('*a* '.repeat(12) + '\n\n').repeat(9999),
      ('[a](b) '.repeat(7) + '\n\n').repeat(9999),
      ('> a\n' + 'b\n'.repeat(62) + '\n').repeat(1000),
      dense(500 * 1024),
    ]) {
      const counts = countMarkdown(markdown);
      expect(markdown.length, markdown.slice(0, 12)).toBeLessThanOrEqual(512 * 1024);
      expect(counts.blockLineCost, markdown.slice(0, 12)).toBeLessThanOrEqual(4_000_000);
      expect(markdownWork(counts), markdown.slice(0, 12)).toBeGreaterThan(1_000_000);
      expect(exceeds(markdown), markdown.slice(0, 12)).toBe(true);
    }
  });

  it('exceeds_WorkBudget_JustBelowAndAbove', () => {
    const row = '|a'.repeat(200) + '|\n';
    const cases: ((n: number) => string)[] = [
      (n) => row + '|-'.repeat(200) + '|\n' + row.repeat(n), // cells (Chrome at the limit: 0.76 s)
      (n) => ('a*'.repeat(25) + '\n\n').repeat(n), // inline nodes (0.65 s)
      (n) => ('> a\n' + 'b\n'.repeat(62) + '\n').repeat(n), // lazy lines (0.86 s)
      (n) => '> x\n\n'.repeat(n), // separate quotes (0.67 s)
      (n) => ('- '.repeat(16) + 'x\n').repeat(n), // nested bullets (0.62 s)
      (n) => Array.from({ length: n }, (_, i) => `x[^${i}]\n\n[^${i}]: y\n\n`).join(''), // footnotes (0.61 s)
    ];
    for (const make of cases) {
      const n = largestAccepted(make);
      expect(n, make(1).slice(0, 12)).toBeGreaterThan(100);
      expect(markdownWork(countMarkdown(make(n))), make(1).slice(0, 12)).toBeLessThanOrEqual(
        1_000_000,
      );
      expect(markdownWork(countMarkdown(make(n + 1))), make(1).slice(0, 12)).toBeGreaterThan(
        1_000_000,
      );
    }
  });

  it('exceeds_FootnotesInTheirOwnParagraphs_AreBoundedByTheBudget', () => {
    // measured in Chrome: 4 999 references and definitions opened in 1.3 s before AP-034's fix
    const notes = (n: number, soft = '') =>
      Array.from({ length: n }, (_, i) => `x[^${i}]${soft}\n\n[^${i}]: y${soft}\n\n`).join('');
    expect(countMarkdown(notes(3)).footnotes).toBe(6);
    expect(exceeds(notes(4999))).toBe(true);
    expect(exceeds(notes(4999, '\na\nb\nc'))).toBe(true);
    // a long text with some dozens of footnotes stays in the editor
    expect(exceeds(prose(200 * 1024) + notes(60))).toBe(false);
  });

  it('countMarkdown_SmallText_CountsEachKind', () => {
    const counts = countMarkdown(
      '# Titel\n\n> - **a** [b](https://x.y) &amp;\n  c\n\n| x | y |\n|---|---|\n',
    );

    expect(counts).toEqual({
      length: 67,
      lines: 5,
      blocks: 3,
      containerBlocks: 1,
      markers: 2,
      headings: 1,
      inline: 5, // ** ** [ ] ://
      cells: 6,
      footnotes: 0,
      entities: 1,
      indent: 2,
      delimiters: 6,
      blockLineCost: 1 + 4 + 4,
      blockInlineCost: 25,
    });
  });

  it('exceeds_NestingIndentationAndRuns_JustBelowAndAbove', () => {
    expect(exceeds('> '.repeat(16) + 'x')).toBe(false);
    expect(exceeds('> '.repeat(17) + 'x')).toBe(true);
    expect(exceeds('1. '.repeat(16) + 'x')).toBe(false);
    expect(exceeds('1. '.repeat(17) + 'x')).toBe(true);
    expect(exceeds(' '.repeat(64) + 'x')).toBe(false);
    expect(exceeds(' '.repeat(65) + 'x')).toBe(true);
    expect(exceeds('\t'.repeat(17) + 'x')).toBe(true);
    expect(exceeds(' '.repeat(200) + '\n\nx')).toBe(false); // blank lines nest nothing
    expect(exceeds('`'.repeat(100))).toBe(false);
    expect(exceeds('`'.repeat(101))).toBe(true);
    expect(exceeds('-'.repeat(1000))).toBe(false); // a long rule is harmless
  });
});

describe('exceedsMarkdownLimits (comments)', () => {
  it('exceeds_CommentLimits_KeepTheirOwnThresholds', () => {
    expect(exceeds('x'.repeat(4000), COMMENT_MARKDOWN_LIMITS)).toBe(false);
    expect(exceeds('x'.repeat(4001), COMMENT_MARKDOWN_LIMITS)).toBe(true);
    expect(exceeds('x\n'.repeat(299) + 'x', COMMENT_MARKDOWN_LIMITS)).toBe(false);
    expect(exceeds('x\n'.repeat(300) + 'x', COMMENT_MARKDOWN_LIMITS)).toBe(true);
    // comments count every delimiter character, also inside runs
    expect(exceeds('**a '.repeat(250), COMMENT_MARKDOWN_LIMITS)).toBe(false);
    expect(exceeds('**a '.repeat(500) + '*', COMMENT_MARKDOWN_LIMITS)).toBe(true);
    // a lone \r ends a line for the parser, so it counts for the line limit, too
    expect(exceeds('x\r'.repeat(300) + 'x', COMMENT_MARKDOWN_LIMITS)).toBe(true);
  });
});
