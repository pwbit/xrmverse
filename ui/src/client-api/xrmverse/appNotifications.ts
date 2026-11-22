/** A notification level: 1 success, 2 error, 3 warning, 4 information. */
export type AppNotificationLevel = 1 | 2 | 3 | 4;

export interface AppNotification {
  id: string;
  level: AppNotificationLevel;
  message: string;
  showCloseButton: boolean;
  action?: { actionLabel: string; eventHandler: () => unknown };
}

export interface AppNotificationStore {
  /**
   * Adds a notification; resolves with its id. Rejects with `TypeError` for a missing or wrong `level` or `message`, and for an
   * `action` without a non-empty string `actionLabel` and a function `eventHandler`.
   */
  add(n: unknown): Promise<string>;
  /** Removes a notification; an unknown id resolves (idempotent). */
  clear(id: string): Promise<void>;
  /** The notifications, newest first. The array is replaced on each change, so it can be a snapshot. */
  list(): AppNotification[];
  subscribe(fn: () => void): () => void;
  /** Clears all notifications; later `add` rejects. */
  dispose(): void;
}

/** The app's notification store; one per open app. */
export function createAppNotifications(): AppNotificationStore {
  let items: AppNotification[] = [];
  let disposed = false;
  let seq = 0;
  const listeners = new Set<() => void>();
  const emit = () => listeners.forEach((fn) => fn());

  return {
    async add(n: unknown): Promise<string> {
      if (disposed) throw new Error('The app was closed');
      if (typeof n !== 'object' || n === null) throw new TypeError('The notification must be an object');
      const o = n as Record<string, unknown>;
      if (o.level !== 1 && o.level !== 2 && o.level !== 3 && o.level !== 4) {
        throw new TypeError('The notification level must be 1 (success), 2 (error), 3 (warning) or 4 (information)');
      }
      if (typeof o.message !== 'string' || o.message === '') throw new TypeError('The notification message must be a non-empty string');
      const notification: AppNotification = {
        id: `notification-${++seq}`,
        level: o.level,
        message: o.message,
        showCloseButton: o.showCloseButton === true,
      };
      if (o.action !== undefined && o.action !== null) {
        // An action that would draw no button fails explicitly rather than being dropped.
        const a = (typeof o.action === 'object' ? o.action : {}) as Record<string, unknown>;
        if (typeof a.actionLabel !== 'string' || a.actionLabel === '' || typeof a.eventHandler !== 'function') {
          throw new TypeError('The notification action must have a non-empty string actionLabel and a function eventHandler');
        }
        notification.action = { actionLabel: a.actionLabel, eventHandler: a.eventHandler as () => unknown };
      }
      items = [notification, ...items];
      emit();
      return notification.id;
    },
    async clear(id: string): Promise<void> {
      if (!items.some((x) => x.id === id)) return;
      items = items.filter((x) => x.id !== id);
      emit();
    },
    list: () => items,
    subscribe(fn) {
      listeners.add(fn);
      return () => {
        listeners.delete(fn);
      };
    },
    dispose() {
      disposed = true;
      if (items.length === 0) return;
      items = [];
      emit();
    },
  };
}
