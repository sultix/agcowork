import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** Error messages under a form field; renders nothing when the list is empty. */
@Component({
  selector: 'ag-field-errors',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @for (error of errors(); track $index) {
      <p class="mt-1.5 text-sm text-destructive">{{ error.message }}</p>
    }
  `,
})
export class FieldErrorsComponent {
  readonly errors = input<readonly { message?: string }[]>([]);
}
