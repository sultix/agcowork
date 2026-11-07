import { InjectionToken } from '@angular/core';

/** Base URL of the versioned REST API. Workspace prefixes are appended by the API client. */
export const API_BASE_URL = new InjectionToken<string>('API_BASE_URL', {
  providedIn: 'root',
  factory: () => '/api/v1',
});

/** URL of the backend health endpoint (outside the versioned API). */
export const HEALTH_URL = new InjectionToken<string>('HEALTH_URL', {
  providedIn: 'root',
  factory: () => '/health',
});
