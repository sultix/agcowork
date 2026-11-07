import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { MembersStore } from '../../core/workspace/members.store';
import { PropertyValue, asText } from './property-types';

/** Select for `actor` properties: the members (people and agents) of the workspace. */
@Component({
  selector: 'ag-actor-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <select
      class="w-full rounded-md border border-border bg-bg px-2 py-1 text-sm disabled:opacity-70"
      data-testid="actor-select"
      [attr.aria-label]="label()"
      [disabled]="readonly()"
      (change)="onChange($any($event.target).value)"
    >
      <option value="" [selected]="current() === ''">–</option>
      @for (choice of choices(); track choice.id) {
        <option [value]="choice.id" [selected]="choice.id === current()">{{ choice.label }}</option>
      }
    </select>
  `,
})
export class ActorEditorComponent {
  private readonly members = inject(MembersStore);

  readonly value = input<PropertyValue>(null);
  readonly readonly = input(false);
  readonly label = input('Actor');
  readonly valueChange = output<string | null>();

  protected readonly current = computed(() => asText(this.value()));
  protected readonly choices = computed(() => {
    const list = this.members.members().map((m) => ({
      id: m.actorId,
      label: `${m.name} (${m.kind === 'agent' ? 'Agent' : 'Mensch'})`,
    }));
    const current = this.current();
    return current && !list.some((c) => c.id === current)
      ? [...list, { id: current, label: current.slice(0, 8) }]
      : list;
  });

  protected onChange(value: string): void {
    this.valueChange.emit(value === '' ? null : value);
  }
}
