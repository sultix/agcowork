import { TestBed } from '@angular/core/testing';
import { EnvironmentProviders, Provider, WritableSignal, signal } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { Routes, provideRouter, withComponentInputBinding } from '@angular/router';
import { apiErrorInterceptor } from '../api/api-error.interceptor';
import { Me, SessionStatus, SessionStore } from '../auth/session.store';
import { WorkspacesStore } from '../../features/workspaces/workspaces.store';
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
  mustChangePassword: false,
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

// ---------------------------------------------------------------------------------------------
// Fixtures and helpers for the spaces / collections / records features.
// ---------------------------------------------------------------------------------------------

export type Space = components['schemas']['SpaceDto'];
export type Collection = components['schemas']['CollectionDto'];
export type RecordDto = components['schemas']['RecordDto'];
export type Member = components['schemas']['MemberDto'];
export type ActivityEvent = components['schemas']['ActivityEventDto'];
export type Revision = components['schemas']['RevisionDto'];
export type RevisionSummary = components['schemas']['RevisionSummaryDto'];
export type PropertyDefinition = components['schemas']['PropertyDefinitionDto'];

export const SPACE_ID = '0199a000-0000-7000-8000-0000000000b1';
export const COLLECTION_ID = '0199a000-0000-7000-8000-0000000000c1';
export const RECORD_ID = '0199a000-0000-7000-8000-0000000000d1';
export const AGENT_ID = '0199a000-0000-7000-8000-000000000099';

export function space(overrides: Partial<Space> = {}): Space {
  return {
    id: SPACE_ID,
    name: 'Produkt',
    description: 'Alles rund um das Produkt',
    icon: null,
    sortOrder: 0,
    createdBy: ADMIN_ME.id,
    createdAt: '2026-11-07T10:00:00Z',
    archivedAt: null,
    ...overrides,
  };
}

export function propertyDefinition(
  overrides: Partial<PropertyDefinition> & Pick<PropertyDefinition, 'key' | 'type'>,
): PropertyDefinition {
  return {
    name: overrides.key,
    description: null,
    required: false,
    readOnly: false,
    options: null,
    targetCollectionId: null,
    multiple: false,
    default: null,
    unit: null,
    precision: null,
    ...overrides,
  };
}

export const STATUS_DEFINITION = propertyDefinition({
  key: 'status',
  name: 'Status',
  type: 'status',
  options: [
    { value: 'todo', label: 'Offen', color: null, group: 'todo' },
    { value: 'done', label: 'Erledigt', color: null, group: 'done' },
  ],
});

export function collection(overrides: Partial<Collection> = {}): Collection {
  return {
    id: COLLECTION_ID,
    spaceId: SPACE_ID,
    name: 'Aufgaben',
    description: 'Arbeitspakete des Teams',
    titlePropertyKey: 'title',
    schemaVersion: 1,
    schema: [
      propertyDefinition({ key: 'title', name: 'Titel', type: 'text', required: true }),
      STATUS_DEFINITION,
      propertyDefinition({ key: 'estimate', name: 'Schätzung', type: 'number' }),
    ],
    createdBy: ADMIN_ME.id,
    createdAt: '2026-11-07T10:00:00Z',
    archivedAt: null,
    ...overrides,
  };
}

export function recordDto(overrides: Partial<RecordDto> = {}): RecordDto {
  return {
    id: RECORD_ID,
    spaceId: SPACE_ID,
    collectionId: COLLECTION_ID,
    parentRecordId: null,
    title: 'Login bauen',
    properties: { status: 'todo' } as unknown as Record<string, never>,
    body: null,
    revision: 3,
    createdBy: ADMIN_ME.id,
    createdAt: '2026-11-07T10:00:00Z',
    updatedBy: ADMIN_ME.id,
    updatedAt: '2026-11-07T11:00:00Z',
    archivedAt: null,
    ...overrides,
  };
}

export function member(overrides: Partial<Member> = {}): Member {
  return {
    actorId: ADMIN_ME.id,
    name: 'Admin',
    kind: 'human',
    role: 'owner',
    scopes: ['read', 'write', 'schema', 'admin'],
    createdBy: ADMIN_ME.id,
    createdAt: '2026-11-07T10:00:00Z',
    ...overrides,
  };
}

export function activityEvent(
  overrides: Omit<Partial<ActivityEvent>, 'diff'> & { diff?: unknown } = {},
): ActivityEvent {
  return {
    seq: 1,
    id: 'e1',
    type: 'record.updated',
    actorId: AGENT_ID,
    targetType: 'record',
    targetId: RECORD_ID,
    spaceId: SPACE_ID,
    collectionId: COLLECTION_ID,
    at: '2026-11-07T11:00:00Z',
    targetTitle: null,
    ...overrides,
    diff: (overrides.diff ?? {}) as never,
  };
}

