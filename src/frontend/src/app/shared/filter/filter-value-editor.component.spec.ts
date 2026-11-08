import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import { RECORD_TITLE_BATCH_MS } from '../../core/records/record-title.store';
import { provideFakeRealtime } from '../../core/testing/fake-hub';
import {
  API,
  COLLECTION_ID,
  SPACE_ID,
  pageProviders,
  propertyDefinition,
  respond,
  respondPost,
  settle,
} from '../../core/testing/test-utils';
import { FilterBuilderComponent } from './filter-builder.component';
import { FilterNode } from './filter-dsl';

@Component({ template: '' })
class BlankComponent {}

const A = '0199a000-0000-7000-8000-00000000ab01';
const B = '0199a000-0000-7000-8000-00000000ab02';

const SCHEMA = [
  propertyDefinition({ key: 'title', name: 'Titel', type: 'text', required: true }),
  propertyDefinition({
    key: 'parent',
    name: 'Eltern',
    type: 'relation',
    targetCollectionId: COLLECTION_ID,
  }),
];

@Component({
  imports: [FilterBuilderComponent],
  template: `<ag-filter-builder [schema]="schema" [filter]="filter()" />`,
})
class HostComponent {
  readonly schema = SCHEMA;
  readonly filter = signal<FilterNode | null>(null);
}

describe('FilterValueEditorComponent (relation values)', () => {
  let fixture: ComponentFixture<HostComponent>;
  let http: HttpTestingController;

  async function render(filter: FilterNode): Promise<void> {
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({ routes: [{ path: 'w/:workspaceSlug', component: BlankComponent }] }),
        provideFakeRealtime(),
        { provide: RECORD_TITLE_BATCH_MS, useValue: 5 },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/w/demo');
    fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.filter.set(filter);
    fixture.detectChanges();
    await respond(http, 'GET', `${API}/members`, []);
  }

  afterEach(() => http.verify());

  const chips = () =>
    Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>(
        '[data-testid="relation-chip"]',
      ),
    ).map((c) => c.textContent!.replace(/\s+/g, ' ').trim());

  const item = (id: string, title: string) => ({
    id,
    title,
    collectionId: COLLECTION_ID,
    spaceId: SPACE_ID,
    archived: false,
  });

  it('render_RelationInCondition_ShowsTitlesNotIds', async () => {
    await render({ and: [{ prop: 'parent', op: 'in', value: [A, B] }] } as FilterNode);
    const lookup = await respondPost(http, `${API}/records/lookup`, {
      items: [item(A, 'Login bauen'), item(B, 'Logout bauen')],
    });
    await settle(fixture);

    expect((lookup.request.body as { ids: string[] }).ids).toEqual([A, B]);
    expect(chips()).toEqual(['Login bauen', 'Logout bauen']);
  });

  it('render_RelationContainsUnknownRecord_ShowsNotAvailable', async () => {
    await render({ and: [{ prop: 'parent', op: 'contains', value: A }] } as FilterNode);
    await respondPost(http, `${API}/records/lookup`, { items: [] });
    await settle(fixture);

    expect(chips()).toEqual(['Nicht verfügbar']);
  });
});
