import { afterEach, describe, expect, it } from 'vitest';
import type { Handler } from '../model/form';
import type { FormNotification } from '../renderers/classic/form/FormNotifications';
import { createExecutionContext } from './executionContext';
import { parseParameters, runHandlers, type HandlerSlot, type RunMode } from './events';
import { createScriptHost, type ScriptHost } from './scriptHost';
import { sourceLoader } from './testHost';

const hosts: ScriptHost[] = [];
afterEach(() => hosts.splice(0).forEach((h) => h.dispose()));

const SOURCE = `
  var calls = [];
  var Lib = {
    boom: function () { throw new Error('exploded'); },
    ok: function () { calls.push(['ok'].concat([].slice.call(arguments))); },
    withCtx: function (ctx) { calls.push(['ctx', typeof ctx.getFormContext, ctx.getFormContext().tag].concat([].slice.call(arguments, 1))); },
    setShared: function (ctx) { ctx.setSharedVariable('k', 'v'); },
    readShared: function (ctx) { calls.push(['shared', ctx.getSharedVariable('k')]); },
    reject: function () { return Promise.reject(new Error('later')); },
    slow: function () { return new Promise(function (resolve) { setTimeout(function () { calls.push(['slow']); resolve(); }, 40); }); },
    never: function () { return new Promise(function () {}); }
  };
`;

const handler = (functionName: string, over: Partial<Handler> = {}): Handler => ({
  functionName,
  libraryName: 'lib.js',
  enabled: true,
  parameters: '',
  passExecutionContext: false,
  ...over,
});

async function run(handlers: Handler[], libs = ['lib.js'], sources: Record<string, string> = { 'lib.js': SOURCE }, mode?: RunMode) {
  const host = await createScriptHost(libs, () => undefined, sourceLoader(sources));
  hosts.push(host);
  const notes: FormNotification[] = [];
  const done = runHandlers(host, handlers, () => createExecutionContext({ formContext: { tag: 'stub' } }), (n) => notes.push(n), mode);
  const calls = (host.window as unknown as { calls: unknown[][] }).calls;
  return { host, notes, calls, done };
}

/** Slots that record which handlers failed; `noTimeout` lists the handlers (by position) that disable their timeout. */
function slots(noTimeout: number[] = []) {
  const failed: number[] = [];
  let index = -1;
  const slot = (): HandlerSlot => {
    const mine = ++index;
    return { timeoutDisabled: () => noTimeout.includes(mine), failed: () => void failed.push(mine) };
  };
  return { failed, slot };
}

describe('runHandlers', () => {
  it('handler_throws_notification_next_handler_runs', async () => {
    const { notes, calls } = await run([handler('Lib.boom'), handler('Lib.ok')]);
    expect(notes).toHaveLength(1);
    expect(notes[0]).toMatchObject({ intent: 'error', text: 'Script error in Lib.boom: exploded' });
    expect(calls).toEqual([['ok']]);
  });

  it('unknown_function_notification', async () => {
    const { notes, calls } = await run([handler('Lib.nope'), handler('Lib.ok')]);
    expect(notes.map((n) => n.text)).toEqual(['Script error: Lib.nope was not found in lib.js.']);
    expect(calls).toEqual([['ok']]);
  });

  it('disabled_handler_not_run', async () => {
    const { notes, calls } = await run([handler('Lib.ok', { enabled: false })]);
    expect(notes).toEqual([]);
    expect(calls).toEqual([]);
  });

  it('parameters_passed_after_context', async () => {
    const { notes, calls } = await run([handler('Lib.withCtx', { passExecutionContext: true, parameters: '1, "two", true, null' })]);
    expect(notes).toEqual([]);
    expect(calls).toEqual([['ctx', 'function', 'stub', 1, 'two', true, null]]);
  });

  it('bad_parameters_notify', async () => {
    const { notes, calls } = await run([handler('Lib.ok', { parameters: 'not json' }), handler('Lib.ok')]);
    expect(notes).toHaveLength(1);
    expect(notes[0]!.intent).toBe('error');
    expect(notes[0]!.text).toMatch(/^Script error in Lib\.ok: the parameters could not be read/);
    expect(calls).toEqual([['ok']]);
  });

  it('pass_execution_context_false_passes_parameters_only', async () => {
    const { calls } = await run([handler('Lib.ok', { parameters: '"a", 2' })]);
    expect(calls).toEqual([['ok', 'a', 2]]);
  });

  it('shared_variables_pass_between_handlers_of_one_run', async () => {
    const { calls } = await run([handler('Lib.setShared', { passExecutionContext: true }), handler('Lib.readShared', { passExecutionContext: true })]);
    expect(calls).toEqual([['shared', 'v']]);
  });

  it('library_that_failed_to_load_is_not_reported_twice', async () => {
    const { notes, host } = await run([handler('Lib.ok', { libraryName: 'gone.js' })], ['gone.js'], {});
    expect(host.failed).toEqual(['gone.js']);
    expect(notes).toEqual([]);
  });

  it('rejected_promise_is_reported_not_awaited', async () => {
    const { notes } = await run([handler('Lib.reject')]);
    await new Promise((r) => setTimeout(r, 0));
    expect(notes.map((n) => n.text)).toEqual(['Script error in Lib.reject: later']);
  });
});

