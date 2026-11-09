import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';

/** Switch between the settings pages of a collection: its schema and its record templates. */
@Component({
  selector: 'ag-collection-settings-nav',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav aria-label="Einstellungen der Collection" class="flex gap-1 border-b">
      @for (tab of tabs(); track tab.id) {
        <a
          class="-mb-px border-b-2 px-3 py-2 text-sm font-medium"
          [class]="
            tab.id === active()
              ? 'border-primary text-foreground'
              : 'border-transparent text-muted-foreground hover:text-foreground'
          "
          [attr.data-testid]="'settings-tab-' + tab.id"
          [attr.aria-current]="tab.id === active() ? 'page' : null"
          [routerLink]="tab.link"
          >{{ tab.label }}</a
        >
      }
    </nav>
  `,
})
export class CollectionSettingsNavComponent {
  private readonly context = inject(WorkspaceContext);

  readonly collectionId = input.required<string>();
  readonly active = input.required<'schema' | 'templates'>();

  protected readonly tabs = computed(() => {
    const base = ['/w', this.context.slug() ?? '', 'settings', 'collections', this.collectionId()];
    return [
      { id: 'schema', label: 'Schema', link: [...base, 'schema'] },
      { id: 'templates', label: 'Vorlagen', link: [...base, 'templates'] },
    ];
  });
}
