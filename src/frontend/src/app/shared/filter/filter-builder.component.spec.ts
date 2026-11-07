import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { pageProviders, propertyDefinition, settle, typeInto } from '../../core/testing/test-utils';
import { FilterBuilderComponent } from './filter-builder.component';
import { FilterNode } from './filter-dsl';

const SCHEMA = [
  propertyDefinition({ key: 'title', name: 'Titel', type: 'text', required: true }),
  propertyDefinition({
    key: 'status',
    name: 'Status',
    type: 'status',
    options: [
      { value: 'open', label: 'Offen', color: null, group: 'todo' },
      { value: 'in_progress', label: 'In Arbeit', color: null, group: 'in_progress' },
      { value: 'done', label: 'Erledigt', color: null, group: 'done' },
    ],
  }),
  propertyDefinition({
    key: 'priority',
    name: 'Priorität',
    type: 'select',
    options: [
      { value: 'low', label: 'Niedrig', color: null, group: null },
      { value: 'high', label: 'Hoch', color: null, group: null },
    ],
  }),
  propertyDefinition({ key: 'due', name: 'Fällig', type: 'date' }),
  propertyDefinition({ key: 'estimate', name: 'Schätzung', type: 'number' }),
  propertyDefinition({ key: 'done', name: 'Fertig', type: 'checkbox' }),
];

@Component({
  imports: [FilterBuilderComponent],
  template: `<ag-filter-builder
    [schema]="schema"
    [filter]="filter()"
    (filterChange)="onChange($event)"
  />`,
})
class HostComponent {
  readonly schema = SCHEMA;
  readonly filter = signal<FilterNode | null>(null);
  readonly emitted: (FilterNode | null)[] = [];

  onChange(filter: FilterNode | null): void {
    this.emitted.push(filter);
    this.filter.set(filter); // as the view page does
  }
}

