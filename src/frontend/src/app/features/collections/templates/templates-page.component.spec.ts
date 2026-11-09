import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HttpTestingController, TestRequest } from '@angular/common/http/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { LIVE_RELOAD_DELAY_MS, LIVE_RELOAD_MAX_WAIT_MS } from '../../../core/realtime/live-reload';
import { FakeRealtime, provideFakeRealtime } from '../../../core/testing/fake-hub';
import { MembersStore } from '../../../core/workspace/members.store';
import { signal } from '@angular/core';
import {
  API,
  COLLECTION_ID,
  Collection,
  RECORD_ID,
  STATUS_DEFINITION,
  activityEvent,
  collection,
  expectRequest,
  pageProviders,
  propertyDefinition,
  queryPage,
  recordDto,
  respond,
  respondPost,
  serve,
  settle,
  space,
  typeInto,
  workspace,
} from '../../../core/testing/test-utils';
import {
  MARKDOWN_EDITOR_FACTORY,
  MARKDOWN_EDITOR_LIMITS,
  MarkdownEditorHandle,
  MarkdownEditorOptions,
} from '../../../shared/editor/markdown-editor.adapter';
import { recordLeaveGuard } from '../../records/record-leave.guard';
import { EDITOR_MARKDOWN_LIMITS } from '../../../shared/markdown/markdown-limits';
import { TemplateDto } from './template-draft';
import { TemplatesPageComponent } from './templates-page.component';

@Component({ template: 'stub' })
class StubComponent {}

/** Stand-in for Milkdown: lets tests type into the "editor" without its debounce or engine. */
class FakeMarkdownEngine implements MarkdownEditorHandle {
  current: string;
  destroyed = false;
  readonly root: HTMLElement;

