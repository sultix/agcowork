import { Injectable, Signal, computed, inject, resource } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../api/api-client';
import type { components } from '../api/generated/schema';
import { SessionStore } from '../auth/session.store';
import { liveReload } from '../realtime/live-reload';
import { WorkspaceContext } from './workspace-context.service';

export type Member = components['schemas']['MemberDto'];

/**
 * Members of the active workspace (`GET /members`): the choices of actor properties and the
 * names behind `createdBy`/`updatedBy` ids. Reloads when the workspace changes, when members are
 * added, changed or removed elsewhere (`member.*` events, bundled) and after a resync.
 */
@Injectable({ providedIn: 'root' })
export class MembersStore {
  private readonly api = inject(ApiClient);
  private readonly context = inject(WorkspaceContext);
  private readonly session = inject(SessionStore);

  private readonly slug = computed(
    () => (this.session.status() === 'authenticated' ? this.context.slug() : null) ?? undefined,
  );

  private readonly listResource = resource<Member[], string | undefined>({
    params: () => this.slug(),
    loader: ({ params: slug }) => firstValueFrom(this.api.workspace(slug).get('/members')),
  });

  /** Grows with every live fetch; only the latest one's answer is applied. */
  private generation = 0;

  readonly members: Signal<readonly Member[]> = computed(() =>
    this.listResource.hasValue() ? this.listResource.value() : [],
  );

  constructor() {
    liveReload({
      slug: () => this.slug(),
      matches: (event) => event.targetType === 'member',
      reload: () => void this.refetch(),
    });
  }

  /**
   * Fetches the list again and shows it once it is there. Unlike a resource reload, a failure
   * keeps the names shown; the next event or resync tries again.
   */
  private async refetch(): Promise<void> {
    const slug = this.slug();
    if (!slug) {
      return;
    }
    const generation = ++this.generation;
    try {
      const list = await firstValueFrom(this.api.workspace(slug).get('/members'));
      if (this.slug() === slug && generation === this.generation) {
        this.listResource.set(list);
      }
    } catch {
      // keep what is shown
    }
  }

  /** Kind (`human` or `agent`) of an actor; `null` while the member is unknown. */
  kindOf(actorId: string | null | undefined): string | null {
    return this.members().find((m) => m.actorId === actorId)?.kind ?? null;
  }

  /** Display name of an actor; the id itself while the member is unknown (or not loaded yet). */
  nameOf(actorId: string | null | undefined): string {
    if (!actorId) {
      return '';
    }
    return this.members().find((m) => m.actorId === actorId)?.name ?? actorId.slice(0, 8);
  }
}
