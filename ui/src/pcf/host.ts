import { Component, type ErrorInfo, type ReactNode } from 'react';
import { Stage, stub } from '../client-api/notImplemented';
import { createWebApi } from '../client-api/webApi';
import type { XrmHost } from '../client-api/xrm';
import type { XrmApi } from '../shell/api/client';
import { t } from '../shell/i18n/t';
import type { PcfContext, PcfContextInput } from './context';
import type { PcfConstructor, PcfControl } from './loader';
import type { PcfManifest } from './manifest';

/**
 * What the two PCF hosts share (field controls, UI-1d; dataset controls, UI-1e): the user's privileges, `userSettings`, the
 * services of the context (`webAPI`, `navigation`, `utils`), the session's control states, the container resize tracking,
 * the init/updateView/destroy lifecycle and the error boundary of a virtual control's element.
 */

/** The user's privileges by name (lower case) with their depth, for `utils.hasEntityPrivilege`. */
export type Privileges = { held: Map<string, number> } | { error: string } | undefined;

const DEPTHS: Record<string, number> = { basic: 0, local: 1, deep: 2, global: 3 };
/** Learn, `PrivilegeType`. */
const PRIVILEGE_NAMES = ['', 'Create', 'Read', 'Write', 'Delete', 'Assign', 'Share', 'Append', 'AppendTo'];

const privilegeCache = new Map<string, Promise<Privileges>>();
export function loadPrivileges(api: XrmApi, userId: string): Promise<Privileges> {
  const id = userId.replace(/[{}]/g, '').toLowerCase();
  let p = privilegeCache.get(id);
  if (!p) {
    p = api
      .get<{ RolePrivileges: { PrivilegeName: string; Depth: string | number }[] }>(`systemusers(${id})/Microsoft.Dynamics.CRM.RetrieveUserPrivileges()`)
      .then(
        (r) => {
          const held = new Map<string, number>();
          for (const x of r.RolePrivileges) {
            const depth = typeof x.Depth === 'number' ? x.Depth : (DEPTHS[String(x.Depth).toLowerCase()] ?? 0);
            const name = x.PrivilegeName.toLowerCase();
            held.set(name, Math.max(depth, held.get(name) ?? -1));
          }
          return { held };
        },
        (e: unknown) => {
          privilegeCache.delete(id);
          return { error: e instanceof Error ? e.message : String(e) };
        },
      );
    privilegeCache.set(id, p);
  }
  return p;
}

/** The privileges of the signed-in user (the global context's `userId`); undefined outside an app. */
export function userPrivileges(api: XrmApi, host: XrmHost | null): Promise<Privileges> {
  const userId = (host?.globalContext as { userSettings?: { userId?: string } } | undefined)?.userSettings?.userId;
  return userId ? loadPrivileges(api, userId) : Promise.resolve(undefined);
}

/** `mode.setControlState` values, by table and control id, for the session. */
const controlStates = new Map<string, Record<string, unknown>>();

/** Forgets the session caches of the PCF host (tests). */
export function resetPcfHost(): void {
  privilegeCache.clear();
  controlStates.clear();
}

/** `userSettings` of the context: the global context's, with the number formats of the shell language. */
export function userSettingsOf(host: XrmHost | null, lcid: number, locale: string, currencySymbol: string | undefined): Record<string, unknown> {
  const base = (host?.globalContext as { userSettings?: object } | undefined)?.userSettings ?? { languageId: lcid, isRTL: false, securityRoles: [], userId: '', userName: '' };
  const parts = new Intl.NumberFormat(locale).formatToParts(-12345.6);
  const part = (type: string, fallback: string) => parts.find((p) => p.type === type)?.value ?? fallback;
  const numberFormattingInfo = {
    NumberDecimalSeparator: part('decimal', '.'),
    NumberGroupSeparator: part('group', ','),
    NumberDecimalDigits: 2,
    NumberGroupSizes: [3],
    NegativeSign: part('minusSign', '-'),
    PositiveSign: '+',
    CurrencyDecimalSeparator: part('decimal', '.'),
    CurrencyGroupSeparator: part('group', ','),
    CurrencyDecimalDigits: 2,
    CurrencyGroupSizes: [3],
    CurrencySymbol: currencySymbol ?? '',
    PercentDecimalSeparator: part('decimal', '.'),
    PercentGroupSeparator: part('group', ','),
  };
  // The global context's members (some of which throw "not available" when read) stay reachable through the prototype.
  return Object.create(base, { numberFormattingInfo: { value: numberFormattingInfo, enumerable: true } }) as Record<string, unknown>;
}

const navigationStub = (): Record<string, unknown> =>
  Object.fromEntries(
    ['openAlertDialog', 'openConfirmDialog', 'openErrorDialog', 'openFile', 'openForm', 'openUrl', 'openWebResource'].map((m) => [m, stub(`context.navigation.${m} outside an app`, Stage.later)]),
  );

/** The context's `navigation`: the UI-1c `Xrm.Navigation` in an app, else members that throw "not available". */
export const navigationOf = (host: XrmHost | null): unknown => host?.xrm.Navigation ?? navigationStub();

export interface HostInputOptions {
  manifest: PcfManifest;
  controlName: string;
  strings: Record<string, string>;
  locale: string;
  lcid: number;
  currencySymbol?: string;
  timeZone?: string;
  /** The user's date and time patterns (in the apps). */
  dateFormats?: PcfContextInput['dateFormats'];
  host: XrmHost | null;
  api: XrmApi | undefined;
  /** As {@link navigationOf} gives it; built here when absent. */
  navigation?: unknown;
  privileges: Privileges;
  /** The key of the control's `setControlState` value: table and control id. */
  stateKey: string;
  requestRender(): void;
  trackContainerResize(track: boolean): void;
  /** The active theme where the control renders (`useActiveTheme()`), for `fluentDesignLanguage`. */
  theme?: PcfContextInput['theme'];
}

