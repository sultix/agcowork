import { Injectable, Signal, computed, inject, resource } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError } from '../../core/api/api-error';
import type { components } from '../../core/api/generated/schema';
import { SessionStore } from '../../core/auth/session.store';

export type Workspace = components['schemas']['WorkspaceDto'];
export type CreateWorkspaceRequest = components['schemas']['CreateWorkspaceRequest'];

/** Instance scopes that allow creating workspaces. */
const CREATE_SCOPES = ['workspaces:create', 'instance:admin'];

/** Roles that may change workspace settings and members. */
export const ADMIN_ROLES: readonly string[] = ['admin', 'owner'];

function byName(a: Workspace, b: Workspace): number {
  return a.name.localeCompare(b.name);
}

/**
 * The workspaces the signed-in actor is a member of (`GET /workspaces`), shared by the list
 * page, the sidebar switcher and the settings pages. Loads once the session is authenticated.
 * Note: the API lists memberships only, also for instance admins.
 */
@Injectable({ providedIn: 'root' })
export class WorkspacesStore {
  private readonly api = inject(ApiClient);
  private readonly session = inject(SessionStore);

  private readonly listResource = resource<Workspace[], true | undefined>({
    params: () => (this.session.status() === 'authenticated' ? true : undefined),
    loader: () => firstValueFrom(this.api.get('/workspaces')),
  });

  readonly workspaces: Signal<readonly Workspace[]> = computed(() =>
    this.listResource.hasValue() ? this.listResource.value() : [],
  );
  readonly isLoading: Signal<boolean> = computed(() => this.listResource.isLoading());
  readonly error: Signal<ApiError | null> = computed(() => {
    const error = this.listResource.error();
    return error ? ApiError.from(error) : null;
  });

  /** Whether the actor may create workspaces (`workspaces:create` or `instance:admin`). */
  readonly canCreate: Signal<boolean> = computed(() =>
    (this.session.me()?.instanceScopes ?? []).some((s) => CREATE_SCOPES.includes(s)),
  );

  find(slug: string | null): Workspace | undefined {
    return slug ? this.workspaces().find((w) => w.slug === slug) : undefined;
  }

  reload(): void {
    this.listResource.reload();
  }

  /** Creates a workspace and adds it to the list. Rejects with an `ApiError` (e.g. `slug_taken`). */
  async create(request: CreateWorkspaceRequest): Promise<Workspace> {
    const created = await firstValueFrom(this.api.post('/workspaces', { body: request }));
    this.upsert(created);
    return created;
  }

  /** Replaces (or adds) one workspace after the server returned its new state. */
  upsert(workspace: Workspace): void {
    const others = this.workspaces().filter((w) => w.id !== workspace.id);
    this.listResource.set([...others, workspace].sort(byName));
  }
}
