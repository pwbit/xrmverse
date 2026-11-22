import { describe, expect, it } from 'vitest';
import { createAppNotifications } from './appNotifications';
import { createSidePanes } from './sidePanes';
import { createXrmVerse } from './xrmVerse';
import { createXrm } from '../xrm';
import { XRM_NOT_IMPLEMENTED } from '../notImplemented';

describe('createXrmVerse', () => {
  it('App has exactly the three members and sidePanes has its five', () => {
    const panes = createSidePanes();
    const xv = createXrmVerse(panes, createAppNotifications());
    expect(Object.keys(xv)).toEqual(['App']);
    expect(Object.keys(xv.App).sort()).toEqual(['addGlobalNotification', 'clearGlobalNotification', 'sidePanes']);
    expect(xv.App.sidePanes).toBe(panes.api);
    expect(['createPane', 'getAllPanes', 'getPane', 'getSelectedPane', 'state'].every((k) => k in xv.App.sidePanes)).toBe(true);
    expect(Object.isFrozen(xv)).toBe(true);
    expect(Object.isFrozen(xv.App)).toBe(true);
  });

  it('notifications go through the store', async () => {
    const notifications = createAppNotifications();
    const xv = createXrmVerse(createSidePanes(), notifications);
    const id = await xv.App.addGlobalNotification({ level: 4, message: 'Hi' });
    expect(notifications.list().map((n) => n.id)).toEqual([id]);
    await xv.App.clearGlobalNotification(id);
    expect(notifications.list()).toEqual([]);
  });

  it('Xrm.App and Xrm.Panel stay not available', () => {
    expect(XRM_NOT_IMPLEMENTED.map((m) => m.path)).toEqual(expect.arrayContaining(['Xrm.App', 'Xrm.Panel']));
    expect(typeof createXrm).toBe('function');
  });
});
