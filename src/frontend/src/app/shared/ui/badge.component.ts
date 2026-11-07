import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

export type BadgeTone = 'neutral' | 'accent' | 'success' | 'danger';

const TONE_CLASSES: Record<BadgeTone, string> = {
  neutral: 'border-border text-muted',
  accent: 'border-accent text-accent',
  success: 'border-success text-success',
  danger: 'border-danger text-danger',
};

/** Small label for roles, kinds and states. */
@Component({
  selector: 'ag-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="inline-block rounded-full border px-2 py-0.5 text-xs" [class]="classes()"
    ><ng-content
  /></span>`,
})
export class BadgeComponent {
  readonly tone = input<BadgeTone>('neutral');
  protected readonly classes = computed(() => TONE_CLASSES[this.tone()]);
}
