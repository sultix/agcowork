import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MembersStore } from '../../core/workspace/members.store';
import { AGENT_ID, member } from '../../core/testing/test-utils';
import {
  MAX_DELIMITERS,
  MAX_FORMATTED_LENGTH,
  MAX_LINES,
  MAX_RENDER_DEPTH,
  RenderNode,
  isPathological,
  renderMarkdown,
  safeHref,
} from './comment-markdown';
import { CommentMarkdownComponent } from './comment-markdown.component';
import { CommentRenderer } from './comment-renderer';

const A = '0190f3a2-7c4e-7b1a-9d2e-3f4a5b6c7d8e';

@Component({
  imports: [CommentMarkdownComponent],
  providers: [CommentRenderer],
  template: `<ag-comment-markdown [markdown]="markdown()" />`,
})
class HostComponent {
  readonly markdown = signal('');
}

describe('renderMarkdown', () => {
  it('safeHref_AllowsHttpMailtoAndRelative_RejectsScriptSchemes', () => {
    expect(safeHref('https://example.org/a?b')).toBe('https://example.org/a?b');
    expect(safeHref('mailto:a@b.c')).toBe('mailto:a@b.c');
    expect(safeHref('/w/demo/records/1')).toBe('/w/demo/records/1');
    expect(safeHref('#teil')).toBe('#teil');
    expect(safeHref('javascript:alert(1)')).toBeNull();
    expect(safeHref('JaVaScRiPt:alert(1)')).toBeNull();
    expect(safeHref(' \u0001javascript:alert(1)')).toBeNull();
    expect(safeHref('java\tscript:alert(1)')).toBeNull();
    expect(safeHref('java\nscript:alert(1)')).toBeNull();
    expect(safeHref('data:text/html,<script>alert(1)</script>')).toBeNull();
    expect(safeHref('vbscript:x')).toBeNull();
    expect(safeHref('actor:' + A)).toBeNull();
  });

  it('renderMarkdown_MentionOutsideCode_IsAMention_InCodeItIsNot', () => {
    expect(renderMarkdown(`Hallo [@Tech-Lead](actor:${A})`)).toEqual([
      {
        kind: 'paragraph',
        children: [
          { kind: 'text', text: 'Hallo ' },
          { kind: 'mention', actorId: A, label: '@Tech-Lead' },
        ],
      },
    ]);
    expect(renderMarkdown(`\`[@a](actor:${A})\``)).toEqual([
      { kind: 'paragraph', children: [{ kind: 'inlineCode', text: `[@a](actor:${A})` }] },
    ]);
  });

  it('renderMarkdown_ActorLinkTheServerDoesNotCount_StaysText', () => {
    // With a title, as a reference link or with an invalid id the server sees no mention.
    for (const markdown of [
      `[@a](actor:${A} "Titel")`,
      `[@a][x]\n\n[x]: actor:${A}`,
      '[@a](actor:alice)',
    ]) {
      const json = JSON.stringify(renderMarkdown(markdown));
      expect(json, markdown).not.toContain('"mention"');
      expect(json, markdown).not.toContain('"link"');
      expect(json, markdown).toContain('@a');
    }
  });

  it('renderMarkdown_DeepNesting_IsCutToText', () => {
    // nested lists by indentation: 30 levels, each list and item are two levels of the tree
    const markdown = Array.from({ length: 30 }, (_, i) => ' '.repeat(i * 2) + '- tief').join('\n');
    expect(isPathological(markdown)).toBe(false);
    const below = (node: RenderNode): RenderNode[] =>
      'children' in node
        ? [...node.children]
        : node.kind === 'list'
          ? node.items.flatMap((item) => item.children)
          : [];
    const depthOf = (node: RenderNode): number => 1 + Math.max(0, ...below(node).map(depthOf));
    const nodes = renderMarkdown(markdown);

    expect(depthOf(nodes[0])).toBeGreaterThan(MAX_RENDER_DEPTH / 2);
    expect(depthOf(nodes[0])).toBeLessThanOrEqual(MAX_RENDER_DEPTH + 2);
    expect(JSON.stringify(nodes).match(/tief/g)).toHaveLength(30);
  });

  it('renderMarkdown_PathologicalInput_PlainTextWithin100ms', () => {
    const fill = (unit: string) => unit.repeat(Math.floor((64 * 1024) / unit.length));
    const cases = [
      '>'.repeat(20000), // stack overflow in the parser
      '*'.repeat(10000) + 'x' + '*'.repeat(10000), // seconds in the parser
      '> '.repeat(20000),
      '- '.repeat(20000),
      fill('*a'), // quadratic emphasis matching
      fill('a]'),
      fill('~a'),
      fill('| a | b |\n|---|---|\n'), // long, slow but harmless
      Array.from({ length: 300 }, (_, i) => ' '.repeat(i * 2) + '- x').join('\n'),
    ];
    for (const markdown of cases) {
      const started = performance.now();
      const nodes = renderMarkdown(markdown);
      const elapsed = performance.now() - started;

      expect(nodes, markdown.slice(0, 20)).toEqual([{ kind: 'plain', text: markdown }]);
      expect(elapsed, markdown.slice(0, 20)).toBeLessThan(100);
    }
  });

  it('renderMarkdown_LazyContinuationAndSetext_PlainTextWithin100ms', () => {
    // 400+ ms each in the parser when formatted (measured in review)
    for (const markdown of [
      '> a\n' + 'b\n'.repeat(3900),
      '- a\n' + 'b\n'.repeat(3900),
      'a\n-\n'.repeat(2000),
      'a\n=\n'.repeat(2000),
      // the same below the length limit: caught by the line limit
      '> a\n' + 'b\n'.repeat(1990),
      '- a\n' + 'b\n'.repeat(1990),
      'a\n-\n'.repeat(995),
    ]) {
      const started = performance.now();
      const nodes = renderMarkdown(markdown);

      expect(performance.now() - started, markdown.slice(0, 8)).toBeLessThan(100);
      expect(nodes).toEqual([{ kind: 'plain', text: markdown }]);
    }
  });

  it('renderMarkdown_JustBelowTheLimits_StaysFormattedAndFast', () => {
    const lines = (head: string, unit: string) => head + unit.repeat(MAX_LINES - 2);
    const wide = '|' + 'c|'.repeat(10) + '\n';
    const cases = [
      lines('> a\n', 'b'.repeat(11) + '\n'), // lazy continuation
      lines('- a\n', 'b'.repeat(11) + '\n'),
      'a\n-\n'.repeat((MAX_LINES - 1) / 2), // setext headings
      '|' + 'a|'.repeat(10) + '\n|' + '-|'.repeat(10) + '\n' + wide.repeat(170), // wide table
      'a*'.repeat(MAX_DELIMITERS), // unmatched emphasis
    ];
    renderMarkdown('warm *up*');
    for (const markdown of cases) {
      expect(isPathological(markdown), markdown.slice(0, 8)).toBe(false);
      const started = performance.now();
      const nodes = renderMarkdown(markdown);
      const elapsed = performance.now() - started;

      expect(nodes[0].kind, markdown.slice(0, 8)).not.toBe('plain');
      // measured 5-17 ms (parser only, jsdom); the bound leaves room for slow CI machines
      expect(elapsed, markdown.slice(0, 8)).toBeLessThan(100);
    }
    expect(isPathological('x\n'.repeat(MAX_LINES))).toBe(true);
  });

  it('renderMarkdown_LongButNormalComment_StaysFormattedUpToTheLimit', () => {
    const sentence = 'Ein Satz mit **fett** und [Link](https://x.y). ';
    const markdown = sentence.repeat(Math.floor(MAX_FORMATTED_LENGTH / sentence.length));

    expect(isPathological(markdown)).toBe(false);
    expect(renderMarkdown(markdown)[0].kind).toBe('paragraph');
    expect(isPathological(markdown + 'x'.repeat(MAX_FORMATTED_LENGTH))).toBe(true);
  });

  it('renderMarkdown_BackslashHosts_CountAsExternal', () => {
    const links = (markdown: string) =>
      JSON.stringify(renderMarkdown(markdown)).match(/"external":(true|false)/g);

    // Browsers read \\host and /\\host like //host.
    expect(links('[a](\\\\\\\\evil.example) [b](/\\evil.example) [c](//evil.example)')).toEqual([
      '"external":true',
      '"external":true',
      '"external":true',
    ]);
    expect(links('[d](/w/demo)')).toEqual(['"external":false']);
  });
});

