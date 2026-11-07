import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { apiErrorInterceptor } from '../../core/api/api-error.interceptor';
import type { components } from '../../core/api/generated/schema';
import { Me, SessionStore } from '../../core/auth/session.store';
import {
  ADMIN_ME,
  MEMBER_ME,
  expectRequest,
  fakeSession,
  settle,
  workspace,
} from '../../core/testing/test-utils';
import { Workspace, WorkspacesStore } from '../workspaces/workspaces.store';
import { MembersComponent } from './members.component';

type Member = components['schemas']['MemberDto'];
type Actor = components['schemas']['ActorDto'];

const OWNER: Member = {
  actorId: ADMIN_ME.id,
  name: 'Admin',
  kind: 'human',
  role: 'owner',
  scopes: ['read', 'write', 'schema', 'admin'],
  createdBy: ADMIN_ME.id,
  createdAt: '2026-11-07T10:00:00Z',
};
const AGENT: Member = {
  ...OWNER,
  actorId: '0199a000-0000-7000-8000-000000000099',
  name: 'Coder',
  kind: 'agent',
  role: 'editor',
  scopes: ['read', 'write'],
};
const AGENT_ACTOR: Actor = {
  id: AGENT.actorId,
  kind: 'agent',
  name: 'Coder',
  role: null,
  email: null,
  instanceScopes: [],
  isActive: true,
  createdAt: '2026-11-07T10:00:00Z',
};
const NEW_ACTOR: Actor = {
  ...AGENT_ACTOR,
  id: '0199a000-0000-7000-8000-0000000000bb',
  name: 'Neuer Agent',
};

const MEMBERS_URL = '/api/v1/w/demo/members';

