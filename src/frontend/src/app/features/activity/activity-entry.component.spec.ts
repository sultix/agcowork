import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import {
  AGENT_ID,
  API,
  COLLECTION_ID,
  RECORD_ID,
  activityEvent,
  collection,
  member,
  pageProviders,
  serve,
  settle,
  space,
} from '../../core/testing/test-utils';
import { ActivityEntryComponent, ActivityEvent } from './activity-entry.component';

let nextEvent: ActivityEvent;

@Component({
  imports: [ActivityEntryComponent],
  template: `<ag-activity-entry [event]="event" />`,
})
class HostComponent {
  event = nextEvent;
}

describe('ActivityEntryComponent', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;

  async function render(event: ActivityEvent): Promise<void> {
    nextEvent = event;
    TestBed.configureTestingModule({
      providers: pageProviders({
        routes: [{ path: 'w/:workspaceSlug', component: HostComponent }],
      }),
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl('/w/demo');
    await serve(http, [
      [`${API}/spaces`, [space()]],
      [`${API}/collections`, [collection()]],
      [`${API}/members`, [member(), member({ actorId: AGENT_ID, name: 'Coder', kind: 'agent' })]],
    ]);
    await navigation;
    await settle(harness.fixture);
  }

  afterEach(() => http.verify());

  const root = () => harness.routeNativeElement as HTMLElement;
  const q = (id: string) => root().querySelector<HTMLElement>(`[data-testid="${id}"]`);
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  it('render_AgentRecordEvent_ShowsActorBadgeTypeNameAndLink', async () => {
    await render(
      activityEvent({ type: 'record.created', diff: { title: { old: null, new: 'Login' } } }),
    );

    expect(text(q('actor-name'))).toBe('Coder');
    expect(text(q('actor-kind'))).toBe('Agent');
    expect(text(q('entry-text'))).toBe('hat Record angelegt');
    expect(text(q('entry-name'))).toBe('„Login“');
    expect(q('activity-link')!.getAttribute('href')).toBe(`/w/demo/records/${RECORD_ID}`);
  });

  it('render_HumanActor_ShowsMenschBadge', async () => {
    await render(activityEvent({ actorId: member().actorId }));

    expect(text(q('actor-kind'))).toBe('Mensch');
  });

  it('render_CollectionEvent_ResolvesNameFromTreeAndLinksToCollection', async () => {
    await render(
      activityEvent({
        type: 'collection.schema_changed',
        targetType: 'collection',
        targetId: COLLECTION_ID,
        diff: { added: ['prio'], affectedRecords: 4 },
      }),
    );

    expect(text(q('entry-text'))).toBe('hat Collection im Schema geändert');
    expect(text(q('entry-name'))).toBe('„Aufgaben“');
    expect(text(q('entry-affected'))).toBe('(4 Records bereinigt)');
    expect(q('activity-link')!.getAttribute('href')).toBe(`/w/demo/collections/${COLLECTION_ID}`);
  });

  it('toggle_DiffDetails_ExpandsChangedKeysAndBodyDelta', async () => {
    await render(
      activityEvent({
        diff: {
          properties: { status: { old: 'todo', new: 'done' } },
          bodyChanged: true,
          bodyDelta: { added: 12, removed: 3 },
        },
      }),
    );
    expect(q('entry-diff')).toBeNull();

    q('entry-toggle')!.click();
    harness.fixture.detectChanges();

    expect(text(q('entry-change'))).toBe('status todo done');
    expect(text(q('entry-body'))).toContain('+12');
    expect(text(q('entry-body'))).toContain('−3');

    q('entry-toggle')!.click();
    harness.fixture.detectChanges();
    expect(q('entry-diff')).toBeNull();
  });

  it('render_EventWithoutDiff_HasNoToggle', async () => {
    await render(activityEvent({ type: 'record.archived', diff: {} }));

    expect(q('entry-toggle')).toBeNull();
  });

  it('render_ViewEvent_LinksToItsCollection', async () => {
    await render(activityEvent({ type: 'view.created', targetType: 'view', targetId: 'v1' }));

    expect(text(q('entry-text'))).toBe('hat eine Ansicht angelegt');
    expect(q('activity-link')!.getAttribute('href')).toBe(`/w/demo/collections/${COLLECTION_ID}`);
  });

  it('render_RecordUpdateWithoutTitle_ShowsPlainTextAndLinkWithoutQuotes', async () => {
    await render(activityEvent({ type: 'record.updated', diff: {} }));

    expect(text(q('entry-text'))).toBe('hat einen Record geändert');
    expect(q('entry-name')).toBeNull();
    expect(q('activity-link')!.getAttribute('href')).toBe(`/w/demo/records/${RECORD_ID}`);
  });

  it('render_TargetTitle_IsLinkTextInsteadOfOpen', async () => {
    await render(activityEvent({ type: 'record.updated', targetTitle: 'Login bauen', diff: {} }));

    expect(text(q('activity-link'))).toBe('Login bauen');
    expect(q('activity-link')!.getAttribute('href')).toBe(`/w/demo/records/${RECORD_ID}`);
    expect(text(q('entry-text'))).toBe('hat einen Record geändert');
    expect(q('entry-name')).toBeNull();
  });

  it('render_NoTargetTitle_LinkTextIsOpen', async () => {
    await render(activityEvent({ type: 'record.updated', targetTitle: null, diff: {} }));

    expect(text(q('activity-link'))).toBe('Öffnen');
  });

  const COMMENT_ID = '0190f3a2-7c4e-7b1a-9d2e-3f4a5b6c7d01';
  const comment = (type: string, diff: unknown = {}) =>
    activityEvent({
      type,
      targetType: 'comment',
      targetId: COMMENT_ID,
      targetTitle: 'Login bauen',
      diff: {
        recordId: RECORD_ID,
        parentCommentId: null,
        mentionedActorIds: [],
        ...(diff as object),
      },
    });

  it('render_CommentEvents_NameRecordAndLinkToTheComment', async () => {
    const cases: [string, string][] = [
      ['comment.created', 'geschrieben'],
      ['comment.updated', 'bearbeitet'],
      ['comment.deleted', 'gelöscht'],
      ['comment.resolved', 'als erledigt markiert'],
      ['comment.reopened', 'wieder geöffnet'],
    ];
    for (const [type, verb] of cases) {
      TestBed.resetTestingModule();
      await render(comment(type));

      expect(text(q('entry-text')), type).toBe('hat einen Kommentar zu');
      expect(text(q('activity-link')), type).toBe('Login bauen');
      expect(text(q('entry-text-after')), type).toBe(verb);
      expect(q('activity-link')!.getAttribute('href'), type).toBe(
        `/w/demo/records/${RECORD_ID}?comment=${COMMENT_ID}`,
      );
      expect(q('entry-name'), type).toBeNull();
      expect(q('entry-toggle'), type).toBeNull();
      http.verify();
    }
  });

  it('render_CommentReply_SaysAnswer', async () => {
    await render(comment('comment.created', { parentCommentId: 'p1' }));

    expect(text(q('entry-text'))).toBe('hat eine Antwort zu');
    expect(text(q('entry-text-after'))).toBe('geschrieben');
  });

  it('render_CommentWithMentions_DetailsNameTheMentionedActors', async () => {
    await render(comment('comment.created', { mentionedActorIds: [AGENT_ID] }));

    q('entry-toggle')!.click();
    harness.fixture.detectChanges();

    expect(text(q('entry-extra'))).toBe('Erwähnt: Coder');
    expect(text(q('entry-diff'))).not.toContain(RECORD_ID);
  });

  it('render_CommentWithoutRecordTitle_SaysARecord', async () => {
    await render({ ...comment('comment.resolved'), targetTitle: null });

    expect(text(q('activity-link'))).toBe('einem Record');
    expect(text(q('entry-text-after'))).toBe('als erledigt markiert');
  });

  it('render_CommentWithoutRecordId_ShowsTextWithoutLink', async () => {
    await render({ ...comment('comment.deleted'), diff: {} as never });

    expect(text(q('entry-text'))).toBe('hat einen Kommentar gelöscht');
    expect(q('activity-link')).toBeNull();
    expect(q('entry-text-after')).toBeNull();
  });

  it('render_UnknownType_ShowsRawType', async () => {
    await render(activityEvent({ type: 'thing.exploded', targetType: 'thing' }));

    expect(text(q('entry-text'))).toBe('thing.exploded');
    expect(q('activity-link')).toBeNull();
  });
});