describe('CommentMarkdownComponent', () => {
  const members = signal([member({ actorId: AGENT_ID, name: 'Coder', kind: 'agent' })]);

  async function render(markdown: string): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      providers: [{ provide: MembersStore, useValue: { members } }],
    });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.markdown.set(markdown);
    await fixture.whenStable();
    return fixture.nativeElement.querySelector('ag-comment-markdown') as HTMLElement;
  }

  afterEach(() => {
    delete (window as unknown as { __xss?: number }).__xss;
  });

  it('render_HtmlWithEventHandlers_IsShownAsTextAndRunsNothing', async () => {
    const element = await render(
      [
        'Bild: <img src="x" onerror="window.__xss = 1">',
        '',
        '<script>window.__xss = 2</script>',
        '',
        '<div onclick="window.__xss = 3">klick</div>',
        '',
        '<iframe src="javascript:window.__xss = 4"></iframe>',
      ].join('\n'),
    );
    await new Promise((resolve) => setTimeout(resolve, 20));

    expect(element.querySelector('img, script, iframe, [onerror], [onclick]')).toBeNull();
    expect(element.textContent).toContain('<img src="x" onerror="window.__xss = 1">');
    expect(element.textContent).toContain('<script>window.__xss = 2</script>');
    expect((window as unknown as { __xss?: number }).__xss).toBeUndefined();
  });

  it('render_JavascriptLinks_HaveNoHref', async () => {
    const element = await render(
      [
        '[eins](javascript:window.__xss=1)',
        '[zwei](JaVaScRiPt:window.__xss=2)',
        '[drei](&#106;avascript:window.__xss=3)',
        '<javascript:window.__xss=4>',
        '[fünf](data:text/html,<script>x</script>)',
        '![bild](javascript:window.__xss=6)',
      ].join('\n\n'),
    );

    const anchors = Array.from(element.querySelectorAll('a'));
    expect(anchors).toEqual([]);
    expect(element.querySelector('img')).toBeNull();
    expect(element.textContent).toContain('eins');
    expect(element.textContent).toContain('drei');
    expect(element.textContent).toContain('[Bild: bild] (javascript:window.__xss=6)');
    for (const anchor of anchors) {
      anchor.click();
    }
    expect((window as unknown as { __xss?: number }).__xss).toBeUndefined();
  });

  it('render_HttpLink_IsANormalLinkInANewTab', async () => {
    const element = await render('Siehe [Doku](https://example.org/doku) und [intern](/w/demo).');

    const [external, internal] = Array.from(element.querySelectorAll('a'));
    expect(external.getAttribute('href')).toBe('https://example.org/doku');
    expect(external.getAttribute('target')).toBe('_blank');
    expect(external.getAttribute('rel')).toContain('noopener');
    expect(internal.getAttribute('href')).toBe('/w/demo');
    expect(internal.hasAttribute('target')).toBe(false);
  });

  it('render_Mention_ShowsChipWithMemberName_UnknownMemberWithLinkText', async () => {
    const element = await render(
      `Frage an [@alter Name](actor:${AGENT_ID}) und [@Gast](actor:${A}), nicht \`[@x](actor:${A})\``,
    );

    const chips = Array.from(element.querySelectorAll<HTMLElement>('[data-testid="mention-chip"]'));
    expect(chips.map((c) => c.textContent)).toEqual(['@Coder', '@Gast']);
    expect(chips[0].dataset['actorId']).toBe(AGENT_ID);
    expect(chips[0].getAttribute('title')).toBe('Erwähnt: Coder (Agent)');
    expect(element.querySelector('a')).toBeNull();
    expect(element.querySelector('code')!.textContent).toBe(`[@x](actor:${A})`);
  });

  it('render_Pathological_ShowsThePlainText', async () => {
    const element = await render('>'.repeat(20000));

    const paragraph = element.querySelector('p')!;
    expect(paragraph.className).toContain('whitespace-pre-wrap');
    expect(paragraph.textContent).toBe('>'.repeat(20000));
    expect(element.querySelector('blockquote')).toBeNull();
  });

  it('render_Formatting_KeepsSpacesBetweenInlineElements', async () => {
    const element = await render(
      '**fett**, *kursiv* und ~~weg~~.\n\n- [x] fertig\n- offen\n\n```\ncode\n```',
    );

    expect(element.querySelector('p')!.textContent).toBe('fett, kursiv und weg.');
    expect(element.querySelector('strong')!.textContent).toBe('fett');
    expect(element.querySelector('del')!.textContent).toBe('weg');
    const box = element.querySelector<HTMLInputElement>('li input[type="checkbox"]')!;
    expect(box.checked).toBe(true);
    expect(box.disabled).toBe(true);
    expect(element.querySelector('pre code')!.textContent).toBe('code');
  });
});
