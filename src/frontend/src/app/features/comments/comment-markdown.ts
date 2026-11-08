import type { Definition, Nodes, Parents, RootContent, TableRow } from 'mdast';
import { fromMarkdown } from 'mdast-util-from-markdown';
import { gfmFromMarkdown } from 'mdast-util-gfm';
import { gfm } from 'micromark-extension-gfm';
import { ACTOR_SCHEME, findMentions, isUuid } from './comment-mentions';

/**
 * What a comment renders to: a small tree of known, inert elements. There is no way to express
 * raw HTML, scripts, event handlers or images in it, so comment text (from agents, too) can only
 * ever become text, formatting and checked links.
 */
export type RenderNode =
  | { readonly kind: 'text'; readonly text: string }
  /** The whole comment as unformatted text (input the parser must not see, see below). */
  | { readonly kind: 'plain'; readonly text: string }
  | { readonly kind: 'inlineCode'; readonly text: string }
  | { readonly kind: 'codeBlock'; readonly text: string }
  | { readonly kind: 'break' }
  | { readonly kind: 'rule' }
  | { readonly kind: 'mention'; readonly actorId: string; readonly label: string }
  | {
      readonly kind: 'link';
      readonly href: string;
      readonly external: boolean;
      readonly children: readonly RenderNode[];
    }
  | {
      readonly kind:
        | 'paragraph'
        | 'heading'
        | 'emphasis'
        | 'strong'
        | 'delete'
        | 'blockquote'
        | 'group'
        | 'span';
      readonly children: readonly RenderNode[];
    }
  | {
      readonly kind: 'list';
      readonly ordered: boolean;
      readonly start: number | null;
      readonly items: readonly RenderListItem[];
    }
  | {
      readonly kind: 'table';
      readonly align: readonly ('left' | 'right' | 'center' | null)[];
      readonly head: readonly (readonly RenderNode[])[];
      readonly rows: readonly (readonly (readonly RenderNode[])[])[];
    };

export interface RenderListItem {
  /** Task list item: `true`/`false`; a normal item: `null`. */
  readonly checked: boolean | null;
  readonly children: readonly RenderNode[];
}

/** Nesting deeper than this is shown as plain text (keeps rendering cheap for odd input). */
export const MAX_RENDER_DEPTH = 24;

/** Schemes a link may have; anything else (javascript:, data:, vbscript:, …) stays text. */
const SAFE_SCHEMES = new Set(['http', 'https', 'mailto']);

/**
 * The `href` a link may get, or `null` when it must stay text. Allowed: http, https, mailto
 * and relative URLs. The scheme is read the way browsers read it (leading control characters
 * and spaces dropped, tabs and line breaks removed), so `java\tscript:` is no way around it.
 */
export function safeHref(url: string): string | null {
  const probe = trimControls(url).replace(/[\t\n\r]/g, '');
  const scheme = /^([a-z][a-z0-9+.-]*):/i.exec(probe);
  if (scheme && !SAFE_SCHEMES.has(scheme[1].toLowerCase())) {
    return null;
  }
  return probe;
}

/** `value` without leading and trailing C0 control characters and spaces (as URL parsers do). */
function trimControls(value: string): string {
  let start = 0;
  let end = value.length;
  while (start < end && value.charCodeAt(start) <= 0x20) {
    start++;
  }
  while (end > start && value.charCodeAt(end - 1) <= 0x20) {
    end--;
  }
  return value.slice(start, end);
}

/**
 * Parses comment Markdown (CommonMark + GFM, like the editor) into a {@link RenderNode} tree.
 * Raw HTML stays literal text (HTML comments are dropped, as any Markdown view hides them),
 * images are not loaded but shown as `[Bild: alt] (url)` like in the editor. A link to
 * `actor:<uuid>` becomes a mention chip only when the server counts it as a mention
 * ({@link findMentions}: not in code, not in an HTML comment, no title, no brackets in the
 * text); otherwise its text is shown without a link.
 */
export function renderMarkdown(markdown: string): RenderNode[] {
  if (isPathological(markdown)) {
    return [{ kind: 'plain', text: markdown }];
  }
  try {
    const root = fromMarkdown(markdown, {
      extensions: [gfm()],
      mdastExtensions: [gfmFromMarkdown()],
    });
    const context: Context = {
      mentions: new Set(findMentions(markdown).actorIds),
      definitions: new Map(),
    };
    collectDefinitions(root, context.definitions);
    return convertAll(root.children, context, 0);
  } catch {
    // e.g. a stack overflow on nesting the checks below did not foresee: text is still readable
    return [{ kind: 'plain', text: markdown }];
  }
}

