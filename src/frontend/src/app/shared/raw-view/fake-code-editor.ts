import { Provider } from '@angular/core';
import {
  CODE_EDITOR_FACTORY,
  CodeEditorHandle,
  CodeEditorOptions,
  RawLanguage,
} from './code-editor.adapter';

/**
 * Test double for the Monaco adapter (Monaco needs layout and workers, which jsdom lacks).
 * Records what the component asks of the engine and lets tests simulate typing.
 */
export class FakeCodeEditor implements CodeEditorHandle {
  value: string;
  language: RawLanguage;
  readonly: boolean;
  dark: boolean;
  destroyed = false;
  readonly setValueCalls: string[] = [];
  /** The element the editor was mounted into. */
  readonly root: HTMLElement;

  constructor(private readonly options: CodeEditorOptions) {
    this.root = options.root;
    this.value = options.value;
    this.language = options.language;
    this.readonly = options.readonly;
    this.dark = options.dark;
    options.root.setAttribute('data-fake-code-editor', '');
  }

  /** Simulates user input; ignored while read-only, like the real editor. */
  type(value: string): void {
    if (this.readonly) {
      return;
    }
    this.value = value;
    this.options.onChange(value);
  }

  getValue(): string {
    return this.value;
  }
  setValue(value: string): void {
    this.setValueCalls.push(value);
    this.value = value;
  }
  setLanguage(language: RawLanguage): void {
    this.language = language;
  }
  setReadonly(readonly: boolean): void {
    this.readonly = readonly;
  }
  setDark(dark: boolean): void {
    this.dark = dark;
  }
  destroy(): void {
    this.destroyed = true;
  }
}

/** Provides the fake engine; created instances are pushed into `instances`. */
export function provideFakeCodeEditor(instances: FakeCodeEditor[] = []): Provider {
  return {
    provide: CODE_EDITOR_FACTORY,
    useValue: () =>
      Promise.resolve((options: CodeEditorOptions) => {
        const editor = new FakeCodeEditor(options);
        instances.push(editor);
        return Promise.resolve(editor);
      }),
  };
}
