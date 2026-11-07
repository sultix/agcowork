import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';

/** Tab bar of the record views: Dokument | Rohdaten | Historie. */
@Component({
  selector: 'ag-record-tabs',
  imports: [RouterLink, RouterLinkActive],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav class="mt-3 flex gap-1 border-b border-border text-sm" aria-label="Ansicht">
      @for (tab of tabs(); track tab.testId) {
        <a
          [routerLink]="tab.link"
          routerLinkActive="border-accent! text-fg! font-medium"
          ariaCurrentWhenActive="page"
          [routerLinkActiveOptions]="{ exact: true }"
          class="-mb-px border-b-2 border-transparent px-3 py-1.5 text-muted hover:text-fg"
          [attr.data-testid]="tab.testId"
          >{{ tab.label }}</a
        >
      }
    </nav>
  `,
})
export class RecordTabsComponent {
  private readonly context = inject(WorkspaceContext);

  readonly recordId = input.required<string>();

  protected readonly tabs = computed(() => {
    const base = ['/w', this.context.slug() ?? '', 'records', this.recordId()];
    return [
      { label: 'Dokument', link: base, testId: 'tab-document' },
      { label: 'Rohdaten', link: [...base, 'raw'], testId: 'tab-raw' },
      { label: 'Historie', link: [...base, 'history'], testId: 'tab-history' },
    ];
  });
}
