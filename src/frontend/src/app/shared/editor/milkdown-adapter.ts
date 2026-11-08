import {
  Editor,
  defaultValueCtx,
  editorViewCtx,
  editorViewOptionsCtx,
  parserCtx,
  remarkStringifyOptionsCtx,
  rootCtx,
  serializerCtx,
} from '@milkdown/kit/core';
import type { Ctx } from '@milkdown/kit/ctx';
import { clipboard } from '@milkdown/kit/plugin/clipboard';
import { history } from '@milkdown/kit/plugin/history';
import {
  commonmark,
  createCodeBlockCommand,
  imageSchema,
  insertHrCommand,
  toggleEmphasisCommand,
  toggleInlineCodeCommand,
  toggleStrongCommand,
  turnIntoTextCommand,
  wrapInBlockquoteCommand,
  wrapInBulletListCommand,
  wrapInHeadingCommand,
  wrapInOrderedListCommand,
} from '@milkdown/kit/preset/commonmark';
import { gfm, toggleStrikethroughCommand } from '@milkdown/kit/preset/gfm';
import { Plugin, PluginKey } from '@milkdown/kit/prose/state';
import { Decoration, DecorationSet } from '@milkdown/kit/prose/view';
import { $prose, $view, callCommand } from '@milkdown/kit/utils';
import type {
  MarkdownCommand,
  MarkdownEditorHandle,
  MarkdownEditorOptions,
} from './markdown-editor.adapter';

/**
 * Serializer settings. Chosen to match how agents usually write Markdown so that a document
 * written by an agent comes back unchanged after a human edit (E2: Markdown is canonical).
 */
export const STRINGIFY_OPTIONS = {
  bullet: '-',
  bulletOrdered: '.',
  emphasis: '*',
  strong: '*',
  fence: '`',
  rule: '-',
  listItemIndent: 'one',
} as const;

/**
 * Images are never loaded (no proxy yet, and agent content must not trigger requests to foreign
 * hosts): the node view shows `alt` and the URL as text. The node itself, and so the Markdown,
 * stays unchanged.
 */
const imagePlaceholder = $view(imageSchema.node, () => (node) => {
  const dom = document.createElement('span');
  dom.className = 'ag-md-image';
  dom.setAttribute('data-testid', 'image-placeholder');
  const alt = String(node.attrs['alt'] ?? '');
  const src = String(node.attrs['src'] ?? '');
  dom.textContent = `[Bild${alt ? `: ${alt}` : ''}] (${src})`;
  return { dom };
});

const PLACEHOLDER_REFRESH = 'ag-placeholder-refresh';
const placeholderKey = new PluginKey('ag-placeholder');

/**
 * Milkdown (ProseMirror + remark) implementation of {@link MarkdownEditorHandle}.
 * Raw HTML in the source is kept as an inert `html` node (rendered as text, never as HTML) by the
 * commonmark preset, so agent content cannot inject markup.
 */
export async function createMilkdownEditor(
  options: MarkdownEditorOptions,
): Promise<MarkdownEditorHandle> {
  let readonly = options.readonly;
  let placeholder = options.placeholder;
  let applyingExternal = false;

  const bridge = $prose(
    () =>
      new Plugin({
        key: placeholderKey,
        props: {
          decorations(state) {
            const doc = state.doc;
            const first = doc.firstChild;
            const empty =
              placeholder !== '' &&
              doc.childCount === 1 &&
              first !== null &&
              first.isTextblock &&
              first.content.size === 0;
            if (!empty) {
              return DecorationSet.empty;
            }
            return DecorationSet.create(doc, [
              Decoration.node(0, first.nodeSize, {
                class: 'ag-md-placeholder',
                'data-placeholder': placeholder,
              }),
            ]);
          },
          handleDOMEvents: {
            blur: () => {
              options.onBlur();
              return false;
            },
            // A link whose unsafe target (`javascript:` …) Milkdown neutralised has `href=""`:
            // following it would reload the page.
            click: (_view, event) => {
              const target = event.target;
              const anchor = target instanceof Element ? target.closest('a') : null;
              if (anchor && !anchor.getAttribute('href')) {
                event.preventDefault();
                return true;
              }
              return false;
            },
          },
        },
        view: () => ({
          update: (view, prevState) => {
            if (!applyingExternal && !view.state.doc.eq(prevState.doc)) {
              options.onDocChange();
            }
          },
        }),
      }),
  );

  const editor = await Editor.make()
    .config((ctx) => {
      ctx.set(rootCtx, options.root);
      ctx.set(defaultValueCtx, options.initialValue);
      ctx.update(remarkStringifyOptionsCtx, (prev) => ({ ...prev, ...STRINGIFY_OPTIONS }));
      ctx.update(editorViewOptionsCtx, (prev) => ({ ...prev, editable: () => !readonly }));
    })
    .use(commonmark)
    .use(gfm)
    .use(history)
    .use(clipboard)
    .use(imagePlaceholder)
    .use(bridge)
    .create();

  const withCtx = <T>(fn: (ctx: Ctx) => T): T => editor.action(fn);
  const serialize = (): string =>
    withCtx((ctx) => ctx.get(serializerCtx)(ctx.get(editorViewCtx).state.doc));

  const commands: Record<MarkdownCommand, (ctx: Ctx) => boolean> = {
    bold: callCommand(toggleStrongCommand.key),
    italic: callCommand(toggleEmphasisCommand.key),
    strike: callCommand(toggleStrikethroughCommand.key),
    code: callCommand(toggleInlineCodeCommand.key),
    h1: callCommand(wrapInHeadingCommand.key, 1),
    h2: callCommand(wrapInHeadingCommand.key, 2),
    h3: callCommand(wrapInHeadingCommand.key, 3),
    paragraph: callCommand(turnIntoTextCommand.key),
    bulletList: callCommand(wrapInBulletListCommand.key),
    orderedList: callCommand(wrapInOrderedListCommand.key),
    blockquote: callCommand(wrapInBlockquoteCommand.key),
    codeBlock: callCommand(createCodeBlockCommand.key),
    rule: callCommand(insertHrCommand.key),
  };

  return {
    getMarkdown: serialize,

    setMarkdown(markdown: string): boolean {
      return withCtx((ctx) => {
        const view = ctx.get(editorViewCtx);
        const doc = ctx.get(parserCtx)(markdown);
        if (!doc || doc.eq(view.state.doc)) {
          return false;
        }
        applyingExternal = true;
        try {
          view.dispatch(view.state.tr.replaceWith(0, view.state.doc.content.size, doc.content));
        } finally {
          applyingExternal = false;
        }
        return true;
      });
    },

    setReadonly(value: boolean): void {
      readonly = value;
      withCtx((ctx) => ctx.get(editorViewCtx).setProps({ editable: () => !readonly }));
    },

    setPlaceholder(value: string): void {
      placeholder = value;
      withCtx((ctx) => {
        const view = ctx.get(editorViewCtx);
        view.dispatch(view.state.tr.setMeta(PLACEHOLDER_REFRESH, true));
      });
    },

    run(command: MarkdownCommand): void {
      if (readonly) {
        return;
      }
      withCtx((ctx) => {
        commands[command](ctx);
        ctx.get(editorViewCtx).focus();
      });
    },

    focus(): void {
      withCtx((ctx) => ctx.get(editorViewCtx).focus());
    },

    destroy(): void {
      void editor.destroy();
    },
  };
}
