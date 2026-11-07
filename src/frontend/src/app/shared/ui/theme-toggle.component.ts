import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideMonitor, lucideMoon, lucideSun } from '@ng-icons/lucide';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmDropdownMenuImports } from '@spartan-ng/helm/dropdown-menu';
import { THEME_MODES, ThemeMode, ThemeService } from '../../core/theme/theme.service';

const MODE_LABELS: Record<ThemeMode, string> = { system: 'System', light: 'Hell', dark: 'Dunkel' };
const MODE_ICONS: Record<ThemeMode, string> = {
  system: 'lucideMonitor',
  light: 'lucideSun',
  dark: 'lucideMoon',
};

/** Icon button with a dropdown to choose the theme mode (System / Hell / Dunkel). */
@Component({
  selector: 'ag-theme-toggle',
  imports: [NgIcon, HlmButton, HlmDropdownMenuImports],
  providers: [provideIcons({ lucideMonitor, lucideMoon, lucideSun })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      hlmBtn
      variant="ghost"
      size="icon"
      data-testid="theme-toggle"
      [attr.aria-label]="'Farbschema: ' + label()"
      [hlmDropdownMenuTrigger]="menu"
      align="end"
    >
      <ng-icon [name]="icon()" />
    </button>
    <ng-template #menu>
      <hlm-dropdown-menu class="w-40" data-testid="theme-menu">
        <div hlmDropdownMenuLabel>Farbschema</div>
        <hlm-dropdown-menu-group>
          @for (mode of modes; track mode) {
            <button
              hlmDropdownMenuRadio
              [checked]="theme.mode() === mode"
              [keepOpen]="false"
              [attr.data-testid]="'theme-option-' + mode"
              (triggered)="theme.setMode(mode)"
            >
              <ng-icon [name]="icons[mode]" />
              {{ labels[mode] }}
              <hlm-dropdown-menu-radio-indicator />
            </button>
          }
        </hlm-dropdown-menu-group>
      </hlm-dropdown-menu>
    </ng-template>
  `,
})
export class ThemeToggleComponent {
  protected readonly theme = inject(ThemeService);

  protected readonly modes = THEME_MODES;
  protected readonly labels = MODE_LABELS;
  protected readonly icons = MODE_ICONS;
  protected readonly label = computed(() => MODE_LABELS[this.theme.mode()]);
  protected readonly icon = computed(() => MODE_ICONS[this.theme.mode()]);
}
