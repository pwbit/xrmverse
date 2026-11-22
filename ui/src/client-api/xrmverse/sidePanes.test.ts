import { describe, expect, it, vi } from 'vitest';
import { createSidePanes } from './sidePanes';

describe('createSidePanes', () => {
  it('creates panes with defaults and generated ids', async () => {
    const s = createSidePanes();
    const a = await s.api.createPane();
    const b = await s.api.createPane({});
    expect([a.paneId, b.paneId]).toEqual(['pane-1', 'pane-2']);
    expect(a).toMatchObject({ title: '', width: 340, canClose: true, hideHeader: false, hidden: false, alwaysRender: false, keepBadgeOnSelect: false });
    expect(s.api.state).toBe(1);
    expect(s.api.getSelectedPane()).toBe(b);
    expect(s.api.getPane('pane-1')).toBe(a);
    expect(s.api.getPane('nope')).toBeUndefined();
  });

  it('rejects a duplicate paneId', async () => {
    const s = createSidePanes();
    const first = await s.api.createPane({ paneId: 'x', title: 'First', width: 400 });
    await expect(s.api.createPane({ paneId: 'x', title: 'Second' })).rejects.toThrow(/already exists/);
    expect(s.api.getAllPanes()).toEqual([first]);
    expect(s.snapshot().panes).toHaveLength(1);
    expect(s.snapshot().panes[0]).toMatchObject({ paneId: 'x', title: 'First', width: 400 });
  });

  it('orders non-closable panes first', async () => {
    const s = createSidePanes();
    await s.api.createPane({ paneId: 'a' });
    await s.api.createPane({ paneId: 'b', canClose: false });
    await s.api.createPane({ paneId: 'c' });
    await s.api.createPane({ paneId: 'd', canClose: false });
    expect(s.api.getAllPanes().map((p) => p.paneId)).toEqual(['b', 'd', 'a', 'c']);
    expect(s.snapshot().panes.map((p) => p.paneId)).toEqual(['b', 'd', 'a', 'c']);
  });

  it('isSelected false keeps the selection and does not expand', async () => {
    const s = createSidePanes();
    await s.api.createPane({ paneId: 'a', isSelected: false });
    expect(s.api.state).toBe(0);
    expect(s.api.getSelectedPane()).toBeUndefined();
    await s.api.createPane({ paneId: 'b' });
    s.api.state = 0;
    await s.api.createPane({ paneId: 'c', isSelected: false });
    expect(s.api.getSelectedPane()?.paneId).toBe('b');
    expect(s.api.state).toBe(0);
  });

  it('accepts only 0 or 1 as state', () => {
    const s = createSidePanes();
    expect(() => {
      (s.api as { state: unknown }).state = 2;
    }).toThrow(TypeError);
    s.api.state = 1;
    expect(s.api.state).toBe(1);
  });

  it('accepts only WebResources/ image paths', async () => {
    const s = createSidePanes();
    await expect(s.api.createPane({ imageSrc: 'https://x.test/a.png' })).rejects.toThrow(TypeError);
    const p = await s.api.createPane({ imageSrc: 'WebResources/a/icon.svg' });
    expect(s.snapshot().panes[0]!.imageSrc).toBe('WebResources/a/icon.svg');
    expect(() => {
      p.imageSrc = '/etc/passwd';
    }).toThrow(TypeError);
  });

  it('setters notify subscribers', async () => {
    const s = createSidePanes();
    const p = await s.api.createPane();
    const fn = vi.fn();
    s.subscribe(fn);
    p.title = 'T';
    p.width = 400;
    expect(fn).toHaveBeenCalledTimes(2);
    expect(s.snapshot().panes[0]).toMatchObject({ title: 'T', width: 400 });
  });

  it('badges: dot, count, 99+, cleared on select unless kept', async () => {
    const s = createSidePanes();
    const a = await s.api.createPane({ paneId: 'a' });
    const b = await s.api.createPane({ paneId: 'b', keepBadgeOnSelect: true });
    a.badge = true;
    expect(s.snapshot().panes[0]!.badge).toBe(true);
    a.badge = 7;
    expect(s.snapshot().panes[0]!.badge).toBe(7);
    a.badge = 120;
    expect(s.snapshot().panes[0]!.badge).toBe('99+');
    b.badge = 3;
    a.select();
    expect(s.snapshot().panes[0]!.badge).toBe(false);
    b.select();
    expect(s.snapshot().panes[1]!.badge).toBe(3);
    a.badge = null;
    expect(a.badge).toBe(false);
  });

  it('navigate rejects other page types', async () => {
    const s = createSidePanes();
    const p = await s.api.createPane();
    await expect(p.navigate({ pageType: 'entityrecord', entityName: 'contact' })).rejects.toThrow(
      'entityrecord is not available in a side pane in this version',
    );
  });

  it('navigate webresource sets content, bumps contentKey and resolves when loaded', async () => {
    const s = createSidePanes();
    const p = await s.api.createPane();
    const done = p.navigate({ pageType: 'webresource', webresourceName: 'a/b.html', data: 'x=1' });
    expect(s.snapshot().panes[0]).toMatchObject({ content: { webresourceName: 'a/b.html', data: 'x=1' }, contentKey: 1 });
    s.markPaneLoaded('pane-1', 1);
    await expect(done).resolves.toBeUndefined();
    const again = p.navigate({ pageType: 'webresource', webresourceName: 'c.html' });
    expect(s.snapshot().panes[0]).toMatchObject({ content: { webresourceName: 'c.html' }, contentKey: 2 });
    s.markPaneLoaded('pane-1', 2);
    await again;
  });

  it('navigate rejects an invalid web resource name', async () => {
    const s = createSidePanes();
    const p = await s.api.createPane();
    await expect(p.navigate({ pageType: 'webresource', webresourceName: '../x' })).rejects.toThrow(/not a web resource name/);
  });

  it('setPaneError rejects the pending navigate and shows the error', async () => {
    const s = createSidePanes();
    const p = await s.api.createPane();
    const done = p.navigate({ pageType: 'webresource', webresourceName: 'missing.html' });
    s.setPaneError('pane-1', 1, 'Not found');
    await expect(done).rejects.toThrow('Not found');
    expect(s.snapshot().panes[0]!.error).toBe('Not found');
  });

  it('stale setPaneError is ignored', async () => {
    const s = createSidePanes();
    const p = await s.api.createPane();
    const a = p.navigate({ pageType: 'webresource', webresourceName: 'a.html' });
    const b = p.navigate({ pageType: 'webresource', webresourceName: 'b.html' });
    await a;
    s.setPaneError('pane-1', 1, 'late A failure');
    expect(s.snapshot().panes[0]!.error).toBeUndefined();
    let settled = false;
    void b.then(() => (settled = true), () => (settled = true));
    await Promise.resolve();
    expect(settled).toBe(false);
    s.markPaneLoaded('pane-1', 2);
    await expect(b).resolves.toBeUndefined();
    expect(s.snapshot().panes[0]!.error).toBeUndefined();
  });

  it('a stale markPaneLoaded does not resolve the newer navigate', async () => {
    const s = createSidePanes();
    const p = await s.api.createPane();
    void p.navigate({ pageType: 'webresource', webresourceName: 'a.html' });
    const b = p.navigate({ pageType: 'webresource', webresourceName: 'b.html' });
    let settled = false;
    void b.then(() => (settled = true));
    s.markPaneLoaded('pane-1', 1);
    await Promise.resolve();
    expect(settled).toBe(false);
    s.markPaneLoaded('pane-1', 2);
    await b;
  });

  it('a pending navigate resolves on close and on dispose', async () => {
    const s = createSidePanes();
    const a = await s.api.createPane({ paneId: 'a' });
    const b = await s.api.createPane({ paneId: 'b' });
    const pa = a.navigate({ pageType: 'webresource', webresourceName: 'a.html' });
    const pb = b.navigate({ pageType: 'webresource', webresourceName: 'b.html' });
    a.close();
    await expect(pa).resolves.toBeUndefined();
    s.dispose();
    await expect(pb).resolves.toBeUndefined();
  });

  it('reorders when canClose changes', async () => {
    const s = createSidePanes();
    await s.api.createPane({ paneId: 'a' });
    const b = await s.api.createPane({ paneId: 'b' });
    await s.api.createPane({ paneId: 'c' });
    b.canClose = false;
    expect(s.api.getAllPanes().map((p) => p.paneId)).toEqual(['b', 'a', 'c']);
    expect(s.snapshot().panes.map((p) => p.paneId)).toEqual(['b', 'a', 'c']);
  });

  it('a superseded navigate resolves', async () => {
    const s = createSidePanes();
    const p = await s.api.createPane();
    const first = p.navigate({ pageType: 'webresource', webresourceName: 'a.html' });
    const second = p.navigate({ pageType: 'webresource', webresourceName: 'b.html' });
    await expect(first).resolves.toBeUndefined();
    s.markPaneLoaded('pane-1', 2);
    await expect(second).resolves.toBeUndefined();
  });

  it('close works when canClose is false and moves the selection', async () => {
    const s = createSidePanes();
    await s.api.createPane({ paneId: 'a' });
    const b = await s.api.createPane({ paneId: 'b', canClose: false });
    b.close();
    expect(s.api.getPane('b')).toBeUndefined();
    expect(s.api.getSelectedPane()?.paneId).toBe('a');
    expect(s.snapshot().panes.map((p) => p.paneId)).toEqual(['a']);
  });

  it('dispose empties the store and later createPane rejects', async () => {
    const s = createSidePanes();
    await s.api.createPane();
    const fn = vi.fn();
    s.subscribe(fn);
    s.dispose();
    expect(fn).toHaveBeenCalled();
    expect(s.api.getAllPanes()).toEqual([]);
    expect(s.snapshot()).toMatchObject({ state: 0, panes: [] });
    await expect(s.api.createPane()).rejects.toThrow('The app was closed');
  });

  it('snapshot is stable until a change', async () => {
    const s = createSidePanes();
    await s.api.createPane();
    expect(s.snapshot()).toBe(s.snapshot());
  });
});
