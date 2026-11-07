import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { apiErrorInterceptor } from '../../core/api/api-error.interceptor';
import type { components } from '../../core/api/generated/schema';
import { expectRequest, settle, workspace } from '../../core/testing/test-utils';
import { WorkspacesStore } from '../workspaces/workspaces.store';
import { ActorKeysComponent, toIsoTimestamp } from './actor-keys.component';

type Actor = components['schemas']['ActorDto'];
type ApiKey = components['schemas']['ApiKeyDto'];

const AGENT: Actor = {
  id: '0199a000-0000-7000-8000-000000000002',
  kind: 'agent',
  name: 'Coder',
  role: null,
  email: null,
  instanceScopes: [],
  isActive: true,
  createdAt: '2026-11-07T10:00:00Z',
};
const KEY: ApiKey = {
  id: '0199a000-0000-7000-8000-0000000000c1',
  actorId: AGENT.id,
  prefix: 'AbCd1234',
  scopes: ['read'],
  workspaceId: null,
  expiresAt: null,
  lastUsedAt: null,
  revokedAt: null,
  createdAt: '2026-11-07T10:00:00Z',
};
const PLAIN_KEY = 'agck_AbCd1234_SECRETSECRETSECRETSECRETSECRET12';
const KEYS_URL = `/api/v1/actors/${AGENT.id}/keys`;

