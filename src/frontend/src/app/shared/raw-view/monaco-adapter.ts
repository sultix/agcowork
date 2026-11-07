// Curated Monaco build: core API plus the contributions a raw view needs. Importing
// `editor.main` instead would pull every language and the TypeScript service (several MB).
import * as monaco from 'monaco-editor/editor/editor.api.js';
// Services the editor's own contributions ask for (code lens, drop into editor, suggestions).
// A contribution that is bundled without its service registration makes Monaco log
// "depends on UNKNOWN service …" on every editor creation.
import 'monaco-editor/editor/contrib/codelens/browser/codeLensCache.js';
import 'monaco-editor/editor/common/services/treeViewsDndService.js';
import 'monaco-editor/editor/contrib/suggest/browser/suggestMemory.js';
import 'monaco-editor/editor/contrib/bracketMatching/browser/bracketMatching.js';
import 'monaco-editor/editor/contrib/clipboard/browser/clipboard.js';
import 'monaco-editor/editor/contrib/comment/browser/comment.js';
import 'monaco-editor/editor/contrib/contextmenu/browser/contextmenu.js';
import 'monaco-editor/editor/contrib/find/browser/findController.js';
import 'monaco-editor/editor/contrib/folding/browser/folding.js';
import 'monaco-editor/editor/contrib/format/browser/formatActions.js';
import 'monaco-editor/editor/contrib/hover/browser/hoverContribution.js';
import 'monaco-editor/editor/contrib/linesOperations/browser/linesOperations.js';
import 'monaco-editor/editor/contrib/wordHighlighter/browser/wordHighlighter.js';
import 'monaco-editor/editor/contrib/placeholderText/browser/placeholderText.contribution.js';
import 'monaco-editor/editor/contrib/readOnlyMessage/browser/contribution.js';
import 'monaco-editor/languages/definitions/markdown/register.js';
import 'monaco-editor/languages/features/json/register.js';
import type { CodeEditorHandle, CodeEditorOptions, RawLanguage } from './code-editor.adapter';
import { monacoThemeColors } from './monaco-theme';

const MONACO_STYLESHEET_ID = 'ag-monaco-styles';
/** Emitted by angular.json (`styles` entry with `inject: false`). */
const MONACO_STYLESHEET_HREF = 'monaco-editor.css';

// CSS imported from Monaco's JS is not attached to lazy chunks by the Angular builder, so the
// prebuilt stylesheet is loaded once when the first editor mounts.
function ensureStylesheet(doc: Document): void {
  if (doc.getElementById(MONACO_STYLESHEET_ID)) {
    return;
  }
  const link = doc.createElement('link');
  link.id = MONACO_STYLESHEET_ID;
  link.rel = 'stylesheet';
  link.href = MONACO_STYLESHEET_HREF;
  doc.head.appendChild(link);
}

function ensureEnvironment(): void {
  if (self.MonacoEnvironment?.getWorker) {
    return;
  }
  self.MonacoEnvironment = {
    getWorker(_workerId: string, label: string): Worker {
      if (label === 'json') {
        return new Worker(new URL('./json.worker', import.meta.url), { type: 'module' });
      }
      return new Worker(new URL('./editor.worker', import.meta.url), { type: 'module' });
    },
  };
}

/** Defines `ag-light`/`ag-dark` from the current theme variables and activates one of them. */
function applyTheme(dark: boolean): void {
  const style = getComputedStyle(document.documentElement);
  const colors = monacoThemeColors((name) => style.getPropertyValue(name).trim());
  const name = dark ? 'ag-dark' : 'ag-light';
  monaco.editor.defineTheme(name, {
    base: dark ? 'vs-dark' : 'vs',
    inherit: true,
    rules: [],
    colors,
  });
  monaco.editor.setTheme(name);
}

export async function createMonacoEditor(options: CodeEditorOptions): Promise<CodeEditorHandle> {
  ensureStylesheet(document);
  ensureEnvironment();

  const model = monaco.editor.createModel(options.value, options.language);
  const editor = monaco.editor.create(options.root, {
    model,
    readOnly: options.readonly,
    automaticLayout: true,
    minimap: { enabled: false },
    scrollBeyondLastLine: false,
    wordWrap: options.language === 'markdown' ? 'on' : 'off',
    fontSize: 13,
    tabSize: 2,
    renderLineHighlight: 'line',
    fixedOverflowWidgets: true,
  });
  applyTheme(options.dark);

  let applyingExternal = false;
  const subscription = model.onDidChangeContent(() => {
    if (!applyingExternal) {
      options.onChange(model.getValue());
    }
  });

  return {
    getValue: () => model.getValue(),
    setValue(value: string): void {
      if (value === model.getValue()) {
        return;
      }
      applyingExternal = true;
      try {
        // pushEditOperations keeps the undo stack, unlike model.setValue.
        model.pushEditOperations(
          [],
          [{ range: model.getFullModelRange(), text: value }],
          () => null,
        );
      } finally {
        applyingExternal = false;
      }
    },
    setLanguage(language: RawLanguage): void {
      monaco.editor.setModelLanguage(model, language);
      editor.updateOptions({ wordWrap: language === 'markdown' ? 'on' : 'off' });
    },
    setReadonly(readonly: boolean): void {
      editor.updateOptions({ readOnly: readonly });
    },
    setDark: applyTheme,
    destroy(): void {
      subscription.dispose();
      editor.dispose();
      model.dispose();
    },
  };
}
