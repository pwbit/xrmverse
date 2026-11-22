import { useSyncExternalStore } from 'react';
import type { OptionMeta } from '../admin/tables/metadata';
import type { ControlNotificationLevel, FormNotificationLevel, RequiredLevel, SubmitMode } from './constants';

/**
 * What a script changed on a tab, a section, a control or a form selector item. An absent property means "as the FormXml
 * says". `reload` counts the refreshes a script asked for (subgrids); `focus` counts the focus requests.
 */
export interface ElementState {
  visible?: boolean;
  disabled?: boolean;
  label?: string;
  /** The options a script left on a choice control (`addOption`, `removeOption`, `clearOptions`). */
  options?: OptionMeta[];
  /** The tables a lookup control offers (`setEntityTypes`). */
  entityTypes?: string[];
  reload?: number;
  focus?: number;
  /** The address a script gave an IFRAME or web resource control (`setSrc`). */
  src?: string;
  /** Set by the form, not by scripts: a PCF control renders this field control now (UI-1d, ruling R10). */
  customControl?: boolean;
}

export interface AttributeState {
  requiredLevel?: RequiredLevel;
  submitMode?: SubmitMode;
}

export interface ControlNotification {
  uniqueId: string;
  message: string;
  level: ControlNotificationLevel;
}

export interface ScriptFormNotification {
  uniqueId: string;
  message: string;
  level: FormNotificationLevel;
}

/** Runs the PreSearch handlers of a lookup control and returns the FetchXML filters they added for `target`. */
export type PreSearch = (target: string) => string[];

/**
 * The UI state that form scripts change, over the FormXml defaults: per tab, section and control (keyed by the form model's
 * own objects), per attribute (by logical name), the control and form notifications, the selected tab, the pre-search
 * filters of the lookups and the table name shown in the title. The form renders from it first. A small store: every change
 * notifies the subscribers.
 */
export interface UiOverlay {
  subscribe(listener: () => void): () => void;
  /** Changes with every change; the snapshot of `useSyncExternalStore`. */
  version(): number;
  element(key: object): ElementState;
  setElement(key: object, patch: ElementState): void;
  attribute(name: string): AttributeState;
  setAttribute(name: string, patch: AttributeState): void;
  controlNotifications(key: object): ControlNotification[];
  /** Adds or replaces (by `uniqueId`) a notification of the control. */
  addControlNotification(key: object, notification: ControlNotification): void;
  /** Removes the notification `uniqueId`, or all of them; true when one was removed. */
  clearControlNotification(key: object, uniqueId?: string): boolean;
  /** The messages of the control `ERROR` notifications: while there are any, the form does not save. */
  blockingMessages(): string[];
  formNotifications(): ScriptFormNotification[];
  setFormNotification(notification: ScriptFormNotification): void;
  clearFormNotification(uniqueId: string): boolean;
  selectedTab(): object | undefined;
  selectTab(tab: object): void;
  preSearch(key: object): PreSearch | undefined;
  setPreSearch(key: object, preSearch: PreSearch | undefined): void;
  /** The IFRAME element a web resource or IFRAME control renders (not part of the change notifications). */
  frame(key: object): HTMLIFrameElement | undefined;
  setFrame(key: object, frame: HTMLIFrameElement | null): void;
  /** Calls `listener` whenever a frame is shown for the control (a first one, or the one that replaced a reloaded one). */
  onFrame(key: object, listener: (frame: HTMLIFrameElement) => void): () => void;
  formEntityName(): string | undefined;
  setFormEntityName(name: string): void;
}

export function createUiOverlay(): UiOverlay {
  const listeners = new Set<() => void>();
  let version = 0;
  const elements = new Map<object, ElementState>();
  const attributes = new Map<string, AttributeState>();
  const controlNotes = new Map<object, ControlNotification[]>();
  let formNotes: ScriptFormNotification[] = [];
  let selected: object | undefined;
  const preSearches = new Map<object, PreSearch>();
  const frames = new Map<object, HTMLIFrameElement>();
  const frameListeners = new Map<object, Set<(frame: HTMLIFrameElement) => void>>();
  let entityName: string | undefined;
  const changed = () => {
    version++;
    for (const l of [...listeners]) l();
  };
  const EMPTY = Object.freeze({});
  return {
    subscribe(listener) {
      listeners.add(listener);
      return () => void listeners.delete(listener);
    },
    version: () => version,
    element: (key) => elements.get(key) ?? EMPTY,
    setElement(key, patch) {
      elements.set(key, { ...elements.get(key), ...patch });
      changed();
    },
    attribute: (name) => attributes.get(name) ?? EMPTY,
    setAttribute(name, patch) {
      attributes.set(name, { ...attributes.get(name), ...patch });
      changed();
    },
    controlNotifications: (key) => controlNotes.get(key) ?? [],
    addControlNotification(key, notification) {
      const rest = (controlNotes.get(key) ?? []).filter((n) => n.uniqueId !== notification.uniqueId);
      controlNotes.set(key, [...rest, notification]);
      changed();
    },
    clearControlNotification(key, uniqueId) {
      const all = controlNotes.get(key) ?? [];
      const rest = uniqueId === undefined ? [] : all.filter((n) => n.uniqueId !== uniqueId);
      if (rest.length === all.length) return false;
      controlNotes.set(key, rest);
      changed();
      return true;
    },
    blockingMessages: () => [...controlNotes.values()].flat().filter((n) => n.level === 'ERROR').map((n) => n.message),
    formNotifications: () => formNotes,
    setFormNotification(notification) {
      const at = formNotes.findIndex((n) => n.uniqueId === notification.uniqueId);
      formNotes = at < 0 ? [...formNotes, notification] : formNotes.map((n, i) => (i === at ? notification : n));
      changed();
    },
    clearFormNotification(uniqueId) {
      if (!formNotes.some((n) => n.uniqueId === uniqueId)) return false;
      formNotes = formNotes.filter((n) => n.uniqueId !== uniqueId);
      changed();
      return true;
    },
    selectedTab: () => selected,
    selectTab(tab) {
      if (selected === tab) return;
      selected = tab;
      changed();
    },
    preSearch: (key) => preSearches.get(key),
    setPreSearch(key, preSearch) {
      if (preSearch) preSearches.set(key, preSearch);
      else preSearches.delete(key);
      changed();
    },
    frame: (key) => frames.get(key),
    setFrame(key, frame) {
      if (!frame) return void frames.delete(key);
      const changed = frames.get(key) !== frame;
      frames.set(key, frame);
      if (changed) for (const l of [...(frameListeners.get(key) ?? [])]) l(frame);
    },
    onFrame(key, listener) {
      const set = frameListeners.get(key) ?? new Set();
      frameListeners.set(key, set);
      set.add(listener);
      return () => void set.delete(listener);
    },
    formEntityName: () => entityName,
    setFormEntityName(name) {
      entityName = name;
      changed();
    },
  };
}

/** Re-renders the caller whenever the overlay changes. */
export function useUiOverlay(overlay: UiOverlay | undefined): void {
  useSyncExternalStore(
    (l) => overlay?.subscribe(l) ?? (() => undefined),
    () => overlay?.version() ?? 0,
  );
}
