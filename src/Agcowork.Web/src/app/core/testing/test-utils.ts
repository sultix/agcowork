import { TestBed } from '@angular/core/testing';
import { WritableSignal, signal } from '@angular/core';
import { HttpTestingController, TestRequest } from '@angular/common/http/testing';
import { Me, SessionStatus } from '../auth/session.store';
import type { components } from '../api/generated/schema';

export type Workspace = components['schemas']['WorkspaceDto'];

export const ADMIN_ME: Me = {
  id: '0199a000-0000-7000-8000-000000000001',
  kind: 'human',
  name: 'Admin',
  role: null,
  email: 'admin@agcowork.local',
  instanceScopes: ['instance:admin'],
  apiKey: null,
};

export const MEMBER_ME: Me = { ...ADMIN_ME, name: 'Mia', instanceScopes: [] };

export interface FakeSession {
  me: WritableSignal<Me | null>;
  status: WritableSignal<SessionStatus>;
  ready: () => Promise<SessionStatus>;
}

/** Stand-in for `SessionStore` with writable signals. */
export function fakeSession(me: Me | null): FakeSession {
  const status = signal<SessionStatus>(me ? 'authenticated' : 'anonymous');
  return { me: signal(me), status, ready: () => Promise.resolve(status()) };
}

export function workspace(overrides: Partial<Workspace> = {}): Workspace {
  return {
    id: '0199a000-0000-7000-8000-0000000000a1',
    slug: 'demo',
    name: 'Demo',
    description: null,
    createdBy: ADMIN_ME.id,
    createdAt: '2026-11-07T10:00:00Z',
    archivedAt: null,
    role: 'owner',
    scopes: ['read', 'write', 'schema', 'admin'],
    ...overrides,
  };
}

/** Waits until effects/resources have issued the request, then returns it. */
export async function expectRequest(
  http: HttpTestingController,
  method: string,
  url: string,
): Promise<TestRequest> {
  const deadline = Date.now() + 1000;
  for (;;) {
    TestBed.tick();
    const matches = http.match({ method, url });
    if (matches.length === 1) {
      return matches[0];
    }
    if (matches.length > 1 || Date.now() > deadline) {
      throw new Error(`Expected one ${method} ${url}, found ${matches.length}.`);
    }
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}

/** Lets pending promises, effects and change detection settle. */
export async function settle(fixture: { whenStable(): Promise<unknown> }): Promise<void> {
  await fixture.whenStable();
  await new Promise((resolve) => setTimeout(resolve));
  await fixture.whenStable();
}
