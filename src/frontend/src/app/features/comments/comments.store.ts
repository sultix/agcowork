import {
  DestroyRef,
  Injectable,
  InjectionToken,
  Signal,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError } from '../../core/api/api-error';
import type { components } from '../../core/api/generated/schema';
import { SessionStore } from '../../core/auth/session.store';
import { liveReload } from '../../core/realtime/live-reload';
import { ActivityEvent } from '../../core/realtime/realtime.service';
import { newUuid } from '../../core/util/uuid';
import { MembersStore } from '../../core/workspace/members.store';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { WorkspacesStore } from '../workspaces/workspaces.store';
import { CommentAction, commentErrorMessages } from './comment-errors';

export type Comment = components['schemas']['CommentDto'];
export type CommentThreads = components['schemas']['CommentThreadsDto'];

/** Session storage key of the sidebar's open state (one value per browser tab). */
export const COMMENTS_OPEN_KEY = 'ag-comments-open';

/** How often relative times ("vor 5 Minuten") are refreshed. */
export const COMMENTS_CLOCK_MS = new InjectionToken<number>('COMMENTS_CLOCK_MS', {
  providedIn: 'root',
  factory: () => 60_000,
});

/** Draft of a new thread. */
export const NEW_THREAD = 'new';
/** Draft of a reply to the thread of `rootId`. */
export const replyKey = (rootId: string) => `reply:${rootId}`;
/** Draft of an edit of `commentId`. */
export const editKey = (commentId: string) => `edit:${commentId}`;
/** Error slot of the actions (delete, resolve) on `commentId`. */
export const actionKey = (commentId: string) => `action:${commentId}`;

/** What the store needs from the page that shows the record. */
export interface CommentsSource {
  /** The open record; `null` while there is none. */
  readonly recordId: () => string | null | undefined;
  /** Whether the record accepts comments (write scope, not archived). */
  readonly writable: () => boolean;
}

/** `recordId` of a `comment.*` event's diff. */
function recordIdOf(event: ActivityEvent): string | null {
  const diff = event.diff as unknown as Record<string, unknown> | null;
  const id = diff?.['recordId'];
  return typeof id === 'string' ? id : null;
}

/** Every comment of the threads (roots and replies). */
function flatten(threads: readonly Comment[]): Comment[] {
  return threads.flatMap((root) => [root, ...(root.replies ?? [])]);
}

/**
 * The discussion of one record for the comment sidebar: threads (`GET …/comments`), the drafts
 * of new threads, replies and edits, and the writes (comment, reply, edit, delete, resolve).
 * Provided by the record document.
 *
 * Live: `comment.*` events of the record (by `diff.recordId`) and resyncs reload the threads,
 * bundled like {@link liveReload}. Drafts live here, keyed by their target, so reloading never
 * touches what is being typed, and reloads never wait for a draft. Two guards keep data from
 * going backwards or drafts from losing their place (the lock and generation of `liveRefresh`):
 * a reload waits while an own write runs, and threads fetched before a write began are thrown
 * away and fetched again; a reload that would remove the comment an open reply or edit draft
 * belongs to (deleted elsewhere) is held back until that draft is sent or discarded.
 */
@Injectable()
export class CommentsStore {
  private readonly api = inject(ApiClient);
  private readonly context = inject(WorkspaceContext);
  private readonly session = inject(SessionStore);
  private readonly workspaces = inject(WorkspacesStore);
  private readonly members = inject(MembersStore);

  private source: CommentsSource = { recordId: () => null, writable: () => false };
  private readonly sourceVersion = signal(0);

  private readonly state = signal<CommentThreads | null>(null);
  readonly loadError = signal<string | null>(null);
  /** The threads (roots with replies), in the server's order. */
  readonly threads: Signal<readonly Comment[]> = computed(() => this.state()?.threads ?? []);
  readonly omittedThreads = computed(() => Number(this.state()?.omittedThreads ?? 0));
  readonly loaded = computed(() => this.state() !== null);
  /** Threads not resolved (the counter at the sidebar toggle). */
  readonly openCount = computed(() => this.threads().filter((t) => !t.resolvedAt).length);

  /** Sidebar shown; remembered per browser tab. */
  readonly open = signal(readOpen());
  /** Resolved threads listed (collapsed). */
  readonly showResolved = signal(false);
  /** Resolved threads the user expanded. */
  readonly expanded = signal<ReadonlySet<string>>(new Set());
  /** Comment to highlight (from `?comment=`). */
  readonly highlightId = signal<string | null>(null);
  /** Comment waiting for the delete confirmation. */
  readonly deleting = signal<Comment | null>(null);
  /** Text for the live region: who commented since the last reload. */
  readonly announcement = signal('');
  /** Grows every minute; relative times read it. */
  readonly now = signal(Date.now());