/**
 * Longest comment shown formatted; longer ones are plain text. Together with {@link MAX_LINES}
 * and {@link MAX_DELIMITERS} this keeps formatting one comment (parser and DOM) below about
 * 50 ms in the worst case measured (wide tables ~36 ms, lists, lazy continuation and setext
 * headings 5–20 ms, in jsdom). A page with many comments is bounded by `CommentRenderer`.
 */
export const MAX_FORMATTED_LENGTH = 4_000;
/** Most lines of a comment shown formatted (block structure costs per line). */
export const MAX_LINES = 300;
/** Longest run of one delimiter character the parser gets (longer: plain text). */
export const MAX_DELIMITER_RUN = 100;
/** Most container markers (`>`, list bullets) at the start of a line. */
export const MAX_LINE_NESTING = 16;
/** Most leading columns of a line (deeper indentation nests lists). */
export const MAX_INDENT = 64;
/**
 * Most emphasis, strike, bracket and code delimiters in one comment. Matching them is quadratic
 * in the parser (8000 unmatched `*` or `]` take about half a second); normal prose with
 * formatting has about one per 25 characters.
 */
export const MAX_DELIMITERS = 1_000;

/** A run of more than {@link MAX_DELIMITER_RUN} of one delimiter (the literal must match it). */
const DELIMITER_RUN = /([*_~>[\]()<`!\\])\1{100,}/;
const LINE_PREFIX = /^(?:[ \t]*(?:>|[*+-](?=[ \t])|\d{1,9}[.)](?=[ \t])))+/gm;

/**
 * Input that would make the parser slow or overflow its stack (it recurses on nesting and
 * resolves emphasis in quadratic time): long runs of one delimiter (`'*'.repeat(10000)`),
 * deep quote or list nesting on one line (`'>'.repeat(20000)`), deep indentation, very many
 * delimiters overall, or simply a very long comment. Such comments are shown as plain text
 * (still readable, without formatting); normal comments never come close.
 */
export function isPathological(markdown: string): boolean {
  if (markdown.length > MAX_FORMATTED_LENGTH || DELIMITER_RUN.test(markdown)) {
    return true;
  }
  let lines = 1;
  for (let i = markdown.indexOf('\n'); i >= 0; i = markdown.indexOf('\n', i + 1)) {
    if (++lines > MAX_LINES) {
      return true;
    }
  }
  for (const match of markdown.matchAll(LINE_PREFIX)) {
    const markers = match[0].replace(/\d{1,9}[.)]/g, '#').replace(/[ \t]/g, '').length;
    if (markers > MAX_LINE_NESTING) {
      return true;
    }
  }
  for (const line of markdown.split('\n')) {
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
    if (columns > MAX_INDENT) {
      return true;
    }
  }
  let delimiters = 0;
  for (let i = 0; i < markdown.length; i++) {
    const c = markdown.charCodeAt(i);
    // * _ ~ [ ] `
    if (c === 42 || c === 95 || c === 126 || c === 91 || c === 93 || c === 96) {
      if (++delimiters > MAX_DELIMITERS) {
        return true;
      }
    }
  }
  return false;
}

interface Context {
  readonly mentions: ReadonlySet<string>;
  readonly definitions: Map<string, Definition>;
}

/** Walks the tree without recursion (its depth is not bounded). */
function collectDefinitions(root: Nodes, into: Map<string, Definition>): void {
  const stack: Nodes[] = [root];
  while (stack.length > 0) {
    const node = stack.pop()!;
    if (node.type === 'definition') {
      if (!into.has(node.identifier)) {
        into.set(node.identifier, node);
      }
    } else if ('children' in node) {
      for (let i = node.children.length - 1; i >= 0; i--) {
        stack.push(node.children[i] as Nodes);
      }
    }
  }
}

function convertAll(nodes: readonly Nodes[], context: Context, depth: number): RenderNode[] {
  const result: RenderNode[] = [];
  for (const node of nodes) {
    const converted = convert(node, context, depth);
    if (converted) {
      result.push(converted);
    }
  }
  return result;
}

function children(node: Parents, context: Context, depth: number): RenderNode[] {
  return convertAll(node.children as readonly Nodes[], context, depth + 1);
}

