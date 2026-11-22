import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createExecutionContext, type ExecutionContext } from '../../client-api/executionContext';
import { runHandlers, type RunMode } from '../../client-api/events';
import {
  createFormContext,
  disposeFormContext,
  raiseOnChange,
  raiseOnPostSave,
  raiseOnSave,
  type FormContext,
  type FormContextEnv,
} from '../../client-api/formContext';
import { createScriptHost, type ScriptHost } from '../../client-api/scriptHost';
import type { FormModel } from '../../model/form';
import type { FormNotification } from '../../renderers/classic/form/FormNotifications';
import { t } from '../../shell/i18n/t';

/** A form outside an app shell (no `Xrm` to install): the host window gets nothing. */
const installNothing = (): void => undefined;

export interface RunOptions {
  /** The attribute of an OnChange event. */
  attribute?: string;
  /** The OnChange attribute object (the execution context's event source). */
  eventSource?: unknown;
  /** The OnSave event arguments. */
  eventArgs?: unknown;
  /** The run's execution context, shared with the handlers a script added to the same event. */
  makeContext?: () => ExecutionContext;
  /** Whether the run waits for returned promises (OnLoad and OnSave with the app setting on). */
  mode?: RunMode;
}

/** What the page gives the formContext; the script host adds how handlers run and how failures show. */
export type PageEnv = Omit<FormContextEnv, 'runFormXml' | 'notify'>;

export interface FormScriptsOptions {
  /** The page's side of the formContext; without it handlers get no formContext. */
  env?: PageEnv;
  /** Puts `Xrm` on the host window before the libraries load (the app shell's `XrmHost.install`). */
  install?: (win: Window) => void;
  /** Told the formContext when the form mounts (before the libraries load) and `undefined` when it is disposed: `Xrm.Page` follows it. */
  onFormContext?: (fc: FormContext | undefined) => void;
}

export interface FormScripts {
  notifications: FormNotification[];
  /** Runs the OnSave or OnChange handlers of the FormXml; nothing happens until the libraries are loaded. */
  run(event: 'onsave' | 'onchange', options?: RunOptions): Promise<void>;
  /** The form's formContext once its libraries are loaded. */
  formContext?: FormContext;
  /** Raises OnSave with the save mode; resolves true when a handler prevented the save. */
  onSave(mode: number): Promise<boolean>;
  /** Raises PostSave after a save that ran. */
  onPostSave(saved: boolean): void;
  /** Raises OnChange after a user change of `attribute`. */
  onChange(attribute: string): void;
}

/** Notifications that stay true after a Refresh: a library that did not load, a host that did not start. */
const isLoadFailure = (n: FormNotification) => n.key.startsWith('library-') || n.key === 'host-failed';

/**
 * One script host for `form` (the caller keys it by form and record): its libraries load into a fresh window, OnLoad runs once
 * they are in and again whenever `refreshKey` changes, and the host is disposed when the form changes or the page is left.
 * The formContext is created when the form mounts (before the libraries load) and is disposed with the host.
 */