  private readonly drafts = signal<ReadonlyMap<string, string>>(new Map());
  private readonly errors = signal<ReadonlyMap<string, readonly string[]>>(new Map());
  private readonly busyKeys = signal<ReadonlySet<string>>(new Set());
  /** Idempotency key per draft, bound to the text it was made for. */
  private readonly idempotencyKeys = new Map<
    string,
    { readonly text: string; readonly key: string }
  >();

  /** Own writes running; reloads wait for them. */
  private readonly writing = signal(0);
  /** Grows with every write that starts; a reload fetched before it is outdated. */
  private generation = 0;
  /** Drafts whose comment the last fetched reload would have removed; it waits for them. */
  private readonly heldFor = signal<ReadonlySet<string>>(new Set());
  private readonly pending = signal(false);
  private running = false;
  /** Grows with every record switch; answers of an older record are dropped. */
  private loadToken = 0;

  readonly me = computed(() => this.session.me()?.id ?? null);
  readonly isAdmin = computed(
    () => !!this.workspaces.find(this.context.slug())?.scopes.includes('admin'),
  );
  readonly writable = computed(() => {
    this.sourceVersion();
    return this.source.writable();
  });
  readonly recordId = computed(() => {
    this.sourceVersion();
    return this.source.recordId() ?? null;
  });

