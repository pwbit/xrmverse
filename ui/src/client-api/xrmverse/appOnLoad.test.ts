import { afterEach, describe, expect, it, vi } from 'vitest';
import { setLibraryLoader } from '../scriptHost';
import { sourceLoader } from '../testHost';
import { runAppOnLoad } from './appOnLoad';

const setup = (sources: Record<string, string>) => {
  setLibraryLoader(sourceLoader(sources));
  const notes: string[] = [];
  const shared = { marker: 1, log: [] as string[], ran: false };
  const controller = new AbortController();
  const deps = {
    install: (win: Window) => void ((win as unknown as { Xrm: unknown }).Xrm = shared),
    globalContext: { id: 'global' },
    notify: (n: { level: 2; message: string }) => void notes.push(`${n.level}:${n.message}`),
    signal: controller.signal,
  };
  return { notes, controller, deps, shared };
};
const handlers = (...h: [string, string][]) => ({ handlers: h.map(([library, fn]) => ({ library, function: fn })) });
const frames = () => document.querySelectorAll('iframe').length;

afterEach(() => {
  setLibraryLoader(undefined);
  vi.useRealTimers();
});

describe('runAppOnLoad', () => {
  it('runs the handlers in array order and awaits an async one before the next', async () => {
    const { deps, notes, shared } = setup({
      'a.js': `var log = Xrm.log; function slow(ctx) { return new Promise(function (r) { setTimeout(function () { log.push('slow'); r(); }, 20); }); }
        function fast(ctx) { log.push('fast:' + (ctx.getEventSource() === null) + ':' + ctx.getContext().id + ':' + (window.Xrm.marker)); }`,
    });
    await runAppOnLoad(handlers(['a.js', 'slow'], ['a.js', 'fast']), deps);
    expect(shared.log).toEqual(['slow', 'fast:true:global:1']);
    expect(notes).toEqual([]);
    deps.signal.dispatchEvent(new Event('abort'));
  });

  it('an { error } setting shows one notification', async () => {
    const { deps, notes } = setup({});
    await runAppOnLoad({ error: 'xrmverse_AppOnLoad is not valid JSON' }, deps);
    expect(notes).toEqual(['2:App OnLoad: xrmverse_AppOnLoad is not valid JSON']);
  });

  it('reports a missing library, a missing function, a throw and a rejection, and the next handler still runs', async () => {
    const { deps, notes, shared } = setup({
      'a.js': `function boom() { throw new Error('bad'); } function rej() { return Promise.reject(new Error('no')); } function ok() { Xrm.ran = true; }`,
    });
    await runAppOnLoad(handlers(['gone.js', 'x'], ['a.js', 'nope'], ['a.js', 'boom'], ['a.js', 'rej'], ['a.js', 'ok']), deps);
    expect(notes).toEqual([
      '2:App OnLoad: gone.js/x: the library could not be loaded',
      '2:App OnLoad: a.js/nope: the function was not found',
      '2:App OnLoad: a.js/boom: bad',
      '2:App OnLoad: a.js/rej: no',
    ]);
    expect(shared.ran).toBe(true);
  });

  it('a handler that never settles times out after 10 seconds and the next runs', async () => {
    const { deps, notes, shared } = setup({ 'a.js': `function hang() { return new Promise(function () {}); } function ok() { Xrm.ran = true; }` });
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    const run = runAppOnLoad(handlers(['a.js', 'hang'], ['a.js', 'ok']), deps);
    await vi.advanceTimersByTimeAsync(9_999);
    expect(notes).toEqual([]);
    await vi.advanceTimersByTimeAsync(1);
    await run;
    expect(notes).toEqual(['2:App OnLoad: a.js/hang: timed out after 10 seconds']);
    expect(shared.ran).toBe(true);
  });

  it('late handler after dispose is ignored', async () => {
    const { deps, notes, controller } = setup({ 'a.js': `function hang() { return new Promise(function (_, rej) { setTimeout(function () { rej(new Error('late')); }, 30); }); }` });
    const before = frames();
    const run = runAppOnLoad(handlers(['a.js', 'hang']), deps);
    await vi.waitFor(() => expect(frames()).toBe(before + 1));
    controller.abort();
    await run;
    await new Promise((r) => setTimeout(r, 60));
    expect(notes).toEqual([]);
    expect(frames()).toBe(before);
  });

  it('no handlers creates no script host', async () => {
    const { deps, notes } = setup({});
    const before = frames();
    await runAppOnLoad({ handlers: [] }, deps);
    await runAppOnLoad(undefined, deps);
    expect(frames()).toBe(before);
    expect(notes).toEqual([]);
  });

  it('keeps the host until the signal aborts', async () => {
    const { deps, controller } = setup({ 'a.js': 'function ok() {}' });
    const before = frames();
    await runAppOnLoad(handlers(['a.js', 'ok']), deps);
    expect(frames()).toBe(before + 1);
    controller.abort();
    expect(frames()).toBe(before);
  });
});
