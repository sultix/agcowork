import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Me, SessionStore } from '../../core/auth/session.store';
import { SessionMenuComponent, initialsOf } from './session-menu.component';

@Component({ imports: [SessionMenuComponent], template: '<ag-session-menu />' })
class HostComponent {}

@Component({ template: 'login page' })
class LoginStubComponent {}

@Component({ template: 'admin page' })
class AdminStubComponent {}

const ME: Me = {
  id: '0199a000-0000-7000-8000-000000000002',
  kind: 'agent',
  name: 'Coder',
  role: null,
  email: null,
  instanceScopes: [],
  apiKey: null,
};

describe('SessionMenuComponent', () => {
  let me: ReturnType<typeof signal<Me | null>>;
  let session: { me: typeof me; logout: ReturnType<typeof vi.fn> };
  let harness: RouterTestingHarness;

  beforeEach(() => {
    me = signal<Me | null>(ME);
    session = {
      me,
      logout: vi.fn(async () => {
        me.set(null);
      }),
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'home', component: HostComponent },
          { path: 'login', component: LoginStubComponent },
          { path: 'admin', component: AdminStubComponent },
        ]),
        { provide: SessionStore, useValue: session },
      ],
    });
  });

  const root = () => harness.routeNativeElement as HTMLElement;
  // Menu items render into the CDK overlay container in <body>.
  const inBody = (id: string) => document.body.querySelector<HTMLElement>(`[data-testid="${id}"]`);

  async function render(): Promise<void> {
    harness = await RouterTestingHarness.create('/home');
  }

  async function openMenu(): Promise<void> {
    root().querySelector<HTMLButtonElement>('[data-testid="session-menu-trigger"]')!.click();
    await harness.fixture.whenStable();
  }

  it('render_Authenticated_ShowsInitialsNameAndKindOnTrigger', async () => {
    await render();

    const trigger = root().querySelector('[data-testid="session-menu-trigger"]')!;
    expect(trigger.querySelector('[data-slot="avatar-fallback"]')?.textContent?.trim()).toBe('C');
    const actor = root().querySelector('[data-testid="session-actor"]');
    expect(actor?.textContent?.replace(/\s+/g, ' ').trim()).toBe('Coder · Agent');
    expect(inBody('session-menu')).toBeNull();
  });

  it('render_Anonymous_ShowsNothing', async () => {
    me.set(null);
    await render();

    expect(root().querySelector('[data-testid="session-menu-trigger"]')).toBeNull();
  });

  it('open_Click_ShowsMenuWithActorAndLogout', async () => {
    await render();

    await openMenu();

    const menu = inBody('session-menu')!;
    expect(menu.getAttribute('role')).toBe('menu');
    expect(menu.textContent).toContain('Coder');
    expect(inBody('logout')?.getAttribute('role')).toBe('menuitem');
  });

  it('open_InstanceAdmin_ShowsAdminLink', async () => {
    me.set({ ...ME, instanceScopes: ['instance:admin'] });
    await render();

    await openMenu();

    expect(inBody('admin-link')?.getAttribute('href')).toBe('/admin');
  });

  it('open_NoInstanceAdmin_HidesAdminLink', async () => {
    await render();

    await openMenu();

    expect(inBody('session-menu')).not.toBeNull();
    expect(inBody('admin-link')).toBeNull();
  });

  it('logout_Click_EndsSessionAndGoesToLogin', async () => {
    await render();
    await openMenu();

    inBody('logout')!.click();
    await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/login'));

    expect(session.logout).toHaveBeenCalledOnce();
    expect(inBody('session-menu')).toBeNull();
  });

  it.each([
    ['Ada Lovelace', 'AL'],
    ['Coder', 'C'],
    ['  grace  brewster murray hopper ', 'GH'],
    ['', '?'],
  ])('initialsOf_%j_Is_%s', (name, expected) => {
    expect(initialsOf(name)).toBe(expected);
  });
});
