import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';
import { WORKSPACE_SLUG_PARAM } from './core/workspace/workspace-context.service';

export const routes: Routes = [
  {
    path: 'login',
    loadComponent: () => import('./features/auth/login.component').then((m) => m.LoginComponent),
  },
  {
    path: 'workspaces',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/workspaces/workspace-switcher.component').then(
        (m) => m.WorkspaceSwitcherComponent,
      ),
  },
  {
    path: `w/:${WORKSPACE_SLUG_PARAM}`,
    canActivate: [authGuard],
    loadChildren: () =>
      import('./features/workspace-shell/workspace-shell.routes').then(
        (m) => m.workspaceShellRoutes,
      ),
  },
  {
    // Manual test bench for editor building blocks; intentionally not linked in the navigation.
    path: 'dev/playground',
    loadComponent: () =>
      import('./features/dev-playground/dev-playground.component').then(
        (m) => m.DevPlaygroundComponent,
      ),
  },
  { path: '', pathMatch: 'full', redirectTo: 'workspaces' },
  { path: '**', redirectTo: 'workspaces' },
];
