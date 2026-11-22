import { describe, expect, it, vi } from 'vitest';
import { createAppNotifications } from './appNotifications';

describe('app notifications store', () => {
  it('add returns an id and lists newest first', async () => {
    const s = createAppNotifications();
    const a = await s.add({ level: 1, message: 'first' });
    const b = await s.add({ level: 4, message: 'second', showCloseButton: true });
    expect(a).not.toBe(b);
    expect(s.list().map((n) => n.message)).toEqual(['second', 'first']);
    expect(s.list()[0].showCloseButton).toBe(true);
    expect(s.list()[1].showCloseButton).toBe(false);
  });

  it('missing level or message rejects TypeError', async () => {
    const s = createAppNotifications();
    await expect(s.add({ message: 'x' })).rejects.toBeInstanceOf(TypeError);
    await expect(s.add({ level: 1 })).rejects.toBeInstanceOf(TypeError);
    await expect(s.add({ level: 5, message: 'x' })).rejects.toBeInstanceOf(TypeError);
    await expect(s.add({ level: 1, message: 3 })).rejects.toBeInstanceOf(TypeError);
    await expect(s.add(null)).rejects.toBeInstanceOf(TypeError);
    expect(s.list()).toEqual([]);
  });

  it('an action without a string actionLabel or a function eventHandler rejects TypeError', async () => {
    const s = createAppNotifications();
    const handler = () => undefined;
    await expect(s.add({ level: 1, message: 'x', action: { eventHandler: handler } })).rejects.toBeInstanceOf(TypeError);
    await expect(s.add({ level: 1, message: 'x', action: { actionLabel: 3, eventHandler: handler } })).rejects.toBeInstanceOf(TypeError);
    await expect(s.add({ level: 1, message: 'x', action: { actionLabel: '', eventHandler: handler } })).rejects.toBeInstanceOf(TypeError);
    await expect(s.add({ level: 1, message: 'x', action: { actionLabel: 'Go' } })).rejects.toBeInstanceOf(TypeError);
    await expect(s.add({ level: 1, message: 'x', action: { actionLabel: 'Go', eventHandler: 'f' } })).rejects.toBeInstanceOf(TypeError);
    await expect(s.add({ level: 1, message: 'x', action: 'Go' })).rejects.toBeInstanceOf(TypeError);
    expect(s.list()).toEqual([]);
    // No action (absent or null) is fine; a complete one is kept.
    await expect(s.add({ level: 1, message: 'x', action: null })).resolves.toBeDefined();
    await expect(s.add({ level: 1, message: 'y', action: { actionLabel: 'Go', eventHandler: handler } })).resolves.toBeDefined();
    expect(s.list()[0].action).toEqual({ actionLabel: 'Go', eventHandler: handler });
  });

  it('clear removes, and an unknown id resolves', async () => {
    const s = createAppNotifications();
    const id = await s.add({ level: 2, message: 'x' });
    await s.clear('nope');
    expect(s.list()).toHaveLength(1);
    await s.clear(id);
    expect(s.list()).toEqual([]);
  });

  it('notifies subscribers until unsubscribed', async () => {
    const s = createAppNotifications();
    const fn = vi.fn();
    const off = s.subscribe(fn);
    await s.add({ level: 1, message: 'x' });
    off();
    await s.add({ level: 1, message: 'y' });
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it('dispose clears and later add rejects', async () => {
    const s = createAppNotifications();
    await s.add({ level: 1, message: 'x' });
    s.dispose();
    expect(s.list()).toEqual([]);
    await expect(s.add({ level: 1, message: 'y' })).rejects.toThrow('The app was closed');
  });
});
