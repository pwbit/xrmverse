import { valuesFromRecord } from '../admin/tables/formValues';
import type { AttributeMeta } from '../admin/tables/metadata';
import { cellText, type ViewPage } from '../apps/runView';
import { Stage, stub, stubProperty } from '../client-api/notImplemented';
import { toClientValue } from '../client-api/values';
import type { ViewColumn } from '../model/view';
import { manifestTypeOf } from './context';

/*
 * The dataset parameter of a PCF control (ruling E7), as the Learn `DataSet`, `Paging`, `EntityRecord` and `SortStatus`
 * references for model-driven apps describe it. Plain TypeScript: the caller supplies the query (`load`) and repaints on
 * `onChange`.
 */

/** Learn, `SortStatus`: `sortDirection` 0 ascending, 1 descending. */
export interface SortStatus {
  name: string;
  sortDirection: 0 | 1;
}

/** Learn, ComponentFramework `EntityReference`. */
export interface EntityReference {
  id: { guid: string };
  name: string;
  etn: string;
}

/** Learn, `DataSetApi.Column`. */
export interface DataSetColumn {
  name: string;
  displayName: string;
  /** The manifest type of the column (`SingleLine.Text`, `Decimal`, `Lookup.Simple`, ...). */
  dataType: string;
  alias: string;
  order: number;
  visualSizeFactor: number;
  isHidden: false;
  isPrimary: boolean;
  disableSorting: false;
}

/** Learn, `EntityRecord`. */
export interface PcfEntityRecord {
  getRecordId(): string;
  /** The Client API value; a lookup is an {@link EntityReference}; a column the row lacks is null. */
  getValue(columnName: string): unknown;
  /** The OData formatted value, else the value as a string; empty when the row lacks the column. */
  getFormattedValue(columnName: string): string;
  getNamedReference(): EntityReference;
}

/** Learn, `Paging`. */
export interface PcfPaging {
  pageSize: number;
  /** -1 when unknown (the query did not count). */
  totalResultCount: number;
  firstPageNumber: number;
  lastPageNumber: number;
  pageNumber: number;
  hasNextPage: boolean;
  hasPreviousPage: boolean;
  loadNextPage(): void;
  loadPreviousPage(): void;
  loadExactPage(pageNumber: number): void;
  /** Applies on the next load; paging starts again from page 1 (the next {@link loadNextPage} loads page 2). */
  setPageSize(pageSize: number): void;
  /** Reloads page 1. */
  reset(): void;
}

export interface PcfDataSet {
  loading: boolean;
  error: boolean;
  errorMessage: string;
  columns: DataSetColumn[];
  records: Record<string, PcfEntityRecord>;
  sortedRecordIds: string[];
  paging: PcfPaging;
  /** The sort the next {@link refresh} applies; the control may change the array or replace it. */
  sorting: SortStatus[];
  /** Not available in this version: reading it throws. */
  readonly filtering: unknown;
  /** Not available in this version: reading it throws. */
  readonly linking: unknown;
  /** Not available in this version: throws. */
  addColumn(name: string, entityAlias?: string): never;
  /** Not available in this version: throws. */
  retrieveRecordCommand(recordIds: string[], specificCommands: string[], ...rest: unknown[]): never;
  /** Reloads page 1 with the current {@link sorting} and page size. */
  refresh(): void;
  /** The selection is kept across page loads and refreshes; only {@link setReady}(false) clears it. */
  getSelectedRecordIds(): string[];
  setSelectedRecordIds(ids: string[]): void;
  clearSelectedRecordIds(): void;
  getTargetEntityType(): string;
  getViewId(): string;
  getTitle(): string;
  openDatasetItem(ref: EntityReference): void;
  /**
   * Host only. False while the subgrid has no parent id (a new record): nothing is queried and the dataset is empty. True
   * loads page 1; false empties the dataset again, drops a pending load, clears the selection and goes back to page 1.
   */
  setReady(ready: boolean): void;
  /** Host only. Later results are ignored and `onChange` is no longer called. */
  dispose(): void;
}

