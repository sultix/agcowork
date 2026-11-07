import { Injectable, Signal, computed, effect, inject, linkedSignal } from '@angular/core';
import { HttpErrorResponse, httpResource } from '@angular/common/http';
import { HEALTH_URL } from './api-tokens';

/**
 * `ok`: healthy. `degraded`: the backend answers but reports a problem (`Degraded`,
 * `Unhealthy`, any 5xx with a body). `unreachable`: no answer (network error, gateway down).
 */
export type BackendStatus = 'checking' | 'ok' | 'degraded' | 'unreachable';

/** While the backend is not `ok` the health endpoint is asked again at this interval. */
export const HEALTH_RETRY_MS = 15_000;

/** Gateway statuses: a proxy answered, but the backend behind it did not. */
const UNREACHABLE_STATUSES = [0, 502, 504];

/**
 * Loads the backend health endpoint and exposes a status signal. While the backend is degraded
 * or unreachable it polls every {@link HEALTH_RETRY_MS} until it is `ok` again; in the `ok`
 * state there is no polling. `reload()` asks immediately.
 */
@Injectable({ providedIn: 'root' })
export class HealthService {
  private readonly url = inject(HEALTH_URL);

  // Text response: ASP.NET health checks answer with plain text ("Healthy") by default.
  private readonly health = httpResource.text(() => this.url);

  /** The state of the latest answer; `checking` while a request is in flight. */
  private readonly latest = computed<BackendStatus>(() => {
    if (this.health.isLoading()) {
      return 'checking';
    }
    const error = this.health.error();
    if (error) {
      const status = error instanceof HttpErrorResponse ? error.status : 0;
      return UNREACHABLE_STATUSES.includes(status) ? 'unreachable' : 'degraded';
    }
    return this.health.hasValue() && this.health.value().trim() === 'Degraded' ? 'degraded' : 'ok';
  });

  /** Like `latest`, but a re-check keeps showing the previous state instead of flickering. */
  readonly status: Signal<BackendStatus> = linkedSignal<BackendStatus, BackendStatus>({
    source: this.latest,
    computation: (latest, previous) =>
      latest === 'checking' && previous ? previous.value : latest,
  });

  constructor() {
    effect((onCleanup) => {
      const latest = this.latest();
      if (latest === 'degraded' || latest === 'unreachable') {
        const timer = setTimeout(() => this.reload(), HEALTH_RETRY_MS);
        onCleanup(() => clearTimeout(timer));
      }
    });
  }

  reload(): void {
    this.health.reload();
  }
}