export function useFormScripts(form: FormModel, refreshKey: number, record: unknown, options: FormScriptsOptions = {}): FormScripts {
  const install = options.install ?? installNothing;
  const [notifications, setNotifications] = useState<FormNotification[]>([]);
  const [host, setHost] = useState<ScriptHost>();
  const hostRef = useRef<ScriptHost>();
  const formRef = useRef(form);
  formRef.current = form;
  const envRef = useRef(options.env);
  envRef.current = options.env;
  const onFormContextRef = useRef(options.onFormContext);
  onFormContextRef.current = options.onFormContext;
  const fcRef = useRef<FormContext>();

  const notify = useCallback((n: FormNotification) => {
    setNotifications((all) => (all.some((x) => x.text === n.text) ? all : [...all, n]));
  }, []);

  const run = useCallback(
    (event: 'onload' | 'onsave' | 'onchange', options: RunOptions = {}) => {
      const current = hostRef.current;
      if (!current) return Promise.resolve();
      const handlers = formRef.current.events
        .filter((e) => e.name === event && (event !== 'onchange' || e.attribute === options.attribute))
        .flatMap((e) => e.handlers);
      const formContext = fcRef.current;
      const makeContext =
        options.makeContext ?? ((): ExecutionContext => createExecutionContext({ formContext, eventSource: options.eventSource, eventArgs: options.eventArgs, globalContext: () => envRef.current?.globalContext }));
      return runHandlers(current, handlers, makeContext, notify, options.mode);
    },
    [notify],
  );

  // The formContext reads the page through this object, so that a handler that runs later (a timer, a promise) sees the
  // current state, not the state of the render that created it.
  const live = useMemo((): FormContextEnv => {
    const page = (): PageEnv => {
      if (!envRef.current) throw new Error('The form is not ready for scripts.');
      return envRef.current;
    };
    return {
      get form() {
        return page().form;
      },
      get ctx() {
        return page().ctx;
      },
      get state() {
        return page().state;
      },
      get overlay() {
        return page().overlay;
      },
      get forms() {
        return page().forms;
      },
      get lcid() {
        return page().lcid;
      },
      get globalContext() {
        return envRef.current?.globalContext;
      },
      get asyncHandlers() {
        return envRef.current?.asyncHandlers;
      },
      // A page without custom commands sets none: refreshRibbon() then does nothing.
      onRefreshRibbon: () => envRef.current?.onRefreshRibbon?.(),
      selectForm: (id) => page().selectForm(id),
      refreshControl: (name) => page().refreshControl(name),
      save: (mode, andClose) => page().save(mode, andClose),
      refresh: () => page().refresh(),
      close: () => page().close(),
      runFormXml: (event, attribute, makeContext, mode) => run(event, { attribute, makeContext, mode }),
      notify,
    };
  }, [run, notify]);

  useEffect(() => {
    // The formContext exists from the moment the form mounts, whether or not the form has libraries, while they load, and
    // even if the script host fails: Xrm.Page and the web resources on the form (parent.Xrm.Page) use it at once. The host,
    // once ready, runs the FormXml handlers against this same formContext.
    let fc: FormContext | undefined;
    if (envRef.current) {
      fc = createFormContext(live);
      fcRef.current = fc;
      onFormContextRef.current?.(fc);
    }
    const disposeContext = () => {
      if (fc) {
        disposeFormContext(fc);
        onFormContextRef.current?.(undefined);
      }
      fcRef.current = undefined;
    };
    if (!form.hasScripts) return disposeContext;
    const abort = new AbortController();
    let created: ScriptHost | undefined;
    setNotifications([]);
    createScriptHost(form.libraries, install, undefined, abort.signal).then(
      (h) => {
        if (abort.signal.aborted) return h.dispose();
        created = h;
        hostRef.current = h;
        for (const name of h.failed) {
          notify({ key: `library-${name}`, intent: 'error', text: t('script.libraryFailed', { name }) });
        }
        setHost(h);
      },
      (e: unknown) => {
        // A host that was aborted because the form was left is not an error; any other failure is shown and the form stays usable.
        if (abort.signal.aborted) return;
        notify({ key: 'host-failed', intent: 'error', text: t('script.hostFailed', { message: e instanceof Error ? e.message : String(e) }) });
      },
    );
    return () => {
      abort.abort();
      created?.dispose();
      disposeContext();
      hostRef.current = undefined;
      setHost(undefined);
    };
  }, [form, notify, live]);

  const loadedFor = useRef(refreshKey);
  useEffect(() => {
    if (!host) return;
    // A Refresh drops the script errors of the previous load; a library that did not load stays reported.
    if (loadedFor.current !== refreshKey) setNotifications((all) => all.filter(isLoadFailure));
    loadedFor.current = refreshKey;
    void run('onload', { mode: { wait: !!envRef.current?.asyncHandlers?.onLoad } });
    // OnLoad runs when the host is ready, after a refresh, and when a reloaded record (Assign) replaces the loaded one.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [host, refreshKey, record]);

  const onSave = useCallback((mode: number) => (fcRef.current ? raiseOnSave(fcRef.current, mode) : Promise.resolve(false)), []);
  const onPostSave = useCallback((saved: boolean) => void (fcRef.current && raiseOnPostSave(fcRef.current, saved)), []);
  const onChange = useCallback((attribute: string) => void (fcRef.current && raiseOnChange(fcRef.current, attribute)), []);

  return { notifications, run: run as FormScripts['run'], formContext: host ? fcRef.current : undefined, onSave, onPostSave, onChange };
}