describe('FilterBuilderComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;

  async function render(filter: FilterNode | null = null): Promise<void> {
    TestBed.configureTestingModule({ providers: pageProviders({ routes: [] }) });
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    host.filter.set(filter);
    await settle(fixture);
  }

  const root = () => fixture.nativeElement as HTMLElement;
  const all = <T extends HTMLElement = HTMLElement>(id: string, scope: ParentNode = root()) =>
    Array.from(scope.querySelectorAll<T>(`[data-testid="${id}"]`));
  const one = <T extends HTMLElement = HTMLElement>(id: string, scope: ParentNode = root()) =>
    scope.querySelector<T>(`[data-testid="${id}"]`)!;
  const last = () => host.emitted[host.emitted.length - 1];

  async function click(element: HTMLElement): Promise<void> {
    element.click();
    await settle(fixture);
  }

  async function choose(select: HTMLSelectElement, value: string): Promise<void> {
    typeInto(select, value);
    await settle(fixture);
  }

  it('build_AcceptanceFilter_EmitsDslOfArchitecture', async () => {
    await render();

    // status in [open, in_progress]
    await click(one('filter-add'));
    let condition = all('filter-condition')[0];
    await choose(one<HTMLSelectElement>('filter-prop', condition), 'status');
    expect(
      Array.from(one<HTMLSelectElement>('filter-op', condition).options).map((o) => o.value),
    ).toEqual(['eq', 'neq', 'in', 'nin', 'is_empty', 'is_not_empty']);
    await choose(one<HTMLSelectElement>('filter-op', condition), 'in');
    await click(condition.querySelector<HTMLInputElement>('[data-option="open"]')!);
    await click(condition.querySelector<HTMLInputElement>('[data-option="in_progress"]')!);

    // (due < 2026-12-01 or priority = high)
    await click(one('filter-add-group'));
    const group = one('filter-group');
    expect(one<HTMLSelectElement>('filter-group-kind', group).value).toBe('or');
    condition = all('filter-condition', group)[0];
    await choose(one<HTMLSelectElement>('filter-prop', condition), 'due');
    await choose(one<HTMLSelectElement>('filter-op', condition), 'lt');
    typeInto(condition.querySelector<HTMLInputElement>('input[type="date"]')!, '2026-12-01');
    await settle(fixture);
    await click(one('filter-group-add', group));
    condition = all('filter-condition', group)[1];
    await choose(one<HTMLSelectElement>('filter-prop', condition), 'priority');
    await choose(one<HTMLSelectElement>('editor-select', condition), 'high');

    const expected = {
      and: [
        { prop: 'status', op: 'in', value: ['open', 'in_progress'] },
        {
          or: [
            { prop: 'due', op: 'lt', value: '2026-12-01' },
            { prop: 'priority', op: 'eq', value: 'high' },
          ],
        },
      ],
    };
    expect(JSON.stringify(last())).toBe(JSON.stringify(expected));
    expect(JSON.parse(one('filter-json-text').textContent!)).toEqual(expected);
    expect(all('filter-incomplete')).toHaveLength(0);
  });

  it('render_GivenDsl_ShowsConditionsWithoutEmitting_RoundTripIdentical', async () => {
    const dsl: FilterNode = {
      and: [
        { prop: 'estimate', op: 'gte', value: 3 },
        {
          or: [
            { prop: 'done', op: 'eq', value: false },
            { prop: 'due', op: 'is_empty' },
          ],
        },
      ],
    };
    await render(dsl);

    expect(host.emitted).toEqual([]);
    expect(all('filter-condition')).toHaveLength(3);
    const [estimate, done, due] = all('filter-condition');
    expect(one<HTMLSelectElement>('filter-prop', estimate).value).toBe('estimate');
    expect(one<HTMLSelectElement>('filter-op', estimate).value).toBe('gte');
    expect(one<HTMLInputElement>('editor-input', estimate).value).toBe('3');
    expect(one<HTMLSelectElement>('filter-value-boolean', done).value).toBe('false');
    expect(one<HTMLSelectElement>('filter-op', due).value).toBe('is_empty');
    expect(JSON.stringify(JSON.parse(one('filter-json-text').textContent!))).toBe(
      JSON.stringify(dsl),
    );

    // a change elsewhere re-emits the untouched parts unchanged
    await choose(one<HTMLSelectElement>('filter-root-kind'), 'or');
    expect(JSON.stringify(last())).toBe(JSON.stringify({ or: dsl.and }));
  });

  it('addCondition_Incomplete_StaysWithoutEmittingAndIsMarked', async () => {
    await render();

    await click(one('filter-add'));
    await choose(one<HTMLSelectElement>('filter-prop'), 'title');

    expect(host.emitted).toEqual([]);
    expect(all('filter-condition')).toHaveLength(1);
    expect(all('filter-incomplete')).toHaveLength(1);
  });

  it('changeOperator_ToEmptyCheck_EmitsWithoutValue', async () => {
    await render({ and: [{ prop: 'title', op: 'contains', value: 'Login' }] });

    await choose(one<HTMLSelectElement>('filter-op'), 'is_empty');

    expect(last()).toEqual({ and: [{ prop: 'title', op: 'is_empty' }] });
  });

  it('listValue_NumberIn_ParsesCommaSeparatedNumbers', async () => {
    await render({ and: [{ prop: 'estimate', op: 'eq', value: 1 }] });

    await choose(one<HTMLSelectElement>('filter-op'), 'in');
    typeInto(one<HTMLInputElement>('filter-value-list'), '1, 2,x, 5');
    await settle(fixture);

    expect(last()).toEqual({ and: [{ prop: 'estimate', op: 'in', value: [1, 2, 5] }] });
  });

  it('remove_And_Clear_EmitShrunkAndNullFilter', async () => {
    await render({
      and: [
        { prop: 'title', op: 'eq', value: 'a' },
        { prop: 'due', op: 'is_empty' },
      ],
    });

    await click(all('filter-remove')[0]);
    expect(last()).toEqual({ and: [{ prop: 'due', op: 'is_empty' }] });

    await click(one('filter-clear'));
    expect(last()).toBeNull();
    expect(one('filter-empty')).not.toBeNull();
  });

  it('render_NotFilter_IsShownAsUnsupportedButClearable', async () => {
    await render({ not: { prop: 'status', op: 'eq', value: 'done' } });

    expect(one('filter-unsupported')).not.toBeNull();
    expect(all('filter-condition')).toHaveLength(0);
    expect(one('filter-json-text').textContent).toContain('"not"');

    await click(one('filter-clear'));
    expect(last()).toBeNull();
  });
});
