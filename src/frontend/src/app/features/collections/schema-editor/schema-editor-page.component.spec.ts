import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HttpTestingController, TestRequest } from '@angular/common/http/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import { LIVE_RELOAD_DELAY_MS, LIVE_RELOAD_MAX_WAIT_MS } from '../../../core/realtime/live-reload';
import { FakeRealtime, provideFakeRealtime } from '../../../core/testing/fake-hub';
import { MembersStore } from '../../../core/workspace/members.store';
import {
  API,
  COLLECTION_ID,
  Collection,
  PropertyDefinition,
  STATUS_DEFINITION,
  activityEvent,
  collection,
  expectRequest,
  pageProviders,
  propertyDefinition,
  respond,
  serve,
  settle,
  space,
  typeInto,
  workspace,
} from '../../../core/testing/test-utils';
import { SchemaEditorPageComponent } from './schema-editor-page.component';

@Component({ template: 'stub' })
class StubComponent {}

const OTHER_ID = '0199a000-0000-7000-8000-0000000000c2';
const COLLECTION_URL = `${API}/collections/${COLLECTION_ID}`;
const SCHEMA_URL = `${COLLECTION_URL}/schema`;
const QUERY_URL = `${COLLECTION_URL}/query`;

const SELECT = propertyDefinition({
  key: 'priority',
  name: 'Priorität',
  type: 'select',
  options: [
    { value: 'high', label: 'Hoch', color: 'red', group: null },
    { value: 'low', label: 'Niedrig', color: null, group: null },
  ],
});
const RELATION = propertyDefinition({
  key: 'affects',
  name: 'Betrifft',
  type: 'relation',
  targetCollectionId: OTHER_ID,
  multiple: true,
});
const NOTES = propertyDefinition({ key: 'notes', name: 'Notizen', type: 'markdown' });

const MEMBERS_STUB = {
  members: signal([]),
  nameOf: (id: string | null | undefined) => id ?? '',
  kindOf: () => null,
};

function schemaOf(...extra: PropertyDefinition[]): PropertyDefinition[] {
  return [...collection().schema, ...extra];
}

function schemaDto(properties: PropertyDefinition[], version: number) {
  return {
    collectionId: COLLECTION_ID,
    schemaVersion: version,
    titlePropertyKey: 'title',
    properties,
    jsonSchema: {},
  };
}

