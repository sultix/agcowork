import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';

export interface Crumb {
  readonly label: string;
  /** Router link; the last crumb (current page) has none. */
  readonly link?: readonly string[];
}

/** Path above a page title, e.g. `Produkt › Aufgaben`. */
@Component({
  selector: 'ag-breadcrumbs',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav
      aria-label="Pfad"
      class="flex flex-wrap items-center gap-1 text-sm text-muted"
      data-testid="breadcrumbs"
    >
      @for (crumb of crumbs(); track $index; let last = $last) {
        @if (crumb.link && !last) {
          <a [routerLink]="crumb.link" class="hover:underline">{{ crumb.label }}</a>
        } @else {
          <span [attr.aria-current]="last ? 'page' : null">{{ crumb.label }}</span>
        }
        @if (!last) {
          <span aria-hidden="true">›</span>
        }
      }
    </nav>
  `,
})
export class BreadcrumbsComponent {
  readonly crumbs = input<readonly Crumb[]>([]);
}
