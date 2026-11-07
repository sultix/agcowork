import { ChangeDetectionStrategy, Component, inject, input, output, signal } from '@angular/core';
import { FieldTree, FormField, form, submit } from '@angular/forms/signals';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCircleAlert } from '@ng-icons/lucide';
import type { BrnDialogState } from '@spartan-ng/brain/dialog';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCheckbox } from '@spartan-ng/helm/checkbox';
import { HlmDialogImports } from '@spartan-ng/helm/dialog';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmLabel } from '@spartan-ng/helm/label';
import { firstValueFrom } from 'rxjs';
import {
  MIN_PASSWORD_LENGTH,
  applyPasswordPolicy,
  applyRepeatRule,
} from '../../core/auth/password-policy';
import { ApiClient } from '../../core/api/api-client';
import type { components } from '../../core/api/generated/schema';
import { mapFormErrors } from '../../shared/ui/api-error-text';
import { FieldErrorsComponent } from '../../shared/ui/field-errors.component';

type Actor = components['schemas']['ActorDto'];

interface ResetModel {
  newPassword: string;
  repeat: string;
}

/**
 * Modal for an admin to set a human's password (`PUT /actors/{id}/password`): new password,
 * repetition and "must be changed at next login" (on by default). Emits `passwordSet` on success;
 * the parent removes the component on `passwordSet` and `cancelled`.
 */
@Component({
  selector: 'ag-reset-password-dialog',
  imports: [
    FormField,
    NgIcon,
    FieldErrorsComponent,
    HlmAlertImports,
    HlmButton,
    HlmCheckbox,
    HlmDialogImports,
    HlmFieldImports,
    HlmInput,
    HlmLabel,
  ],
  providers: [provideIcons({ lucideCircleAlert })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-dialog
      state="open"
      [closeOnOutsidePointerEvents]="false"
      (stateChanged)="onStateChanged($event)"
    >
      <hlm-dialog-content *hlmDialogPortal [showCloseButton]="false">
        <hlm-dialog-header>
          <h2 hlmDialogTitle>Passwort zurücksetzen</h2>
          <p hlmDialogDescription>
            Neues Passwort für {{ actor().name }}. Alle bestehenden Sitzungen werden abgemeldet.
          </p>
        </hlm-dialog-header>
        <form novalidate data-testid="reset-password-dialog" (submit)="onSubmit($event)">
          <div hlmFieldGroup class="gap-5">
            @if (formError(); as message) {
              <div hlmAlert variant="destructive" data-testid="form-error">
                <ng-icon name="lucideCircleAlert" />
                <p hlmAlertDescription class="whitespace-pre-line">{{ message }}</p>
              </div>
            }

            <div hlmField class="gap-2">
              <label hlmFieldLabel for="reset-new"
                >Neues Passwort (mind. {{ minLength }} Zeichen)</label
              >
              <input
                hlmInput
                id="reset-new"
                type="password"
                autocomplete="new-password"
                [formField]="form.newPassword"
              />
              <div data-testid="new-errors">
                @if (shown(form.newPassword)) {
                  <ag-field-errors [errors]="form.newPassword().errors()" />
                }
              </div>
            </div>

            <div hlmField class="gap-2">
              <label hlmFieldLabel for="reset-repeat">Passwort wiederholen</label>
              <input
                hlmInput
                id="reset-repeat"
                type="password"
                autocomplete="new-password"
                [formField]="form.repeat"
              />
              <div data-testid="repeat-errors">
                @if (shown(form.repeat)) {
                  <ag-field-errors [errors]="form.repeat().errors()" />
                }
              </div>
            </div>

            <div class="flex items-center gap-2">
              <hlm-checkbox
                inputId="reset-must-change"
                data-testid="must-change"
                [checked]="mustChange()"
                (checkedChange)="mustChange.set($event === true)"
              />
              <label hlmLabel class="font-normal" for="reset-must-change"
                >Muss beim nächsten Login geändert werden</label
              >
            </div>

            <hlm-dialog-footer>
              <button
                type="button"
                hlmBtn
                variant="outline"
                data-testid="reset-cancel"
                [disabled]="form().submitting()"
                (click)="cancelled.emit()"
              >
                Abbrechen
              </button>
              <button
                type="submit"
                hlmBtn
                data-testid="reset-submit"
                [disabled]="form().submitting()"
              >
                Passwort setzen
              </button>
            </hlm-dialog-footer>
          </div>
        </form>
      </hlm-dialog-content>
    </hlm-dialog>
  `,
})
export class ResetPasswordDialogComponent {
  private readonly api = inject(ApiClient);

  /** The human whose password is set. */
  readonly actor = input.required<Actor>();

  readonly passwordSet = output();
  readonly cancelled = output();

  protected readonly minLength = MIN_PASSWORD_LENGTH;
  protected readonly mustChange = signal(true);
  protected readonly formError = signal<string | null>(null);

  private readonly model = signal<ResetModel>({ newPassword: '', repeat: '' });
  protected readonly form = form(this.model, (path) => {
    applyPasswordPolicy(path.newPassword);
    applyRepeatRule(path.repeat, path.newPassword);
  });

  protected shown(field: FieldTree<string>): boolean {
    return field().touched() && field().invalid();
  }

  /** Brain closed the dialog (Escape): the parent removes the component. */
  protected onStateChanged(state: BrnDialogState): void {
    // While the PUT runs, Escape must not look like a cancel: the password still gets set.
    if (state === 'closed' && !this.form().submitting()) {
      this.cancelled.emit();
    }
  }

  protected async onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    this.formError.set(null);
    await submit(this.form, async (fields) => {
      try {
        await firstValueFrom(
          this.api.put('/actors/{id}/password', {
            path: { id: this.actor().id },
            body: { newPassword: fields().value().newPassword, mustChange: this.mustChange() },
          }),
          { defaultValue: undefined },
        );
      } catch (error) {
        const { fieldErrors, formMessage } = mapFormErrors(error, {
          newPassword: fields.newPassword,
        });
        this.formError.set(formMessage);
        return fieldErrors;
      }
      this.passwordSet.emit();
      return undefined;
    });
  }
}
