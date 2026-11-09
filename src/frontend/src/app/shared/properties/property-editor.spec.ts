import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { SessionStore } from '../../core/auth/session.store';
import { provideFakeRealtime } from '../../core/testing/fake-hub';
import { MembersStore } from '../../core/workspace/members.store';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import {
  ADMIN_ME,
  AGENT_ID,
  API,
  COLLECTION_ID,
  RECORD_ID,
  expectRequest,
  fakeSession,
  member,
  propertyDefinition,
  queryPage,
  recordDto,
  respondPost,
} from '../../core/testing/test-utils';
import { accessibleName } from './accessible-name.testing';
import { FakeCodeEditor, provideFakeCodeEditor } from '../raw-view/fake-code-editor';
import { PropertyEditorComponent } from './property-editor.component';
import { PropertyDefinition, PropertyType } from './property-types';
import {
  RELATION_SEARCH_DEBOUNCE_MS,
  RELATION_SEARCH_LIMIT,
  relationSearchQuery,
} from './relation-editor.component';

@Component({
  imports: [PropertyEditorComponent],
  template: `
    <span id="visible-label">Sichtbarer Name</span>
    <ag-property-editor
      [definition]="definition()"
      [value]="value()"
      [readonly]="readonly()"
      [errors]="errors()"
      [labelledBy]="labelledBy()"
      [describedBy]="describedBy()"
      (valueChange)="changes.push($event)"
    />
  `,
})
class HostComponent {
  readonly definition = signal<PropertyDefinition>(propertyDefinition({ key: 'k', type: 'text' }));
  readonly value = signal<unknown>(null);
  readonly readonly = signal(false);
  readonly errors = signal<string[]>([]);
  readonly labelledBy = signal<string | null>(null);
  readonly describedBy = signal<string | null>(null);
  readonly changes: unknown[] = [];
}

const OPTIONS = [
  { value: 'a', label: 'Alpha', color: null, group: null },
  { value: 'b', label: 'Beta', color: null, group: null },
];

