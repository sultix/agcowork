import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { Router } from '@angular/router';
import { FieldTree, FormField, email, form, required, submit } from '@angular/forms/signals';
import { ApiError, NETWORK_ERROR_CODE } from '../../core/api/api-error';
import { toServerErrors } from '../../core/api/field-errors';
import { SessionStore } from '../../core/auth/session.store';

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
  return value.startsWith('/login') ? DEFAULT_AFTER_LOGIN : value;
}

/** Same text for unknown email and wrong password: the UI must not reveal which accounts exist. */
export const INVALID_CREDENTIALS_MESSAGE = 'E-Mail oder Passwort falsch.';

/** Login with email and password (session cookie). Redirects to `returnUrl` on success. */
@Component({
  selector: 'ag-login',
  imports: [FormField],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="flex min-h-screen items-center justify-center p-6">
      <form
        class="w-full max-w-sm rounded-lg border border-border bg-surface p-6"
        novalidate
        (submit)="onSubmit($event)"
      >
        <h1 class="text-lg font-semibold">Anmelden</h1>

        @if (formError(); as message) {
          <p
            class="mt-4 rounded-md border border-danger px-3 py-2 text-sm text-danger"
            role="alert"
            data-testid="form-error"
          >
            {{ message }}
          </p>
        }

        <label for="login-email" class="mt-4 block text-sm font-medium">E-Mail</label>
        <input
          id="login-email"
          type="email"
          autocomplete="username"
          class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 text-sm"
          [formField]="form.email"
          [attr.aria-invalid]="showErrors(form.email)"
          aria-describedby="login-email-errors"
        />
        <div id="login-email-errors" data-testid="email-errors">
          @if (showErrors(form.email)) {
            @for (error of form.email().errors(); track $index) {
              <p class="mt-1 text-sm text-danger">{{ error.message }}</p>
            }
          }
        </div>

        <label for="login-password" class="mt-4 block text-sm font-medium">Passwort</label>
        <input
          id="login-password"
          type="password"
          autocomplete="current-password"
          class="mt-1 w-full rounded-md border border-border bg-bg px-3 py-2 text-sm"
          [formField]="form.password"
          [attr.aria-invalid]="showErrors(form.password)"
          aria-describedby="login-password-errors"
        />
        <div id="login-password-errors" data-testid="password-errors">
          @if (showErrors(form.password)) {
            @for (error of form.password().errors(); track $index) {
              <p class="mt-1 text-sm text-danger">{{ error.message }}</p>
            }
          }
        </div>

        <button
          type="submit"
          class="mt-6 w-full rounded-md bg-accent px-3 py-2 text-sm font-medium text-accent-fg disabled:opacity-60"
          [disabled]="form().submitting()"
        >
          {{ form().submitting() ? 'Anmelden …' : 'Anmelden' }}
        </button>
      </form>
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
