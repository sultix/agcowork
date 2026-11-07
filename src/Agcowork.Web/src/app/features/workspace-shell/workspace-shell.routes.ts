import { Routes } from '@angular/router';

const placeholder = () =>
  import('../../shared/ui/placeholder-page.component').then((m) => m.PlaceholderPageComponent);

/** Child routes of `/w/:workspaceSlug`. Placeholders until the features exist. */
export const workspaceShellRoutes: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./workspace-shell.component').then((m) => m.WorkspaceShellComponent),
    children: [
      { path: '', loadComponent: placeholder, data: { title: 'Workspace-Start' } },
      { path: 'spaces/:spaceId', loadComponent: placeholder, data: { title: 'Space' } },
      { path: 'search', loadComponent: placeholder, data: { title: 'Suche' } },
      { path: 'activity', loadComponent: placeholder, data: { title: 'Aktivität' } },
      {
        path: 'settings',
        loadComponent: () =>
          import('../workspace-settings/workspace-settings.component').then(
            (m) => m.WorkspaceSettingsComponent,
          ),
      },
      {
        path: 'settings/members',
        loadComponent: () =>
          import('../workspace-settings/members.component').then((m) => m.MembersComponent),
      },
    ],
  },
];
