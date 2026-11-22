import { describe, expect, it, vi } from 'vitest';
import { attr } from '../admin/tables/testAttrs';
import type { ViewPage } from '../apps/runView';
import type { ViewColumn } from '../model/view';
import { createDataSet, type DataSetInput, type SortStatus } from './dataset';

const FMT = '@OData.Community.Display.V1.FormattedValue';
const LOGICAL = '@Microsoft.Dynamics.CRM.lookuplogicalname';

const attributes = [
  attr('xv_nodeid', { kind: 'unsupported', typeName: 'Uniqueidentifier', isPrimaryId: true }),
  attr('xv_name', { displayName: 'Name', isPrimaryName: true }),
  attr('xv_hours', { kind: 'decimal', typeName: 'Decimal', displayName: 'Hours' }),
  attr('xv_parentid', { kind: 'lookup', typeName: 'Lookup', displayName: 'Parent', targets: ['xv_node'] }),
];

const columns: ViewColumn[] = [
  { name: 'xv_name', width: 200, attribute: 'xv_name', isHidden: false },
  { name: 'xv_hours', width: 100, attribute: 'xv_hours', isHidden: false },
  { name: 'xv_secret', width: 100, attribute: 'xv_secret', isHidden: true },
  { name: 'xv_parentid', width: 150, attribute: 'xv_parentid', isHidden: false },
  { name: 'p.xv_code', width: 80, alias: 'p', attribute: 'xv_code', isHidden: false },
];

const row = (n: number): Record<string, unknown> => ({
  xv_nodeid: `00000000-0000-0000-0000-00000000000${n}`,
  xv_name: `Node ${n}`,
  xv_hours: n * 1.5,
  [`xv_hours${FMT}`]: `${n * 1.5} h`,
  _xv_parentid_value: 'aaaaaaaa-0000-0000-0000-000000000001',
  [`_xv_parentid_value${FMT}`]: 'Root',
  [`_xv_parentid_value${LOGICAL}`]: 'xv_node',
  'p.xv_code': `C${n}`,
});

const page = (rows: Record<string, unknown>[], over: Partial<ViewPage> = {}): ViewPage => ({ rows, more: false, ...over });

