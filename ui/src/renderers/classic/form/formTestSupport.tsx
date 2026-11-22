import { NotificationsProvider } from '../../../shell/notify/Notifications';
import { render } from '@testing-library/react';
import { HttpResponse } from 'msw';
import type { ReactNode } from 'react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { onPath } from '../../../admin/security/testSupport';
import type { AttributeMeta } from '../../../admin/tables/metadata';
import { entity } from '../../../admin/tables/testAttrs';
import type { AccessRight } from '../../../apps/record/access';
import type { RecordContext } from '../../../apps/record/loadRecord';
import { useRecordForm } from '../../../apps/record/useRecordForm';
import { mockApps, type AppsMock } from '../../../apps/testSupport';
import type { Cell, Control, FormModel, Labels, Section, Tab } from '../../../model/form';
import { AuthProvider, useAuth } from '../../../shell/auth/AuthProvider';
import { server } from '../../../test/server';
import { FormView, type FormViewProps } from './FormView';

export const RECORD_ID = 'cccccccc-0000-0000-0000-000000000003';

export function field(attribute: string, over: Partial<Cell> = {}, control: Partial<Extract<Control, { kind: 'field' }>> = {}): Cell {
  return {
    id: `cell-${attribute}`,
    labels: { 1033: `${attribute} label` },
    showLabel: true,
    visible: true,
    colSpan: 1,
    rowSpan: 1,
    control: { kind: 'field', id: attribute, attribute, disabled: false, ...control },
    ...over,
  };
}

export function section(rows: Cell[][], over: Partial<Section> = {}): Section {
  return { name: 's', labels: {}, showLabel: true, visible: true, columns: 1, labelPosition: 'left', rows, ...over };
}

export function tab(name: string, columns: { width: string; sections: Section[] }[], over: Partial<Tab> = {}): Tab {
  return { name, labels: { 1033: `${name} tab` }, visible: true, columns, ...over };
}

/** One tab with one section holding `cells`, one per row. */
export function simpleForm(cells: Cell[], over: Partial<FormModel> = {}): FormModel {
  return formOf([tab('main', [{ width: '100%', sections: [section(cells.map((c) => [c]))] }])], over);
}

export function formOf(tabs: Tab[], over: Partial<FormModel> = {}): FormModel {
  return { id: 'f1', name: 'Main', entity: 'xv_project', type: 2, isDefault: true, order: 0, roles: [], tabs, header: [], footer: [], hasScripts: false, libraries: [], events: [], ...over };
}

export const labelsOf = (en: string, it?: string): Labels => {
  const labels: Labels = { 1033: en };
  if (it) labels[1040] = it;
  return labels;
};

export function contextOf(
  attributes: AttributeMeta[],
  data: Record<string, unknown> | undefined,
  over: { rights?: AccessRight[]; lookups?: RecordContext['fieldContext']['lookups']; displayName?: string } = {},
): RecordContext {
  return {
    entity: entity(attributes, over.displayName ? { displayName: over.displayName } : {}),
    fieldContext: { table: 'xv_project', lookups: over.lookups ?? [], globalChoices: {}, record: data ? { set: 'xv_projects', id: RECORD_ID, data } : undefined },
    record: data,
    rights: data ? (over.rights ?? ['Read', 'Write']) : [],
    canCreate: true,
  };
}

/** Shows the router's location and its `returnTo` and `fromSubgrid` state, for tests of what a click navigates to. */
function LocationProbe() {
  const { pathname, search, state } = useLocation();
  return (
    <>
      <output data-testid="location">{pathname + search}</output>
      <output data-testid="location-return-to">{(state as { returnTo?: string } | null)?.returnTo ?? ''}</output>
      <output data-testid="location-from-subgrid">{JSON.stringify((state as { fromSubgrid?: unknown } | null)?.fromSubgrid ?? null)}</output>
    </>
  );
}

function Gate({ children }: { children: ReactNode }) {
  return useAuth().api ? <>{children}</> : null;
}

export const Providers = ({ children }: { children: ReactNode }) => (
  <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
    <AuthProvider>
      <NotificationsProvider>
        <Gate>{children}</Gate>
        <LocationProbe />
      </NotificationsProvider>
    </AuthProvider>
  </MemoryRouter>
);

type Harness = Omit<FormViewProps, 'state'> & { saveLabel?: string };

/** A form view with its state hook and a Save button (the command bar is Task 7's). */
export function Page({ form, ctx, lcid, ...rest }: Harness) {
  const state = useRecordForm(ctx, form, { lcid });
  return (
    <>
      <FormView form={form} ctx={ctx} lcid={lcid} state={state} {...rest} />
      <button onClick={() => void state.save()}>Save now</button>
    </>
  );
}

export interface Sent {
  method: string;
  path: string;
  body?: unknown;
}

/** Signs in, renders the form with its hook, and records PATCH and POST requests. */
export function renderForm(form: FormModel, ctx: RecordContext, props: { lcid?: number; formView?: Partial<Harness>; patchError?: string; apps?: AppsMock; setup?: () => void } = {}) {
  const requests = mockApps(props.apps);
  const sent: Sent[] = [];
  server.use(
    onPath('get', `xv_projects(${RECORD_ID})`, () => HttpResponse.json(ctx.record ?? {})),
    onPath('patch', `xv_projects(${RECORD_ID})`, async ({ request }) => {
      sent.push({ method: 'PATCH', path: 'xv_projects', body: await request.json() });
      return props.patchError
        ? HttpResponse.json({ error: { code: '0x80040333', message: props.patchError } }, { status: 400 })
        : new HttpResponse(null, { status: 204 });
    }),
  );
  props.setup?.();
  const view = render(
    <Providers>
      <Page form={form} ctx={ctx} lcid={props.lcid ?? 1033} onSelectForm={() => {}} {...props.formView} />
    </Providers>,
  );
  return { ...view, sent, requests };
}
