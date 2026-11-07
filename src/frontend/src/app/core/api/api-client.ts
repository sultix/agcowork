import { Injectable, inject } from '@angular/core';
import {
  HttpClient,
  HttpContext,
  HttpHeaders,
  HttpParams,
  HttpResourceRequest,
} from '@angular/common/http';
import { Observable, catchError, throwError } from 'rxjs';
import { API_BASE_URL } from './api-tokens';
import { ApiError } from './api-error';
import type { paths } from './generated/schema';

// ---------------------------------------------------------------------------------------------
// Types derived from the generated OpenAPI schema. Paths in the schema are absolute
// (`/api/v1/me`); the client works with paths relative to the API base (`/me`) and, for
// workspace endpoints, relative to the workspace prefix (`/records/{id}`).
// ---------------------------------------------------------------------------------------------

type HttpMethod = 'get' | 'post' | 'put' | 'patch' | 'delete';
type ApiPrefix = '/api/v1';
type WorkspacePrefix = `${ApiPrefix}/w/{${string}}`;

/** Instance-level endpoints of `TAll` keyed by path relative to `/api/v1`, e.g. `/me`. */
export type InstancePathsOf<TAll> = {
  [
    P in keyof TAll as P extends `${WorkspacePrefix}${string}`
      ? never
      : P extends `${ApiPrefix}${infer R}`
        ? R
        : never
  ]: TAll[P];
};

/** Removes path parameter `Name` from an operation; no path parameters left → `path?: never`. */
type WithoutPathParam<Op, Name extends string> = Op extends { parameters: infer Params }
  ? Params extends { path: infer PP }
    ? Omit<Op, 'parameters'> & {
        parameters: Omit<Params, 'path'> &
          ([Exclude<keyof PP, Name>] extends [never] ? { path?: never } : { path: Omit<PP, Name> });
      }
    : Op
  : Op;

/** Path item with the workspace route parameter (`{slug}`) removed from every operation. */
type WithoutPrefixParam<Item, Name extends string> = {
  [M in keyof Item]: M extends HttpMethod ? WithoutPathParam<Item[M], Name> : Item[M];
};

/**
 * Workspace endpoints of `TAll` keyed by path relative to `/api/v1/w/{slug}`, e.g. `/members`.
 * The slug is supplied by {@link ApiClient.workspace}, so it is removed from the operations'
 * path parameters (ASP.NET documents it on every operation of the route group).
 */
export type WorkspacePathsOf<TAll> = {
  [
    P in keyof TAll as P extends `${WorkspacePrefix}${infer R}` ? R : never
  ]: P extends `${ApiPrefix}/w/{${infer Name}}${string}`
    ? WithoutPrefixParam<TAll[P], Name>
    : never;
};

/** Instance-level endpoints of the API, e.g. `/me`. */
export type InstancePaths = InstancePathsOf<paths>;

/** Workspace endpoints of the API, relative to `/api/v1/w/{slug}`. */
export type WorkspacePaths = WorkspacePathsOf<paths>;

/** Paths of `TPaths` that support `method`. */
export type PathsWith<TPaths, M extends HttpMethod> = {
  [P in keyof TPaths]: TPaths[P] extends Record<M, object> ? P : never;
}[keyof TPaths] &
  string;

/** The operation object of `method` on `path`. */
export type Operation<TPaths, P extends keyof TPaths, M extends HttpMethod> =
  TPaths[P] extends Record<M, infer Op> ? Op : never;

type PathParamsOf<Op> = Op extends { parameters: { path: infer PP } } ? PP : never;
type QueryOf<Op> = Op extends { parameters: { query?: infer Q } } ? NonNullable<Q> : never;
/** openapi-typescript emits `query` (not `query?`) when at least one query parameter is required. */
type QueryRequired<Op> = Op extends { parameters: { query: object } } ? true : false;
type BodyOf<Op> = Op extends { requestBody?: infer RB }
  ? [NonNullable<RB>] extends [never]
    ? never
    : NonNullable<RB> extends { content: { 'application/json': infer B } }
      ? B
      : never
  : never;
type BodyRequired<Op> = Op extends { requestBody: object } ? true : false;
type JsonContent<R> = R extends { content: { 'application/json': infer T } } ? T : void;

/** Body type of the success response (200, 201 or 204 → `void`). */
export type ResponseOf<Op> = Op extends { responses: infer R }
  ? 200 extends keyof R
    ? JsonContent<R[200]>
    : 201 extends keyof R
      ? JsonContent<R[201]>
      : void
  : never;

/** Options of one request; `path` and `body` are required exactly when the operation requires them. */
export type RequestOptions<Op> = ([PathParamsOf<Op>] extends [never]
  ? { path?: never }
  : { path: PathParamsOf<Op> }) &
  ([BodyOf<Op>] extends [never]
    ? { body?: never }
    : BodyRequired<Op> extends true
      ? { body: BodyOf<Op> }
      : { body?: BodyOf<Op> }) &
  ([QueryOf<Op>] extends [never]
    ? { query?: never }
    : QueryRequired<Op> extends true
      ? { query: QueryOf<Op> }
      : { query?: QueryOf<Op> }) & {
    context?: HttpContext;
  };

/** Makes the options argument mandatory only when it carries something required. */
export type RequestArgs<Op> = [PathParamsOf<Op>] extends [never]
  ? BodyRequired<Op> extends true
    ? [options: RequestOptions<Op>]
    : QueryRequired<Op> extends true
      ? [options: RequestOptions<Op>]
      : [options?: RequestOptions<Op>]
  : [options: RequestOptions<Op>];

