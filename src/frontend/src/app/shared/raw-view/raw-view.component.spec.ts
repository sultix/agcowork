import { Component, signal, viewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ThemeService } from '../../core/theme/theme.service';
import { CODE_EDITOR_FACTORY, RawLanguage } from './code-editor.adapter';
import { FakeCodeEditor, provideFakeCodeEditor } from './fake-code-editor';
import { RawViewComponent } from './raw-view.component';

@Component({
  imports: [RawViewComponent],
  template: `
    <ag-raw-view
      [value]="value()"
      [language]="language()"
      [readonly]="readonly()"
      (valueChange)="changes.push($event)"
    />
  `,
})
class HostComponent {
  readonly view = viewChild.required(RawViewComponent);
  readonly value = signal('{"a": 1}');
  readonly language = signal<RawLanguage>('json');
  readonly readonly = signal(false);
  readonly changes: string[] = [];
}

describe('RawViewComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let editors: FakeCodeEditor[];

  const element = () => fixture.nativeElement as HTMLElement;
  const status = () => element().querySelector('[data-testid="raw-status"]')?.textContent?.trim();
  const toggle = () =>
    element().querySelector('[data-testid="raw-mode-toggle"]') as HTMLButtonElement | null;
  const engine = () => editors[0];

  async function stable(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  beforeEach(async () => {
    editors = [];
    localStorage.clear();
    document.documentElement.classList.remove('dark');
    TestBed.configureTestingModule({ providers: [provideFakeCodeEditor(editors)] });
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    await stable();
  });

  afterEach(() => {
    fixture.destroy();
    localStorage.clear();
    document.documentElement.classList.remove('dark');
  });

  it('mount_PassesValueLanguageAndModeToEngine', () => {
    expect(editors).toHaveLength(1);
    expect(engine().value).toBe('{"a": 1}');
    expect(engine().language).toBe('json');
    expect(engine().readonly).toBe(false);
    expect(element().querySelector('[data-fake-code-editor]')).not.toBeNull();
    expect(status()).toBe('gültig');
  });

  it('json_InvalidValue_IsValidFalseWithErrorMessage', async () => {
    host.value.set('{"a": }');
    await stable();

    expect(host.view().isValid()).toBe(false);
    expect(host.view().error()).toBeTruthy();
    expect(status()).toContain('ungültig');
    expect(status()).toContain(host.view().error()!);
  });

  it('userInput_EmitsValueChangeAndRevalidates', async () => {
    engine().type('{"a": ');
    await stable();

    expect(host.changes).toEqual(['{"a": ']);
    expect(host.view().text()).toBe('{"a": ');
    expect(host.view().isValid()).toBe(false);

    engine().type('{"a": 2}');
    await stable();

    expect(host.changes).toEqual(['{"a": ', '{"a": 2}']);
    expect(host.view().isValid()).toBe(true);
    expect(host.view().error()).toBeNull();
  });

  it('value_SetFromOutside_UpdatesEngineWithoutValueChange', async () => {
    host.value.set('{"b": 2}');
    await stable();

    expect(engine().setValueCalls).toEqual(['{"b": 2}']);
    expect(host.changes).toEqual([]);
  });

  it('value_EchoOfUserInput_DoesNotResetEngine', async () => {
    engine().type('{"c": 3}');
    host.value.set(host.changes[0]);
    await stable();

    expect(engine().setValueCalls).toEqual([]);
  });

  it('language_Switch_UpdatesEngineAndSkipsJsonValidation', async () => {
    host.value.set('# Kein JSON');
    await stable();
    expect(host.view().isValid()).toBe(false);

    host.language.set('markdown');
    await stable();

    expect(engine().language).toBe('markdown');
    expect(host.view().isValid()).toBe(true);
    expect(host.view().error()).toBeNull();
    expect(status()).toBeUndefined();
    expect(element().querySelector('ag-raw-view')?.getAttribute('data-language')).toBe('markdown');

    host.language.set('json');
    await stable();
    expect(engine().language).toBe('json');
    expect(host.view().isValid()).toBe(false);
  });

  it('toggle_SwitchesBetweenReadingAndEditing', async () => {
    expect(toggle()?.textContent?.trim()).toBe('Lesen');

    toggle()?.click();
    await stable();

    expect(engine().readonly).toBe(true);
    expect(toggle()?.textContent?.trim()).toBe('Bearbeiten');
    engine().type('ignored');
    expect(host.changes).toEqual([]);

    toggle()?.click();
    await stable();

    expect(engine().readonly).toBe(false);
  });

  it('readonly_LocksEngineAndHidesToggle', async () => {
    host.readonly.set(true);
    await stable();

    expect(engine().readonly).toBe(true);
    expect(toggle()).toBeNull();
  });

  it('theme_Dark_IsForwardedToEngine', async () => {
    expect(engine().dark).toBe(false);

    TestBed.inject(ThemeService).mode.set('dark');
    await stable();

    expect(engine().dark).toBe(true);
  });

  it('destroy_DisposesEngine', () => {
    fixture.destroy();

    expect(engine().destroyed).toBe(true);
  });
});

describe('RawViewComponent load failure', () => {
  it('mount_ChunkImportRejects_ShowsErrorInsteadOfLoadingHint', async () => {
    const logged = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    TestBed.configureTestingModule({
      providers: [
        {
          provide: CODE_EDITOR_FACTORY,
          useValue: () => Promise.reject(new Error('chunk load failed')),
        },
      ],
    });
    const fixture = TestBed.createComponent(HostComponent);

    await fixture.whenStable();
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[data-testid="load-error"]')?.textContent?.trim()).toBe(
      'Ansicht konnte nicht geladen werden.',
    );
    expect(element.textContent).not.toContain('Ansicht wird geladen');
    expect(fixture.componentInstance.view().isValid()).toBe(true);
    expect(logged).toHaveBeenCalled();
    fixture.destroy();
    logged.mockRestore();
  });
});
