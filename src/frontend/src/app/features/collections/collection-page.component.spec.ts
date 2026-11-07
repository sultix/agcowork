import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { HttpTestingController } from '@angular/common/http/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import {
  ADMIN_ME,
  API,
  COLLECTION_ID,
  RECORD_ID,
  SPACE_ID,
  collection,
  expectRequest,
  member,
  queryPage,
  VIEW_ID,
  viewDto,
  pageProviders,
  recordDto,
  respond,
  respondIfRequested,
  serve,
  settle,
  space,
  typeInto,
  workspace,
} from '../../core/testing/test-utils';
import { CollectionPageComponent } from './collection-page.component';
import { DefaultViewRedirectComponent } from '../views/default-view-redirect.component';
import { ViewPageComponent } from '../views/view-page.component';

@Component({ template: 'stub' })
class StubComponent {}

const TEMPLATES = [
  {
    id: 't1',
    collectionId: COLLECTION_ID,
    name: 'Leer',
    description: null,
    propertiesPreset: {},
    bodyMarkdown: '',
    isDefault: true,
    createdBy: ADMIN_ME.id,
    createdAt: '2026-11-07T10:00:00Z',
  },
  {
    id: 't2',
    collectionId: COLLECTION_ID,
    name: 'Bug',
    description: null,
    propertiesPreset: {},
    bodyMarkdown: '## Schritte',
    isDefault: false,
    createdBy: ADMIN_ME.id,
    createdAt: '2026-11-07T10:00:00Z',
  },
];
const QUERY_URL = `${API}/views/${VIEW_ID}/query`;

function describeBody(templates = TEMPLATES.slice(0, 1), recordCount = 2) {
  return {
    collection: collection(),
    space: space(),
    jsonSchema: {},
    example: {},
    templates,
    statistics: { recordCount },
  };
}

