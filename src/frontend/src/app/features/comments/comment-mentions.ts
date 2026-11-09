/**
 * Mentions in comments, by the same rules as the server (`CommentMarkdown.FindMentions` in
 * `Agcowork.Domain/Comments/CommentMarkdown.cs`, rules in its XML comment). The web app shows a
 * mention chip only for links this scanner finds, so a chip always stands for an inbox entry: no
 * mentions in code spans, code blocks (fenced or indented) and HTML comments; the link text may
 * span lines of one paragraph but holds no brackets; link titles and reference links are no
 * mentions. Keep this file in step with the server; the spec mirrors `CommentMarkdownTests`.
 */

export const ACTOR_SCHEME = 'actor:';

/** What {@link findMentions} found. */
export interface MentionScan {
  /** Mentioned actor ids (lower case), each once, in order of first appearance. */
  readonly actorIds: readonly string[];
  /** `actor:` targets that are no uuid (e.g. `actor:alice`), each once. */
  readonly invalidTargets: readonly string[];
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Whether `value` is a uuid in the `D` format (`8-4-4-4-12` hex digits, any case). */
export function isUuid(value: string): boolean {
  return UUID.test(value);
}

/**
 * The canonical mention link, e.g. `[@Tech-Lead](actor:0190…)`. Characters of the name that
 * would make server and renderer read the link differently are replaced: brackets (end the link
 * text), backslashes (escape the next character for the renderer, not for the server),
 * backticks (open a code span), `<` (opens an HTML comment) and line breaks.
 */
export function mentionLink(displayName: string, actorId: string): string {
  const name = displayName
    .replace(/\[/g, '(')
    .replace(/\]/g, ')')
    .replace(/\\/g, '/')
    .replace(/`/g, "'")
    .replace(/</g, '‹')
    .replace(/[\r\n]+/g, ' ')
    .trim();
  return `[@${name}](${ACTOR_SCHEME}${actorId})`;
}

/** The actors mentioned in `markdown` (outside code), and malformed `actor:` targets. */
export function findMentions(markdown: string | null | undefined): MentionScan {
  const ids: string[] = [];
  const invalid: string[] = [];
  if (!markdown) {
    return { actorIds: ids, invalidTargets: invalid };
  }
  for (const segment of inlineSegments(markdown)) {
    scanInline(segment, ids, invalid);
  }
  return { actorIds: ids, invalidTargets: invalid };
}

/** Block quotes nest at most this deep; further markers are text of the innermost quote. */
export const MAX_QUOTE_DEPTH = 32;

// The patterns below mirror the generated regexes of CommentMarkdown (same names). They are
// sticky and matched at the start of a line's content (after markers and indentation).
// JS `\s` also matches U+FEFF, .NET's does not: use the .NET set (char.IsWhiteSpace) explicitly.
const WS = String.raw`\t\n\v\f\r \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000`;
const WHITESPACE = new RegExp(`^[${WS}]$`, 'u');
const BACKTICKS = '`{3,}';
const OPENING_FENCE = new RegExp(`[${WS}]*(${BACKTICKS}|~{3,})([^\\n]*)$`, 'uy');
const CLOSING_FENCE = new RegExp(`[${WS}]*(${BACKTICKS}|~{3,})[ \\t]*$`, 'uy');
const LIST_ITEM = /([-+*]|\p{Nd}{1,9}[.)])([ \t]|$)/uy;
const HEADING = /#{1,6}([ \t]|$)/uy;
/** `[text](actor:target)` or `[text](<actor:target>)`, anchored (sticky) at `lastIndex`. */
const ACTOR_LINK = new RegExp(
  String.raw`\[([^[\]]*)\]\([${WS}]*<?actor:([^${WS}()<>]*)>?[${WS}]*\)`,
  'iuy',
);

function matchAt(pattern: RegExp, line: string, start: number): RegExpExecArray | null {
  pattern.lastIndex = start;
  return pattern.exec(line);
}

/**
 * Where the content of a line starts inside its innermost container, without copying the line:
 * `start` is the index after the container's markers and leading whitespace, `indent` the columns
 * of that whitespace, `column` the column of `start` in the document line (a tab counts to the
 * next multiple of four).
 */
interface LineView {
  readonly indent: number;
  readonly start: number;
  readonly column: number;
}

function skipWhitespace(line: string, index: number, column: number): [number, number] {
  while (index < line.length && (line[index] === ' ' || line[index] === '\t')) {
    column += line[index] === '\t' ? 4 - (column % 4) : 1;
    index++;
  }
  return [index, column];
}

function viewOf(line: string): LineView {
  const [start, column] = skipWhitespace(line, 0, 0);
  return { indent: column, start, column };
}

function isBlank(line: string, view: LineView): boolean {
  for (let i = view.start; i < line.length; i++) {
    if (!WHITESPACE.test(line[i])) {
      return false;
    }
  }
  return true;
}

/** A block quote marker: `>` after at most three columns of indentation. */
function hasQuoteMarker(line: string, view: LineView): boolean {
  return view.indent <= 3 && view.start < line.length && line[view.start] === '>';
}

/** The content after the marker; one optional space follows it, the rest is indentation. */
function afterQuoteMarker(line: string, view: LineView): LineView {
  const afterMarker = view.column + 1;
  const [start, column] = skipWhitespace(line, view.start + 1, afterMarker);
  const whitespace = column - afterMarker;
  return { indent: whitespace - (whitespace > 0 ? 1 : 0), start, column };
}

function openingFence(line: string, view: LineView, inList: boolean): RegExpExecArray | null {
  const fence = matchAt(OPENING_FENCE, line, view.start);
  return fence && (view.indent <= 3 || inList) && !(fence[1][0] === '`' && fence[2].includes('`'))
    ? fence
    : null;
}

const isHtmlCommentStart = (line: string, view: LineView) =>
  view.indent <= 3 && line.startsWith('<!--', view.start);
const isListItem = (line: string, view: LineView) =>
  view.indent <= 3 && matchAt(LIST_ITEM, line, view.start) !== null;
const isHeading = (line: string, view: LineView) =>
  view.indent <= 3 && matchAt(HEADING, line, view.start) !== null;

/** Lines that start a block of their own and never continue a quoted paragraph lazily. */
function startsBlock(line: string, view: LineView): boolean {
  return (
    openingFence(line, view, false) !== null ||
    isHtmlCommentStart(line, view) ||
    isListItem(line, view) ||
    isHeading(line, view)
  );
}

/** State of one container: the open block and whether a fence, HTML comment or list is open. */
class Container {
  block = '';
  fenceChar: string | null = null;
  fenceLength = 0;
  inHtmlComment = false;
  inList = false;

  /** Whether a line without quote marker may continue the paragraph of this container. */
  get hasOpenParagraph(): boolean {
    return this.fenceChar === null && !this.inHtmlComment && this.block.length > 0;
  }
}

/**
 * Splits the document into inline segments (the server's `BlockScanner`). Block quotes are
 * containers: a line with a quote marker opens a nested container that gets the content after the
 * marker. Iterative over a stack of containers, at most {@link MAX_QUOTE_DEPTH} quotes deep; a
 * marker beyond that is text. Lines are read by offset, never copied per level.
 */
class BlockScanner {
  private readonly segments: string[] = [];
  private readonly containers: Container[] = [new Container()];

  feed(line: string): void {
    let view = viewOf(line);

    // Down through the open quotes: each one continues while the line carries its marker.
    let level = 0;
    while (level + 1 < this.containers.length) {
      if (hasQuoteMarker(line, view)) {
        view = afterQuoteMarker(line, view);
        level++;
        continue;
      }
      if (
        !isBlank(line, view) &&
        this.containers[this.containers.length - 1].hasOpenParagraph &&
        !startsBlock(line, view)
      ) {
        // Lazy continuation: the paragraph of the innermost quote goes on.
        level = this.containers.length - 1;
        break;
      }
      this.closeAbove(level);
      break;
    }

    for (;;) {
      const container = this.containers[level];
      if (container.fenceChar !== null) {
        const close = matchAt(CLOSING_FENCE, line, view.start);
        if (
          close &&
          close[1][0] === container.fenceChar &&
          close[1].length >= container.fenceLength
        ) {
          container.fenceChar = null;
        }
        return;
      }

      if (container.inHtmlComment) {
        container.inHtmlComment = line.indexOf('-->', view.start) < 0;
        return;
      }

      const fence = openingFence(line, view, container.inList);
      if (fence) {
        this.flush(container);
        container.fenceChar = fence[1][0];
        container.fenceLength = fence[1].length;
        return;
      }

      // An HTML comment block (CommonMark type 2) may interrupt a paragraph and ends with the
      // line that contains "-->".
      if (isHtmlCommentStart(line, view)) {
        this.flush(container);
        container.inHtmlComment = line.indexOf('-->', view.start + 4) < 0;
        return;
      }

      if (isBlank(line, view)) {
        this.flush(container);
        return;
      }

      // A block quote may interrupt a paragraph; beyond the depth limit the marker is text.
      if (hasQuoteMarker(line, view) && this.containers.length <= MAX_QUOTE_DEPTH) {
        this.flush(container);
        container.inList = container.inList && view.indent >= 2;
        this.containers.push(new Container());
        view = afterQuoteMarker(line, view);
        level++;
        continue;
      }

      // Indented code needs a place where no paragraph is open; inside lists indented lines are
      // list content.
      if (view.indent >= 4 && container.block.length === 0 && !container.inList) {
        return;
      }

      const listItem = isListItem(line, view);
      const heading = isHeading(line, view);
      if (listItem || heading) {
        this.flush(container);
      }
      container.inList = listItem || (container.inList && view.indent >= 2);
      container.block += line.slice(view.start) + '\n';
      if (heading) {
        // A heading is a block of one line: an indented line right after it starts code.
        this.flush(container);
      }
      return;
    }
  }

  /** Ends the document: open quotes end, open blocks become segments. */
  finish(): string[] {
    this.closeAbove(-1);
    return this.segments;
  }

  /** Closes the containers deeper than `level`, innermost first. */
  private closeAbove(level: number): void {
    for (let i = this.containers.length - 1; i > level; i--) {
      this.flush(this.containers[i]);
      if (i > 0) {
        this.containers.splice(i, 1);
      }
    }
  }

  private flush(container: Container): void {
    if (container.block.length > 0) {
      this.segments.push(container.block);
      container.block = '';
    }
  }
}

/** The inline content outside code blocks and HTML comment blocks, one entry per block. */
function inlineSegments(markdown: string): string[] {
  const scanner = new BlockScanner();
  for (const raw of markdown.split('\n')) {
    scanner.feed(raw.replace(/\r+$/, ''));
  }
  return scanner.finish();
}

function scanInline(text: string, ids: string[], invalid: string[]): void {
  const ranges = protectedRanges(text);
  let next = 0;
  let i = 0;
  while (i < text.length) {
    if (next < ranges.length && i >= ranges[next].start) {
      i = Math.max(i, ranges[next].end);
      next++;
      continue;
    }

    if (isEscape(text, i)) {
      i += 2;
      continue;
    }

    if (text[i] === '[') {
      ACTOR_LINK.lastIndex = i;
      const link = ACTOR_LINK.exec(text);
      if (link) {
        const textEnd = i + 1 + link[1].length;
        const linkEnd = i + link[0].length;
        // A code span or comment that starts before the link ends and reaches past its text
        // cuts the link.
        let cut = false;
        for (let k = next; k < ranges.length && ranges[k].start < linkEnd; k++) {
          if (ranges[k].end > textEnd) {
            cut = true;
            break;
          }
        }
        if (!cut) {
          add(link[2], ids, invalid);
          i = linkEnd;
          continue;
        }
      }
    }

    i++;
  }
}

function add(target: string, ids: string[], invalid: string[]): void {
  if (isUuid(target)) {
    const id = target.toLowerCase();
    if (!ids.includes(id)) {
      ids.push(id);
    }
    return;
  }
  const shown = ACTOR_SCHEME + target;
  if (!invalid.includes(shown)) {
    invalid.push(shown);
  }
}

interface Range {
  readonly start: number;
  readonly end: number;
}

/** Code spans and inline HTML comments, left to right (they bind before links). */
function protectedRanges(text: string): Range[] {
  const ranges: Range[] = [];
  let commentsUnclosed = false;
  let i = 0;
  while (i < text.length) {
    if (isEscape(text, i)) {
      i += 2;
      continue;
    }

    if (text[i] === '`') {
      const run = runLength(text, i, '`');
      const close = findRun(text, i + run, run);
      if (close < 0) {
        i += run;
        continue;
      }
      ranges.push({ start: i, end: close + run });
      i = close + run;
      continue;
    }

    if (!commentsUnclosed && text.startsWith('<!--', i)) {
      const end = text.indexOf('-->', i + 4);
      if (end >= 0) {
        ranges.push({ start: i, end: end + 3 });
        i = end + 3;
        continue;
      }
      // No "-->" up to the end: no later "<!--" closes either (searching again is quadratic).
      commentsUnclosed = true;
    }

    i++;
  }
  return ranges;
}

/** A backslash before ASCII punctuation (CommonMark): the character is literal. */
function isEscape(text: string, i: number): boolean {
  return text[i] === '\\' && i + 1 < text.length && /[!-/:-@[-`{-~]/.test(text[i + 1]);
}

function runLength(text: string, start: number, c: string): number {
  let end = start;
  while (end < text.length && text[end] === c) {
    end++;
  }
  return end - start;
}

/** Start of the next run of exactly `length` backticks at or after `from`; -1 if none. */
function findRun(text: string, from: number, length: number): number {
  let i = from;
  while (i < text.length) {
    if (text[i] !== '`') {
      i++;
      continue;
    }
    const run = runLength(text, i, '`');
    if (run === length) {
      return i;
    }
    i += run;
  }
  return -1;
}