function convert(node: Nodes, context: Context, depth: number): RenderNode | null {
  if (depth > MAX_RENDER_DEPTH) {
    const text = textOf(node);
    return text ? { kind: 'text', text } : null;
  }
  switch (node.type) {
    case 'text':
      return { kind: 'text', text: node.value };
    case 'inlineCode':
      return { kind: 'inlineCode', text: node.value };
    case 'code':
      return { kind: 'codeBlock', text: node.value };
    case 'html':
      return /^<!--[\s\S]*-->\s*$/.test(node.value) ? null : { kind: 'text', text: node.value };
    case 'break':
      return { kind: 'break' };
    case 'thematicBreak':
      return { kind: 'rule' };
    case 'paragraph':
    case 'emphasis':
    case 'strong':
    case 'delete':
    case 'blockquote':
      return { kind: node.type, children: children(node, context, depth) };
    case 'heading':
      return { kind: 'heading', children: children(node, context, depth) };
    case 'list':
      return {
        kind: 'list',
        ordered: !!node.ordered,
        start: node.ordered ? (node.start ?? 1) : null,
        items: node.children.map((item) => ({
          checked: item.checked ?? null,
          children: children(item, context, depth),
        })),
      };
    case 'listItem':
      return { kind: 'group', children: children(node, context, depth) };
    case 'table':
      return table(node.align ?? [], node.children, context, depth);
    case 'link':
      return link(node.url, node.title ?? null, children(node, context, depth), node, context);
    case 'linkReference': {
      const definition = context.definitions.get(node.identifier);
      const inner = children(node, context, depth);
      // Reference links are no mentions on the server: an actor: target stays text.
      return definition && !isActorTarget(definition.url)
        ? link(definition.url, definition.title ?? null, inner, node, context)
        : { kind: 'span', children: inner };
    }
    case 'image':
    case 'imageReference': {
      const url =
        node.type === 'image' ? node.url : (context.definitions.get(node.identifier)?.url ?? '');
      const alt = node.alt ?? '';
      return { kind: 'text', text: `[Bild${alt ? `: ${alt}` : ''}] (${url})` };
    }
    case 'footnoteReference':
      return { kind: 'text', text: `[^${node.label ?? node.identifier}]` };
    case 'footnoteDefinition':
      return {
        kind: 'group',
        children: [
          { kind: 'text', text: `[^${node.label ?? node.identifier}]: ` },
          ...children(node, context, depth),
        ],
      };
    case 'definition':
      return null;
    default: {
      const text = textOf(node);
      return text ? { kind: 'text', text } : null;
    }
  }
}

function link(
  url: string,
  title: string | null,
  inner: RenderNode[],
  node: Nodes,
  context: Context,
): RenderNode {
  if (isActorTarget(url)) {
    const target = actorTarget(url);
    const label = textOf(node);
    // The server's pattern: no title, no brackets in the link text.
    if (
      node.type === 'link' &&
      title === null &&
      isUuid(target) &&
      context.mentions.has(target.toLowerCase()) &&
      !/[[\]]/.test(label)
    ) {
      return { kind: 'mention', actorId: target.toLowerCase(), label };
    }
    return { kind: 'span', children: inner };
  }
  const href = safeHref(url);
  if (href === null) {
    return { kind: 'span', children: inner };
  }
  return { kind: 'link', href, external: /^(https?:)?[\\/]{2}/i.test(href), children: inner };
}

function isActorTarget(url: string): boolean {
  return url.trim().toLowerCase().startsWith(ACTOR_SCHEME);
}

function actorTarget(url: string): string {
  return url.trim().slice(ACTOR_SCHEME.length);
}

function table(
  align: readonly (string | null | undefined)[],
  rows: readonly TableRow[],
  context: Context,
  depth: number,
): RenderNode {
  const cells = (row: TableRow) => row.children.map((cell) => children(cell, context, depth + 1));
  const [head, ...body] = rows;
  return {
    kind: 'table',
    align: align.map((a) => (a === 'left' || a === 'right' || a === 'center' ? a : null)),
    head: head ? cells(head) : [],
    rows: body.map(cells),
  };
}

/** The plain text of a node (alt text for images). */
function textOf(root: Nodes | RootContent): string {
  // Without recursion: it also flattens what lies below MAX_RENDER_DEPTH.
  const parts: string[] = [];
  const stack: (Nodes | RootContent)[] = [root];
  while (stack.length > 0) {
    const node = stack.pop()!;
    if ('value' in node && typeof node.value === 'string') {
      parts.push(node.value);
    } else if (node.type === 'image' || node.type === 'imageReference') {
      parts.push(node.alt ?? '');
    } else if ('children' in node) {
      for (let i = node.children.length - 1; i >= 0; i--) {
        stack.push(node.children[i] as Nodes);
      }
    }
  }
  return parts.join('');
}