interface UntypedOptions {
  path?: Record<string, string | number>;
  query?: Record<string, unknown>;
  body?: unknown;
  context?: HttpContext;
}

const JSON_ACCEPT = 'application/json';

/**
 * Thin typed client over `HttpClient` for one URL prefix. Paths, parameters, bodies and
 * responses are checked against the generated OpenAPI schema; every request is sent with
 * credentials (session cookie) and `Accept: application/json`; every failure surfaces as
 * {@link ApiError}.
 */
export class TypedApiClient<TPaths> {
  constructor(
    protected readonly http: HttpClient,
    readonly prefix: string,
  ) {}

  get<P extends PathsWith<TPaths, 'get'>>(
    path: P,
    ...[options]: RequestArgs<Operation<TPaths, P, 'get'>>
  ): Observable<ResponseOf<Operation<TPaths, P, 'get'>>> {
    return this.send('GET', path, options as UntypedOptions | undefined);
  }

  post<P extends PathsWith<TPaths, 'post'>>(
    path: P,
    ...[options]: RequestArgs<Operation<TPaths, P, 'post'>>
  ): Observable<ResponseOf<Operation<TPaths, P, 'post'>>> {
    return this.send('POST', path, options as UntypedOptions | undefined);
  }

  put<P extends PathsWith<TPaths, 'put'>>(
    path: P,
    ...[options]: RequestArgs<Operation<TPaths, P, 'put'>>
  ): Observable<ResponseOf<Operation<TPaths, P, 'put'>>> {
    return this.send('PUT', path, options as UntypedOptions | undefined);
  }

  patch<P extends PathsWith<TPaths, 'patch'>>(
    path: P,
    ...[options]: RequestArgs<Operation<TPaths, P, 'patch'>>
  ): Observable<ResponseOf<Operation<TPaths, P, 'patch'>>> {
    return this.send('PATCH', path, options as UntypedOptions | undefined);
  }

  delete<P extends PathsWith<TPaths, 'delete'>>(
    path: P,
    ...[options]: RequestArgs<Operation<TPaths, P, 'delete'>>
  ): Observable<ResponseOf<Operation<TPaths, P, 'delete'>>> {
    return this.send('DELETE', path, options as UntypedOptions | undefined);
  }

  /**
   * Request description for `httpResource`, with the same URL building and credentials as the
   * methods above: `httpResource<MeDto>(() => api.resourceRequest('/me'))`. Errors of an
   * `httpResource` are `ApiError`s when the error interceptor is installed (`provideApi()`).
   */
  resourceRequest<P extends PathsWith<TPaths, 'get'>>(
    path: P,
    ...[options]: RequestArgs<Operation<TPaths, P, 'get'>>
  ): HttpResourceRequest {
    const untyped = options as UntypedOptions | undefined;
    return {
      url: this.url(path, untyped?.path),
      method: 'GET',
      params: toParams(untyped?.query),
      headers: { Accept: JSON_ACCEPT },
      withCredentials: true,
      context: untyped?.context,
    };
  }

  /** Absolute API URL of `path` with path parameters substituted (URL-encoded). */
  url(path: string, pathParams?: Record<string, string | number>): string {
    const resolved = path.replace(/\{([^}]+)\}/g, (_, name: string) => {
      const value = pathParams?.[name];
      if (value === undefined || value === null || value === '') {
        throw new Error(`Missing path parameter '${name}' for '${path}'.`);
      }
      return encodeURIComponent(String(value));
    });
    return `${this.prefix}${resolved}`;
  }

  private send<T>(
    method: string,
    path: string,
    options: UntypedOptions | undefined,
  ): Observable<T> {
    return this.http
      .request<T>(method, this.url(path, options?.path), {
        body: options?.body,
        params: toParams(options?.query),
        headers: new HttpHeaders({ Accept: JSON_ACCEPT }),
        context: options?.context,
        withCredentials: true,
        responseType: 'json',
        observe: 'body',
      })
      .pipe(catchError((error: unknown) => throwError(() => ApiError.from(error))));
  }
}

function toParams(query: Record<string, unknown> | undefined): HttpParams | undefined {
  if (!query) {
    return undefined;
  }
  let params = new HttpParams();
  for (const [key, value] of Object.entries(query)) {
    if (value === undefined || value === null) {
      continue;
    }
    for (const item of Array.isArray(value) ? value : [value]) {
      params = params.append(key, String(item));
    }
  }
  return params;
}

/**
 * API client of the app. Instance-level endpoints directly (`api.get('/me')`), workspace
 * endpoints through {@link ApiClient.workspace} (`api.workspace(slug).get('/records/{id}', …)`).
 */
@Injectable({ providedIn: 'root' })
export class ApiClient extends TypedApiClient<InstancePaths> {
  constructor() {
    super(inject(HttpClient), inject(API_BASE_URL));
  }

  /**
   * Client for one workspace: every path is prefixed with `/api/v1/w/{slug}`. `TAll` defaults
   * to the generated schema; tests pass their own absolute paths.
   */
  workspace<TAll = paths>(slug: string): TypedApiClient<WorkspacePathsOf<TAll>> {
    if (slug === '') {
      throw new Error('Workspace slug must not be empty.');
    }
    return new TypedApiClient<WorkspacePathsOf<TAll>>(
      this.http,
      `${this.prefix}/w/${encodeURIComponent(slug)}`,
    );
  }
}
