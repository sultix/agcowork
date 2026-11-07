import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmBreadcrumbImports } from '@spartan-ng/helm/breadcrumb';

export interface Crumb {
  readonly label: string;
  /** Router link; the last crumb (current page) has none. */
  readonly link?: readonly string[];
}

/** Path above a page title, e.g. `Produkt › Aufgaben` (Helm breadcrumb). */
@Component({
  selector: 'ag-breadcrumbs',
  imports: [HlmBreadcrumbImports],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav hlmBreadcrumb aria-label="Pfad" data-testid="breadcrumbs">
      <ol hlmBreadcrumbList>
        @for (crumb of crumbs(); track $index; let last = $last) {
          <li hlmBreadcrumbItem>
            @if (crumb.link && !last) {
              <a hlmBreadcrumbLink [link]="crumb.link">{{ crumb.label }}</a>
            } @else if (last) {
              <span hlmBreadcrumbPage>{{ crumb.label }}</span>
            } @else {
              <span>{{ crumb.label }}</span>
            }
          </li>
          @if (!last) {
            <!-- Text separator (instead of the default chevron icon) keeps the path readable as text. -->
            <li hlmBreadcrumbSeparator>›</li>
          }
        }
      </ol>
    </nav>
  `,
})
export class BreadcrumbsComponent {
  readonly crumbs = input<readonly Crumb[]>([]);
}