describe('CollectionPageComponent', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;

  async function render(
    options: { scopes?: string[]; templates?: typeof TEMPLATES; records?: unknown } = {},
  ): Promise<void> {
    TestBed.configureTestingModule({
      providers: pageProviders({
        ws: workspace({ scopes: options.scopes ?? ['read', 'write'] }),
        routes: [
          {
            path: 'w/:workspaceSlug',
            children: [
              {
                path: 'collections/:collectionId',
                component: CollectionPageComponent,
                children: [
                  { path: '', component: DefaultViewRedirectComponent },
                  { path: 'views/:viewId', component: ViewPageComponent },
                ],
              },
              { path: 'spaces/:spaceId', component: StubComponent },
              { path: 'records/:recordId', component: StubComponent },
            ],
          },
        ],
      }),
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl(`/w/demo/collections/${COLLECTION_ID}`);
    await serve(http, [
      [`${API}/collections/${COLLECTION_ID}/describe`, describeBody(options.templates)],
      [`${API}/collections/${COLLECTION_ID}/views`, [viewDto()]],
    ]);
    await respond(
      http,
      'POST',
      QUERY_URL,
      options.records ??
        queryPage([
          recordDto({ id: RECORD_ID, title: 'Login bauen' }),
          recordDto({
            id: 'r2',
            title: 'Logout bauen',
            properties: { status: 'done' } as never,
            updatedBy: 'agent-1',
          }),
        ]),
    );
    await serve(http, [
      [`${API}/spaces`, [space()]],
      [`${API}/collections`, [collection()]],
    ]);
    await navigation;
    await respondIfRequested(http, `${API}/members`, [
      member(),
      member({ actorId: 'agent-1', name: 'Coder', kind: 'agent' }),
    ]);
    await settle(harness.fixture);
  }

  afterEach(() => {
    // members are asked for once the table renders cells; not part of these checks
    for (const request of http.match(`${API}/members`)) {
      request.flush([member()]);
    }
    http.verify();
  });

  const root = () => harness.routeNativeElement as HTMLElement;
  // The record create dialog renders into the CDK overlay container in <body>.
  const q = (id: string) =>
    root().querySelector<HTMLElement>(`[data-testid="${id}"]`) ??
    document.body.querySelector<HTMLElement>(`[data-testid="${id}"]`);
  const all = (id: string) =>
    Array.from(root().querySelectorAll<HTMLElement>(`[data-testid="${id}"]`));
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  it('render_Collection_ShowsHeaderSchemaChipsAndOpensDefaultViewAsTable', async () => {
    await render();

    expect(TestBed.inject(Router).url).toBe(
      `/w/demo/collections/${COLLECTION_ID}/views/${VIEW_ID}`,
    );
    expect(text(q('collection-title'))).toBe('Aufgaben');
    expect(text(q('collection-description'))).toBe('Arbeitspakete des Teams');
    expect(text(q('record-count'))).toBe('2 Records');
    expect(all('schema-chips')[0].querySelectorAll('li')).toHaveLength(3);
    expect(text(q('breadcrumbs'))).toBe('Produkt›Aufgaben');
    expect(all('view-tab').map(text)).toEqual(['Tabelle (Standard)']);
    // the default view is marked with a star icon inside its tab
    expect(all('view-tab')[0].querySelector('[data-testid="view-default-mark"]')).not.toBeNull();
    expect(all('table-row')).toHaveLength(2);
    expect(text(all('row-link')[0])).toBe('Login bauen');
    expect(all('row-link')[0].getAttribute('href')).toBe(`/w/demo/records/${RECORD_ID}`);
  });

  it('render_StatusColumn_ShowsOptionLabel', async () => {
    await render();

    const [first, second] = all('table-row');
    expect(text(first.querySelector('[data-key="status"]'))).toBe('Offen');
    expect(text(second.querySelector('[data-key="status"]'))).toBe('Erledigt');
  });

  it('render_NoRecords_ShowsEmptyHint', async () => {
    await render({ records: queryPage([]) });

    expect(text(q('table-empty'))).toBe('Noch keine Records.');
  });

  it('render_Viewer_HidesNewRecordButtonAndViewActions', async () => {
    await render({ scopes: ['read'] });

    expect(q('record-new')).toBeNull();
    expect(q('view-new')).toBeNull();
  });

  describe('new record', () => {
    const templatesUrl = `${API}/collections/${COLLECTION_ID}/templates`;

    /** Clicks "Neuer Record" and answers the dialog's template request. */
    async function openDialog(templates: typeof TEMPLATES): Promise<void> {
      q('record-new')!.click();
      harness.fixture.detectChanges();
      await respond(http, 'GET', templatesUrl, templates);
      await settle(harness.fixture);
    }

    it('create_SingleTemplate_PostsTitleWithoutTemplateChoiceAndOpensRecord', async () => {
      await render();
      await openDialog(TEMPLATES.slice(0, 1));
      expect(q('create-template')).toBeNull();

      typeInto(q('create-title') as HTMLInputElement, 'Neue Aufgabe');
      (q('create-submit') as HTMLButtonElement).click();
      const post = await expectRequest(http, 'POST', `${API}/records`);
      expect(post.request.body).toEqual({
        title: 'Neue Aufgabe',
        collectionId: COLLECTION_ID,
        spaceId: SPACE_ID,
      });
      post.flush(recordDto({ id: 'new', title: 'Neue Aufgabe' }), {
        status: 201,
        statusText: 'Created',
      });

      await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/w/demo/records/new'));
    });

    it('create_SeveralTemplates_OffersChoiceWithDefaultPreselected', async () => {
      await render({ templates: TEMPLATES });
      await openDialog(TEMPLATES);

      const select = q('create-template') as HTMLSelectElement;
      expect(select.value).toBe('t1');
      expect(Array.from(select.options).map((o) => text(o))).toEqual(['Leer (Standard)', 'Bug']);

      typeInto(select, 't2');
      typeInto(q('create-title') as HTMLInputElement, 'Absturz');
      (q('create-submit') as HTMLButtonElement).click();
      const post = await expectRequest(http, 'POST', `${API}/records`);

      expect(post.request.body).toEqual({
        title: 'Absturz',
        collectionId: COLLECTION_ID,
        spaceId: SPACE_ID,
        templateId: 't2',
      });
      post.flush(recordDto({ id: 'new2' }), { status: 201, statusText: 'Created' });
      await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/w/demo/records/new2'));
    });

    it('create_EmptyTitle_ShowsFieldErrorWithoutRequest', async () => {
      await render();
      await openDialog(TEMPLATES.slice(0, 1));

      (q('create-submit') as HTMLButtonElement).click();
      await settle(harness.fixture);

      expect(text(q('title-errors'))).toBe('Bitte einen Titel angeben.');
    });

    it('create_ValidationFailed_ShowsServerFieldError', async () => {
      await render();
      await openDialog(TEMPLATES.slice(0, 1));

      typeInto(q('create-title') as HTMLInputElement, 'x'.repeat(600));
      (q('create-submit') as HTMLButtonElement).click();
      (await expectRequest(http, 'POST', `${API}/records`)).flush(
        {
          code: 'validation_failed',
          status: 400,
          errors: [{ field: 'title', code: 'too_long', message: 'Title too long.' }],
        },
        { status: 400, statusText: 'Bad Request' },
      );
      await settle(harness.fixture);

      expect(text(q('title-errors'))).toBe('Zu lang.');
      expect(q('create-record-dialog')).not.toBeNull();
    });

    it('create_ArchivedWorkspace_ShowsMessage', async () => {
      await render();
      await openDialog(TEMPLATES.slice(0, 1));

      typeInto(q('create-title') as HTMLInputElement, 'x');
      (q('create-submit') as HTMLButtonElement).click();
      (await expectRequest(http, 'POST', `${API}/records`)).flush(
        { code: 'workspace_archived', status: 409 },
        { status: 409, statusText: 'Conflict' },
      );
      await settle(harness.fixture);

      expect(text(q('form-error'))).toContain('archiviert');
    });
  });
});