describe('MembersComponent', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;

  /** Renders the page; `members` is what `GET /members` answers. */
  async function render(options: {
    me?: Me;
    ws?: Workspace;
    members?: Member[];
    actors?: Actor[];
  }): Promise<void> {
    const me = options.me ?? ADMIN_ME;
    const list = signal([options.ws ?? workspace()]);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'w/:workspaceSlug/settings/members', component: MembersComponent }]),
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
        { provide: SessionStore, useValue: fakeSession(me) },
        {
          provide: WorkspacesStore,
          useValue: { find: (s: string) => list().find((w) => w.slug === s) },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl('/w/demo/settings/members');
    (await expectRequest(http, 'GET', MEMBERS_URL)).flush(options.members ?? [OWNER, AGENT]);
    if (me.instanceScopes.includes('instance:admin')) {
      (await expectRequest(http, 'GET', '/api/v1/actors')).flush(
        options.actors ?? [AGENT_ACTOR, NEW_ACTOR],
      );
    }
    await navigation;
    await settle(harness.fixture);
  }

  afterEach(() => http.verify());

  const root = () => harness.routeNativeElement as HTMLElement;
  // Overlays (e.g. the confirm dialog) render into the CDK overlay container in <body>.
  const q = (id: string) =>
    root().querySelector<HTMLElement>(`[data-testid="${id}"]`) ??
    document.body.querySelector<HTMLElement>(`[data-testid="${id}"]`);
  const rows = () => Array.from(root().querySelectorAll('[data-testid="member-row"]'));
  const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

  async function pick(selector: string, value: string): Promise<void> {
    const select = root().querySelector<HTMLSelectElement | HTMLInputElement>(selector)!;
    select.value = value;
    select.dispatchEvent(new Event('input'));
    select.dispatchEvent(new Event('change'));
    await harness.fixture.whenStable();
  }

  it('render_Members_ShowsNameKindRoleScopesAndSince', async () => {
    await render({});

    expect(rows()).toHaveLength(2);
    expect(text(rows()[0])).toContain('Admin (du)');
    expect(text(rows()[0])).toContain('Mensch');
    expect(text(rows()[1])).toContain('Coder');
    expect(text(rows()[1])).toContain('Agent');
    expect(text(rows()[1])).toContain('editor');
    expect(text(rows()[1])).toContain('read, write');
    expect(text(rows()[1])).toContain('07.11.2026');
  });

  it('add_InstanceAdminPicksActor_PostsRoleAndReloads', async () => {
    await render({ members: [OWNER] });

    await pick('#add-actor', NEW_ACTOR.id);
    await pick('#add-role', 'editor');
    q('add-form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    const request = await expectRequest(http, 'POST', MEMBERS_URL);
    expect(request.request.body).toEqual({ actorId: NEW_ACTOR.id, role: 'editor', scopes: null });
    request.flush({ ...AGENT, actorId: NEW_ACTOR.id }, { status: 201, statusText: 'Created' });
    (await expectRequest(http, 'GET', MEMBERS_URL)).flush([
      OWNER,
      { ...AGENT, actorId: NEW_ACTOR.id },
    ]);
    await settle(harness.fixture);

    expect(rows()).toHaveLength(2);
  });

  it('add_WithScopes_SendsNarrowedScopes', async () => {
    await render({ members: [OWNER] });

    await pick('#add-actor', NEW_ACTOR.id);
    q('add-scope-read')!.click();
    await harness.fixture.whenStable();
    q('add-form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    const request = await expectRequest(http, 'POST', MEMBERS_URL);

    expect(request.request.body).toEqual({
      actorId: NEW_ACTOR.id,
      role: 'editor',
      scopes: ['read'],
    });
    request.flush({}, { status: 500, statusText: 'x' });
    await settle(harness.fixture);
  });

  it('add_NonInstanceAdmin_UsesActorIdInput', async () => {
    await render({ me: MEMBER_ME, members: [OWNER] });

    expect(root().querySelector('select#add-actor')).toBeNull();
    expect(root().querySelector('input#add-actor')).not.toBeNull();
  });

  it('add_NoActor_ShowsRequiredError', async () => {
    await render({});

    q('add-form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    await settle(harness.fixture);

    expect(q('actorId-errors')!.textContent).toContain('Actor');
  });

  it('add_AlreadyMember_ShowsMessageAtActorField', async () => {
    await render({ me: MEMBER_ME, members: [OWNER] });

    await pick('input#add-actor', AGENT.actorId);
    q('add-form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    (await expectRequest(http, 'POST', MEMBERS_URL)).flush(
      {
        status: 409,
        code: 'already_member',
        errors: [{ field: 'actorId', code: 'conflict', message: 'm' }],
      },
      { status: 409, statusText: 'Conflict' },
    );
    await settle(harness.fixture);

    expect(q('actorId-errors')!.textContent).toContain('bereits Mitglied');
  });

  it('edit_RoleToViewer_PatchesOnlyRole', async () => {
    await render({});

    rows()[1].querySelector<HTMLElement>('[data-testid="member-edit"]')!.click();
    await harness.fixture.whenStable();
    await pick('[data-testid="edit-role"]', 'viewer');
    q('edit-save')!.click();
    const request = await expectRequest(http, 'PATCH', `${MEMBERS_URL}/${AGENT.actorId}`);
    expect(request.request.body).toEqual({ role: 'viewer', scopes: null });
    request.flush({ ...AGENT, role: 'viewer', scopes: ['read'] });
    (await expectRequest(http, 'GET', MEMBERS_URL)).flush([
      OWNER,
      { ...AGENT, role: 'viewer', scopes: ['read'] },
    ]);
    await settle(harness.fixture);

    expect(text(rows()[1])).toContain('viewer');
  });

  it('edit_ScopesChanged_PatchesScopes', async () => {
    await render({});

    rows()[1].querySelector<HTMLElement>('[data-testid="member-edit"]')!.click();
    await harness.fixture.whenStable();
    q('edit-scope-write')!.click();
    await harness.fixture.whenStable();
    q('edit-save')!.click();
    const request = await expectRequest(http, 'PATCH', `${MEMBERS_URL}/${AGENT.actorId}`);

    expect(request.request.body).toEqual({ role: null, scopes: ['read'] });
    request.flush({ ...AGENT, scopes: ['read'] });
    (await expectRequest(http, 'GET', MEMBERS_URL)).flush([OWNER]);
    await settle(harness.fixture);
  });

  it('edit_SelfEscalation_ShowsMessage', async () => {
    await render({});

    rows()[0].querySelector<HTMLElement>('[data-testid="member-edit"]')!.click();
    await harness.fixture.whenStable();
    await pick('[data-testid="edit-role"]', 'viewer');
    q('edit-save')!.click();
    (await expectRequest(http, 'PATCH', `${MEMBERS_URL}/${OWNER.actorId}`)).flush(
      { status: 409, code: 'self_escalation' },
      { status: 409, statusText: 'Conflict' },
    );
    await settle(harness.fixture);

    expect(q('action-error')!.textContent).toContain('nur eingeschränkt');
  });

  it('remove_Confirmed_DeletesAndReloads', async () => {
    await render({});

    rows()[1].querySelector<HTMLElement>('[data-testid="member-remove"]')!.click();
    await harness.fixture.whenStable();
    expect(http.match(() => true)).toHaveLength(0);
    q('confirm-ok')!.click();
    (await expectRequest(http, 'DELETE', `${MEMBERS_URL}/${AGENT.actorId}`)).flush(null, {
      status: 204,
      statusText: 'No Content',
    });
    (await expectRequest(http, 'GET', MEMBERS_URL)).flush([OWNER]);
    await settle(harness.fixture);

    expect(rows()).toHaveLength(1);
    expect(q('confirm-dialog')).toBeNull();
  });

  it('remove_Cancelled_SendsNothing', async () => {
    await render({});

    rows()[1].querySelector<HTMLElement>('[data-testid="member-remove"]')!.click();
    await harness.fixture.whenStable();
    q('confirm-cancel')!.click();
    await harness.fixture.whenStable();

    http.expectNone(`${MEMBERS_URL}/${AGENT.actorId}`);
    expect(rows()).toHaveLength(2);
  });

  it('remove_LastOwner_ShowsLastOwnerMessage', async () => {
    await render({});

    rows()[0].querySelector<HTMLElement>('[data-testid="member-remove"]')!.click();
    await harness.fixture.whenStable();
    q('confirm-ok')!.click();
    (await expectRequest(http, 'DELETE', `${MEMBERS_URL}/${OWNER.actorId}`)).flush(
      { status: 409, code: 'last_owner' },
      { status: 409, statusText: 'Conflict' },
    );
    await settle(harness.fixture);

    expect(q('action-error')!.textContent).toContain('mindestens einen Owner');
    expect(rows()).toHaveLength(2);
  });

  it('render_Viewer_ShowsNoWriteActions', async () => {
    await render({ me: MEMBER_ME, ws: workspace({ role: 'viewer', scopes: ['read'] }) });

    expect(q('add-form')).toBeNull();
    expect(root().querySelector('[data-testid="member-edit"]')).toBeNull();
    expect(root().querySelector('[data-testid="member-remove"]')).toBeNull();
    expect(q('readonly-notice')).not.toBeNull();
  });

  it('render_AdminRole_CannotChangeOwners', async () => {
    await render({ me: MEMBER_ME, ws: workspace({ role: 'admin' }) });

    expect(rows()[0].querySelector('[data-testid="member-edit"]')).toBeNull();
    expect(rows()[1].querySelector('[data-testid="member-edit"]')).not.toBeNull();
    const roles = Array.from(root().querySelectorAll('#add-role option')).map((o) => o.textContent);
    expect(roles).not.toContain('owner');
  });
});
