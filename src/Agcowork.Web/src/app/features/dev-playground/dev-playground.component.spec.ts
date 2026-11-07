import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FakeCodeEditor, provideFakeCodeEditor } from '../../shared/raw-view/fake-code-editor';
import { DevPlaygroundComponent } from './dev-playground.component';
import { PLAYGROUND_JSON, PLAYGROUND_MARKDOWN } from './playground-samples';

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

describe('DevPlaygroundComponent', () => {
  let fixture: ComponentFixture<DevPlaygroundComponent>;
  let editors: FakeCodeEditor[];

  const surface = () =>
    (fixture.nativeElement as HTMLElement).querySelector('ag-markdown-editor .ProseMirror')!;
  const rawMarkdown = () => editors.find((e) => e.language === 'markdown')!;
  const rawJson = () => editors.find((e) => e.language === 'json')!;

  beforeEach(async () => {
    editors = [];
    TestBed.configureTestingModule({ providers: [provideFakeCodeEditor(editors)] });
    fixture = TestBed.createComponent(DevPlaygroundComponent);
    await fixture.whenStable();
  });

  afterEach(() => fixture.destroy());

  it('init_ShowsSamplesInAllThreeBlocks', () => {
    expect(surface().querySelector('h1')?.textContent).toBe('Spielwiese');
    expect(rawMarkdown().value).toBe(PLAYGROUND_MARKDOWN);
    expect(rawJson().value).toBe(PLAYGROUND_JSON);
  });

  it('editorChange_AppearsInMarkdownRawViewWithinASecond', async () => {
    const heading2 = (fixture.nativeElement as HTMLElement).querySelector(
      'ag-markdown-editor [data-command="h2"]',
    ) as HTMLButtonElement;

    // Cursor sits at the start of the document (the H1); turn it into an H2.
    heading2.click();
    await wait(400);
    await fixture.whenStable();

    expect(rawMarkdown().value.startsWith('## Spielwiese')).toBe(true);
  });

  it('rawChange_AppearsInEditor', async () => {
    rawMarkdown().type('# Aus Raw\n\n- [x] fertig');
    await fixture.whenStable();

    expect(surface().querySelector('h1')?.textContent).toBe('Aus Raw');
    expect(surface().querySelector('li[data-checked="true"]')).not.toBeNull();
  });
});
