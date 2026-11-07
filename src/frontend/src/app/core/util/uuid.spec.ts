import { newUuid } from './uuid';

const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

describe('newUuid', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('newUuid_RandomUuidAvailable_UsesIt', () => {
    vi.stubGlobal('crypto', {
      randomUUID: () => '11111111-1111-4111-8111-111111111111',
      getRandomValues: crypto.getRandomValues.bind(crypto),
    });

    expect(newUuid()).toBe('11111111-1111-4111-8111-111111111111');
  });

  it('newUuid_RandomUuidUndefined_FallsBackToValidV4FromGetRandomValues', () => {
    const real = crypto.getRandomValues.bind(crypto);
    vi.stubGlobal('crypto', { randomUUID: undefined, getRandomValues: real });

    const first = newUuid();
    const second = newUuid();

    expect(first).toMatch(UUID_V4);
    expect(second).toMatch(UUID_V4);
    expect(first).not.toBe(second);
  });

  it('newUuid_FallbackWithFixedBytes_SetsVersionAndVariantBits', () => {
    vi.stubGlobal('crypto', {
      randomUUID: undefined,
      getRandomValues: (array: Uint8Array) => array.fill(0xff),
    });

    expect(newUuid()).toBe('ffffffff-ffff-4fff-bfff-ffffffffffff');
  });
});
