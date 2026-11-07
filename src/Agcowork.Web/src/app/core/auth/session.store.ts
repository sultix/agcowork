import { Injectable, Injector, Signal, computed, inject, resource, signal } from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import { filter, firstValueFrom } from 'rxjs';
import { ApiClient } from '../api/api-client';
import { withoutAuthRedirect } from '../api/api-context';
import { ApiError } from '../api/api-error';
import type { components } from '../api/generated/schema';

/** The signed-in actor as returned by `GET /api/v1/me`. */
export type Me = components['schemas']['MeDto'];

/** `unknown` until the first `GET /me` has answered (or while it is re-checked). */
export type SessionStatus = 'unknown' | 'anonymous' | 'authenticated';

/**
 * Session state of the browser: who is signed in (cookie session), loaded from `GET /me`.
 * `load()` runs once at app start (see `provideSession()`); the auth guard waits for the
 * status via `ready()`; the error interceptor calls `clear()` when a request answers 401.
 */
@Injectable({ providedIn: 'root' })
export class SessionStore {
  private readonly api = inject(ApiClient);
  private readonly injector = inject(Injector);

  /** Stays idle (no request) until the first `load()`. */
  private readonly started = signal(false);

  private readonly meResource = resource<Me | null, true | undefined>({
    params: () => (this.started() ? true : undefined),
    loader: () => this.fetchMe(),
  });

  readonly me: Signal<Me | null> = computed(() =>
    this.meResource.hasValue() ? (this.meResource.value() ?? null) : null,
  );

  readonly status: Signal<SessionStatus> = computed(() => {
    switch (this.meResource.status()) {
      case 'resolved':
      case 'local':
        return this.meResource.value() ? 'authenticated' : 'anonymous';
      case 'error':
        // API unreachable or broken: treat as signed out; the login page reports the error on submit.
        return 'anonymous';
      default:
        // idle, loading, reloading
        return 'unknown';
    }
  });

  /**
   * (Re)loads the actor from `GET /me`; resolves with the resulting status. Always answers
   * from a request started by this call: if an earlier `GET /me` is still in flight (e.g. the
   * start-up probe, sent before a login set the cookie), it waits for it and then loads again,
   * because `resource.reload()` is a no-op while loading.
   */
  async load(): Promise<SessionStatus> {
    if (!this.started()) {
      this.started.set(true);
      return this.settled();
    }
    if (this.meResource.isLoading()) {
      await this.settled();
    }
    this.meResource.reload();
    return this.settled();
  }

  /** Resolves once the status is known; starts the first load if nobody did yet. */
  ready(): Promise<SessionStatus> {
    return this.started() ? this.settled() : this.load();
  }

  /**
   * Logs in with email and password (`POST /auth/login`, sets the session cookie) and loads
   * the actor. Rejects with an `ApiError` (401 for wrong credentials, 400 with `errors[]`).
   */
  async login(email: string, password: string): Promise<void> {
    await firstValueFrom(this.api.post('/auth/login', { body: { email, password } }), {
      defaultValue: undefined,
    });
    const status = await this.load();
    if (status !== 'authenticated') {
      // Cookie was not accepted (e.g. blocked); report like a failed login.
      throw new ApiError(401, 'unauthorized');
    }
  }

  /** Ends the session (`POST /auth/logout`); the local state is cleared even if the call fails. */
  async logout(): Promise<void> {
    try {
      await firstValueFrom(this.api.post('/auth/logout'), { defaultValue: undefined });
    } finally {
      this.clear();
    }
  }

  /** Forgets the actor locally (status becomes `anonymous`) without calling the API. */
  clear(): void {
    this.meResource.set(null);
  }

  private settled(): Promise<SessionStatus> {
    return firstValueFrom(
      toObservable(this.status, { injector: this.injector }).pipe(filter((s) => s !== 'unknown')),
    );
  }

  private async fetchMe(): Promise<Me | null> {
    try {
      return await firstValueFrom(this.api.get('/me', { context: withoutAuthRedirect() }));
    } catch (error) {
      if (ApiError.from(error).status === 401) {
        return null;
      }
      throw error;
    }
  }
}
