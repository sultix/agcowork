import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';
import { instanceAdminGuard } from './features/admin/admin.guard';
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
      import('./features/workspaces/workspace-list.component').then(
        (m) => m.WorkspaceListComponent,
      ),
  },
  {
    path: 'workspaces/new',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/workspaces/workspace-create.component').then(
        (m) => m.WorkspaceCreateComponent,
      ),
  },
  {
    path: 'admin',
    canActivate: [authGuard, instanceAdminGuard],
    loadChildren: () => import('./features/admin/admin.routes').then((m) => m.adminRoutes),
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
