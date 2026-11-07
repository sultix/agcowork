import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from './api-client';
import { ApiError } from './api-error';
import type { components } from './generated/schema';

type Me = components['schemas']['MeDto'];

/**
 * Stand-in for workspace endpoints in the shape ASP.NET documents them: absolute paths, and the
 * route group's `{slug}` as path parameter of every operation.
 */
interface FakePaths {
  '/api/v1/w/{slug}/records/{id}': {
    parameters: { query?: never; header?: never; path?: never; cookie?: never };
    get: {
      parameters: {
        query?: { limit?: number; tags?: string[] };
        header?: never;
        path: { slug: string; id: string };
        cookie?: never;
      };
      requestBody?: never;
      responses: { 200: { content: { 'application/json': { id: string } } } };
    };
    post?: never;
  };
  '/api/v1/w/{slug}/members': {
    parameters: { query?: never; header?: never; path?: never; cookie?: never };
    get: {
      parameters: { query?: never; header?: never; path: { slug: string }; cookie?: never };
      requestBody?: never;
      responses: { 200: { content: { 'application/json': { name: string }[] } } };
    };
  };
  '/api/v1/w/{slug}/search': {
    parameters: { query?: never; header?: never; path?: never; cookie?: never };
    get: {
      parameters: { query: { q: string }; header?: never; path: { slug: string }; cookie?: never };
      requestBody?: never;
      responses: { 200: { content: { 'application/json': string[] } } };
    };
  };
}

const ME: Me = {
  id: '0199a000-0000-7000-8000-000000000001',
  kind: 'human',
  name: 'Admin',
  role: null,
  email: 'admin@agcowork.local',
  instanceScopes: ['instance:admin'],
  apiKey: null,
  mustChangePassword: false,
};

