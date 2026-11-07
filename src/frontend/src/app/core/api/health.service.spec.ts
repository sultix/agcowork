import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef } from '@angular/core';
import { HealthService } from './health.service';

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

  afterEach(() => http.verify());

  it('status_BeforeResponse_IsChecking', () => {
    expect(service.status()).toBe('checking');
    http.expectOne('/health').flush('Healthy');
  });

  it('status_HealthyResponse_IsOk', async () => {
    http.expectOne('/health').flush('Healthy');
    await TestBed.inject(ApplicationRef).whenStable();
    expect(service.status()).toBe('ok');
  });

  it('status_ServerError_IsUnreachable', async () => {
    http.expectOne('/health').flush('down', { status: 503, statusText: 'Service Unavailable' });
    await TestBed.inject(ApplicationRef).whenStable();
    expect(service.status()).toBe('unreachable');
  });
});