export function revisionSummary(
  no: number,
  overrides: Partial<RevisionSummary> = {},
): RevisionSummary {
  return {
    no,
    kind: 'updated',
    actorId: ADMIN_ME.id,
    at: '2026-11-07T11:00:00Z',
    reason: null,
    ...overrides,
  };
}

export function revision(no: number, overrides: Partial<Revision> = {}): Revision {
  return {
    recordId: RECORD_ID,
    no,
    kind: 'updated',
    title: 'Login bauen',
    properties: {} as never,
    body: '',
    actorId: ADMIN_ME.id,
    at: '2026-11-07T11:00:00Z',
    reason: null,
    ...overrides,
  };
}

export const API = '/api/v1/w/demo';

/** Waits for one request and answers it with `body`. */
export async function respond(
  http: HttpTestingController,
  method: string,
  url: string,
  body: unknown,
  init: { status?: number; statusText?: string } = {},
): Promise<TestRequest> {
  const request = await expectRequest(http, method, url);
  request.flush(body as never, init);
  return request;
}

/**
 * Providers for rendering one page of a workspace in a router harness: routes with component
 * input binding, HTTP testing, an authenticated fake session and a fake workspace list.
 */
export function pageProviders(options: {
  routes: Routes;
  me?: Me;
  ws?: Workspace;
}): (Provider | EnvironmentProviders)[] {
  const list = signal([options.ws ?? workspace()]);
  return [
    provideRouter(options.routes, withComponentInputBinding()),
    provideHttpClient(withInterceptors([apiErrorInterceptor])),
    provideHttpClientTesting(),
    { provide: SessionStore, useValue: fakeSession(options.me ?? ADMIN_ME) },
    {
      provide: WorkspacesStore,
      useValue: { workspaces: list, find: (s: string) => list().find((w) => w.slug === s) },
    },
  ];
}

/** Answers the given `GET` requests in order (each waits until it was issued). */
export async function serve(
  http: HttpTestingController,
  answers: readonly (readonly [url: string, body: unknown])[],
): Promise<void> {
  for (const [url, body] of answers) {
    await respond(http, 'GET', url, body);
  }
}

/** Types into an input/textarea/select like a user (works with Signal Forms bindings). */
export function typeInto(
  element: HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement,
  value: string,
): void {
  element.value = value;
  element.dispatchEvent(new Event('input', { bubbles: true }));
  element.dispatchEvent(new Event('change', { bubbles: true }));
}

// ---------------------------------------------------------------------------------------------
// Fixtures for views, tables and boards (AP-015).
// ---------------------------------------------------------------------------------------------

export type View = components['schemas']['ViewDto'];

export const VIEW_ID = '0199a000-0000-7000-8000-0000000000e1';
export const BOARD_VIEW_ID = '0199a000-0000-7000-8000-0000000000e2';

export function viewDto(overrides: Partial<View> = {}): View {
  return {
    id: VIEW_ID,
    collectionId: COLLECTION_ID,
    name: 'Tabelle',
    type: 'table',
    filter: null,
    sorts: [],
    groupBy: null,
    visibleProperties: [],
    cardCoverProperty: null,
    isDefault: true,
    isSystem: true,
    sortOrder: 0,
    needsAttention: false,
    createdBy: ADMIN_ME.id,
    createdAt: '2026-11-07T10:00:00Z',
    ...overrides,
  };
}

/** A query result page (`POST …/query`). */
export function queryPage(
  items: readonly RecordDto[],
  nextCursor: string | null = null,
  count: number | null = items.length,
): { items: readonly RecordDto[]; nextCursor: string | null; count: number | null } {
  return { items, nextCursor, count };
}

/** Waits for one `POST` and answers it with `body`; returns the request for body checks. */
export function respondPost(
  http: HttpTestingController,
  url: string,
  body: unknown,
  init: { status?: number; statusText?: string } = {},
): Promise<TestRequest> {
  return respond(http, 'POST', url, body, init);
}

/**
 * Answers a `GET` that may or may not be issued (e.g. `/members`, asked for once a cell renders);
 * waits up to `ms` for it. Resolves whether it came.
 */
export async function respondIfRequested(
  http: HttpTestingController,
  url: string,
  body: unknown,
  ms = 300,
): Promise<boolean> {
  const deadline = Date.now() + ms;
  for (;;) {
    TestBed.tick();
    const matches = http.match({ method: 'GET', url });
    if (matches.length > 0) {
      for (const match of matches) {
        match.flush(body as never);
      }
      return true;
    }
    if (Date.now() > deadline) {
      return false;
    }
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}
