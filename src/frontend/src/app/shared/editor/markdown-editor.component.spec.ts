import { Component, ErrorHandler, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { EDITOR_MARKDOWN_LIMITS, MarkdownLimits } from '../markdown/markdown-limits';
import {
  MARKDOWN_EDITOR_FACTORY,
  MARKDOWN_EDITOR_LIMITS,
  MarkdownEditorHandle,
  MarkdownEditorOptions,
} from './markdown-editor.adapter';
import { MarkdownEditorComponent } from './markdown-editor.component';

@Component({
  imports: [MarkdownEditorComponent],
  template: `
    <ag-markdown-editor
      [value]="value()"
      [readonly]="readonly()"
      [debounceMs]="20"
      placeholder="Leer"
      (valueChange)="changes.push($event)"
      (editorBlur)="blurs = blurs + 1"
    />
  `,
})
class HostComponent {
  readonly value = signal('Titel');
  readonly readonly = signal(false);
  readonly changes: string[] = [];
  blurs = 0;
}

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

/** Sends a key combination to the ProseMirror surface like a user would. */
function press(target: Element, key: string, init: KeyboardEventInit = {}): void {
  target.dispatchEvent(
    new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init }),
  );
}

describe('MarkdownEditorComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;

  const element = () => fixture.nativeElement as HTMLElement;
  const surface = () => element().querySelector('.ProseMirror') as HTMLElement;
  const toolbarButton = (command: string) =>
    element().querySelector(`[data-command="${command}"]`) as HTMLButtonElement | null;

  async function stable(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  beforeEach(async () => {
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    await stable();
  });

  afterEach(() => fixture.destroy());

  it('mount_LoadsEngineLazilyAndRendersValue', () => {
    expect(surface()).not.toBeNull();
    expect(surface().textContent).toBe('Titel');
    expect(toolbarButton('bold')?.disabled).toBe(false);
  });

  it('value_SetFromOutside_UpdatesEditorWithoutValueChange', async () => {
    host.value.set('# Neu\n\n- [ ] Aufgabe');
    await stable();
    await wait(60);

    expect(surface().querySelector('h1')?.textContent).toBe('Neu');
    expect(surface().querySelector('li[data-item-type="task"]')).not.toBeNull();
    expect(host.changes).toEqual([]);
  });

  it('userEdit_Toolbar_EmitsDebouncedValueChangeOnce', async () => {
    toolbarButton('h1')?.click();
    toolbarButton('bold')?.click();
    expect(host.changes).toEqual([]);

    await wait(60);

    expect(host.changes).toHaveLength(1);
    expect(host.changes[0].trim()).toBe('# Titel');
  });

  it('userEdit_Shortcut_EmitsValueChange', async () => {
    press(surface(), '2', { ctrlKey: true, altKey: true });
    await wait(60);

    expect(host.changes.map((c) => c.trim())).toEqual(['## Titel']);
  });

  it('userEdit_EchoedBackAsValue_DoesNotResetOrReemit', async () => {
    toolbarButton('h1')?.click();
    await wait(60);
    host.value.set(host.changes[0]);
    await stable();
    await wait(60);

    expect(host.changes).toHaveLength(1);
    expect(surface().querySelector('h1')?.textContent).toBe('Titel');
  });

  it('readonly_BlocksInputAndHidesToolbar', async () => {
    host.readonly.set(true);
    await stable();

    press(surface(), '1', { ctrlKey: true, altKey: true });
    await wait(60);

    expect(surface().getAttribute('contenteditable')).toBe('false');
    expect(toolbarButton('h1')).toBeNull();
    expect(surface().querySelector('h1')).toBeNull();
    expect(host.changes).toEqual([]);
  });

  it('readonly_SwitchedOffAgain_AllowsInput', async () => {
    host.readonly.set(true);
    await stable();
    host.readonly.set(false);
    await stable();

    press(surface(), '1', { ctrlKey: true, altKey: true });
    await wait(60);

    expect(surface().getAttribute('contenteditable')).toBe('true');
    expect(host.changes.map((c) => c.trim())).toEqual(['# Titel']);
  });

  it('blur_FlushesPendingChangeBeforeBlurEvent', async () => {
    toolbarButton('h1')?.click();
    surface().dispatchEvent(new FocusEvent('blur'));

    expect(host.changes.map((c) => c.trim())).toEqual(['# Titel']);
    expect(host.blurs).toBe(1);

    await wait(60);
    expect(host.changes).toHaveLength(1);
  });

  it('mount_NormalBodyBelowTheLimits_LoadsInTheRealEditor', { timeout: 30_000 }, async () => {
    // 100 KB of prose with headings and lists (Chrome: ~0.1 s; jsdom is slower)
    const section =
      '## Kapitel\n\n' +
      'Ein Absatz mit **fett**, *kursiv* und einem [Link](https://example.com). '.repeat(8) +
      '\n\n- erster Punkt\n- zweiter Punkt\n\n';
    const markdown = section.repeat(Math.ceil((100 * 1024) / section.length));
    fixture.destroy();
    fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.value.set(markdown);
    await stable();

    expect(element().querySelector('[data-testid="editor-fallback-text"]')).toBeNull();
    expect(surface().querySelectorAll('h2').length).toBe(Math.ceil((100 * 1024) / section.length));
  });

  it('placeholder_EmptyValue_IsShown', async () => {
    host.value.set('');
    await stable();

    expect(surface().querySelector('[data-placeholder="Leer"]')).not.toBeNull();
  });
});

