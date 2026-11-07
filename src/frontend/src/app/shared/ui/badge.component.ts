import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmBadge, type BadgeVariants } from '@spartan-ng/helm/badge';

export type BadgeTone = 'neutral' | 'accent' | 'success' | 'danger';

/** Helm variant per tone; `success` has no Helm variant and is rendered with its own colors. */
const TONE_VARIANTS: Record<Exclude<BadgeTone, 'success'>, BadgeVariants['variant']> = {
  neutral: 'secondary',
  accent: 'default',
  danger: 'destructive',
};

/**
 * Small label for roles, kinds and states: a Helm badge with a semantic tone. New code may also
 * use `hlmBadge` directly; this wrapper adds the `success` tone shadcn does not have.
 */
@Component({
  selector: 'ag-badge',
  imports: [HlmBadge, NgTemplateOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ng-template #content><ng-content /></ng-template>
    @if (tone() === 'success') {
      <span hlmBadge variant="secondary" class="bg-success/10 text-success dark:bg-success/20"
        ><ng-container [ngTemplateOutlet]="content"
      /></span>
    } @else {
      <span hlmBadge [variant]="variant()"><ng-container [ngTemplateOutlet]="content" /></span>
    }
  `,
})
export class BadgeComponent {
  readonly tone = input<BadgeTone>('neutral');
  protected readonly variant = computed(() => {
    const tone = this.tone();
    return tone === 'success' ? undefined : TONE_VARIANTS[tone];
  });
}
