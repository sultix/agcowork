import { InjectionToken } from '@angular/core';

/**
 * Library-neutral contract between `ag-raw-view` and the code editor engine.
 * This file must not import the engine so that the engine stays in a lazy chunk.
 */
export type RawLanguage = 'json' | 'markdown';

export interface CodeEditorOptions {
  root: HTMLElement;
  value: string;
  language: RawLanguage;
  readonly: boolean;
  dark: boolean;
  /** Called on every user edit with the full text (not for `setValue`). */
  onChange: (value: string) => void;
}

export interface CodeEditorHandle {
  getValue(): string;
  /** Replaces the text without calling `onChange`. */
  setValue(value: string): void;
  setLanguage(language: RawLanguage): void;
  setReadonly(readonly: boolean): void;
  setDark(dark: boolean): void;
  destroy(): void;
}

export type CodeEditorFactory = (options: CodeEditorOptions) => Promise<CodeEditorHandle>;

/** Loads the engine on demand. Override in tests or to swap the engine. */
export const CODE_EDITOR_FACTORY = new InjectionToken<() => Promise<CodeEditorFactory>>(
  'CODE_EDITOR_FACTORY',
  {
    providedIn: 'root',
    factory: () => () => import('./monaco-adapter').then((m) => m.createMonacoEditor),
  },
);