  constructor(private readonly options: MarkdownEditorOptions) {
    this.root = options.root;
    this.current = options.initialValue;
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
  setReadonly(): void {
    // not needed by the tests
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

const COLLECTION_URL = `${API}/collections/${COLLECTION_ID}`;
const TEMPLATES_URL = `${COLLECTION_URL}/templates`;
const OTHER_COLLECTION_ID = '0199a000-0000-7000-8000-0000000000c9';
const MEMBERS_STUB = {
  members: signal([]),
  nameOf: (id: string | null | undefined) => id ?? '',
  kindOf: () => null,
};

const SCHEMA = [
  ...collection().schema,
  propertyDefinition({ key: 'created', name: 'Angelegt', type: 'created_at' }),
];

function template(overrides: Partial<TemplateDto> = {}): TemplateDto {
  return {
    id: 't-adr',
    collectionId: COLLECTION_ID,
    name: 'ADR',
    description: 'Für Entscheidungen',
    propertiesPreset: { status: 'todo', estimate: 3 } as never,
    bodyMarkdown: '# Kontext\n\nWarum?',
    isDefault: true,
    createdBy: 'a1',
    createdAt: '2026-11-07T10:00:00Z',
    ...overrides,
  };
}

const BUG = template({
  id: 't-bug',
  name: 'Bug',
  description: null,
  propertiesPreset: {} as never,
  bodyMarkdown: '',
  isDefault: false,
});

describe('TemplatesPageComponent', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;
  let realtime: FakeRealtime;
  let engines: FakeMarkdownEngine[];

  async function render(
    options: {
      scopes?: string[];
      collection?: Collection;
      templates?: TemplateDto[];
      archivedWorkspace?: boolean;
      query?: string;
      /** Answers of the requests a `fromRecord` page makes; `false`: none expected. */
      record?: ReturnType<typeof recordDto> | false;
      limits?: typeof EDITOR_MARKDOWN_LIMITS;
      /** The editor engine overflows its stack on every text (a crafted Markdown). */
      engineOverflows?: boolean;
      /** The editor engine cannot be loaded or breaks (not a stack overflow). */
      engineFails?: boolean;
      /** The record behind `fromRecord` does not exist for this workspace. */
      recordMissing?: boolean;
    } = {},
  ): Promise<void> {
    engines = [];
    realtime = new FakeRealtime();
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({
          ws: workspace({
            scopes: options.scopes ?? ['read', 'write', 'schema', 'admin'],
            archivedAt: options.archivedWorkspace ? '2026-11-08T10:00:00Z' : null,
          }),
          routes: [
            {
              path: 'w/:workspaceSlug',
              children: [
                {
                  path: 'settings/collections/:collectionId/templates',
                  component: TemplatesPageComponent,
                  canDeactivate: [recordLeaveGuard],
                },
                { path: 'settings/collections/:collectionId/schema', component: StubComponent },
                { path: 'collections/:collectionId', component: StubComponent },
                { path: 'spaces/:spaceId', component: StubComponent },
              ],
            },
          ],
        }),
        provideFakeRealtime(realtime),
        { provide: MembersStore, useValue: MEMBERS_STUB },
        { provide: LIVE_RELOAD_DELAY_MS, useValue: 5 },
        { provide: LIVE_RELOAD_MAX_WAIT_MS, useValue: 20 },
        ...(options.limits ? [{ provide: MARKDOWN_EDITOR_LIMITS, useValue: options.limits }] : []),
        {
          provide: MARKDOWN_EDITOR_FACTORY,
          useValue: () =>
            Promise.resolve((o: MarkdownEditorOptions) => {
              if (options.engineFails) {
                return Promise.reject(new Error('chunk failed'));
              }
              if (options.engineOverflows) {
                return Promise.reject(new RangeError('Maximum call stack size exceeded'));
              }
              const engine = new FakeMarkdownEngine(o);
              engines.push(engine);
              return Promise.resolve(engine);
            }),
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl(
      `/w/demo/settings/collections/${COLLECTION_ID}/templates${options.query ?? ''}`,
    );
    await serve(http, [
      [COLLECTION_URL, options.collection ?? collection({ schema: SCHEMA })],
      [TEMPLATES_URL, options.templates ?? [template(), BUG]],
      [`${API}/spaces`, [space()]],
      [`${API}/collections`, [collection()]],
    ]);
    if (options.record) {
      await respond(http, 'GET', `${API}/records/${RECORD_ID}?includeBody=true`, options.record);
    }
    if (options.recordMissing) {
      const request = await expectRequest(
        http,
        'GET',
        `${API}/records/${RECORD_ID}?includeBody=true`,
      );
      request.flush({ code: 'not_found', status: 404 }, { status: 404, statusText: 'Not Found' });
    }
    await navigation;
    await settle(harness.fixture);
  }

  afterEach(() => {
    // the sidebar tree reloads after collection events; not part of these checks
    for (const url of [`${API}/spaces`, `${API}/collections`]) {
      for (const request of http.match({ method: 'GET', url })) {
        request.flush([]);
      }
    }
    http.verify();
  });

  const root = () => harness.routeNativeElement as HTMLElement;
  // Dialogs, menus and confirmations render into the CDK overlay container in <body>.
  const q = (id: string) =>
    root().querySelector<HTMLElement>(`[data-testid="${id}"]`) ??
    document.body.querySelector<HTMLElement>(`[data-testid="${id}"]`);
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const rows = () =>
    Array.from(root().querySelectorAll<HTMLElement>('[data-testid="template-row"]'));
  const row = (name: string) =>
    rows().find((r) => text(r.querySelector('[data-testid="template-row-name"]')) === name)!;
  const input = (id: string) => q(id) as HTMLInputElement;
  const presetRow = (key: string) =>
    document.body.querySelector<HTMLElement>(`[data-testid="preset-row"][data-key="${key}"]`)!;
  /** The Helm checkbox renders a `button[role=checkbox]` inside the element with the test id. */
  const box = (id: string) => q(id)!.querySelector<HTMLElement>('button[role="checkbox"]')!;
  const bodyEngine = () => engines.at(-1)!;

  const stable = () => settle(harness.fixture);

  async function set(
    element: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement,
    value: string,
  ) {
    typeInto(element, value);
    await stable();
  }

  async function openMenu(name: string): Promise<void> {
    row(name).querySelector<HTMLElement>('[data-testid="template-actions"]')!.click();
    await stable();
  }

  async function openCreate(): Promise<void> {
    q('template-new')!.click();
    await stable();
  }

  async function openEdit(name: string): Promise<void> {
    await openMenu(name);
    q('template-edit')!.click();
    await stable();
  }

  async function submit(): Promise<void> {
    q('template-submit')!.click();
    await stable();
  }

  /** Waits for the PATCH/POST of the dialog (the fresh comparison GET of an edit comes first). */
  const expectCall = (method: string, url: string): Promise<TestRequest> =>
    expectRequest(http, method, url);

  /** After a change the list is fetched again. */
  async function reload(templates: TemplateDto[]): Promise<void> {
    await respond(http, 'GET', TEMPLATES_URL, templates);
    await stable();
  }

  describe('list', () => {
    it('render_Templates_ShowsNameDefaultBadgePresetCountAndExcerpt', async () => {
      await render();

      expect(rows().map((r) => text(r.querySelector('[data-testid="template-row-name"]')))).toEqual(
        ['ADR', 'Bug'],
      );
      expect(text(q('templates-title'))).toBe('Vorlagen: Aufgaben');
      expect(text(row('ADR').querySelector('[data-testid="default-badge"]'))).toBe('Standard');
      expect(row('Bug').querySelector('[data-testid="default-badge"]')).toBeNull();
      expect(text(row('ADR').querySelector('[data-testid="preset-count"]'))).toBe('2 Felder');
      expect(text(row('ADR').querySelector('[data-testid="template-row-description"]'))).toBe(
        'Für Entscheidungen',
      );
      expect(text(row('ADR').querySelector('[data-testid="template-excerpt"]'))).toBe(
        'Kontext Warum?',
      );
      expect(text(row('Bug').querySelector('[data-testid="preset-count"]'))).toBe('–');
      expect(text(q('breadcrumbs'))).toBe('Produkt›Aufgaben›Vorlagen');
      expect(q('settings-tab-schema')!.getAttribute('href')).toBe(
        `/w/demo/settings/collections/${COLLECTION_ID}/schema`,
      );
      expect(q('settings-tab-templates')!.getAttribute('aria-current')).toBe('page');
    });

    it('render_NoTemplates_ShowsEmptyHint', async () => {
      await render({ templates: [] });

      expect(text(q('templates-empty'))).toContain('Noch keine Vorlagen');
    });

    it('render_WithoutSchemaScope_IsReadOnly', async () => {
      await render({ scopes: ['read', 'write'] });

      expect(rows()).toHaveLength(2);
      expect(text(q('readonly-notice'))).toContain('Recht „schema“');
      expect(q('template-new')).toBeNull();
      expect(root().querySelector('[data-testid="template-actions"]')).toBeNull();
    });

    it('render_ArchivedCollection_IsReadOnly', async () => {
      await render({ collection: collection({ archivedAt: '2026-11-08T10:00:00Z' }) });

      expect(text(q('readonly-notice'))).toContain('Collection ist archiviert');
      expect(q('template-new')).toBeNull();
    });

    it('render_ArchivedWorkspace_IsReadOnly', async () => {
      await render({ archivedWorkspace: true });

      expect(text(q('readonly-notice'))).toContain('Workspace ist archiviert');
      expect(q('template-new')).toBeNull();
    });
  });

  describe('create', () => {
    it('create_NameSelectNumberAndBody_PostsPresetAndBodyAndReloadsList', async () => {
      await render();
      await openCreate();
      await set(input('template-name'), '  Neue Aufgabe ');
      await set(input('template-description'), 'Standardaufgabe');
      await set(presetRow('status').querySelector('select')!, 'done');
      await set(
        presetRow('estimate').querySelector<HTMLInputElement>('[data-testid="editor-input"]')!,
        '5',
      );
      bodyEngine().type('## Ziel\n\nKlar.');
      await submit();

      const post = await expectCall('POST', TEMPLATES_URL);
      expect(post.request.body).toEqual({
        name: 'Neue Aufgabe',
        description: 'Standardaufgabe',
        propertiesPreset: { status: 'done', estimate: 5 },
        bodyMarkdown: '## Ziel\n\nKlar.',
        isDefault: false,
      });
      post.flush(template({ id: 't-new', name: 'Neue Aufgabe', isDefault: false }), {
        status: 201,
        statusText: 'Created',
      });
      await reload([
        template(),
        BUG,
        template({ id: 't-new', name: 'Neue Aufgabe', isDefault: false }),
      ]);

      expect(q('template-dialog')).toBeNull();
      expect(text(q('notice'))).toBe('Vorlage „Neue Aufgabe“ angelegt.');
      expect(rows()).toHaveLength(3);
    });

    it('create_TitlePreset_IsSentInThePreset', async () => {
      await render();
      await openCreate();
      await set(input('template-name'), 'Mit Titel');
      await set(
        presetRow('title').querySelector<HTMLInputElement>('[data-testid="editor-input"]')!,
        'Wochenbericht',
      );
      await submit();

      const post = await expectCall('POST', TEMPLATES_URL);
      expect(post.request.body.propertiesPreset).toEqual({ title: 'Wochenbericht' });
      post.flush(template({ id: 't-new' }), { status: 201, statusText: 'Created' });
      await reload([template(), BUG]);
    });

    it('create_RelationPreset_SendsTheChosenRecordIds', async () => {
      const RELATION = propertyDefinition({
        key: 'affects',
        name: 'Betrifft',
        type: 'relation',
        targetCollectionId: OTHER_COLLECTION_ID,
        multiple: true,
      });
      await render({ collection: collection({ schema: [...SCHEMA, RELATION] }) });
      await openCreate();
      await set(input('template-name'), 'Mit Relation');

      const search = presetRow('affects').querySelector<HTMLInputElement>(
        '[data-testid="relation-search"]',
      )!;
      await set(search, 'login');
      await new Promise((resolve) => setTimeout(resolve, 20)); // search debounce
      const query = await respondPost(
        http,
        `${API}/collections/${OTHER_COLLECTION_ID}/query`,
        queryPage([recordDto({ id: 'r-1', title: 'Login bauen' })]),
      );
      expect(query.request.body.filter).toEqual({ prop: 'title', op: 'contains', value: 'login' });
      await stable();
      presetRow('affects')
        .querySelector<HTMLElement>('[data-testid="relation-suggestion"]')!
        .click();
      await stable();
      expect(text(presetRow('affects'))).toContain('Login bauen');
      expect(presetRow('affects').querySelector('[data-testid="relation-chip-link"]')).toBeNull(); // no navigation out of the editor
      await submit();

      const post = await expectCall('POST', TEMPLATES_URL);
      expect(post.request.body.propertiesPreset).toEqual({ affects: ['r-1'] });
      post.flush(template({ id: 't-new' }), { status: 201, statusText: 'Created' });
      await reload([template(), BUG]);
    });

    it('create_WithoutName_ShowsFieldErrorAndSendsNothing', async () => {
      await render();
      await openCreate();
      await submit();

      expect(text(q('name-errors'))).toBe('Bitte einen Namen angeben.');
      http.expectNone({ method: 'POST', url: TEMPLATES_URL });
    });

    it('create_ComputedPropertyIsNotOfferedButTheTitleIs', async () => {
      await render();
      await openCreate();

      const keys = Array.from(
        document.body.querySelectorAll<HTMLElement>('[data-testid="preset-row"]'),
      ).map((r) => r.dataset['key']);
      expect(keys).toEqual(['title', 'status', 'estimate']);
    });

    it('create_AsDefault_SendsIsDefault', async () => {
      await render();
      await openCreate();
      await set(input('template-name'), 'Neu');
      box('template-default').click();
      await stable();
      await submit();

      const post = await expectCall('POST', TEMPLATES_URL);
      expect(post.request.body.isDefault).toBe(true);
      post.flush(template({ id: 't-new', name: 'Neu' }), { status: 201, statusText: 'Created' });
      await reload([template({ isDefault: false }), BUG, template({ id: 't-new', name: 'Neu' })]);

      expect(
        rows()
          .filter((r) => r.querySelector('[data-testid="default-badge"]'))
          .map((r) => r.dataset['id']),
      ).toEqual(['t-new']);
    });
  });

  describe('edit', () => {
    it('edit_OnlyNameChanged_SendsOnlyTheName', async () => {
      await render();
      await openEdit('ADR');
      expect(input('template-name').value).toBe('ADR');
      await set(input('template-name'), 'ADR neu');
      await submit();

      await respond(http, 'GET', TEMPLATES_URL, [template(), BUG]); // comparison with the server
      const patch = await expectCall('PATCH', `${TEMPLATES_URL}/t-adr`);
      expect(patch.request.body).toEqual({ name: 'ADR neu' });
      patch.flush(template({ name: 'ADR neu' }));
      await reload([template({ name: 'ADR neu' }), BUG]);

      expect(text(q('notice'))).toBe('Vorlage „ADR neu“ gespeichert.');
      expect(row('ADR neu')).toBeDefined();
    });

    it('edit_ClearField_SendsPresetWithoutIt', async () => {
      await render();
      await openEdit('ADR');
      presetRow('estimate').querySelector<HTMLElement>('[data-testid="preset-clear"]')!.click();
      await stable();
      expect(presetRow('estimate').querySelector('[data-testid="preset-clear"]')).toBeNull();
      await submit();

      await respond(http, 'GET', TEMPLATES_URL, [template(), BUG]);
      const patch = await expectCall('PATCH', `${TEMPLATES_URL}/t-adr`);
      expect(patch.request.body).toEqual({ propertiesPreset: { status: 'todo' } });
      patch.flush(template({ propertiesPreset: { status: 'todo' } as never }));
      await reload([template({ propertiesPreset: { status: 'todo' } as never }), BUG]);
    });

    it('edit_NumberInputEmptied_RemovesThePresetEntry', async () => {
      await render();
      await openEdit('ADR');
      await set(
        presetRow('estimate').querySelector<HTMLInputElement>('[data-testid="editor-input"]')!,
        '',
      );
      await submit();

      await respond(http, 'GET', TEMPLATES_URL, [template(), BUG]);
      const patch = await expectCall('PATCH', `${TEMPLATES_URL}/t-adr`);
      expect(patch.request.body).toEqual({ propertiesPreset: { status: 'todo' } });
      patch.flush(template());
      await reload([template(), BUG]);
    });

    it('edit_NothingChanged_ClosesWithoutRequest', async () => {
      await render();
      await openEdit('ADR');
      await submit();

      expect(q('template-dialog')).toBeNull();
      http.expectNone({ method: 'PATCH' });
    });

    it('edit_ChangedOnServerMeanwhile_SavesAndSaysSo', async () => {
      await render();
      await openEdit('ADR');
      await set(input('template-name'), 'ADR neu');
      await submit();

      await respond(http, 'GET', TEMPLATES_URL, [
        template({ description: 'Jemand anders war hier' }),
        BUG,
      ]);
      const patch = await expectCall('PATCH', `${TEMPLATES_URL}/t-adr`);
      expect(patch.request.body).toEqual({ name: 'ADR neu' });
      patch.flush(template({ name: 'ADR neu' }));
      await reload([template({ name: 'ADR neu' }), BUG]);

      expect(text(q('notice'))).toContain('inzwischen von jemand anderem geändert');
    });

    it('edit_FieldErrors_ShowAtNameAndAtThePresetProperty', async () => {
      await render();
      await openEdit('ADR');
      await set(input('template-name'), 'Bug');
      await set(presetRow('status').querySelector('select')!, 'done');
      await submit();

      await respond(http, 'GET', TEMPLATES_URL, [template(), BUG]);
      (await expectCall('PATCH', `${TEMPLATES_URL}/t-adr`)).flush(
        {
          code: 'validation_failed',
          status: 400,
          errors: [
            { field: 'name', code: 'too_long', message: 'long' },
            {
              field: 'propertiesPreset.status',
              code: 'not_in_options',
              message: 'no',
              allowed: ['todo', 'done'],
            },
            { field: 'propertiesPreset.gone', code: 'unknown_property', message: 'unknown' },
          ],
        },
        { status: 400, statusText: 'Bad Request' },
      );
      await stable();

      expect(text(q('name-errors'))).toBe('Zu lang.');
      expect(text(presetRow('status').querySelector('[data-testid="editor-errors"]'))).toBe(
        'Wert ist nicht erlaubt. Erlaubt: todo, done.',
      );
      expect(text(q('form-error'))).toBe('gone: Diese Property gibt es im Schema nicht (mehr).');
      // the input is kept
      expect(input('template-name').value).toBe('Bug');
      expect(q('template-dialog')).not.toBeNull();
    });

    it('edit_NameTaken_ShowsConflictAtTheName', async () => {
      await render();
      await openCreate();
      await set(input('template-name'), 'Bug');
      await submit();

      (await expectCall('POST', TEMPLATES_URL)).flush(
        {
          code: 'template_name_taken',
          status: 409,
          errors: [{ field: 'name', code: 'template_name_taken', message: 'taken' }],
        },
        { status: 409, statusText: 'Conflict' },
      );
      await stable();

      expect(text(q('name-errors'))).toContain('schon eine Vorlage mit diesem Namen');
    });

    it('edit_TemplateDeletedMeanwhile_DisablesSaving', async () => {
      await render();
      await openEdit('ADR');
      realtime.emit(
        activityEvent({
          type: 'collection.schema_changed',
          targetType: 'collection',
          targetId: COLLECTION_ID,
        }),
      );
      await respond(http, 'GET', COLLECTION_URL, collection({ schema: SCHEMA }));
      await respond(http, 'GET', TEMPLATES_URL, [BUG]);
      await stable();

      expect(q('removed-notice')).not.toBeNull();
      expect((q('template-submit') as HTMLButtonElement).disabled).toBe(true);
    });
  });

  describe('discarding', () => {
    it('cancel_Untouched_ClosesWithoutQuestion', async () => {
      await render();
      await openCreate();
      q('template-cancel')!.click();
      await stable();

      expect(q('template-dialog')).toBeNull();
      expect(q('confirm-dialog')).toBeNull();
    });

    it('cancel_ChangedDraft_AsksAndKeepsTheDialogUntilConfirmed', async () => {
      await render();
      await openCreate();
      await set(input('template-name'), 'Entwurf');
      q('template-cancel')!.click();
      await stable();

      expect(text(q('confirm-dialog'))).toContain('Änderungen verwerfen?');
      q('confirm-cancel')!.click();
      await stable();
      expect(input('template-name').value).toBe('Entwurf');

      q('template-cancel')!.click();
      await stable();
      q('confirm-ok')!.click();
      await stable();
      expect(q('template-dialog')).toBeNull();
    });

    it('escape_ChangedDraft_AsksInsteadOfClosing', async () => {
      await render();
      await openEdit('ADR');
      await set(input('template-name'), 'ADR neu');
      q('template-dialog')!.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }),
      );
      await stable();

      expect(q('confirm-dialog')).not.toBeNull();
      expect(q('template-dialog')).not.toBeNull();
    });

    it('navigate_ChangedDraft_AsksBeforeLeavingAndStaysWhenCancelled', async () => {
      await render();
      await openCreate();
      await set(input('template-name'), 'Entwurf');
      const here = TestBed.inject(Router).url;

      const navigation = harness.navigateByUrl(`/w/demo/collections/${COLLECTION_ID}`);
      await vi.waitFor(() => expect(q('confirm-dialog')).not.toBeNull());
      expect(text(q('confirm-dialog'))).toContain('Änderungen verwerfen und verlassen?');

      q('confirm-cancel')!.click();
      await navigation;
      await stable();
      expect(TestBed.inject(Router).url).toBe(here);
      expect(input('template-name').value).toBe('Entwurf');
    });

    it('navigate_UntouchedDialog_LeavesWithoutQuestion', async () => {
      await render();
      await openCreate();

      await harness.navigateByUrl(`/w/demo/collections/${COLLECTION_ID}`);

      expect(TestBed.inject(Router).url).toBe(`/w/demo/collections/${COLLECTION_ID}`);
    });
  });

  describe('body', () => {
    it('body_TooLargeForEditor_IsEditedAsPlainTextAndNothingIsLost', async () => {
      const long = `# Titel\n\n${'Lorem ipsum dolor sit amet. '.repeat(10)}`;
      await render({
        templates: [template({ bodyMarkdown: long }), BUG],
        limits: { ...EDITOR_MARKDOWN_LIMITS, maxLength: 100 },
      });
      await openEdit('ADR');

      const area = q('template-body-text') as HTMLTextAreaElement;
      expect(q('template-body')).toBeNull();
      expect(area.value).toBe(long);
      expect(text(q('body-plain-notice'))).toContain('zu groß oder zu komplex');

      await set(area, `${long}\nErgänzt.`);
      await submit();
      await respond(http, 'GET', TEMPLATES_URL, [template({ bodyMarkdown: long }), BUG]);
      const patch = await expectCall('PATCH', `${TEMPLATES_URL}/t-adr`);
      expect(patch.request.body).toEqual({ bodyMarkdown: `${long}\nErgänzt.` });
      patch.flush(template());
      await reload([template(), BUG]);
    });

    it('body_EngineOverflows_FallsBackToPlainTextWithTheSameText', async () => {
      vi.spyOn(console, 'warn').mockImplementation(() => undefined);
      await render({ engineOverflows: true });
      await openEdit('ADR');

      const area = q('template-body-text') as HTMLTextAreaElement;
      expect(q('template-body')).toBeNull();
      expect(area.value).toBe('# Kontext\n\nWarum?');
      await set(area, 'Neu');
      await submit();

      await respond(http, 'GET', TEMPLATES_URL, [template(), BUG]);
      const patch = await expectCall('PATCH', `${TEMPLATES_URL}/t-adr`);
      expect(patch.request.body).toEqual({ bodyMarkdown: 'Neu' });
      patch.flush(template({ bodyMarkdown: 'Neu' }));
      await reload([template({ bodyMarkdown: 'Neu' }), BUG]);
    });

    it('body_EditorCannotBeLoaded_FallsBackToPlainTextWithTheSameText', async () => {
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      await render({ engineFails: true });
      await openEdit('ADR');

      expect((q('template-body-text') as HTMLTextAreaElement).value).toBe('# Kontext\n\nWarum?');
      expect(q('template-body')).toBeNull();
    });

    it('body_Editor_IsLabelledByItsHeading', async () => {
      await render();
      await openEdit('ADR');

      expect(q('template-body')!.getAttribute('aria-labelledby')).toBe('template-body-label');
      expect(text(document.getElementById('template-body-label'))).toBe('Text (Markdown)');
    });

    it('body_Edited_SendsMarkdownOfTheEditorEvenWhenStillDebounced', async () => {
      await render();
      await openEdit('ADR');
      expect(bodyEngine().current).toBe('# Kontext\n\nWarum?');
      bodyEngine().type('# Neu');
      await submit(); // no wait for the 300 ms debounce

      await respond(http, 'GET', TEMPLATES_URL, [template(), BUG]);
      const patch = await expectCall('PATCH', `${TEMPLATES_URL}/t-adr`);
      expect(patch.request.body).toEqual({ bodyMarkdown: '# Neu' });
      patch.flush(template({ bodyMarkdown: '# Neu' }));
      await reload([template({ bodyMarkdown: '# Neu' }), BUG]);
    });
  });

  describe('default and delete', () => {
    it('makeDefault_Template_PatchesAndMovesTheBadge', async () => {
      await render();
      await openMenu('Bug');
      q('template-make-default')!.click();
      await stable();

      const patch = await expectCall('PATCH', `${TEMPLATES_URL}/t-bug`);
      expect(patch.request.body).toEqual({ isDefault: true });
      patch.flush({ ...BUG, isDefault: true });
      await reload([
        { ...template(), isDefault: false },
        { ...BUG, isDefault: true },
      ]);

      expect(
        rows()
          .filter((r) => r.querySelector('[data-testid="default-badge"]'))
          .map((r) => r.dataset['id']),
      ).toEqual(['t-bug']);
      expect(text(q('notice'))).toBe('„Bug“ ist jetzt die Standardvorlage.');
    });

    it('menu_DefaultTemplate_OffersNoMakeDefault', async () => {
      await render();
      await openMenu('ADR');

      expect(q('template-make-default')).toBeNull();
      expect(q('template-edit')).not.toBeNull();
    });

    it('remove_Cancelled_SendsNothing', async () => {
      await render();
      await openMenu('Bug');
      q('template-remove')!.click();
      await stable();
      expect(text(q('confirm-dialog'))).toContain('Vorlage „Bug“ wird gelöscht');

      q('confirm-cancel')!.click();
      await stable();

      http.expectNone({ method: 'DELETE' });
      expect(rows()).toHaveLength(2);
    });

    it('remove_Confirmed_DeletesAndReloadsList', async () => {
      await render();
      await openMenu('Bug');
      q('template-remove')!.click();
      await stable();
      q('confirm-ok')!.click();
      await stable();

      (await expectCall('DELETE', `${TEMPLATES_URL}/t-bug`)).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      await reload([template()]);

      expect(rows()).toHaveLength(1);
      expect(text(q('notice'))).toBe('Vorlage „Bug“ gelöscht.');
    });

    it('remove_DefaultTemplate_WarnsAboutMissingDefault', async () => {
      await render();
      await openMenu('ADR');
      q('template-remove')!.click();
      await stable();

      expect(text(q('confirm-dialog'))).toContain('keine Standardvorlage mehr');
    });

    it('remove_AlreadyGone_ShowsErrorAndReloads', async () => {
      await render();
      await openMenu('Bug');
      q('template-remove')!.click();
      await stable();
      q('confirm-ok')!.click();
      await stable();

      (await expectCall('DELETE', `${TEMPLATES_URL}/t-bug`)).flush(
        { code: 'not_found', status: 404 },
        { status: 404, statusText: 'Not Found' },
      );
      await reload([template()]);

      expect(text(q('action-error'))).toContain('Nicht gefunden');
      expect(rows()).toHaveLength(1);
    });
  });

  describe('schema cleanup', () => {
    it('edit_PresetCleanedByServerSchemaChange_IsNotReportedAndNotSent', async () => {
      await render();
      await openEdit('ADR');
      const withoutEstimate = collection({ schema: SCHEMA.filter((p) => p.key !== 'estimate') });

      realtime.emit(
        activityEvent({
          type: 'collection.schema_changed',
          targetType: 'collection',
          targetId: COLLECTION_ID,
        }),
      );
      await respond(http, 'GET', COLLECTION_URL, withoutEstimate);
      const cleaned = template({ propertiesPreset: { status: 'todo' } as never });
      await respond(http, 'GET', TEMPLATES_URL, [cleaned, BUG]);
      await stable();
      expect(q('changed-notice')).toBeNull();

      await set(input('template-name'), 'ADR neu');
      await submit();
      await respond(http, 'GET', TEMPLATES_URL, [cleaned, BUG]);
      const patch = await expectCall('PATCH', `${TEMPLATES_URL}/t-adr`);
      expect(patch.request.body).toEqual({ name: 'ADR neu' });
      patch.flush({ ...cleaned, name: 'ADR neu' });
      await reload([{ ...cleaned, name: 'ADR neu' }, BUG]);

      expect(text(q('notice'))).toBe('Vorlage „ADR neu“ gespeichert.');
    });
  });

  describe('live', () => {
    it('live_SchemaChanged_ReloadsListAndKeepsOpenDraft', async () => {
      await render();
      await openCreate();
      await set(input('template-name'), 'Mein Entwurf');
      await set(presetRow('status').querySelector('select')!, 'done');

      realtime.emit(
        activityEvent({
          type: 'collection.schema_changed',
          targetType: 'collection',
          targetId: COLLECTION_ID,
        }),
      );
      await respond(
        http,
        'GET',
        COLLECTION_URL,
        collection({
          schema: [...SCHEMA, propertyDefinition({ key: 'notes', name: 'Notizen', type: 'text' })],
        }),
      );
      await respond(http, 'GET', TEMPLATES_URL, [BUG]);
      await stable();

      expect(rows()).toHaveLength(1);
      expect(presetRow('notes')).toBeDefined();
      expect(input('template-name').value).toBe('Mein Entwurf');
      expect(presetRow('status').querySelector('select')!.value).toBe('done');
    });

    it('live_OtherCollectionEvent_IsIgnored', async () => {
      await render();

      realtime.emit(
        activityEvent({
          type: 'collection.schema_changed',
          targetType: 'collection',
          targetId: OTHER_COLLECTION_ID,
        }),
      );
      await new Promise((resolve) => setTimeout(resolve, 40));

      http.expectNone({ method: 'GET', url: TEMPLATES_URL });
    });
  });

  describe('from a record', () => {
    it('fromRecord_Record_OpensDraftWithValuesWithoutComputedAndBody', async () => {
      await render({
        query: `?fromRecord=${RECORD_ID}`,
        record: recordDto({
          title: 'Login bauen',
          body: '## Schritte\n\n1. Los',
          properties: { status: 'done', estimate: 8, created: '2026-11-07T10:00:00Z' } as never,
        }),
      });

      expect(input('template-name').value).toBe('Login bauen');
      expect(presetRow('status').querySelector('select')!.value).toBe('done');
      expect(
        presetRow('estimate').querySelector<HTMLInputElement>('[data-testid="editor-input"]')!
          .value,
      ).toBe('8');
      expect(presetRow('created')).toBeNull();
      expect(bodyEngine().current).toBe('## Schritte\n\n1. Los');

      await submit();
      const post = await expectCall('POST', TEMPLATES_URL);
      expect(post.request.body).toEqual({
        name: 'Login bauen',
        propertiesPreset: { status: 'done', estimate: 8 },
        bodyMarkdown: '## Schritte\n\n1. Los',
        isDefault: false,
      });
      post.flush(template({ id: 't-new', name: 'Login bauen', isDefault: false }), {
        status: 201,
        statusText: 'Created',
      });
      await reload([template(), BUG]);

      expect(TestBed.inject(Router).url).toBe(
        `/w/demo/settings/collections/${COLLECTION_ID}/templates`,
      );
    });

    it('fromRecord_Cancelled_DropsTheQueryParameter', async () => {
      await render({ query: `?fromRecord=${RECORD_ID}`, record: recordDto() });

      q('template-cancel')!.click();
      await stable();

      expect(q('template-dialog')).toBeNull();
      expect(TestBed.inject(Router).url).not.toContain('fromRecord');
    });

    it('fromRecord_RecordOfAnotherCollection_ShowsErrorInsteadOfDraft', async () => {
      await render({
        query: `?fromRecord=${RECORD_ID}`,
        record: recordDto({ collectionId: OTHER_COLLECTION_ID }),
      });

      expect(q('template-dialog')).toBeNull();
      expect(text(q('action-error'))).toContain('anderen Collection');
    });

    it('fromRecord_UnknownRecord_ShowsErrorAndDropsTheQueryParameter', async () => {
      await render({ query: `?fromRecord=${RECORD_ID}`, recordMissing: true });
      await stable();

      expect(q('template-dialog')).toBeNull();
      expect(text(q('action-error'))).toContain('Nicht gefunden');
      expect(TestBed.inject(Router).url).not.toContain('fromRecord');
    });

    it('fromRecord_LargeRecordBody_WarnsEarlyAndBlocksSaving', async () => {
      await render({
        query: `?fromRecord=${RECORD_ID}`,
        record: recordDto({ title: 'Groß', body: 'a'.repeat(200_001) }),
      });

      expect(text(q('body-length-error'))).toContain('200.001 Zeichen');
      await submit();
      http.expectNone({ method: 'POST', url: TEMPLATES_URL });
    });

    it('fromRecord_ArchivedCollection_OpensNoDraftAndDropsTheQueryParameter', async () => {
      await render({
        collection: collection({ schema: SCHEMA, archivedAt: '2026-11-08T10:00:00Z' }),
        query: `?fromRecord=${RECORD_ID}`,
      });
      await stable();

      expect(q('template-dialog')).toBeNull();
      expect(TestBed.inject(Router).url).not.toContain('fromRecord');
    });

    it('fromRecord_WithoutSchemaScope_OpensNoDraft', async () => {
      await render({ scopes: ['read', 'write'], query: `?fromRecord=${RECORD_ID}` });
      await stable();

      expect(TestBed.inject(Router).url).not.toContain('fromRecord');
      expect(q('template-dialog')).toBeNull();
      expect(q('readonly-notice')).not.toBeNull();
      http.expectNone({ method: 'GET', url: `${API}/records/${RECORD_ID}?includeBody=true` });
    });
  });
});

// keep the shared fixture referenced for readers: the status property drives the preset rows
void STATUS_DEFINITION;
