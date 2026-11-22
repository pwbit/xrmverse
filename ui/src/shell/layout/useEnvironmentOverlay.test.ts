import { act, renderHook, waitFor } from '@testing-library/react';
import { createElement, type ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { XrmApi } from '../api/client';
import { XrmError } from '../api/errors';
import { AccessContext, type AccessLevel } from '../auth/roles';
import { refreshEnvironmentLook, useEnvironmentOverlay } from './useEnvironmentOverlay';

const api = vi.hoisted(() => ({ get: vi.fn() }));
vi.mock('../auth/AuthProvider', () => ({ useAuth: () => ({ api: api as unknown as XrmApi }) }));

const DEF = '6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e11';

/** Answers the definition read with `definitions` and the environment value read with `rows` (or an error). */
function answer(definitions: object[], rows: object[] | Error) {
  api.get.mockImplementation(async (path: string) => {
    if (path.startsWith('settingdefinitions')) return { value: definitions };
    if (path.startsWith('organizationsettings')) {
      if (rows instanceof Error) throw rows;
      return { value: rows };
    }
    throw new Error(`unexpected ${path}`);
  });
}

/** The hook under an access level (admin unless given). */
const levelWrapper = (level: AccessLevel) => ({ children }: { children: ReactNode }) => createElement(AccessContext.Provider, { value: { level, roles: [] } }, children);
const hook = (level: AccessLevel = 'admin') => renderHook(() => useEnvironmentOverlay(), { wrapper: levelWrapper(level) });

const paths = () => api.get.mock.calls.map((c) => String(c[0]));

// A function returned from beforeEach is a teardown: the reset returns the mock, so it must not be returned.
beforeEach(() => {
  api.get.mockReset();
});

describe('useEnvironmentOverlay', () => {
  it('the environment value wins over the definition default, trimmed', async () => {
    answer([{ settingdefinitionid: DEF, uniquename: 'xrmverse_Presentation', defaultvalue: 'x_/default.json' }], [{ _settingdefinitionid_value: DEF, value: '  x_/look.json ' }]);
    const { result } = hook();
    await waitFor(() => expect(result.current.name).toBe('x_/look.json'));
    expect(paths()[0]).toContain("uniquename eq 'xrmverse_Presentation'");
    expect(paths()[1]).toContain(`_settingdefinitionid_value eq ${DEF}`);
  });

  it('a blank environment value falls back to the definition default', async () => {
    answer([{ settingdefinitionid: DEF, uniquename: 'xrmverse_Presentation', defaultvalue: 'x_/default.json' }], [{ _settingdefinitionid_value: DEF, value: '   ' }]);
    const { result } = hook();
    await waitFor(() => expect(result.current.name).toBe('x_/default.json'));
  });

  it('no definition gives no name and reads no organizationsettings', async () => {
    answer([], []);
    const { result } = hook();
    await waitFor(() => expect(api.get).toHaveBeenCalledTimes(1));
    await act(async () => {});
    expect(result.current.name).toBeUndefined();
    expect(paths().some((p) => p.startsWith('organizationsettings'))).toBe(false);
  });

  it('a row without _settingdefinitionid_value does not throw and falls back to the definition default', async () => {
    answer([{ settingdefinitionid: DEF, uniquename: 'xrmverse_Presentation', defaultvalue: 'x_/default.json' }], [{ value: 'x_/other.json' }]);
    const { result } = hook();
    await waitFor(() => expect(result.current.name).toBe('x_/default.json'));
    expect(result.current.error).toBeUndefined();
  });

  it('a 403 on organizationsettings gives no name', async () => {
    answer([{ settingdefinitionid: DEF, uniquename: 'xrmverse_Presentation', defaultvalue: 'x_/default.json' }], new XrmError(403, 'Principal user is missing prvReadorganizationsetting', '0x80040220'));
    const { result } = hook();
    await waitFor(() => expect(api.get).toHaveBeenCalledTimes(2));
    await act(async () => {});
    expect(result.current.name).toBeUndefined();
    // No read access is not an error to show (R5).
    expect(result.current.error).toBeUndefined();
  });

  it('a 500 on organizationsettings gives no name and the server error', async () => {
    answer([{ settingdefinitionid: DEF, uniquename: 'xrmverse_Presentation', defaultvalue: 'x_/default.json' }], new XrmError(500, 'Database unavailable'));
    const { result } = hook();
    await waitFor(() => expect(result.current.error).toBe('Database unavailable'));
    expect(result.current.name).toBeUndefined();
  });

  it('a network failure on settingdefinitions gives its message as the error', async () => {
    api.get.mockRejectedValue(new TypeError('Failed to fetch'));
    const { result } = hook();
    await waitFor(() => expect(result.current.error).toBe('Failed to fetch'));
    expect(result.current.name).toBeUndefined();
  });

  it('a non-admin (customizer, denied, error) reads nothing and has no name', async () => {
    answer([{ settingdefinitionid: DEF, uniquename: 'xrmverse_Presentation', defaultvalue: 'x_/default.json' }], []);
    for (const level of ['customizer', 'denied', 'error'] as const) {
      const { result, unmount } = hook(level);
      await act(async () => {});
      expect(result.current).toEqual({ reload: expect.any(Function) });
      act(() => refreshEnvironmentLook());
      await act(async () => {});
      unmount();
    }
    expect(api.get).not.toHaveBeenCalled();
  });

  it('reads nothing while access loads, then reads once it is admin', async () => {
    answer([{ settingdefinitionid: DEF, uniquename: 'xrmverse_Presentation', defaultvalue: 'x_/default.json' }], []);
    let level: AccessLevel = 'loading';
    const { result, rerender } = renderHook(() => useEnvironmentOverlay(), {
      wrapper: ({ children }: { children: ReactNode }) => createElement(AccessContext.Provider, { value: { level, roles: [] } }, children),
    });
    await act(async () => {});
    expect(api.get).not.toHaveBeenCalled();
    expect(result.current.name).toBeUndefined();
    level = 'admin';
    rerender();
    await waitFor(() => expect(result.current.name).toBe('x_/default.json'));
    expect(api.get).toHaveBeenCalledTimes(2);
  });

  it('refreshEnvironmentLook re-reads in every mounted hook', async () => {
    const rows = [{ _settingdefinitionid_value: DEF, value: 'x_/one.json' }];
    answer([{ settingdefinitionid: DEF, uniquename: 'xrmverse_Presentation', defaultvalue: null }], rows);
    const a = hook();
    const b = hook();
    await waitFor(() => expect(a.result.current.name).toBe('x_/one.json'));
    await waitFor(() => expect(b.result.current.name).toBe('x_/one.json'));
    rows[0]!.value = 'x_/two.json';
    act(() => refreshEnvironmentLook());
    await waitFor(() => expect(a.result.current.name).toBe('x_/two.json'));
    await waitFor(() => expect(b.result.current.name).toBe('x_/two.json'));
    // reload() does the same for its own hook.
    rows.length = 0;
    act(() => a.result.current.reload());
    await waitFor(() => expect(a.result.current.name).toBeUndefined());
  });
});
