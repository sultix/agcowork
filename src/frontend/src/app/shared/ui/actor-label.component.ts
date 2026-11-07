import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { MembersStore } from '../../core/workspace/members.store';
import { BadgeComponent } from './badge.component';

/** Name of an actor with a badge for its kind (Mensch / Agent). */
@Component({
  selector: 'ag-actor-label',
  imports: [BadgeComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="font-medium" data-testid="actor-name">{{ members.nameOf(actorId()) }}</span>
    @if (members.kindOf(actorId()); as kind) {
      <ag-badge [tone]="kind === 'agent' ? 'accent' : 'neutral'"
        ><span data-testid="actor-kind">{{ kind === 'agent' ? 'Agent' : 'Mensch' }}</span></ag-badge
      >
    }
  `,
})
export class ActorLabelComponent {
  protected readonly members = inject(MembersStore);

  readonly actorId = input.required<string>();
}