export interface DataSetInput {
  /** The target table's logical name. */
  target: string;
  viewId: string;
  title: string;
  /** The view's layout cells, in order (`parseLayout`). */
  columns: ViewColumn[];
  /** The target table's attributes. */
  attributes: AttributeMeta[];
  pageSize: number;
  /** False when there is nothing to query yet (no parent id): see {@link PcfDataSet.setReady}. */
  ready: boolean;
  /** One page of rows (1-based `page`), as `runView` returns it. `pageSize` is the current page size. */
  load(page: number, sort: SortStatus[], pageSize: number): Promise<ViewPage>;
  /** Opens the referenced record's form (`Xrm.Navigation.openForm`). */
  openForm(ref: EntityReference): void;
  /** Called after each change the control sees: loading started, loaded, failed. Not called from `createDataSet` itself. */
  onChange(): void;
  /** The user's time zone, for User Local dates. */
  zone: string;
}

/** The type of a column without metadata (a linked table's `alias.attr` cell). */
const UNKNOWN_TYPE = 'SingleLine.Text';

function columnsOf(input: DataSetInput, meta: Map<string, AttributeMeta>): DataSetColumn[] {
  return input.columns
    .filter((c) => !c.isHidden)
    .map((c, order) => {
      const a = c.alias ? undefined : meta.get(c.attribute);
      return {
        name: c.name,
        displayName: a?.displayName || c.name,
        dataType: a ? manifestTypeOf(a) : UNKNOWN_TYPE,
        alias: c.name,
        order,
        visualSizeFactor: c.width,
        isHidden: false,
        isPrimary: a?.isPrimaryName === true,
        disableSorting: false,
      };
    });
}

const asText = (v: unknown): string =>
  typeof v === 'string' || typeof v === 'number' || typeof v === 'boolean' ? String(v) : '';

function recordOf(row: Record<string, unknown>, id: string, input: DataSetInput, meta: Map<string, AttributeMeta>, primaryName: string | undefined): PcfEntityRecord {
  const reference: EntityReference = { id: { guid: id }, name: primaryName ? asText(row[primaryName]) : '', etn: input.target };
  const viewColumn = (name: string): ViewColumn => {
    const dot = name.indexOf('.');
    return dot > 0
      ? { name, alias: name.slice(0, dot), attribute: name.slice(dot + 1), width: 0, isHidden: false }
      : { name, attribute: name, width: 0, isHidden: false };
  };
  return {
    getRecordId: () => id,
    getValue(name) {
      const a = name.includes('.') ? undefined : meta.get(name);
      if (!a) return row[name] ?? null;
      const value = valuesFromRecord([a], row, () => undefined, input.zone)[a.logicalName];
      if (a.kind === 'lookup') {
        if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
        return { id: { guid: value.id }, name: value.name ?? '', etn: value.target } satisfies EntityReference;
      }
      return toClientValue(a, value, input.zone);
    },
    getFormattedValue: (name) => cellText(row, viewColumn(name)),
    getNamedReference: () => ({ ...reference, id: { ...reference.id } }),
  };
}

