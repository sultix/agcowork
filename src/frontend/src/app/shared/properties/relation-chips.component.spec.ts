import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import { RECORD_TITLE_BATCH_MS } from '../../core/records/record-title.store';
import { FakeRealtime, provideFakeRealtime } from '../../core/testing/fake-hub';
import {
  API,
  COLLECTION_ID,
  SPACE_ID,
  activityEvent,
  expectRequest,
  pageProviders,
  settle,
} from '../../core/testing/test-utils';
import { ChipNavigation, RelationChipsComponent } from './relation-chips.component';

@Component({ template: '' })
class BlankComponent {}

@Component({
  imports: [RelationChipsComponent],
  template: `<ag-relation-chips
    [ids]="ids()"
    [max]="max()"
    [removable]="removable()"
    [navigation]="navigation()"
    (remove)="removed.push($event)"
  />`,
})
class HostComponent {
  readonly ids = signal<string[]>([]);
  readonly max = signal<number | null>(null);
  readonly removable = signal(false);
  readonly navigation = signal<ChipNavigation>('link');
  readonly removed: string[] = [];
}

const LOOKUP = `${API}/records/lookup`;
const A = '0199a000-0000-7000-8000-00000000aa01';
const B = '0199a000-0000-7000-8000-00000000aa02';
const C = '0199a000-0000-7000-8000-00000000aa03';
const D = '0199a000-0000-7000-8000-00000000aa04';
const E = '0199a000-0000-7000-8000-00000000aa05';

const item = (id: string, title: string, archived = false) => ({
  id,
  title,
  collectionId: COLLECTION_ID,
  spaceId: SPACE_ID,
  archived,
});

describe('RelationChipsComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let http: HttpTestingController;
  let realtime: FakeRealtime;

  async function render(ids: string[], setup: (h: HostComponent) => void = () => undefined) {
    realtime = new FakeRealtime();
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({ routes: [{ path: 'w/:workspaceSlug', component: BlankComponent }] }),
        provideFakeRealtime(realtime),
        { provide: RECORD_TITLE_BATCH_MS, useValue: 5 },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/w/demo');
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    host.ids.set(ids);
    setup(host);
    fixture.detectChanges();
  }

  afterEach(() => http.verify());

  const root = () => fixture.nativeElement as HTMLElement;
  const chips = () =>
    Array.from(root().querySelectorAll<HTMLElement>('[data-testid="relation-chip"]'));
  const text = (el: Element | null | undefined) =>
    el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  async function answer(items: unknown[]): Promise<string[]> {
    const request = await expectRequest(http, 'POST', LOOKUP);
    request.flush({ items });
    await settle(fixture);
    return (request.request.body as { ids: string[] }).ids;
  }

  it('render_Titles_AsLinksToTheRecords', async () => {
    await render([A, B]);
    expect(chips().map((c) => c.dataset['state'])).toEqual(['loading', 'loading']);

    expect(await answer([item(A, 'Login bauen'), item(B, 'Logout bauen')])).toEqual([A, B]);

    const links = chips().map((c) => c.querySelector('a')!);
    expect(links.map((a) => text(a))).toEqual(['Login bauen', 'Logout bauen']);
    expect(links.map((a) => a.getAttribute('href'))).toEqual([
      `/w/demo/records/${A}`,
      `/w/demo/records/${B}`,
    ]);
  });

  it('render_ArchivedTarget_StruckThroughWithScreenReaderHint', async () => {
    await render([A]);
    await answer([item(A, 'Alt', true)]);

    const link = chips()[0].querySelector('a')!;
    expect(link.classList).toContain('line-through');
    expect(text(chips()[0])).toBe('Alt (archiviert)');
    expect(text(chips()[0].querySelector('.sr-only'))).toBe('(archiviert)');
  });

  it('render_UnknownOrForeignTarget_NotAvailableWithoutLink', async () => {
    await render([A, B]);
    await answer([item(A, 'Da')]);

    expect(chips()[1].dataset['state']).toBe('missing');
    expect(text(chips()[1])).toBe('Nicht verfügbar');
    expect(chips()[1].querySelector('a')).toBeNull();
  });

  it('render_MoreThanMax_ShowsMaxChipsAndPlusN', async () => {
    await render([A, B, C, D, E], (h) => h.max.set(3));

    expect(await answer([item(A, 'a'), item(B, 'b'), item(C, 'c')])).toEqual([A, B, C]);
    expect(chips()).toHaveLength(3);
    expect(text(root().querySelector('[data-testid="relation-more"]'))).toBe('+2 weitere');
  });

  it('render_TitleWithMarkup_IsShownAsText', async () => {
    await render([A]);
    await answer([item(A, '<img src=x onerror="window.__xss=1"><b>fett</b>')]);

    const link = chips()[0].querySelector('a')!;
    expect(link.textContent).toBe('<img src=x onerror="window.__xss=1"><b>fett</b>');
    expect(link.querySelector('img, b')).toBeNull();
    expect((window as unknown as { __xss?: number }).__xss).toBeUndefined();
  });

  it('render_NavigationNone_TitlesAsPlainText', async () => {
    await render([A], (h) => h.navigation.set('none'));
    await answer([item(A, 'Login bauen', true)]);

    expect(chips()[0].querySelector('a')).toBeNull();
    expect(text(chips()[0])).toBe('Login bauen (archiviert)');
    expect(chips()[0].querySelector('[data-testid="relation-chip-text"]')!.classList).toContain(
      'line-through',
    );
  });

  it('remove_Removable_EmitsTheIdAndNamesTheTitle', async () => {
    await render([A], (h) => h.removable.set(true));
    await answer([item(A, 'Login bauen')]);

    const button = root().querySelector<HTMLElement>('[data-testid="relation-remove"]')!;
    expect(button.getAttribute('aria-label')).toBe('Entfernen: Login bauen');
    button.click();
    expect(host.removed).toEqual([A]);
  });

  it('live_RenamedByAgent_UpdatesEveryChipOfTheRecord', async () => {
    await render([A, B, A]);
    await answer([item(A, 'Alt'), item(B, 'Zwei')]);

    realtime.emit(activityEvent({ targetId: A, diff: { title: { old: 'Alt', new: 'Neu' } } }));
    expect(await answer([item(A, 'Neu')])).toEqual([A]);

    expect(chips().map((c) => text(c))).toEqual(['Neu', 'Zwei', 'Neu']);
  });
});
