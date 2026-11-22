import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { controlFileUrl, loadControl, registeredControl, resetPcfLoader, type LoaderDeps, type PcfControl } from './loader';
import { PcfLoadError } from './errors';
import { standardControl, virtualControl, type TestControl } from '../test/pcf/controls';

const globals = window as unknown as Record<string, unknown>;

/** Deps serving `controls`: the manifest by name (case-insensitive) and each bundle evaluated as a script would be. */
function depsFor(controls: TestControl[], over: Partial<LoaderDeps> = {}) {
  const loaded: string[] = [];
  const deps: LoaderDeps = {
    fetchManifest: vi.fn(async (name: string) => {
      const c = controls.find((x) => x.name.toLowerCase() === name.toLowerCase());
      return c ? { name: c.name, manifest: c.manifest, version: '1' } : undefined;
    }),
    loadScript: async (url: string) => {
      loaded.push(url);
      const c = controls.find((x) => url.startsWith(`/Controls/${encodeURIComponent(x.name)}/`));
      const path = c ? decodeURI(url.slice(`/Controls/${encodeURIComponent(c.name)}/`.length)) : '';
      const body = c?.files[path];
      if (body === undefined) throw new Error('not loaded');
      new Function(body)();
    },
    ...over,
  };
  return { deps, loaded };
}

async function rejection(p: Promise<unknown>): Promise<PcfLoadError> {
  try {
    await p;
  } catch (e) {
    expect(e).toBeInstanceOf(PcfLoadError);
    return e as PcfLoadError;
  }
  throw new Error('expected a rejection');
}

beforeEach(() => resetPcfLoader());
afterEach(() => {
  vi.useRealTimers();
  resetPcfLoader();
  for (const k of ['Reactv16', 'FluentUIReactv940', 'FluentUIReactv9', 'ComponentFramework']) delete globals[k];
  document.head.querySelectorAll('link[data-pcf]').forEach((l) => l.remove());
});

describe('controlFileUrl', () => {
  it('encodes_the_name_and_each_path_segment', () => {
    expect(controlFileUrl('xv_A.B', 'strings/B.1033.resx')).toBe('/Controls/xv_A.B/strings/B.1033.resx');
    expect(controlFileUrl('xv a', 'css/a b.css', '/base')).toBe('/base/xv%20a/css/a%20b.css');
  });
});

