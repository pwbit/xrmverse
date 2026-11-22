import { describe, expect, it, vi } from 'vitest';
import type { XrmApi } from '../../shell/api/client';
import { t } from '../../shell/i18n/t';
import { addToQueueOp, pickOp, queueCommandState, releaseOp, removeOp, routeOp } from './queueCommands';

const NS = 'Microsoft.Dynamics.CRM';
const api = () => ({ post: vi.fn().mockResolvedValue(undefined) });
const as = (a: ReturnType<typeof api>) => a as unknown as XrmApi;
const HIDDEN = { enabled: false, reason: '' };

describe('queueCommandState', () => {
  it('Add to queue is shown on a queue-enabled table only', () => {
    expect(queueCommandState(1, { logicalName: 'email', isValidForQueue: true }).add).toEqual({ enabled: true });
    expect(queueCommandState(1, { logicalName: 'account', isValidForQueue: false }).add).toEqual(HIDDEN);
    expect(queueCommandState(1, { logicalName: 'account' }).add).toEqual(HIDDEN);
  });

  it('Pick, Release, Route and Remove are shown on queueitem only', () => {
    const on = queueCommandState(2, { logicalName: 'queueitem' });
    for (const c of [on.pick, on.release, on.route, on.remove]) expect(c).toEqual({ enabled: true });
    expect(on.add).toEqual(HIDDEN);
    const off = queueCommandState(2, { logicalName: 'email', isValidForQueue: true });
    for (const c of [off.pick, off.release, off.route, off.remove]) expect(c).toEqual(HIDDEN);
  });

  it('with nothing selected the commands that apply are disabled with the reason', () => {
    const s = queueCommandState(0, { logicalName: 'queueitem' });
    for (const c of [s.pick, s.release, s.route, s.remove]) expect(c).toEqual({ enabled: false, reason: t('apps.views.cmd.noSelection') });
  });
});

describe('the requests', () => {
  it('addToQueueOp posts AddToQueue bound to the queue with the record as Target', async () => {
    const a = api();
    await addToQueueOp(as(a), { logicalName: 'email', primaryId: 'activityid' }, 'Q1')('E1');
    expect(a.post).toHaveBeenCalledWith(`queues(Q1)/${NS}.AddToQueue`, { Target: { '@odata.type': `${NS}.email`, activityid: 'E1' } });
  });

  it('removeOp and releaseOp post to the queue item without a body', async () => {
    const a = api();
    await removeOp(as(a))('I1');
    await releaseOp(as(a))('I2');
    expect(a.post.mock.calls).toEqual([
      [`queueitems(I1)/${NS}.RemoveFromQueue`, {}],
      [`queueitems(I2)/${NS}.ReleaseToQueue`, {}],
    ]);
  });

  it('pickOp posts PickFromQueue with the worker and keeps the queue item', async () => {
    const a = api();
    await pickOp(as(a), 'U1')('I1');
    expect(a.post).toHaveBeenCalledWith(`queueitems(I1)/${NS}.PickFromQueue`, {
      SystemUser: { '@odata.type': `${NS}.systemuser`, systemuserid: 'U1' },
      RemoveQueueItem: false,
    });
  });

  it('routeOp posts RouteTo with the target and the queue item', async () => {
    const a = api();
    await routeOp(as(a), { kind: 'queue', id: 'Q1', name: 'Support' })('I1');
    await routeOp(as(a), { kind: 'team', id: 'T1', name: 'Sales' })('I2');
    expect(a.post.mock.calls).toEqual([
      ['RouteTo', { Target: { '@odata.type': `${NS}.queue`, queueid: 'Q1' }, QueueItem: { queueitemid: 'I1' } }],
      ['RouteTo', { Target: { '@odata.type': `${NS}.team`, teamid: 'T1' }, QueueItem: { queueitemid: 'I2' } }],
    ]);
  });
});
