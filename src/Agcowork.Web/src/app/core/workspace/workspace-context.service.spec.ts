import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { WorkspaceContext } from './workspace-context.service';

@Component({ template: '' })
class BlankComponent {}

describe('WorkspaceContext', () => {
  let harness: RouterTestingHarness;
  let context: WorkspaceContext;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'workspaces', component: BlankComponent },
          {
            path: 'w/:workspaceSlug',
            children: [
              { path: '', component: BlankComponent },
              { path: 'spaces/:spaceId', component: BlankComponent },
            ],
          },
        ]),
      ],
    });
    harness = await RouterTestingHarness.create();
    context = TestBed.inject(WorkspaceContext);
  });

  it('slug_OutsideWorkspace_IsNull', async () => {
    await harness.navigateByUrl('/workspaces');
    expect(context.slug()).toBeNull();
  });

  it('slug_NestedWorkspaceRoute_ReturnsSlugFromParentParam', async () => {
    await harness.navigateByUrl('/w/demo/spaces/1');
    expect(context.slug()).toBe('demo');
  });

  it('slug_SwitchingWorkspaces_FollowsUrl', async () => {
    await harness.navigateByUrl('/w/alpha');
    expect(context.slug()).toBe('alpha');

    await harness.navigateByUrl('/w/beta/spaces/7');
    expect(context.slug()).toBe('beta');

    await harness.navigateByUrl('/workspaces');
    expect(context.slug()).toBeNull();
  });

  it('slug_ServiceCreatedAfterNavigation_HasInitialValue', async () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: 'w/:workspaceSlug', component: BlankComponent }])],
    });
    const lateHarness = await RouterTestingHarness.create('/w/late');
    expect(lateHarness).toBeTruthy();
    expect(TestBed.inject(WorkspaceContext).slug()).toBe('late');
  });
});
