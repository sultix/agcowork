import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { HttpTestingController } from '@angular/common/http/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import {
  API,
  COLLECTION_ID,
  RECORD_ID,
  SPACE_ID,
  collection,
  expectRequest,
  member,
  pageProviders,
  propertyDefinition,
  recordDto,
  respond,
  serve,
  settle,
  space,
  typeInto,
  workspace,
} from '../../core/testing/test-utils';
import {
  MARKDOWN_EDITOR_FACTORY,
  MarkdownEditorHandle,
  MarkdownEditorOptions,
} from '../../shared/editor/markdown-editor.adapter';
import { FakeCodeEditor, provideFakeCodeEditor } from '../../shared/raw-view/fake-code-editor';
import { RecordDocumentComponent } from './record-document.component';
import { recordLeaveGuard } from './record-leave.guard';
import { AUTOSAVE_DELAY_MS } from './record-save.service';

@Component({ template: 'stub' })
class StubComponent {}

/** Stand-in for Milkdown: lets tests type into the "editor" without waiting for its debounce. */
class FakeMarkdownEngine implements MarkdownEditorHandle {
  current: string;
  readonly: boolean;
  destroyed = false;

  readonly root: HTMLElement;

  constructor(private readonly options: MarkdownEditorOptions) {
    this.root = options.root;
    this.current = options.initialValue;
    this.readonly = options.readonly;
  }

  type(markdown: string): void {
    this.current = markdown;
    this.options.onDocChange();
  }
  getMarkdown(): string {
    return this.current;
  }
  setMarkdown(markdown: string): boolean {
    this.current = markdown;
    return true;
  }
  setReadonly(readonly: boolean): void {
    this.readonly = readonly;
  }
  setPlaceholder(): void {
    // not needed by the tests
  }
  run(): void {
    // not needed by the tests
  }
  focus(): void {
    // not needed by the tests
  }
  destroy(): void {
    this.destroyed = true;
  }
}

const RECORD_URL = `${API}/records/${RECORD_ID}`;
const LOAD_URL = `${RECORD_URL}?includeBody=true`;
const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

