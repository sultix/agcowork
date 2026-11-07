import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import {
  AGENT_ID,
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
  revision,
  revisionSummary,
  serve,
  settle,
  space,
  workspace,
} from '../../../core/testing/test-utils';
import { RecordHistoryComponent } from './record-history.component';

@Component({ template: 'stub' })
class StubComponent {}

const RECORD_URL = `${API}/records/${RECORD_ID}`;
const HISTORY_URL = `${RECORD_URL}/history?limit=30`;
const revisionUrl = (no: number) => `${RECORD_URL}/revisions/${no}`;

const SUMMARIES = [
  revisionSummary(3, { actorId: AGENT_ID, reason: 'Status gesetzt' }),
  revisionSummary(2),
  revisionSummary(1, { kind: 'created' }),
];

describe('RecordHistoryComponent', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;

  async function render(
    options: {
      scopes?: string[];
      record?: Partial<ReturnType<typeof recordDto>>;
      page?: { items: unknown[]; nextCursor: string | null };
      /** Leave the collection request unanswered. */
      holdSchema?: boolean;
    } = {},
  ): Promise<void> {
    TestBed.configureTestingModule({
      providers: pageProviders({
        ws: workspace({ scopes: options.scopes ?? ['read', 'write'] }),
        routes: [
          {
            path: 'w/:workspaceSlug',
            children: [
              { path: 'records/:recordId/history', component: RecordHistoryComponent },
              { path: 'records/:recordId', component: StubComponent },
              { path: 'spaces/:spaceId', component: StubComponent },
              { path: 'collections/:collectionId', component: StubComponent },
            ],
          },
        ],
      }),
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl(`/w/demo/records/${RECORD_ID}/history`);
    await serve(http, [
      [
        RECORD_URL,
        recordDto({
          title: 'Login bauen',
          properties: { status: 'done', estimate: 3, created_at: 'x' } as never,
          revision: 3,
          ...options.record,
        }),
      ],
      [HISTORY_URL, options.page ?? { items: SUMMARIES, nextCursor: null }],
      ...(options.holdSchema
        ? []
        : ([
            [
              `${API}/collections/${COLLECTION_ID}`,
              collection({
                schema: [
                  ...collection().schema,
                  propertyDefinition({ key: 'created_at', type: 'datetime', readOnly: true }),
                ],
              }),
            ],
          ] as const)),
      [`${API}/spaces`, [space()]],
      [`${API}/collections`, [collection()]],
      [`${API}/members`, [member(), member({ actorId: AGENT_ID, name: 'Coder', kind: 'agent' })]],
    ]);
    await navigation;
    if (options.holdSchema) {
      // whenStable would wait for the open collection request
      TestBed.tick();
      await new Promise((resolve) => setTimeout(resolve, 20));
      harness.fixture.detectChanges();
    } else {
      await settle(harness.fixture);
    }
  }

  afterEach(() => http.verify({ ignoreCancelled: true }));

  const root = () => harness.routeNativeElement as HTMLElement;
  const q = <T extends HTMLElement = HTMLElement>(id: string) =>
    root().querySelector<T>(`[data-testid="${id}"]`);
  const all = (id: string, scope: ParentNode = root()) =>
    Array.from(scope.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`));
  const text = (el: Element | null | undefined) =>
    el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const rev = (no: number) => root().querySelector<HTMLElement>(`[data-no="${no}"]`)!;

  async function select(no: number): Promise<void> {
    rev(no).querySelector<HTMLInputElement>('[data-testid="revision-select"]')!.click();
    harness.fixture.detectChanges();
  }

  /** Answers the revision fetches of the current comparison (superseded ones are cancelled). */
  async function answerRevisions(contents: Record<number, ReturnType<typeof revision>>) {
    for (const no of Object.keys(contents).map(Number)) {
      const deadline = Date.now() + 1000;
      for (;;) {
        TestBed.tick();
        const live = http
          .match({ method: 'GET', url: revisionUrl(no) })
          .filter((request) => !request.cancelled);
        if (live.length > 0) {
          live[0].flush(contents[no]);
          break;
        }
        if (Date.now() > deadline) {
          throw new Error(`No pending request for revision ${no}.`);
        }
        await new Promise((resolve) => setTimeout(resolve, 5));
      }
    }
    await settle(harness.fixture);
  }

  describe('list', () => {
    it('render_Revisions_ShowsNumberKindActorTimeAndReason', async () => {
      await render();

      expect(all('revision')).toHaveLength(3);
      const first = rev(3);
      expect(text(first.querySelector('[data-testid="revision-no"]'))).toBe('#3');
      expect(text(first)).toContain('geändert');
      expect(text(first.querySelector('[data-testid="actor-name"]'))).toBe('Coder');
      expect(text(first.querySelector('[data-testid="actor-kind"]'))).toBe('Agent');
      expect(text(first.querySelector('[data-testid="revision-reason"]'))).toBe('„Status gesetzt“');
      expect(text(rev(1))).toContain('angelegt');
      expect(text(q('breadcrumbs'))).toBe('Produkt›Aufgaben›Login bauen');
    });

    it('loadMore_Cursor_AppendsOlderRevisions', async () => {
      await render({ page: { items: SUMMARIES.slice(0, 2), nextCursor: 'c1' } });

      q('load-more')!.click();
      await respond(http, 'GET', `${RECORD_URL}/history?limit=30&cursor=c1`, {
        items: [SUMMARIES[2]],
        nextCursor: null,
      });
      await settle(harness.fixture);

      expect(all('revision')).toHaveLength(3);
      expect(q('load-more')).toBeNull();
    });

    it('render_HistoryError_ShowsMessage', async () => {
      TestBed.configureTestingModule({
        providers: pageProviders({
          routes: [
            {
              path: 'w/:workspaceSlug/records/:recordId/history',
              component: RecordHistoryComponent,
            },
          ],
        }),
      });
      http = TestBed.inject(HttpTestingController);
      harness = await RouterTestingHarness.create();
      const navigation = harness.navigateByUrl(`/w/demo/records/${RECORD_ID}/history`);
      await respond(
        http,
        'GET',
        RECORD_URL,
        { code: 'not_found', status: 404 },
        {
          status: 404,
          statusText: 'Not Found',
        },
      );
      await respond(
        http,
        'GET',
        HISTORY_URL,
        { code: 'not_found', status: 404 },
        {
          status: 404,
          statusText: 'Not Found',
        },
      );
      await serve(http, [[`${API}/members`, []]]);
      await navigation;
      await settle(harness.fixture);

      expect(text(q('load-error'))).toContain('Nicht gefunden');
    });
  });

  describe('diff', () => {
    it('select_TwoRevisions_ShowsPropertyTableTitleAndBodyLines', async () => {
      await render();

      await select(3);
      await select(1); // order of choosing does not matter: older → newer
      await answerRevisions({
        1: revision(1, {
          title: 'Login',
          properties: { status: 'todo', estimate: 3, note: 'weg' } as never,
          body: 'Zeile 1\nZeile 2',
        }),
        3: revision(3, {
          title: 'Login bauen',
          properties: { status: 'done', estimate: 3, owner: 'Mia' } as never,
          body: 'Zeile 1\nZeile 2 neu\nZeile 3',
        }),
      });

      expect(text(q('diff-heading'))).toBe('Änderungen von #1 zu #3');
      expect(text(q('diff-title'))).toBe('Login → Login bauen');
      const rows = all('diff-property');
      expect(rows.map((r) => r.dataset['key'])).toEqual(['note', 'owner', 'status']);
      expect(rows.map((r) => r.dataset['kind'])).toEqual(['removed', 'added', 'changed']);
      expect(text(rows[2].querySelector('[data-testid="diff-old"]'))).toBe('todo');
      expect(text(rows[2].querySelector('[data-testid="diff-new"]'))).toBe('done');
      expect(all('diff-line').map((l) => [l.dataset['kind'], text(l)])).toEqual([
        ['same', 'Zeile 1'],
        ['remove', '- Zeile 2'],
        ['add', '+ Zeile 2 neu'],
        ['add', '+ Zeile 3'],
      ]);
    });

    it('select_OneRevision_ComparesWithItsPredecessor', async () => {
      await render();

      await select(2);
      await answerRevisions({
        1: revision(1, { properties: { status: 'todo' } as never }),
        2: revision(2, { properties: { status: 'done' } as never }),
      });

      expect(text(q('diff-heading'))).toBe('Änderungen von #1 zu #2');
      expect(all('diff-property')).toHaveLength(1);
      expect(text(root().querySelector('[data-testid="diff"]'))).toContain(
        'Der Body ist unverändert.',
      );
    });

    it('select_FirstRevision_ShowsItsContentAsAddition', async () => {
      await render();

      await select(1);
      await answerRevisions({
        1: revision(1, { title: 'Login', properties: { status: 'todo' } as never, body: 'Text' }),
      });

      expect(text(q('diff-heading'))).toBe('Inhalt von #1');
      expect(all('diff-property')).toHaveLength(1);
      expect(all('diff-line').map((l) => l.dataset['kind'])).toEqual(['add']);
    });

    it('select_Third_ReplacesTheOldestSelection', async () => {
      await render();
      await select(1);
      await answerRevisions({ 1: revision(1) });
      await select(2);
      await answerRevisions({ 1: revision(1), 2: revision(2) });
      expect(text(q('diff-heading'))).toBe('Änderungen von #1 zu #2');

      await select(3);
      await answerRevisions({ 2: revision(2), 3: revision(3) });

      expect(text(q('diff-heading'))).toBe('Änderungen von #2 zu #3');
    });
  });

  describe('restore', () => {
    /** Clicks "restore" on a revision and answers the fetch of its content; the dialog opens. */
    async function openRestore(no: number, content: ReturnType<typeof revision>): Promise<void> {
      rev(no).querySelector<HTMLElement>('[data-testid="revision-restore"]')!.click();
      harness.fixture.detectChanges();
      await respond(http, 'GET', revisionUrl(no), content);
      await settle(harness.fixture);
    }

    const collectionDto = () =>
      collection({
        schema: [
          ...collection().schema,
          propertyDefinition({ key: 'created_at', type: 'datetime', readOnly: true }),
        ],
      });

    it('restore_Revision_SendsOldStateWithCurrentExpectedRevisionAndNullsForMissingKeys', async () => {
      await render();

      await openRestore(
        1,
        revision(1, {
          title: 'Login',
          properties: { status: 'todo' } as never,
          body: 'Alter Text',
        }),
      );
      q('confirm-ok')!.click();
      const patch = await expectRequest(http, 'PATCH', RECORD_URL);
      // `estimate` exists now but not in revision 1 → removed; `created_at` is computed → untouched
      expect(patch.request.body).toEqual({
        title: 'Login',
        properties: { status: 'todo', estimate: null },
        body: 'Alter Text',
        expectedRevision: 3,
        reason: 'Revision 1 wiederhergestellt',
      });
      patch.flush(recordDto({ revision: 4 }));
      await respond(http, 'GET', RECORD_URL, recordDto({ revision: 4 }));
      await respond(http, 'GET', HISTORY_URL, {
        items: [revisionSummary(4, { kind: 'restored' }), ...SUMMARIES],
        nextCursor: null,
      });
      await settle(harness.fixture);

      expect(text(q('restore-notice'))).toBe('Revision 1 wiederhergestellt (neue Revision 4).');
      expect(all('revision')).toHaveLength(4);
    });

    it('restore_RevisionWithKeysUnknownToTheSchema_SendsOnlyKnownKeysAndNamesTheRest', async () => {
      await render();

      await openRestore(
        1,
        revision(1, { properties: { status: 'todo', legacy: 'x' } as never, body: 'Alt' }),
      );
      expect(text(q('confirm-dialog'))).toContain('Schema nicht mehr kennt: legacy');
      q('confirm-ok')!.click();

      const patch = await expectRequest(http, 'PATCH', RECORD_URL);
      expect((patch.request.body as { properties: object }).properties).toEqual({
        status: 'todo',
        estimate: null,
      });
      patch.flush(recordDto({ revision: 4 }));
      await respond(http, 'GET', RECORD_URL, recordDto({ revision: 4 }));
      await respond(http, 'GET', HISTORY_URL, { items: SUMMARIES, nextCursor: null });
    });

    it('restore_NoDifference_ReportsNoNewRevision', async () => {
      await render();

      await openRestore(2, revision(2, { properties: { status: 'done', estimate: 3 } as never }));
      q('confirm-ok')!.click();
      (await expectRequest(http, 'PATCH', RECORD_URL)).flush(recordDto({ revision: 3 }));
      await respond(http, 'GET', RECORD_URL, recordDto({ revision: 3 }));
      await respond(http, 'GET', HISTORY_URL, { items: SUMMARIES, nextCursor: null });
      await settle(harness.fixture);

      expect(text(q('restore-notice'))).toContain('keine Änderung');
      expect(text(q('restore-notice'))).not.toContain('neue Revision 4');
    });

    it('restore_Conflict_ShowsErrorAndReloadsRecord', async () => {
      await render();

      await openRestore(2, revision(2));
      q('confirm-ok')!.click();
      (await expectRequest(http, 'PATCH', RECORD_URL)).flush(
        { code: 'revision_mismatch', status: 409, currentRevision: 5 },
        { status: 409, statusText: 'Conflict' },
      );
      await respond(http, 'GET', RECORD_URL, recordDto({ revision: 5 }));
      await settle(harness.fixture);

      expect(text(q('action-error'))).toContain('zwischenzeitlich');
      expect(q('restore-notice')).toBeNull();
    });

    it('restore_Cancelled_SendsNothing', async () => {
      await render();

      await openRestore(1, revision(1));
      q('confirm-cancel')!.click();
      harness.fixture.detectChanges();

      expect(q('confirm-dialog')).toBeNull();
    });

    it('render_SchemaNotLoadedYet_OffersNoRestoreUntilItIs', async () => {
      await render({ holdSchema: true });
      expect(all('revision-restore')).toHaveLength(0);

      await respond(http, 'GET', `${API}/collections/${COLLECTION_ID}`, collectionDto());
      await settle(harness.fixture);

      expect(all('revision-restore')).toHaveLength(2);
    });

    it('render_CurrentRevision_HasNoRestoreButton', async () => {
      await render();

      expect(rev(3).querySelector('[data-testid="revision-restore"]')).toBeNull();
      expect(rev(2).querySelector('[data-testid="revision-restore"]')).not.toBeNull();
    });

    it('render_ViewerWithoutWriteScope_OffersNoRestore', async () => {
      await render({ scopes: ['read'] });

      expect(all('revision-restore')).toHaveLength(0);
      expect(all('revision-select')).toHaveLength(3);
    });

    it('render_ArchivedRecord_OffersNoRestore', async () => {
      await render({ record: { archivedAt: '2026-11-07T12:00:00Z' } });

      expect(all('revision-restore')).toHaveLength(0);
    });
  });
});
