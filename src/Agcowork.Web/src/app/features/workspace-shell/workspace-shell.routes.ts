import { Routes } from '@angular/router';
import { recordLeaveGuard } from '../records/record-leave.guard';

const placeholder = () =>
  import('../../shared/ui/placeholder-page.component').then((m) => m.PlaceholderPageComponent);

/** Child routes of `/w/:workspaceSlug`. Placeholders until the features exist. */
export const workspaceShellRoutes: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./workspace-shell.component').then((m) => m.WorkspaceShellComponent),
    children: [
      {
        path: '',
        loadComponent: () =>
          import('../spaces/workspace-home.component').then((m) => m.WorkspaceHomeComponent),
      },
      {
        path: 'spaces/:spaceId',
        loadComponent: () =>
          import('../spaces/space-page.component').then((m) => m.SpacePageComponent),
      },
      {
        path: 'collections/:collectionId',
        loadComponent: () =>
          import('../collections/collection-page.component').then((m) => m.CollectionPageComponent),
      },
      {
        path: 'records/:recordId',
        canDeactivate: [recordLeaveGuard],
        loadComponent: () =>
          import('../records/record-document.component').then((m) => m.RecordDocumentComponent),
      },
      {
        path: 'records/:recordId/raw',
        canDeactivate: [recordLeaveGuard],
        loadComponent: () =>
          import('../records/raw/record-raw.component').then((m) => m.RecordRawComponent),
      },
      {
        path: 'records/:recordId/history',
        loadComponent: () =>
          import('../records/history/record-history.component').then(
            (m) => m.RecordHistoryComponent,
          ),
      },
      { path: 'search', loadComponent: placeholder, data: { title: 'Suche' } },
      {
        path: 'activity',
        loadComponent: () =>
          import('../activity/activity-feed.component').then((m) => m.ActivityFeedComponent),
      },
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
