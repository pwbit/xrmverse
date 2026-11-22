import { describe, expect, it } from 'vitest';
import vector from '../../../../fixtures/ui/dev-token-vector.json';
import { createDevToken } from './devToken';

function decode(part: string): unknown {
  const padded = part.replace(/-/g, '+').replace(/_/g, '/');
  return JSON.parse(atob(padded));
}

const args = {
  signingKey: vector.signingKey,
  objectId: vector.objectId,
  name: vector.name,
  audience: vector.audience,
  now: new Date(vector.nbf * 1000),
};

describe('createDevToken', () => {
  it('Dev_token_matches_the_CLI_token_shape', async () => {
    const token = await createDevToken(args);
    const [header, payload, signature] = token.split('.');
    expect(token).not.toMatch(/[=+/]/);
    expect(decode(header)).toEqual({ alg: 'HS256', typ: 'JWT' });
    expect(decode(payload)).toEqual({
      iss: 'xrmverse-dev',
      aud: vector.audience,
      oid: vector.objectId,
      name: vector.name,
      nbf: vector.nbf,
      exp: vector.nbf + 3600,
    });
    expect(Object.keys(decode(payload) as object)).toEqual(['iss', 'aud', 'oid', 'name', 'nbf', 'exp']);

    const key = await crypto.subtle.importKey(
      'raw',
      new TextEncoder().encode(vector.signingKey),
      { name: 'HMAC', hash: 'SHA-256' },
      false,
      ['verify'],
    );
    const raw = Uint8Array.from(atob(signature.replace(/-/g, '+').replace(/_/g, '/')), (c) => c.charCodeAt(0));
    expect(await crypto.subtle.verify('HMAC', key, raw, new TextEncoder().encode(`${header}.${payload}`))).toBe(true);
  });

  it('Dev_token_equals_the_committed_vector', async () => {
    expect(await createDevToken(args)).toBe(vector.token);
  });

  it('Dev_token_uses_whole_seconds', async () => {
    const token = await createDevToken({ ...args, now: new Date(vector.nbf * 1000 + 999) });
    expect(token).toBe(vector.token);
  });
});
