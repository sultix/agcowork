import { EnvironmentProviders, inject, provideAppInitializer } from '@angular/core';
import { SessionStore } from './session.store';

/**
 * Starts loading the session once at app start. Not awaited: the app renders immediately,
 * guarded routes wait for the status themselves.
 */
export function provideSession(): EnvironmentProviders {
  return provideAppInitializer(() => {
    void inject(SessionStore).load();
  });
}