describe('loadControl', () => {
  it('registers_the_standard_test_control_and_injects_its_css', async () => {
    const { deps, loaded } = depsFor([standardControl]);
    const { manifest, ctor } = await loadControl(standardControl.name, deps);
    expect(manifest.constructor).toBe('Doubler');
    expect(loaded).toEqual(['/Controls/xv_XvTest.Doubler/bundle.js']);
    const control: PcfControl = new ctor();
    const container = document.createElement('div');
    control.init({ parameters: { value: { raw: 3 } } }, () => {}, {}, container);
    control.updateView({ parameters: { value: { raw: 4 } } });
    expect(container.textContent).toBe('4');
    expect(control.getOutputs?.()).toEqual({ value: 8 });
    const link = document.head.querySelector<HTMLLinkElement>('link[data-pcf]');
    expect(link?.getAttribute('href')).toBe('/Controls/xv_XvTest.Doubler/css/doubler.css');
    expect(link?.rel).toBe('stylesheet');
    expect(registeredControl('XvTest.Doubler')).toBe(ctor);
  });

  it('loads_once_per_name_in_a_session', async () => {
    const { deps, loaded } = depsFor([standardControl]);
    const a = await loadControl(standardControl.name, deps);
    const b = await loadControl(standardControl.name.toUpperCase(), deps);
    expect(b).toBe(a);
    expect(deps.fetchManifest).toHaveBeenCalledTimes(1);
    expect(loaded).toHaveLength(1);
    expect(document.head.querySelectorAll('link[data-pcf]')).toHaveLength(1);
  });

  it('gives_a_virtual_control_the_host_react_and_fluent', async () => {
    const { deps } = depsFor([virtualControl]);
    const { manifest, ctor } = await loadControl(virtualControl.name, deps);
    expect(manifest.controlType).toBe('virtual');
    const element = new ctor().updateView({ parameters: { value: { formatted: '7' } } }) as { props: { children: string } };
    expect(element.props.children).toBe('Badge 7');
  });

  it('loads_code_files_in_order', async () => {
    const control: TestControl = {
      name: 'xv_XvTest.Two',
      manifest: '<manifest><control namespace="XvTest" constructor="Two" version="1" control-type="standard"><resources><code path="second.js" order="2" /><code path="first.js" order="1" /></resources></control></manifest>',
      files: {
        'first.js': 'window.__xvFirst = true;',
        'second.js': "if (window.__xvFirst) window.ComponentFramework.registerControl('XvTest.Two', function Two() {});",
      },
    };
    const { deps, loaded } = depsFor([control]);
    await loadControl(control.name, deps);
    expect(loaded).toEqual(['/Controls/xv_XvTest.Two/first.js', '/Controls/xv_XvTest.Two/second.js']);
    delete globals.__xvFirst;
  });

  it('accepts_a_registration_that_comes_after_the_script_loaded', async () => {
    const control: TestControl = {
      name: 'xv_XvTest.Late',
      manifest: '<manifest><control namespace="XvTest" constructor="Late" version="1" control-type="standard"><resources><code path="b.js" order="1" /></resources></control></manifest>',
      files: { 'b.js': "setTimeout(function () { window.ComponentFramework.registerControl('XvTest.Late', function Late() {}); }, 50);" },
    };
    const { deps } = depsFor([control]);
    const { ctor } = await loadControl(control.name, deps);
    expect(ctor.name).toBe('Late');
  });

  it('rejects_an_unknown_control', async () => {
    const { deps } = depsFor([]);
    const e = await rejection(loadControl('xv_Missing.Control', deps));
    expect(e.code).toBe('notFound');
  });

  it('rejects_when_the_manifest_cannot_be_read', async () => {
    const { deps } = depsFor([], { fetchManifest: async () => Promise.reject(new Error('HTTP 500')) });
    const e = await rejection(loadControl('xv_A.B', deps));
    expect(e.code).toBe('manifest');
    expect(e.message).toContain('HTTP 500');
  });

  it('rejects_on_a_script_error', async () => {
    const broken: TestControl = { ...standardControl, name: 'xv_XvTest.Broken', files: {} };
    const { deps } = depsFor([broken]);
    const e = await rejection(loadControl(broken.name, deps));
    expect(e.code).toBe('script');
    expect(e.message).toContain('bundle.js');
  });

  it('rejects_a_bundle_that_throws_while_it_evaluates', async () => {
    const throwing: TestControl = { ...standardControl, name: 'xv_XvTest.Throws', files: { 'bundle.js': "throw new Error('boom');" } };
    const { deps } = depsFor([throwing]);
    expect((await rejection(loadControl(throwing.name, deps))).code).toBe('script');
  });

  it('rejects_a_control_that_does_not_register_within_10_seconds', async () => {
    vi.useFakeTimers();
    const silent: TestControl = { ...standardControl, name: 'xv_XvTest.Silent', files: { 'bundle.js': 'var nothing = 1;' } };
    const { deps } = depsFor([silent]);
    const pending = rejection(loadControl(silent.name, deps));
    await vi.advanceTimersByTimeAsync(9_999);
    let settled = false;
    void pending.then(() => (settled = true));
    await Promise.resolve();
    expect(settled).toBe(false);
    await vi.advanceTimersByTimeAsync(1);
    expect((await pending).code).toBe('timeout');
  });

  it('never_uses_a_constructor_registered_before_the_load', async () => {
    vi.useFakeTimers();
    const silent: TestControl = { ...standardControl, name: 'xv_XvTest.Stale', files: { 'bundle.js': 'var nothing = 1;' } };
    const { deps } = depsFor([silent]);
    // Something registered the same namespace.constructor earlier in the session.
    const first = depsFor([standardControl]);
    await loadControl(standardControl.name, first.deps);
    expect(registeredControl('XvTest.Doubler')).toBeDefined();
    const pending = rejection(loadControl(silent.name, deps));
    await vi.advanceTimersByTimeAsync(10_000);
    expect((await pending).code).toBe('timeout');
  });

  it('refuses_an_unsupported_platform_library_before_loading_any_script', async () => {
    const fluent8: TestControl = {
      ...virtualControl,
      name: 'xv_XvTest.Old',
      manifest: virtualControl.manifest.replace('version="9.46.2"', 'version="8.115.0"'),
    };
    const { deps, loaded } = depsFor([fluent8]);
    const e = await rejection(loadControl(fluent8.name, deps));
    expect(e.code).toBe('platformLibrary');
    expect(e.message).toContain('Fluent 8.115.0');
    expect(loaded).toEqual([]);
  });

  it('keeps_a_failure_for_the_session', async () => {
    const { deps } = depsFor([]);
    await rejection(loadControl('xv_Missing.Control', deps));
    await rejection(loadControl('xv_Missing.Control', deps));
    expect(deps.fetchManifest).toHaveBeenCalledTimes(1);
  });

  it('keeps_an_invalid_manifest_and_an_unsupported_platform_library_for_the_session', async () => {
    const invalid: TestControl = { ...standardControl, name: 'xv_XvTest.Invalid', manifest: '<manifest />' };
    const fluent8: TestControl = { ...virtualControl, name: 'xv_XvTest.Old', manifest: virtualControl.manifest.replace('version="9.46.2"', 'version="8.115.0"') };
    const { deps } = depsFor([invalid, fluent8]);
    for (const name of [invalid.name, fluent8.name]) {
      await rejection(loadControl(name, deps));
      await rejection(loadControl(name, deps));
    }
    expect(deps.fetchManifest).toHaveBeenCalledTimes(2);
  });

  it('retries_after_the_manifest_read_failed', async () => {
    const { deps: good } = depsFor([standardControl]);
    let calls = 0;
    const { deps } = depsFor([standardControl], {
      fetchManifest: vi.fn(async (name: string) => {
        calls += 1;
        if (calls === 1) throw new Error('HTTP 401');
        return good.fetchManifest(name);
      }),
    });
    expect((await rejection(loadControl(standardControl.name, deps))).code).toBe('manifest');
    const { ctor } = await loadControl(standardControl.name, deps);
    expect(ctor.name).toBe('Doubler');
    expect(deps.fetchManifest).toHaveBeenCalledTimes(2);
  });

  it('retries_after_a_script_failed_and_removes_the_half_installed_tags', async () => {
    const { deps: good } = depsFor([standardControl]);
    let calls = 0;
    const loadScript = vi.fn(async (url: string) => {
      calls += 1;
      const script = document.createElement('script');
      script.setAttribute('src', url);
      document.head.appendChild(script);
      if (calls === 1) throw new Error('HTTP 401');
      await good.loadScript!(url);
    });
    const { deps } = depsFor([standardControl], { loadScript });
    expect((await rejection(loadControl(standardControl.name, deps))).code).toBe('script');
    expect(document.head.querySelectorAll('script[src="/Controls/xv_XvTest.Doubler/bundle.js"]')).toHaveLength(0);
    expect(document.head.querySelectorAll('link[data-pcf]')).toHaveLength(0);
    const { ctor } = await loadControl(standardControl.name, deps);
    expect(ctor.name).toBe('Doubler');
    expect(loadScript).toHaveBeenCalledTimes(2);
    expect(document.head.querySelectorAll('script[src="/Controls/xv_XvTest.Doubler/bundle.js"]')).toHaveLength(1);
    expect(document.head.querySelectorAll('link[data-pcf]')).toHaveLength(1);
    document.head.querySelectorAll('script[src]').forEach((s) => s.remove());
  });

  it('retries_after_a_timeout', async () => {
    vi.useFakeTimers();
    const silent: TestControl = { ...standardControl, name: 'xv_XvTest.Doubler', files: { 'bundle.js': 'var nothing = 1;' } };
    const first = depsFor([silent]);
    const pending = rejection(loadControl(silent.name, first.deps));
    await vi.advanceTimersByTimeAsync(10_000);
    expect((await pending).code).toBe('timeout');
    vi.useRealTimers();
    const { ctor } = await loadControl(standardControl.name, depsFor([standardControl]).deps);
    expect(ctor.name).toBe('Doubler');
  });

  it('gives_each_of_two_controls_with_the_same_constructor_its_own_class', async () => {
    const twin = (name: string, cls: string): TestControl => ({
      ...standardControl,
      name,
      files: { 'bundle.js': `setTimeout(function () { window.ComponentFramework.registerControl('XvTest.Doubler', function ${cls}() {}); }, 10);` },
    });
    const a = twin('xv_XvTest.Doubler', 'First');
    const b = twin('yy_XvTest.Doubler', 'Second');
    const { deps } = depsFor([a, b]);
    const [ra, rb] = await Promise.all([loadControl(a.name, deps), loadControl(b.name, deps)]);
    expect(ra.ctor.name).toBe('First');
    expect(rb.ctor.name).toBe('Second');
  });
});
