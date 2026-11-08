import { PLAYGROUND_MARKDOWN } from '../../features/dev-playground/playground-samples';
import { MarkdownEditorHandle } from './markdown-editor.adapter';
import { createMilkdownEditor } from './milkdown-adapter';

/**
 * Round-trip tests against the real Milkdown engine (ProseMirror + remark run in jsdom):
 * Markdown → editor document → Markdown.
 *
 * Normalization is deliberately narrow: trailing whitespace per line, runs of blank lines and
 * leading/trailing blank lines. Indentation is preserved, so a flattened list or a de-indented
 * code line fails. Inside table rows, the cell padding and the dash count of the delimiter row
 * are normalized, because the serializer pads both to the column width (`| --- |` → `| ---- |`,
 * semantically identical GFM).
 */
function normalize(markdown: string): string {
  return markdown
    .split('\n')
    .map((line) => line.replace(/[ \t]+$/, ''))
    .map((line) => (line.startsWith('|') ? normalizeTableRow(line) : line))
    .join('\n')
    .replace(/\n{3,}/g, '\n\n')
    .replace(/^\n+|\n+$/g, '');
}

function normalizeTableRow(line: string): string {
  const cells = line
    .slice(1, -1)
    .split('|')
    .map((cell) => cell.trim());
  const delimiter = cells.every((cell) => /^:?-+:?$/.test(cell));
  return `| ${cells.map((cell) => (delimiter ? cell.replace(/-+/, '---') : cell)).join(' | ')} |`;
}

const editors: MarkdownEditorHandle[] = [];

async function mount(
  markdown: string,
  readonly = false,
): Promise<{
  editor: MarkdownEditorHandle;
  root: HTMLElement;
  changes: () => number;
}> {
  const root = document.createElement('div');
  document.body.appendChild(root);
  let changes = 0;
  const editor = await createMilkdownEditor({
    root,
    initialValue: markdown,
    readonly,
    placeholder: '',
    onDocChange: () => changes++,
    onBlur: () => undefined,
  });
  editors.push(editor);
  return { editor, root, changes: () => changes };
}

async function roundTrip(markdown: string): Promise<string> {
  const { editor } = await mount(markdown);
  return editor.getMarkdown();
}

