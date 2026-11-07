import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { Router } from '@angular/router';
import { FieldTree, FormField, email, form, required, submit } from '@angular/forms/signals';
import { ApiError, NETWORK_ERROR_CODE } from '../../core/api/api-error';
import { toServerErrors } from '../../core/api/field-errors';
import { SessionStore } from '../../core/auth/session.store';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCircleAlert } from '@ng-icons/lucide';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCardImports } from '@spartan-ng/helm/card';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';

interface LoginModel {
  email: string;
  password: string;
}

/** Where to go after login when no (safe) `returnUrl` is given. */
export const DEFAULT_AFTER_LOGIN = '/workspaces';

/** Only app-internal paths are accepted as `returnUrl` (no `//host` or absolute URLs). */
export function safeReturnUrl(value: string | null | undefined): string {
  if (!value || !value.startsWith('/') || value.startsWith('//') || value.startsWith('/\\')) {
    return DEFAULT_AFTER_LOGIN;
  }
  return value.startsWith('/login') || value.startsWith('/account/password')
    ? DEFAULT_AFTER_LOGIN
    : value;
}

/** Same text for unknown email and wrong password: the UI must not reveal which accounts exist. */
export const INVALID_CREDENTIALS_MESSAGE = 'E-Mail oder Passwort falsch.';

/** Login with email and password (session cookie). Redirects to `returnUrl` on success. */
@Component({
  selector: 'ag-login',
  imports: [
    FormField,
    NgIcon,
    HlmAlertImports,
    HlmButton,
    HlmCardImports,
    HlmFieldImports,
    HlmInput,
  ],
  providers: [provideIcons({ lucideCircleAlert })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="flex min-h-svh items-center justify-center bg-muted p-6 md:p-10">
      <div class="flex w-full max-w-sm flex-col gap-6">
        <p class="text-center text-sm font-semibold tracking-tight">agcowork</p>
        <section hlmCard>
          <header hlmCardHeader>
            <h1 hlmCardTitle class="text-xl">Anmelden</h1>
            <p hlmCardDescription>Mit E-Mail-Adresse und Passwort.</p>
          </header>
          <div hlmCardContent>
            <form novalidate (submit)="onSubmit($event)">
              <div hlmFieldGroup class="gap-5">
                @if (formError(); as message) {
                  <div hlmAlert variant="destructive" data-testid="form-error">
                    <ng-icon name="lucideCircleAlert" />
                    <p hlmAlertDescription class="whitespace-pre-line">{{ message }}</p>
                  </div>
                }

                <div hlmField class="gap-2">
                  <label hlmFieldLabel for="login-email">E-Mail</label>
                  <input
                    hlmInput
                    id="login-email"
                    type="email"
                    autocomplete="username"
                    [formField]="form.email"
                    aria-describedby="login-email-errors"
                  />
                  <div id="login-email-errors" data-testid="email-errors">
                    @if (showErrors(form.email)) {
                      @for (error of form.email().errors(); track $index) {
                        <p class="text-sm text-destructive">{{ error.message }}</p>
                      }
                    }
                  </div>
                </div>

                <div hlmField class="gap-2">
                  <label hlmFieldLabel for="login-password">Passwort</label>
                  <input
                    hlmInput
                    id="login-password"
                    type="password"
                    autocomplete="current-password"
                    [formField]="form.password"
                    aria-describedby="login-password-errors"
                  />
                  <div id="login-password-errors" data-testid="password-errors">
                    @if (showErrors(form.password)) {
                      @for (error of form.password().errors(); track $index) {
                        <p class="text-sm text-destructive">{{ error.message }}</p>
                      }
                    }
                  </div>
                </div>

                <button hlmBtn type="submit" class="w-full" [disabled]="form().submitting()">
                  {{ form().submitting() ? 'Anmelden …' : 'Anmelden' }}
                </button>
              </div>
            </form>
          </div>
        </section>
      </div>
    </main>
  `,
})
export class LoginComponent {
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);

  /** Bound from the query parameter `returnUrl` (router input binding). */
  readonly returnUrl = input<string>();

  private readonly model = signal<LoginModel>({ email: '', password: '' });

  protected readonly form = form(this.model, (path) => {
    required(path.email, { message: 'Bitte die E-Mail-Adresse eingeben.' });
    email(path.email, { message: 'Bitte eine gültige E-Mail-Adresse eingeben.' });
    required(path.password, { message: 'Bitte das Passwort eingeben.' });
  });

  /** Error that belongs to no single field (wrong credentials, network, server). */
  protected readonly formError = signal<string | null>(null);

  protected showErrors(field: FieldTree<string>): boolean {
    return field().touched() && field().invalid();
  }

  protected async onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    this.formError.set(null);

    await submit(this.form, async (fields) => {
      const { email: address, password } = fields().value();
      try {
        await this.session.login(address, password);
      } catch (error) {
        const apiError = ApiError.from(error);
        const serverErrors = toServerErrors(apiError, {
          email: fields.email,
          password: fields.password,
        });
        const unmatched = serverErrors.filter((e) => !e.fieldTree);
        this.formError.set(
          serverErrors.length > 0
            ? unmatched.map((e) => e.message).join(' ') || null
            : messageFor(apiError),
        );
        return serverErrors.filter((e) => e.fieldTree);
      }
      await this.router.navigateByUrl(safeReturnUrl(this.returnUrl()));
      return undefined;
    });
  }
}

function messageFor(error: ApiError): string {
  if (error.status === 401) {
    return INVALID_CREDENTIALS_MESSAGE;
  }
  if (error.code === NETWORK_ERROR_CODE || [502, 503, 504].includes(error.status)) {
    return 'Server nicht erreichbar. Bitte später erneut versuchen.';
  }
  return error.detail ?? 'Anmeldung fehlgeschlagen. Bitte erneut versuchen.';
}
