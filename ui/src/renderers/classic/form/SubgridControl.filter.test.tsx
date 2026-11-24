import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { FormValues, LookupValue } from '../../../admin/tables/formValues';
import { rawOneToMany } from '../../../admin/tables/tableTestSupport';
import { attr } from '../../../admin/tables/testAttrs';
import { RecordOpenerContext } from '../../../apps/record/recordOpener';
import type { SubgridFilter } from '../../../apps/record/subgridFilters';
import type { RecordForm } from '../../../apps/record/useRecordForm';
import { mockApps, type AppsMock, type MockView } from '../../../apps/testSupport';
import type { DialogHost } from '../../../client-api/DialogHost';
import type { SubgridControl as SubgridModel } from '../../../model/form';
import { server } from '../../../test/server';
import type { ControlEnv } from './controlEnv';
import { contextOf, Providers, RECORD_ID } from './formTestSupport';
import { SubgridControl } from './SubgridControl';

const APP_ID = '11111111-0000-0000-0000-000000000001';
const FORM_ID = 'f0000000-0000-0000-0000-0000000000f1';
const VIEW_ID = 'a0000000-0000-0000-0000-0000000000d1';
const ACCOUNT_1 = 'acc00000-0000-0000-0000-000000000001';
const ACCOUNT_2 = 'acc00000-0000-0000-0000-000000000002';

const name = attr('xv_name', { isPrimaryName: true, displayName: 'Name' });
const customer = attr('xv_customerid', { kind: 'lookup', typeName: 'Lookup', displayName: 'Customer', targets: ['account', 'contact'] });
const RECORD = { xv_projectid: RECORD_ID, xv_name: 'Alpha' };

const TASK_VIEW: MockView = {
  id: VIEW_ID,
  name: 'Active Tasks',
  entity: 'xv_task',
  isdefault: true,
  fetchxml: `<fetch version="1.0" mapping="logical"><entity name="xv_task"><attribute name="xv_title"/><attribute name="xv_taskid"/><filter type="or"><condition attribute="statecode" operator="eq" value="0"/></filter></entity></fetch>`,
  layoutxml: `<grid name="resultset" jump="xv_title" select="1" icon="1" preview="1"><row name="result" id="xv_taskid"><cell name="xv_title" width="200"/></row></grid>`,
};
const TASKS: AppsMock = {
  savedQueries: [TASK_VIEW],
  tables: { xv_task: { entitySetName: 'xv_tasks', primaryId: 'xv_taskid', primaryName: 'xv_title', displayName: 'Task', labels: { xv_title: { 1033: 'Title' } } } },
  privileges: ['prvCreatexv_Task', 'prvAppendxv_Task', 'prvAppendToxv_Project'],
  schemaNames: { xv_task: 'xv_Task', xv_project: 'xv_Project' },
};

/** The attributes of xv_task as `loadEntity` reads them: the account lookup, a text column and a choice. */
const TASK_ATTRIBUTES = [
  { LogicalName: 'xv_taskid', AttributeType: 'Uniqueidentifier', IsPrimaryId: true },
  { LogicalName: 'xv_title', AttributeType: 'String', IsPrimaryName: true },
  { LogicalName: 'xv_accountid', '@odata.type': '#Microsoft.Dynamics.CRM.LookupAttributeMetadata', AttributeType: 'Lookup', Targets: ['account'] },
  { LogicalName: 'xv_projectid', '@odata.type': '#Microsoft.Dynamics.CRM.LookupAttributeMetadata', AttributeType: 'Lookup', Targets: ['xv_project'] },
];

const control = (over: Partial<SubgridModel> = {}): SubgridModel => ({
  kind: 'subgrid',
  id: ' Tasks ',
  relationship: '',
  targetEntity: 'xv_task',
  viewId: VIEW_ID,
  recordsPerPage: 4,
  enableViewPicker: false,
  ...over,
});

const account = (id = ACCOUNT_1, target = 'account'): LookupValue => ({ id, target, name: 'Contoso' });

