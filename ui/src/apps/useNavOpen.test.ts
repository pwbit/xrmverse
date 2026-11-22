import { act, renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useNavOpen } from './useNavOpen';

const Q = '(max-width: 600px)';
const stubMedia = (narrow: boolean) =>
  vi.stubGlobal('matchMedia', (query: string) => ({ matches: narrow && query === Q, media: query, addEventListener: () => {}, removeEventListener: () => {} }));

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  localStorage.clear();
});

describe('useNavOpen', () => {
  it('defaults to open', () => {
    expect(renderHook(() => useNavOpen(Q)).result.current[0]).toBe(true);
  });

  it('starts closed when the narrow query matches, whatever was remembered', () => {
    localStorage.setItem('xrmverse.nav.open', '1');
    stubMedia(true);
    expect(renderHook(() => useNavOpen(Q)).result.current[0]).toBe(false);
  });

  it('remembers a toggle across a remount', () => {
    const first = renderHook(() => useNavOpen(Q));
    act(() => first.result.current[1]());
    expect(first.result.current[0]).toBe(false);
    expect(localStorage.getItem('xrmverse.nav.open')).toBe('0');
    first.unmount();
    const second = renderHook(() => useNavOpen(Q));
    expect(second.result.current[0]).toBe(false);
    act(() => second.result.current[1]());
    second.unmount();
    expect(renderHook(() => useNavOpen(Q)).result.current[0]).toBe(true);
  });

  it('survives a throwing localStorage', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    const { result } = renderHook(() => useNavOpen(Q));
    expect(result.current[0]).toBe(true);
    act(() => result.current[1]());
    expect(result.current[0]).toBe(false);
  });
});
