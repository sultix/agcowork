import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** Temporary page for routes whose feature is not built yet. Title comes from route data. */
@Component({
  selector: 'ag-placeholder-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="p-6">
      <h1 class="text-2xl font-semibold tracking-tight">{{ title() }}</h1>
      <p class="mt-1 text-sm text-muted-foreground">Noch nicht umgesetzt.</p>
    </section>
  `,
})
export class PlaceholderPageComponent {
  readonly title = input('');
}
