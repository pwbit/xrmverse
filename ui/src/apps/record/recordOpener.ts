import { createContext, useContext, useMemo } from 'react';
import { UNSAFE_NavigationContext } from 'react-router-dom';
import type { DialogHost, SavedRecord } from '../../client-api/DialogHost';
import { mainAspxUrl } from '../mainAspx';

/**
 * How a form opens a related record (a lookup's value, a subgrid's row or New): `sidepane`, xrmverse's default, opens its main
 * form in a side dialog two thirds as wide as the form; `page` navigates to it, as Dataverse does. The app setting
 * `xrmverse_LookupOpenMode` (app value, else environment value, else the definition's default) chooses.
 */
export type RecordOpenMode = 'sidepane' | 'page';

/** Reads the setting's text; an unknown value is the default, with a console warning. */
export function parseRecordOpenMode(value: string | undefined): RecordOpenMode {
  const v = value?.trim().toLowerCase();
  if (!v || v === 'sidepane') return 'sidepane';
  if (v === 'page') return 'page';
  console.warn(`xrmverse_LookupOpenMode: "${value}" is not sidepane or page; sidepane is used.`);
  return 'sidepane';
}

export interface OpenRecordTarget {
  etn: string;
  /** Omitted for a create form. */
  id?: string;
  extraqs?: Record<string, string>;
  /** The history state of the form (a subgrid's New). */
  state?: unknown;
  /** Where a page navigation comes back to (Save & Close); a side pane closes instead. */
  returnTo?: string;
}

export interface OpenRecordOptions {
  /** The click: Ctrl or Cmd held opens the full page, as in Dataverse. */
  event?: { ctrlKey?: boolean; metaKey?: boolean };
  /** The element clicked: the side pane is two thirds as wide as the form around it. */
  anchor?: Element | null;
}

/** Opens a related record; resolves when its side pane closes (with the record saved last) or at once after a page navigation. */
export type OpenRecord = (target: OpenRecordTarget, options?: OpenRecordOptions) => Promise<SavedRecord | undefined>;

/** What the app shell provides; absent outside a model-driven app (the web admin), where related records are not opened. */
export interface RecordOpenerEnv {
  appId: string;
  mode: RecordOpenMode;
  dialogs: DialogHost;
}

export const RecordOpenerContext = createContext<RecordOpenerEnv | null>(null);

/**
 * The opener of the app around the component, or null outside an app. A full page opens through the nearest router, so a form
 * inside a side pane navigates the pane's own history, not the page under it.
 */
export function useRecordOpener(): OpenRecord | null {
  const env = useContext(RecordOpenerContext);
  const navigator = useContext(UNSAFE_NavigationContext)?.navigator;
  return useMemo(
    () => (env && navigator ? createRecordOpener({ ...env, navigate: (url, state) => navigator.push(url, state) }) : null),
    [env, navigator],
  );
}

/** The side pane's width: two thirds of the form around `anchor`, else two thirds of the window. */
export function sidePaneWidth(anchor: Element | null | undefined): { value: number; unit: 'px' | '%' } {
  const form = anchor?.closest('[data-xrmverse-form]');
  const width = form?.getBoundingClientRect().width ?? 0;
  return width > 0 ? { value: Math.round((width * 2) / 3), unit: 'px' } : { value: 66, unit: '%' };
}

/** The opener of an app: `navigate` for a full page, the dialog host's record dialog (position 2) for a side pane. */
export function createRecordOpener(env: RecordOpenerEnv & {
  navigate: (url: string, state?: unknown) => void;
}): OpenRecord {
  return async (target, options) => {
    const url = mainAspxUrl({ appId: env.appId, pageType: 'entityrecord', etn: target.etn, id: target.id, extraqs: target.extraqs });
    const page = env.mode === 'page' || !!options?.event?.ctrlKey || !!options?.event?.metaKey;
    if (page) {
      const state = target.returnTo === undefined ? target.state : { ...((target.state as object | undefined) ?? {}), returnTo: target.returnTo };
      env.navigate(url, state);
      return undefined;
    }
    return env.dialogs.record({ url, position: 2, width: sidePaneWidth(options?.anchor), state: target.state });
  };
}