describe('ApiClient', () => {
  let http: HttpTestingController;
  let api: ApiClient;

  beforeEach(() => {
    // No interceptor: the client converts errors itself.
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    api = TestBed.inject(ApiClient);
  });

  afterEach(() => http.verify());

  it('get_Me_UsesApiBaseWithCredentialsAndAcceptJson', async () => {
    const result = firstValueFrom(api.get('/me'));

    const req = http.expectOne('/api/v1/me');
    expect(req.request.method).toBe('GET');
    expect(req.request.withCredentials).toBe(true);
    expect(req.request.headers.get('Accept')).toBe('application/json');
    req.flush(ME);

    const me: Me = await result;
    expect(me.name).toBe('Admin');
  });

  it('post_HeadersOption_IsSentAlongsideAccept', () => {
    api
      .post('/auth/login', {
        body: { email: 'a@b.c', password: 'secret' },
        headers: { 'Idempotency-Key': 'key-1', Accept: 'text/plain' },
      })
      .subscribe();

    const req = http.expectOne('/api/v1/auth/login');
    expect(req.request.headers.get('Idempotency-Key')).toBe('key-1');
    // The client's own Accept wins.
    expect(req.request.headers.get('Accept')).toBe('application/json');
    req.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('get_PathParameter_IsSubstitutedAndEncoded', () => {
    api.get('/actors/{id}', { path: { id: 'a b/c' } }).subscribe();

    http.expectOne('/api/v1/actors/a%20b%2Fc').flush({});
  });

  it('post_Body_IsSentAsJsonWithCredentials', async () => {
    const done = firstValueFrom(
      api.post('/auth/login', { body: { email: 'a@b.c', password: 'secret' } }),
      { defaultValue: undefined },
    );

    const req = http.expectOne('/api/v1/auth/login');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ email: 'a@b.c', password: 'secret' });
    expect(req.request.withCredentials).toBe(true);
    req.flush(null, { status: 204, statusText: 'No Content' });

    await expect(done).resolves.toBeNull();
  });

  it('workspace_Slug_PrefixesEveryPathAndEncodesQuery', () => {
    const workspace = api.workspace<FakePaths>('demo');

    workspace
      .get('/records/{id}', { path: { id: 'r1' }, query: { limit: 5, tags: ['a', 'b'] } })
      .subscribe();

    const req = http.expectOne((r) => r.url === '/api/v1/w/demo/records/r1');
    expect(req.request.urlWithParams).toBe('/api/v1/w/demo/records/r1?limit=5&tags=a&tags=b');
    expect(req.request.withCredentials).toBe(true);
    req.flush({ id: 'r1' });
  });

  it('workspace_SlugParameter_IsSuppliedByPrefixNotByCaller', async () => {
    const result = firstValueFrom(api.workspace<FakePaths>('demo').get('/members'));

    http.expectOne('/api/v1/w/demo/members').flush([{ name: 'Admin' }]);

    const members: { name: string }[] = await result;
    expect(members[0].name).toBe('Admin');
  });

  it('workspace_RequiredQuery_IsSent', () => {
    api
      .workspace<FakePaths>('demo')
      .get('/search', { query: { q: 'adr' } })
      .subscribe();

    http.expectOne('/api/v1/w/demo/search?q=adr').flush([]);
  });

  it('workspace_SlugWithSpecialCharacters_IsEncoded', () => {
    expect(api.workspace('a/b').prefix).toBe('/api/v1/w/a%2Fb');
  });

  it('workspace_EmptySlug_Throws', () => {
    expect(() => api.workspace('')).toThrow();
  });

  it('url_MissingPathParameter_Throws', () => {
    expect(() => api.url('/actors/{id}')).toThrow(/id/);
  });

  it('resourceRequest_Me_CarriesCredentialsAndAccept', () => {
    expect(api.resourceRequest('/me')).toEqual(
      expect.objectContaining({
        url: '/api/v1/me',
        method: 'GET',
        withCredentials: true,
        headers: { Accept: 'application/json' },
      }),
    );
  });

  it('error_ValidationProblem_BecomesApiErrorWithCodeAndErrors', async () => {
    const result = firstValueFrom(api.post('/actors', { body: { kind: 'robot', name: null } }));

    http.expectOne('/api/v1/actors').flush(
      {
        type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
        title: 'Bad Request',
        status: 400,
        detail: "One or more fields are invalid. See 'errors'.",
        code: 'validation_failed',
        errors: [
          {
            field: 'kind',
            code: 'not_in_options',
            message: 'bad kind',
            allowed: ['human', 'agent'],
          },
          { field: 'name', code: 'required', message: "'name' is required." },
        ],
        traceId: '00-abc-def-01',
      },
      { status: 400, statusText: 'Bad Request' },
    );

    const error = await result.catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    const apiError = error as ApiError;
    expect(apiError.status).toBe(400);
    expect(apiError.code).toBe('validation_failed');
    expect(apiError.traceId).toBe('00-abc-def-01');
    expect(apiError.errors.map((e) => e.field)).toEqual(['kind', 'name']);
    expect(apiError.errorsFor('kind')[0].allowed).toEqual(['human', 'agent']);
  });

  it('error_UnauthorizedProblemWithoutDetail_HasCodeAndNoErrors', async () => {
    const result = firstValueFrom(api.get('/me'));

    http
      .expectOne('/api/v1/me')
      .flush(
        { status: 401, code: 'unauthorized', title: 'Unauthorized' },
        { status: 401, statusText: 'Unauthorized' },
      );

    const error = (await result.catch((e: unknown) => e)) as ApiError;
    expect(error).toBeInstanceOf(ApiError);
    expect(error.status).toBe(401);
    expect(error.code).toBe('unauthorized');
    expect(error.errors).toEqual([]);
    expect(error.detail).toBeUndefined();
  });

  it('error_NonProblemBody_FallsBackToCodeForStatus', async () => {
    const result = firstValueFrom(api.get('/me'));

    http.expectOne('/api/v1/me').flush('Bad Gateway', { status: 502, statusText: 'Bad Gateway' });

    const error = (await result.catch((e: unknown) => e)) as ApiError;
    expect(error.status).toBe(502);
    expect(error.code).toBe('internal_error');
  });

  it('error_NetworkFailure_IsNetworkError', async () => {
    const result = firstValueFrom(api.get('/me'));

    http.expectOne('/api/v1/me').error(new ProgressEvent('error'));

    const error = (await result.catch((e: unknown) => e)) as ApiError;
    expect(error.status).toBe(0);
    expect(error.code).toBe('network_error');
  });

  it('types_UnknownPathOrMissingBody_DoNotCompile', () => {
    // Compile-time checks: each line below must be a type error. If one compiles,
    // `@ts-expect-error` itself fails the build.
    const typeChecks = () => {
      // @ts-expect-error unknown path
      api.get('/does-not-exist');
      // @ts-expect-error login requires a body
      api.post('/auth/login');
      // @ts-expect-error path parameter is required
      api.get('/actors/{id}');
      // @ts-expect-error GET /me has no body
      api.get('/me', { body: {} });
      const workspace = api.workspace<FakePaths>('demo');
      // @ts-expect-error the slug comes from the prefix, not from the caller
      workspace.get('/members', { path: { slug: 'other' } });
      // @ts-expect-error remaining path parameter is still required
      workspace.get('/records/{id}');
      // @ts-expect-error required query parameter makes options mandatory
      workspace.get('/search');
      // @ts-expect-error required query parameter must be present
      workspace.get('/search', {});
    };
    expect(typeChecks).toBeTypeOf('function');
  });
});
