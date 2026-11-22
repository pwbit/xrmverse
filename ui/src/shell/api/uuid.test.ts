import { afterEach, describe, expect, it, vi } from 'vitest';
import { newGuid } from './uuid';

const V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

afterEach(() => vi.restoreAllMocks());

describe('newGuid', () => {
  it('uses randomUUID when the context is secure', () => {
    vi.spyOn(crypto, 'randomUUID').mockReturnValue('11111111-2222-4333-8444-555555555555');
    expect(newGuid()).toBe('11111111-2222-4333-8444-555555555555');
  });

  it('builds a version 4 GUID without randomUUID (plain HTTP on another host)', () => {
    const original = crypto.randomUUID;
    Object.defineProperty(crypto, 'randomUUID', { value: undefined, configurable: true });
    try {
      const a = newGuid();
      expect(a).toMatch(V4);
      expect(newGuid()).not.toBe(a);
    } finally {
      Object.defineProperty(crypto, 'randomUUID', { value: original, configurable: true });
    }
  });
});
