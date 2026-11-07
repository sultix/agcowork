import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef } from '@angular/core';
import { expectRequest } from '../testing/test-utils';
import { HEALTH_RETRY_MS, HealthService } from './health.service';

describe('HealthService', () => {
  let http: HttpTestingController;
  let service: HealthService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(HealthService);
    TestBed.tick();
  });

  afterEach(() => {
    vi.useRealTimers();
    http.verify();
  });

  /** Only the timers: promises and microtasks must keep running for `whenStable()`. */
  const fakeTimers = () => vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
  const settled = () => TestBed.inject(ApplicationRef).whenStable();

  it('status_BeforeResponse_IsChecking', () => {
    expect(service.status()).toBe('checking');
    http.expectOne('/health').flush('Healthy');
  });

  it('status_HealthyResponse_IsOk', async () => {
    http.expectOne('/health').flush('Healthy');
    await settled();
    expect(service.status()).toBe('ok');
  });

  it('status_DegradedText_IsDegraded', async () => {
    http.expectOne('/health').flush('Degraded');
    await settled();
    expect(service.status()).toBe('degraded');
    service.reload();
    (await expectRequest(http, 'GET', '/health')).flush('Healthy');
  });

  it('status_ServiceUnavailableWithBody_IsDegraded', async () => {
    http.expectOne('/health').flush('Unhealthy', { status: 503, statusText: 'Unavailable' });
    await settled();
    expect(service.status()).toBe('degraded');
    service.reload();
    (await expectRequest(http, 'GET', '/health')).flush('Healthy');
  });

  it('status_NetworkError_IsUnreachable', async () => {
    http.expectOne('/health').error(new ProgressEvent('error'));
    await settled();
    expect(service.status()).toBe('unreachable');
    service.reload();
    (await expectRequest(http, 'GET', '/health')).flush('Healthy');
  });

  it('status_ErrorState_PollsAgainUntilOk', async () => {
    fakeTimers();
    http.expectOne('/health').error(new ProgressEvent('error'));
    await settled();
    expect(service.status()).toBe('unreachable');
    TestBed.tick();
    vi.advanceTimersByTime(HEALTH_RETRY_MS - 1);
    http.expectNone('/health');
    vi.advanceTimersByTime(1);
    TestBed.tick();
    // Previous state stays visible while re-checking.
    expect(service.status()).toBe('unreachable');
    (await expectRequest(http, 'GET', '/health')).flush('Degraded');
    await settled();
    expect(service.status()).toBe('degraded');

    TestBed.tick();
    vi.advanceTimersByTime(HEALTH_RETRY_MS);
    (await expectRequest(http, 'GET', '/health')).flush('Healthy');
    await settled();
    expect(service.status()).toBe('ok');
  });

  it('status_OkState_DoesNotPoll', async () => {
    fakeTimers();
    http.expectOne('/health').flush('Healthy');
    await settled();

    TestBed.tick();
    vi.advanceTimersByTime(HEALTH_RETRY_MS * 4);
    http.expectNone('/health');
    expect(service.status()).toBe('ok');
  });

  it('reload_InErrorState_AsksImmediately', async () => {
    http.expectOne('/health').error(new ProgressEvent('error'));
    await settled();

    service.reload();
    (await expectRequest(http, 'GET', '/health')).flush('Healthy');
    await settled();
    expect(service.status()).toBe('ok');
  });
});
