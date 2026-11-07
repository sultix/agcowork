import { HttpErrorResponse } from '@angular/common/http';
import type { components } from './generated/schema';

/** Error body of every API endpoint (RFC 9457 plus `code`, `errors[]`, `traceId`). */
export type ProblemDetails = components['schemas']['ProblemDetails'];

/** One entry of `errors[]`: which input is wrong, a machine-readable code and a message. */
export type ProblemFieldError = NonNullable<ProblemDetails['errors']>[number];

/** `code` for responses that never reached the API (offline, DNS, CORS, aborted). */
export const NETWORK_ERROR_CODE = 'network_error';

/** Fallback codes when a response has no ProblemDetails body; mirrors the backend's CodeForStatus. */
function codeForStatus(status: number): string {
  switch (status) {
    case 0:
      return NETWORK_ERROR_CODE;
    case 400:
      return 'bad_request';
    case 401:
      return 'unauthorized';
    case 403:
      return 'forbidden';
    case 404:
      return 'not_found';
    case 409:
      return 'conflict';
    default:
      return status >= 500 ? 'internal_error' : 'error';
  }
}

function isProblemBody(body: unknown): body is ProblemDetails {
  return typeof body === 'object' && body !== null && !Array.isArray(body) && 'code' in body;
}

function asString(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined;
}

function asFieldErrors(value: unknown): ProblemFieldError[] {
  if (!Array.isArray(value)) {
    return [];
  }
  return value.filter(
    (e): e is ProblemFieldError =>
      typeof e === 'object' &&
      e !== null &&
      typeof (e as ProblemFieldError).field === 'string' &&
      typeof (e as ProblemFieldError).code === 'string',
  );
}

/**
 * Error of an API call. Every failed request made through the API client (or the error
 * interceptor) surfaces as an `ApiError`, so callers handle one shape: `status`, `code`
 * (always set), `detail`, `errors[]` (field errors, empty if none) and `traceId`.
 */
export class ApiError extends Error {
  override readonly name = 'ApiError';

  constructor(
    readonly status: number,
    readonly code: string,
    readonly errors: readonly ProblemFieldError[] = [],
    readonly detail?: string,
    readonly title?: string,
    readonly traceId?: string,
    readonly url?: string,
    /** Only for 409 `revision_mismatch`: the record's current revision. */
    readonly currentRevision?: number,
    /** Only for 409 `revision_mismatch`: what changed since `expectedRevision` (`null`: unknown). */
    readonly changedKeys?: readonly string[] | null,
  ) {
    super(detail ?? title ?? `API request failed with ${status} (${code})`);
  }

  /** Normalizes anything thrown by HttpClient into an `ApiError`; `ApiError`s pass through unchanged. */
  static from(error: unknown): ApiError {
    if (error instanceof ApiError) {
      return error;
    }
    if (error instanceof HttpErrorResponse) {
      return ApiError.fromResponse(error);
    }
    const message = error instanceof Error ? error.message : undefined;
    return new ApiError(0, NETWORK_ERROR_CODE, [], message);
  }

  private static fromResponse(response: HttpErrorResponse): ApiError {
    const url = response.url ?? undefined;
    const body: unknown = response.error;
    if (!isProblemBody(body)) {
      return new ApiError(
        response.status,
        codeForStatus(response.status),
        [],
        undefined,
        undefined,
        undefined,
        url,
      );
    }
    const status = typeof body.status === 'number' ? body.status : response.status;
    return new ApiError(
      status,
      asString(body.code) ?? codeForStatus(status),
      asFieldErrors(body.errors),
      asString(body.detail),
      asString(body.title),
      asString(body.traceId),
      url,
      typeof body.currentRevision === 'number' ? body.currentRevision : undefined,
      Array.isArray(body.changedKeys)
        ? body.changedKeys.filter((k): k is string => typeof k === 'string')
        : body.changedKeys === null
          ? null
          : undefined,
    );
  }

  /** Field errors for one input, matching `field` exactly or as list/child path (`scopes[1]`, `name.first`). */
  errorsFor(field: string): readonly ProblemFieldError[] {
    return this.errors.filter((e) => fieldRoot(e.field) === field || e.field === field);
  }
}

/** `instanceScopes[1]` → `instanceScopes`, `address.street` → `address`. */
export function fieldRoot(field: string): string {
  const end = field.search(/[.[]/);
  return end === -1 ? field : field.slice(0, end);
}
