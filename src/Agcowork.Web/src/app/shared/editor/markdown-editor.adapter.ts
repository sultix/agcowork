import { InjectionToken } from '@angular/core';

/**
 * Library-neutral contract between `ag-markdown-editor` and the WYSIWYG engine.
 * This file must not import the engine so that the engine stays in a lazy chunk.
 */
export type MarkdownCommand =
  | 'bold'
  | 'italic'
  | 'strike'
  | 'code'
  | 'h1'
  | 'h2'
  | 'h3'
  | 'paragraph'
  | 'bulletList'
  | 'orderedList'
  | 'blockquote'
  | 'codeBlock'
  | 'rule';

export interface MarkdownEditorOptions {
  /** Element the editor mounts into. */
  root: HTMLElement;
  initialValue: string;
  readonly: boolean;
  placeholder: string;
  /** Called synchronously after every user-driven document change (not for `setMarkdown`). */
  onDocChange: () => void;
  onBlur: () => void;
}

export interface MarkdownEditorHandle {
  /** Serializes the current document to Markdown. */
  getMarkdown(): string;
  /**
   * Replaces the document without calling `onDocChange`. Returns `false` and leaves the document
   * (and the cursor) untouched when the Markdown parses to the same document.
   */
  setMarkdown(markdown: string): boolean;
  setReadonly(readonly: boolean): void;
  setPlaceholder(placeholder: string): void;
  run(command: MarkdownCommand): void;
  focus(): void;
  destroy(): void;
}

export type MarkdownEditorFactory = (
  options: MarkdownEditorOptions,
) => Promise<MarkdownEditorHandle>;

/** Loads the engine on demand. Override in tests or to swap the engine. */
export const MARKDOWN_EDITOR_FACTORY = new InjectionToken<() => Promise<MarkdownEditorFactory>>(
  'MARKDOWN_EDITOR_FACTORY',
  {
    providedIn: 'root',
    factory: () => () => import('./milkdown-adapter').then((m) => m.createMilkdownEditor),
  },
);
