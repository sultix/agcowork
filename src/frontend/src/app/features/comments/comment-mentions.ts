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

// The patterns below mirror the generated regexes of CommentMarkdown (same names).
const OPENING_FENCE = /^\s*(`{3,}|~{3,})(.*)$/u;
const CLOSING_FENCE = /^\s*(`{3,}|~{3,})[ \t]*$/u;
const HTML_COMMENT_START = /^ {0,3}<!--/u;
const LIST_ITEM = /^ {0,3}([-+*]|\p{Nd}{1,9}[.)])([ \t]|$)/u;
const HEADING = /^ {0,3}#{1,6}([ \t]|$)/u;
/** `[text](actor:target)` or `[text](<actor:target>)`, anchored (sticky) at `lastIndex`. */
const ACTOR_LINK = /\[([^[\]]*)\]\(\s*<?actor:([^\s()<>]*)>?\s*\)/iuy;

/** The inline content outside code blocks and HTML comment blocks, one entry per block. */
function inlineSegments(markdown: string): string[] {
  const segments: string[] = [];
  let block = '';
  const flush = () => {
    if (block.length > 0) {
      segments.push(block);
      block = '';
    }
  };

  let fenceChar: string | null = null;
  let fenceLength = 0;
  let inHtmlComment = false;
  let inList = false;
  for (const raw of markdown.split('\n')) {
    const line = raw.replace(/\r+$/, '');
    const indent = indentation(line);
    if (fenceChar !== null) {
      const close = CLOSING_FENCE.exec(line);
      if (close && close[1][0] === fenceChar && close[1].length >= fenceLength) {
        fenceChar = null;
      }
      continue;
    }

    if (inHtmlComment) {
      inHtmlComment = !line.includes('-->');
      continue;
    }

    const fence = OPENING_FENCE.exec(line);
    if (fence && (indent <= 3 || inList) && !(fence[1][0] === '`' && fence[2].includes('`'))) {
      flush();
      fenceChar = fence[1][0];
      fenceLength = fence[1].length;
      continue;
    }

    // An HTML comment block (CommonMark type 2) may interrupt a paragraph and ends with the
    // line that contains "-->".
    if (HTML_COMMENT_START.test(line)) {
      flush();
      inHtmlComment = !line.slice(line.indexOf('<!--') + 4).includes('-->');
      continue;
    }

    if (line.trim() === '') {
      flush();
      continue;
    }

    // Indented code needs a place where no paragraph is open; inside lists indented lines are
    // list content.
    if (indent >= 4 && block.length === 0 && !inList) {
      continue;
    }

    const listItem = LIST_ITEM.test(line);
    const heading = HEADING.test(line);
    if (listItem || heading) {
      flush();
    }

    inList = listItem || (inList && indent >= 2);
    block += line + '\n';
    if (heading) {
      // A heading is a block of one line: an indented line right after it starts code.
      flush();
    }
  }

  flush();
  return segments;
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

    if (text.startsWith('<!--', i)) {
      const end = text.indexOf('-->', i + 4);
      if (end >= 0) {
        ranges.push({ start: i, end: end + 3 });
        i = end + 3;
        continue;
      }
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

/** Columns of leading whitespace; a tab counts to the next multiple of four. */
function indentation(line: string): number {
  let columns = 0;
  for (const c of line) {
    if (c === ' ') {
      columns++;
    } else if (c === '\t') {
      columns += 4 - (columns % 4);
    } else {
      break;
    }
  }
  return columns;
}
