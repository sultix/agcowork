import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { Router } from '@angular/router';
import { FieldTree, FormField, form, required, submit } from '@angular/forms/signals';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCircleAlert, lucideCircleCheck, lucideInfo } from '@ng-icons/lucide';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCardImports } from '@spartan-ng/helm/card';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';
import {
  MIN_PASSWORD_LENGTH,
  applyPasswordPolicy,
  applyRepeatRule,
} from '../../core/auth/password-policy';
import { SessionStore } from '../../core/auth/session.store';
import { mapFormErrors } from '../../shared/ui/api-error-text';
import { FieldErrorsComponent } from '../../shared/ui/field-errors.component';
import { safeReturnUrl } from './login.component';

interface PasswordModel {
  currentPassword: string;
  newPassword: string;
  repeat: string;
}

/**
 * `/account/password`: a human changes their own password (`PATCH /me/password`). Reached from
 * the session menu, or forced by `authGuard` while `GET /me` reports `mustChangePassword`
 * (then there is no way to cancel, only to log out).
 */
@Component({
  selector: 'ag-change-password',
  imports: [
    FormField,
    NgIcon,
    FieldErrorsComponent,
    HlmAlertImports,
    HlmButton,
    HlmCardImports,
    HlmFieldImports,
    HlmInput,
  ],
  providers: [provideIcons({ lucideCircleAlert, lucideCircleCheck, lucideInfo })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="flex min-h-svh items-center justify-center bg-muted p-6 md:p-10">
      <div class="flex w-full max-w-sm flex-col gap-6">
        <p class="text-center text-sm font-semibold tracking-tight">agcowork</p>
        <section hlmCard>
          <header hlmCardHeader>
            <h1 hlmCardTitle class="text-xl">Passwort ändern</h1>
            <p hlmCardDescription>Mindestens {{ minLength }} Zeichen.</p>
          </header>
          <div hlmCardContent>
            @if (done()) {
              <div class="flex flex-col gap-4">
                <div hlmAlert role="status" data-testid="success">
                  <ng-icon name="lucideCircleCheck" />
                  <p hlmAlertDescription>
                    Dein Passwort wurde geändert. Andere Sitzungen wurden abgemeldet.
                  </p>
                </div>
                <button
                  hlmBtn
                  #continueButton
                  type="button"
                  data-testid="continue"
                  (click)="leave()"
                >
                  Weiter
                </button>
              </div>
            } @else {
              <form novalidate (submit)="onSubmit($event)">
                <div hlmFieldGroup class="gap-5">
                  @if (forced()) {
                    <div hlmAlert role="status" data-testid="forced-hint">
                      <ng-icon name="lucideInfo" />
                      <p hlmAlertDescription>
                        Du meldest dich mit einem vorläufigen Passwort an. Bitte wähle ein eigenes,
                        bevor du weiterarbeitest.
                      </p>
                    </div>
                  }
                  @if (formError(); as message) {
                    <div hlmAlert variant="destructive" data-testid="form-error">
                      <ng-icon name="lucideCircleAlert" />
                      <p hlmAlertDescription class="whitespace-pre-line">{{ message }}</p>
                    </div>
                  }

                  <div hlmField class="gap-2">
                    <label hlmFieldLabel for="pw-current">Aktuelles Passwort</label>
                    <input
                      hlmInput
                      id="pw-current"
                      type="password"
                      autocomplete="current-password"
                      [formField]="form.currentPassword"
                    />
                    <div data-testid="current-errors">
                      @if (shown(form.currentPassword)) {
                        <ag-field-errors [errors]="form.currentPassword().errors()" />
                      }
                    </div>
                  </div>

                  <div hlmField class="gap-2">
                    <label hlmFieldLabel for="pw-new">Neues Passwort</label>
                    <input
                      hlmInput
                      id="pw-new"
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
                    <label hlmFieldLabel for="pw-repeat">Neues Passwort wiederholen</label>
                    <input
                      hlmInput
                      id="pw-repeat"
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

                  <div class="flex flex-col gap-2">
                    <button
                      hlmBtn
                      type="submit"
                      class="w-full"
                      data-testid="submit"
                      [disabled]="form().submitting()"
                    >
                      {{ form().submitting() ? 'Speichern …' : 'Passwort ändern' }}
                    </button>
                    @if (forced()) {
                      <button
                        hlmBtn
                        type="button"
                        variant="ghost"
                        class="w-full"
                        data-testid="logout"
                        (click)="logout()"
                      >
                        Abmelden
                      </button>
                    } @else {
                      <button
                        hlmBtn
                        type="button"
                        variant="ghost"
                        class="w-full"
                        data-testid="cancel"
                        (click)="leave()"
                      >
                        Abbrechen
                      </button>
                    }
                  </div>
                </div>
              </form>
            }
          </div>
        </section>
      </div>
    </main>
  `,
})
export class ChangePasswordComponent {
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);

  /** Bound from the query parameter `returnUrl` (router input binding). */
  readonly returnUrl = input<string>();

  protected readonly minLength = MIN_PASSWORD_LENGTH;

  private readonly continueButton = viewChild<ElementRef<HTMLButtonElement>>('continueButton');

  constructor() {
    // The form is replaced by the success message: keep keyboard and screen-reader users oriented.
    effect(() => this.continueButton()?.nativeElement.focus());
  }

  /** True while the server demands a new password; stays false once the change succeeded. */
  protected readonly done = signal(false);
  protected readonly forced = computed(
    () => !this.done() && (this.session.me()?.mustChangePassword ?? false),
  );

  private readonly model = signal<PasswordModel>({
    currentPassword: '',
    newPassword: '',
    repeat: '',
  });

  protected readonly form = form(this.model, (path) => {
    required(path.currentPassword, { message: 'Bitte das aktuelle Passwort eingeben.' });
    applyPasswordPolicy(path.newPassword);
    applyRepeatRule(path.repeat, path.newPassword);
  });

  protected readonly formError = signal<string | null>(null);

  protected shown(field: FieldTree<string>): boolean {
    return field().touched() && field().invalid();
  }

  protected async onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    this.formError.set(null);

    await submit(this.form, async (fields) => {
      const { currentPassword, newPassword } = fields().value();
      try {
        await this.session.changePassword(currentPassword, newPassword);
      } catch (error) {
        const { fieldErrors, formMessage } = mapFormErrors(error, {
          currentPassword: fields.currentPassword,
          newPassword: fields.newPassword,
        });
        this.formError.set(formMessage);
        return fieldErrors;
      }
      this.done.set(true);
      return undefined;
    });
  }

  protected async leave(): Promise<void> {
    await this.router.navigateByUrl(safeReturnUrl(this.returnUrl()));
  }

  protected async logout(): Promise<void> {
    try {
      await this.session.logout();
    } finally {
      await this.router.navigateByUrl('/login');
    }
  }
}
