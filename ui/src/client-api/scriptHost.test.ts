import { afterEach, describe, expect, it } from 'vitest';
import { createScriptHost, webResourceUrl, type ScriptHost } from './scriptHost';
import { sourceLoader } from './testHost';

const hosts: ScriptHost[] = [];
const make = async (libs: string[], sources: Record<string, string>, install: (w: Window) => void = () => undefined) => {
  const host = await createScriptHost(libs, install, sourceLoader(sources));
  hosts.push(host);
  return host;
};
afterEach(() => hosts.splice(0).forEach((h) => h.dispose()));

describe('script host', () => {
  it('host_loads_libraries_in_order_into_own_window', async () => {
    const host = await make(['a.js', 'b.js'], { 'a.js': 'var base = 7;', 'b.js': 'var seen = base + 1;' });
    expect(host.failed).toEqual([]);
    expect((host.window as unknown as { seen: number }).seen).toBe(8);
    expect(document.querySelector('iframe')?.style.display).toBe('none');
  });

  it('install_runs_before_the_first_library', async () => {
    const host = await make(['a.js'], { 'a.js': 'var got = Marker;' }, (w) => ((w as unknown as { Marker: string }).Marker = 'xrm'));
    expect((host.window as unknown as { got: string }).got).toBe('xrm');
  });

  it('host_window_globals_do_not_leak_to_shell', async () => {
    await make(['a.js'], { 'a.js': 'var leaky = 1; window.alsoLeaky = 2;' });
    expect('leaky' in window).toBe(false);
    expect('alsoLeaky' in window).toBe(false);
  });

  it('resolve_dotted_function_name', async () => {
    const host = await make(['a.js'], { 'a.js': 'var OP = { Account: { onLoad: function () { return this === OP.Account; } }, n: 1 }; function topLevel() { return 5; }' });
    expect(host.resolve('OP.Account.onLoad')!()).toBe(true);
    expect(host.resolve('topLevel')!()).toBe(5);
    expect(host.resolve('OP.Account.missing')).toBeUndefined();
    expect(host.resolve('OP.n')).toBeUndefined();
    expect(host.resolve('Nope.deeper.still')).toBeUndefined();
  });

  it('missing_library_reported_form_still_opens', async () => {
    const host = await make(['gone.js', 'b.js'], { 'b.js': 'var ok = 1;' });
    expect(host.failed).toEqual(['gone.js']);
    expect((host.window as unknown as { ok: number }).ok).toBe(1);
  });

  it('syntax_error_library_reported', async () => {
    const host = await make(['bad.js', 'b.js'], { 'bad.js': 'function (', 'b.js': 'var ok = 1;' });
    expect(host.failed).toEqual(['bad.js']);
    expect((host.window as unknown as { ok: number }).ok).toBe(1);
  });

  it('dispose_removes_the_window_and_stops_its_timers', async () => {
    const host = await make(['a.js'], { 'a.js': 'var ticks = 0; setInterval(function () { ticks++; }, 5);' });
    const win = host.window as unknown as { ticks: number };
    await new Promise((r) => setTimeout(r, 30));
    expect(win.ticks).toBeGreaterThan(0);
    host.dispose();
    expect(document.querySelector('iframe')).toBeNull();
    const after = win.ticks;
    await new Promise((r) => setTimeout(r, 30));
    expect(win.ticks).toBe(after);
  });

  it('web_resource_url_keeps_slashes_and_encodes_the_rest', () => {
    expect(webResourceUrl('op365_/scripts/account.form.js')).toBe('/WebResources/op365_/scripts/account.form.js');
    expect(webResourceUrl('a b/c#d.js')).toBe('/WebResources/a%20b/c%23d.js');
  });
});

describe('script host failures and abort', () => {
  it('install_throwing_rejects_and_removes_the_iframe', async () => {
    await expect(
      createScriptHost(['a.js'], () => {
        throw new Error('boom');
      }, sourceLoader({ 'a.js': '' })),
    ).rejects.toThrow('boom');
    expect(document.querySelectorAll('iframe')).toHaveLength(0);
  });

  it('abort_mid_load_never_runs_the_next_library_and_removes_the_iframe', async () => {
    const controller = new AbortController();
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    const inner = sourceLoader({ 'a.js': 'var a = 1;', 'b.js': 'window.parent.document.__bRan = true;' });
    const loader = async (win: Window, url: string, name: string) => {
      if (name === 'a.js') await gate;
      return inner(win, url, name);
    };
    const pending = createScriptHost(['a.js', 'b.js'], () => undefined, loader, controller.signal);
    const settled = expect(pending).rejects.toMatchObject({ name: 'AbortError' });
    expect(document.querySelectorAll('iframe')).toHaveLength(1);
    controller.abort();
    expect(document.querySelectorAll('iframe')).toHaveLength(0);
    release();
    await settled;
    expect((document as unknown as { __bRan?: boolean }).__bRan).toBeUndefined();
  });
});
