import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ApiError } from '../../core/api/api-error';
import { SessionStore } from '../../core/auth/session.store';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { apiErrorInterceptor } from '../../core/api/api-error.interceptor';
import {
  ADMIN_ME,
  expectRequest,
  fakeSession,
  settle,
  workspace,
} from '../../core/testing/test-utils';
import { WorkspaceCreateComponent } from './workspace-create.component';
import { WorkspacesStore } from './workspaces.store';

@Component({ template: 'target' })
class TargetComponent {}

describe('WorkspaceCreateComponent', () => {
  let harness: RouterTestingHarness;
  let store: { create: ReturnType<typeof vi.fn> };
  let http: HttpTestingController;

  const TEMPLATES = [
    { id: 'empty', name: 'Leer', description: 'Nichts drin.', isDefault: false },
    {
      id: 'software-projekt',
      name: 'Software-Projekt',
      description: 'Mit Spaces.',
      isDefault: true,
    },
  ];

  /** Opens the page; `templates` answers `GET /workspace-templates`, `null` makes it fail. */
  beforeEach(() => {
    store = { create: vi.fn().mockResolvedValue(workspace({ slug: 'neu' })) };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'workspaces/new', component: WorkspaceCreateComponent },
          { path: 'workspaces', component: TargetComponent },
          { path: 'w/:slug', component: TargetComponent },
        ]),
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
        { provide: SessionStore, useValue: fakeSession(ADMIN_ME) },
        { provide: WorkspacesStore, useValue: store },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  async function open(templates: typeof TEMPLATES | null = TEMPLATES): Promise<void> {
    harness = await RouterTestingHarness.create('/workspaces/new');
    const request = await expectRequest(http, 'GET', '/api/v1/workspace-templates');
    if (templates) {
      request.flush(templates);
    } else {
      request.flush(null, { status: 500, statusText: 'Server Error' });
    }
    await settle(harness.fixture);
  }

  afterEach(() => http.verify());

  const root = () => harness.routeNativeElement as HTMLElement;
  const text = (id: string) =>
    root().querySelector(`[data-testid="${id}"]`)?.textContent?.trim() ?? '';

  async function type(id: string, value: string): Promise<void> {
    const input = root().querySelector<HTMLInputElement>(`#${id}`)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await harness.fixture.whenStable();
  }

  async function submit(): Promise<void> {
    root().querySelector('form')!.requestSubmit();
    await settle(harness.fixture);
  }

  it('type_Slug_ShowsLivePreview', async () => {
    await open();
    await type('ws-slug', 'mein-projekt');

    expect(text('slug-preview')).toBe('/w/mein-projekt');
  });

  it('submit_Empty_ShowsRequiredErrorsWithoutCallingApi', async () => {
    await open();
    await submit();

    expect(text('name-errors')).toBe('Bitte einen Namen eingeben.');
    expect(text('slug-errors')).toBe('Bitte einen Slug eingeben.');
    expect(store.create).not.toHaveBeenCalled();
  });

  it.each(['Mein Projekt', 'UPPER', '-lead', 'double--hyphen', 'trail-'])(
    'submit_InvalidSlug_%s_ShowsPatternError',
    async (slug) => {
      await open();
      await type('ws-name', 'Name');
      await type('ws-slug', slug);
      await submit();

      expect(text('slug-errors')).toContain('Kleinbuchstaben');
      expect(store.create).not.toHaveBeenCalled();
    },
  );

  it('submit_Valid_CreatesAndNavigatesIntoWorkspace', async () => {
    await open();
    await type('ws-name', ' Neu ');
    await type('ws-slug', 'neu');
    await type('ws-description', 'Beschreibung');
    await submit();

    expect(store.create).toHaveBeenCalledWith({
      slug: 'neu',
      name: 'Neu',
      description: 'Beschreibung',
      templateId: 'software-projekt',
    });
    expect(TestBed.inject(Router).url).toBe('/w/neu');
  });

  it('submit_SlugTaken_ShowsMessageAtSlugField', async () => {
    await open();
    store.create.mockRejectedValue(
      new ApiError(409, 'slug_taken', [{ field: 'slug', code: 'conflict', message: 'taken' }]),
    );
    await type('ws-name', 'Demo');
    await type('ws-slug', 'demo');
    await submit();

    expect(text('slug-errors')).toBe(
      'Dieser Slug ist bereits vergeben. Bitte einen anderen wählen.',
    );
    expect(text('name-errors')).toBe('');
    expect(TestBed.inject(Router).url).toBe('/workspaces/new');
  });

  it('render_Templates_PreselectsDefaultAndShowsDescription', async () => {
    await open();

    const select = root().querySelector<HTMLSelectElement>('#ws-template')!;
    expect(Array.from(select.options).map((o) => o.textContent?.trim())).toEqual([
      'Leer',
      'Software-Projekt',
    ]);
    expect(select.value).toBe('software-projekt');
    expect(text('template-description')).toBe('Mit Spaces.');
  });

  it('submit_OtherTemplateChosen_SendsChosenTemplateId', async () => {
    await open();
    const select = root().querySelector<HTMLSelectElement>('#ws-template')!;
    select.value = 'empty';
    select.dispatchEvent(new Event('input'));
    select.dispatchEvent(new Event('change'));
    await type('ws-name', 'Neu');
    await type('ws-slug', 'neu');
    await submit();

    expect(store.create).toHaveBeenCalledWith(expect.objectContaining({ templateId: 'empty' }));
  });

  it('render_TemplatesFailToLoad_FallsBackToEmptyWithHint', async () => {
    await open(null);

    const select = root().querySelector<HTMLSelectElement>('#ws-template')!;
    expect(Array.from(select.options).map((o) => o.value)).toEqual(['empty']);
    expect(text('template-fallback')).toContain('nicht geladen');
    await type('ws-name', 'Neu');
    await type('ws-slug', 'neu');
    await submit();
    expect(store.create).toHaveBeenCalledWith(expect.objectContaining({ templateId: 'empty' }));
  });

  it('submit_ServerRejectsTemplate_ShowsErrorAtTemplateField', async () => {
    await open();
    store.create.mockRejectedValue(
      new ApiError(400, 'validation_failed', [
        { field: 'templateId', code: 'not_in_options', message: 'm', allowed: ['empty'] },
      ]),
    );
    await type('ws-name', 'Demo');
    await type('ws-slug', 'demo');
    await submit();

    expect(text('template-errors')).toContain('Erlaubt: empty');
  });

  it('submit_NetworkError_ShowsFormError', async () => {
    await open();
    store.create.mockRejectedValue(new ApiError(0, 'network_error'));
    await type('ws-name', 'Demo');
    await type('ws-slug', 'demo');
    await submit();

    expect(text('form-error')).toContain('nicht erreichbar');
  });
});
