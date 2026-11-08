import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  effect,
  inject,
  input,
  untracked,
} from '@angular/core';
import { MembersStore } from '../../core/workspace/members.store';
import { RenderListItem, RenderNode } from './comment-markdown';
import { CommentRenderer } from './comment-renderer';

/**
 * Shows the Markdown of a comment, safely: the text is parsed into a {@link RenderNode} tree and
 * built with `createElement`/`textContent`, never through `innerHTML`. Raw HTML stays text,
 * images are not loaded, links keep only http, https, mailto and relative targets, and
 * mentions (`[@Name](actor:<id>)`, by the server's rules) become chips with the member's current
 * name (the link text while the member is unknown). Formatting runs through the sidebar's
 * {@link CommentRenderer} (time budget per page, cache by text).
 */
@Component({
  selector: 'ag-comment-markdown',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block text-sm break-words', 'data-testid': 'comment-markdown' },
  template: '',
})
export class CommentMarkdownComponent {
  private readonly members = inject(MembersStore);
  private readonly renderer = inject(CommentRenderer);
  private readonly host = inject(ElementRef<HTMLElement>).nativeElement as HTMLElement;

  readonly markdown = input.required<string>();

  constructor() {
    let built: readonly RenderNode[] | null = null;
    effect(() => {
      const markdown = this.markdown();
      // In the effect, not a computed: the renderer updates its budget signal.
      const tree = untracked(() => this.renderer.render(markdown));
      const names = new Map(this.members.members().map((m) => [m.actorId, m] as const));
      const document = this.host.ownerDocument;
      const builder = new Builder(document, (id) => names.get(id) ?? null);
      const build = () =>
        this.host.replaceChildren(...tree.map((node) => builder.node(node, true)));
      if (tree === built) {
        build(); // new member names only: cheap, not charged
      } else {
        untracked(() => this.renderer.measure(build));
        built = tree;
      }
    });
  }
}

const ALIGN_CLASSES = { left: 'text-left', right: 'text-right', center: 'text-center' } as const;

interface MemberInfo {
  readonly name: string;
  readonly kind: string;
}

class Builder {
  constructor(
    private readonly document: Document,
    private readonly member: (actorId: string) => MemberInfo | null,
  ) {}

  node(node: RenderNode, block = false): Node {
    switch (node.kind) {
      case 'text':
        return this.document.createTextNode(node.text);
      case 'plain':
        return this.element('p', 'whitespace-pre-wrap', [this.document.createTextNode(node.text)]);
      case 'inlineCode':
        return this.element('code', 'rounded bg-muted px-1 py-0.5 font-mono text-[0.85em]', [
          this.document.createTextNode(node.text),
        ]);
      case 'codeBlock': {
        const code = this.element('code', 'font-mono', [this.document.createTextNode(node.text)]);
        return this.element('pre', 'my-2 overflow-x-auto rounded-md bg-muted p-2 text-xs', [code]);
      }
      case 'break':
        return this.document.createElement('br');
      case 'rule':
        return this.element('hr', 'my-2 border-border', []);
      case 'mention':
        return this.mention(node.actorId, node.label);
      case 'link': {
        const a = this.element('a', 'underline underline-offset-4', this.all(node.children));
        a.setAttribute('href', node.href);
        if (node.external) {
          a.setAttribute('target', '_blank');
          a.setAttribute('rel', 'noopener noreferrer nofollow');
        }
        return a;
      }
      case 'paragraph':
        return this.element(
          'p',
          block ? 'my-1.5 first:mt-0 last:mb-0' : '',
          this.all(node.children),
        );
      case 'heading':
        return this.element(
          'p',
          'my-1.5 font-semibold first:mt-0 last:mb-0',
          this.all(node.children),
        );
      case 'emphasis':
        return this.element('em', '', this.all(node.children));
      case 'strong':
        return this.element('strong', 'font-semibold', this.all(node.children));
      case 'delete':
        return this.element('del', '', this.all(node.children));
      case 'blockquote':
        return this.element(
          'blockquote',
          'my-1.5 border-l-2 border-border pl-3 text-muted-foreground',
          this.all(node.children, true),
        );
      case 'group':
        return this.element('div', '', this.all(node.children, true));
      case 'span':
        return this.element('span', '', this.all(node.children));
      case 'list':
        return this.list(node.ordered, node.start, node.items);
      case 'table':
        return this.table(node.align, node.head, node.rows);
    }
  }

  private all(nodes: readonly RenderNode[], block = false): Node[] {
    return nodes.map((n) => this.node(n, block));
  }

  private element(tag: string, classes: string, content: readonly Node[]): HTMLElement {
    const element = this.document.createElement(tag);
    if (classes) {
      element.className = classes;
    }
    element.append(...content);
    return element;
  }

  private mention(actorId: string, label: string): Node {
    const member = this.member(actorId);
    const name = member?.name ?? label.replace(/^@/, '');
    const chip = this.element(
      'span',
      'inline-flex items-baseline rounded-sm bg-primary/10 px-1 font-medium text-foreground',
      [this.document.createTextNode(`@${name}`)],
    );
    chip.setAttribute('data-testid', 'mention-chip');
    chip.setAttribute('data-actor-id', actorId);
    const kind = member ? (member.kind === 'agent' ? 'Agent' : 'Mensch') : null;
    chip.setAttribute('title', kind ? `Erwähnt: ${name} (${kind})` : `Erwähnt: ${name}`);
    return chip;
  }

  private list(ordered: boolean, start: number | null, items: readonly RenderListItem[]): Node {
    const list = this.element(
      ordered ? 'ol' : 'ul',
      ordered ? 'my-1.5 list-decimal pl-5' : 'my-1.5 list-disc pl-5',
      [],
    );
    if (ordered && start !== null && start !== 1) {
      list.setAttribute('start', String(start));
    }
    for (const item of items) {
      const content = this.all(item.children, true);
      if (item.checked !== null) {
        const box = this.document.createElement('input');
        box.type = 'checkbox';
        box.checked = item.checked;
        box.disabled = true;
        box.className = 'mr-1.5 size-3.5 align-middle accent-primary';
        box.setAttribute('aria-label', item.checked ? 'erledigt' : 'offen');
        content.unshift(box);
      }
      list.append(this.element('li', item.checked !== null ? 'list-none' : '', content));
    }
    return list;
  }

  private table(
    align: readonly ('left' | 'right' | 'center' | null)[],
    head: readonly (readonly RenderNode[])[],
    rows: readonly (readonly (readonly RenderNode[])[])[],
  ): Node {
    const cell = (tag: 'th' | 'td', content: readonly RenderNode[], column: number) => {
      const alignment = align[column];
      return this.element(
        tag,
        `border border-border px-1.5 py-0.5 ${alignment ? ALIGN_CLASSES[alignment] : 'text-left'}`,
        this.all(content),
      );
    };
    const headRow = this.element(
      'tr',
      '',
      head.map((content, i) => cell('th', content, i)),
    );
    const bodyRows = rows.map((row) =>
      this.element(
        'tr',
        '',
        row.map((content, i) => cell('td', content, i)),
      ),
    );
    const table = this.element('table', 'my-1.5 border-collapse text-xs', [
      this.element('thead', '', [headRow]),
      this.element('tbody', '', bodyRows),
    ]);
    return this.element('div', 'overflow-x-auto', [table]);
  }
}