  /** A reload waits: an own write runs, or it would remove the target of an open draft. */
  private readonly blocked = computed(() => {
    if (this.writing() > 0) {
      return true;
    }
    const drafts = this.drafts();
    return [...this.heldFor()].some((key) => drafts.has(key));
  });
  /** The live view is held back for a draft (hint in the sidebar). */
  readonly heldBack = computed(() => {
    const drafts = this.drafts();
    return [...this.heldFor()].some((key) => drafts.has(key));
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    const clock = setInterval(() => this.now.set(Date.now()), inject(COMMENTS_CLOCK_MS));
    destroyRef.onDestroy(() => clearInterval(clock));

    effect(() => {
      const slug = this.context.slug();
      const recordId = this.recordId();
      untracked(() => this.reset(slug ?? null, recordId));
    });

    liveReload({
      slug: () => (this.recordId() ? this.context.slug() : null),
      matches: (event) =>
        event.targetType === 'comment' &&
        event.type.startsWith('comment.') &&
        recordIdOf(event) === this.recordId(),
      reload: () => this.pending.set(true),
    });

    effect(() => {
      if (this.pending() && !this.blocked()) {
        untracked(() => void this.refresh());
      }
    });
  }

  /** Connects the store to the page (call in the page's constructor). */
  bind(source: CommentsSource): void {
    this.source = source;
    this.sourceVersion.update((v) => v + 1);
  }

  setOpen(open: boolean): void {
    this.open.set(open);
    try {
      if (open) {
        sessionStorage.setItem(COMMENTS_OPEN_KEY, '1');
      } else {
        sessionStorage.removeItem(COMMENTS_OPEN_KEY);
      }
    } catch {
      // storage unavailable: the state lasts for this page only
    }
  }

  // --- permissions --------------------------------------------------------------------------

  canEdit(comment: Comment): boolean {
    return this.writable() && !comment.deletedAt && comment.authorId === this.me();
  }

  canDelete(comment: Comment): boolean {
    return (
      this.writable() && !comment.deletedAt && (comment.authorId === this.me() || this.isAdmin())
    );
  }

  canResolve(comment: Comment): boolean {
    return this.writable() && !comment.deletedAt && comment.parentCommentId === null;
  }

  canReply(root: Comment): boolean {
    return this.writable() && !root.deletedAt;
  }

  // --- drafts -------------------------------------------------------------------------------

  draft(key: string): string {
    return this.drafts().get(key) ?? '';
  }

  isOpen(key: string): boolean {
    return this.drafts().has(key);
  }

  /** Opens a reply or edit draft (keeps the text when it is open already). */
  openDraft(key: string, initial = ''): void {
    if (!this.drafts().has(key)) {
      this.setDraft(key, initial);
    }
  }

  setDraft(key: string, text: string): void {
    this.drafts.update((map) => new Map(map).set(key, text));
  }

  /** Discards a draft and its error. */
  closeDraft(key: string): void {
    this.drafts.update((map) => {
      const next = new Map(map);
      next.delete(key);
      return next;
    });
    this.clearError(key);
  }

  /**
   * Text typed and not sent: a non-empty new-thread or reply draft, or an edit that differs
   * from the comment. Leaving the page asks before it is lost.
   */
  hasUnsentDrafts(): boolean {
    const byId = new Map(flatten(this.threads()).map((c) => [c.id, c] as const));
    for (const [key, text] of this.drafts()) {
      if (key.startsWith('edit:')) {
        if (text !== (byId.get(key.slice('edit:'.length))?.markdown ?? '')) {
          return true;
        }
      } else if (text.trim() !== '') {
        return true;
      }
    }
    return false;
  }

  /** An open reply draft of the thread or an edit draft of one of its comments. */
  hasDraftIn(root: Comment): boolean {
    const drafts = this.drafts();
    return (
      drafts.has(replyKey(root.id)) ||
      drafts.has(editKey(root.id)) ||
      (root.replies ?? []).some((r) => drafts.has(editKey(r.id)))
    );
  }

  errorsOf(key: string): readonly string[] {
    return this.errors().get(key) ?? [];
  }

  isBusy(key: string): boolean {
    return this.busyKeys().has(key);
  }

  toggleExpanded(rootId: string): void {
    this.expanded.update((set) => {
      const next = new Set(set);
      if (!next.delete(rootId)) {
        next.add(rootId);
      }
      return next;
    });
  }

  /** Shows the thread that contains `commentId` (also when resolved); its root, or `null`. */
  reveal(commentId: string): Comment | null {
    const root = this.threads().find(
      (t) => t.id === commentId || (t.replies ?? []).some((r) => r.id === commentId),
    );
    if (root?.resolvedAt) {
      this.showResolved.set(true);
      this.expanded.update((set) => new Set(set).add(root.id));
    }
    return root ?? null;
  }

  // --- writes -------------------------------------------------------------------------------

  /** Sends the draft `key` as a new thread or (with `parent`) as a reply. */
  async send(key: string, parent: Comment | null): Promise<boolean> {
    const slug = this.context.slug();
    const recordId = this.recordId();
    const markdown = this.draft(key);
    if (!slug || !recordId || markdown.trim() === '') {
      return false;
    }
    const idempotencyKey = this.idempotencyKeyFor(key, markdown);
    const created = await this.write(key, parent ? 'reply' : 'add', markdown, () =>
      firstValueFrom(
        this.api.workspace(slug).post('/records/{recordId}/comments', {
          path: { recordId },
          headers: { 'Idempotency-Key': idempotencyKey },
          body: { markdown, ...(parent ? { parentCommentId: parent.id } : {}) },
        }),
      ),
    );
    if (!created) {
      return false;
    }
    this.idempotencyKeys.delete(key);
    this.patch((threads) => addComment(threads, created));
    if (key === NEW_THREAD) {
      this.setDraft(key, '');
    } else {
      this.closeDraft(key);
    }
    return true;
  }

  /** Saves the edit draft of `comment`. */
  async saveEdit(comment: Comment): Promise<boolean> {
    const key = editKey(comment.id);
    const slug = this.context.slug();
    const markdown = this.draft(key);
    if (!slug || markdown.trim() === '') {
      return false;
    }
    const updated = await this.write(key, 'edit', markdown, () =>
      firstValueFrom(
        this.api.workspace(slug).patch('/comments/{commentId}', {
          path: { commentId: comment.id },
          body: { markdown },
        }),
      ),
    );
    if (!updated) {
      return false;
    }
    this.patch((threads) => replaceComment(threads, updated));
    this.closeDraft(key);
    return true;
  }

  /** Resolves or reopens the thread of `root`. */
  async setResolved(root: Comment, resolved: boolean): Promise<boolean> {
    const slug = this.context.slug();
    if (!slug) {
      return false;
    }
    const path = { commentId: root.id };
    const updated = await this.write(actionKey(root.id), 'resolve', '', () =>
      firstValueFrom(
        resolved
          ? this.api.workspace(slug).post('/comments/{commentId}/resolve', { path })
          : this.api.workspace(slug).post('/comments/{commentId}/reopen', { path }),
      ),
    );
    if (!updated) {
      return false;
    }
    this.patch((threads) => replaceComment(threads, updated));
    if (resolved) {
      this.expanded.update((set) => {
        const next = new Set(set);
        next.delete(root.id);
        return next;
      });
    }
    return true;
  }

  /** Deletes `comment` (after the confirmation). */
  async remove(comment: Comment): Promise<boolean> {
    const slug = this.context.slug();
    if (!slug) {
      return false;
    }
    const done = await this.write(actionKey(comment.id), 'delete', '', async () => {
      await firstValueFrom(
        this.api.workspace(slug).delete('/comments/{commentId}', {
          path: { commentId: comment.id },
        }),
        { defaultValue: undefined },
      );
      return true as const;
    });
    if (!done) {
      return false;
    }
    this.patch((threads) => removeComment(threads, comment, new Date(Date.now()).toISOString()));
    this.closeDraft(editKey(comment.id));
    if (comment.parentCommentId === null) {
      // A deleted root takes no replies (409 comment_deleted); its reply field goes with it.
      this.closeDraft(replyKey(comment.id));
    }
    return true;
  }

  /** Loads the threads again (after a load error). */
  retry(): void {
    this.reset(this.context.slug() ?? null, this.recordId());
  }

  // --- internals ----------------------------------------------------------------------------

  /**
   * One key per intention: the draft (its key names the target, new thread or the thread replied
   * to) and its text. A retry of the same text after a lost answer returns the first comment
   * instead of a second. A changed text is a new intention with a new key: the server answers a
   * known key with the first comment and would drop the change silently. Success starts anew.
   */
  private idempotencyKeyFor(key: string, text: string): string {
    const known = this.idempotencyKeys.get(key);
    if (known && known.text === text) {
      return known.key;
    }
    const value = newUuid();
    this.idempotencyKeys.set(key, { text, key: value });
    return value;
  }

  private async write<T>(
    key: string,
    action: CommentAction,
    markdown: string,
    run: () => Promise<T>,
  ): Promise<T | null> {
    if (this.busyKeys().has(key)) {
      return null;
    }
    this.generation++;
    this.writing.update((n) => n + 1);
    this.busyKeys.update((set) => new Set(set).add(key));
    this.clearError(key);
    const token = this.loadToken;
    try {
      const result = await run();
      return token === this.loadToken ? result : null;
    } catch (error) {
      if (ApiError.from(error).code === 'idempotency_key_reused') {
        this.idempotencyKeys.delete(key);
      }
      if (token === this.loadToken) {
        this.errors.update((map) =>
          new Map(map).set(key, commentErrorMessages(error, action, markdown)),
        );
      }
      return null;
    } finally {
      this.busyKeys.update((set) => {
        const next = new Set(set);
        next.delete(key);
        return next;
      });
      this.writing.update((n) => n - 1);
    }
  }

  private clearError(key: string): void {
    if (this.errors().has(key)) {
      this.errors.update((map) => {
        const next = new Map(map);
        next.delete(key);
        return next;
      });
    }
  }

  private patch(change: (threads: readonly Comment[]) => Comment[]): void {
    this.state.update((state) =>
      state
        ? { ...state, threads: change(state.threads) }
        : { threads: change([]), omittedThreads: 0 },
    );
  }

  /** A new record (or workspace): everything starts over. */
  private reset(slug: string | null, recordId: string | null): void {
    const token = ++this.loadToken;
    this.generation++;
    this.state.set(null);
    this.loadError.set(null);
    this.drafts.set(new Map());
    this.errors.set(new Map());
    this.heldFor.set(new Set());
    this.expanded.set(new Set());
    this.showResolved.set(false);
    this.deleting.set(null);
    this.announcement.set('');
    this.idempotencyKeys.clear();
    this.pending.set(false);
    if (!slug || !recordId) {
      return;
    }
    void this.fetch(slug, recordId).then(
      (threads) => {
        if (token === this.loadToken) {
          this.state.set(threads);
        }
      },
      (error: unknown) => {
        if (token === this.loadToken) {
          this.loadError.set(commentErrorMessages(error, 'add')[0]);
        }
      },
    );
  }

  private fetch(slug: string, recordId: string): Promise<CommentThreads> {
    return firstValueFrom(
      this.api.workspace(slug).get('/records/{recordId}/comments', { path: { recordId } }),
    );
  }

  /** One live reload; see the class comment for when its answer is applied. */
  private async refresh(): Promise<void> {
    if (this.running) {
      return;
    }
    const slug = this.context.slug();
    const recordId = this.recordId();
    if (!slug || !recordId) {
      this.pending.set(false);
      return;
    }
    this.running = true;
    this.pending.set(false);
    const token = this.loadToken;
    const generation = this.generation;
    try {
      const fetched = await this.fetch(slug, recordId);
      if (token !== this.loadToken) {
        return;
      }
      if (untracked(this.blocked) || generation !== this.generation) {
        this.pending.set(true); // a write began meanwhile: once more after it
        return;
      }
      const dropped = this.droppedDrafts(fetched);
      if (dropped.size > 0) {
        this.heldFor.set(dropped);
        this.pending.set(true);
        return;
      }
      this.heldFor.set(new Set());
      this.announce(fetched);
      this.state.set(fetched);
      this.loadError.set(null);
    } catch {
      // keep what is shown; the next event or resync tries again
    } finally {
      this.running = false;
      if (untracked(this.pending) && !untracked(this.blocked)) {
        void this.refresh();
      }
    }
  }

  /**
   * Open reply and edit drafts whose comment the fetched threads no longer have. Only drafts
   * whose comment is shown now count: a draft without a visible field could never be closed,
   * and holding back for it would stop the live view for good.
   */
  private droppedDrafts(fetched: CommentThreads): Set<string> {
    const shownRoots = new Set(untracked(this.threads).map((t) => t.id));
    const shown = new Set(flatten(untracked(this.threads)).map((c) => c.id));
    const roots = new Set(fetched.threads.map((t) => t.id));
    const editable = new Set(
      flatten(fetched.threads)
        .filter((c) => !c.deletedAt)
        .map((c) => c.id),
    );
    const dropped = new Set<string>();
    for (const key of untracked(this.drafts).keys()) {
      if (key.startsWith('reply:')) {
        const id = key.slice('reply:'.length);
        if (shownRoots.has(id) && !roots.has(id)) {
          dropped.add(key);
        }
      } else if (key.startsWith('edit:')) {
        const id = key.slice('edit:'.length);
        if (shown.has(id) && !editable.has(id)) {
          dropped.add(key);
        }
      }
    }
    return dropped;
  }

  /** Puts new comments of others into the live region. */
  private announce(fetched: CommentThreads): void {
    const known = new Set(flatten(untracked(this.threads)).map((c) => c.id));
    const me = untracked(this.me);
    const added = flatten(fetched.threads).filter(
      (c) => !known.has(c.id) && !c.deletedAt && c.authorId !== me,
    );
    if (added.length === 1) {
      const comment = added[0];
      const name = comment.authorName || this.members.nameOf(comment.authorId);
      this.say(
        comment.parentCommentId ? `Neue Antwort von ${name}` : `Neuer Kommentar von ${name}`,
      );
    } else if (added.length > 1) {
      this.say(`${added.length} neue Kommentare`);
    }
  }

  /**
   * Sets the live region text. The same sentence twice in a row gets a trailing no-break space
   * the second time: screen readers only announce a change of the text.
   */
  private say(text: string): void {
    const previous = untracked(this.announcement);
    this.announcement.set(previous === text ? `${text}\u00a0` : text);
  }
}

function readOpen(): boolean {
  try {
    return sessionStorage.getItem(COMMENTS_OPEN_KEY) === '1';
  } catch {
    return false;
  }
}

function addComment(threads: readonly Comment[], created: Comment): Comment[] {
  if (flatten(threads).some((c) => c.id === created.id)) {
    return [...threads];
  }
  if (created.parentCommentId === null) {
    return [...threads, { ...created, replies: [], omittedReplies: 0 }];
  }
  return threads.map((root) =>
    root.id === created.parentCommentId
      ? { ...root, replies: [...(root.replies ?? []), created] }
      : root,
  );
}

function replaceComment(threads: readonly Comment[], updated: Comment): Comment[] {
  return threads.map((root) => {
    if (root.id === updated.id) {
      return { ...updated, replies: root.replies, omittedReplies: root.omittedReplies };
    }
    if (updated.parentCommentId === root.id) {
      return {
        ...root,
        replies: (root.replies ?? []).map((r) => (r.id === updated.id ? updated : r)),
      };
    }
    return root;
  });
}

function removeComment(threads: readonly Comment[], removed: Comment, at: string): Comment[] {
  if (removed.parentCommentId !== null) {
    return threads.map((root) =>
      root.id === removed.parentCommentId
        ? { ...root, replies: (root.replies ?? []).filter((r) => r.id !== removed.id) }
        : root,
    );
  }
  return threads.flatMap((root) => {
    if (root.id !== removed.id) {
      return [root];
    }
    // A root with replies stays as placeholder, like the server lists it.
    return (root.replies ?? []).length > 0 ? [{ ...root, markdown: null, deletedAt: at }] : [];
  });
}
