import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** Temporary page for routes whose feature is not built yet. Title comes from route data. */
@Component({
  selector: 'ag-placeholder-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="p-6">
      <h1 class="text-xl font-semibold text-fg">{{ title() }}</h1>
      <p class="mt-2 text-sm text-muted">Noch nicht umgesetzt.</p>
    </section>
  `,
})
export class PlaceholderPageComponent {
  readonly title = input('');
}