/** A deferred promise, to observe the loading state. */
function deferred<T>() {
  let resolve!: (v: T) => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

const flush = () => new Promise((r) => setTimeout(r, 0));

function make(over: Partial<DataSetInput> = {}) {
  const load = vi.fn<(page: number, sort: SortStatus[], pageSize: number) => Promise<ViewPage>>(async () => page([row(1), row(2)], { total: 2 }));
  const onChange = vi.fn();
  const openForm = vi.fn();
  const ds = createDataSet({
    target: 'xv_node',
    viewId: '{5a26f37e-0000-0000-0000-000000000002}',
    title: 'Nodes',
    columns,
    attributes,
    pageSize: 20,
    ready: true,
    zone: 'Etc/UTC',
    load,
    openForm,
    onChange,
    ...over,
  });
  return { ds, load: (over.load as typeof load | undefined) ?? load, onChange, openForm };
}

describe('createDataSet', () => {
  it('loads_page_1_and_sets_records_sorted_ids_columns', async () => {
    const d = deferred<ViewPage>();
    const { ds, load, onChange } = make({ load: vi.fn(() => d.promise) });
    expect(ds.loading).toBe(true);
    expect(load).toHaveBeenCalledWith(1, [], 20);
    d.resolve(page([row(1), row(2)], { total: 2 }));
    await flush();
    expect(ds.loading).toBe(false);
    expect(ds.error).toBe(false);
    expect(onChange).toHaveBeenCalled();
    expect(ds.sortedRecordIds).toEqual(['00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000002']);
    expect(Object.keys(ds.records)).toHaveLength(2);
    expect(ds.records['00000000-0000-0000-0000-000000000002']!.getRecordId()).toBe('00000000-0000-0000-0000-000000000002');
    expect(ds.columns).toEqual([
      { name: 'xv_name', displayName: 'Name', dataType: 'SingleLine.Text', alias: 'xv_name', order: 0, visualSizeFactor: 200, isHidden: false, isPrimary: true, disableSorting: false },
      { name: 'xv_hours', displayName: 'Hours', dataType: 'Decimal', alias: 'xv_hours', order: 1, visualSizeFactor: 100, isHidden: false, isPrimary: false, disableSorting: false },
      { name: 'xv_parentid', displayName: 'Parent', dataType: 'Lookup.Simple', alias: 'xv_parentid', order: 2, visualSizeFactor: 150, isHidden: false, isPrimary: false, disableSorting: false },
      { name: 'p.xv_code', displayName: 'p.xv_code', dataType: 'SingleLine.Text', alias: 'p.xv_code', order: 3, visualSizeFactor: 80, isHidden: false, isPrimary: false, disableSorting: false },
    ]);
    expect(ds.paging).toMatchObject({ pageSize: 20, totalResultCount: 2, firstPageNumber: 1, lastPageNumber: 1, pageNumber: 1, hasNextPage: false, hasPreviousPage: false });
    expect(ds.getTargetEntityType()).toBe('xv_node');
    expect(ds.getViewId()).toBe('{5a26f37e-0000-0000-0000-000000000002}');
    expect(ds.getTitle()).toBe('Nodes');
    expect(ds.records['00000000-0000-0000-0000-000000000001']!.getNamedReference()).toEqual({
      id: { guid: '00000000-0000-0000-0000-000000000001' },
      name: 'Node 1',
      etn: 'xv_node',
    });
  });

  it('paging_next_previous_exact_and_setPageSize', async () => {
    const load = vi.fn(async (p: number) => page([row(p)], { more: p < 3 }));
    const { ds } = make({ load });
    await flush();
    expect(ds.paging.totalResultCount).toBe(-1);
    expect(ds.paging.hasNextPage).toBe(true);
    expect(ds.paging.hasPreviousPage).toBe(false);
    ds.paging.loadNextPage();
    await flush();
    expect(load).toHaveBeenLastCalledWith(2, [], 20);
    expect(ds.paging).toMatchObject({ firstPageNumber: 2, lastPageNumber: 2, hasPreviousPage: true, hasNextPage: true });
    expect(ds.sortedRecordIds).toEqual(['00000000-0000-0000-0000-000000000002']);
    ds.paging.loadExactPage(3);
    await flush();
    expect(load).toHaveBeenLastCalledWith(3, [], 20);
    expect(ds.paging.hasNextPage).toBe(false);
    ds.paging.loadPreviousPage();
    await flush();
    expect(load).toHaveBeenLastCalledWith(2, [], 20);
    const calls = load.mock.calls.length;
    ds.paging.setPageSize(5);
    expect(ds.paging.pageSize).toBe(5);
    expect(load).toHaveBeenCalledTimes(calls); // applied on the next refresh
    ds.paging.reset();
    await flush();
    expect(load).toHaveBeenLastCalledWith(1, [], 5);
    expect(ds.paging.firstPageNumber).toBe(1);
  });

  it('sorting_change_then_refresh_requeries_page_1', async () => {
    const load = vi.fn(async (p: number) => page([row(p)], { more: true }));
    const { ds } = make({ load });
    await flush();
    ds.paging.loadNextPage();
    await flush();
    ds.sorting.push({ name: 'xv_hours', sortDirection: 1 });
    expect(load).toHaveBeenCalledTimes(2);
    ds.refresh();
    await flush();
    expect(load).toHaveBeenLastCalledWith(1, [{ name: 'xv_hours', sortDirection: 1 }], 20);
    ds.paging.loadNextPage();
    await flush();
    expect(load).toHaveBeenLastCalledWith(2, [{ name: 'xv_hours', sortDirection: 1 }], 20);
    ds.sorting = [{ name: 'xv_name', sortDirection: 0 }];
    ds.refresh();
    expect(load).toHaveBeenLastCalledWith(1, [{ name: 'xv_name', sortDirection: 0 }], 20);
  });

  it('selection_round_trip', async () => {
    const { ds } = make();
    await flush();
    expect(ds.getSelectedRecordIds()).toEqual([]);
    ds.setSelectedRecordIds(['00000000-0000-0000-0000-000000000001']);
    const got = ds.getSelectedRecordIds();
    got.push('x');
    expect(ds.getSelectedRecordIds()).toEqual(['00000000-0000-0000-0000-000000000001']);
    ds.clearSelectedRecordIds();
    expect(ds.getSelectedRecordIds()).toEqual([]);
  });

  it('getValue_lookup_is_entity_reference', async () => {
    const { ds, openForm } = make();
    await flush();
    const r = ds.records['00000000-0000-0000-0000-000000000001']!;
    expect(r.getValue('xv_parentid')).toEqual({ id: { guid: 'aaaaaaaa-0000-0000-0000-000000000001' }, name: 'Root', etn: 'xv_node' });
    expect(r.getValue('xv_hours')).toBe(1.5);
    expect(r.getValue('xv_name')).toBe('Node 1');
    expect(r.getValue('p.xv_code')).toBe('C1');
    expect(r.getValue('xv_missing')).toBeNull();
    ds.openDatasetItem(r.getNamedReference());
    expect(openForm).toHaveBeenCalledWith({ id: { guid: '00000000-0000-0000-0000-000000000001' }, name: 'Node 1', etn: 'xv_node' });
  });

  it('getFormattedValue_uses_annotation', async () => {
    const { ds } = make();
    await flush();
    const r = ds.records['00000000-0000-0000-0000-000000000002']!;
    expect(r.getFormattedValue('xv_hours')).toBe('3 h');
    expect(r.getFormattedValue('xv_parentid')).toBe('Root');
    expect(r.getFormattedValue('xv_name')).toBe('Node 2');
    expect(r.getFormattedValue('p.xv_code')).toBe('C2');
    expect(r.getFormattedValue('xv_missing')).toBe('');
  });

  it('error_sets_error_and_message', async () => {
    const onChange = vi.fn();
    const { ds } = make({ load: vi.fn(async () => Promise.reject(new Error('boom'))), onChange });
    await flush();
    expect(ds.loading).toBe(false);
    expect(ds.error).toBe(true);
    expect(ds.errorMessage).toBe('boom');
    expect(ds.sortedRecordIds).toEqual([]);
    expect(onChange).toHaveBeenCalled();
  });

  it('refresh_clears_a_previous_error_and_ignores_a_stale_result', async () => {
    const first = deferred<ViewPage>();
    const second = deferred<ViewPage>();
    const load = vi.fn().mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    const { ds, onChange } = make({ load });
    ds.refresh();
    expect(onChange).toHaveBeenCalledTimes(1); // loading started
    second.resolve(page([row(2)]));
    await flush();
    first.resolve(page([row(1)]));
    await flush();
    expect(ds.sortedRecordIds).toEqual(['00000000-0000-0000-0000-000000000002']);
    expect(onChange).toHaveBeenCalledTimes(2);
  });

  it('a_load_that_throws_synchronously_ends_in_error', async () => {
    const { ds, onChange } = make({
      load: vi.fn(() => {
        throw new Error('sync');
      }),
    });
    expect(ds.loading).toBe(true);
    await flush();
    expect(ds.loading).toBe(false);
    expect(ds.error).toBe(true);
    expect(ds.errorMessage).toBe('sync');
    expect(onChange).toHaveBeenCalledTimes(1);
  });

  it('a_malformed_result_ends_in_error', async () => {
    const { ds } = make({ load: vi.fn(async () => ({ more: false }) as unknown as ViewPage) });
    await flush();
    expect(ds.loading).toBe(false);
    expect(ds.error).toBe(true);
    expect(ds.sortedRecordIds).toEqual([]);
  });

  it('a_slow_page_1_after_a_page_2_request_is_dropped', async () => {
    const p1 = deferred<ViewPage>();
    const p2 = deferred<ViewPage>();
    const load = vi.fn().mockReturnValueOnce(p1.promise).mockReturnValueOnce(p2.promise);
    const { ds } = make({ load });
    ds.paging.loadExactPage(2);
    expect(load).toHaveBeenLastCalledWith(2, [], 20);
    p2.resolve(page([row(2)], { more: false }));
    await flush();
    p1.resolve(page([row(1)], { more: true }));
    await flush();
    expect(ds.sortedRecordIds).toEqual(['00000000-0000-0000-0000-000000000002']);
    expect(ds.paging).toMatchObject({ pageNumber: 2, hasPreviousPage: true, hasNextPage: false });
    expect(ds.loading).toBe(false);
  });

  it('refresh_during_a_load_keeps_loading_until_the_new_load_lands', async () => {
    const first = deferred<ViewPage>();
    const second = deferred<ViewPage>();
    const load = vi.fn().mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    const { ds } = make({ load });
    ds.sorting.push({ name: 'xv_name', sortDirection: 0 });
    ds.refresh();
    expect(load).toHaveBeenLastCalledWith(1, [{ name: 'xv_name', sortDirection: 0 }], 20);
    first.resolve(page([row(1)]));
    await flush();
    expect(ds.loading).toBe(true); // the first result was overtaken
    expect(ds.sortedRecordIds).toEqual([]);
    second.resolve(page([row(2), row(1)]));
    await flush();
    expect(ds.loading).toBe(false);
    expect(ds.sortedRecordIds).toEqual(['00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000001']);
  });

  it('an_error_then_a_successful_load_clears_error', async () => {
    const load = vi.fn().mockRejectedValueOnce(new Error('boom')).mockResolvedValueOnce(page([row(1)]));
    const { ds } = make({ load });
    await flush();
    expect(ds.error).toBe(true);
    ds.refresh();
    await flush();
    expect(ds.error).toBe(false);
    expect(ds.errorMessage).toBe('');
    expect(ds.sortedRecordIds).toEqual(['00000000-0000-0000-0000-000000000001']);
  });

  it('setReady_false_then_true', async () => {
    const pending = deferred<ViewPage>();
    const load = vi.fn(async (p: number) => page([row(p)], { more: true }));
    const { ds, onChange } = make({ load });
    await flush();
    ds.paging.loadNextPage();
    await flush();
    ds.setSelectedRecordIds(['00000000-0000-0000-0000-000000000002']);
    load.mockReturnValueOnce(pending.promise);
    ds.paging.loadNextPage(); // page 3, pending
    const changes = onChange.mock.calls.length;
    ds.setReady(false);
    expect(onChange).toHaveBeenCalledTimes(changes + 1);
    expect(ds.loading).toBe(false);
    expect(ds.sortedRecordIds).toEqual([]);
    expect(ds.getSelectedRecordIds()).toEqual([]);
    expect(ds.paging).toMatchObject({ pageNumber: 1, totalResultCount: -1, hasNextPage: false, hasPreviousPage: false });
    pending.resolve(page([row(3)]));
    await flush();
    expect(ds.sortedRecordIds).toEqual([]); // the dropped load does not land
    ds.setReady(false); // no change
    expect(onChange).toHaveBeenCalledTimes(changes + 1);
    ds.setReady(true);
    expect(load).toHaveBeenLastCalledWith(1, [], 20);
    expect(ds.loading).toBe(true);
    await flush();
    expect(ds.sortedRecordIds).toEqual(['00000000-0000-0000-0000-000000000001']);
  });

  it('selection_is_kept_across_loads', async () => {
    const load = vi.fn(async (p: number) => page([row(p)], { more: true }));
    const { ds } = make({ load });
    await flush();
    ds.setSelectedRecordIds(['00000000-0000-0000-0000-000000000001']);
    ds.paging.loadNextPage();
    await flush();
    ds.refresh();
    await flush();
    expect(ds.getSelectedRecordIds()).toEqual(['00000000-0000-0000-0000-000000000001']);
  });

  it('setPageSize_restarts_paging_from_page_1', async () => {
    const load = vi.fn(async (p: number) => page([row(p)], { more: true }));
    const { ds } = make({ load });
    await flush();
    ds.paging.loadExactPage(3);
    await flush();
    ds.paging.setPageSize(10);
    ds.paging.loadNextPage();
    expect(load).toHaveBeenLastCalledWith(2, [], 10);
  });

  it('filtering_throws_not_available', async () => {
    const { ds } = make();
    await flush();
    expect(() => ds.filtering).toThrow(/not available/);
    expect(() => ds.linking).toThrow(/not available/);
    expect(() => ds.addColumn('xv_x')).toThrow(/not available/);
    expect(() => ds.retrieveRecordCommand([], [])).toThrow(/not available/);
  });

  it('no_parent_id_does_not_query', async () => {
    const { ds, load, onChange } = make({ ready: false });
    await flush();
    expect(load).not.toHaveBeenCalled();
    expect(ds.loading).toBe(false);
    expect(ds.sortedRecordIds).toEqual([]);
    expect(ds.records).toEqual({});
    ds.refresh();
    ds.paging.loadNextPage();
    expect(load).not.toHaveBeenCalled();
    ds.setReady(true);
    expect(ds.loading).toBe(true);
    expect(load).toHaveBeenCalledWith(1, [], 20);
    await flush();
    expect(ds.sortedRecordIds).toHaveLength(2);
    expect(onChange).toHaveBeenCalledTimes(2); // loading started, loaded
  });

  it('dispose_ignores_late_results', async () => {
    const d = deferred<ViewPage>();
    const { ds, onChange } = make({ load: vi.fn(() => d.promise) });
    ds.dispose();
    d.resolve(page([row(1)]));
    await flush();
    expect(onChange).not.toHaveBeenCalled();
    expect(ds.sortedRecordIds).toEqual([]);
  });
});
