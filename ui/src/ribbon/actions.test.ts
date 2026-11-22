import { describe, expect, it, vi } from 'vitest';
import { runActions } from './actions';
import type { RibbonEnv } from './rules';

const env = (fns: Record<string, Function>): RibbonEnv => ({
  formContext: 'fc', entity: 'xv_order', recordId: 'id1', formId: 'f', formType: 'existing', readOnly: false, allDisabled: false,
  valueOf: () => ({ onForm: false, text: '' }), lcid: 1033, orgLcid: 1033, orgName: 'o',
  resolve: (library, fn) => (fns[`${library}:${fn}`] ? Promise.resolve(fns[`${library}:${fn}`]!) : Promise.reject(new Error(`${fn} not found`))),
});
const act = (functionName: string, parameters: { kind: 'crm' | 'string'; value: string }[] = []) => ({ library: 'l.js', functionName, parameters });

describe('runActions', () => {
  it('actions_run_in_order', async () => {
    const log: string[] = [];
    const a = vi.fn(async () => { await new Promise((r) => setTimeout(r, 5)); log.push('a'); });
    const b = vi.fn(() => { log.push('b'); });
    await runActions([act('a', [{ kind: 'crm', value: 'PrimaryControl' }, { kind: 'string', value: 's' }]), act('b')], env({ 'l.js:a': a, 'l.js:b': b }));
    expect(log).toEqual(['a', 'b']);
    expect(a).toHaveBeenCalledWith('fc', 's');
  });

  it('action_error_rejects_with_message', async () => {
    const after = vi.fn();
    const bad = () => Promise.reject(new Error('it failed'));
    await expect(runActions([act('bad'), act('after')], env({ 'l.js:bad': bad, 'l.js:after': after }))).rejects.toThrow('it failed');
    expect(after).not.toHaveBeenCalled();
  });

  it('a_synchronous_throw_and_an_unresolvable_function_reject', async () => {
    await expect(runActions([act('t')], env({ 'l.js:t': () => { throw new Error('sync'); } }))).rejects.toThrow('sync');
    await expect(runActions([act('missing')], env({}))).rejects.toThrow('missing not found');
  });

  it('no_actions_resolves', async () => expect(runActions([], env({}))).resolves.toBeUndefined());
});