describe('createMilkdownEditor', () => {
  afterEach(() => {
    editors.splice(0).forEach((editor) => editor.destroy());
    document.body.innerHTML = '';
  });

  const cases: [string, string][] = [
    ['headings 1-3', '# Eins\n\n## Zwei\n\n### Drei'],
    ['paragraphs', 'Erster Absatz.\n\nZweiter Absatz\nmit weicher Zeile.'],
    ['bold, italic, inline code', 'Text mit **fett**, *kursiv* und `code`.'],
    ['strikethrough (GFM)', 'Das ist ~~falsch~~ richtig.'],
    ['unordered list', '- a\n- b\n- c'],
    ['ordered list', '1. eins\n2. zwei\n3. drei'],
    ['task list', '- [ ] offen\n- [x] erledigt'],
    ['code block with language', '```ts\nconst a: number = 1;\n\nconsole.log(a);\n```'],
    ['code block without language', '```\nplain\n```'],
    [
      'code block with indented lines',
      '```py\ndef f(x):\n    if x:\n        return 1\n\treturn 0\n```',
    ],
    ['blockquote', '> Zitat\n>\n> zweiter Absatz'],
    ['links', '[Link](https://example.com) und [mit Titel](https://example.com "Titel")'],
    ['autolink', 'Siehe <https://example.com>.'],
    ['table (GFM)', '| Name | Wert |\n| --- | --- |\n| a | 1 |\n| b | 2 |'],
    ['table with alignment', '| L | C | R |\n| :-- | :-: | --: |\n| 1 | 2 | 3 |'],
    ['horizontal rule', 'oben\n\n---\n\nunten'],
    ['hard break', 'Zeile eins\\\nZeile zwei'],
    ['raw HTML stays literal', 'Text <b>fett</b>\n\n<div>block</div>'],
  ];

  it.each(cases)('roundTrip_%s_ReturnsSameMarkdown', async (_name, markdown) => {
    expect(normalize(await roundTrip(markdown))).toBe(normalize(markdown));
  });

  it('image_Markdown_RendersPlaceholderWithoutImgAndKeepsMarkdown', async () => {
    const markdown = 'Vorher ![Logo](https://example.org/logo.png) nachher\n';

    const { editor, root } = await mount(markdown);

    expect(root.querySelector('img')).toBeNull();
    const placeholder = root.querySelector('[data-testid="image-placeholder"]');
    expect(placeholder?.textContent).toBe('[Bild: Logo] (https://example.org/logo.png)');
    expect(normalize(editor.getMarkdown())).toBe(normalize(markdown));
  });

  it('roundTrip_NestedList_KeepsIndentationExactly', async () => {
    const markdown = '- a\n  - a1\n    - a1x\n- b\n  1. b1\n  2. b2\n';

    expect(await roundTrip(markdown)).toBe(markdown);
  });

  it('normalize_FlattenedList_DoesNotMatchNestedList', () => {
    expect(normalize('- a\n- a1')).not.toBe(normalize('- a\n  - a1'));
  });

  it.each([
    ['javascript', '[x](javascript:alert(1))'],
    ['mixed-case javascript', '[x](JaVaScRiPt:alert(1))'],
    ['data', '[x](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)'],
  ])('render_%sLink_HasEmptyHrefAndKeepsMarkdown', async (_name, markdown) => {
    const { editor, root } = await mount(markdown);

    const anchor = root.querySelector('.ProseMirror a');
    expect(anchor).not.toBeNull();
    expect(anchor?.getAttribute('href')).toBe('');
    // The URL itself survives; the serializer only escapes parentheses inside it (`\(`), which
    // is the same destination in CommonMark.
    expect(
      editor
        .getMarkdown()
        .trim()
        .replace(/\\([()])/g, '$1'),
    ).toBe(markdown);
  });

  it.each([
    ['javascript', '[x](javascript:alert(1))'],
    ['entity-encoded javascript', '[x](&#106;avascript:alert(1))'],
    ['vbscript', '[x](vbscript:msgbox(1))'],
  ])('render_ReadonlyBodyWith%sLink_HasNoUsableHref', async (_name, markdown) => {
    // The document view of a read-only record (viewer, archived) shows agent content as is; the
    // engine must defuse script links there too (Milkdown 7.23 does; this guards a downgrade).
    const { root } = await mount(markdown, true);

    const anchor = root.querySelector('.ProseMirror a');
    expect(anchor).not.toBeNull();
    expect(anchor?.getAttribute('href') ?? '').toBe('');
  });

  it('render_HttpsLink_KeepsHref', async () => {
    const { root } = await mount('[x](https://example.com/a?b=1)');

    expect(root.querySelector('.ProseMirror a')?.getAttribute('href')).toBe(
      'https://example.com/a?b=1',
    );
  });

  it('roundTrip_PlaygroundSampleWithAllElements_ReturnsSameMarkdown', async () => {
    expect(normalize(await roundTrip(PLAYGROUND_MARKDOWN))).toBe(normalize(PLAYGROUND_MARKDOWN));
  });

  it('roundTrip_SerializedOutput_IsStable', async () => {
    const once = await roundTrip(PLAYGROUND_MARKDOWN);
    expect(await roundTrip(once)).toBe(once);
  });

  it('render_RawHtml_IsNotRenderedAsHtml', async () => {
    const { root } = await mount(
      'a <b>bold</b> <img src=x onerror="alert(1)">\n\n<script>alert(1)</script>\n\n<div>x</div>',
    );

    expect(root.querySelector('b')).toBeNull();
    expect(root.querySelector('script')).toBeNull();
    expect(root.querySelector('img[src="x"]')).toBeNull();
    expect(root.querySelector('.ProseMirror div')).toBeNull();
    expect(root.textContent).toContain('<b>');
    expect(root.textContent).toContain('<script>');
  });

  it('setMarkdown_NewContent_ReplacesDocumentWithoutChangeCallback', async () => {
    const { editor, changes } = await mount('# Alt');

    expect(editor.setMarkdown('# Neu\n\n- x')).toBe(true);

    expect(normalize(editor.getMarkdown())).toBe('# Neu\n\n- x');
    expect(changes()).toBe(0);
  });

  it('setMarkdown_EquivalentMarkdown_KeepsDocument', async () => {
    const { editor } = await mount('- a\n- b');

    // `*` bullets parse to the same document as `-` bullets.
    expect(editor.setMarkdown('* a\n* b')).toBe(false);
  });

  it('run_Command_ChangesDocumentAndNotifies', async () => {
    const { editor, changes } = await mount('Titel');

    editor.run('h1');

    expect(editor.getMarkdown().trim()).toBe('# Titel');
    expect(changes()).toBeGreaterThan(0);
  });

  it('setReadonly_True_MakesContentNotEditableAndIgnoresCommands', async () => {
    const { editor, root, changes } = await mount('Titel');

    editor.setReadonly(true);
    editor.run('h1');

    expect(root.querySelector('.ProseMirror')?.getAttribute('contenteditable')).toBe('false');
    expect(editor.getMarkdown().trim()).toBe('Titel');
    expect(changes()).toBe(0);
  });

  it('setPlaceholder_EmptyDocument_ShowsPlaceholder', async () => {
    const { editor, root } = await mount('');

    editor.setPlaceholder('Schreib etwas');

    const node = root.querySelector('.ag-md-placeholder');
    expect(node?.getAttribute('data-placeholder')).toBe('Schreib etwas');
  });
});
