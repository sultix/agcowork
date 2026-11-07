import { Injectable, Signal, computed, inject } from '@angular/core';
import { httpResource } from '@angular/common/http';
import { HEALTH_URL } from './api.config';

export type BackendStatus = 'checking' | 'ok' | 'unreachable';

/** Loads the backend health endpoint once and exposes a simple status signal. */
@Injectable({ providedIn: 'root' })
export class HealthService {
  private readonly url = inject(HEALTH_URL);

  // Text response: ASP.NET health checks answer with plain text ("Healthy") by default.
  private readonly health = httpResource.text(() => this.url);

  readonly status: Signal<BackendStatus> = computed(() => {
    if (this.health.isLoading()) {
      return 'checking';
    }
    return this.health.error() ? 'unreachable' : 'ok';
  });

  reload(): void {
    this.health.reload();
  }
}
