import { act, cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { onPath } from '../admin/security/testSupport';
import { attr } from '../admin/tables/testAttrs';
import { rawAttr, rawEntity, rawOneToMany } from '../admin/tables/tableTestSupport';
import type { AppsMock } from '../apps/testSupport';
import { mountForm } from '../client-api/testForm';
import type { XrmHost } from '../client-api/xrm';
import type { Cell, CustomControlDataSet } from '../model/form';
import { contextOf, field, RECORD_ID, simpleForm } from '../renderers/classic/form/formTestSupport';
import { server } from '../test/server';
import { datasetControl, type TestControl } from '../test/pcf/controls';
import { probeDatasetControl, serveControls } from '../test/pcf/serve';
import { resetPcfLoader, setScriptLoader } from './loader';

/* eslint-disable @typescript-eslint/no-explicit-any */
type Ctx = any;

const globals = window as unknown as Record<string, unknown>;

/** What the host did to the probe control of a test, with the data-set as each `updateView` saw it. */
interface Probe {
  calls: string[];
  contexts: Ctx[];
  seen: { loading: boolean; ids: string[] }[];
  container?: HTMLDivElement;
}
let probe: Probe;

function probeClass() {
  return class {
    init(context: Ctx, _notify: () => void, _state: unknown, container?: HTMLDivElement) {
      probe.calls.push('init');
      probe.container = container;
      probe.contexts.push(context);
    }
    updateView(context: Ctx) {
      probe.calls.push(`updateView:${(context.updatedProperties as string[]).join(',')}`);
      probe.contexts.push(context);
      const ds = context.parameters.records;
      probe.seen.push({ loading: ds.loading, ids: [...ds.sortedRecordIds] });
    }
    getOutputs() {
      return {};
    }
    destroy() {
      probe.calls.push('destroy');
    }
  };
}

beforeEach(() => {
  resetPcfLoader();
  probe = { calls: [], contexts: [], seen: [] };
  globals.__xvProbe = probeClass();
});
afterEach(() => {
  setScriptLoader(undefined);
  resetPcfLoader();
  for (const k of ['Reactv16', 'FluentUIReactv940', 'FluentUIReactv9', 'ComponentFramework', '__xvProbe']) delete globals[k];
});

const VIEW_ID = 'a0000000-0000-0000-0000-0000000000a1';
const name = attr('xv_name', { isPrimaryName: true, displayName: 'Name' });
const RECORD = { xv_projectid: RECORD_ID, xv_name: 'Alpha' };

const TASKS: AppsMock = {
  savedQueries: [
    {
      id: VIEW_ID,
      name: 'Active Tasks',
      entity: 'xv_task',
      isdefault: true,
      fetchxml: `<fetch version="1.0" mapping="logical"><entity name="xv_task"><attribute name="xv_title"/><attribute name="xv_taskid"/><order attribute="xv_title" descending="false"/></entity></fetch>`,
      layoutxml: `<grid name="resultset" jump="xv_title" select="1" icon="1" preview="1"><row name="result" id="xv_taskid"><cell name="xv_title" width="200"/></row></grid>`,
    },
  ],
  tables: { xv_task: { entitySetName: 'xv_tasks', primaryId: 'xv_taskid', primaryName: 'xv_title', labels: { xv_title: { 1033: 'Title' } } } },
};

const ROWS = [
  { xv_taskid: 't1', xv_title: 'Write docs' },
  { xv_taskid: 't2', xv_title: 'Ship it' },
];

const dataSetOf = (over: Partial<CustomControlDataSet> = {}): CustomControlDataSet => ({
  name: 'records',
  parameters: { ViewId: `{${VIEW_ID}}`, TargetEntityType: 'xv_task', RelationshipName: 'xv_project_task', RecordsPerPage: '4' },
  ...over,
});

function subgridCell(control: TestControl, dataSet = dataSetOf(), over: { relationship?: string } = {}): Cell {
  return {
    id: 'cell-tasks',
    labels: { 1033: 'Tasks' },
    showLabel: true,
    visible: true,
    colSpan: 1,
    rowSpan: 1,
    control: {
      kind: 'subgrid',
      id: 'tasks',
      relationship: over.relationship ?? 'xv_project_task',
      targetEntity: 'xv_task',
      viewId: VIEW_ID,
      recordsPerPage: 4,
      enableViewPicker: false,
      customControl: { name: control.name, parameters: {}, dataSet },
    },
  };
}

interface Options {
  data?: Record<string, unknown> | null;
  rights?: ('Read' | 'Write')[];
  records?: (fetch: Element) => Record<string, unknown>;
  xrmHost?: XrmHost;
  cell?: Cell;
}

/** Mounts a form with a name field and the dataset subgrid; returns the fetches of the subgrid's rows. */
function mount(control: TestControl, o: Options = {}) {
  const fetches: Element[] = [];
  const data = o.data === undefined ? RECORD : o.data;
  const ctx = contextOf([name], data ?? undefined, { rights: o.rights });
  const m = mountForm(simpleForm([field('xv_name'), o.cell ?? subgridCell(control)]), ctx, {
    apps: {
      ...TASKS,
      records: (_set, fetch) => {
        fetches.push(fetch);
        return o.records ? o.records(fetch) : { value: ROWS };
      },
    },
    xrmHost: o.xrmHost,
    setup: () => {
      serveControls([control]);
      server.use(
        http.get('*/api/data/v9.2/RelationshipDefinitions', () => HttpResponse.json({ value: [rawOneToMany('xv_project_task', 'xv_task', 'xv_projectid', 'xv_project')] })),
        onPath('get', "EntityDefinitions(LogicalName='xv_task')", () =>
          HttpResponse.json(
            rawEntity(
              'xv_task',
              'xv_tasks',
              [rawAttr('xv_taskid', 'Uniqueidentifier', { IsPrimaryId: true }), rawAttr('xv_title', 'String', { IsPrimaryName: true, display: 'Title' })],
              { PrimaryNameAttribute: 'xv_title' },
            ),
          ),
        ),
      );
    },
  });
  return { m, fetches };
}

const last = (): Ctx => probe.contexts[probe.contexts.length - 1];
const count = (call: string) => probe.calls.filter((c) => c === call).length;

/** A host whose `Xrm.Navigation.openForm` is `openForm`. */
const hostWith = (openForm: (...a: unknown[]) => unknown): XrmHost =>
  ({ xrm: { Navigation: { openForm }, Utility: {} }, globalContext: {}, install: () => {}, setCurrentForm: () => {}, currentForm: () => null }) as unknown as XrmHost;

describe('PcfDatasetControl', () => {
  it('init_once_then_update_view_with_the_data_set_after_the_load', async () => {
    const control = probeDatasetControl('DsProbe');
    const { fetches } = mount(control);
    await waitFor(() => expect(probe.seen.at(-1)).toEqual({ loading: false, ids: ['t1', 't2'] }));
    expect(count('init')).toBe(1);
    expect(probe.calls.at(-1)).toBe('updateView:records');
    // The first updateView is sent while page 1 loads.
    expect(probe.seen[0]).toEqual({ loading: true, ids: [] });
    const ds = last().parameters.records;
    expect(probe.contexts.every((c: Ctx) => c.parameters.records === ds)).toBe(true);
    expect(ds.getTitle()).toBe('Active Tasks');
    expect(ds.getTargetEntityType()).toBe('xv_task');
    expect(ds.getViewId()).toBe(VIEW_ID);
    expect(ds.paging.pageSize).toBe(4);
    expect(ds.columns.map((c: Ctx) => [c.name, c.displayName, c.isPrimary])).toEqual([['xv_title', 'Title', true]]);
    expect(ds.records.t1.getFormattedValue('xv_title')).toBe('Write docs');
    // The rows are the record's.
    expect(fetches).toHaveLength(1);
    const condition = fetches[0]!.getElementsByTagName('condition')[0]!;
    expect(condition.getAttribute('attribute')).toBe('xv_projectid');
    expect(condition.getAttribute('value')).toBe(RECORD_ID);
    expect(fetches[0]!.getAttribute('count')).toBe('4');
    // The form's record, as for a field control.
    expect(last().mode.contextInfo).toEqual({ entityId: RECORD_ID, entityTypeName: 'xv_project', entityRecordName: 'Alpha' });
    expect(last().page.entityId).toBe(RECORD_ID);
    expect(last().mode.isControlDisabled).toBe(false);
    expect(last().mode.label).toBe('Tasks');
    // The classic grid is not shown.
    expect(screen.queryByRole('grid')).toBeNull();
  });

  it('refresh_from_the_control_re_queries', async () => {
    const control = probeDatasetControl('DsProbe');
    const { fetches } = mount(control);
    await waitFor(() => expect(probe.seen.at(-1)?.loading).toBe(false));
    const before = probe.calls.length;
    act(() => last().parameters.records.refresh());
    await waitFor(() => expect(fetches).toHaveLength(2));
    await waitFor(() => expect(probe.seen.at(-1)).toEqual({ loading: false, ids: ['t1', 't2'] }));
    expect(probe.calls.slice(before).every((c) => c === 'updateView:records')).toBe(true);
    expect(count('init')).toBe(1);
  });

  it('a_script_refresh_of_the_subgrid_reloads_the_data_set', async () => {
    const control = probeDatasetControl('DsProbe');
    const { m, fetches } = mount(control);
    await waitFor(() => expect(probe.seen.at(-1)?.loading).toBe(false));
    act(() => void (m.fc().getControl('tasks') as unknown as { refresh(): void }).refresh());
    await waitFor(() => expect(fetches).toHaveLength(2));
    expect(count('init')).toBe(1);
  });

  it('a_sort_change_then_refresh_orders_the_query', async () => {
    const control = probeDatasetControl('DsProbe');
    const { fetches } = mount(control);
    await waitFor(() => expect(probe.seen.at(-1)?.loading).toBe(false));
    act(() => {
      const ds = last().parameters.records;
      ds.sorting.push({ name: 'xv_title', sortDirection: 1 });
      ds.refresh();
    });
    await waitFor(() => expect(fetches).toHaveLength(2));
    const orders = Array.from(fetches[1]!.getElementsByTagName('order'));
    expect(orders.map((o) => [o.getAttribute('attribute'), o.getAttribute('descending')])).toEqual([['xv_title', 'true']]);
  });

  it('open_dataset_item_opens_the_form', async () => {
    const openForm = vi.fn(() => Promise.resolve({ savedEntityReference: [] }));
    const control = probeDatasetControl('DsProbe');
    mount(control, { xrmHost: hostWith(openForm) });
    await waitFor(() => expect(probe.seen.at(-1)?.loading).toBe(false));
    const ds = last().parameters.records;
    act(() => ds.openDatasetItem(ds.records.t2.getNamedReference()));
    expect(openForm).toHaveBeenCalledWith({ entityName: 'xv_task', entityId: 't2' });
  });

  it('a_property_set_manifest_falls_back_with_the_notice', async () => {
    const control = probeDatasetControl('DsSet', { propertySet: true });
    mount(control);
    expect(await screen.findByText("Custom control 'xv_XvTest.DsSet' could not be loaded: property-set is not supported in this version")).toBeInTheDocument();
    // The classic grid takes its place.
    expect(await screen.findByText('Write docs')).toBeInTheDocument();
    expect(screen.getByRole('grid', { name: 'Active Tasks' })).toBeInTheDocument();
    expect(probe.calls).not.toContain('init');
  });

  it('a_manifest_without_the_form_data_set_falls_back', async () => {
    const control = probeDatasetControl('DsOther', { dataSet: 'items' });
    mount(control);
    expect(await screen.findByText("Custom control 'xv_XvTest.DsOther' could not be loaded: the manifest has no data-set 'records'")).toBeInTheDocument();
    expect(await screen.findByText('Write docs')).toBeInTheDocument();
  });

  it('a_load_error_sets_error', async () => {
    const control = probeDatasetControl('DsProbe');
    mount(control);
    server.use(
      http.get('*/api/data/v9.2/xv_tasks', () => HttpResponse.json({ error: { code: '0x80040217', message: 'The view cannot run' } }, { status: 400 })),
    );
    await waitFor(() => expect(count('init')).toBe(1));
    await waitFor(() => expect(last().parameters.records.error).toBe(true));
    const ds = last().parameters.records;
    expect(ds.loading).toBe(false);
    expect(ds.errorMessage).toMatch(/The view cannot run/);
    expect(probe.calls.at(-1)).toBe('updateView:records');
  });

  it('a_new_record_does_not_query_until_the_first_save', async () => {
    const control = probeDatasetControl('DsProbe');
    const { fetches } = mount(control, { data: null });
    await waitFor(() => expect(count('init')).toBe(1));
    await new Promise((r) => setTimeout(r, 30));
    const ds = last().parameters.records;
    expect(fetches).toHaveLength(0);
    expect(ds.loading).toBe(false);
    expect(ds.sortedRecordIds).toEqual([]);
    expect(last().mode.contextInfo.entityId).toBe('');
    await userEvent.type(screen.getByLabelText('xv_name label'), 'Beta');
    await userEvent.click(screen.getByRole('button', { name: 'Save now' }));
    await waitFor(() => expect(fetches).toHaveLength(1));
    await waitFor(() => expect(probe.seen.at(-1)).toEqual({ loading: false, ids: ['t1', 't2'] }));
    expect(fetches[0]!.getElementsByTagName('condition')[0]!.getAttribute('value')).toBe(RECORD_ID);
    expect(last().parameters.records).toBe(ds);
    expect(count('init')).toBe(1);
  });

  it('without_a_relationship_the_view_is_queried_at_once', async () => {
    const control = probeDatasetControl('DsProbe');
    const dataSet = dataSetOf({ parameters: { ViewId: `{${VIEW_ID}}`, TargetEntityType: 'xv_task', RecordsPerPage: '4' } });
    const { fetches } = mount(control, { data: null, cell: subgridCell(control, dataSet, { relationship: '' }) });
    await waitFor(() => expect(probe.seen.at(-1)).toEqual({ loading: false, ids: ['t1', 't2'] }));
    expect(fetches[0]!.getElementsByTagName('condition')).toHaveLength(0);
  });

  it('a_read_only_form_disables_the_control', async () => {
    const control = probeDatasetControl('DsProbe');
    mount(control, { rights: ['Read'] });
    await waitFor(() => expect(count('init')).toBe(1));
    expect(last().mode.isControlDisabled).toBe(true);
  });

  it('track_container_resize_sends_layout', async () => {
    const callbacks: ResizeObserverCallback[] = [];
    const original = globalThis.ResizeObserver;
    globalThis.ResizeObserver = class {
      constructor(callback: ResizeObserverCallback) {
        callbacks.push(callback);
      }
      observe() {}
      unobserve() {}
      disconnect() {}
    } as unknown as typeof ResizeObserver;
    try {
      const control = probeDatasetControl('DsProbe');
      mount(control);
      await waitFor(() => expect(probe.seen.at(-1)?.loading).toBe(false));
      act(() => last().mode.trackContainerResize(true));
      act(() => callbacks[0]!([{ contentRect: { width: 640, height: 300 } } as ResizeObserverEntry], {} as ResizeObserver));
      await waitFor(() => expect(last().updatedProperties).toEqual(['layout']));
      expect(last().mode.allocatedWidth).toBe(640);
      expect(last().mode.allocatedHeight).toBe(300);
    } finally {
      globalThis.ResizeObserver = original;
    }
  });

  it('unmount_destroys_once', async () => {
    const control = probeDatasetControl('DsProbe');
    mount(control);
    await waitFor(() => expect(probe.seen.at(-1)?.loading).toBe(false));
    cleanup();
    expect(count('destroy')).toBe(1);
  });

  it('the_dataset_test_control_lists_the_rows_and_refreshes', async () => {
    globals.__xvProbe = undefined;
    const cell = subgridCell(datasetControl, dataSetOf({ name: 'items' }));
    const { fetches } = mount(datasetControl, { cell });
    expect(await screen.findByText('Item Write docs')).toBeInTheDocument();
    expect(screen.getByText('Item Ship it')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Reload list' }));
    await waitFor(() => expect(fetches).toHaveLength(2));
  });
});
