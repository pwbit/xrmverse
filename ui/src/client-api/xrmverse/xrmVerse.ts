import type { AppNotificationStore } from './appNotifications';
import type { SidePanesApi, SidePaneStore } from './sidePanes';

/** xrmverse's own client extension namespace (not part of Dataverse); set next to `Xrm` inside a model-driven app. */
export interface XrmVerseNamespace {
  App: {
    sidePanes: SidePanesApi;
    addGlobalNotification(n: unknown): Promise<string>;
    clearGlobalNotification(id: string): Promise<void>;
  };
}

/** `XrmVerse` for one open app, on top of its side pane and notification stores. */
export function createXrmVerse(panes: SidePaneStore, notifications: AppNotificationStore): XrmVerseNamespace {
  const app = Object.freeze({
    sidePanes: panes.api,
    addGlobalNotification: (n: unknown) => notifications.add(n),
    clearGlobalNotification: (id: string) => notifications.clear(id),
  });
  return Object.freeze({ App: app });
}

/** What lives as long as one open app: its stores and the `XrmVerse` over them. */
export interface AppRuntime {
  panes: SidePaneStore;
  notifications: AppNotificationStore;
  xrmVerse: XrmVerseNamespace;
  /** Closes the panes and clears the notifications; later `createPane` and `add` reject. Safe to call twice. */
  dispose(): void;
}
