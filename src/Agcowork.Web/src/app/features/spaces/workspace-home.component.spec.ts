import { TestBed } from '@angular/core/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import {
  ADMIN_ME,
  API,
  RECORD_ID,
  SPACE_ID,
  collection,
  member,
  pageProviders,
  serve,
  settle,
  space,
} from '../../core/testing/test-utils';
import { WorkspaceHomeComponent } from './workspace-home.component';

const event = (over: object) => ({
  seq: 1,
  id: 'e1',
  type: 'record.updated',
  actorId: ADMIN_ME.id,
  targetType: 'record',
  targetId: RECORD_ID,
  spaceId: SPACE_ID,
  collectionId: null,
  diff: {},
  at: '2026-11-07T11:00:00Z',
  ...over,
});

describe('WorkspaceHomeComponent', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;

  async function render(events: unknown[], spaces: unknown[] = [space()]): Promise<void> {
    TestBed.configureTestingModule({
      providers: pageProviders({
        routes: [{ path: 'w/:workspaceSlug', component: WorkspaceHomeComponent }],
      }),
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl('/w/demo');
    await serve(http, [
      [`${API}/spaces`, spaces],
      [`${API}/collections`, [collection()]],
      [`${API}/members`, [member()]],
      [`${API}/activity?limit=10`, { items: events, nextCursor: null }],
    ]);
    await navigation;
    await settle(harness.fixture);
  }

  afterEach(() => http.verify());

  const root = () => harness.routeNativeElement as HTMLElement;
  const all = (id: string) =>
    Array.from(root().querySelectorAll<HTMLElement>(`[data-testid="${id}"]`));
  const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

  it('render_Spaces_ShowsCardsLinkingToTheSpacePage', async () => {
    await render([]);

    const [card] = all('space-card');
    expect(text(card)).toContain('Produkt');
    expect(text(card)).toContain('Alles rund um das Produkt');
    expect(card.getAttribute('href')).toBe(`/w/demo/spaces/${SPACE_ID}`);
  });

  it('render_NoSpaces_ShowsHint', async () => {
    await render([], []);

    expect(text(root())).toContain('noch keine Spaces');
  });

  it('render_Activity_ShowsOnlyRecordEventsWithActorTitleAndLink', async () => {
    await render([
      event({ type: 'record.created', diff: { title: { old: null, new: 'Login bauen' } } }),
      event({ id: 'e2', type: 'collection.created', targetType: 'collection', targetId: 'c1' }),
      event({ id: 'e3', type: 'record.archived', targetId: 'r3' }),
    ]);

    const items = all('activity-item');
    expect(items).toHaveLength(2);
    expect(text(items[0])).toContain('Admin hat einen Record angelegt „Login bauen“');
    expect(text(items[1])).toContain('hat einen Record archiviert');
    expect(all('activity-link').map((a) => a.getAttribute('href'))).toEqual([
      `/w/demo/records/${RECORD_ID}`,
      '/w/demo/records/r3',
    ]);
  });

  it('render_NoActivity_ShowsHint', async () => {
    await render([]);

    expect(text(all('recent-activity')[0])).toContain('Noch keine Änderungen.');
  });
});
