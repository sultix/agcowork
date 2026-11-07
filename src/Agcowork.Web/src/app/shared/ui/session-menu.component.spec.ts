import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Me, SessionStore } from '../../core/auth/session.store';
import { SessionMenuComponent } from './session-menu.component';

@Component({ imports: [SessionMenuComponent], template: '<ag-session-menu />' })
class HostComponent {}

@Component({ template: 'login page' })
class LoginStubComponent {}

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
        ]),
        { provide: SessionStore, useValue: session },
      ],
    });
  });

  it('render_Authenticated_ShowsNameAndKind', async () => {
    const harness = await RouterTestingHarness.create('/home');

    const actor = (harness.routeNativeElement as HTMLElement).querySelector(
      '[data-testid="session-actor"]',
    );
    expect(actor?.textContent?.replace(/\s+/g, ' ').trim()).toBe('Coder · Agent');
  });

  it('render_Anonymous_ShowsNothing', async () => {
    me.set(null);
    const harness = await RouterTestingHarness.create('/home');

    expect(
      (harness.routeNativeElement as HTMLElement).querySelector('[data-testid="logout"]'),
    ).toBeNull();
  });

  it('logout_Click_EndsSessionAndGoesToLogin', async () => {
    const harness = await RouterTestingHarness.create('/home');

    (harness.routeNativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('[data-testid="logout"]')!
      .click();
    await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/login'));

    expect(session.logout).toHaveBeenCalledOnce();
  });
});
