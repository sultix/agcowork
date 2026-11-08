import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { FakeRealtime, provideFakeRealtime } from '../../core/testing/fake-hub';
import { COLLECTION_ID, activityEvent, pageProviders } from '../../core/testing/test-utils';
import { CollectionStore } from './collection.store';
import { LIVE_REFRESH_DELAY_MS, LIVE_REFRESH_MAX_WAIT_MS, liveRefresh } from './live-refresh';

@Component({ template: '' })
class HostComponent {
  readonly refreshes: number[] = [];
  readonly blocked = signal(false);
  readonly generation = signal(0);
  constructor() {
    liveRefresh({
      collectionId: () => COLLECTION_ID,
      blocked: () => this.blocked(),
      generation: () => this.generation(),
      refresh: () => {
        this.refreshes.push(Date.now());
        return Promise.resolve(true);
      },
    });
  }
}

/** Like {@link HostComponent}, but its lock also holds the collection's schema back. */
@Component({ template: '' })
class SchemaHolderComponent {
  readonly blocked = signal(false);
  constructor() {
    liveRefresh({
      collectionId: () => COLLECTION_ID,
      blocked: () => this.blocked(),
      generation: () => 0,
      refresh: () => Promise.resolve(true),
      holdSchema: true,
    });
  }
}

describe('liveRefresh', () => {
  const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

  it('delay_Default_BundlesForAtLeast500ms', () => {
    expect(TestBed.inject(LIVE_REFRESH_DELAY_MS)).toBeGreaterThanOrEqual(500);
    expect(TestBed.inject(LIVE_REFRESH_MAX_WAIT_MS)).toBeGreaterThan(
      TestBed.inject(LIVE_REFRESH_DELAY_MS),
    );
  });

  async function host(delay: number, maxWait: number) {
    const realtime = new FakeRealtime();
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({ routes: [{ path: 'w/:workspaceSlug', children: [] }] }),
        provideFakeRealtime(realtime),
        { provide: LIVE_REFRESH_DELAY_MS, useValue: delay },
        { provide: LIVE_REFRESH_MAX_WAIT_MS, useValue: maxWait },
      ],
    });
    await TestBed.inject(Router).navigateByUrl('/w/demo');
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    return { realtime, component: fixture.componentInstance, fixture };
  }

  const event = (seq: number) => activityEvent({ seq, collectionId: COLLECTION_ID });

  it('schedule_ChangesKeepComing_WaitsForQuietTimeButNotLongerThanMaxWait', async () => {
    const { realtime, component, fixture } = await host(40, 120);

    const start = Date.now();
    for (let i = 0; i < 10; i++) {
      realtime.emit(event(i));
      await wait(20);
      fixture.detectChanges();
    }

    // a pure debounce would not have fired yet (changes every 20 ms < 40 ms quiet time)
    expect(component.refreshes.length).toBeGreaterThanOrEqual(1);
    expect(component.refreshes[0] - start).toBeLessThan(200);
  });

  it('schedule_QuietAfterChanges_RefreshesOnce', async () => {
    const { realtime, component, fixture } = await host(30, 1000);

    realtime.emit(event(1));
    await wait(10);
    realtime.emit(event(2));
    await wait(80);
    fixture.detectChanges();
    await fixture.whenStable();

    expect(component.refreshes).toHaveLength(1);
  });
});

describe('liveRefresh schema', () => {
  const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

  async function setup() {
    const realtime = new FakeRealtime();
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({ routes: [{ path: 'w/:workspaceSlug', children: [] }] }),
        provideFakeRealtime(realtime),
        CollectionStore,
        { provide: LIVE_REFRESH_DELAY_MS, useValue: 20 },
        { provide: LIVE_REFRESH_MAX_WAIT_MS, useValue: 80 },
      ],
    });
    await TestBed.inject(Router).navigateByUrl('/w/demo');
    return { realtime, store: TestBed.inject(CollectionStore) };
  }

  const schemaChanged = (collectionId: string) =>
    activityEvent({
      seq: 1,
      type: 'collection.schema_changed',
      targetType: 'collection',
      targetId: collectionId,
      collectionId,
    });

  it('schemaChanged_OfTheCollection_RefreshesTheRows', async () => {
    const { realtime } = await setup();
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();

    // values can be rewritten by a schema change without any record event
    realtime.emit(schemaChanged(COLLECTION_ID));
    await wait(60);
    fixture.detectChanges();
    await fixture.whenStable();

    expect(fixture.componentInstance.refreshes).toHaveLength(1);
  });

  it('schemaChanged_OfAnotherCollection_DoesNotRefresh', async () => {
    const { realtime } = await setup();
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();

    realtime.emit(schemaChanged('other-collection'));
    realtime.emit(activityEvent({ seq: 2, type: 'collection.updated', targetType: 'collection' }));
    await wait(60);
    fixture.detectChanges();
    await fixture.whenStable();

    expect(fixture.componentInstance.refreshes).toHaveLength(0);
  });

  it('holdSchema_Set_TheLockHoldsTheStoresSchema', async () => {
    const { store } = await setup();
    const fixture = TestBed.createComponent(SchemaHolderComponent);
    fixture.detectChanges();

    fixture.componentInstance.blocked.set(true);
    expect(store.editing()).toBe(true);
    fixture.componentInstance.blocked.set(false);
    expect(store.editing()).toBe(false);

    fixture.destroy();
    fixture.componentInstance.blocked.set(true);
    expect(store.editing()).toBe(false);
  });

  it('holdSchema_NotSet_LeavesTheStoreAlone', async () => {
    const { store } = await setup();
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();

    fixture.componentInstance.blocked.set(true);

    expect(store.editing()).toBe(false);
  });
});
