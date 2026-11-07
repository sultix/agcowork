import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ApiError } from '../../core/api/api-error';
import { SessionStore } from '../../core/auth/session.store';
import { ADMIN_ME, fakeSession, settle, workspace } from '../../core/testing/test-utils';
import { WorkspaceCreateComponent } from './workspace-create.component';
import { WorkspacesStore } from './workspaces.store';

@Component({ template: 'target' })
class TargetComponent {}

describe('WorkspaceCreateComponent', () => {
  let harness: RouterTestingHarness;
  let store: { create: ReturnType<typeof vi.fn> };

  beforeEach(async () => {
    store = { create: vi.fn().mockResolvedValue(workspace({ slug: 'neu' })) };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'workspaces/new', component: WorkspaceCreateComponent },
          { path: 'workspaces', component: TargetComponent },
          { path: 'w/:slug', component: TargetComponent },
        ]),
        { provide: SessionStore, useValue: fakeSession(ADMIN_ME) },
        { provide: WorkspacesStore, useValue: store },
      ],
    });
    harness = await RouterTestingHarness.create('/workspaces/new');
  });

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
    await type('ws-slug', 'mein-projekt');

    expect(text('slug-preview')).toBe('/w/mein-projekt');
  });

  it('submit_Empty_ShowsRequiredErrorsWithoutCallingApi', async () => {
    await submit();

    expect(text('name-errors')).toBe('Bitte einen Namen eingeben.');
    expect(text('slug-errors')).toBe('Bitte einen Slug eingeben.');
    expect(store.create).not.toHaveBeenCalled();
  });

  it.each(['Mein Projekt', 'UPPER', '-lead', 'double--hyphen', 'trail-'])(
    'submit_InvalidSlug_%s_ShowsPatternError',
    async (slug) => {
      await type('ws-name', 'Name');
      await type('ws-slug', slug);
      await submit();

      expect(text('slug-errors')).toContain('Kleinbuchstaben');
      expect(store.create).not.toHaveBeenCalled();
    },
  );

  it('submit_Valid_CreatesAndNavigatesIntoWorkspace', async () => {
    await type('ws-name', ' Neu ');
    await type('ws-slug', 'neu');
    await type('ws-description', 'Beschreibung');
    await submit();

    expect(store.create).toHaveBeenCalledWith({
      slug: 'neu',
      name: 'Neu',
      description: 'Beschreibung',
      templateId: 'empty',
    });
    expect(TestBed.inject(Router).url).toBe('/w/neu');
  });

  it('submit_SlugTaken_ShowsMessageAtSlugField', async () => {
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

  it('submit_TemplateRejected_OffersAllowedTemplates', async () => {
    store.create.mockRejectedValue(
      new ApiError(400, 'validation_failed', [
        {
          field: 'templateId',
          code: 'not_in_options',
          message: 'm',
          allowed: ['empty', 'software-projekt'],
        },
      ]),
    );
    await type('ws-name', 'Demo');
    await type('ws-slug', 'demo');
    await submit();

    const options = Array.from(root().querySelectorAll('#ws-template option')).map((o) =>
      o.textContent?.trim(),
    );
    expect(options).toEqual(['Leer', 'Software-Projekt']);
  });

  it('submit_NetworkError_ShowsFormError', async () => {
    store.create.mockRejectedValue(new ApiError(0, 'network_error'));
    await type('ws-name', 'Demo');
    await type('ws-slug', 'demo');
    await submit();

    expect(text('form-error')).toContain('nicht erreichbar');
  });
});