describe('MarkdownEditorComponent load failure', () => {
  it.each([
    ['chunk import rejects', () => Promise.reject(new Error('chunk load failed'))],
    [
      'engine creation rejects',
      () => Promise.resolve(() => Promise.reject(new Error('init failed'))),
    ],
  ])('mount_%s_ShowsErrorInsteadOfLoadingHint', async (_name, factory) => {
    const logged = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    TestBed.configureTestingModule({
      providers: [{ provide: MARKDOWN_EDITOR_FACTORY, useValue: factory }],
    });
    const fixture = TestBed.createComponent(HostComponent);

    await fixture.whenStable();
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[data-testid="load-error"]')?.textContent?.trim()).toBe(
      'Editor konnte nicht geladen werden.',
    );
    expect(element.textContent).not.toContain('Editor wird geladen');
    // only a stack overflow (RangeError) is the text's fault and shown as plain text
    expect(element.querySelector('[data-testid="editor-fallback-text"]')).toBeNull();
    expect(logged).toHaveBeenCalled();
    fixture.destroy();
    logged.mockRestore();
  });
});

describe('MarkdownEditorComponent without limits (real engine)', () => {
  it('mount_ParserOverflowsItsStack_CatchesAndShowsPlainText', async () => {
    // Counter-check for the try/catch: with the limits switched off the real engine overflows.
    const warned = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    TestBed.configureTestingModule({
      providers: [
        {
          provide: MARKDOWN_EDITOR_LIMITS,
          useValue: {
            maxLength: Infinity,
            maxDelimiterRun: Infinity,
            maxLineNesting: Infinity,
            maxIndent: Infinity,
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.value.set('>'.repeat(10000));
    await fixture.whenStable();
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[data-testid="editor-fallback-text"]')?.textContent).toBe(
      '>'.repeat(10000),
    );
    expect(element.querySelector('.ProseMirror')).toBeNull();
    expect(warned.mock.calls[0][1]).toBeInstanceOf(RangeError);
    fixture.destroy();
    warned.mockRestore();
  });
});

@Component({
  imports: [MarkdownEditorComponent],
  template: `
    <ag-markdown-editor
      [value]="value()"
      [readonly]="readonly()"
      [debounceMs]="20"
      (valueChange)="changes.push($event)"
    >
      <a agEditorFallbackAction href="/raw" data-testid="fallback-action">Im Raw-View bearbeiten</a>
    </ag-markdown-editor>
  `,
})
class FallbackHostComponent {
  readonly value = signal('Titel');
  readonly readonly = signal(false);
  readonly changes: string[] = [];
}

/** Patterns from the AP-031 review (the parser overflows its stack or takes seconds). */
const PATHOLOGICAL: readonly [string, string][] = [
  ['quote run', '>'.repeat(10000)],
  ['emphasis run', '*'.repeat(10000) + 'x' + '*'.repeat(10000)],
  ['lazy continuation', '> a\n' + 'b\n'.repeat(3900)],
  ['list run', '- '.repeat(5000) + 'x'],
  ['too large', 'Ein ganz normaler Satz. '.repeat(Math.ceil((2 * 1024 * 1024) / 24))],
];

describe('MarkdownEditorComponent fallback', () => {
  let fixture: ComponentFixture<FallbackHostComponent>;
  let host: FallbackHostComponent;
  let engines: { destroyed: boolean; options: MarkdownEditorOptions }[];
  let createEngine: (options: MarkdownEditorOptions) => Promise<MarkdownEditorHandle>;

  const element = () => fixture.nativeElement as HTMLElement;
  const q = (id: string) => element().querySelector<HTMLElement>(`[data-testid="${id}"]`);

  function fakeEngine(options: MarkdownEditorOptions): MarkdownEditorHandle {
    const engine = { destroyed: false, options, current: options.initialValue };
    engines.push(engine);
    return {
      getMarkdown: () => engine.current,
      setMarkdown: (markdown) => {
        engine.current = markdown;
        return true;
      },
      setReadonly: () => undefined,
      setPlaceholder: () => undefined,
      run: () => undefined,
      focus: () => undefined,
      destroy: () => {
        engine.destroyed = true;
      },
    };
  }

  const editor = () =>
    fixture.debugElement.children[0].componentInstance as MarkdownEditorComponent;

  async function render(value: string, limits?: MarkdownLimits, readonly = false): Promise<void> {
    TestBed.configureTestingModule({
      providers: [
        {
          provide: MARKDOWN_EDITOR_FACTORY,
          useValue: () => Promise.resolve((o: MarkdownEditorOptions) => createEngine(o)),
        },
        ...(limits ? [{ provide: MARKDOWN_EDITOR_LIMITS, useValue: limits }] : []),
      ],
    });
    fixture = TestBed.createComponent(FallbackHostComponent);
    host = fixture.componentInstance;
    host.value.set(value);
    host.readonly.set(readonly);
    await fixture.whenStable();
    fixture.detectChanges();
  }

  beforeEach(() => {
    engines = [];
    createEngine = (o) => Promise.resolve(fakeEngine(o));
  });

  afterEach(() => fixture.destroy());

  it.each(PATHOLOGICAL)(
    'mount_%s_ShowsPlainTextWithNoticeAndActionWithoutEngineWithin200ms',
    async (_name, markdown) => {
      const handler = vi.fn();
      TestBed.configureTestingModule({
        providers: [{ provide: ErrorHandler, useValue: { handleError: handler } }],
      });
      const started = performance.now();
      await render(markdown);
      const elapsed = performance.now() - started;

      expect(q('editor-fallback-notice')?.textContent).toContain(
        'Dieser Text ist zu groß oder zu komplex für den Editor',
      );
      expect(q('editor-fallback-text')?.textContent).toBe(markdown);
      expect(q('fallback-action')?.textContent).toBe('Im Raw-View bearbeiten');
      expect(element().querySelector('.ag-md-toolbar')).toBeNull();
      expect(element().textContent).not.toContain('Editor wird geladen');
      expect(element().querySelector('ag-markdown-editor')?.getAttribute('aria-busy')).toBe(
        'false',
      );
      expect(engines).toEqual([]);
      expect(handler).not.toHaveBeenCalled();
      expect(elapsed).toBeLessThan(200);
    },
  );

  it('mount_NormalValue_LoadsEngineAndHidesFallbackAction', async () => {
    await render('# Titel\n\nEin *normaler* Text.');

    expect(engines).toHaveLength(1);
    expect(q('editor-fallback-notice')).toBeNull();
    expect(q('editor-fallback-text')).toBeNull();
    expect(q('fallback-action')).toBeNull();
  });

  it('mount_JustBelowAndAboveTheLimits_LoadsEngineOrFallsBack', async () => {
    const limits: MarkdownLimits = { ...EDITOR_MARKDOWN_LIMITS, maxLength: 20 };
    await render('x'.repeat(20), limits);
    expect(engines).toHaveLength(1);
    expect(q('editor-fallback-text')).toBeNull();
    fixture.destroy();
    TestBed.resetTestingModule();

    await render('x'.repeat(21), limits);
    expect(engines).toHaveLength(1); // still only the first one
    expect(q('editor-fallback-text')?.textContent).toBe('x'.repeat(21));
  });

  it('mount_EngineThrowsOnTheText_ShowsTheSameFallbackAndWarnsOnly', async () => {
    const warned = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const logged = vi.spyOn(console, 'error');
    createEngine = () => Promise.reject(new RangeError('Maximum call stack size exceeded'));

    await render('> tief');

    expect(q('editor-fallback-text')?.textContent).toBe('> tief');
    expect(q('editor-fallback-notice')).not.toBeNull();
    expect(q('load-error')).toBeNull();
    expect(warned).toHaveBeenCalled();
    expect(logged).not.toHaveBeenCalled();
    warned.mockRestore();
    logged.mockRestore();
  });

  it('mount_FirstSerializationThrows_DestroysEngineAndFallsBack', async () => {
    const warned = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    createEngine = (o) =>
      Promise.resolve({
        ...fakeEngine(o),
        getMarkdown: () => {
          throw new RangeError('Maximum call stack size exceeded');
        },
      });

    await render('Text');

    expect(engines[0].destroyed).toBe(true);
    expect(q('editor-fallback-text')?.textContent).toBe('Text');
    warned.mockRestore();
  });

  it('value_SetToPathologicalAfterMount_DropsEngineAndShowsText', async () => {
    await render('Normal');
    expect(engines).toHaveLength(1);

    host.value.set('>'.repeat(10000));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(engines[0].destroyed).toBe(true);
    expect(q('editor-fallback-text')?.textContent).toBe('>'.repeat(10000));
    expect(host.changes).toEqual([]);
  });

  it('value_EngineThrowsOnNewValue_DropsEngineAndShowsText', async () => {
    const warned = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    createEngine = (o) =>
      Promise.resolve({
        ...fakeEngine(o),
        setMarkdown: () => {
          throw new RangeError('Maximum call stack size exceeded');
        },
      });
    await render('Normal');

    host.value.set('Neu');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(engines[0].destroyed).toBe(true);
    expect(q('editor-fallback-text')?.textContent).toBe('Neu');
    warned.mockRestore();
  });

  it('value_EngineFailsOtherwiseOnNewValue_ReportsABrokenEditor', async () => {
    const logged = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    createEngine = (o) =>
      Promise.resolve({
        ...fakeEngine(o),
        setMarkdown: () => {
          throw new TypeError('engine bug');
        },
      });
    await render('Normal');

    host.value.set('Neu');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(engines[0].destroyed).toBe(true);
    expect(q('load-error')).not.toBeNull();
    expect(q('editor-fallback-text')).toBeNull();
    expect(logged).toHaveBeenCalled();
    logged.mockRestore();
  });

  it('mount_Readonly_DoesNotSerializeUntilWritable', async () => {
    let serializations = 0;
    createEngine = (o) => {
      const handle = fakeEngine(o);
      return Promise.resolve({
        ...handle,
        getMarkdown: () => {
          serializations++;
          return handle.getMarkdown();
        },
      });
    };
    await render('Titel', undefined, true);
    expect(engines).toHaveLength(1);

    // a new value while read-only is applied without serializing either
    host.value.set('Anders');
    fixture.detectChanges();
    await fixture.whenStable();
    editor().flush();
    expect(serializations).toBe(0);
    expect(host.changes).toEqual([]);

    host.readonly.set(false);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(serializations).toBe(1);

    // writable from now on: the agreed Markdown is the serialization, nothing to emit
    editor().flush();
    expect(host.changes).toEqual([]);
  });

  it('mount_Writable_SerializesOnceToKnowTheAgreedMarkdown', async () => {
    let serializations = 0;
    createEngine = (o) => {
      const handle = fakeEngine(o);
      return Promise.resolve({
        ...handle,
        getMarkdown: () => {
          serializations++;
          return handle.getMarkdown();
        },
      });
    };
    await render('Titel');

    expect(serializations).toBe(1);
  });

  it('fallback_NeverEmitsValueChangeAndGetMarkdownReturnsTheValue', async () => {
    const markdown = '>'.repeat(10000);
    await render(markdown);
    const editor = fixture.debugElement.children[0].componentInstance as MarkdownEditorComponent;

    editor.flush();
    await new Promise((resolve) => setTimeout(resolve, 60));

    expect(editor.getMarkdown()).toBe(markdown);
    expect(editor.fallback()).toBe(true);
    expect(host.changes).toEqual([]);
  });
});