describe('ActorKeysComponent', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;

  async function render(keys: ApiKey[] = [KEY], actor: Actor = AGENT): Promise<void> {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(
          [{ path: 'admin/actors/:id/keys', component: ActorKeysComponent }],
          withComponentInputBinding(),
        ),
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
        {
          provide: WorkspacesStore,
          useValue: { workspaces: signal([workspace({ id: 'ws-1', name: 'Demo' })]) },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl(`/admin/actors/${AGENT.id}/keys`);
    (await expectRequest(http, 'GET', `/api/v1/actors/${AGENT.id}`)).flush(actor);
    (await expectRequest(http, 'GET', KEYS_URL)).flush(keys);
    await navigation;
    await settle(harness.fixture);
  }

  afterEach(() => http.verify());

  const root = () => harness.routeNativeElement as HTMLElement;
  // Overlays (e.g. the confirm dialog) render into the CDK overlay container in <body>.
  const q = (id: string) =>
    root().querySelector<HTMLElement>(`[data-testid="${id}"]`) ??
    document.body.querySelector<HTMLElement>(`[data-testid="${id}"]`);
  const rows = () => Array.from(root().querySelectorAll('[data-testid="key-row"]'));
  const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

  /** Creates a key and answers with the plain-text key plus the refreshed list. */
  async function createKey(listAfter: ApiKey[]): Promise<void> {
    q('create-form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    (await expectRequest(http, 'POST', KEYS_URL)).flush(
      { key: PLAIN_KEY, apiKey: listAfter[0] },
      { status: 201, statusText: 'Created' },
    );
    (await expectRequest(http, 'GET', KEYS_URL)).flush(listAfter);
    await settle(harness.fixture);
  }

  it('render_Keys_ShowsPrefixScopesStatusButNoSecret', async () => {
    await render([
      KEY,
      { ...KEY, id: 'r', prefix: 'RevokedX', revokedAt: '2026-11-07T11:00:00Z' },
      {
        ...KEY,
        id: 'e',
        prefix: 'ExpiredX',
        expiresAt: '2020-01-01T00:00:00Z',
        workspaceId: 'ws-1',
      },
    ]);

    expect(rows()).toHaveLength(3);
    expect(text(rows()[0])).toContain('AbCd1234');
    expect(text(rows()[0])).toContain('aktiv');
    expect(text(rows()[1])).toContain('widerrufen');
    expect(text(rows()[2])).toContain('abgelaufen');
    expect(text(rows()[2])).toContain('Demo');
    expect(root().textContent).not.toContain('agck_');
    expect(q('created-key')).toBeNull();
    expect(rows()[1].querySelector('[data-testid="revoke-key"]')).toBeNull();
  });

  it('create_Key_ShowsPlainTextOnceAndListHasNoSecret', async () => {
    await render([]);
    q('key-scope-read')!.click();
    await harness.fixture.whenStable();

    q('create-form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    const request = await expectRequest(http, 'POST', KEYS_URL);
    expect(request.request.body).toEqual({ scopes: ['read'], workspaceId: null, expiresAt: null });
    request.flush({ key: PLAIN_KEY, apiKey: KEY }, { status: 201, statusText: 'Created' });
    (await expectRequest(http, 'GET', KEYS_URL)).flush([KEY]);
    await settle(harness.fixture);

    expect(q('created-key-value')!.textContent).toBe(PLAIN_KEY);
    expect(rows()).toHaveLength(1);
    expect(text(rows()[0])).not.toContain(PLAIN_KEY);

    q('dismiss-key')!.click();
    await harness.fixture.whenStable();
    expect(root().textContent).not.toContain(PLAIN_KEY);
    expect(q('created-key')).toBeNull();
  });

  it('create_Key_PlainTextIsNotPersistedInBrowserStorage', async () => {
    await render([]);

    await createKey([KEY]);

    expect(q('created-key')).not.toBeNull();
    expect(JSON.stringify({ ...localStorage })).not.toContain(PLAIN_KEY);
    expect(JSON.stringify({ ...sessionStorage })).not.toContain(PLAIN_KEY);
  });

  it('copy_Click_WritesKeyToClipboard', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    await render([]);
    await createKey([KEY]);

    q('copy-key')!.click();
    await settle(harness.fixture);

    expect(writeText).toHaveBeenCalledWith(PLAIN_KEY);
    expect(q('copy-state')!.textContent).toBe('Kopiert.');
  });

  it('create_WithWorkspaceAndExpiry_SendsBoth', async () => {
    await render([]);
    const select = q('key-workspace') as HTMLSelectElement;
    select.value = 'ws-1';
    select.dispatchEvent(new Event('change'));
    const expires = q('key-expires') as HTMLInputElement;
    expires.value = '2030-01-02T03:04';
    expires.dispatchEvent(new Event('input'));
    await harness.fixture.whenStable();

    q('create-form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    const request = await expectRequest(http, 'POST', KEYS_URL);

    expect(request.request.body).toEqual({
      scopes: [],
      workspaceId: 'ws-1',
      expiresAt: new Date('2030-01-02T03:04').toISOString(),
    });
    request.flush({}, { status: 400, statusText: 'x' });
    await settle(harness.fixture);
    expect(q('form-error')).not.toBeNull();
  });

  it('revoke_Confirmed_DeletesKeyAndMarksIt', async () => {
    await render();

    rows()[0].querySelector<HTMLElement>('[data-testid="revoke-key"]')!.click();
    await harness.fixture.whenStable();
    q('confirm-ok')!.click();
    (await expectRequest(http, 'DELETE', `/api/v1/keys/${KEY.id}`)).flush(null, {
      status: 204,
      statusText: 'No Content',
    });
    (await expectRequest(http, 'GET', KEYS_URL)).flush([
      { ...KEY, revokedAt: '2026-11-07T12:00:00Z' },
    ]);
    await settle(harness.fixture);

    expect(text(rows()[0])).toContain('widerrufen');
    expect(q('confirm-dialog')).toBeNull();
  });

  it('render_HumanActor_ShowsHintInsteadOfCreateForm', async () => {
    await render([], { ...AGENT, kind: 'human', email: 'm@example.com' });

    expect(q('create-form')).toBeNull();
    expect(q('human-hint')).not.toBeNull();
  });

  it.each([
    ['', null],
    ['not a date', null],
    ['2030-01-02T03:04', new Date('2030-01-02T03:04').toISOString()],
  ])('toIsoTimestamp_%j_Is_%s', (input, expected) => {
    expect(toIsoTimestamp(input)).toBe(expected);
  });

  it('create_ExpiryInPast_ShowsGermanFieldMessage', async () => {
    await render([]);

    q('create-form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    (await expectRequest(http, 'POST', KEYS_URL)).flush(
      {
        status: 400,
        code: 'validation_failed',
        detail: 'One or more validation errors occurred.',
        errors: [{ field: 'expiresAt', code: 'out_of_range', message: 'must be in the future' }],
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle(harness.fixture);

    expect(q('form-error')!.textContent!.trim()).toBe(
      'expiresAt: Wert liegt außerhalb des erlaubten Bereichs.',
    );
  });

  it('navigate_OtherActor_HidesPlainTextKey', async () => {
    await render([]);
    await createKey([KEY]);
    expect(q('created-key-value')!.textContent).toBe(PLAIN_KEY);

    const other = '0199a000-0000-7000-8000-000000000003';
    const navigation = harness.navigateByUrl(`/admin/actors/${other}/keys`);
    (await expectRequest(http, 'GET', `/api/v1/actors/${other}`)).flush({ ...AGENT, id: other });
    (await expectRequest(http, 'GET', `/api/v1/actors/${other}/keys`)).flush([]);
    await navigation;
    await settle(harness.fixture);

    expect(q('created-key')).toBeNull();
    expect(root().textContent).not.toContain(PLAIN_KEY);
  });
});