describe('PropertyEditorComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let http: HttpTestingController;
  let editors: FakeCodeEditor[];

  beforeEach(() => {
    editors = [];
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        provideFakeRealtime(),
        { provide: SessionStore, useValue: fakeSession(ADMIN_ME) },
        { provide: RELATION_SEARCH_DEBOUNCE_MS, useValue: 10 },
        provideFakeCodeEditor(editors),
        { provide: WorkspaceContext, useValue: { slug: signal('demo') } },
        {
          provide: MembersStore,
          useValue: {
            members: signal([
              member(),
              member({ actorId: AGENT_ID, name: 'Coder', kind: 'agent' }),
            ]),
            nameOf: (id: string) => (id === AGENT_ID ? 'Coder' : 'Admin'),
          },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
  });

  afterEach(() => http.verify());

  const el = () => fixture.nativeElement as HTMLElement;
  const q = <T extends HTMLElement = HTMLElement>(selector: string) =>
    el().querySelector<T>(selector);

  async function render(
    type: PropertyType,
    value: unknown = null,
    extra: Partial<PropertyDefinition> = {},
  ) {
    host.definition.set(propertyDefinition({ key: 'k', name: 'Feld', type, ...extra }));
    host.value.set(value);
    fixture.detectChanges();
    // An open HTTP request keeps the app unstable; relation tests answer it themselves.
    if (type !== 'relation') {
      await fixture.whenStable();
      fixture.detectChanges();
    }
  }

  function type(input: HTMLInputElement | HTMLSelectElement, value: string): void {
    input.value = value;
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
  }

  describe('accessible name', () => {
    const SINGLE = { options: OPTIONS };
    const RELATION = { targetCollectionId: COLLECTION_ID };
    // [type, extra definition, selector of the labelled control]
    const cases: [PropertyType, Partial<PropertyDefinition>, string][] = [
      ['text', {}, 'input[type="text"]'],
      ['number', {}, 'input[type="number"]'],
      ['checkbox', {}, 'input[type="checkbox"]'],
      ['select', SINGLE, 'select[data-testid="editor-select"]'],
      ['status', SINGLE, 'select[data-testid="editor-select"]'],
      ['multi_select', SINGLE, '[data-testid="editor-multi"]'],
      ['date', {}, 'input[type="date"]'],
      ['datetime', {}, 'input[type="datetime-local"]'],
      ['url', {}, 'input[type="url"]'],
      ['email', {}, 'input[type="email"]'],
      ['actor', {}, 'select[data-testid="actor-select"]'],
      ['relation', RELATION, '[data-testid="relation-search"]'],
      ['markdown', {}, 'ag-markdown-editor'],
      ['json', {}, 'ag-raw-view'],
    ];

    it.each(cases)('name_%s_IsThePropertyName', async (propertyType, extra, selector) => {
      await render(propertyType, null, extra);

      expect(accessibleName(q(selector)!)).toBe('Feld');
    });

    it.each(cases)('name_%s_FollowsTheVisibleLabel', async (propertyType, extra, selector) => {
      host.labelledBy.set('visible-label');
      await render(propertyType, null, extra);

      const control = q(selector)!;
      expect(control.getAttribute('aria-labelledby')).toBe('visible-label');
      expect(control.hasAttribute('aria-label')).toBe(false);
      expect(accessibleName(control)).toBe('Sichtbarer Name');
    });

    it('describedBy_HelpTextAndErrors_AreBothReferenced', async () => {
      host.describedBy.set('help-text');
      host.errors.set(['Pflichtfeld.']);
      await render('text');

      const ids = q('input')!.getAttribute('aria-describedby')!.split(' ');
      expect(ids[0]).toBe('help-text');
      expect(ids).toHaveLength(2);
      expect(el().querySelector(`#${ids[1]}`)!.textContent).toContain('Pflichtfeld.');
    });

    it('describedBy_NoHelpNoErrors_IsAbsent', async () => {
      await render('text');

      expect(q('input')!.hasAttribute('aria-describedby')).toBe(false);
    });

    it('ids_TwoEditorsOfTheSameProperty_DoNotShareThem', async () => {
      host.errors.set(['Pflichtfeld.']);
      await render('text');
      const other = TestBed.createComponent(HostComponent);
      other.componentInstance.errors.set(['Pflichtfeld.']);
      other.detectChanges();

      const idOf = (root: Element) => root.querySelector('input')!.getAttribute('aria-describedby');
      expect(idOf(el())).not.toBe(idOf(other.nativeElement));
      expect(el().querySelector(`#${idOf(el())}`)!.textContent).toContain('Pflichtfeld.');
    });
  });

  describe('typed value', () => {
    // [type, definition, value shown, text typed into the control, expected valueChange]
    const cases: [PropertyType, Partial<PropertyDefinition>, unknown, string, unknown][] = [
      ['text', {}, 'alt', 'neu', 'neu'],
      ['text', {}, 'alt', '', null],
      ['number', { precision: 2 }, 3, '4.25', 4.25],
      ['number', {}, 3, '', null],
      ['select', { options: OPTIONS }, 'a', 'b', 'b'],
      ['select', { options: OPTIONS }, 'a', '', null],
      ['status', { options: OPTIONS }, null, 'a', 'a'],
      ['date', {}, '2026-01-01', '2026-11-07', '2026-11-07'],
      ['url', {}, null, 'https://example.org', 'https://example.org'],
      ['email', {}, null, 'a@b.de', 'a@b.de'],
    ];

    it.each(cases)(
      'valueChange_%s_EmitsParsedValue(%j → %j)',
      async (propertyType, extra, value, typed, expected) => {
        await render(propertyType, value, extra);

        const control = q<HTMLInputElement | HTMLSelectElement>(
          '[data-testid="editor-input"], [data-testid="editor-select"]',
        )!;
        type(control, typed);

        expect(host.changes).toEqual([expected]);
      },
    );

    it.each([
      ['text', 'Hallo'],
      ['number', 7],
      ['date', '2026-11-07'],
      ['url', 'https://example.org'],
      ['email', 'a@b.de'],
    ] as [PropertyType, string | number][])(
      'render_%s_ShowsCurrentValueAndDoesNotEmit',
      async (propertyType, value) => {
        await render(propertyType, value);

        expect(q<HTMLInputElement>('[data-testid="editor-input"]')!.value).toBe(String(value));
        expect(host.changes).toEqual([]);
      },
    );
  });

  it('valueChange_Checkbox_EmitsBoolean', async () => {
    await render('checkbox', false);
    const box = q<HTMLInputElement>('[data-testid="editor-input"]')!;

    box.click();

    expect(host.changes).toEqual([true]);
    expect(box.checked).toBe(true);
  });

  it('valueChange_MultiSelect_AddsAndRemovesOptions', async () => {
    await render('multi_select', ['a'], { options: OPTIONS });

    q<HTMLInputElement>('[data-option="b"]')!.click();
    host.value.set(['a', 'b']);
    fixture.detectChanges();
    q<HTMLInputElement>('[data-option="a"]')!.click();
    q<HTMLInputElement>('[data-option="b"]')!.click();

    expect(host.changes).toEqual([['a', 'b'], ['b'], ['a']]);
  });

  it('valueChange_MultiSelectLastOptionRemoved_EmitsNull', async () => {
    await render('multi_select', ['a'], { options: OPTIONS });

    q<HTMLInputElement>('[data-option="a"]')!.click();

    expect(host.changes).toEqual([null]);
  });

  it('valueChange_Datetime_EmitsIsoUtc', async () => {
    await render('datetime', '2026-11-07T10:30:00Z');
    const input = q<HTMLInputElement>('[data-testid="editor-input"]')!;

    type(input, '2026-11-08T09:15');

    expect(host.changes).toEqual([new Date('2026-11-08T09:15').toISOString()]);
  });

  it('render_Datetime_ShowsLocalTime', async () => {
    await render('datetime', '2026-11-07T10:30:00Z');

    const shown = q<HTMLInputElement>('[data-testid="editor-input"]')!.value;
    expect(new Date(shown).toISOString()).toBe('2026-11-07T10:30:00.000Z');
  });

  it('valueChange_Actor_EmitsMemberId', async () => {
    await render('actor', null);
    const select = q<HTMLSelectElement>('[data-testid="actor-select"]')!;

    type(select, AGENT_ID);

    expect(host.changes).toEqual([AGENT_ID]);
    expect(Array.from(select.options).map((o) => o.textContent!.trim())).toEqual([
      '–',
      'Admin (Mensch)',
      'Coder (Agent)',
    ]);
  });

  describe('relation', () => {
    const target = { targetCollectionId: COLLECTION_ID };
    const R2 = '0199a000-0000-7000-8000-0000000000d2';
    const R3 = '0199a000-0000-7000-8000-0000000000d3';
    const QUERY_URL = `${API}/collections/${COLLECTION_ID}/query`;
    const LOOKUP_URL = `${API}/records/lookup`;
    const lookupItem = (id: string, title: string, archived = false) => ({
      id,
      title,
      collectionId: COLLECTION_ID,
      spaceId: 'space',
      archived,
    });

    async function settleAll(): Promise<void> {
      await fixture.whenStable();
      fixture.detectChanges();
    }

    it('search_Title_AsksTheServerAndSelectsOne', async () => {
      await render('relation', null, target);
      await settleAll();

      const search = q<HTMLInputElement>('[data-testid="relation-search"]')!;
      type(search, ' log ');
      const request = await respondPost(
        http,
        QUERY_URL,
        queryPage([
          recordDto({ id: RECORD_ID, title: 'Login bauen' }),
          recordDto({ id: R2, title: 'Logout bauen' }),
        ]),
      );
      await settleAll();

      expect(request.request.body).toEqual(relationSearchQuery('log'));
      expect(request.request.body).toMatchObject({
        filter: { prop: 'title', op: 'contains', value: 'log' },
        sorts: [{ prop: 'created_at', dir: 'desc' }],
        limit: RELATION_SEARCH_LIMIT,
        includeArchived: false,
      });
      const suggestions = Array.from(el().querySelectorAll('[data-testid="relation-suggestion"]'));
      expect(suggestions.map((s) => s.textContent!.trim())).toEqual([
        'Login bauen',
        'Logout bauen',
      ]);
      (suggestions[1] as HTMLElement).click();

      expect(host.changes).toEqual([R2]);
    });

    it('search_RecordBeyondTheFirst200_IsFoundByTitle', async () => {
      // the fake server holds 250 records and answers the title filter like the real one
      const all = Array.from({ length: 250 }, (_, i) =>
        recordDto({
          id: `0199a000-0000-7000-8000-${String(i).padStart(12, '0')}`,
          title: `Aufgabe Nr. ${i + 1}`,
        }),
      );
      await render('relation', null, target);
      await settleAll();

      type(q<HTMLInputElement>('[data-testid="relation-search"]')!, 'Nr. 250');
      const request = await expectRequest(http, 'POST', QUERY_URL);
      const needle = (request.request.body as { filter: { value: string } }).filter.value;
      request.flush(
        queryPage(
          all.filter((r) => r.title.toLowerCase().includes(needle.toLowerCase())).reverse(),
        ),
      );
      await settleAll();

      const suggestions = Array.from(el().querySelectorAll('[data-testid="relation-suggestion"]'));
      expect(suggestions.map((s) => s.textContent!.trim())).toEqual(['Aufgabe Nr. 250']);
      (suggestions[0] as HTMLElement).click();
      expect(host.changes).toEqual([all[249].id]);
    });

    it('search_TypingQuickly_SendsOneRequestForTheLastTerm', async () => {
      await render('relation', null, target);
      await settleAll();
      const search = q<HTMLInputElement>('[data-testid="relation-search"]')!;

      type(search, 'L');
      type(search, 'Lo');
      type(search, 'Log');
      fixture.detectChanges();
      expect(q('[data-testid="relation-searching"]')).not.toBeNull();
      const request = await respondPost(http, QUERY_URL, queryPage([]));
      await settleAll();

      expect((request.request.body as { filter: { value: string } }).filter.value).toBe('Log');
      expect(q('[data-testid="relation-suggestions"]')!.textContent).toContain('Keine Treffer.');
    });

    it('select_MultipleRelation_ChipsShowTitlesAppendAndRemove', async () => {
      await render('relation', [R3], { ...target, multiple: true });
      const lookup = await respondPost(http, LOOKUP_URL, { items: [lookupItem(R3, 'Doku')] });
      await settleAll();

      expect(lookup.request.body).toEqual({ ids: [R3] });
      expect(q('[data-testid="relation-chip"]')!.textContent).toContain('Doku');
      expect(q('[data-testid="relation-chip-link"]')!.getAttribute('href')).toBe(
        `/w/demo/records/${R3}`,
      );
      type(q<HTMLInputElement>('[data-testid="relation-search"]')!, 'login');
      await respondPost(http, QUERY_URL, queryPage([recordDto({ title: 'Login bauen' })]));
      await settleAll();
      q('[data-testid="relation-suggestion"]')!.click();
      expect(q('[data-testid="relation-remove"]')!.getAttribute('aria-label')).toBe(
        'Doku entfernen',
      );
      q('[data-testid="relation-remove"]')!.click();

      expect(host.changes).toEqual([[R3, RECORD_ID], null]);
    });

    it('select_PickedRecord_IsTitledWithoutALookup', async () => {
      await render('relation', null, target);
      await settleAll();

      type(q<HTMLInputElement>('[data-testid="relation-search"]')!, 'login');
      await respondPost(http, QUERY_URL, queryPage([recordDto({ title: 'Login bauen' })]));
      await settleAll();
      q('[data-testid="relation-suggestion"]')!.click();
      host.value.set(host.changes[0]);
      await settleAll();
      await new Promise((resolve) => setTimeout(resolve, 30));

      expect(q('[data-testid="relation-chip"]')!.textContent).toContain('Login bauen');
      http.expectNone(LOOKUP_URL);
    });

    describe('keyboard', () => {
      const search = () => q<HTMLInputElement>('[data-testid="relation-search"]')!;
      const options = () => Array.from(el().querySelectorAll<HTMLElement>('[role="option"]'));
      const key = (name: string) => {
        const event = new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true });
        search().dispatchEvent(event);
        fixture.detectChanges();
        return event;
      };

      async function openList(): Promise<void> {
        await render('relation', null, target);
        await settleAll();
        type(search(), 'log');
        await respondPost(
          http,
          QUERY_URL,
          queryPage([
            recordDto({ id: RECORD_ID, title: 'Login bauen' }),
            recordDto({ id: R2, title: 'Logout bauen' }),
            recordDto({ id: R3, title: 'Logbuch' }),
          ]),
        );
        await settleAll();
      }

      it('combobox_Typing_ExposesComboboxAndListboxSemantics', async () => {
        await render('relation', null, target);
        await settleAll();
        expect(search().getAttribute('role')).toBe('combobox');
        expect(search().getAttribute('aria-autocomplete')).toBe('list');
        expect(search().getAttribute('aria-expanded')).toBe('false');

        type(search(), 'log');
        await respondPost(
          http,
          QUERY_URL,
          queryPage([recordDto({ id: RECORD_ID, title: 'Login bauen' })]),
        );
        await settleAll();

        const listbox = q('[role="listbox"]')!;
        expect(search().getAttribute('aria-expanded')).toBe('true');
        expect(search().getAttribute('aria-controls')).toBe(listbox.id);
        expect(options().map((o) => o.textContent!.trim())).toEqual(['Login bauen']);
        expect(options()[0].getAttribute('aria-selected')).toBe('true');
      });

      it('arrowKeys_List_MoveTheActiveOptionAndWrap', async () => {
        await openList();
        const active = () => search().getAttribute('aria-activedescendant');
        expect(active()).toBe(options()[0].id);

        expect(key('ArrowDown').defaultPrevented).toBe(true);
        expect(active()).toBe(options()[1].id);
        expect(options().map((o) => o.getAttribute('aria-selected'))).toEqual([
          'false',
          'true',
          'false',
        ]);
        key('ArrowDown');
        key('ArrowDown');
        expect(active()).toBe(options()[0].id);
        key('ArrowUp');
        expect(active()).toBe(options()[2].id);
      });

      it('enter_ActiveOption_SelectsItAndClosesTheList', async () => {
        await openList();
        key('ArrowDown');

        const event = key('Enter');

        expect(event.defaultPrevented).toBe(true);
        expect(host.changes).toEqual([R2]);
        expect(q('[role="listbox"]')).toBeNull();
        expect(search().value).toBe('');
      });

      it('escape_OpenList_ClosesItAndStopsThePropagation', async () => {
        await openList();
        const outer = vi.fn();
        el().addEventListener('keydown', outer);

        key('Escape');

        expect(outer).not.toHaveBeenCalled();
        expect(q('[role="listbox"]')).toBeNull();
        expect(search().getAttribute('aria-expanded')).toBe('false');
        expect(search().value).toBe('log');
        expect(host.changes).toEqual([]);

        outer.mockClear();
        key('Escape'); // nothing open any more: the Escape belongs to the surroundings
        expect(outer).toHaveBeenCalledTimes(1);
      });

      it('keydown_ImeComposing_IsLeftToTheInputMethod', async () => {
        await openList();
        for (const name of ['ArrowDown', 'Enter', 'Escape']) {
          const event = new KeyboardEvent('keydown', {
            key: name,
            isComposing: true,
            bubbles: true,
            cancelable: true,
          });
          search().dispatchEvent(event);
          expect(event.defaultPrevented, name).toBe(false);
        }
        fixture.detectChanges();

        expect(host.changes).toEqual([]);
        expect(options()).toHaveLength(3);
        expect(search().getAttribute('aria-activedescendant')).toBe(options()[0].id);
      });

      it.each(['ArrowDown', 'ArrowUp'])('%s_AfterEscape_ReopensTheList', async (name) => {
        await openList();
        key('Escape');
        expect(q('[role="listbox"]')).toBeNull();

        const event = key(name);

        expect(event.defaultPrevented).toBe(true);
        expect(options()).toHaveLength(3);
        expect(search().getAttribute('aria-expanded')).toBe('true');
      });

      it('blur_OpenList_ClosesItAndArrowDownReopensIt', async () => {
        await openList();

        search().dispatchEvent(new FocusEvent('blur'));
        fixture.detectChanges();
        expect(q('[role="listbox"]')).toBeNull();
        expect(search().getAttribute('aria-expanded')).toBe('false');

        key('ArrowDown');
        expect(options()).toHaveLength(3);
      });

      it('mousedown_Option_KeepsTheFocusSoTheClickStillSelects', async () => {
        await openList();
        const down = new MouseEvent('mousedown', { bubbles: true, cancelable: true });

        options()[1].dispatchEvent(down);
        options()[1].click();

        expect(down.defaultPrevented).toBe(true);
        expect(host.changes).toEqual([R2]);
      });

      it('escape_ThenTyping_OpensTheListAgain', async () => {
        await openList();
        key('Escape');

        type(search(), 'logo');
        await respondPost(
          http,
          QUERY_URL,
          queryPage([recordDto({ id: R2, title: 'Logout bauen' })]),
        );
        await settleAll();

        expect(options()).toHaveLength(1);
      });

      it('status_SearchingAndNoHits_AreAnnouncedInALiveRegion', async () => {
        await render('relation', null, target);
        await settleAll();
        type(search(), 'xyz');
        fixture.detectChanges();
        const status = q('[role="status"]')!;
        expect(status.textContent).toContain('Sucht');

        await respondPost(http, QUERY_URL, queryPage([]));
        await settleAll();

        expect(status.textContent).toContain('Keine Treffer.');
        expect(q('[role="listbox"]')).toBeNull();
        expect(search().getAttribute('aria-expanded')).toBe('false');
      });
    });

    it('render_Readonly_ShowsChipsWithoutSearch', async () => {
      host.readonly.set(true);
      await render('relation', R3, target);
      await respondPost(http, LOOKUP_URL, { items: [lookupItem(R3, 'Doku')] });
      await settleAll();

      expect(q('[data-testid="relation-chip"]')!.textContent).toContain('Doku');
      expect(q('[data-testid="relation-search"]')).toBeNull();
      expect(q('[data-testid="relation-remove"]')).toBeNull();
    });
  });

  describe('json', () => {
    it('valueChange_ValidJson_EmitsParsedValue', async () => {
      await render('json', { a: 1 });

      editors[0].type('{"a": 2, "b": [1]}');

      expect(host.changes).toEqual([{ a: 2, b: [1] }]);
    });

    it('valueChange_InvalidJson_EmitsNothing', async () => {
      await render('json', { a: 1 });

      editors[0].type('{"a": ');

      fixture.detectChanges();
      expect(host.changes).toEqual([]);
      expect(q('[data-testid="raw-status"]')!.textContent).toContain('ungültig');
    });

    it('valueChange_EmptyText_EmitsNull', async () => {
      await render('json', { a: 1 });

      editors[0].type('');

      expect(host.changes).toEqual([null]);
    });

    it('render_Value_IsShownFormatted', async () => {
      await render('json', { a: 1 });

      expect(editors[0].getValue()).toBe('{\n  "a": 1\n}');
    });

    it('echoOfTypedValue_DoesNotResetTheText', async () => {
      await render('json', { a: 1 });
      editors[0].type('{"a":2}');

      host.value.set({ a: 2 });
      fixture.detectChanges();

      expect(editors[0].getValue()).toBe('{"a":2}');
      expect(editors[0].setValueCalls).toEqual([]);
    });
  });

  it('valueChange_MarkdownEdited_EmitsDebouncedMarkdown', async () => {
    await render('markdown', 'Hallo');
    q('[data-command="h1"]')!.click();
    expect(host.changes).toEqual([]);

    await new Promise((resolve) => setTimeout(resolve, 400));

    expect(host.changes).toHaveLength(1);
    expect((host.changes[0] as string).trim()).toBe('# Hallo');
  });

  it.each([
    ['number', 'editor-input'],
    ['date', 'editor-input'],
    ['datetime', 'editor-input'],
  ] as [PropertyType, string][])(
    'valueChange_%s_HalfTypedInputIsNotEmittedAsNull',
    async (propertyType) => {
      await render(propertyType, propertyType === 'number' ? 5 : '2026-11-07');
      const input = q<HTMLInputElement>('[data-testid="editor-input"]')!;

      // browsers report '' plus badInput while a segment is cleared or a '-' is typed
      Object.defineProperty(input, 'validity', { value: { badInput: true }, configurable: true });
      type(input, '');
      expect(host.changes).toEqual([]);

      Object.defineProperty(input, 'validity', { value: { badInput: false }, configurable: true });
      type(input, '');
      expect(host.changes).toEqual([null]);
    },
  );

  it('render_Markdown_ShowsTextInEditor', async () => {
    await render('markdown', 'Hallo **Welt**');

    expect(q('.ProseMirror')!.textContent).toBe('Hallo Welt');
  });

  it('render_Markdown_ReadonlyHidesToolbar', async () => {
    host.readonly.set(true);
    await render('markdown', 'Hallo');

    expect(q('[role="toolbar"]')).toBeNull();
  });

  describe('computed types', () => {
    it.each(['created_at', 'updated_at'] as const)('render_%s_ShowsDateWithoutInput', async (t) => {
      await render(t, '2026-11-07T10:30:00Z');

      expect(q('[data-testid="editor-computed"]')!.textContent).toMatch(/07\.11\.2026/);
      expect(q('input')).toBeNull();
    });

    it.each(['created_by', 'updated_by'] as const)('render_%s_ShowsMemberName', async (t) => {
      await render(t, AGENT_ID);

      expect(q('[data-testid="editor-computed"]')!.textContent).toBe('Coder');
    });
  });

  it.each([
    ['text', '[data-testid="editor-input"]'],
    ['number', '[data-testid="editor-input"]'],
    ['select', '[data-testid="editor-select"]'],
    ['actor', '[data-testid="actor-select"]'],
  ] as [PropertyType, string][])('render_Readonly_%s_LocksTheControl', async (t, selector) => {
    host.readonly.set(true);
    await render(t, null, { options: OPTIONS });

    const control = q<HTMLInputElement | HTMLSelectElement>(selector)!;
    expect(
      'disabled' in control && control.disabled ? true : (control as HTMLInputElement).readOnly,
    ).toBe(true);
  });

  it('render_ReadOnlyDefinition_LocksTheControl', async () => {
    await render('text', 'x', { readOnly: true });

    expect(q<HTMLInputElement>('[data-testid="editor-input"]')!.readOnly).toBe(true);
  });

  it('render_Errors_ShowsMessagesBelowTheEditor', async () => {
    host.errors.set(['Pflichtfeld.', 'Zu lang.']);
    await render('text', '');

    const messages = Array.from(el().querySelectorAll('[data-testid="editor-errors"] p')).map((p) =>
      p.textContent!.trim(),
    );
    expect(messages).toEqual(['Pflichtfeld.', 'Zu lang.']);
  });

  it('render_NoErrors_ShowsNoErrorBlock', async () => {
    await render('text', 'ok');

    expect(q('[data-testid="editor-errors"]')).toBeNull();
  });
});