interface Options {
  control?: SubgridModel;
  filter?: SubgridFilter;
  /** The form the entries belong to; the form shown by default. */
  filterFormId?: string;
  /** No record: a create form. */
  creating?: boolean;
  /** The app's filters are still being read, or one of them could not be read: the form has no entries yet. */
  status?: 'pending' | 'failed';
}

/** The subgrid alone, with the form's values and a side-pane opener; returns the row fetches, the paths read and the opener. */
function mount(values: FormValues, o: Options = {}) {
  const fetches: Element[] = [];
  const requests = mockApps({
    ...TASKS,
    records: (_set, fetch) => {
      fetches.push(fetch);
      return { value: [{ xv_taskid: 't1', xv_title: 'Write docs' }] };
    },
  });
  server.use(
    http.get('*/api/data/v9.2/RelationshipDefinitions', () =>
      HttpResponse.json({ value: [rawOneToMany('xv_project_task', 'xv_task', 'xv_projectid', 'xv_project', 'xv_projectid')] }),
    ),
    http.get('*/api/data/v9.2/*', ({ request }) => {
      const url = new URL(request.url);
      const path = decodeURIComponent(url.pathname.replace(/^.*\/api\/data\/v9\.2\//, ''));
      if (path === "EntityDefinitions(LogicalName='xv_task')" && url.searchParams.get('$expand') === 'Attributes') {
        return HttpResponse.json({ LogicalName: 'xv_task', SchemaName: 'xv_Task', EntitySetName: 'xv_tasks', PrimaryIdAttribute: 'xv_taskid', Attributes: TASK_ATTRIBUTES });
      }
      return undefined;
    }),
  );
  const record = vi.fn(async () => undefined);
  const base = contextOf([name, customer], o.creating ? undefined : RECORD);
  const ctx = { ...base, entity: { ...base.entity, schemaName: 'xv_Project' } };
  const entries: Record<string, SubgridFilter> = Object.create(null);
  entries.tasks = o.filter ?? { source: 'xv_customerid', target: 'xv_accountid' };
  const cache = new Map<string, Promise<unknown>>();
  const envOf = (v: FormValues): ControlEnv => ({
    ctx,
    state: { values: v, record: o.creating ? undefined : RECORD } as unknown as RecordForm,
    lcid: 1033,
    formId: `{${FORM_ID.toUpperCase()}}`,
    appId: APP_ID,
    ...(o.status ? { subgridFiltersStatus: o.status } : { subgridFilters: { formId: o.filterFormId ?? FORM_ID, entries } }),
    cache,
  });
  const ui = (v: FormValues) => (
    <Providers>
      <RecordOpenerContext.Provider value={{ appId: APP_ID, mode: 'sidepane', dialogs: { record } as unknown as DialogHost }}>
        <SubgridControl control={o.control ?? control()} env={envOf(v)} title="Tasks" />
      </RecordOpenerContext.Provider>
    </Providers>
  );
  const view = render(ui(values));
  return { fetches, requests, record, rerender: (v: FormValues) => view.rerender(ui(v)) };
}

const commandsMenu = () => screen.findByRole('button', { name: 'Task commands' });
const conditionOn = (fetch: Element, attribute: string) => fetch.querySelector(`condition[attribute="${attribute}"]`)?.getAttribute('value');
/** The rows are requested only once the metadata is read: wait for the reads to settle before saying none was made. */
const settle = () => new Promise((resolve) => setTimeout(resolve, 50));

afterEach(() => vi.restoreAllMocks());

describe('Subgrid filtered by a form lookup (X.11)', () => {
  it('lists only rows matching the source', async () => {
    const { fetches } = mount({ xv_customerid: account() });
    expect(await screen.findByRole('grid', { name: 'Active Tasks' })).toBeInTheDocument();
    expect(await screen.findByText('Write docs')).toBeInTheDocument();
    expect(fetches).toHaveLength(1);
    const fetch = fetches[0]!;
    expect(conditionOn(fetch, 'xv_accountid')).toBe(ACCOUNT_1);
    expect(fetch.querySelector('condition[attribute="xv_accountid"]')!.getAttribute('operator')).toBe('eq');
    // The view's own `or` filter is kept whole, ANDed with the lookup condition.
    expect(fetch.querySelector('entity > filter[type="and"] > filter[type="or"] > condition[attribute="statecode"]')).not.toBeNull();
    expect(fetch.getAttribute('count')).toBe('4');
    // No relationship: nothing limits the rows to the form's record.
    expect(conditionOn(fetch, 'xv_projectid')).toBeUndefined();
  });

  it('reloads when the source value changes', async () => {
    const { fetches, rerender } = mount({ xv_customerid: account() });
    await screen.findByText('Write docs');
    expect(fetches).toHaveLength(1);
    rerender({ xv_customerid: account(ACCOUNT_2) });
    await waitFor(() => expect(fetches).toHaveLength(2));
    expect(conditionOn(fetches[1]!, 'xv_accountid')).toBe(ACCOUNT_2);
  });

  it('empty source shows the fill notice', async () => {
    const { fetches, rerender } = mount({});
    expect(await screen.findByText('Fill Customer to see the records.')).toBeInTheDocument();
    await userEvent.click(await commandsMenu());
    expect(await screen.findByRole('menuitem', { name: 'New Task' })).toHaveAttribute('aria-disabled', 'true');
    await userEvent.keyboard('{Escape}');
    // A source cleared on the form (null) is empty too.
    rerender({ xv_customerid: null });
    expect(await screen.findByText('Fill Customer to see the records.')).toBeInTheDocument();
    await settle();
    expect(fetches).toHaveLength(0);
    expect(screen.queryByRole('grid')).toBeNull();
  });

  it('works on a create form', async () => {
    const { fetches } = mount({ xv_customerid: account() }, { creating: true });
    expect(await screen.findByText('Write docs')).toBeInTheDocument();
    expect(screen.queryByText('Save the record to see related records.')).toBeNull();
    expect(conditionOn(fetches[0]!, 'xv_accountid')).toBe(ACCOUNT_1);
  });

  it("invalid target for the value's table shows the warning", async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const { fetches } = mount({ xv_customerid: account(ACCOUNT_1, 'contact') });
    expect(await screen.findByText("This subgrid's filter cannot be applied: the column xv_accountid of xv_task cannot hold a record of contact.")).toBeInTheDocument();
    await settle();
    expect(fetches).toHaveLength(0);
    expect(warn).toHaveBeenCalledWith('[xrmverse subgrid filter]', expect.stringContaining('xv_accountid'));
  });

  it('a missing target column shows the warning', async () => {
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const missing = mount({ xv_customerid: account() }, { filter: { source: 'xv_customerid', target: 'xv_missingid' } });
    expect(await screen.findByText("This subgrid's filter cannot be applied: the table xv_task has no lookup column xv_missingid.")).toBeInTheDocument();
    await settle();
    expect(missing.fetches).toHaveLength(0);
  });

  it('a target column that is not a lookup shows the warning', async () => {
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const text = mount({ xv_customerid: account() }, { filter: { source: 'xv_customerid', target: 'xv_title' } });
    expect(await screen.findByText("This subgrid's filter cannot be applied: the table xv_task has no lookup column xv_title.")).toBeInTheDocument();
    await settle();
    expect(text.fetches).toHaveLength(0);
  });

  it('New fills the target column', async () => {
    const { record } = mount({ xv_customerid: account() });
    await screen.findByText('Write docs');
    await userEvent.click(await commandsMenu());
    expect(screen.queryByRole('menuitem', { name: 'Add Existing Task' })).toBeNull();
    await userEvent.click(await screen.findByRole('menuitem', { name: 'New Task' }));
    await waitFor(() => expect(record).toHaveBeenCalledTimes(1));
    const { url } = (record.mock.calls[0] as unknown as [{ url: string }])[0];
    const extraqs = new URLSearchParams(new URLSearchParams(url.slice(url.indexOf('?'))).get('extraqs')!);
    expect(Object.fromEntries(extraqs)).toEqual({ xv_accountid: ACCOUNT_1, xv_accountidname: 'Contoso', xv_accountidtype: 'account' });
    expect(url).toContain('etn=xv_task');
  });

  it('relationship wins', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const { fetches } = mount({ xv_customerid: account() }, { control: control({ relationship: 'xv_project_task' }) });
    await screen.findByText('Write docs');
    const fetch = fetches[0]!;
    expect(conditionOn(fetch, 'xv_projectid')).toBe(RECORD_ID);
    expect(conditionOn(fetch, 'xv_accountid')).toBeUndefined();
    const filterWarnings = warn.mock.calls.filter(([prefix]) => prefix === '[xrmverse subgrid filter]');
    expect(filterWarnings).toHaveLength(1);
    expect(filterWarnings[0]![1]).toContain('tasks');
  });

  it('an entry of another form is not applied', async () => {
    const { fetches } = mount({ xv_customerid: account() }, { control: control({ relationship: 'xv_project_task' }), filterFormId: 'f0000000-0000-0000-0000-0000000000f2' });
    await screen.findByText('Write docs');
    expect(conditionOn(fetches[0]!, 'xv_accountid')).toBeUndefined();
  });

  it('dataset control with a filter shows filterPcf', async () => {
    const { fetches, requests } = mount(
      { xv_customerid: account() },
      { control: control({ customControl: { name: 'xv_Test.Grid', parameters: {}, dataSet: { name: 'Grid', parameters: {} } } }) },
    );
    expect(await screen.findByText('This subgrid is filtered by a form column, which its custom control does not support.')).toBeInTheDocument();
    await settle();
    expect(fetches).toHaveLength(0);
    expect(requests.some((r) => r.startsWith('customcontrols'))).toBe(false);
  });

  describe('while the filters are read, or could not be read', () => {
    const dataSet = { customControl: { name: 'xv_Test.Grid', parameters: {}, dataSet: { name: 'Grid', parameters: {} } } };

    it('a dataset subgrid without a relationship waits: no PCF, no rows', async () => {
      const { fetches, requests } = mount({ xv_customerid: account() }, { control: control(dataSet), status: 'pending' });
      expect(await screen.findByText('Loading related records...')).toBeInTheDocument();
      await settle();
      expect(screen.getByText('Loading related records...')).toBeInTheDocument();
      expect(fetches).toHaveLength(0);
      expect(requests.some((r) => r.startsWith('customcontrols'))).toBe(false);
    });

    it('a grid without a relationship waits instead of showing the save-first notice', async () => {
      const { fetches } = mount({ xv_customerid: account() }, { creating: true, status: 'pending' });
      expect(await screen.findByText('Loading related records...')).toBeInTheDocument();
      await settle();
      expect(screen.queryByText('Save the record to see related records.')).toBeNull();
      expect(screen.getByText('Loading related records...')).toBeInTheDocument();
      expect(fetches).toHaveLength(0);
    });

    it('a subgrid with a relationship does not wait', async () => {
      const { fetches } = mount({ xv_customerid: account() }, { control: control({ relationship: 'xv_project_task' }), status: 'pending' });
      expect(await screen.findByText('Write docs')).toBeInTheDocument();
      expect(conditionOn(fetches[0]!, 'xv_accountid')).toBeUndefined();
    });

    it('a dataset subgrid without a relationship shows a notice when a filter resource failed: no PCF', async () => {
      const { fetches, requests } = mount({ xv_customerid: account() }, { control: control(dataSet), status: 'failed' });
      expect(await screen.findByText("This subgrid's filters could not be read, so its custom control is not shown.")).toBeInTheDocument();
      await settle();
      expect(fetches).toHaveLength(0);
      expect(requests.some((r) => r.startsWith('customcontrols'))).toBe(false);
    });
  });
});