/** The dataset object of E7. When `input.ready`, page 1 starts loading at once (`loading` is true on return). */
export function createDataSet(input: DataSetInput): PcfDataSet {
  const meta = new Map(input.attributes.map((a) => [a.logicalName, a]));
  const primaryId = input.attributes.find((a) => a.isPrimaryId)?.logicalName ?? `${input.target}id`;
  const primaryName = input.attributes.find((a) => a.isPrimaryName)?.logicalName;
  let ready = input.ready;
  let disposed = false;
  /** Bumped by every load, so that only the latest one lands. */
  let sequence = 0;
  let page = 1;
  /** The sort of the last refresh: paging keeps it. */
  let appliedSort: SortStatus[] = [];
  let selected: string[] = [];

  const changed = () => {
    if (!disposed) input.onChange();
  };

  const empty = () => {
    ds.records = {};
    ds.sortedRecordIds = [];
    Object.assign(ds.paging, { totalResultCount: -1, firstPageNumber: 1, lastPageNumber: 1, pageNumber: 1, hasNextPage: false, hasPreviousPage: false });
  };

  const failed = (mine: number, e: unknown) => {
    if (mine !== sequence || disposed) return;
    // The records and page of the last load stay.
    ds.loading = false;
    ds.error = true;
    ds.errorMessage = e instanceof Error ? e.message : String(e);
    changed();
  };

  const loaded = (mine: number, target: number, result: ViewPage) => {
    if (mine !== sequence || disposed) return;
    const records: Record<string, PcfEntityRecord> = {};
    const ids: string[] = [];
    try {
      for (const row of result.rows) {
        const raw = row[primaryId];
        if (typeof raw !== 'string' || raw === '' || records[raw]) continue;
        records[raw] = recordOf(row, raw, input, meta, primaryName);
        ids.push(raw);
      }
    } catch (e) {
      // A malformed result (no `rows`, a row that is not an object) fails like a rejected load.
      failed(mine, e);
      return;
    }
    page = target;
    ds.records = records;
    ds.sortedRecordIds = ids;
    Object.assign(ds.paging, {
      totalResultCount: typeof result.total === 'number' ? result.total : -1,
      firstPageNumber: page,
      lastPageNumber: page,
      pageNumber: page,
      hasNextPage: result.more === true,
      hasPreviousPage: page > 1,
    });
    ds.loading = false;
    ds.error = false;
    ds.errorMessage = '';
    changed();
  };

  const loadPage = (target: number, notify: boolean) => {
    if (!ready || disposed) return;
    const mine = ++sequence;
    ds.loading = true;
    if (notify) changed();
    // `load` is called now; a synchronous throw becomes a rejection, so `loading` always ends.
    new Promise<ViewPage>((resolve) => resolve(input.load(target, appliedSort.map((s) => ({ ...s })), ds.paging.pageSize))).then(
      (result) => loaded(mine, target, result),
      (e: unknown) => failed(mine, e),
    );
  };

  const refresh = (notify: boolean) => {
    appliedSort = ds.sorting.map((s) => ({ ...s }));
    loadPage(1, notify);
  };

  const ds: PcfDataSet = {
    loading: false,
    error: false,
    errorMessage: '',
    columns: columnsOf(input, meta),
    records: {},
    sortedRecordIds: [],
    sorting: [],
    paging: {
      pageSize: input.pageSize,
      totalResultCount: -1,
      firstPageNumber: 1,
      lastPageNumber: 1,
      pageNumber: 1,
      hasNextPage: false,
      hasPreviousPage: false,
      loadNextPage: () => {
        if (ds.paging.hasNextPage) loadPage(page + 1, true);
      },
      loadPreviousPage: () => {
        if (page > 1) loadPage(page - 1, true);
      },
      loadExactPage: (n: number) => {
        if (Number.isInteger(n) && n >= 1) loadPage(n, true);
      },
      setPageSize: (n: number) => {
        if (!Number.isInteger(n) || n <= 0) return;
        ds.paging.pageSize = n;
        // Pages of the old size do not map onto the new one: paging starts again from page 1.
        page = 1;
      },
      reset: () => refresh(true),
    },
    addColumn: stub('DataSet.addColumn', Stage.later),
    retrieveRecordCommand: stub('DataSet.retrieveRecordCommand', Stage.later),
    refresh: () => refresh(true),
    getSelectedRecordIds: () => [...selected],
    setSelectedRecordIds: (ids: string[]) => {
      selected = [...ids];
    },
    clearSelectedRecordIds: () => {
      selected = [];
    },
    getTargetEntityType: () => input.target,
    getViewId: () => input.viewId,
    getTitle: () => input.title,
    openDatasetItem: (ref: EntityReference) => input.openForm(ref),
    setReady: (value: boolean) => {
      if (value === ready || disposed) return;
      ready = value;
      if (ready) {
        refresh(true);
        return;
      }
      sequence++;
      page = 1;
      selected = [];
      empty();
      ds.loading = false;
      ds.error = false;
      ds.errorMessage = '';
      changed();
    },
    dispose: () => {
      disposed = true;
      sequence++;
    },
  } as Omit<PcfDataSet, 'filtering' | 'linking'> as PcfDataSet;
  stubProperty(ds, 'filtering', 'DataSet.filtering', Stage.later);
  stubProperty(ds, 'linking', 'DataSet.linking', Stage.later);

  refresh(false);
  return ds;
}
