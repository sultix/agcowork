import { Injectable, Signal, computed, inject, resource } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../api/api-client';
import type { components } from '../api/generated/schema';
import { SessionStore } from '../auth/session.store';
import { WorkspaceContext } from './workspace-context.service';

export type Member = components['schemas']['MemberDto'];

/**
 * Members of the active workspace (`GET /members`): the choices of actor properties and the
 * names behind `createdBy`/`updatedBy` ids. Reloads when the workspace changes.
 */
@Injectable({ providedIn: 'root' })
export class MembersStore {
  private readonly api = inject(ApiClient);
  private readonly context = inject(WorkspaceContext);
  private readonly session = inject(SessionStore);

  private readonly listResource = resource<Member[], string | undefined>({
    params: () =>
      (this.session.status() === 'authenticated' ? this.context.slug() : null) ?? undefined,
    loader: ({ params: slug }) => firstValueFrom(this.api.workspace(slug).get('/members')),
  });

  readonly members: Signal<readonly Member[]> = computed(() =>
    this.listResource.hasValue() ? this.listResource.value() : [],
  );

  /** Display name of an actor; the id itself while the member is unknown (or not loaded yet). */
  nameOf(actorId: string | null | undefined): string {
    if (!actorId) {
      return '';
    }
    return this.members().find((m) => m.actorId === actorId)?.name ?? actorId.slice(0, 8);
  }
}