describe('runHandlers with the async app setting', () => {
  it('without_wait_the_next_handler_runs_before_a_promise_settles', async () => {
    const { calls, done } = await run([handler('Lib.slow'), handler('Lib.ok')]);
    await done;
    expect(calls).toEqual([['ok']]);
  });

  it('with_wait_each_promise_settles_before_the_next_handler_runs', async () => {
    const { calls, done, notes } = await run([handler('Lib.slow'), handler('Lib.ok')], undefined, undefined, { wait: true });
    await done;
    expect(calls).toEqual([['slow'], ['ok']]);
    expect(notes).toEqual([]);
  });

  it('a_promise_that_does_not_settle_times_out_and_the_next_handler_runs', async () => {
    const s = slots();
    const { calls, done, notes } = await run([handler('Lib.never'), handler('Lib.ok')], undefined, undefined, { wait: true, timeoutMs: 20, slot: s.slot });
    await done;
    expect(notes.map((n) => n.text)).toEqual(['Lib.never did not finish within 0.02 seconds; the form went on without it.']);
    expect(calls).toEqual([['ok']]);
    expect(s.failed).toEqual([0]);
  });

  it('a_handler_that_disabled_its_timeout_is_awaited_past_it', async () => {
    const s = slots([0]);
    const { calls, done, notes } = await run([handler('Lib.slow'), handler('Lib.ok')], undefined, undefined, { wait: true, timeoutMs: 10, slot: s.slot });
    await done;
    expect(notes).toEqual([]);
    expect(calls).toEqual([['slow'], ['ok']]);
    expect(s.failed).toEqual([]);
  });

  it('a_throw_and_a_rejection_fail_their_own_slot', async () => {
    const s = slots();
    const { done, notes } = await run([handler('Lib.ok'), handler('Lib.boom'), handler('Lib.reject')], undefined, undefined, { wait: true, slot: s.slot });
    await done;
    expect(s.failed).toEqual([1, 2]);
    expect(notes.map((n) => n.text)).toEqual(['Script error in Lib.boom: exploded', 'Script error in Lib.reject: later']);
  });
});

describe('parseParameters', () => {
  it('reads_a_comma_separated_list', () => {
    expect(parseParameters('')).toEqual([]);
    expect(parseParameters(' 1, "a", [2], {"x": 3}')).toEqual([1, 'a', [2], { x: 3 }]);
    expect(() => parseParameters('a b')).toThrow();
  });
});

describe('execution context', () => {
  it('event_source_args_and_depth', () => {
    const attr = {};
    const ctx = createExecutionContext({ formContext: 'fc', eventSource: attr, eventArgs: { x: 1 } });
    expect(ctx.getEventSource()).toBe(attr);
    expect(ctx.getEventArgs()).toEqual({ x: 1 });
    expect(ctx.getDepth()).toBe(1);
    expect(createExecutionContext({ formContext: 'fc' }).getEventSource()).toBe('fc');
    expect(createExecutionContext({ formContext: 'fc' }).getEventArgs()).toBeNull();
    expect(() => ctx.getContext()).toThrow('not available in this version');
  });
});