describe('SchemaEditorPageComponent', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;
  let realtime: FakeRealtime;

  async function render(
    options: { scopes?: string[]; collection?: Collection; archivedWorkspace?: boolean } = {},
  ): Promise<void> {
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
                  path: 'settings/collections/:collectionId/schema',
                  component: SchemaEditorPageComponent,
                },
                { path: 'collections/:collectionId', component: StubComponent },
                { path: 'spaces/:spaceId', component: StubComponent },
              ],
            },
          ],
        }),
        provideFakeRealtime(realtime),
        // the default editor (shared property editor) knows members; not part of these checks
        { provide: MembersStore, useValue: MEMBERS_STUB },
        { provide: LIVE_RELOAD_DELAY_MS, useValue: 5 },
        { provide: LIVE_RELOAD_MAX_WAIT_MS, useValue: 20 },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl(
      `/w/demo/settings/collections/${COLLECTION_ID}/schema`,
    );
    await serve(http, [
      [COLLECTION_URL, options.collection ?? collection({ schema: schemaOf(SELECT, RELATION) })],
      [`${API}/spaces`, [space()]],
      [
        `${API}/collections`,
        [
          collection(),
          collection({ id: OTHER_ID, name: 'Komponenten' }),
          collection({ id: 'c-archived', name: 'Alt', archivedAt: '2026-11-01T00:00:00Z' }),
        ],
      ],
    ]);
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
  const all = (id: string) =>
    Array.from(document.body.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`));
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const rows = () =>
    Array.from(root().querySelectorAll<HTMLElement>('[data-testid="property-row"]'));
  const row = (key: string) => root().querySelector<HTMLElement>(`[data-key="${key}"]`)!;
  const input = (id: string) => q(id) as HTMLInputElement;
  /** The native `<select>` inside an `hlm-native-select` carrying the test id. */
  const select = (id: string) => q(id)!.querySelector('select')!;
  /** The Helm checkbox renders a `button[role=checkbox]` inside the element with the test id. */
  const box = (id: string) => q(id)!.querySelector<HTMLElement>('button[role="checkbox"]')!;

  async function stable(): Promise<void> {
    await settle(harness.fixture);
  }

  async function openCreate(): Promise<void> {
    q('property-new')!.click();
    await stable();
  }

  async function openEdit(key: string): Promise<void> {
    row(key).querySelector<HTMLElement>('[data-testid="property-actions"]')!.click();
    await stable();
    q('property-edit')!.click();
    await stable();
  }

  async function set(
    element: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement,
    value: string,
  ) {
    typeInto(element, value);
    await stable();
  }

  async function submit(): Promise<void> {
    q('property-submit')!.click();
    await stable();
  }

  async function expectPatch(): Promise<TestRequest> {
    return expectRequest(http, 'PATCH', SCHEMA_URL);
  }

  describe('list', () => {
    it('render_WithSchemaScope_ListsPropertiesInSchemaOrderWithDetails', async () => {
      await render();

      expect(rows().map((r) => r.dataset['key'])).toEqual([
        'title',
        'status',
        'estimate',
        'priority',
        'affects',
      ]);
      expect(text(q('schema-title'))).toBe('Schema: Aufgaben');
      expect(text(q('breadcrumbs'))).toBe('Produkt›Aufgaben›Schema');
      expect(text(row('priority').querySelector('[data-testid="property-type-label"]'))).toBe(
        'Auswahl',
      );
      expect(text(row('priority'))).toContain('Hoch');
      expect(text(row('affects'))).toContain('→ Komponenten (mehrere)');
      expect(text(row('title'))).toContain('Ja');
      expect(q('property-new')).not.toBeNull();
      expect(q('readonly-notice')).toBeNull();
    });

    it('menu_Title_OffersNoRemoval', async () => {
      await render();

      const actions = row('title').querySelector<HTMLElement>('[data-testid="property-actions"]')!;
      expect(actions.getAttribute('aria-label')).toBe('Aktionen für Titel');
      actions.click();
      await stable();

      expect(q('property-edit')).not.toBeNull();
      expect(q('property-remove')).toBeNull();
    });

    it('render_WithoutSchemaScope_IsReadOnly', async () => {
      await render({ scopes: ['read', 'write'] });

      expect(rows()).toHaveLength(5);
      expect(text(q('readonly-notice'))).toContain('Recht „schema“');
      expect(q('property-new')).toBeNull();
      expect(root().querySelector('[data-testid="property-actions"]')).toBeNull();
    });

    it('render_ArchivedCollection_IsReadOnly', async () => {
      await render({ collection: collection({ archivedAt: '2026-11-08T10:00:00Z' }) });

      expect(text(q('readonly-notice'))).toContain('Collection ist archiviert');
      expect(q('property-new')).toBeNull();
    });

    it('render_ArchivedWorkspace_IsReadOnly', async () => {
      await render({ archivedWorkspace: true });

      expect(text(q('readonly-notice'))).toContain('Workspace ist archiviert');
      expect(q('property-new')).toBeNull();
    });

    it('render_UnknownCollection_ShowsLoadError', async () => {
      realtime = new FakeRealtime();
      TestBed.configureTestingModule({
        providers: [
          ...pageProviders({
            routes: [
              {
                path: 'w/:workspaceSlug/settings/collections/:collectionId/schema',
                component: SchemaEditorPageComponent,
              },
            ],
          }),
          provideFakeRealtime(realtime),
        ],
      });
      http = TestBed.inject(HttpTestingController);
      harness = await RouterTestingHarness.create();
      const navigation = harness.navigateByUrl(`/w/demo/settings/collections/x/schema`);
      await respond(
        http,
        'GET',
        `${API}/collections/x`,
        { code: 'not_found', status: 404 },
        { status: 404, statusText: 'Not Found' },
      );
      await navigation;
      for (const request of http.match(() => true)) {
        request.flush([]);
      }
      await stable();

      expect(text(q('load-error'))).toContain('Nicht gefunden');
    });
  });

  describe('create', () => {
    it('create_Text_SuggestsKeyFromNameAndAddsWithVersion', async () => {
      await render();
      await openCreate();

      await set(input('property-name'), 'Fällig am');
      expect(input('property-key').value).toBe('faellig_am');
      await set(q('property-description') as HTMLTextAreaElement, 'Bis wann es fertig sein muss.');
      box('property-required').click();
      await stable();
      await submit();

      const patch = await expectPatch();
      expect(patch.request.body).toEqual({
        add: [
          {
            key: 'faellig_am',
            name: 'Fällig am',
            type: 'text',
            required: true,
            description: 'Bis wann es fertig sein muss.',
          },
        ],
        expectedSchemaVersion: 1,
      });
      patch.flush(
        schemaDto(
          schemaOf(
            SELECT,
            RELATION,
            propertyDefinition({ key: 'faellig_am', name: 'Fällig am', type: 'text' }),
          ),
          2,
        ),
      );
      await stable();

      expect(q('property-dialog')).toBeNull();
      expect(row('faellig_am')).not.toBeNull();
      expect(text(q('notice'))).toBe('Property „Fällig am“ hinzugefügt.');
    });

    it('create_OwnKey_IsNotOverwrittenByTheSuggestion', async () => {
      await render();
      await openCreate();

      await set(input('property-name'), 'Fällig');
      await set(input('property-key'), 'due');
      await set(input('property-name'), 'Fällig am');

      expect(input('property-key').value).toBe('due');
      q('property-cancel')!.click();
      await stable();
    });

    it.each([
      ['text'],
      ['markdown'],
      ['number'],
      ['checkbox'],
      ['date'],
      ['datetime'],
      ['url'],
      ['email'],
      ['actor'],
      ['json'],
      ['created_at'],
      ['updated_at'],
      ['created_by'],
      ['updated_by'],
    ])('create_Type_%s_SendsThatType', async (type) => {
      await render();
      await openCreate();

      await set(input('property-name'), 'Neu');
      await set(select('property-type'), type);
      await submit();

      const patch = await expectPatch();
      expect(patch.request.body.add).toEqual([{ key: 'neu', name: 'Neu', type, required: false }]);
      patch.flush(
        schemaDto(
          schemaOf(propertyDefinition({ key: 'neu', name: 'Neu', type: type as never })),
          2,
        ),
      );
      await stable();
      expect(row('neu')).not.toBeNull();
    });

    it('create_SelectAndStatus_SendsOptionsWithColorsAndGroups', async () => {
      await render();
      await openCreate();

      await set(input('property-name'), 'Phase');
      await set(select('property-type'), 'status');
      q('option-add')!.click();
      await stable();
      expect(document.activeElement).toBe(all('option-value')[0]);
      q('option-add')!.click();
      await stable();
      const [first, second] = all('option-value') as HTMLInputElement[];
      await set(first, 'open');
      await set(all('option-label')[0] as HTMLInputElement, 'Offen');
      await set(all('option-color')[0].querySelector('select')!, 'blue');
      await set(second, 'done');
      await set(all('option-group')[1].querySelector('select')!, 'done');
      await submit();

      const patch = await expectPatch();
      expect(patch.request.body.add[0]).toEqual({
        key: 'phase',
        name: 'Phase',
        type: 'status',
        required: false,
        options: [
          { value: 'open', label: 'Offen', color: 'blue', group: 'todo' },
          { value: 'done', label: 'done', color: null, group: 'done' },
        ],
      });
      patch.flush(schemaDto(schemaOf(), 2));
      await stable();
    });

    it('create_MultiSelectWithDefault_SendsDefaultFromSharedEditor', async () => {
      await render();
      await openCreate();

      await set(input('property-name'), 'Tags');
      await set(select('property-type'), 'multi_select');
      q('option-add')!.click();
      await stable();
      await set(all('option-value')[0] as HTMLInputElement, 'ui');
      const checkbox = q('default-editor')!.querySelector<HTMLInputElement>('[data-option="ui"]')!;
      checkbox.click();
      await stable();
      await submit();

      const patch = await expectPatch();
      expect(patch.request.body.add[0]).toEqual({
        key: 'tags',
        name: 'Tags',
        type: 'multi_select',
        required: false,
        options: [{ value: 'ui', label: 'ui', color: null, group: null }],
        default: ['ui'],
      });
      patch.flush(schemaDto(schemaOf(), 2));
      await stable();
    });

    it('create_Number_SendsUnitAndPrecision', async () => {
      await render();
      await openCreate();

      await set(input('property-name'), 'Aufwand');
      await set(select('property-type'), 'number');
      await set(input('property-unit'), 'h');
      await set(input('property-precision'), '1');
      await submit();

      const patch = await expectPatch();
      expect(patch.request.body.add[0]).toEqual({
        key: 'aufwand',
        name: 'Aufwand',
        type: 'number',
        required: false,
        unit: 'h',
        precision: 1,
      });
      patch.flush(schemaDto(schemaOf(), 2));
      await stable();
    });

    it('create_RelationTargets_OwnCollectionFirstArchivedHidden', async () => {
      await render();
      await openCreate();

      await set(input('property-name'), 'Ersetzt');
      await set(select('property-type'), 'relation');
      const options = Array.from(select('property-target').options).map((o) => [o.value, text(o)]);

      expect(options).toEqual([
        ['', 'Bitte wählen …'],
        [COLLECTION_ID, 'Diese Collection (Aufgaben)'],
        [OTHER_ID, 'Komponenten'],
      ]);
      q('property-cancel')!.click();
      await stable();
    });

    it('create_RelationToOwnCollectionMultiple_SendsTargetAndMultiple', async () => {
      await render();
      await openCreate();

      await set(input('property-name'), 'Ersetzt');
      await set(select('property-type'), 'relation');
      expect(q('default-field')).toBeNull();
      await set(select('property-target'), COLLECTION_ID);
      box('property-multiple').click();
      await stable();
      await submit();

      const patch = await expectPatch();
      expect(patch.request.body.add[0]).toEqual({
        key: 'ersetzt',
        name: 'Ersetzt',
        type: 'relation',
        required: false,
        targetCollectionId: COLLECTION_ID,
        multiple: true,
      });
      patch.flush(schemaDto(schemaOf(), 2));
      await stable();
    });

    it('create_RelationToOtherCollection_SendsSingleReference', async () => {
      await render();
      await openCreate();

      await set(input('property-name'), 'Komponente');
      await set(select('property-type'), 'relation');
      await set(select('property-target'), OTHER_ID);
      await submit();

      const patch = await expectPatch();
      expect(patch.request.body.add[0]).toMatchObject({
        type: 'relation',
        targetCollectionId: OTHER_ID,
        multiple: false,
      });
      patch.flush(schemaDto(schemaOf(), 2));
      await stable();
    });

    it('create_InvalidInput_ShowsFieldErrorsWithoutRequest', async () => {
      await render();
      await openCreate();

      await set(input('property-name'), 'Body');
      await set(input('property-key'), 'body');
      await submit();
      expect(text(q('key-errors'))).toContain('reserviert');

      await set(input('property-key'), 'Status');
      expect(text(q('key-errors'))).toContain('Nur Kleinbuchstaben');

      await set(input('property-key'), 'status');
      expect(text(q('key-errors'))).toBe('Diesen Key gibt es in der Collection schon.');

      await set(input('property-key'), 'ok');
      await set(select('property-type'), 'relation');
      await submit();
      expect(text(q('target-errors'))).toBe('Bitte die Ziel-Collection wählen.');

      await set(select('property-type'), 'select');
      box('property-required').click();
      await stable();
      await submit();
      expect(text(q('options-errors'))).toContain('mindestens eine Option');

      http.expectNone(SCHEMA_URL);
      q('property-cancel')!.click();
      await stable();
    });

    it('create_ServerErrors_ShowAtTheirFields', async () => {
      await render();
      await openCreate();

      await set(input('property-name'), 'Ziel');
      await set(select('property-type'), 'relation');
      await set(select('property-target'), OTHER_ID);
      await submit();
      (await expectPatch()).flush(
        {
          code: 'validation_failed',
          status: 400,
          errors: [
            { field: 'add[0].key', code: 'reserved', message: 'reserved' },
            {
              field: 'add[0].targetCollectionId',
              code: 'target_archived',
              message: 'archived',
            },
            { field: 'add[0].multiple', code: 'not_allowed', message: 'no' },
            { field: 'remove', code: 'whatever', message: 'Something else.' },
          ],
        },
        { status: 400, statusText: 'Bad Request' },
      );
      await stable();

      expect(text(q('key-errors'))).toContain('reserviert');
      expect(text(q('target-errors'))).toContain('Ziel-Collection ist archiviert');
      expect(text(q('multiple-errors'))).toBe('Nicht erlaubt.');
      expect(text(q('form-error'))).toBe('Something else.');
      expect(q('property-dialog')).not.toBeNull();
      q('property-cancel')!.click();
      await stable();
    });

    it('create_ServerErrorOnOption_ShowsAtTheOption', async () => {
      await render();
      await openCreate();

      await set(input('property-name'), 'Prio');
      await set(select('property-type'), 'select');
      q('option-add')!.click();
      await stable();
      q('option-add')!.click();
      await stable();
      await set(all('option-value')[0] as HTMLInputElement, 'a');
      await set(all('option-value')[1] as HTMLInputElement, 'b');
      await submit();
      (await expectPatch()).flush(
        {
          code: 'validation_failed',
          status: 400,
          errors: [
            {
              field: 'add[0].options[1].value',
              code: 'too_long',
              message: 'long',
            },
          ],
        },
        { status: 400, statusText: 'Bad Request' },
      );
      await stable();

      const rowsOfOptions = all('option-row');
      expect(text(rowsOfOptions[0])).not.toContain('Zu lang.');
      expect(text(rowsOfOptions[1])).toContain('Zu lang.');
      q('property-cancel')!.click();
      await stable();
    });
  });

  describe('edit', () => {
    it('edit_Relation_LocksKeyTargetAndMultipleWithExplanation', async () => {
      await render();
      await openEdit('affects');

      expect(input('property-key').disabled).toBe(true);
      expect(text(q('key-locked'))).toContain('Keys sind unveränderlich');
      expect(select('property-target').disabled).toBe(true);
      expect(text(q('target-locked'))).toContain('lässt sich nicht ändern');
      expect(text(q('multiple-locked'))).toContain('nicht wieder auf einen beschränken');
      expect(box('property-multiple').hasAttribute('disabled')).toBe(true);
      expect(select('property-type').disabled).toBe(true);
      expect(text(q('type-locked'))).toContain('Typ lässt sich nicht ändern');
      expect(q('default-field')).toBeNull();
      q('property-cancel')!.click();
      await stable();
    });

    it('edit_Title_LocksTypeAndRequired', async () => {
      await render();
      await openEdit('title');

      expect(select('property-type').disabled).toBe(true);
      expect(text(q('type-locked'))).toBe('Der Titel ist immer vom Typ Text.');
      expect(text(q('required-locked'))).toBe('Der Titel ist immer Pflicht.');
      expect(box('property-required').hasAttribute('disabled')).toBe(true);
      expect(q('default-field')).toBeNull();
      q('property-cancel')!.click();
      await stable();
    });

    it('edit_Select_OffersOnlyAllowedTypeChangeAndLocksStoredValues', async () => {
      await render();
      await openEdit('priority');

      expect(Array.from(select('property-type').options).map((o) => o.value)).toEqual([
        'select',
        'multi_select',
      ]);
      const values = all('option-value') as HTMLInputElement[];
      expect(values.map((v) => v.disabled)).toEqual([true, true]);

      await set(select('property-type'), 'multi_select');
      expect(text(q('type-hint'))).toContain('lässt sich nicht zurücknehmen');
      await submit();

      const patch = await expectPatch();
      expect(patch.request.body).toEqual({
        update: [{ key: 'priority', type: 'multi_select' }],
        expectedSchemaVersion: 1,
      });
      patch.flush(schemaDto(schemaOf({ ...SELECT, type: 'multi_select' }, RELATION), 2));
      await stable();
      expect(text(row('priority').querySelector('[data-testid="property-type-label"]'))).toBe(
        'Mehrfachauswahl',
      );
    });

    it('edit_NameLabelColorAndNewOption_SendsOnlyChanges', async () => {
      await render();
      await openEdit('priority');

      await set(input('property-name'), 'Dringlichkeit');
      await set(all('option-label')[1] as HTMLInputElement, 'Gering');
      await set(all('option-color')[1].querySelector('select')!, 'green');
      q('option-add')!.click();
      await stable();
      await set(all('option-value')[2] as HTMLInputElement, 'mid');
      await submit();

      const patch = await expectPatch();
      expect(patch.request.body).toEqual({
        update: [
          {
            key: 'priority',
            name: 'Dringlichkeit',
            options: [
              { value: 'high', label: 'Hoch', color: 'red', group: null },
              { value: 'low', label: 'Gering', color: 'green', group: null },
              { value: 'mid', label: 'mid', color: null, group: null },
            ],
          },
        ],
        expectedSchemaVersion: 1,
      });
      patch.flush(schemaDto(schemaOf({ ...SELECT, name: 'Dringlichkeit' }, RELATION), 2));
      await stable();
      expect(text(q('notice'))).toBe('Property „Dringlichkeit“ gespeichert.');
    });

    it('edit_Nothing_ClosesWithoutRequest', async () => {
      await render();
      await openEdit('priority');

      await submit();

      http.expectNone(SCHEMA_URL);
      expect(q('property-dialog')).toBeNull();
    });

    it('edit_RemoveOption_ConfirmsWithCountThenSendsConfirmRemoval', async () => {
      await render();
      await openEdit('priority');

      all('option-remove')[1].click();
      await stable();
      await submit();

      const count = await expectRequest(http, 'POST', QUERY_URL);
      expect(count.request.body).toEqual({
        filter: { prop: 'priority', op: 'in', value: ['low'] },
        limit: 1,
        includeArchived: true,
        includeCount: true,
      });
      http.expectNone(SCHEMA_URL);
      count.flush({ items: [], nextCursor: null, count: 3 });
      await stable();
      expect(text(q('confirm-dialog'))).toContain(
        'Die Option „low“ von „Priorität“ wird entfernt. Werte in 3 Records gehen verloren.',
      );

      q('confirm-ok')!.click();
      const patch = await expectPatch();
      expect(patch.request.body).toEqual({
        update: [
          {
            key: 'priority',
            options: [{ value: 'high', label: 'Hoch', color: 'red', group: null }],
          },
        ],
        expectedSchemaVersion: 1,
        confirmRemoval: true,
      });
      patch.flush(schemaDto(schemaOf(SELECT, RELATION), 2));
      await stable();
      await vi.waitFor(() => expect(q('confirm-dialog')).toBeNull());
      expect(q('property-dialog')).toBeNull();
    });

    it('edit_ConfirmationCancelled_SendsNothingAndKeepsDialog', async () => {
      await render();
      await openEdit('priority');

      all('option-remove')[0].click();
      await stable();
      await submit();
      (await expectRequest(http, 'POST', QUERY_URL)).flush({
        items: [],
        nextCursor: null,
        count: 0,
      });
      await stable();
      expect(text(q('confirm-dialog'))).toContain('Kein Record verwendet sie.');

      q('confirm-cancel')!.click();
      await vi.waitFor(() => expect(q('confirm-dialog')).toBeNull());

      http.expectNone(SCHEMA_URL);
      expect(q('property-dialog')).not.toBeNull();
      q('property-cancel')!.click();
      await stable();
    });

    it('edit_MarkdownToText_AsksConfirmation', async () => {
      await render({ collection: collection({ schema: schemaOf(NOTES) }) });
      await openEdit('notes');

      await set(select('property-type'), 'text');
      await submit();
      (await expectRequest(http, 'POST', QUERY_URL)).flush({
        items: [],
        nextCursor: null,
        count: 2,
      });
      await stable();
      expect(text(q('confirm-dialog'))).toContain('von Markdown zu Text');
      expect(text(q('confirm-dialog'))).toContain('Betroffen sind 2 Records.');

      q('confirm-ok')!.click();
      const patch = await expectPatch();
      expect(patch.request.body).toEqual({
        update: [{ key: 'notes', type: 'text' }],
        expectedSchemaVersion: 1,
        confirmRemoval: true,
      });
      patch.flush(schemaDto(schemaOf({ ...NOTES, type: 'text' }), 2));
      await stable();
    });

    it('edit_LowerPrecision_AsksConfirmation', async () => {
      const estimate = propertyDefinition({
        key: 'estimate',
        name: 'Schätzung',
        type: 'number',
        precision: 2,
      });
      await render({
        collection: collection({ schema: [collection().schema[0], STATUS_DEFINITION, estimate] }),
      });
      await openEdit('estimate');

      await set(input('property-precision'), '0');
      await submit();
      (await expectRequest(http, 'POST', QUERY_URL)).flush({
        items: [],
        nextCursor: null,
        count: 4,
      });
      await stable();
      expect(text(q('confirm-dialog'))).toContain('Werte in bis zu 4 Records werden gerundet.');

      q('confirm-cancel')!.click();
      await vi.waitFor(() => expect(q('confirm-dialog')).toBeNull());
      q('property-cancel')!.click();
      await stable();
    });

    it('edit_ServerAsksForConfirmation_OpensConfirmation', async () => {
      await render();
      await openEdit('priority');

      await set(input('property-name'), 'Dringlichkeit');
      await submit();
      (await expectPatch()).flush(
        {
          code: 'validation_failed',
          status: 400,
          errors: [{ field: 'confirmRemoval', code: 'removal_not_confirmed', message: 'x' }],
        },
        { status: 400, statusText: 'Bad Request' },
      );
      await stable();

      expect(text(q('confirm-dialog'))).toContain('entfernt gespeicherte Werte');
      q('confirm-ok')!.click();
      const patch = await expectPatch();
      expect(patch.request.body.confirmRemoval).toBe(true);
      patch.flush(schemaDto(schemaOf(SELECT, RELATION), 2));
      await stable();
    });

    it('edit_VersionConflict_ReloadsKeepsInputAndRetriesWithNewVersion', async () => {
      await render();
      await openEdit('priority');

      await set(input('property-name'), 'Dringlichkeit');
      await submit();
      (await expectPatch()).flush(
        {
          code: 'schema_version_conflict',
          status: 409,
          errors: [{ field: 'expectedSchemaVersion', code: 'conflict', message: 'stale' }],
        },
        { status: 409, statusText: 'Conflict' },
      );
      const changedByOther = { ...SELECT, description: 'Von jemand anderem' };
      await respond(
        http,
        'GET',
        COLLECTION_URL,
        collection({ schemaVersion: 5, schema: schemaOf(changedByOther, RELATION) }),
      );
      await stable();

      expect(text(q('conflict-notice'))).toContain('inzwischen geändert und neu geladen');
      expect(input('property-name').value).toBe('Dringlichkeit');
      expect((q('property-description') as HTMLTextAreaElement).value).toBe('Von jemand anderem');
      expect(q('property-dialog')).not.toBeNull();

      await submit();
      const patch = await expectPatch();
      expect(patch.request.body).toEqual({
        update: [{ key: 'priority', name: 'Dringlichkeit' }],
        expectedSchemaVersion: 5,
      });
      patch.flush(schemaDto(schemaOf({ ...changedByOther, name: 'Dringlichkeit' }, RELATION), 6));
      await stable();
      expect(q('property-dialog')).toBeNull();
    });

    it('edit_ConflictReloadOvertakenByLiveReload_UsesNewestStateWithoutError', async () => {
      await render();
      await openEdit('priority');

      await set(input('property-name'), 'Dringlichkeit');
      await submit();
      (await expectPatch()).flush(
        { code: 'schema_version_conflict', status: 409 },
        { status: 409, statusText: 'Conflict' },
      );
      // the dialog's reload is underway when a live event starts another one
      const first = await expectRequest(http, 'GET', COLLECTION_URL);
      realtime.emit(
        activityEvent({
          type: 'collection.schema_changed',
          targetType: 'collection',
          targetId: COLLECTION_ID,
        }),
      );
      const second = await expectRequest(http, 'GET', COLLECTION_URL);
      first.flush(collection({ schemaVersion: 4, schema: schemaOf(SELECT, RELATION) }));
      second.flush(
        collection({
          schemaVersion: 5,
          schema: schemaOf({ ...SELECT, description: 'Neuester Stand' }, RELATION),
        }),
      );
      await stable();

      expect(q('form-error')).toBeNull();
      expect(q('conflict-notice')).not.toBeNull();
      expect((q('property-description') as HTMLTextAreaElement).value).toBe('Neuester Stand');

      await submit();
      const patch = await expectPatch();
      expect(patch.request.body).toEqual({
        update: [{ key: 'priority', name: 'Dringlichkeit' }],
        expectedSchemaVersion: 5,
      });
      patch.flush(schemaDto(schemaOf(SELECT, RELATION), 6));
      await stable();
    });

    it('edit_ErrorsOnLockedFields_ShowAboveTheForm', async () => {
      await render();
      await openEdit('affects');

      await set(input('property-name'), 'Betrifft Komponenten');
      await submit();
      (await expectPatch()).flush(
        {
          code: 'validation_failed',
          status: 400,
          errors: [
            { field: 'update[0].key', code: 'key_immutable', message: 'immutable' },
            { field: 'update[0].targetCollectionId', code: 'not_allowed', message: 'no' },
          ],
        },
        { status: 400, statusText: 'Bad Request' },
      );
      await stable();

      expect(text(q('form-error'))).toBe(
        'Key: Keys lassen sich nicht ändern. Lege bei Bedarf eine neue Property an. Ziel-Collection: Nicht erlaubt.',
      );
      q('property-cancel')!.click();
      await stable();
    });

    it('edit_OptionChangedWhileRemovedElsewhere_ShowsHintAtOption', async () => {
      await render();
      await openEdit('priority');

      await set(all('option-color')[1].querySelector('select')!, 'green');
      await submit();
      (await expectPatch()).flush(
        { code: 'schema_version_conflict', status: 409 },
        { status: 409, statusText: 'Conflict' },
      );
      const changed = {
        ...SELECT,
        options: [
          { value: 'high', label: 'Sehr hoch', color: 'red', group: null },
          { value: 'mid', label: 'Mittel', color: null, group: null },
        ],
      };
      await respond(
        http,
        'GET',
        COLLECTION_URL,
        collection({ schemaVersion: 3, schema: schemaOf(changed, RELATION) }),
      );
      await stable();

      expect((all('option-value') as HTMLInputElement[]).map((v) => v.value)).toEqual([
        'high',
        'mid',
        'low',
      ]);
      expect((all('option-label')[0] as HTMLInputElement).value).toBe('Sehr hoch');
      const conflicts = all('option-conflict');
      expect(conflicts).toHaveLength(1);
      expect(all('option-row')[2].contains(conflicts[0])).toBe(true);

      await submit();
      const patch = await expectPatch();
      expect(patch.request.body).toEqual({
        update: [
          {
            key: 'priority',
            options: [
              { value: 'high', label: 'Sehr hoch', color: 'red', group: null },
              { value: 'mid', label: 'Mittel', color: null, group: null },
              { value: 'low', label: 'Niedrig', color: 'green', group: null },
            ],
          },
        ],
        expectedSchemaVersion: 3,
      });
      patch.flush(schemaDto(schemaOf(changed, RELATION), 4));
      await stable();
    });

    it('confirm_Escape_ClosesOnlyConfirmationAndReturnsFocus', async () => {
      await render();
      await openEdit('priority');

      all('option-remove')[1].click();
      await stable();
      const submitButton = q('property-submit') as HTMLButtonElement;
      submitButton.focus();
      submitButton.click();
      await stable();
      (await expectRequest(http, 'POST', QUERY_URL)).flush({
        items: [],
        nextCursor: null,
        count: 1,
      });
      await stable();

      q('confirm-dialog')!.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
      );
      await stable();
      await vi.waitFor(() => expect(q('confirm-dialog')).toBeNull());

      expect(q('property-dialog')).not.toBeNull();
      await vi.waitFor(() => expect(document.activeElement).toBe(q('property-submit')));
      http.expectNone(SCHEMA_URL);
      q('property-cancel')!.click();
      await stable();
    });

    it('edit_PropertyRemovedElsewhere_BlocksSaving', async () => {
      await render();
      await openEdit('priority');

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
        collection({ schemaVersion: 2, schema: schemaOf(RELATION) }),
      );
      await stable();

      expect(q('removed-notice')).not.toBeNull();
      expect((q('property-submit') as HTMLButtonElement).disabled).toBe(true);
      q('property-cancel')!.click();
      await stable();
    });
  });

  describe('remove', () => {
    it('remove_Confirmed_CountsThenSendsRemoveWithConfirmation', async () => {
      await render();

      row('priority').querySelector<HTMLElement>('[data-testid="property-actions"]')!.click();
      await stable();
      q('property-remove')!.click();
      await stable();
      const count = await expectRequest(http, 'POST', QUERY_URL);
      expect(count.request.body.filter).toEqual({ not: { prop: 'priority', op: 'is_empty' } });
      count.flush({ items: [], nextCursor: null, count: 7 });
      await stable();
      expect(text(q('confirm-dialog'))).toContain(
        '„Priorität“ wird entfernt. Werte in 7 Records gehen verloren.',
      );

      q('confirm-ok')!.click();
      const patch = await expectPatch();
      expect(patch.request.body).toEqual({
        remove: ['priority'],
        confirmRemoval: true,
        expectedSchemaVersion: 1,
      });
      patch.flush(schemaDto(schemaOf(RELATION), 2));
      await stable();

      expect(root().querySelector('[data-key="priority"]')).toBeNull();
      expect(text(q('notice'))).toBe('Property „Priorität“ entfernt.');
    });

    it('remove_Cancelled_SendsNothing', async () => {
      await render();

      row('priority').querySelector<HTMLElement>('[data-testid="property-actions"]')!.click();
      await stable();
      q('property-remove')!.click();
      await stable();
      (await expectRequest(http, 'POST', QUERY_URL)).flush({
        items: [],
        nextCursor: null,
        count: 0,
      });
      q('confirm-cancel')!.click();
      await vi.waitFor(() => expect(q('confirm-dialog')).toBeNull());

      http.expectNone(SCHEMA_URL);
      expect(row('priority')).not.toBeNull();
    });

    it('remove_VersionConflict_ReloadsAndRemovesNothing', async () => {
      await render();

      row('priority').querySelector<HTMLElement>('[data-testid="property-actions"]')!.click();
      await stable();
      q('property-remove')!.click();
      await stable();
      (await expectRequest(http, 'POST', QUERY_URL)).flush({
        items: [],
        nextCursor: null,
        count: 1,
      });
      q('confirm-ok')!.click();
      (await expectPatch()).flush(
        { code: 'schema_version_conflict', status: 409 },
        { status: 409, statusText: 'Conflict' },
      );
      await respond(
        http,
        'GET',
        COLLECTION_URL,
        collection({ schemaVersion: 3, schema: schemaOf(SELECT, RELATION, NOTES) }),
      );
      await stable();

      expect(text(q('action-error'))).toContain('Nichts wurde entfernt');
      expect(row('notes')).not.toBeNull();
      expect(row('priority')).not.toBeNull();
    });
  });

  describe('live', () => {
    it('live_SchemaChangedByOther_ReloadsListAndKeepsOpenDialogInput', async () => {
      await render();
      await openCreate();
      await set(input('property-name'), 'Meine Property');

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
        collection({ schemaVersion: 2, schema: schemaOf(SELECT, RELATION, NOTES) }),
      );
      await stable();

      expect(row('notes')).not.toBeNull();
      expect(input('property-name').value).toBe('Meine Property');
      expect(input('property-key').value).toBe('meine_property');

      // the dialog still works against the version it was opened with
      await submit();
      const patch = await expectPatch();
      expect(patch.request.body.expectedSchemaVersion).toBe(1);
      patch.flush(
        { code: 'schema_version_conflict', status: 409 },
        { status: 409, statusText: 'Conflict' },
      );
      await respond(
        http,
        'GET',
        COLLECTION_URL,
        collection({ schemaVersion: 2, schema: schemaOf(SELECT, RELATION, NOTES) }),
      );
      await stable();
      expect(q('conflict-notice')).not.toBeNull();

      await submit();
      const retry = await expectPatch();
      expect(retry.request.body.expectedSchemaVersion).toBe(2);
      retry.flush(schemaDto(schemaOf(SELECT, RELATION, NOTES), 3));
      await stable();
    });

    it('live_OtherCollectionEvent_IsIgnored', async () => {
      await render();

      realtime.emit(
        activityEvent({
          type: 'collection.schema_changed',
          targetType: 'collection',
          targetId: OTHER_ID,
        }),
      );
      await new Promise((resolve) => setTimeout(resolve, 40));

      http.expectNone(COLLECTION_URL);
    });
  });
});
