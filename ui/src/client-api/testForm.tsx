import { render } from '@testing-library/react';
import { HttpResponse } from 'msw';
import { useMemo, useRef } from 'react';
import { onPath } from '../admin/security/testSupport';
import { DisplayZoneContext } from '../apps/displayZone';
import type { RecordContext } from '../apps/record/loadRecord';
import { useRecordForm } from '../apps/record/useRecordForm';
import { mockApps, type AppsMock } from '../apps/testSupport';
import type { FormModel } from '../model/form';
import type { FormNotification } from '../renderers/classic/form/FormNotifications';
import { FormView } from '../renderers/classic/form/FormView';
import { Providers, RECORD_ID } from '../renderers/classic/form/formTestSupport';
import { NotificationsProvider } from '../shell/notify/Notifications';
import { server } from '../test/server';
import { controlCells } from './control';
import type { RunMode } from './events';
import { createFormContext, raiseOnChange, raiseOnSave, type AsyncHandlers, type FormContext, type FormContextEnv } from './formContext';
import type { ExecutionContext } from './executionContext';
import { createUiOverlay, useUiOverlay } from './uiOverlay';
import { XrmHostContext, type XrmHost } from './xrm';

export interface Mounted {
  /** The formContext of the mounted form. */
  fc(): FormContext;
  /** PATCH and POST bodies sent. */
  sent: { method: string; body: unknown }[];
  /** Script failures reported. */
  notes: FormNotification[];
  /** The FormXml events the formContext asked to run: `onchange:{attribute}` or `onsave`. */
  formXml: string[];
  /** Forms opened through the form selector. */
  selected: string[];
  /** Record reads (a refresh reads it again). */
  reads(): number;
}

export interface MountOptions {
  forms?: FormModel[];
  apps?: AppsMock;
  /** Stands for the FormXml handlers of an event. */
  runFormXml?: (event: string, attribute: string | undefined, makeContext: () => ExecutionContext, mode?: RunMode) => Promise<void> | void;
  setup?: () => void;
  /** The app settings AsyncOnLoad and AsyncOnSave. */
  asyncHandlers?: AsyncHandlers;
  /** The user's time zone (IANA), as the apps provide it (#131); none: UTC. */
  timeZone?: string;
  /** Stands for the command bar's rule evaluation (`formContext.ui.refreshRibbon`). */
  onRefreshRibbon?: () => void;
  /** The app's Xrm host (`XrmHostContext`); none: the form is shown outside an app. */
  xrmHost?: XrmHost;
}

/**
 * Renders a form with its state, the UI overlay and a formContext over them, as the record page wires them (without the
 * script host): a user change raises OnChange, "Save now" raises OnSave and saves.
 */
export function mountForm(form: FormModel, ctx: RecordContext, options: MountOptions = {}): Mounted {
  mockApps(options.apps);
  let reads = 0;
  const out: Mounted = { fc: () => current!, sent: [], notes: [], formXml: [], selected: [], reads: () => reads };
  let current: FormContext | undefined;
  server.use(
    onPath('get', `xv_projects(${RECORD_ID})`, () => {
      reads++;
      return HttpResponse.json(ctx.record ?? {});
    }),
    onPath('patch', `xv_projects(${RECORD_ID})`, async ({ request }) => {
      out.sent.push({ method: 'PATCH', body: await request.json() });
      return new HttpResponse(null, { status: 204 });
    }),
    onPath('post', 'xv_projects', async ({ request }) => {
      out.sent.push({ method: 'POST', body: await request.json() });
      return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://localhost/api/data/v9.2/xv_projects(${RECORD_ID})` } });
    }),
  );
  options.setup?.();

  function Harness() {
    const overlay = useMemo(createUiOverlay, []);
    useUiOverlay(overlay);
    const fcRef = useRef<FormContext>();
    const state = useRecordForm(ctx, form, { lcid: 1033, overlay, onUserChange: (a) => fcRef.current && raiseOnChange(fcRef.current, a) });
    const cells = useMemo(() => controlCells(form), []);
    const envRef = useRef<FormContextEnv>();
    envRef.current = {
      form,
      ctx,
      state,
      overlay,
      forms: options.forms ?? [form],
      lcid: 1033,
      timeZone: options.timeZone,
      selectForm: (id) => out.selected.push(id),
      refreshControl: (name) => {
        const cell = cells.get(name);
        if (cell) overlay.setElement(cell, { reload: (overlay.element(cell).reload ?? 0) + 1 });
      },
      save: async (mode) => {
        if (await raiseOnSave(fcRef.current!, mode)) return 'prevented';
        return (await envRef.current!.state.save()) ? 'saved' : 'failed';
      },
      refresh: () => envRef.current!.state.refresh(),
      close: () => undefined,
      runFormXml: (event, attribute, makeContext, mode) => {
        out.formXml.push(attribute ? `${event}:${attribute}` : event);
        return options.runFormXml?.(event, attribute, makeContext, mode);
      },
      asyncHandlers: options.asyncHandlers,
      notify: (n) => out.notes.push(n),
      onRefreshRibbon: options.onRefreshRibbon,
    };
    if (!fcRef.current) {
      // The same live view of the page the script host gives a formContext.
      const live = new Proxy({} as FormContextEnv, { get: (_, key) => envRef.current![key as keyof FormContextEnv] });
      fcRef.current = createFormContext(live);
      current = fcRef.current;
    }
    return (
      <>
        <FormView form={form} ctx={ctx} lcid={1033} state={state} overlay={overlay} forms={options.forms} onSelectForm={() => undefined} />
        <button onClick={() => void envRef.current!.save(1, false)}>Save now</button>
      </>
    );
  }

  render(
    <Providers>
      <NotificationsProvider>
        <DisplayZoneContext.Provider value={options.timeZone}>
          <XrmHostContext.Provider value={options.xrmHost ?? null}>
            <Harness />
          </XrmHostContext.Provider>
        </DisplayZoneContext.Provider>
      </NotificationsProvider>
    </Providers>,
  );
  return out;
}
