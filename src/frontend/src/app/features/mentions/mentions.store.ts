import { Injectable, Signal, computed, inject, resource } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import type { components } from '../../core/api/generated/schema';
import { SessionStore } from '../../core/auth/session.store';
import { liveReload } from '../../core/realtime/live-reload';
import type { ActivityEvent } from '../../core/realtime/realtime.service';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';

export type Mention = components['schemas']['MentionDto'];
export type MentionPage = components['schemas']['MentionPage'];
export type MarkReadResult = components['schemas']['MarkMentionsReadResult'];

/** Largest count shown as a number in the badge. */
export const BADGE_LIMIT = 99;

/** Badge text for `count` unread mentions: the number, "99+" above the limit. */
export function badgeLabel(count: number): string {
  return count > BADGE_LIMIT ? `${BADGE_LIMIT}+` : String(count);
}

/**
 * Whether a live event hands `actorId` a new mention: a `comment.*` event whose
 * `diff.mentionedActorIds` (on created: all mentioned; on updated: the newly mentioned) contains it.
 */
export function mentionsActor(event: ActivityEvent, actorId: string | undefined): boolean {
  if (!actorId || !event.type.startsWith('comment.')) {
    return false;
  }
  const ids = (event.diff as Record<string, unknown> | null)?.['mentionedActorIds'];
  return Array.isArray(ids) && ids.includes(actorId);
}

/**
 * Whether a live event may change the caller's inbox, so the count and list are read again:
 * a new mention of the caller (`comment.created`/`comment.updated` with the own id), or an event
 * that can remove mentions without naming anyone (`comment.deleted` carries no ids, and
 * `comment.updated` lists only the newly mentioned, not the removed ones).
 */
export function mayChangeMentions(event: ActivityEvent, actorId: string | undefined): boolean {
  return event.type === 'comment.deleted' || event.type === 'comment.updated'
    ? true
    : mentionsActor(event, actorId);
}

/**
 * The unread-mention count of the caller in the active workspace (for the badge in the shell),
 * and the "mark read" calls of the inbox. Reloads when the workspace changes, when a live event
 * mentions the caller (bundled) and after a resync.
 */
@Injectable({ providedIn: 'root' })
export class MentionsStore {
  private readonly api = inject(ApiClient);
  private readonly context = inject(WorkspaceContext);
  private readonly session = inject(SessionStore);

  private readonly slug = computed(
    () => (this.session.status() === 'authenticated' ? this.context.slug() : null) ?? undefined,
  );

  /** `limit=1`: only the `unreadCount` of the answer is used. */
  private readonly countResource = resource<number, string | undefined>({
    params: () => this.slug(),
    loader: async ({ params: slug }) => {
      const page = await firstValueFrom(
        this.api.workspace(slug).get('/mentions', { query: { unreadOnly: true, limit: 1 } }),
      );
      return Number(page.unreadCount);
    },
  });

  /** Grows with every live fetch; only the latest one's answer is applied. */
  private generation = 0;

  /** Unread mentions of the caller; 0 while unknown (or when loading failed). */
  readonly unreadCount: Signal<number> = computed(() =>
    this.countResource.hasValue() ? this.countResource.value() : 0,
  );

  constructor() {
    liveReload({
      slug: () => this.slug(),
      matches: (event) => mayChangeMentions(event, this.session.me()?.id),
      reload: () => void this.refetch(),
    });
  }

  /** Takes the unread count of a fresher answer (e.g. the list); older pending answers lose. */
  accept(count: number): void {
    if (this.slug()) {
      ++this.generation;
      this.countResource.set(count);
    }
  }

  /** Marks the given mentions read; the badge follows the answer. */
  async markRead(ids: readonly string[]): Promise<MarkReadResult | null> {
    return this.mark({ ids: [...ids] });
  }

  /** Marks all unread mentions of the workspace read. */
  async markAllRead(): Promise<MarkReadResult | null> {
    return this.mark({ all: true });
  }

  /** Fetches the count again; a failure keeps the number shown. */
  async refetch(): Promise<void> {
    const slug = this.slug();
    if (!slug) {
      return;
    }
    const generation = ++this.generation;
    try {
      const page = await firstValueFrom(
        this.api.workspace(slug).get('/mentions', { query: { unreadOnly: true, limit: 1 } }),
      );
      if (this.slug() === slug && generation === this.generation) {
        this.countResource.set(Number(page.unreadCount));
      }
    } catch {
      // Keep the number; the next event or resync tries again.
    }
  }

  private async mark(body: components['schemas']['MarkMentionsReadRequest']) {
    const slug = this.slug();
    if (!slug) {
      return null;
    }
    // Claimed before sending: an older answer (count fetch or mark) must not overwrite a newer one.
    const generation = ++this.generation;
    const result = await firstValueFrom(this.api.workspace(slug).post('/mentions/read', { body }));
    if (this.slug() === slug && generation === this.generation) {
      this.countResource.set(Number(result.unreadCount));
    }
    return result;
  }
}
