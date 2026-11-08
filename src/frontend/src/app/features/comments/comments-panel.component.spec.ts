import { Component, inject, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import { LIVE_RELOAD_DELAY_MS, LIVE_RELOAD_MAX_WAIT_MS } from '../../core/realtime/live-reload';
import { FakeRealtime, provideFakeRealtime } from '../../core/testing/fake-hub';
import {
  ADMIN_ME,
  AGENT_ID,
  API,
  RECORD_ID,
  activityEvent,
  expectRequest,
  member,
  pageProviders,
  respond,
  settle,
  workspace,
} from '../../core/testing/test-utils';
import { COMMENT_RENDER_BUDGET_MS } from './comment-renderer';
import { CommentsPanelComponent } from './comments-panel.component';
import { Comment, CommentThreads, CommentsStore } from './comments.store';

const DELAY = 20;
const MIA_ID = '0199a000-0000-7000-8000-0000000000f2';
const COMMENTS_URL = `${API}/records/${RECORD_ID}/comments`;
const minutesAgo = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();

function comment(overrides: Partial<Comment> = {}): Comment {
  return {
    id: 'c1',
    recordId: RECORD_ID,
    parentCommentId: null,
    authorId: ADMIN_ME.id,
    authorName: 'Admin',
    authorKind: 'human',
    markdown: 'Hallo',
    createdAt: minutesAgo(5),
    editedAt: null,
    resolvedAt: null,
    resolvedBy: null,
    deletedAt: null,
    ...overrides,
  };
}

function root(id: string, overrides: Partial<Comment> = {}, replies: Comment[] = []): Comment {
  return comment({ id, ...overrides, replies, omittedReplies: overrides.omittedReplies ?? 0 });
}

function reply(id: string, rootId: string, overrides: Partial<Comment> = {}): Comment {
  return comment({ id, parentCommentId: rootId, ...overrides });
}

const threads = (list: Comment[], omittedThreads = 0): CommentThreads => ({
  threads: list,
  omittedThreads,
});

/** A comment of the agent on the open record (as the hub delivers it). */
const commentEvent = (seq: number, commentId: string, recordId = RECORD_ID) =>
  activityEvent({
    seq,
    type: 'comment.created',
    targetType: 'comment',
    targetId: commentId,
    actorId: AGENT_ID,
    diff: { recordId, parentCommentId: null, mentionedActorIds: [] },
  });

@Component({
  imports: [CommentsPanelComponent],
  providers: [CommentsStore],
  template: `<ag-comments-panel [focusCommentId]="focus()" />`,
})
class HostComponent {
  static writable = signal(true);
  readonly store = inject(CommentsStore);
  readonly focus = signal<string | null>(null);

  constructor() {
    this.store.bind({ recordId: () => RECORD_ID, writable: () => HostComponent.writable() });
  }
}

describe('CommentsPanelComponent', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;
  let realtime: FakeRealtime;

  async function render(
    initial: CommentThreads,
    options: { scopes?: string[]; writable?: boolean; budgetMs?: number } = {},
  ): Promise<void> {
    realtime = new FakeRealtime();
    HostComponent.writable.set(options.writable ?? true);
    TestBed.configureTestingModule({
      providers: [
        provideFakeRealtime(realtime),
        ...pageProviders({
          ws: workspace({ scopes: options.scopes ?? ['read', 'write'] }),
          routes: [{ path: 'w/:workspaceSlug/x', component: HostComponent }],
        }),
        { provide: LIVE_RELOAD_DELAY_MS, useValue: DELAY },
        { provide: LIVE_RELOAD_MAX_WAIT_MS, useValue: DELAY * 4 },
        ...(options.budgetMs === undefined
          ? []
          : [{ provide: COMMENT_RENDER_BUDGET_MS, useValue: options.budgetMs }]),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/w/demo/x');
    await respond(http, 'GET', COMMENTS_URL, initial);
    await respond(http, 'GET', `${API}/members`, [
      member(),
      member({ actorId: AGENT_ID, name: 'Coder', kind: 'agent' }),
      member({ actorId: MIA_ID, name: 'Mia', kind: 'human' }),
    ]);
    await settle(harness.fixture);
  }

  afterEach(() => {
    http.verify();
    sessionStorage.clear();
  });

  const host = () => harness.routeDebugElement!.componentInstance as HostComponent;
  const el = () => harness.routeNativeElement as HTMLElement;
  // Without a scope, overlays (menu, confirm dialog) are searched in <body> as well.
  const q = <T extends HTMLElement = HTMLElement>(id: string, scope?: ParentNode) =>
    scope
      ? scope.querySelector<T>(`[data-testid="${id}"]`)
      : (el().querySelector<T>(`[data-testid="${id}"]`) ??
        document.body.querySelector<T>(`[data-testid="${id}"]`));
  const all = (id: string, scope: ParentNode = el()) =>
    Array.from(scope.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`));
  const names = (options: HTMLElement[]) =>
    options.map((o) => o.querySelector('span')!.textContent!.trim());
  const text = (node: Element | null) => node?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const thread = (id: string) =>
    el().querySelector<HTMLElement>(`[data-testid="comment-thread"][data-thread-id="${id}"]`);
  const item = (id: string) =>
    el().querySelector<HTMLElement>(`[data-testid="comment"][data-comment-id="${id}"]`);
  const change = () => settle(harness.fixture);

  /** Opens the action menu of a comment (overlay in <body>). */
  async function openMenu(id: string): Promise<void> {
    q('comment-actions', item(id)!)!.click();
    await change();
  }

  function keydown(target: HTMLElement, key: string, init: KeyboardEventInit = {}): KeyboardEvent {
    const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init });
    target.dispatchEvent(event);
    return event;
  }

  /** Types `value` into a composer with the caret at its end. */
  async function typeComposer(field: HTMLTextAreaElement, value: string): Promise<void> {
    field.focus();
    field.value = value;
    field.setSelectionRange(value.length, value.length);
    field.dispatchEvent(new Event('input', { bubbles: true }));
    await change();
  }

  describe('rendering', () => {
    it('render_Threads_InServerOrderWithAllStates', async () => {
      await render(
        threads(
          [
            root('r1', { markdown: 'Erste Frage', editedAt: minutesAgo(1) }, [
              reply('a1', 'r1', {
                authorId: AGENT_ID,
                authorName: 'Coder',
                authorKind: 'agent',
                markdown: 'Antwort vom Agenten',
                createdAt: minutesAgo(2),
              }),
            ]),
            root('r2', { markdown: null, deletedAt: minutesAgo(3), omittedReplies: 3 }, [
              reply('a2', 'r2', { markdown: 'Bleibt stehen' }),
            ]),
            root('r3', { markdown: 'Erledigt', resolvedAt: minutesAgo(1), resolvedBy: MIA_ID }),
          ],
          2,
        ),
      );

      // resolved threads are hidden until "Erledigte anzeigen"
      expect(all('comment-thread').map((t) => t.dataset['threadId'])).toEqual(['r1', 'r2']);
      expect(text(q('omitted-threads'))).toBe('2 ältere Threads nicht angezeigt');

      const first = item('r1')!;
      expect(text(q('comment-author', first))).toBe('Admin');
      expect(text(q('comment-author-kind', first))).toBe('Mensch');
      expect(text(q('comment-edited', first))).toBe('(bearbeitet)');
      expect(text(q('comment-time', first))).toBe('vor 5 Minuten');
      expect(q('comment-time', first)!.getAttribute('title')).toMatch(
        /^\d\d\.\d\d\.\d{4}, \d\d:\d\d$/,
      );
      expect(text(q('comment-markdown', first))).toBe('Erste Frage');
      expect(text(q('comment-author-kind', item('a1')!))).toBe('Agent');

      expect(text(q('comment-placeholder', item('r2')!))).toBe('Dieser Kommentar wurde gelöscht.');
      expect(q('comment-markdown', item('r2')!)).toBeNull();
      expect(text(q('omitted-replies', thread('r2')!))).toBe('3 ältere Antworten nicht angezeigt');
      expect(text(q('comment-markdown', item('a2')!))).toBe('Bleibt stehen');

      q<HTMLInputElement>('show-resolved')!.click();
      await change();

      const resolved = thread('r3')!;
      expect(all('comment-thread').map((t) => t.dataset['threadId'])).toEqual(['r1', 'r2', 'r3']);
      expect(text(q('resolved-by', resolved))).toContain('Erledigt von Mia');
      expect(text(q('resolved-excerpt', resolved))).toBe('Erledigt');
      expect(item('r3')).toBeNull(); // collapsed
      const toggle = q('thread-toggle', resolved)!;
      expect(toggle.getAttribute('aria-expanded')).toBe('false');

      toggle.click();
      await change();

      expect(toggle.getAttribute('aria-expanded')).toBe('true');
      expect(text(q('comment-markdown', item('r3')!))).toBe('Erledigt');
    });

    it('render_RenderBudgetSpent_FurtherCommentsArePlainWithNotice', async () => {
      await render(
        threads([root('r1', { markdown: '**eins**' }), root('r2', { markdown: '**zwei**' })]),
        { budgetMs: 0 },
      );

      expect(q('comment-markdown', item('r1')!)!.querySelector('strong')).not.toBeNull();
      const plain = q('comment-markdown', item('r2')!)!;
      expect(plain.querySelector('strong')).toBeNull();
      expect(text(plain)).toBe('**zwei**');
      expect(q('comments-plain')!.getAttribute('role')).toBe('status');
    });

    it('render_Panel_IsANamedComplementaryLandmarkWithLiveRegion', async () => {
      await render(threads([]));

      const panel = el().querySelector('ag-comments-panel')!;
      expect(panel.getAttribute('role')).toBe('complementary');
      expect(panel.getAttribute('aria-label')).toBe('Kommentare');
      expect(q('comments-live')!.getAttribute('aria-live')).toBe('polite');
      expect(text(q('comments-empty'))).toContain('Noch keine Kommentare');
    });
  });

  describe('permissions', () => {
    const mixed = () =>
      threads([
        root('own', {}, [reply('foreign-reply', 'own', { authorId: MIA_ID, authorName: 'Mia' })]),
        root('foreign', { authorId: AGENT_ID, authorName: 'Coder', authorKind: 'agent' }),
      ]);

    it('actions_Writer_EditsAndDeletesOnlyOwn_ResolvesOnlyRoots', async () => {
      await render(mixed());

      expect(q('comment-actions', item('own')!)).not.toBeNull();
      expect(q('comment-actions', item('foreign')!)).toBeNull();
      expect(q('comment-actions', item('foreign-reply')!)).toBeNull();
      expect(q('comment-resolve', item('own')!)).not.toBeNull();
      expect(q('comment-resolve', item('foreign')!)).not.toBeNull();
      expect(q('comment-resolve', item('foreign-reply')!)).toBeNull();

      await openMenu('own');
      expect(q('comment-edit')).not.toBeNull();
      expect(q('comment-delete')).not.toBeNull();
    });

    it('actions_Admin_DeletesOthersButEditsOnlyOwn', async () => {
      await render(mixed(), { scopes: ['read', 'write', 'admin'] });

      await openMenu('foreign');

      expect(q('comment-delete')).not.toBeNull();
      expect(q('comment-edit')).toBeNull();
    });

    it('actions_ReadOnly_NoComposerNoActionsNoReply', async () => {
      await render(mixed(), { scopes: ['read'], writable: false });

      expect(all('comment-actions')).toEqual([]);
      expect(all('comment-resolve')).toEqual([]);
      expect(all('thread-reply')).toEqual([]);
      expect(q('comment-composer')).toBeNull();
      expect(q('comments-readonly')).not.toBeNull();
    });

    it('actions_DeletedRoot_CanNeitherBeAnsweredNorResolved', async () => {
      await render(
        threads([root('r', { markdown: null, deletedAt: minutesAgo(1) }, [reply('a', 'r')])]),
      );

      expect(q('thread-reply', thread('r')!)).toBeNull();
      expect(q('comment-resolve', item('r')!)).toBeNull();
      expect(q('comment-actions', item('r')!)).toBeNull();
    });
  });

  describe('writing', () => {
    it('send_CtrlEnter_PostsWithIdempotencyKey_AndShowsTheThread', async () => {
      await render(threads([]));
      const field = q<HTMLTextAreaElement>('composer-input')!;

      await typeComposer(field, 'Neue Frage');
      expect(keydown(field, 'Enter', { ctrlKey: true }).defaultPrevented).toBe(true);
      const post = await expectRequest(http, 'POST', COMMENTS_URL);
      expect(post.request.body).toEqual({ markdown: 'Neue Frage' });
      const key = post.request.headers.get('Idempotency-Key');
      expect(key).toMatch(/^[0-9a-f-]{36}$/);
      post.flush(comment({ id: 'n1', markdown: 'Neue Frage' }));
      await change();

      expect(text(q('comment-markdown', item('n1')!))).toBe('Neue Frage');
      expect(field.value).toBe('');
    });

    it('send_RetryAfterLostAnswer_SendsTheSameIdempotencyKey', async () => {
      await render(threads([]));
      const field = q<HTMLTextAreaElement>('composer-input')!;
      await typeComposer(field, 'Einmal bitte');

      q('composer-submit')!.click();
      const first = await expectRequest(http, 'POST', COMMENTS_URL);
      first.error(new ProgressEvent('error'));
      await change();
      expect(text(q('composer-errors'))).toContain('Server nicht erreichbar');
      expect(field.value).toBe('Einmal bitte');

      q('composer-submit')!.click();
      const second = await expectRequest(http, 'POST', COMMENTS_URL);
      expect(second.request.headers.get('Idempotency-Key')).toBe(
        first.request.headers.get('Idempotency-Key'),
      );
      second.flush(comment({ id: 'n1', markdown: 'Einmal bitte' }));
      await change();

      // the next comment is a new intention with a new key
      await typeComposer(field, 'Noch einer');
      q('composer-submit')!.click();
      const third = await expectRequest(http, 'POST', COMMENTS_URL);
      expect(third.request.headers.get('Idempotency-Key')).not.toBe(
        first.request.headers.get('Idempotency-Key'),
      );
      third.flush(comment({ id: 'n2', markdown: 'Noch einer' }));
      await change();
    });

    it('reply_Sent_AppearsUnderTheRootAndClosesTheField', async () => {
      await render(threads([root('r1')]));

      q('thread-reply', thread('r1')!)!.click();
      await change();
      const field = q<HTMLTextAreaElement>('composer-input', thread('r1')!)!;
      expect(document.activeElement).toBe(field);
      await typeComposer(field, 'Meine Antwort');
      q('composer-submit', thread('r1')!)!.click();
      const post = await expectRequest(http, 'POST', COMMENTS_URL);
      expect(post.request.body).toEqual({ markdown: 'Meine Antwort', parentCommentId: 'r1' });
      post.flush(reply('a1', 'r1', { markdown: 'Meine Antwort' }));
      await change();

      expect(text(q('comment-markdown', item('a1')!))).toBe('Meine Antwort');
      expect(q('composer-input', thread('r1')!)).toBeNull();
    });

    it('edit_Saved_PatchesTheTextAndShowsEdited', async () => {
      await render(threads([root('r1', { markdown: 'Alt' })]));

      await openMenu('r1');
      q('comment-edit')!.click();
      await change();
      const field = q<HTMLTextAreaElement>('composer-input', item('r1')!)!;
      expect(field.value).toBe('Alt');
      await typeComposer(field, 'Neu');
      q('composer-submit', item('r1')!)!.click();
      const patch = await expectRequest(http, 'PATCH', `${API}/comments/r1`);
      expect(patch.request.body).toEqual({ markdown: 'Neu' });
      patch.flush(comment({ id: 'r1', markdown: 'Neu', editedAt: new Date().toISOString() }));
      await change();

      expect(text(q('comment-markdown', item('r1')!))).toBe('Neu');
      expect(q('comment-edited', item('r1')!)).not.toBeNull();
    });

    it('delete_Confirmed_DeletesAfterConfirmation', async () => {
      await render(threads([root('r1'), root('r2')]));

      await openMenu('r1');
      q('comment-delete')!.click();
      await change();
      expect(q('confirm-dialog')).not.toBeNull();
      http.expectNone({ method: 'DELETE' });

      q('confirm-ok')!.click();
      (await expectRequest(http, 'DELETE', `${API}/comments/r1`)).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      await change();

      expect(thread('r1')).toBeNull();
      expect(thread('r2')).not.toBeNull();
      await vi.waitFor(() =>
        expect(document.body.querySelector('[data-testid="confirm-dialog"]')).toBeNull(),
      );
    });

    it('delete_Cancelled_SendsNothing', async () => {
      await render(threads([root('r1')]));

      await openMenu('r1');
      q('comment-delete')!.click();
      await change();
      q('confirm-cancel')!.click();
      await change();

      http.expectNone({ method: 'DELETE' });
      expect(thread('r1')).not.toBeNull();
    });

    it('delete_RootWithReplies_StaysAsPlaceholder', async () => {
      await render(threads([root('r1', {}, [reply('a1', 'r1', { authorId: MIA_ID })])]));

      await openMenu('r1');
      q('comment-delete')!.click();
      await change();
      q('confirm-ok')!.click();
      (await expectRequest(http, 'DELETE', `${API}/comments/r1`)).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      await change();

      expect(q('comment-placeholder', item('r1')!)).not.toBeNull();
      expect(item('a1')).not.toBeNull();
    });

    it('resolve_Root_PostsAndHidesTheThreadUntilResolvedAreShown', async () => {
      await render(threads([root('r1'), root('r2')]));
      expect(host().store.openCount()).toBe(2);

      q('comment-resolve', item('r1')!)!.click();
      (await expectRequest(http, 'POST', `${API}/comments/r1/resolve`)).flush(
        comment({ id: 'r1', resolvedAt: new Date().toISOString(), resolvedBy: ADMIN_ME.id }),
      );
      await change();

      expect(thread('r1')).toBeNull();
      expect(host().store.openCount()).toBe(1);

      q<HTMLInputElement>('show-resolved')!.click();
      await change();
      q('thread-toggle', thread('r1')!)!.click();
      await change();
      q('comment-resolve', item('r1')!)!.click();
      (await expectRequest(http, 'POST', `${API}/comments/r1/reopen`)).flush(comment({ id: 'r1' }));
      await change();

      expect(host().store.openCount()).toBe(2);
    });
  });

  describe('drafts', () => {
    it('delete_OwnRootWithOpenReplyDraft_LiveKeepsWorking', async () => {
      await render(threads([root('r1')]));
      q('thread-reply', thread('r1')!)!.click();
      await change();
      await typeComposer(q<HTMLTextAreaElement>('composer-input', thread('r1')!)!, 'Antwort');

      await openMenu('r1');
      q('comment-delete')!.click();
      await change();
      q('confirm-ok')!.click();
      (await expectRequest(http, 'DELETE', `${API}/comments/r1`)).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      await change();
      expect(thread('r1')).toBeNull();

      realtime.emit(commentEvent(1, 'agent-thread'));
      await respond(
        http,
        'GET',
        COMMENTS_URL,
        threads([root('agent-thread', { authorId: AGENT_ID, authorName: 'Coder' })]),
      );
      await change();

      expect(item('agent-thread')).not.toBeNull();
      expect(q('comments-held')).toBeNull();
      expect(host().store.hasUnsentDrafts()).toBe(false);
    });

    it('live_DraftWithoutVisibleComment_DoesNotHoldTheViewBack', async () => {
      // Defensive: a draft whose comment is not shown has no field to close it with.
      await render(threads([root('r1')]));
      host().store.openDraft('reply:ghost', 'verwaist');
      host().store.openDraft('edit:ghost-reply', 'verwaist');

      realtime.emit(commentEvent(1, 'r2'));
      await respond(http, 'GET', COMMENTS_URL, threads([root('r1'), root('r2')]));
      await change();

      expect(item('r2')).not.toBeNull();
      expect(q('comments-held')).toBeNull();
    });

    it('live_ThreadResolvedElsewhereWhileReplying_StaysOpenWithTheDraft', async () => {
      await render(threads([root('r1', { authorId: MIA_ID, authorName: 'Mia' })]));
      q('thread-reply', thread('r1')!)!.click();
      await change();
      const field = q<HTMLTextAreaElement>('composer-input', thread('r1')!)!;
      await typeComposer(field, 'Ich antworte gerade');

      realtime.emit(commentEvent(1, 'r1'));
      await respond(
        http,
        'GET',
        COMMENTS_URL,
        threads([
          root('r1', {
            authorId: MIA_ID,
            authorName: 'Mia',
            resolvedAt: minutesAgo(0),
            resolvedBy: AGENT_ID,
          }),
        ]),
      );
      await change();

      expect(q<HTMLInputElement>('show-resolved')!.checked).toBe(false);
      expect(text(q('resolved-by', thread('r1')!))).toContain('Erledigt von Coder');
      // collapsing would hide the draft: no toggle while it is open
      expect(q('thread-toggle', thread('r1')!)).toBeNull();
      expect(text(q('thread-held-open', thread('r1')!))).toBe('Offen, solange du schreibst');
      expect(q<HTMLTextAreaElement>('composer-input', thread('r1')!)).toBe(field);
      expect(field.value).toBe('Ich antworte gerade');
      expect(item('r1')).not.toBeNull();

      q('composer-cancel', thread('r1')!)!.click();
      await change();
      expect(thread('r1')).toBeNull(); // hidden again once the draft is gone
    });

    it('live_RecordArchivedWhileWritingANewThread_DraftStaysReadableAndDiscardable', async () => {
      await render(threads([]));
      const field = q<HTMLTextAreaElement>('composer-input')!;
      await typeComposer(field, 'Wichtiger Gedanke');

      HostComponent.writable.set(false);
      await change();

      const kept = q<HTMLTextAreaElement>('composer-input')!;
      expect(kept.value).toBe('Wichtiger Gedanke');
      expect(kept.readOnly).toBe(true);
      expect(q('composer-submit')).toBeNull();
      expect(q('comments-readonly')).not.toBeNull();

      q('composer-cancel')!.click();
      await change();
      expect(q('composer-input')).toBeNull();
    });

    it('send_TextChangedAfterFailure_UsesANewIdempotencyKey', async () => {
      await render(threads([]));
      const field = q<HTMLTextAreaElement>('composer-input')!;
      await typeComposer(field, 'Erste Fassung');
      q('composer-submit')!.click();
      const first = await expectRequest(http, 'POST', COMMENTS_URL);
      first.error(new ProgressEvent('error'));
      await change();

      await typeComposer(field, 'Zweite Fassung');
      q('composer-submit')!.click();
      const second = await expectRequest(http, 'POST', COMMENTS_URL);

      expect(second.request.body).toEqual({ markdown: 'Zweite Fassung' });
      expect(second.request.headers.get('Idempotency-Key')).not.toBe(
        first.request.headers.get('Idempotency-Key'),
      );
      second.flush(comment({ id: 'n1', markdown: 'Zweite Fassung' }));
      await change();
    });
  });

  describe('errors', () => {
    it('send_NotMember_NamesTheMentionAtTheForm', async () => {
      await render(threads([]));
      const stranger = '0199a000-0000-7000-8000-0000000000ff';
      const field = q<HTMLTextAreaElement>('composer-input')!;
      await typeComposer(field, `Hallo [@Fremd](actor:${stranger})`);

      q('composer-submit')!.click();
      (await expectRequest(http, 'POST', COMMENTS_URL)).flush(
        {
          code: 'validation_failed',
          status: 400,
          errors: [
            {
              field: 'markdown',
              code: 'not_member',
              message: `actor:${stranger} is no member of this workspace and cannot be mentioned.`,
            },
          ],
        },
        { status: 400, statusText: 'Bad Request' },
      );
      await change();

      expect(text(q('composer-errors'))).toBe(
        '@Fremd ist kein Mitglied dieses Workspaces und kann nicht erwähnt werden.',
      );
      expect(q('composer-errors')!.getAttribute('role')).toBe('alert');
      expect(field.getAttribute('aria-describedby')).toContain(q('composer-errors')!.id);
      expect(field.value).toBe(`Hallo [@Fremd](actor:${stranger})`);
    });

    it('send_RecordArchived_ExplainsAtTheForm', async () => {
      await render(threads([]));
      await typeComposer(q<HTMLTextAreaElement>('composer-input')!, 'Zu spät');

      q('composer-submit')!.click();
      (await expectRequest(http, 'POST', COMMENTS_URL)).flush(
        { code: 'record_archived', status: 409 },
        { status: 409, statusText: 'Conflict' },
      );
      await change();

      expect(text(q('composer-errors'))).toContain('Der Record ist archiviert');
    });

    it('send_WorkspaceArchived_ExplainsAtTheForm', async () => {
      await render(threads([]));
      await typeComposer(q<HTMLTextAreaElement>('composer-input')!, 'Zu spät');

      q('composer-submit')!.click();
      (await expectRequest(http, 'POST', COMMENTS_URL)).flush(
        { code: 'workspace_archived', status: 409 },
        { status: 409, statusText: 'Conflict' },
      );
      await change();

      expect(text(q('composer-errors'))).toBe(
        'Der Workspace ist archiviert, Kommentare sind schreibgeschützt.',
      );
    });

    it('reply_RootDeletedMeanwhile_ExplainsAtTheReplyField', async () => {
      await render(threads([root('r1')]));
      q('thread-reply', thread('r1')!)!.click();
      await change();
      await typeComposer(q<HTMLTextAreaElement>('composer-input', thread('r1')!)!, 'Antwort');

      q('composer-submit', thread('r1')!)!.click();
      (await expectRequest(http, 'POST', COMMENTS_URL)).flush(
        { code: 'comment_deleted', status: 409 },
        { status: 409, statusText: 'Conflict' },
      );
      await change();

      expect(text(q('composer-errors', thread('r1')!))).toContain('inzwischen gelöscht');
    });

    it('delete_NotAuthor_ShowsTheReasonAtTheComment', async () => {
      await render(threads([root('r1')]));
      await openMenu('r1');
      q('comment-delete')!.click();
      await change();

      q('confirm-ok')!.click();
      (await expectRequest(http, 'DELETE', `${API}/comments/r1`)).flush(
        { code: 'not_author', status: 403 },
        { status: 403, statusText: 'Forbidden' },
      );
      await change();

      expect(text(q('comment-action-error', item('r1')!))).toContain('Admin-Recht');
    });

    it('resolve_NotRoot_ShowsTheReasonAtTheComment', async () => {
      await render(threads([root('r1')]));

      q('comment-resolve', item('r1')!)!.click();
      (await expectRequest(http, 'POST', `${API}/comments/r1/resolve`)).flush(
        {
          code: 'validation_failed',
          status: 400,
          errors: [{ field: 'resolved', code: 'not_root', message: 'x' }],
        },
        { status: 400, statusText: 'Bad Request' },
      );
      await change();

      expect(text(q('comment-action-error', item('r1')!))).toContain(
        'Nur der erste Kommentar eines Threads',
      );
    });
  });

  describe('mention picker', () => {
    it('mention_AtSign_OpensFilteredListbox_KeyboardInsertsCanonicalLink', async () => {
      await render(threads([]));
      const field = q<HTMLTextAreaElement>('composer-input')!;

      await typeComposer(field, 'Frage an @');
      const list = q('mention-list')!;
      expect(list.getAttribute('role')).toBe('listbox');
      expect(names(all('mention-option', list))).toEqual(['Admin', 'Coder', 'Mia']);
      expect(text(all('mention-option', list)[1].querySelector('ag-badge'))).toBe('Agent');

      await typeComposer(field, 'Frage an @m');
      expect(names(all('mention-option'))).toEqual(['Mia', 'Admin']);
      const options = all('mention-option');
      expect(field.getAttribute('aria-controls')).toBe(list.id);
      expect(field.getAttribute('aria-activedescendant')).toBe(options[0].id);
      expect(options[0].getAttribute('aria-selected')).toBe('true');

      expect(keydown(field, 'ArrowDown').defaultPrevented).toBe(true);
      await change();
      expect(field.getAttribute('aria-activedescendant')).toBe(all('mention-option')[1].id);
      keydown(field, 'ArrowDown'); // wraps around
      await change();
      keydown(field, 'ArrowUp');
      await change();
      expect(field.getAttribute('aria-activedescendant')).toBe(all('mention-option')[1].id);

      expect(keydown(field, 'Enter').defaultPrevented).toBe(true);
      await change();

      expect(field.value).toBe(`Frage an [@Admin](actor:${ADMIN_ME.id}) `);
      expect(field.selectionStart).toBe(field.value.length);
      expect(q('mention-list')).toBeNull();
      expect(field.hasAttribute('aria-activedescendant')).toBe(false);
      expect(host().store.draft('new')).toBe(field.value);
    });

    it('mention_InsertAtCaretInTheMiddle_KeepsTheRest', async () => {
      await render(threads([]));
      const field = q<HTMLTextAreaElement>('composer-input')!;
      field.focus();
      field.value = 'Hallo @Co, bitte prüfen';
      field.setSelectionRange(9, 9);
      field.dispatchEvent(new Event('input', { bubbles: true }));
      await change();

      keydown(field, 'Tab');
      await change();

      expect(field.value).toBe(`Hallo [@Coder](actor:${AGENT_ID}), bitte prüfen`);
      expect(field.selectionStart).toBe(`Hallo [@Coder](actor:${AGENT_ID})`.length);
    });

    it('mention_Escape_ClosesOnlyTheList_StaysClosedForThisAt', async () => {
      await render(threads([]));
      const field = q<HTMLTextAreaElement>('composer-input')!;
      await typeComposer(field, '@C');
      const outer = vi.fn();
      el().addEventListener('keydown', outer);

      const escape = keydown(field, 'Escape');
      await change();

      expect(escape.defaultPrevented).toBe(true);
      expect(outer).not.toHaveBeenCalled();
      expect(q('mention-list')).toBeNull();
      expect(field.value).toBe('@C');

      await typeComposer(field, '@Co');
      expect(q('mention-list')).toBeNull();
      // a new @ opens again
      await typeComposer(field, '@Co @');
      expect(q('mention-list')).not.toBeNull();
    });

    it('mention_EnterWhileComposing_BelongsToTheInputMethod', async () => {
      await render(threads([]));
      const field = q<HTMLTextAreaElement>('composer-input')!;
      await typeComposer(field, '@Co');

      const enter = keydown(field, 'Enter', { isComposing: true });
      await change();

      expect(enter.defaultPrevented).toBe(false);
      expect(field.value).toBe('@Co');
      expect(q('mention-list')).not.toBeNull();
    });

    it('mention_ClickOnOption_Inserts', async () => {
      await render(threads([]));
      const field = q<HTMLTextAreaElement>('composer-input')!;
      await typeComposer(field, '@co');

      const option = all('mention-option')[0];
      const press = new MouseEvent('mousedown', { bubbles: true, cancelable: true });
      option.dispatchEvent(press);
      await change();

      expect(press.defaultPrevented).toBe(true);
      expect(field.value).toBe(`[@Coder](actor:${AGENT_ID}) `);
    });

    it('mention_Sent_ShowsTheChip', async () => {
      await render(threads([]));
      const field = q<HTMLTextAreaElement>('composer-input')!;
      await typeComposer(field, 'Bitte @Cod');
      keydown(field, 'Enter');
      await change();
      await typeComposer(field, `${field.value}prüfen`);

      keydown(field, 'Enter', { metaKey: true });
      const post = await expectRequest(http, 'POST', COMMENTS_URL);
      const markdown = (post.request.body as { markdown: string }).markdown;
      expect(markdown).toBe(`Bitte [@Coder](actor:${AGENT_ID}) prüfen`);
      post.flush(comment({ id: 'n1', markdown }));
      await change();

      const chip = q('mention-chip', item('n1')!)!;
      expect(text(chip)).toBe('@Coder');
      expect(chip.dataset['actorId']).toBe(AGENT_ID);
    });
  });

  describe('live', () => {
    it('live_AgentComments_ThreadAppearsAndAnOpenReplyDraftStays', async () => {
      await render(threads([root('r1')]));
      q('thread-reply', thread('r1')!)!.click();
      await change();
      const field = q<HTMLTextAreaElement>('composer-input', thread('r1')!)!;
      await typeComposer(field, 'Halb geschriebene Antwort');
      expect(document.activeElement).toBe(field);

      realtime.emit(commentEvent(1, 'agent-thread'));
      await respond(
        http,
        'GET',
        COMMENTS_URL,
        threads([
          root('r1'),
          root('agent-thread', {
            authorId: AGENT_ID,
            authorName: 'Coder',
            authorKind: 'agent',
            markdown: 'Ich habe eine Frage',
          }),
        ]),
      );
      await change();

      expect(text(q('comment-markdown', item('agent-thread')!))).toBe('Ich habe eine Frage');
      const still = q<HTMLTextAreaElement>('composer-input', thread('r1')!)!;
      expect(still).toBe(field);
      expect(still.value).toBe('Halb geschriebene Antwort');
      expect(document.activeElement).toBe(field);
      expect(text(q('comments-live'))).toBe('Neuer Kommentar von Coder');
    });

    it('live_ThreadOfAnOpenReplyDraftDeletedElsewhere_HeldBackUntilTheDraftCloses', async () => {
      // Without the hold-back the reload removes the thread and with it the reply field.
      await render(threads([root('r1', { authorId: MIA_ID, authorName: 'Mia' }), root('r2')]));
      q('thread-reply', thread('r1')!)!.click();
      await change();
      await typeComposer(q<HTMLTextAreaElement>('composer-input', thread('r1')!)!, 'Entwurf');

      realtime.emit(commentEvent(1, 'r1'));
      await respond(http, 'GET', COMMENTS_URL, threads([root('r2')]));
      await change();

      expect(thread('r1')).not.toBeNull();
      expect(q<HTMLTextAreaElement>('composer-input', thread('r1')!)!.value).toBe('Entwurf');
      expect(q('comments-held')).not.toBeNull();

      // more events while held: no request until the draft is closed
      realtime.emit(commentEvent(2, 'x'));
      await new Promise((resolve) => setTimeout(resolve, DELAY * 3));
      http.expectNone(COMMENTS_URL);

      q('composer-cancel', thread('r1')!)!.click();
      await respond(http, 'GET', COMMENTS_URL, threads([root('r2')]));
      await change();

      expect(thread('r1')).toBeNull();
      expect(q('comments-held')).toBeNull();
    });

    it('live_EditedCommentChangesWhileEditing_EditDraftStays', async () => {
      await render(threads([root('r1', { markdown: 'Alt' })]));
      await openMenu('r1');
      q('comment-edit')!.click();
      await change();
      await typeComposer(q<HTMLTextAreaElement>('composer-input', item('r1')!)!, 'Mein Text');

      realtime.emit(commentEvent(1, 'r1'));
      await respond(
        http,
        'GET',
        COMMENTS_URL,
        threads([root('r1', { markdown: 'Fremd geändert' })]),
      );
      await change();

      expect(q<HTMLTextAreaElement>('composer-input', item('r1')!)!.value).toBe('Mein Text');
    });

    it('live_ReloadFetchedBeforeOwnWrite_IsDiscardedAndFetchedAgain', async () => {
      await render(threads([root('r1')]));
      await typeComposer(q<HTMLTextAreaElement>('composer-input')!, 'Meins');

      realtime.emit(commentEvent(1, 'x'));
      const stale = await expectRequest(http, 'GET', COMMENTS_URL);
      q('composer-submit')!.click();
      const post = await expectRequest(http, 'POST', COMMENTS_URL);
      post.flush(comment({ id: 'mine', markdown: 'Meins' }));
      await change();
      expect(item('mine')).not.toBeNull();
      // answered after the write ended, but fetched before the own comment existed
      stale.flush(threads([root('r1')]));
      await change();

      expect(item('mine')).not.toBeNull();
      await respond(
        http,
        'GET',
        COMMENTS_URL,
        threads([root('r1'), root('mine', { markdown: 'Meins' })]),
      );
      await change();
      expect(item('mine')).not.toBeNull();
      expect(all('comment-thread')).toHaveLength(2);
    });

    it('live_SameAnnouncementTwice_ChangesTheLiveRegion', async () => {
      await render(threads([]));
      const agent = { authorId: AGENT_ID, authorName: 'Coder', authorKind: 'agent' as const };

      realtime.emit(commentEvent(1, 'a1'));
      await respond(http, 'GET', COMMENTS_URL, threads([root('a1', agent)]));
      await change();
      const first = q('comments-live')!.textContent;

      realtime.emit(commentEvent(2, 'a2'));
      await respond(http, 'GET', COMMENTS_URL, threads([root('a1', agent), root('a2', agent)]));
      await change();
      const second = q('comments-live')!.textContent;

      expect(first?.trim()).toBe('Neuer Kommentar von Coder');
      expect(second?.trim()).toBe('Neuer Kommentar von Coder');
      expect(second).not.toBe(first);
    });

    it('live_EventOfAnotherRecordOrOtherType_DoesNotReload', async () => {
      await render(threads([]));

      realtime.emit(commentEvent(1, 'x', 'other-record'));
      realtime.emit(activityEvent({ seq: 2, type: 'record.updated' }));
      await new Promise((resolve) => setTimeout(resolve, DELAY * 3));

      http.expectNone(COMMENTS_URL);
    });

    it('live_Resync_Reloads', async () => {
      await render(threads([]));

      realtime.emitResync();
      await respond(
        http,
        'GET',
        COMMENTS_URL,
        threads([root('r1', { authorId: MIA_ID, authorName: 'Mia' })]),
      );
      await respond(http, 'GET', `${API}/members`, [member()]);
      await change();

      expect(item('r1')).not.toBeNull();
    });
  });

  describe('link to a comment', () => {
    it('focus_ReplyInResolvedThread_RevealsHighlightsAndFocusesIt', async () => {
      await render(
        threads([
          root('r1'),
          root('r2', { resolvedAt: minutesAgo(1), resolvedBy: MIA_ID }, [reply('target', 'r2')]),
        ]),
      );

      host().focus.set('target');
      await change();

      const target = item('target')!;
      expect(target).not.toBeNull();
      expect(target.getAttribute('aria-current')).toBe('true');
      expect(target.className).toContain('ring-2');
      await vi.waitFor(() => expect(document.activeElement).toBe(target));
      expect(q<HTMLInputElement>('show-resolved')!.checked).toBe(true);
    });

    it('focus_UnknownComment_ShowsNotice', async () => {
      await render(threads([root('r1')]));

      host().focus.set('gone');
      await change();

      expect(text(q('comment-missing'))).toContain('nicht mehr vorhanden');
    });
  });
});
