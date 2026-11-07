import { EnvironmentProviders, makeEnvironmentProviders } from '@angular/core';
import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { apiErrorInterceptor } from './api-error.interceptor';

// Tokens live in their own file so the interceptor and the client can import them without a cycle.
export { API_BASE_URL, HEALTH_URL } from './api-tokens';

/** HTTP setup for the app: fetch-based HttpClient with the API error interceptor. */
export function provideApi(): EnvironmentProviders {
  return makeEnvironmentProviders([
    provideHttpClient(withFetch(), withInterceptors([apiErrorInterceptor])),
  ]);
}
