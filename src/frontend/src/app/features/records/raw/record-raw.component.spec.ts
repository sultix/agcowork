import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import {
  API,
  COLLECTION_ID,
  RECORD_ID,
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
  workspace,
} from '../../../core/testing/test-utils';
import { FakeCodeEditor, provideFakeCodeEditor } from '../../../shared/raw-view/fake-code-editor';
import { recordLeaveGuard } from '../record-leave.guard';
import { RecordRawComponent } from './record-raw.component';

@Component({ template: 'stub' })
class StubComponent {}

const RECORD_URL = `${API}/records/${RECORD_ID}`;
const LOAD_URL = `${RECORD_URL}?includeBody=true`;
const MARKDOWN_URL = `${RECORD_URL}?format=markdown`;
const FRONTMATTER = '---\nid: x\n---\nHallo';

describe('RecordRawComponent', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;
  let editors: FakeCodeEditor[];

  async function render(
    options: { scopes?: string[]; record?: Partial<ReturnType<typeof recordDto>> } = {},
  ): Promise<void> {
    editors = [];
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({
          ws: workspace({ scopes: options.scopes ?? ['read', 'write'] }),
          routes: [
            {
              path: 'w/:workspaceSlug',
              children: [
                {
                  path: 'records/:recordId/raw',
                  component: RecordRawComponent,
                  canDeactivate: [recordLeaveGuard],
                },
                { path: 'records/:recordId', component: StubComponent },
                { path: 'records/:recordId/history', component: StubComponent },
                { path: 'spaces/:spaceId', component: StubComponent },
                { path: 'collections/:collectionId', component: StubComponent },
              ],
            },
          ],
        }),
        provideFakeCodeEditor(editors),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl(`/w/demo/records/${RECORD_ID}/raw`);
    await serve(http, [
      [
        LOAD_URL,
        recordDto({
          body: 'Hallo',
          properties: { status: 'todo', estimate: 3, created_at: '2026-11-07' } as never,
          ...options.record,
        }),
      ],
      [
        `${API}/collections/${COLLECTION_ID}`,
        collection({
          schema: [
            ...collection().schema,
            propertyDefinition({ key: 'created_at', type: 'datetime', readOnly: true }),
          ],
        }),
      ],
      [MARKDOWN_URL, FRONTMATTER],
      [`${API}/spaces`, [space()]],
      [`${API}/collections`, [collection()]],
      [`${API}/members`, [member()]],
    ]);
    await navigation;
    await settle(harness.fixture);
  }

  afterEach(() => http.verify());

  const root = () => harness.routeNativeElement as HTMLElement;
  // Overlays (e.g. the confirm dialog) render into the CDK overlay container in <body>.
  const q = <T extends HTMLElement = HTMLElement>(id: string) =>
    root().querySelector<T>(`[data-testid="${id}"]`) ??
    document.body.querySelector<T>(`[data-testid="${id}"]`);
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const editorIn = (id: string) => editors.filter((e) => q(id)!.contains(e.root)).at(-1)!;
  const jsonEditor = () => editorIn('raw-properties');
  const bodyEditor = () => editorIn('raw-body');
  const frontmatterEditor = () => editorIn('raw-frontmatter');

  /** The saved revision changed: the frontmatter view is fetched again. */
  const reloadFrontmatter = () => respond(http, 'GET', MARKDOWN_URL, FRONTMATTER);

  async function change() {
    harness.fixture.detectChanges();
    await settle(harness.fixture);
  }

  async function typeJson(value: unknown): Promise<void> {
    jsonEditor().type(typeof value === 'string' ? value : JSON.stringify(value, null, 2));
    await change();
  }

  const saved = (overrides = {}) =>
    recordDto({ properties: { status: 'todo', estimate: 3 } as never, revision: 4, ...overrides });

  describe('rendering', () => {
    it('render_Record_ShowsPropertiesJsonBodyAndFrontmatter', async () => {
      await render();

      // computed (read-only) properties are not part of the editable JSON
      expect(JSON.parse(jsonEditor().value)).toEqual({ status: 'todo', estimate: 3 });
      expect(jsonEditor().language).toBe('json');
      expect(bodyEditor().value).toBe('Hallo');
      expect(bodyEditor().language).toBe('markdown');
      expect(frontmatterEditor().value).toBe(FRONTMATTER);
      expect(frontmatterEditor().readonly).toBe(true);
      expect(text(q('record-revision'))).toBe('Revision 3');
      expect(text(q('breadcrumbs'))).toBe('Produkt›Aufgaben›Login bauen');
    });

    it('render_Tabs_LinkToDocumentRawAndHistory', async () => {
      await render();

      expect(q('tab-document')!.getAttribute('href')).toBe(`/w/demo/records/${RECORD_ID}`);
      expect(q('tab-raw')!.getAttribute('href')).toBe(`/w/demo/records/${RECORD_ID}/raw`);
      expect(q('tab-history')!.getAttribute('href')).toBe(`/w/demo/records/${RECORD_ID}/history`);
    });

    it('copy_Button_WritesFrontmatterMarkdownToClipboard', async () => {
      await render();
      const writeText = vi.fn().mockResolvedValue(undefined);
      vi.stubGlobal('navigator', { clipboard: { writeText } });
      try {
        q('copy-markdown')!.click();
        await change();

        expect(writeText).toHaveBeenCalledWith(FRONTMATTER);
        expect(text(q('copied'))).toBe('Kopiert');
      } finally {
        vi.unstubAllGlobals();
      }
    });
  });

  describe('saving properties', () => {
    it('save_ChangedAndRemovedKeys_SendsPatchWithNullForRemovedKey', async () => {
      await render();
      expect(q<HTMLButtonElement>('save')!.disabled).toBe(true);

      await typeJson({ status: 'done' }); // estimate removed
      expect(q<HTMLButtonElement>('save')!.disabled).toBe(false);
      q('save')!.click();

      const patch = await expectRequest(http, 'PATCH', RECORD_URL);
      expect(patch.request.body).toEqual({
        properties: { status: 'done', estimate: null },
        expectedRevision: 3,
      });
      patch.flush(saved({ properties: { status: 'done' } as never }));
      await reloadFrontmatter();
      await settle(harness.fixture);

      expect(text(q('record-revision'))).toBe('Revision 4');
      expect(text(q('save-status'))).toBe('Gespeichert');
      expect(q<HTMLButtonElement>('save')!.disabled).toBe(true);
      expect(JSON.parse(jsonEditor().value)).toEqual({ status: 'done' });
    });

    it('save_AddedKey_SendsOnlyTheChangedKeys', async () => {
      await render();

      await typeJson({ status: 'todo', estimate: 3, note: 'x' });
      q('save')!.click();

      const patch = await expectRequest(http, 'PATCH', RECORD_URL);
      expect(patch.request.body).toEqual({ properties: { note: 'x' }, expectedRevision: 3 });
      patch.flush(saved());
      await reloadFrontmatter();
    });

    it('save_InvalidJson_BlocksSavingAndShowsStatus', async () => {
      await render();

      await typeJson('{ "status": ');

      expect(q<HTMLButtonElement>('save')!.disabled).toBe(true);
      expect(text(q('save-status'))).toContain('Ungültiges JSON');
      expect(text(root())).toContain('ungültig');
    });

    it('save_JsonThatIsNotAnObject_BlocksSaving', async () => {
      await render();

      await typeJson('[1, 2]');

      expect(q<HTMLButtonElement>('save')!.disabled).toBe(true);
      expect(text(q('json-shape-error'))).toContain('JSON-Objekt');
    });

    it('save_ValidationError_ListsMessagesWithLineOfTheKey', async () => {
      await render();
      await typeJson({ status: 'todo', estimate: 'viel' });
      q('save')!.click();

      (await expectRequest(http, 'PATCH', RECORD_URL)).flush(
        {
          code: 'validation_failed',
          status: 400,
          errors: [{ field: 'properties.estimate', code: 'invalid_number' }],
        },
        { status: 400, statusText: 'Bad Request' },
      );
      await settle(harness.fixture);

      const [error] = Array.from(root().querySelectorAll('[data-testid="json-error"]'));
      expect(text(error)).toContain('estimate');
      expect(text(error)).toContain('(Zeile 3)');
      expect(text(q('save-error'))).toContain('markierten Felder');
    });
  });

  describe('after a failed save', () => {
    it('save_JsonRevertedAfterValidationError_DoesNotResendTheRejectedKey', async () => {
      await render();
      await typeJson({ status: 'todo', estimate: 'viel' });
      q('save')!.click();
      (await expectRequest(http, 'PATCH', RECORD_URL)).flush(
        {
          code: 'validation_failed',
          status: 400,
          errors: [{ field: 'properties.estimate', code: 'invalid_number' }],
        },
        { status: 400, statusText: 'Bad Request' },
      );
      await settle(harness.fixture);

      await typeJson({ status: 'todo', estimate: 3 }); // back to the saved state
      bodyEditor().type('Neuer Text');
      await change();
      q('save')!.click();

      const patch = await expectRequest(http, 'PATCH', RECORD_URL);
      expect(patch.request.body).toEqual({ body: 'Neuer Text', expectedRevision: 3 });
      patch.flush(saved());
      await reloadFrontmatter();
    });

    it('save_JsonRevertedAfterValidationError_NoUnsavedChangesLeft', async () => {
      await render();
      await typeJson({ status: 'todo', estimate: 'viel' });
      q('save')!.click();
      (await expectRequest(http, 'PATCH', RECORD_URL)).flush(
        { code: 'validation_failed', status: 400, errors: [{ field: 'properties.estimate' }] },
        { status: 400, statusText: 'Bad Request' },
      );
      await settle(harness.fixture);

      await typeJson({ status: 'todo', estimate: 3 });

      expect(q<HTMLButtonElement>('save')!.disabled).toBe(true);
    });
  });

  describe('invalid or empty JSON', () => {
    it('edit_JsonCleared_KeepsEditorsAndStillSavesTheBody', async () => {
      await render();
      bodyEditor().type('Neuer Text');
      await change();
      const before = editors.length;

      await typeJson('');

      expect(editors.length).toBe(before); // nothing was re-created
      expect(q('raw-properties')).not.toBeNull();
      expect(q('raw-body')).not.toBeNull();
      expect(text(q('save-status'))).toContain('Ungültiges JSON');
      expect(q<HTMLButtonElement>('save')!.disabled).toBe(false);

      q('save')!.click();
      const patch = await expectRequest(http, 'PATCH', RECORD_URL);
      expect(patch.request.body).toEqual({ body: 'Neuer Text', expectedRevision: 3 });
      patch.flush(saved());
      await reloadFrontmatter();
    });

    it('save_BodyWhileJsonIsHalfTyped_KeepsTheJsonText', async () => {
      await render();
      await typeJson('{ "status": ');
      bodyEditor().type('Neuer Text');
      await change();
      q('save')!.click();
      (await expectRequest(http, 'PATCH', RECORD_URL)).flush(saved());
      await reloadFrontmatter();
      await settle(harness.fixture);

      expect(jsonEditor().value).toBe('{ "status": ');
      expect(text(q('save-status'))).toContain('Ungültiges JSON');
    });

    it('edit_InvalidJsonAndNothingElse_StaysUnsavable', async () => {
      await render();

      await typeJson('{');

      expect(q<HTMLButtonElement>('save')!.disabled).toBe(true);
    });

    it('save_BodyAndTitleErrors_ShownAsGeneralMessagesNotInTheJsonList', async () => {
      await render();
      bodyEditor().type('x');
      await change();
      q('save')!.click();
      (await expectRequest(http, 'PATCH', RECORD_URL)).flush(
        {
          code: 'validation_failed',
          status: 400,
          errors: [{ field: 'body', code: 'too_long' }],
        },
        { status: 400, statusText: 'Bad Request' },
      );
      await settle(harness.fixture);

      expect(q('json-errors')).toBeNull();
      expect(text(q('general-error'))).toContain('body');
    });
  });

  describe('another record in the same component', () => {
    it('navigate_OtherRecord_NeverCarriesJsonOfTheFirstOne', async () => {
      await render();
      await typeJson({ status: 'done', estimate: 99 }); // unsaved edit on record A
      const other = '0199a000-0000-7000-8000-0000000000d2';

      const navigation = harness.navigateByUrl(`/w/demo/records/${other}/raw`);
      await vi.waitFor(() => expect(q('confirm-dialog')).not.toBeNull());
      q('confirm-ok')!.click();
      await respond(
        http,
        'GET',
        `${API}/records/${other}?includeBody=true`,
        recordDto({
          id: other,
          title: 'Anderer',
          body: 'B',
          properties: { status: 'todo' } as never,
        }),
      );
      await respond(http, 'GET', `${API}/records/${other}?format=markdown`, 'fm-b');
      await navigation;
      await settle(harness.fixture);

      expect(JSON.parse(jsonEditor().value)).toEqual({ status: 'todo' });
      expect(bodyEditor().value).toBe('B');
      expect(q<HTMLButtonElement>('save')!.disabled).toBe(true);
    });
  });

  describe('saving the body', () => {
    it('save_Body_SendsBodyWithExpectedRevision', async () => {
      await render();

      bodyEditor().type('Neuer Text');
      await change();
      expect(text(q('save-status'))).toBe('Ungespeicherte Änderungen');
      q('save')!.click();

      const patch = await expectRequest(http, 'PATCH', RECORD_URL);
      expect(patch.request.body).toEqual({ body: 'Neuer Text', expectedRevision: 3 });
      patch.flush(saved());
      await reloadFrontmatter();
      await settle(harness.fixture);

      expect(text(q('save-status'))).toBe('Gespeichert');
    });

    it('save_NoAutoSave_NothingIsSentWithoutClickingSave', async () => {
      await render();
      vi.useFakeTimers({ toFake: ['setTimeout'] });
      try {
        bodyEditor().type('Neuer Text');
        await vi.advanceTimersByTimeAsync(5000);
        http.expectNone({ method: 'PATCH', url: RECORD_URL });
      } finally {
        vi.useRealTimers();
      }
    });
  });

  describe('conflict', () => {
    it('save_RevisionMismatch_ShowsDialogAndOverwriteSendsWithoutExpectedRevision', async () => {
      await render();
      await typeJson({ status: 'done', estimate: 3 });
      q('save')!.click();
      (await expectRequest(http, 'PATCH', RECORD_URL)).flush(
        { code: 'revision_mismatch', status: 409, currentRevision: 5, changedKeys: ['status'] },
        { status: 409, statusText: 'Conflict' },
      );
      await settle(harness.fixture);

      expect(text(q('conflict-dialog'))).toContain('Revision 5');
      expect(text(q('conflict-keys'))).toBe('status');

      q('conflict-overwrite')!.click();
      const patch = await expectRequest(http, 'PATCH', RECORD_URL);
      expect(patch.request.body).toEqual({ properties: { status: 'done' } });
      patch.flush(saved({ revision: 6, properties: { status: 'done', estimate: 3 } as never }));
      await respond(http, 'GET', LOAD_URL, recordDto({ body: 'Hallo', revision: 6 }));
      await reloadFrontmatter();
      await settle(harness.fixture);

      expect(q('conflict-dialog')).toBeNull();
      expect(text(q('record-revision'))).toBe('Revision 6');
    });

    it('conflictReload_DiscardsJsonEditsAndShowsServerState', async () => {
      await render();
      await typeJson({ status: 'done', estimate: 3 });
      q('save')!.click();
      (await expectRequest(http, 'PATCH', RECORD_URL)).flush(
        { code: 'revision_mismatch', status: 409, currentRevision: 5, changedKeys: ['status'] },
        { status: 409, statusText: 'Conflict' },
      );
      await settle(harness.fixture);

      q('conflict-reload')!.click();
      await respond(
        http,
        'GET',
        LOAD_URL,
        recordDto({ body: 'Fremd', revision: 5, properties: { status: 'review' } as never }),
      );
      await reloadFrontmatter();
      await settle(harness.fixture);

      expect(q('conflict-dialog')).toBeNull();
      expect(JSON.parse(jsonEditor().value)).toEqual({ status: 'review' });
      expect(bodyEditor().value).toBe('Fremd');
    });
  });

  describe('read-only', () => {
    it('render_ViewerWithoutWriteScope_EverythingReadOnlyAndNoSaveButton', async () => {
      await render({ scopes: ['read'] });

      expect(q('save')).toBeNull();
      expect(jsonEditor().readonly).toBe(true);
      expect(bodyEditor().readonly).toBe(true);
      expect(text(q('readonly-notice'))).toContain('Leserechte');
    });

    it('render_ArchivedRecord_ReadOnly', async () => {
      await render({ record: { archivedAt: '2026-11-07T12:00:00Z' } });

      expect(q('save')).toBeNull();
      expect(jsonEditor().readonly).toBe(true);
      expect(text(q('readonly-notice'))).toContain('Archiviert');
    });
  });

  describe('leaving the page', () => {
    it('canDeactivate_UnsavedJsonEdits_AsksAndStaysOnCancel', async () => {
      await render();
      await typeJson({ status: 'done', estimate: 3 });

      const navigation = harness.navigateByUrl('/w/demo/spaces/s1');
      await vi.waitFor(() => expect(q('confirm-dialog')).not.toBeNull());
      expect(text(q('confirm-dialog'))).toContain('Änderungen verwerfen');

      q('confirm-cancel')!.click();
      await navigation;

      expect(TestBed.inject(Router).url).toBe(`/w/demo/records/${RECORD_ID}/raw`);
      expect(q('save')).not.toBeNull();
    });

    it('canDeactivate_UnsavedJsonEdits_LeavesAfterDiscarding', async () => {
      await render();
      await typeJson({ status: 'done', estimate: 3 });

      const navigation = harness.navigateByUrl('/w/demo/spaces/s1');
      await vi.waitFor(() => expect(q('confirm-dialog')).not.toBeNull());
      q('confirm-ok')!.click();
      await navigation;

      expect(TestBed.inject(Router).url).toBe('/w/demo/spaces/s1');
    });

    it('canDeactivate_NoEdits_LeavesWithoutQuestion', async () => {
      await render();

      await harness.navigateByUrl(`/w/demo/records/${RECORD_ID}/history`);

      expect(harness.routeNativeElement?.textContent).toContain('stub');
    });
  });
});
