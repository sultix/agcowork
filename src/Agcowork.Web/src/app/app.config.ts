import {
  ApplicationConfig,
  provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection,
} from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { provideApi } from './core/api/api.config';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // Zoneless is the Angular 22 default; stated explicitly to document the decision.
    provideZonelessChangeDetection(),
    provideRouter(routes, withComponentInputBinding()),
    provideApi(),
  ],
};
