import { renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import type { FormContext } from '../../client-api/formContext';
import { setLibraryLoader } from '../../client-api/scriptHost';
import { sourceLoader } from '../../client-api/testHost';
import { contextOf, formOf } from '../../renderers/classic/form/formTestSupport';
import { useFormScripts, type PageEnv } from './useFormScripts';

afterEach(() => setLibraryLoader(undefined));

/** Enough of the page for a formContext to be created and to answer `data.entity`. */
const pageEnv = (form: ReturnType<typeof formOf>): PageEnv => ({ form, forms: [form], lcid: 1033, ctx: contextOf([], { name: 'Contoso' }) }) as unknown as PageEnv;

/** The table a formContext answers for (`data.entity.getEntityName()`); throws once the formContext is disposed. */
const entityOf = (fc: FormContext | undefined) => (fc as unknown as { data: { entity: { getEntityName(): string } } }).data.entity.getEntityName();

describe('useFormScripts', () => {
  it('host_creation_failure_notifies_and_leaves_no_iframe', async () => {
    setLibraryLoader(sourceLoader({}));
    const form = formOf([], { hasScripts: true, libraries: ['a.js'] });
    const { result } = renderHook(() =>
      useFormScripts(form, 0, undefined, {
        install: () => {
          throw new Error('no Xrm');
        },
      }),
    );
    await waitFor(() => expect(result.current.notifications).toHaveLength(1));
    expect(result.current.notifications[0]).toMatchObject({
      intent: 'error',
      text: 'Script error: the script host could not be started (no Xrm).',
    });
    expect(document.querySelectorAll('iframe')).toHaveLength(0);
  });

  it('form_context_exists_while_the_libraries_load_and_the_host_uses_the_same_one', async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    const inner = sourceLoader({ 'a.js': 'function a() {}', 'b.js': 'function b() {}' });
    setLibraryLoader(async (win, url, name) => {
      await gate;
      return inner(win, url, name);
    });
    const form = formOf([], { hasScripts: true, libraries: ['a.js', 'b.js'] });
    const env = pageEnv(form);
    const seen: (FormContext | undefined)[] = [];
    const { result, unmount } = renderHook(() => useFormScripts(form, 0, undefined, { env, onFormContext: (fc) => seen.push(fc) }));
    // Registered at mount, before any library has loaded.
    expect(seen).toHaveLength(1);
    expect(entityOf(seen[0])).toBe('xv_project');
    expect(result.current.formContext).toBeUndefined();
    release();
    await waitFor(() => expect(result.current.formContext).toBeDefined());
    expect(result.current.formContext).toBe(seen[0]);
    expect(seen).toHaveLength(1);
    unmount();
    expect(seen).toEqual([seen[0], undefined]);
    expect(() => entityOf(seen[0])).toThrow('no longer open');
  });

  it('form_context_stays_usable_when_the_host_fails_to_start', async () => {
    setLibraryLoader(sourceLoader({}));
    const form = formOf([], { hasScripts: true, libraries: ['a.js'] });
    const env = pageEnv(form);
    const seen: (FormContext | undefined)[] = [];
    const { result, unmount } = renderHook(() =>
      useFormScripts(form, 0, undefined, {
        env,
        install: () => {
          throw new Error('no Xrm');
        },
        onFormContext: (fc) => seen.push(fc),
      }),
    );
    await waitFor(() => expect(result.current.notifications).toHaveLength(1));
    expect(result.current.notifications[0]).toMatchObject({ key: 'host-failed' });
    expect(seen).toHaveLength(1);
    expect(entityOf(seen[0])).toBe('xv_project');
    unmount();
    expect(seen[1]).toBeUndefined();
    expect(seen).toHaveLength(2);
  });
});
