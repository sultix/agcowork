import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';

/** Tab bar of the record views: Dokument | Rohdaten | Historie. */
@Component({
  selector: 'ag-record-tabs',
  imports: [RouterLink, RouterLinkActive],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav
      class="mt-3 inline-flex w-fit items-center gap-1 rounded-lg bg-muted p-[3px] text-muted-foreground"
      aria-label="Ansicht"
    >
      @for (tab of tabs(); track tab.testId) {
        <a
          [routerLink]="tab.link"
          routerLinkActive="bg-background! text-foreground! shadow-sm! dark:bg-input/30! dark:border-input!"
          ariaCurrentWhenActive="page"
          [routerLinkActiveOptions]="{ exact: true }"
          class="inline-flex h-8 items-center rounded-md border border-transparent px-2.5 text-sm font-medium whitespace-nowrap outline-none transition-all hover:text-foreground focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50"
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
