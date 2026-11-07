import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { apiErrorInterceptor } from '../../core/api/api-error.interceptor';
import type { components } from '../../core/api/generated/schema';
import { expectRequest, settle } from '../../core/testing/test-utils';
import { ActorsComponent } from './actors.component';

type Actor = components['schemas']['ActorDto'];

const ADMIN: Actor = {
  id: '0199a000-0000-7000-8000-000000000001',
  kind: 'human',
  name: 'Admin',
  role: 'Betreiber',
  email: 'admin@agcowork.local',
  instanceScopes: ['instance:admin'],
  isActive: true,
  createdAt: '2026-11-07T10:00:00Z',
};
const AGENT: Actor = {
  ...ADMIN,
  id: '0199a000-0000-7000-8000-000000000002',
  kind: 'agent',
  name: 'Coder',
  role: null,
  email: null,
  instanceScopes: [],
};

describe('ActorsComponent', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;

  async function render(actors: Actor[] = [ADMIN, AGENT]): Promise<void> {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'admin/actors', component: ActorsComponent }]),
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl('/admin/actors');
    (await expectRequest(http, 'GET', '/api/v1/actors')).flush(actors);
    await navigation;
    await settle(harness.fixture);
  }

  afterEach(() => http.verify());

  const root = () => harness.routeNativeElement as HTMLElement;
  // Menus render into the CDK overlay container in <body>.
  const q = (id: string) =>
    root().querySelector<HTMLElement>(`[data-testid="${id}"]`) ??
    document.body.querySelector<HTMLElement>(`[data-testid="${id}"]`);
  const rows = () => Array.from(root().querySelectorAll('[data-testid="actor-row"]'));
  const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

  async function fill(id: string, value: string): Promise<void> {
    const el = root().querySelector<HTMLInputElement | HTMLSelectElement>(`#${id}`)!;
    el.value = value;
    el.dispatchEvent(new Event('input'));
    el.dispatchEvent(new Event('change'));
    await harness.fixture.whenStable();
  }

  /** Opens the row action menu (overlay in <body>) of the given row. */
  async function openActions(row: number): Promise<void> {
    rows()[row].querySelector<HTMLElement>('[data-testid="actor-actions"]')!.click();
    await harness.fixture.whenStable();
  }

  /** The Helm checkbox renders a `button[role=checkbox]` inside the element carrying the test id. */
  const box = (id: string) => q(id)!.querySelector<HTMLElement>('button[role="checkbox"]')!;

  async function openCreate(): Promise<void> {
    q('new-actor')!.click();
    await harness.fixture.whenStable();
  }

  async function submitCreate(): Promise<void> {
    q('create-form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    await settle(harness.fixture);
  }

  it('render_Actors_ShowsNameKindRoleEmailScopesAndStatus', async () => {
    await render([ADMIN, { ...AGENT, isActive: false }]);

    const cells = (row: Element) => Array.from(row.querySelectorAll('td')).map((td) => text(td));
    expect(cells(rows()[0]).slice(0, 6)).toEqual([
      'Admin',
      'Mensch',
      'Betreiber',
      'admin@agcowork.local',
      'instance:admin',
      'aktiv',
    ]);
    expect(cells(rows()[1]).slice(0, 2)).toEqual(['Coder', 'Agent']);
    expect(cells(rows()[1])[5]).toBe('inaktiv');
  });

  it('actions_Agent_OffersKeysLinkAndEdit', async () => {
    await render();

    await openActions(1);

    expect(q('actor-menu')!.getAttribute('role')).toBe('menu');
    expect(q('actor-keys')!.getAttribute('href')).toBe(`/admin/actors/${AGENT.id}/keys`);
    expect(q('actor-edit')!.getAttribute('role')).toBe('menuitem');
  });

  it('actions_Human_OffersNoKeysLink', async () => {
    await render();

    await openActions(0);

    expect(q('actor-menu')).not.toBeNull();
    expect(q('actor-keys')).toBeNull();
  });

  it('actions_Trigger_LabelContainsActorName', async () => {
    await render();

    const labels = rows().map((r) =>
      r.querySelector('[data-testid="actor-actions"]')?.getAttribute('aria-label'),
    );
    expect(labels).toEqual(['Aktionen für Admin', 'Aktionen für Coder']);
  });

  it('actions_EditItem_SwitchesRowToEditMode', async () => {
    await render();

    await openActions(1);
    q('actor-edit')!.click();
    await harness.fixture.whenStable();

    expect(q('edit-save')).not.toBeNull();
    expect(q('edit-name')).not.toBeNull();
  });

  it('create_Agent_PostsNameAndRoleWithoutEmailOrPassword', async () => {
    await render([ADMIN]);
    await openCreate();
    await fill('actor-kind', 'agent');
    await fill('actor-name', 'Neuer Agent');
    await fill('actor-role', 'Tech-Lead');

    q('create-form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    const request = await expectRequest(http, 'POST', '/api/v1/actors');
    expect(request.request.body).toEqual({
      kind: 'agent',
      name: 'Neuer Agent',
      role: 'Tech-Lead',
      email: null,
      password: null,
      instanceScopes: [],
    });
    request.flush({ ...AGENT, name: 'Neuer Agent' }, { status: 201, statusText: 'Created' });
    (await expectRequest(http, 'GET', '/api/v1/actors')).flush([ADMIN, AGENT]);
    await settle(harness.fixture);

    expect(rows()).toHaveLength(2);
    expect(q('create-form')).toBeNull();
  });

  it('create_Human_PostsEmailPasswordAndScopes', async () => {
    await render([ADMIN]);
    await openCreate();
    await fill('actor-kind', 'human');
    await fill('actor-name', 'Mia');
    await fill('actor-email', 'mia@example.com');
    await fill('actor-password', 'correct horse battery');
    box('create-scope-workspaces:create').click();
    await harness.fixture.whenStable();

    q('create-form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    const request = await expectRequest(http, 'POST', '/api/v1/actors');

    expect(request.request.body).toEqual({
      kind: 'human',
      name: 'Mia',
      role: null,
      email: 'mia@example.com',
      password: 'correct horse battery',
      instanceScopes: ['workspaces:create'],
    });
    request.flush({}, { status: 500, statusText: 'x' });
    await settle(harness.fixture);
  });

  it('create_HumanWithoutEmailAndPassword_ShowsRequiredErrorsLocally', async () => {
    await render([ADMIN]);
    await openCreate();
    await fill('actor-kind', 'human');
    await fill('actor-name', 'Mia');
    await fill('actor-password', 'short');
    await submitCreate();

    expect(q('email-errors')!.textContent).toContain('E-Mail');
    expect(q('password-errors')!.textContent).toContain('mindestens 12');
  });

  it('create_ServerValidationError_ShowsErrorAtField', async () => {
    await render([ADMIN]);
    await openCreate();
    await fill('actor-kind', 'agent');
    await fill('actor-name', 'X');
    q('create-form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    (await expectRequest(http, 'POST', '/api/v1/actors')).flush(
      {
        status: 400,
        code: 'validation_failed',
        errors: [{ field: 'name', code: 'too_long', message: 'm' }],
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle(harness.fixture);

    expect(q('name-errors')!.textContent).toBe('Zu lang.');
    expect(q('form-error')).toBeNull();
  });

  it('create_EmailTaken_ShowsMessageAtEmailField', async () => {
    await render([ADMIN]);
    await openCreate();
    await fill('actor-kind', 'human');
    await fill('actor-name', 'Mia');
    await fill('actor-email', 'admin@agcowork.local');
    await fill('actor-password', 'correct horse battery');
    q('create-form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    (await expectRequest(http, 'POST', '/api/v1/actors')).flush(
      {
        status: 409,
        code: 'email_taken',
        errors: [{ field: 'email', code: 'conflict', message: 'm' }],
      },
      { status: 409, statusText: 'Conflict' },
    );
    await settle(harness.fixture);

    expect(q('email-errors')!.textContent).toContain('bereits von einem anderen Actor');
  });

  it('edit_Save_PatchesAllEditableFields', async () => {
    await render();

    await openActions(1);
    q('actor-edit')!.click();
    await harness.fixture.whenStable();
    const name = q('edit-name') as HTMLInputElement;
    name.value = 'Coder 2';
    name.dispatchEvent(new Event('input'));
    box('edit-scope-workspaces:create').click();
    box('edit-active').click();
    await harness.fixture.whenStable();
    q('edit-save')!.click();
    const request = await expectRequest(http, 'PATCH', `/api/v1/actors/${AGENT.id}`);

    expect(request.request.body).toEqual({
      name: 'Coder 2',
      role: '',
      instanceScopes: ['workspaces:create'],
      isActive: false,
    });
    request.flush({ ...AGENT, name: 'Coder 2' });
    (await expectRequest(http, 'GET', '/api/v1/actors')).flush([
      ADMIN,
      { ...AGENT, name: 'Coder 2' },
    ]);
    await settle(harness.fixture);

    expect(text(rows()[1])).toContain('Coder 2');
  });

  it('edit_SelfLockout_ShowsMessage', async () => {
    await render();

    await openActions(0);
    q('actor-edit')!.click();
    await harness.fixture.whenStable();
    box('edit-active').click();
    await harness.fixture.whenStable();
    q('edit-save')!.click();
    (await expectRequest(http, 'PATCH', `/api/v1/actors/${ADMIN.id}`)).flush(
      {
        status: 409,
        code: 'self_lockout',
        errors: [{ field: 'isActive', code: 'conflict', message: 'm' }],
      },
      { status: 409, statusText: 'Conflict' },
    );
    await settle(harness.fixture);

    expect(q('action-error')!.textContent).toContain('nicht selbst deaktivieren');
    expect(q('edit-save')).not.toBeNull();
  });

  it('edit_EmptyName_ShowsGermanFieldMessage', async () => {
    await render();

    await openActions(1);
    q('actor-edit')!.click();
    await harness.fixture.whenStable();
    const name = q('edit-name') as HTMLInputElement;
    name.value = '';
    name.dispatchEvent(new Event('input'));
    await harness.fixture.whenStable();
    q('edit-save')!.click();
    (await expectRequest(http, 'PATCH', `/api/v1/actors/${AGENT.id}`)).flush(
      {
        status: 400,
        code: 'validation_failed',
        detail: 'One or more validation errors occurred.',
        errors: [{ field: 'name', code: 'required', message: "'name' is required." }],
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle(harness.fixture);

    expect(q('action-error')!.textContent!.trim()).toBe('name: Pflichtfeld.');
  });

  it('create_ServerErrorOnInstanceScopes_ShowsAsFormMessage', async () => {
    await render([ADMIN]);
    await openCreate();
    await fill('actor-kind', 'agent');
    await fill('actor-name', 'X');
    q('create-form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    (await expectRequest(http, 'POST', '/api/v1/actors')).flush(
      {
        status: 400,
        code: 'validation_failed',
        errors: [
          {
            field: 'instanceScopes[0]',
            code: 'not_in_options',
            message: 'm',
            allowed: ['instance:admin'],
          },
        ],
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle(harness.fixture);

    expect(q('form-error')!.textContent).toContain('Wert ist nicht erlaubt.');
  });
});