describe('RecordDocumentComponent', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;
  let engines: FakeMarkdownEngine[];
  let codeEditors: FakeCodeEditor[];

  /** Renders the document and answers all initial requests. */
  async function render(
    options: {
      scopes?: string[];
      record?: Partial<ReturnType<typeof recordDto>>;
      schema?: ReturnType<typeof propertyDefinition>[];
    } = {},
  ): Promise<void> {
    engines = [];
    codeEditors = [];
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({
          ws: workspace({ scopes: options.scopes ?? ['read', 'write'] }),
          routes: [
            {
              path: 'w/:workspaceSlug',
              children: [
                {
                  path: 'records/:recordId',
                  component: RecordDocumentComponent,
                  canDeactivate: [recordLeaveGuard],
                },
                { path: 'spaces/:spaceId', component: StubComponent },
                { path: 'collections/:collectionId', component: StubComponent },
              ],
            },
          ],
        }),
        provideFakeCodeEditor(codeEditors),
        {
          provide: MARKDOWN_EDITOR_FACTORY,
          useValue: () =>
            Promise.resolve((o: MarkdownEditorOptions) => {
              const engine = new FakeMarkdownEngine(o);
              engines.push(engine);
              return Promise.resolve(engine);
            }),
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl(`/w/demo/records/${RECORD_ID}`);
    await serve(http, [
      [LOAD_URL, recordDto({ body: 'Hallo', ...options.record })],
      ...(options.record?.collectionId === null
        ? []
        : ([
            [
              `${API}/collections/${COLLECTION_ID}`,
              collection({ schema: [...collection().schema, ...(options.schema ?? [])] }),
            ],
          ] as const)),
      [`${API}/spaces`, [space()]],
      [`${API}/collections`, [collection()]],
      [`${API}/members`, [member(), member({ actorId: 'agent-1', name: 'Coder', kind: 'agent' })]],
    ]);
    await navigation;
    await settle(harness.fixture);
  }

  afterEach(() => {
    vi.useRealTimers();
    http.verify();
  });

  const root = () => harness.routeNativeElement as HTMLElement;
  // Overlays (e.g. the confirm dialog) render into the CDK overlay container in <body>.
  const q = <T extends HTMLElement = HTMLElement>(id: string) =>
    root().querySelector<T>(`[data-testid="${id}"]`) ??
    document.body.querySelector<T>(`[data-testid="${id}"]`);
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const row = (key: string) => root().querySelector<HTMLElement>(`[data-key="${key}"]`)!;
  const isBody = (e: FakeMarkdownEngine) => !!e.root.closest('[data-testid="record-body"]');
  const bodyEditor = () => engines.filter(isBody).at(-1)!;
  const propertyEditor = () => engines.filter((e) => !isBody(e)).at(-1)!;

  async function change() {
    harness.fixture.detectChanges();
    await settle(harness.fixture);
  }

  /** User edits the title and the status, then the answer to the save is `revision`. */
  async function edit(): Promise<void> {
    typeInto(q<HTMLInputElement>('record-title')!, 'Login fertig');
    typeInto(row('status').querySelector('select')!, 'done');
    await change();
  }

  /** Focuses the body (as a user would) and types; the editor has not reported it yet. */
  function typeBody(markdown: string): void {
    q('record-body')!.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    bodyEditor().type(markdown);
  }

  const saved = (overrides = {}) =>
    recordDto({
      title: 'Login fertig',
      properties: { status: 'done' } as never,
      revision: 4,
      updatedBy: 'agent-1',
      ...overrides,
    });

  describe('rendering', () => {
    it('render_Record_ShowsTitleBreadcrumbRevisionAndMeta', async () => {
      await render();

      expect(q<HTMLInputElement>('record-title')!.value).toBe('Login bauen');
      expect(text(q('breadcrumbs'))).toBe('Produkt›Aufgaben›Login bauen');
      expect(text(q('record-revision'))).toBe('Revision 3');
      expect(text(q('record-meta'))).toContain('von Admin');
      expect(q('archived-badge')).toBeNull();
      expect(bodyEditor().current).toBe('Hallo');
    });

    it('render_Collection_ShowsEditorPerPropertyWithoutTheTitle', async () => {
      await render();

      expect(
        Array.from(root().querySelectorAll('[data-testid="property-row"]')).map((r) =>
          r.getAttribute('data-key'),
        ),
      ).toEqual(['status', 'estimate']);
      expect(row('status').querySelector('select')!.value).toBe('todo');
      expect(text(root().querySelector('dt'))).toBe('Status');
    });

    it('render_FreePage_ShowsNoPropertiesPanel', async () => {
      await render({ record: { collectionId: null } });
      // (the collection request is not made for free pages, see http.verify)
      expect(q('properties-panel')).toBeNull();
      expect(text(q('breadcrumbs'))).toBe('Produkt›Login bauen');
    });

    it('render_NotFound_ShowsError', async () => {
      TestBed.configureTestingModule({
        providers: pageProviders({
          routes: [
            { path: 'w/:workspaceSlug/records/:recordId', component: RecordDocumentComponent },
          ],
        }),
      });
      http = TestBed.inject(HttpTestingController);
      harness = await RouterTestingHarness.create();
      const navigation = harness.navigateByUrl(`/w/demo/records/${RECORD_ID}`);
      (await expectRequest(http, 'GET', LOAD_URL)).flush(
        { code: 'not_found', status: 404 },
        { status: 404, statusText: 'Not Found' },
      );
      await serve(http, [
        [`${API}/spaces`, []],
        [`${API}/collections`, []],
        [`${API}/members`, []],
      ]);
      await navigation;
      await settle(harness.fixture);

      expect(text(q('load-error'))).toContain('Nicht gefunden');
    });
  });

  describe('saving', () => {
    it('save_Button_SendsPatchWithExpectedRevisionAndShowsNewRevision', async () => {
      await render();
      await edit();
      expect(text(q('save-status'))).toBe('Ungespeicherte Änderungen');

      q('save')!.click();
      const patch = await expectRequest(http, 'PATCH', RECORD_URL);
      expect(patch.request.body).toEqual({
        title: 'Login fertig',
        properties: { status: 'done' },
        expectedRevision: 3,
      });
      patch.flush(saved());
      await settle(harness.fixture);

      expect(text(q('record-revision'))).toBe('Revision 4');
      expect(text(q('save-status'))).toBe('Gespeichert');
      expect(text(q('record-meta'))).toContain('von Coder');
      expect(q<HTMLButtonElement>('save')!.disabled).toBe(true);
      expect(q<HTMLInputElement>('record-title')!.value).toBe('Login fertig');
    });

    it('save_BodyEdited_SendsBodyAndKeepsEditorText', async () => {
      await render();
      typeBody('Neuer Text');
      await wait(350);
      await change();

      q('save')!.click();
      const patch = await expectRequest(http, 'PATCH', RECORD_URL);
      expect(patch.request.body).toEqual({ body: 'Neuer Text', expectedRevision: 3 });
      patch.flush(recordDto({ revision: 4 }));
      await settle(harness.fixture);

      expect(bodyEditor().current).toBe('Neuer Text');
      expect(engines).toHaveLength(1);
    });

    it.each([
      ['ctrlKey', { ctrlKey: true }],
      ['metaKey', { metaKey: true }],
    ])('keyboard_%sS_SavesImmediatelyAndPreventsBrowserSave', async (_name, modifier) => {
      await render();
      await edit();

      const event = new KeyboardEvent('keydown', { key: 's', cancelable: true, ...modifier });
      document.dispatchEvent(event);
      const patch = await expectRequest(http, 'PATCH', RECORD_URL);
      patch.flush(saved());

      expect(event.defaultPrevented).toBe(true);
      await settle(harness.fixture);
      expect(text(q('save-status'))).toBe('Gespeichert');
    });

    it('autoSave_TwoSecondsAfterLastChange_Saves', async () => {
      await render();
      vi.useFakeTimers();

      typeInto(q<HTMLInputElement>('record-title')!, 'Login fertig');
      vi.advanceTimersByTime(AUTOSAVE_DELAY_MS - 1);
      expect(http.match(RECORD_URL)).toHaveLength(0);
      vi.advanceTimersByTime(1);

      const [patch] = http.match(RECORD_URL);
      expect(patch.request.body).toEqual({ title: 'Login fertig', expectedRevision: 3 });
      patch.flush(saved({ properties: { status: 'todo' } }));
      vi.useRealTimers();
      await settle(harness.fixture);
    });

    it('save_ValidationErrors_ShowOnTheirEditors', async () => {
      await render();
      typeInto(row('estimate').querySelector('input')!, '-5');
      typeInto(q<HTMLInputElement>('record-title')!, '');
      await change();

      q('save')!.click();
      (await expectRequest(http, 'PATCH', RECORD_URL)).flush(
        {
          code: 'validation_failed',
          status: 400,
          errors: [
            { field: 'title', code: 'required', message: 'x' },
            { field: 'properties.estimate', code: 'out_of_range', message: 'y' },
          ],
        },
        { status: 400, statusText: 'Bad Request' },
      );
      await settle(harness.fixture);

      expect(text(row('estimate').querySelector('[data-testid="editor-errors"]'))).toBe(
        'Wert liegt außerhalb des erlaubten Bereichs.',
      );
      expect(text(q('title-error'))).toBe('Pflichtfeld.');
      expect(text(q('save-error'))).toContain('markierten Felder');
    });
  });

  describe('conflict', () => {
    async function conflicted(): Promise<void> {
      await render();
      await edit();
      q('save')!.click();
      (await expectRequest(http, 'PATCH', RECORD_URL)).flush(
        {
          code: 'revision_mismatch',
          status: 409,
          currentRevision: 5,
          changedKeys: ['status', 'body'],
        },
        { status: 409, statusText: 'Conflict' },
      );
      await settle(harness.fixture);
    }

    it('save_RevisionMismatch_ShowsDialogWithChangedKeys', async () => {
      await conflicted();

      expect(text(q('conflict-dialog'))).toContain('Von jemand anderem geändert');
      expect(text(q('conflict-dialog'))).toContain('Revision 5');
      expect(text(q('conflict-keys'))).toBe('statusbody');
    });

    it('save_RevisionMismatch_DoesNotFocusAnActionButton', async () => {
      await conflicted();

      // Enter or Space right after the dialog opened must not trigger "Neu laden"
      expect(document.activeElement).not.toBe(q('conflict-reload'));
      expect(document.activeElement).not.toBe(q('conflict-overwrite'));
      expect(q('conflict-dialog')!.contains(document.activeElement)).toBe(true);
    });

    it('conflictReload_DiscardsOwnChangesAndLoadsServerState', async () => {
      await conflicted();

      q('conflict-reload')!.click();
      await respond(
        http,
        'GET',
        LOAD_URL,
        recordDto({
          title: 'Fremd',
          properties: { status: 'done' } as never,
          body: 'Fremder Text',
          revision: 5,
        }),
      );
      await settle(harness.fixture);

      expect(q('conflict-dialog')).toBeNull();
      expect(q<HTMLInputElement>('record-title')!.value).toBe('Fremd');
      expect(text(q('record-revision'))).toBe('Revision 5');
      expect(bodyEditor().current).toBe('Fremder Text');
      expect(text(q('save-status'))).toBe('');
    });

    it('conflictOverwrite_SendsWithoutExpectedRevision', async () => {
      await conflicted();

      q('conflict-overwrite')!.click();
      const patch = await expectRequest(http, 'PATCH', RECORD_URL);
      expect(patch.request.body).toEqual({
        title: 'Login fertig',
        properties: { status: 'done' },
      });
      patch.flush(saved({ revision: 6 }));
      // body was not edited here: the other writer's body is fetched into a fresh editor
      await respond(http, 'GET', LOAD_URL, recordDto({ body: 'Fremder Text', revision: 6 }));
      await settle(harness.fixture);

      expect(q('conflict-dialog')).toBeNull();
      expect(text(q('record-revision'))).toBe('Revision 6');
      expect(bodyEditor().current).toBe('Fremder Text');
    });
  });

  describe('leaving the page', () => {
    it('canDeactivate_BodyTypedWithinDebounceWindow_SavesBeforeNavigating', async () => {
      await render();
      typeBody('Gerade getippt');

      const navigation = harness.navigateByUrl(`/w/demo/spaces/${SPACE_ID}`);
      const patch = await expectRequest(http, 'PATCH', RECORD_URL);
      expect(patch.request.body).toEqual({ body: 'Gerade getippt', expectedRevision: 3 });
      expect(TestBed.inject(Router).url).toBe(`/w/demo/records/${RECORD_ID}`);
      patch.flush(recordDto({ revision: 4 }));
      await navigation;

      expect(TestBed.inject(Router).url).toBe(`/w/demo/spaces/${SPACE_ID}`);
    });

    it('canDeactivate_PendingTitleChange_SavesBeforeNavigating', async () => {
      await render();
      await edit();

      const navigation = harness.navigateByUrl(`/w/demo/spaces/${SPACE_ID}`);
      (await expectRequest(http, 'PATCH', RECORD_URL)).flush(saved());
      await navigation;

      expect(TestBed.inject(Router).url).toBe(`/w/demo/spaces/${SPACE_ID}`);
    });

    it('canDeactivate_NothingChanged_NavigatesWithoutRequest', async () => {
      await render();
      // The editor serializes the loaded body differently; focusing alone is no edit.
      bodyEditor().current = 'Hallo\n';
      q('record-body')!.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));

      await harness.navigateByUrl(`/w/demo/spaces/${SPACE_ID}`);

      expect(TestBed.inject(Router).url).toBe(`/w/demo/spaces/${SPACE_ID}`);
    });

    it('canDeactivate_SaveConflicts_StaysOnPageWithDialog', async () => {
      await render();
      await edit();

      const navigation = harness.navigateByUrl(`/w/demo/spaces/${SPACE_ID}`);
      (await expectRequest(http, 'PATCH', RECORD_URL)).flush(
        { code: 'revision_mismatch', status: 409, currentRevision: 5, changedKeys: ['title'] },
        { status: 409, statusText: 'Conflict' },
      );
      await navigation;
      await settle(harness.fixture);

      expect(TestBed.inject(Router).url).toBe(`/w/demo/records/${RECORD_ID}`);
      expect(q('conflict-dialog')).not.toBeNull();
    });
  });

  describe('leaving with unsaved work', () => {
    const NOTES = propertyDefinition({ key: 'notes', name: 'Notizen', type: 'markdown' });
    const PAYLOAD = propertyDefinition({ key: 'payload', name: 'Payload', type: 'json' });
    const away = `/w/demo/spaces/${SPACE_ID}`;

    it('canDeactivate_MarkdownPropertyTypedWithinDebounceWindow_IsSaved', async () => {
      await render({ schema: [NOTES] });
      propertyEditor().type('Notiz');

      const navigation = harness.navigateByUrl(away);
      const patch = await expectRequest(http, 'PATCH', RECORD_URL);
      expect(patch.request.body).toEqual({ properties: { notes: 'Notiz' }, expectedRevision: 3 });
      patch.flush(recordDto({ revision: 4 }));
      await navigation;

      expect(TestBed.inject(Router).url).toBe(away);
    });

    it('canDeactivate_TextTypedWhileSaving_IsSavedBeforeLeaving', async () => {
      await render();
      await edit();

      const navigation = harness.navigateByUrl(away);
      const first = await expectRequest(http, 'PATCH', RECORD_URL);
      typeBody('Spät getippt');
      first.flush(saved());
      const second = await expectRequest(http, 'PATCH', RECORD_URL);
      expect(second.request.body).toEqual({ body: 'Spät getippt', expectedRevision: 4 });
      second.flush(recordDto({ revision: 5 }));
      await navigation;

      expect(TestBed.inject(Router).url).toBe(away);
    });

    it('canDeactivate_SaveFails_AsksToDiscardAndLeavesOnConfirm', async () => {
      await render();
      await edit();

      const navigation = harness.navigateByUrl(away);
      (await expectRequest(http, 'PATCH', RECORD_URL)).flush(
        { code: 'internal_error', status: 500 },
        { status: 500, statusText: 'Server Error' },
      );
      await vi.waitFor(() => expect(q('confirm-dialog')).not.toBeNull());
      expect(text(q('confirm-dialog'))).toContain('Änderungen verwerfen und verlassen?');
      q('confirm-ok')!.click();
      await navigation;

      expect(TestBed.inject(Router).url).toBe(away);
    });

    it('canDeactivate_SaveFails_StaysWhenUserCancels', async () => {
      await render();
      await edit();

      const navigation = harness.navigateByUrl(away);
      (await expectRequest(http, 'PATCH', RECORD_URL)).flush(
        { code: 'internal_error', status: 500 },
        { status: 500, statusText: 'Server Error' },
      );
      await vi.waitFor(() => expect(q('confirm-dialog')).not.toBeNull());
      q('confirm-cancel')!.click();
      await navigation;

      expect(TestBed.inject(Router).url).toBe(`/w/demo/records/${RECORD_ID}`);
      expect(q<HTMLInputElement>('record-title')!.value).toBe('Login fertig');
    });

    it('canDeactivate_RecordArchivedMeanwhile_ReloadsMetadataAndAsks', async () => {
      await render();
      await edit();

      const navigation = harness.navigateByUrl(away);
      (await expectRequest(http, 'PATCH', RECORD_URL)).flush(
        { code: 'record_archived', status: 409 },
        { status: 409, statusText: 'Conflict' },
      );
      (await expectRequest(http, 'GET', RECORD_URL)).flush(
        recordDto({ revision: 4, archivedAt: '2026-11-07T12:00:00Z' }),
      );
      await vi.waitFor(() => expect(q('confirm-dialog')).not.toBeNull());
      harness.fixture.detectChanges(); // the pending navigation keeps the app unstable

      expect(text(q('archived-badge'))).toBe('archiviert');
      expect(q<HTMLInputElement>('record-title')!.readOnly).toBe(true);
      q('confirm-ok')!.click();
      await navigation;
      expect(TestBed.inject(Router).url).toBe(away);
    });

    it('canDeactivate_InvalidJson_WarnsInsteadOfLeavingSilently', async () => {
      await render({ schema: [PAYLOAD] });
      codeEditors[0].type('{"a": ');
      await change();

      const navigation = harness.navigateByUrl(away);
      await vi.waitFor(() => expect(q('confirm-dialog')).not.toBeNull());
      expect(text(q('confirm-dialog'))).toContain('Ungültige Eingaben');
      q('confirm-cancel')!.click();
      await navigation;

      expect(TestBed.inject(Router).url).toBe(`/w/demo/records/${RECORD_ID}`);
    });

    it('beforeunload_UnsavedChanges_AsksBrowserToConfirm', async () => {
      await render();
      await edit();

      const event = new Event('beforeunload', { cancelable: true });
      window.dispatchEvent(event);

      expect(event.defaultPrevented).toBe(true);
    });

    it('beforeunload_BodyTypedWithinDebounceWindow_AsksBrowserToConfirm', async () => {
      await render();
      typeBody('Gerade getippt');

      const event = new Event('beforeunload', { cancelable: true });
      window.dispatchEvent(event);

      expect(event.defaultPrevented).toBe(true);
    });

    it('beforeunload_NothingToSave_DoesNotInterfere', async () => {
      await render();

      const event = new Event('beforeunload', { cancelable: true });
      window.dispatchEvent(event);

      expect(event.defaultPrevented).toBe(false);
    });

    it('navigate_RecordToRecord_SavesFirstThenLoadsTheOther', async () => {
      await render();
      await edit();

      const navigation = harness.navigateByUrl('/w/demo/records/other-id');
      (await expectRequest(http, 'PATCH', RECORD_URL)).flush(saved());
      await respond(
        http,
        'GET',
        `${API}/records/other-id?includeBody=true`,
        recordDto({ id: 'other-id', title: 'Anderer', body: 'Anderer Text' }),
      );
      await navigation;
      await settle(harness.fixture);

      expect(q<HTMLInputElement>('record-title')!.value).toBe('Anderer');
      expect(bodyEditor().current).toBe('Anderer Text');
      expect(text(q('save-status'))).toBe('');
    });
  });

  describe('read-only', () => {
    it('render_Viewer_LocksEverything', async () => {
      await render({ scopes: ['read'] });

      expect(q('save')).toBeNull();
      expect(q('archive-toggle')).toBeNull();
      expect(q<HTMLInputElement>('record-title')!.readOnly).toBe(true);
      expect(row('status').querySelector('select')!.disabled).toBe(true);
      expect(row('estimate').querySelector('input')!.readOnly).toBe(true);
      expect(bodyEditor().readonly).toBe(true);
      expect(text(q('readonly-notice'))).toContain('Leserechte');
      expect(q('show-markdown')).not.toBeNull();
    });

    it('canDeactivate_Viewer_NavigatesWithoutSaving', async () => {
      await render({ scopes: ['read'] });

      await harness.navigateByUrl(`/w/demo/spaces/${SPACE_ID}`);

      expect(TestBed.inject(Router).url).toBe(`/w/demo/spaces/${SPACE_ID}`);
    });

    it('render_ArchivedRecord_IsReadonlyWithRestoreForWriters', async () => {
      await render({ record: { archivedAt: '2026-11-07T12:00:00Z' } });

      expect(text(q('archived-badge'))).toBe('archiviert');
      expect(q<HTMLInputElement>('record-title')!.readOnly).toBe(true);
      expect(bodyEditor().readonly).toBe(true);
      expect(q('save')).toBeNull();
      expect(text(q('archive-toggle'))).toBe('Wiederherstellen');
    });

    it('render_ArchivedRecordForViewer_HasNoRestoreButton', async () => {
      await render({ scopes: ['read'], record: { archivedAt: '2026-11-07T12:00:00Z' } });

      expect(q('archive-toggle')).toBeNull();
    });
  });

  describe('archive and restore', () => {
    it('archive_Confirmed_PostsAndLocksTheDocument', async () => {
      await render();

      q('archive-toggle')!.click();
      await change();
      expect(q('confirm-dialog')).not.toBeNull();
      q('confirm-ok')!.click();
      (await expectRequest(http, 'POST', `${RECORD_URL}/archive`)).flush(
        recordDto({ revision: 4, archivedAt: '2026-11-07T12:00:00Z' }),
      );
      await settle(harness.fixture);

      expect(text(q('archived-badge'))).toBe('archiviert');
      expect(q<HTMLInputElement>('record-title')!.readOnly).toBe(true);
      expect(bodyEditor().readonly).toBe(true);
      expect(text(q('archive-toggle'))).toBe('Wiederherstellen');
      expect(q('confirm-dialog')).toBeNull();
    });

    it('archive_Cancelled_SendsNothing', async () => {
      await render();

      q('archive-toggle')!.click();
      await change();
      q('confirm-cancel')!.click();
      await change();

      expect(q('confirm-dialog')).toBeNull();
    });

    it('archive_WithPendingChanges_SavesFirst', async () => {
      await render();
      await edit();

      q('archive-toggle')!.click();
      await change();
      q('confirm-ok')!.click();
      (await expectRequest(http, 'PATCH', RECORD_URL)).flush(saved());
      (await expectRequest(http, 'POST', `${RECORD_URL}/archive`)).flush(
        saved({ revision: 5, archivedAt: '2026-11-07T12:00:00Z' }),
      );
      await settle(harness.fixture);

      expect(text(q('record-revision'))).toBe('Revision 5');
    });

    it('restore_Confirmed_PostsAndUnlocksTheDocument', async () => {
      await render({ record: { archivedAt: '2026-11-07T12:00:00Z' } });

      q('archive-toggle')!.click();
      await change();
      q('confirm-ok')!.click();
      (await expectRequest(http, 'POST', `${RECORD_URL}/restore`)).flush(
        recordDto({ revision: 4 }),
      );
      await settle(harness.fixture);

      expect(q('archived-badge')).toBeNull();
      expect(q<HTMLInputElement>('record-title')!.readOnly).toBe(false);
      expect(bodyEditor().readonly).toBe(false);
      expect(text(q('archive-toggle'))).toBe('Archivieren');
    });

    it('archive_Fails_ShowsMessageAndKeepsState', async () => {
      await render();

      q('archive-toggle')!.click();
      await change();
      q('confirm-ok')!.click();
      (await expectRequest(http, 'POST', `${RECORD_URL}/archive`)).flush(
        { code: 'record_archived', status: 409 },
        { status: 409, statusText: 'Conflict' },
      );
      await settle(harness.fixture);

      expect(text(q('save-error'))).toContain('archiviert');
      expect(q('archived-badge')).toBeNull();
    });
  });

  describe('markdown view', () => {
    it('showMarkdown_Click_FetchesFrontmatterMarkdownIntoReadonlyRawView', async () => {
      await render();

      q('show-markdown')!.click();
      const request = await expectRequest(http, 'GET', `${RECORD_URL}?format=markdown`);
      expect(request.request.responseType).toBe('text');
      request.flush('---\nid: x\n---\n\nHallo');
      await settle(harness.fixture);

      expect(q('markdown-dialog')).not.toBeNull();
      expect(codeEditors[0].language).toBe('markdown');
      expect(codeEditors[0].getValue()).toBe('---\nid: x\n---\n\nHallo');
      expect(codeEditors[0].readonly).toBe(true);

      q('markdown-close')!.click();
      await change();
      expect(q('markdown-dialog')).toBeNull();
    });

    it('showMarkdown_Escape_ClosesDialog', async () => {
      await render();

      q('show-markdown')!.click();
      (await expectRequest(http, 'GET', `${RECORD_URL}?format=markdown`)).flush('Hallo');
      await settle(harness.fixture);
      expect(q('markdown-dialog')).not.toBeNull();

      q('markdown-dialog')!.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }),
      );
      await vi.waitFor(() => expect(q('markdown-dialog')).toBeNull());
    });

    it('showMarkdown_PendingChanges_SavesFirst', async () => {
      await render();
      await edit();

      q('show-markdown')!.click();
      (await expectRequest(http, 'PATCH', RECORD_URL)).flush(saved());
      (await expectRequest(http, 'GET', `${RECORD_URL}?format=markdown`)).flush('# Neu');
      await settle(harness.fixture);

      expect(codeEditors[0].getValue()).toBe('# Neu');
    });
  });
});
