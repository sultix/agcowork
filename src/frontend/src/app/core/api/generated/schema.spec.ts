import type { components, paths } from './schema';

// Compile test for the generated OpenAPI types: a renamed path, method or property in
// openapi/v1.json breaks the build here, not somewhere in a feature.
type GetMe = paths['/api/v1/me']['get'];
type MeResponse = GetMe['responses'][200]['content']['application/json'];
type LoginBody = NonNullable<
  paths['/api/v1/auth/login']['post']['requestBody']
>['content']['application/json'];
type Problem = components['schemas']['ProblemDetails'];

describe('generated OpenAPI schema', () => {
  it('paths_Me_HasTypedActorResponse', () => {
    const me: MeResponse = {
      id: '0199a000-0000-7000-8000-000000000001',
      kind: 'agent',
      name: 'Coder',
      role: 'Tech-Lead',
      email: null,
      instanceScopes: [],
      apiKey: null,
    };
    const body: LoginBody = { email: 'admin@agcowork.local', password: 'x' };
    const problem: Problem = {
      status: 400,
      code: 'validation_failed',
      errors: [{ field: 'email', code: 'required', message: 'm' }],
      traceId: 't',
    };

    expect(me.kind).toBe('agent');
    expect(body.email).toContain('@');
    expect(problem.errors?.[0].field).toBe('email');
  });
});
