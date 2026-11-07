import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MARKDOWN_EDITOR_FACTORY } from './markdown-editor.adapter';
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
    expect(logged).toHaveBeenCalled();
    fixture.destroy();
    logged.mockRestore();
  });
});
