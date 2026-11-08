import { Component } from '@angular/core';
import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
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
  pageProviders,
  respond,
  settle,
} from '../../core/testing/test-utils';
import { MentionsPageComponent } from './mentions-page.component';
import { Mention, MentionsStore } from './mentions.store';

@Component({ template: '' })
class BlankComponent {}

const COUNT_URL = `${API}/mentions?unreadOnly=true&limit=1`;

function mention(id: string, overrides: Partial<Mention> = {}): Mention {
  return {
    id,
    commentId: `comment-${id}`,
    parentCommentId: null,
    recordId: RECORD_ID,
    recordTitle: 'Release plan',
    byActorId: AGENT_ID,
    byActorName: 'Tech-Lead',
    excerpt: 'Please look at this',
    createdAt: '2026-11-07T10:00:00Z',
    readAt: null,
    ...overrides,
  };
}

const page = (items: Mention[], nextCursor: string | null = null, unreadCount = 2) => ({
  items,
  nextCursor,
  unreadCount,
});

describe('MentionsPageComponent', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;
  let realtime: FakeRealtime;

  /** Opens `url`; the page's list request (and the badge count) are answered. */
  async function render(url: string, list: ReturnType<typeof page>): Promise<void> {
    realtime = new FakeRealtime();
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({
          routes: [
            { path: 'w/:workspaceSlug/mentions', component: MentionsPageComponent },
            { path: 'w/:workspaceSlug/records/:recordId', component: BlankComponent },
          ],
        }),
        provideFakeRealtime(realtime),
        { provide: LIVE_RELOAD_DELAY_MS, useValue: 0 },
        { provide: LIVE_RELOAD_MAX_WAIT_MS, useValue: 0 },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    TestBed.inject(MentionsStore); // the shell has it alive; the page relies on that
    const query = url.split('?')[1];
    const navigation = harness.navigateByUrl(url);
    await respond(http, 'GET', COUNT_URL, page([], null, list.unreadCount));
    await respond(http, 'GET', `${API}/mentions?${query ? `${query}&` : ''}limit=20`, list);
    await navigation;
    await settle(harness.fixture);
  }

  afterEach(() => http.verify());

  const root = () => harness.routeNativeElement as HTMLElement;
  const q = <T extends HTMLElement = HTMLElement>(id: string) =>
    root().querySelector<T>(`[data-testid="${id}"]`);
  const all = (id: string) =>
    Array.from(root().querySelectorAll<HTMLElement>(`[data-testid="${id}"]`));
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  it('render_Mentions_ShowsWhoWhereExcerptAndHighlightsUnread', async () => {
    await render(
      '/w/demo/mentions',
      page([mention('m1'), mention('m2', { readAt: '2026-11-07T11:00:00Z', excerpt: 'Done' })]),
    );

    const rows = all('mention');
    expect(rows).toHaveLength(2);
    expect(text(rows[0].querySelector('[data-testid="mention-by"]'))).toBe(
      'Tech-Lead hat dich erwähnt',
    );
    expect(text(rows[0].querySelector('[data-testid="mention-excerpt"]'))).toBe(
      'Please look at this',
    );
    expect(text(rows[0].querySelector('[data-testid="mention-time"]'))).toMatch(
      /^07\.11\.2026 \d\d:\d\d$/,
    );
    const link = rows[0].querySelector<HTMLAnchorElement>('[data-testid="mention-link"]')!;
    expect(text(link)).toBe('Release plan');
    expect(link.getAttribute('href')).toBe(`/w/demo/records/${RECORD_ID}?comment=comment-m1`);
    expect(rows[0].getAttribute('data-unread')).toBe('true');
    expect(rows[0].querySelector('[data-testid="mention-unread"]')).not.toBeNull();
    expect(rows[1].getAttribute('data-unread')).toBe('false');
    expect(rows[1].querySelector('[data-testid="mention-unread"]')).toBeNull();
  });

  it('render_Empty_ShowsHint', async () => {
    await render('/w/demo/mentions', page([], null, 0));

    expect(text(q('mentions-empty'))).toBe('Keine Erwähnungen.');
  });

  it('render_ExcerptWithMarkup_ShowsItAsText', async () => {
    await render(
      '/w/demo/mentions',
      page([mention('m1', { excerpt: 'hi <img src=x onerror=alert(1)> <b>there</b>' })]),
    );

    const excerpt = q('mention-excerpt')!;
    expect(excerpt.querySelector('img')).toBeNull();
    expect(excerpt.querySelector('b')).toBeNull();
    expect(text(excerpt)).toBe('hi <img src=x onerror=alert(1)> <b>there</b>');
  });

  it('render_UnreadOnlyInUrl_AsksForUnreadAndChecksFilter', async () => {
    await render('/w/demo/mentions?unreadOnly=true', page([mention('m1')], null, 1));

    expect(q('filter-unread')!.querySelector('button')!.getAttribute('aria-checked')).toBe('true');
    expect(all('mention')).toHaveLength(1);
  });

  it('setUnreadOnly_Toggle_WritesUrlAndReloads', async () => {
    await render('/w/demo/mentions', page([mention('m1'), mention('m2')]));

    q('filter-unread')!.querySelector<HTMLElement>('button')!.click();
    await respond(http, 'GET', `${API}/mentions?unreadOnly=true&limit=20`, page([mention('m1')]));
    await settle(harness.fixture);

    expect(TestBed.inject(Router).url).toBe('/w/demo/mentions?unreadOnly=true');
    expect(all('mention')).toHaveLength(1);
  });

  it('loadMore_Cursor_AppendsNextPageWithoutDuplicates', async () => {
    await render('/w/demo/mentions', page([mention('m1')], 'c1'));

    q('load-more')!.click();
    await respond(
      http,
      'GET',
      `${API}/mentions?limit=20&cursor=c1`,
      page([mention('m1'), mention('m2')]),
    );
    await settle(harness.fixture);

    expect(all('mention')).toHaveLength(2);
    expect(q('load-more')).toBeNull();
  });

  it('click_UnreadMention_MarksItReadAndCountDrops', async () => {
    await render('/w/demo/mentions', page([mention('m1'), mention('m2')]));
    const store = TestBed.inject(MentionsStore);
    expect(store.unreadCount()).toBe(2);

    q<HTMLAnchorElement>('mention-link')!.click();
    const request = await expectRequest(http, 'POST', `${API}/mentions/read`);
    expect(request.request.body).toEqual({ ids: ['m1'] });
    request.flush({ marked: 1, unreadCount: 1 });
    await settle(harness.fixture);

    expect(store.unreadCount()).toBe(1);
    expect(TestBed.inject(Router).url).toBe(`/w/demo/records/${RECORD_ID}?comment=comment-m1`);
  });

  it('click_ReadMention_DoesNotPost', async () => {
    await render(
      '/w/demo/mentions',
      page([mention('m1', { readAt: '2026-11-07T11:00:00Z' })], null, 0),
    );

    q<HTMLAnchorElement>('mention-link')!.click();
    await settle(harness.fixture);

    http.expectNone(`${API}/mentions/read`);
  });

  it('markAll_Click_PostsAllAndClearsUnreadDisplay', async () => {
    await render('/w/demo/mentions', page([mention('m1'), mention('m2')]));

    q('mark-all-read')!.click();
    const request = await expectRequest(http, 'POST', `${API}/mentions/read`);
    expect(request.request.body).toEqual({ all: true });
    request.flush({ marked: 2, unreadCount: 0 });
    await settle(harness.fixture);

    expect(TestBed.inject(MentionsStore).unreadCount()).toBe(0);
    expect(all('mention').every((m) => m.getAttribute('data-unread') === 'false')).toBe(true);
    expect(q<HTMLButtonElement>('mark-all-read')!.disabled).toBe(true);
  });

  it('live_MentionOfOwnActor_ReloadsListFromTheStart', async () => {
    await render('/w/demo/mentions', page([mention('m1')]));

    realtime.emit(
      activityEvent({
        seq: 7,
        type: 'comment.created',
        targetType: 'comment',
        diff: { mentionedActorIds: [ADMIN_ME.id] },
      }),
    );
    await respond(http, 'GET', `${API}/mentions?limit=20`, page([mention('m3'), mention('m1')]));
    await respond(http, 'GET', COUNT_URL, page([], null, 3));
    await settle(harness.fixture);

    expect(all('mention')).toHaveLength(2);
  });

  it('live_CommentDeletedWithoutOwnId_RefreshesAndKeepsListVisible', async () => {
    await render('/w/demo/mentions', page([mention('m1'), mention('m2')], null, 2));

    realtime.emit(
      activityEvent({
        seq: 8,
        type: 'comment.deleted',
        targetType: 'comment',
        diff: { mentionedActorIds: [] },
      }),
    );
    const list = await expectRequest(http, 'GET', `${API}/mentions?limit=20`);
    expect(all('mention')).toHaveLength(2); // stays on screen while the answer is pending
    expect(root().querySelector('hlm-skeleton')).toBeNull();
    list.flush(page([mention('m2')], null, 1));
    await respond(http, 'GET', COUNT_URL, page([], null, 1));
    await settle(harness.fixture);

    expect(all('mention')).toHaveLength(1);
    expect(TestBed.inject(MentionsStore).unreadCount()).toBe(1);
  });

  it('live_Refresh_KeepsOlderLoadedPages', async () => {
    await render(
      '/w/demo/mentions',
      page([mention('m1', { createdAt: '2026-11-07T10:00:00Z' })], 'c1'),
    );
    q('load-more')!.click();
    await respond(
      http,
      'GET',
      `${API}/mentions?limit=20&cursor=c1`,
      page([mention('m0', { createdAt: '2026-11-06T10:00:00Z' })], null),
    );
    await settle(harness.fixture);

    realtime.emit(activityEvent({ seq: 9, type: 'comment.deleted', targetType: 'comment' }));
    await respond(
      http,
      'GET',
      `${API}/mentions?limit=20`,
      page([mention('m1', { createdAt: '2026-11-07T10:00:00Z' })], 'c1'),
    );
    await respond(http, 'GET', COUNT_URL, page([], null, 2));
    await settle(harness.fixture);

    expect(all('mention')).toHaveLength(2);
  });

  it('markAll_Fails_ShowsAlertAndKeepsUnread', async () => {
    await render('/w/demo/mentions', page([mention('m1')], null, 1));

    q('mark-all-read')!.click();
    await respond(http, 'POST', `${API}/mentions/read`, {}, { status: 500, statusText: 'Error' });
    await settle(harness.fixture);

    expect(q('mentions-error')).not.toBeNull();
    expect(all('mention')[0].getAttribute('data-unread')).toBe('true');
    expect(TestBed.inject(MentionsStore).unreadCount()).toBe(1);
  });

  it('markAll_CounterZeroButUnreadListed_IsEnabled', async () => {
    await render('/w/demo/mentions', page([mention('m1')], null, 0));

    expect(q<HTMLButtonElement>('mark-all-read')!.disabled).toBe(false);
  });

  it('click_Twice_SendsOnePost', async () => {
    await render('/w/demo/mentions', page([mention('m1')], null, 1));
    const link = q<HTMLAnchorElement>('mention-link')!;

    link.click();
    link.click();
    (await expectRequest(http, 'POST', `${API}/mentions/read`)).flush({
      marked: 1,
      unreadCount: 0,
    });
    await settle(harness.fixture);

    http.expectNone(`${API}/mentions/read`);
  });
});
