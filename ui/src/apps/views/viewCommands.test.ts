import { describe, expect, it, vi } from 'vitest';
import type { XrmApi } from '../../shell/api/client';
import { t } from '../../shell/i18n/t';
import { assignOp, commandState, readStates, setStateOp } from './viewCommands';

const WITH_STATE = { hasState: true };

describe('commandState › enablement', () => {
  it('none selected: every command disabled', () => {
    const s = commandState([], WITH_STATE);
    for (const c of [s.activate, s.deactivate, s.assign, s.share]) {
      expect(c.enabled).toBe(false);
      expect(c.enabled === false && c.reason).toBe(t('apps.views.cmd.noSelection'));
    }
  });

  it('one active: deactivate enabled, activate disabled', () => {
    const s = commandState([{ id: 'a', statecode: 0 }], WITH_STATE);
    expect(s.deactivate).toEqual({ enabled: true });
    expect(s.activate).toEqual({ enabled: false, reason: t('apps.views.cmd.allActive') });
  });

  it('one inactive: activate enabled, deactivate disabled', () => {
    const s = commandState([{ id: 'a', statecode: 1 }], WITH_STATE);
    expect(s.activate).toEqual({ enabled: true });
    expect(s.deactivate).toEqual({ enabled: false, reason: t('apps.views.cmd.allInactive') });
  });

  it('two active: deactivate enabled', () => {
    const s = commandState([{ id: 'a', statecode: 0 }, { id: 'b', statecode: 0 }], WITH_STATE);
    expect(s.deactivate).toEqual({ enabled: true });
  });

  it('active + inactive: both disabled with the mixed states reason', () => {
    const s = commandState([{ id: 'a', statecode: 0 }, { id: 'b', statecode: 1 }], WITH_STATE);
    expect(s.activate).toEqual({ enabled: false, reason: t('apps.views.cmd.mixedStates') });
    expect(s.deactivate).toEqual({ enabled: false, reason: t('apps.views.cmd.mixedStates') });
  });

  it('a state not read yet: both disabled until it is', () => {
    const s = commandState([{ id: 'a', statecode: 0 }, { id: 'b' }], WITH_STATE);
    expect(s.activate).toEqual({ enabled: false, reason: t('apps.views.cmd.statesUnknown') });
    expect(s.deactivate).toEqual({ enabled: false, reason: t('apps.views.cmd.statesUnknown') });
  });

  it('a table without state: activate and deactivate hidden (empty reason)', () => {
    const s = commandState([{ id: 'a' }], { hasState: false });
    expect(s.activate).toEqual({ enabled: false, reason: '' });
    expect(s.deactivate).toEqual({ enabled: false, reason: '' });
  });

  it('assign and share enabled for one or more', () => {
    for (const sel of [[{ id: 'a' }], [{ id: 'a', statecode: 0 }, { id: 'b', statecode: 1 }]]) {
      const s = commandState(sel, WITH_STATE);
      expect(s.assign).toEqual({ enabled: true });
      expect(s.share).toEqual({ enabled: true });
    }
  });
});

describe('the requests', () => {
  const api = () => ({ patch: vi.fn().mockResolvedValue(undefined), get: vi.fn() });

  it('setStateOp patches statecode and statuscode (statecode only without a status)', async () => {
    const a = api();
    await setStateOp(a as unknown as XrmApi, 'accounts', 1, 2)('id1');
    await setStateOp(a as unknown as XrmApi, 'accounts', 0)('id2');
    expect(a.patch.mock.calls).toEqual([
      ['accounts(id1)', { statecode: 1, statuscode: 2 }],
      ['accounts(id2)', { statecode: 0 }],
    ]);
  });

  it('assignOp binds the owner to the user or team', async () => {
    const a = api();
    await assignOp(a as unknown as XrmApi, 'accounts', { id: 'u1', name: 'U', target: 'systemuser', set: 'systemusers' })('id1');
    await assignOp(a as unknown as XrmApi, 'accounts', { id: 't1', name: 'T', target: 'team' })('id2');
    expect(a.patch.mock.calls).toEqual([
      ['accounts(id1)', { 'ownerid@odata.bind': '/systemusers(u1)' }],
      ['accounts(id2)', { 'ownerid@odata.bind': '/teams(t1)' }],
    ]);
  });

  it('readStates reads the statecode of the records with one request', async () => {
    const a = api();
    a.get.mockResolvedValue({ value: [{ accountid: 'a', statecode: 0 }, { accountid: 'b', statecode: 1 }] });
    expect(await readStates(a as unknown as XrmApi, 'accounts', 'accountid', ['a', 'b'])).toEqual({ a: 0, b: 1 });
    expect(a.get).toHaveBeenCalledTimes(1);
    expect(a.get.mock.calls[0][0]).toBe('accounts?$select=accountid,statecode&$filter=accountid eq a or accountid eq b');
  });
});
