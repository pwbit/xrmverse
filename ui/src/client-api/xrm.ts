import { createContext } from 'react';
import type { AppModel } from '../model/app';
import type { XrmApi } from '../shell/api/client';
import type { DialogHost } from './DialogHost';
import type { FormContext } from './formContext';
import { createGlobalContext, type CurrentUser, type OrgSettings } from './globalContext';
import { createNavigation } from './navigation';
import { stubMembers } from './notImplemented';
import { createUtility } from './utility';
import { createWebApi } from './webApi';

/** What the `Xrm` object of one app shell works over. */
export interface XrmEnv {
  api: XrmApi;
  app: AppModel;
  user: CurrentUser;
  org: OrgSettings;
  /** Moves the app shell to a `/main.aspx` address. */
  navigate: (url: string) => void;
  dialogs: DialogHost;
  /** The formContext of the form open in the shell, if any. */
  currentForm: () => FormContext | null;
}

/** The documented and supported `Xrm` surface of UI-1c (Global Constraints, "Xrm surface"). */
export type Xrm = {
  WebApi: Record<string, unknown>;
  Navigation: Record<string, unknown>;
  Utility: Record<string, unknown>;
  /** The current form's formContext, plus `context` (the global context). */
  readonly Page: FormContext;
};

/**
 * `Xrm` for one app shell. Documented members UI-1c leaves out throw "not available" with their path and stage
 * (`XRM_NOT_IMPLEMENTED`); undocumented names (`Xrm.Internal`, `Mscrm.*`) are simply absent.
 */
export function createXrm(env: XrmEnv): Xrm {
  const globalContext = createGlobalContext(env.app, env.user, env.org);
  // Xrm.Page is the formContext itself with `context` added, without adding `context` to the formContext a handler gets.
  const pages = new WeakMap<FormContext, FormContext>();
  const pageOf = (fc: FormContext): FormContext => {
    let page = pages.get(fc);
    if (!page) {
      page = new Proxy(fc, {
        get: (target, prop, receiver) => (prop === 'context' ? globalContext : Reflect.get(target, prop, receiver)),
        has: (target, prop) => prop === 'context' || Reflect.has(target, prop),
      });
      pages.set(fc, page);
    }
    return page;
  };
  const xrm = {
    WebApi: createWebApi(env.api),
    Navigation: createNavigation({ appId: env.app.id, navigate: env.navigate, dialogs: env.dialogs }),
    Utility: createUtility({ api: env.api, dialogs: env.dialogs, globalContext, lcid: env.user.lcid }),
  } as unknown as Xrm;
  Object.defineProperty(xrm, 'Page', {
    enumerable: true,
    get: () => {
      const fc = env.currentForm();
      if (!fc) throw new Error('Xrm.Page is available only while a form is open.');
      return pageOf(fc);
    },
  });
  stubMembers(xrm, 'Xrm');
  return xrm;
}

/** What the app shell hands its pages: the `Xrm` to install in each script host, and where the open form registers. */
export interface XrmHost {
  xrm: Xrm;
  /** `Xrm.Utility.getGlobalContext()`, which `executionContext.getContext()` returns too. */
  globalContext: unknown;
  /** The organization settings the pages need, such as auto-save (#117). */
  org: OrgSettings;
  /** Puts `Xrm` and `XrmVerse` on a script host window before its libraries load. */
  install(win: Window): void;
  /** The form whose formContext `Xrm.Page` is; `null` when it closes. */
  setCurrentForm(fc: FormContext | null): void;
  currentForm(): FormContext | null;
}

/** Provided by the app shell while an app is open. */
export const XrmHostContext = createContext<XrmHost | null>(null);
