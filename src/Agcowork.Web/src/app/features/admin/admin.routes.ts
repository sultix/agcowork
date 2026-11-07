import { Routes } from '@angular/router';

/** Child routes of `/admin` (guarded by `instanceAdminGuard` in the app routes). */
export const adminRoutes: Routes = [
  {
    path: '',
    loadComponent: () => import('./admin-shell.component').then((m) => m.AdminShellComponent),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'actors' },
      {
        path: 'workspaces',
        data: { embedded: true },
        loadComponent: () =>
          import('../workspaces/workspace-list.component').then((m) => m.WorkspaceListComponent),
      },
      {
        path: 'actors',
        loadComponent: () => import('./actors.component').then((m) => m.ActorsComponent),
      },
      {
        path: 'actors/:id/keys',
        loadComponent: () => import('./actor-keys.component').then((m) => m.ActorKeysComponent),
      },
    ],
  },
];