/** What stays for the life of one control instance (`PcfContextInput`). */
export function hostInput(o: HostInputOptions): PcfContextInput {
  const { host, api, privileges } = o;
  return {
    manifest: o.manifest,
    controlName: o.controlName,
    strings: o.strings,
    locale: o.locale,
    currencySymbol: o.currencySymbol,
    timeZone: o.timeZone,
    dateFormats: o.dateFormats,
    userSettings: userSettingsOf(host, o.lcid, o.locale, o.currencySymbol),
    webAPI: host?.xrm.WebApi ?? (api ? createWebApi(api) : undefined),
    navigation: o.navigation ?? navigationOf(host),
    utils: {
      getEntityMetadata: (host?.xrm.Utility.getEntityMetadata as PcfContextInput['utils']['getEntityMetadata']) ?? stub('context.utils.getEntityMetadata outside an app', Stage.later),
      lookupObjects: (host?.xrm.Utility.lookupObjects as PcfContextInput['utils']['lookupObjects']) ?? stub('context.utils.lookupObjects outside an app', Stage.later),
      hasEntityPrivilege: (entityTypeName, privilegeType, privilegeDepth) => {
        if (!privileges) throw new Error('context.utils.hasEntityPrivilege is not available outside an app.');
        if ('error' in privileges) throw new Error(`context.utils.hasEntityPrivilege: the user's privileges could not be read (${privileges.error}).`);
        const action = PRIVILEGE_NAMES[privilegeType];
        if (!action) return false;
        const depth = privileges.held.get(`prv${action}${String(entityTypeName)}`.toLowerCase());
        return depth !== undefined && depth >= (Number.isFinite(privilegeDepth) ? privilegeDepth : 0);
      },
    },
    requestRender: o.requestRender,
    trackContainerResize: o.trackContainerResize,
    setControlState: (value) => controlStates.set(o.stateKey, value),
    ...(o.theme ? { theme: o.theme } : {}),
  };
}

/** `mode.trackContainerResize` on `target()`: `onSize` gets each new size. `dispose` stops it for good. */
export function resizeTracker(target: () => Element | null, onSize: (size: { width: number; height: number }) => void) {
  let observer: ResizeObserver | undefined;
  let disposed = false;
  const disconnect = () => {
    observer?.disconnect();
    observer = undefined;
  };
  return {
    track(track: boolean) {
      if (disposed) return;
      if (!track) {
        disconnect();
        return;
      }
      const element = target();
      if (observer || !element || typeof ResizeObserver === 'undefined') return;
      observer = new ResizeObserver((entries) => {
        const rect = entries[entries.length - 1]?.contentRect;
        if (rect) onSize({ width: Math.round(rect.width), height: Math.round(rect.height) });
      });
      observer.observe(element);
    },
    dispose() {
      disposed = true;
      disconnect();
    },
  };
}

export const reasonOf = (e: unknown): string => (e instanceof Error && e.message ? e.message : String(e));

/** A control that `init` and its first `updateView` accepted. */
export interface PcfRunning {
  instance: PcfControl;
  /** What the first `updateView` returned (a virtual control's element). */
  element: unknown;
  /** Calls `destroy` (once) and removes the container. */
  stop(): void;
}

export interface StartOptions {
  name: string;
  ctor: PcfConstructor;
  virtual: boolean;
  context: PcfContext;
  stateKey: string;
  /** Where a standard control's container goes. */
  parent: HTMLElement | null;
  /** `notifyOutputChanged`, while the control runs. */
  notifyOutputChanged(instance: PcfControl): void;
  /** Called first when the control stops. */
  onStop?(): void;
}

/**
 * `init` once (with the state `setControlState` kept), then the first `updateView`. Throws the reason the form shows when
 * either fails; the control is stopped then.
 */
export function startControl(o: StartOptions): PcfRunning {
  let container: HTMLDivElement | undefined;
  let started = false;
  let stopped = false;
  let instance: PcfControl | undefined;
  const stop = () => {
    if (stopped) return;
    stopped = true;
    o.onStop?.();
    if (started && instance) {
      try {
        instance.destroy();
      } catch (e) {
        console.warn(`Custom control ${o.name}: destroy failed`, e);
      }
    }
    container?.remove();
  };
  try {
    instance = new o.ctor();
    if (!o.virtual) {
      container = document.createElement('div');
      o.parent?.appendChild(container);
    }
    const self = instance;
    instance.init(o.context, () => !stopped && o.notifyOutputChanged(self), { ...(controlStates.get(o.stateKey) ?? {}) }, container);
    started = true;
  } catch (e) {
    stop();
    throw new Error(t('record.customControl.initFailed', { message: reasonOf(e) }));
  }
  let element: unknown;
  const running = instance!;
  try {
    element = running.updateView(o.context);
  } catch (e) {
    stop();
    throw new Error(t('record.customControl.updateFailed', { message: reasonOf(e) }));
  }
  return { instance: running, element, stop };
}

/** Catches a render failure of a virtual control's element: the standard control takes its place. */
export class PcfBoundary extends Component<{ onError: (e: unknown) => void; children: ReactNode }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() {
    return { failed: true };
  }
  componentDidCatch(error: unknown, _info: ErrorInfo) {
    this.props.onError(error);
  }
  render() {
    return this.state.failed ? null : this.props.children;
  }
}
