import { Body1, Button, makeStyles, MessageBar, MessageBarActions, MessageBarBody, Spinner, Text, tokens } from '@fluentui/react-components';
import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import type { ViewModel } from '../model/view';
import type { ViewMode } from '../parsers/presentationJson';
import { applyQuickFind, linkEntities, withOrder, withoutPaging, withPaging, withTotalCount } from '../parsers/fetchXml';
import { columnHeaders, columnKinds } from './columnHeaders';
import { QuickFind } from '../renderers/classic/QuickFind';
import { useRenderer } from '../renderers/RendererContext';
import { useViewPresentation } from '../renderers/modern/PresentationContext';
import type { GridRow, GridSort, ViewSelection } from '../renderers/classic/ViewGrid';
import { ViewSelector, type ViewChoice } from '../renderers/classic/ViewSelector';
import type { XrmApi } from '../shell/api/client';
import { useAuth } from '../shell/auth/AuthProvider';
import { t } from '../shell/i18n/t';
import { tableRights } from './record/canCreate';
import { loadTable, loadViews, type TableInfo, type TableViews } from './loadViews';
import { mainAspxUrl } from './mainAspx';
import { cellText, pageSize, runView, type ViewPage } from './runView';
import { wallClockOf } from './timeZones';
import { useUserSettings } from './userSettings';
import { ViewCommands } from './viewCommands';
import { BackIcon, DismissIcon, FirstPageIcon, NextPageIcon, PreviousPageIcon } from '../shell/icons';
import type { EntityMeta } from '../admin/tables/metadata';
import { loadEntity } from '../admin/tables/tableApi';
import { statesOf } from './record/recordStates';
import { ViewCommandBar } from './views/ViewCommandBar';
import { readStates } from './views/viewCommands';
import { useBulkRun } from './views/useBulkRun';
import { ModeView } from './views/ModeView';
import { useResolvedModes } from './views/useResolvedModes';
import { ViewModeSwitcher } from './views/ViewModeSwitcher';

/** `viewType` of a public view (`savedquery` object type code) and of a personal one (`userquery`). */
const SAVED_QUERY_TYPE = 1039;
const USER_QUERY_TYPE = 4230;

/** The entity list URL of a view: `viewid` and `viewType` 1039 (public) or 4230 (personal). */
export function viewUrl(appId: string, etn: string, view: { id: string; personal: boolean }): string {
  return mainAspxUrl({ appId, pageType: 'entitylist', etn, viewId: view.id, viewType: view.personal ? USER_QUERY_TYPE : SAVED_QUERY_TYPE });
}

const errorText = (e: unknown) => (e instanceof Error ? e.message : String(e));

interface Setup {
  views: TableViews;
  /** The listed table and the tables of the views' link-entities, by logical name. */
  tables: Record<string, TableInfo>;
}

type SetupState = { status: 'loading' } | { status: 'error'; error: string } | ({ status: 'ready' } & Setup);

async function loadSetup(api: XrmApi, etn: string, lcid: number): Promise<Setup> {
  const [views, table] = await Promise.all([loadViews(api, etn), loadTable(api, etn, lcid)]);
  const all = [...views.system, ...views.personal, ...(views.quickFind ? [views.quickFind] : [])];
  const targets = new Set(all.flatMap((v) => Object.values(linkEntities(v.fetchXml))).filter((name) => name !== etn));
  const linked = await Promise.all([...targets].map((name) => loadTable(api, name, lcid)));
  return { views, tables: Object.fromEntries([table, ...linked].map((info) => [info.logicalName, info])) };
}

/** The view the URL names (public or personal), else the default public view, else the first by name. */
function selectView(views: TableViews, viewId: string | undefined): ViewChoice | undefined {
  const id = viewId?.toLowerCase();
  const named = views.system.find((v) => v.id === id);
  if (named) return { view: named, personal: false };
  const mine = views.personal.find((v) => v.id === id);
  if (mine) return { view: mine, personal: true };
  const fallback = views.system.find((v) => v.isDefault) ?? views.system[0];
  if (fallback) return { view: fallback, personal: false };
  return views.personal[0] ? { view: views.personal[0], personal: true } : undefined;
}

/** What "Save view" stores (#101): the view's query with the current sort, without paging or count. */
function savedFetchXml(fetchXml: string, sort: GridSort | undefined): string {
  return withoutPaging(sort ? withOrder(fetchXml, sort.attribute, sort.descending, sort.linkAlias) : fetchXml);
}

/** What the user did on the current view: quick find text, sort and page (with the cookie of each page reached). */
interface Query {
  viewKey: string;
  search: string;
  sort?: GridSort;
  page: number;
  /** `cookies[n]` is the paging cookie that requests page n + 1 (none for page 1). */
  cookies: (string | undefined)[];
}

const freshQuery = (viewKey: string): Query => ({ viewKey, search: '', page: 1, cookies: [undefined] });

const useStyles = makeStyles({
  // The page fills the main area: the command bar, the title and the view's bar stay, only the grid's rows scroll (both
  // ways) and the paging stays under them.
  // The command bar stays where it is in the tree (as in the loading state), so it is not mounted again: no wrapper.
  page: { display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0, '& > :not([data-grid])': { flexShrink: 0 } },
  // The grid is a card (border and shadow, as the header): its rows scroll, the paging stays at its foot.
  grid: {
    display: 'flex',
    flexDirection: 'column',
    flex: '0 1 auto',
    minHeight: '200px',
    marginBlock: tokens.spacingVerticalXS,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    boxShadow: tokens.shadow4,
    backgroundColor: tokens.colorNeutralBackground1,
    overflow: 'hidden',
  },
  rows: { flex: '0 1 auto', minHeight: '120px', overflow: 'auto' },
  // The table's name, small, above the view selector: the page's heading.
  heading: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  caption: {
    margin: 0,
    paddingInlineStart: tokens.spacingHorizontalM,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    fontWeight: tokens.fontWeightRegular,
    color: tokens.colorNeutralForeground3,
    whiteSpace: 'nowrap',
  },
  // The view's name, as the header's title.
  selector: {
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    fontWeight: tokens.fontWeightSemibold,
    whiteSpace: 'nowrap',
    minWidth: 0,
    paddingBlock: 0,
  },
  title: { margin: 0, paddingBlock: tokens.spacingVerticalS, fontSize: tokens.fontSizeBase500, lineHeight: tokens.lineHeightBase500, whiteSpace: 'nowrap' },
  divider: { width: '1px', height: '24px', backgroundColor: tokens.colorNeutralStroke2, marginInline: tokens.spacingHorizontalXS, flexShrink: 0 },
  search: { flex: '0 1 460px', minWidth: '200px', '& > *': { width: '100%', maxWidth: 'none' } },
  paging: { display: 'flex', alignItems: 'center', gap: '4px', padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`, borderTop: `1px solid ${tokens.colorNeutralStroke2}`, flexShrink: 0 },
  range: { marginInline: '8px' },
});

export interface EntityListPageProps {
  appId: string;
  etn: string;
  viewId?: string;
  title: string;
}

/** `pagetype=entitylist`: the table's views as a read-only grid with the view selector, quick find, sorting and paging. */
export function EntityListPage({ appId, etn, viewId, title }: EntityListPageProps) {
  const { ViewGrid } = useRenderer();
  const styles = useStyles();
  const { api } = useAuth();
  const { lcid, pagingLimit, userId, timeZone } = useUserSettings();
  const navigate = useNavigate();
  const location = useLocation();
  const size = pageSize(pagingLimit);
  const [setup, setSetup] = useState<SetupState>({ status: 'loading' });
  // Bumped after a view is saved, renamed or deleted (#101): the views are read again.
  const [reload, setReload] = useState(0);

  useEffect(() => {
    if (!api) return;
    let cancelled = false;
    setSetup({ status: 'loading' });
    loadSetup(api, etn, lcid).then(
      (loaded) => !cancelled && setSetup({ status: 'ready', ...loaded }),
      (e: unknown) => !cancelled && setSetup({ status: 'error', error: errorText(e) }),
    );
    return () => {
      cancelled = true;
    };
  }, [api, etn, lcid, reload]);

  // The user holds `prvCreate{SchemaName}` on the table (`RetrieveUserPrivileges`); the SchemaName comes with the table's setup.
  const schemaName = setup.status === 'ready' ? setup.tables[etn]?.schemaName : undefined;
  const [rights, setRights] = useState({ create: false, delete: false });
  const showNew = rights.create;
  useEffect(() => {
    if (!api) return;
    let cancelled = false;
    setRights({ create: false, delete: false });
    if (!schemaName) return;
    // Without the answer New and Delete stay hidden, as they do without the privilege.
    tableRights(api, userId, { schemaName }).then(
      (held) => !cancelled && setRights(held),
      () => undefined,
    );
    return () => {
      cancelled = true;
    };
  }, [api, userId, schemaName]);

  // The table's states and ownership (the view's record commands, ruling W8): without them the bar shows New only.
  const [meta, setMeta] = useState<{ etn: string; entity?: EntityMeta }>({ etn });
  useEffect(() => {
    if (!api) return;
    let cancelled = false;
    loadEntity(api, etn).then(
      (entity) => !cancelled && setMeta({ etn, entity }),
      () => !cancelled && setMeta({ etn }),
    );
    return () => {
      cancelled = true;
    };
  }, [api, etn]);
  const entity = meta.etn === etn ? meta.entity : undefined;
  const states = useMemo(() => (entity ? statesOf(entity) : []), [entity]);
  const owned = entity?.ownership === 'UserOwned';

  const selected = setup.status === 'ready' ? selectView(setup.views, viewId) : undefined;
  const viewKey = selected ? `${selected.personal ? 'p' : 's'}:${selected.view.id}` : '';
  const [stored, setQuery] = useState<Query>(() => freshQuery(viewKey));
  // A view change (the URL's view, or the setup loading) starts over: no search, the view's order, page 1. The stored state
  // is reset during render, so going back to an earlier view never brings back its old search, sort, page or cookie.
  if (stored.viewKey !== viewKey) setQuery(freshQuery(viewKey));
  const query = stored.viewKey === viewKey ? stored : freshQuery(viewKey);
  const quickFind = setup.status === 'ready' ? setup.views.quickFind : undefined;
  const active: ViewModel | undefined = query.search && quickFind ? quickFind : selected?.view;

  // View modes (UI-2b): only the modern look provides an overlay; without one (classic) the list is the grid.
  const overlay = useViewPresentation(selected?.view.id);
  const resolved = useResolvedModes({ api, table: etn, overlay, lcid, scope: viewKey });
  const [chosen, setChosen] = useState<{ viewKey: string; mode?: ViewMode }>({ viewKey });
  // Like the query: another view starts from its own default mode (B5).
  if (chosen.viewKey !== viewKey) setChosen({ viewKey });
  const picked = chosen.viewKey === viewKey ? chosen.mode : undefined;
  const mode: ViewMode | undefined = resolved && (picked && resolved.modes.includes(picked) ? picked : resolved.initial);
  // The month the calendar shows: today in the user's zone; another view starts over (like the mode).
  const [shown, setShown] = useState<{ viewKey: string; year: number; month: number }>();
  const today = () => wallClockOf(new Date(), timeZone);
  const month = shown?.viewKey === viewKey ? shown : { viewKey, year: today().year, month: today().month };
  // The grid and the cards page through the view; the board and the calendar read it once.
  const paged = mode === 'grid' || mode === 'cards';

  /** The view's query with quick find and order applied (no paging), or why it cannot be built. */
  const base = useMemo((): { fetchXml: string } | { error: string } | undefined => {
    if (!active) return undefined;
    try {
      let xml = active.fetchXml;
      if (query.search) xml = applyQuickFind(xml, query.search);
      if (query.sort) xml = withOrder(xml, query.sort.attribute, query.sort.descending, query.sort.linkAlias);
      return { fetchXml: xml };
    } catch (e) {
      return { error: errorText(e) };
    }
  }, [active, query.search, query.sort]);

  /** The FetchXML of the current request (count, total, order and quick find applied), or why it cannot be built. */
  const request = useMemo((): { fetchXml: string } | { error: string } | undefined => {
    if (!base || 'error' in base) return base;
    try {
      return { fetchXml: withTotalCount(withPaging(base.fetchXml, 1, size)) };
    } catch (e) {
      return { error: errorText(e) };
    }
  }, [base, size]);

  const table = setup.status === 'ready' ? setup.tables[etn] : undefined;
  const cookie = query.cookies[query.page - 1];
  const requestKey = request && 'fetchXml' in request ? `${query.page}\n${cookie ?? ''}\n${request.fetchXml}` : '';
  const [result, setResult] = useState<{ key: string; page?: ViewPage; error?: string }>({ key: '' });
  // Bumped after a command ran on the selected records: the page is read again (the rows shown meanwhile stay).
  const [refresh, setRefresh] = useState(0);

  useEffect(() => {
    if (!api || !table || !paged || !request || !('fetchXml' in request)) return;
    let cancelled = false;
    const abort = new AbortController();
    runView(api, table.entitySetName, request.fetchXml, query.page, cookie, abort.signal).then(
      (page) => !cancelled && setResult({ key: requestKey, page }),
      (e: unknown) => !cancelled && setResult({ key: requestKey, error: errorText(e) }),
    );
    return () => {
      cancelled = true;
      abort.abort();
    };
    // requestKey stands for the request, its page and its cookie.
  }, [api, table, requestKey, paged, refresh]);

  const columns = useMemo(() => (active ? active.columns.filter((c) => !c.isHidden) : []), [active]);
  const headers = useMemo(
    () => (setup.status === 'ready' && active ? columnHeaders(columns, active.fetchXml, setup.tables) : {}),
    [setup, active, columns],
  );
  const cellKinds = useMemo(
    () => (setup.status === 'ready' && active ? columnKinds(columns, active.fetchXml, setup.tables) : {}),
    [setup, active, columns],
  );

  // The selected records (grid and cards, ruling W8): another view, page, sort, quick find or mode starts with none.
  const scope = `${viewKey}\n${query.search}\n${JSON.stringify(query.sort ?? null)}\n${query.page}\n${mode ?? ''}`;
  const [chosenIds, setChosenIds] = useState<{ scope: string; ids: ReadonlySet<string> }>({ scope, ids: new Set() });
  // Reset during render (like the query): coming back to an earlier page does not bring back its old selection.
  if (chosenIds.scope !== scope) setChosenIds({ scope, ids: new Set() });
  const selectedIds = useMemo(() => (chosenIds.scope === scope ? chosenIds.ids : new Set<string>()), [chosenIds, scope]);
  const onSelect = useCallback((ids: Set<string>) => setChosenIds({ scope, ids }), [scope]);
  const selection: ViewSelection = useMemo(() => ({ selected: selectedIds, onChange: onSelect }), [selectedIds, onSelect]);
  const queueTable = table ? { logicalName: table.logicalName, primaryId: table.primaryId, isValidForQueue: table.isValidForQueue } : undefined;
  const selectable = paged && (states.length > 1 || owned || rights.delete || queueTable?.isValidForQueue === true || etn === 'queueitem');
  // The statecode of the selection, read with one request each time it changes or the records changed.
  const hasState = states.length > 1;
  const statesKey = `${[...selectedIds].sort().join(',')}\n${refresh}`;
  const [readState, setReadState] = useState<{ key: string; states: Record<string, number> }>({ key: '', states: {} });
  useEffect(() => {
    if (!api || !table || !hasState || selectedIds.size === 0) return;
    let cancelled = false;
    readStates(api, table.entitySetName, table.primaryId, [...selectedIds]).then(
      (read) => !cancelled && setReadState({ key: statesKey, states: read }),
      // Unread, the states stay unknown: Activate and Deactivate stay disabled.
      () => undefined,
    );
    return () => {
      cancelled = true;
    };
    // statesKey stands for the selection and the reloads.
  }, [api, table, hasState, statesKey]);
  const bulk = useBulkRun();
  // The scope the last result belongs to: the result goes away with the selection.
  const [bulkScope, setBulkScope] = useState<string>();
  if (bulkScope !== undefined && bulkScope !== scope) setBulkScope(undefined);

  // The list the record forms return to.
  const listState = { returnTo: location.pathname + location.search };
  const onNew = showNew ? () => navigate(mainAspxUrl({ appId, pageType: 'entityrecord', etn }), { state: listState }) : undefined;
  // Back to the page the user came from in the app; nothing when the list was opened first (a link or a reload).
  const back = location.key !== 'default' && (
    <>
      <Button appearance="subtle" icon={<BackIcon />} aria-label={t('apps.views.back')} title={t('apps.views.back')} onClick={() => navigate(-1)} />
      <span className={styles.divider} />
    </>
  );
  /** The start of the header: back, then the table's name (the page's heading) over the view selector. */
  const leading = (selector?: ReactNode) => (
    <>
      {back}
      <div className={styles.heading}>
        {selector ? (
          <>
            <h1 className={styles.caption}>{title}</h1>
            {selector}
          </>
        ) : (
          <h1 className={styles.title}>{title}</h1>
        )}
      </div>
    </>
  );
  /** The header before the view is shown: the table's name and New only. */
  const newOnly = <ViewCommandBar leading={leading()} onNew={onNew} states={[]} owned={false} onRun={async () => undefined} />;
  const error = (detail: string) => (
    <MessageBar intent="error">
      <MessageBarBody>{t('apps.views.error', { detail })}</MessageBarBody>
    </MessageBar>
  );

  if (setup.status === 'loading') {
    return (
      <section>
        {newOnly}
        <Spinner label={t('apps.views.loading')} />
      </section>
    );
  }
  if (setup.status === 'error') {
    return (
      <section>
        {newOnly}
        {error(setup.error)}
      </section>
    );
  }
  if (!selected || !active) {
    return (
      <section>
        {newOnly}
        <Body1 block>{t('apps.views.none')}</Body1>
      </section>
    );
  }

  const update = (patch: Partial<Query>) => setQuery({ ...query, ...patch });
  const current = result.key === requestKey ? result : undefined;
  const page = current?.page;
  const rows: GridRow[] = (page?.rows ?? []).map((row, index) => {
    const value = table ? row[table.primaryId] : undefined;
    const id = typeof value === 'string' && value ? value : undefined;
    return {
      key: id ?? `row-${index}`,
      id,
      cells: Object.fromEntries(columns.map((c) => [c.name, cellText(row, c)])),
      values: row,
    };
  });
  const sort = query.sort ?? active.order.find((o) => !o.alias);
  // The records' names, for the failures of a command (the primary name, else the first column).
  const names: Record<string, string> = {};
  for (const row of rows) {
    const name = table?.primaryName ? row.values?.[table.primaryName] : undefined;
    if (row.id) names[row.id] = typeof name === 'string' && name ? name : (columns[0] ? row.cells[columns[0].name] : undefined) || row.id;
  }
  // A selected record the reloaded page no longer lists (e.g. deactivated in a view of active records) drops out.
  const selectedRecords = [...selectedIds].filter((id) => id in names).map((id) => ({
    id,
    ...(readState.key === statesKey && readState.states[id.toLowerCase()] !== undefined ? { statecode: readState.states[id.toLowerCase()] } : {}),
  }));
  const runOnSelection = async (op: (id: string) => Promise<void>) => {
    setBulkScope(scope);
    await bulk.run(
      selectedRecords.map((r) => r.id),
      names,
      op,
    );
    setRefresh((n) => n + 1);
  };
  const outcome = bulk.result && bulkScope === scope ? bulk.result : undefined;
  const first = (query.page - 1) * size + 1;
  const last = first + rows.length - 1;

  return (
    <section className={styles.page}>
      <ViewCommandBar
        api={api}
        set={table?.entitySetName}
        onNew={onNew}
        selected={paged && selectable ? selectedRecords : undefined}
        states={states}
        owned={owned}
        canDelete={rights.delete}
        queue={queueTable}
        onRefresh={paged ? () => setRefresh((n) => n + 1) : undefined}
        onRun={runOnSelection}
        leading={leading(
          <ViewSelector
            className={styles.selector}
            system={setup.views.system}
            personal={setup.views.personal}
            selected={selected}
            onSelect={(choice) => navigate(viewUrl(appId, etn, { id: choice.view.id, personal: choice.personal }))}
          />,
        )}
        modes={resolved && mode ? <ViewModeSwitcher modes={resolved.modes} mode={mode} onChange={(next) => setChosen({ viewKey, mode: next })} /> : undefined}
        trailing={
          api && (
          <ViewCommands
            api={api}
            table={etn}
            selected={selected}
            userId={userId}
            fetchXml={savedFetchXml(selected.view.fetchXml, query.sort)}
            onCreated={(id) => {
              setReload((n) => n + 1);
              navigate(viewUrl(appId, etn, { id, personal: true }));
            }}
            onChanged={() => setReload((n) => n + 1)}
            onDeleted={() => {
              setReload((n) => n + 1);
              navigate(mainAspxUrl({ appId, pageType: 'entitylist', etn }));
            }}
          />
          )
        }
        below={
          quickFind && (
            <div className={styles.search}>
              <QuickFind key={viewKey} onSearch={(search) => update({ search, sort: undefined, page: 1, cookies: [undefined] })} />
            </div>
          )
        }
      />
      {request && 'error' in request && error(request.error)}
      {outcome && (
        <MessageBar intent={outcome.failed.length === 0 ? 'success' : outcome.done === 0 ? 'error' : 'warning'} layout="multiline">
          <MessageBarBody>
            <div>{t('apps.views.bulk.result', { done: outcome.done, total: outcome.total })}</div>
            {outcome.failed.map((f) => (
              <div key={f.id}>{t('apps.views.bulk.failure', { name: f.name, message: f.message })}</div>
            ))}
          </MessageBarBody>
          <MessageBarActions
            containerAction={<Button appearance="transparent" aria-label={t('common.close')} icon={<DismissIcon />} onClick={bulk.clear} />}
          />
        </MessageBar>
      )}
      {!resolved && <Spinner label={t('apps.views.loading')} />}
      {resolved && !paged && base && 'fetchXml' in base && (
        <ModeView
          mode={mode as 'kanban' | 'calendar'}
          resolved={resolved}
          api={api}
          table={etn}
          entitySet={table?.entitySetName}
          fetchXml={base.fetchXml}
          label={active.name}
          columns={columns}
          headers={headers}
          primaryId={table?.primaryId}
          calendar={{ year: month.year, month: month.month, zone: timeZone, lcid, onNavigate: (year, m) => setShown({ viewKey, year, month: m }) }}
          onMove={async (id, body) => {
            if (!api || !table) throw new Error(t('apps.views.kanban.moveFailedNoTable'));
            // `If-Match: *` makes the PATCH an update only: a record deleted meanwhile answers 404 instead of being upserted.
            await api.patch(`${table.entitySetName}(${id})`, body, { 'If-Match': '*' });
          }}
          onOpen={(id) => navigate(mainAspxUrl({ appId, pageType: 'entityrecord', etn, id }), { state: listState })}
        />
      )}
      {paged && current?.error && error(current.error)}
      {paged && request && 'fetchXml' in request && !current && <Spinner label={t('apps.views.loading')} />}
      {paged && page && (
        <div className={styles.grid} data-grid="">
          <div className={styles.rows} data-rows="">
            <ViewGrid
              label={active.name}
              columns={columns}
              headers={headers}
              kinds={cellKinds}
              rows={rows}
              layout={mode === 'cards' ? 'cards' : undefined}
              table={etn}
              wrap={overlay?.wrap}
              stickyHeader
              selection={selectable ? selection : undefined}
              sort={sort ? { attribute: sort.attribute, descending: sort.descending, linkAlias: sort.linkAlias } : undefined}
              onSort={(next) => update({ sort: next, page: 1, cookies: [undefined] })}
              onOpen={(id) => navigate(mainAspxUrl({ appId, pageType: 'entityrecord', etn, id }), { state: listState })}
            />
            {rows.length === 0 && <Body1 block>{t('apps.views.empty')}</Body1>}
          </div>
          <div className={styles.paging} role="group" aria-label={t('apps.views.paging')}>
            <Button
              appearance="subtle"
              aria-label={t('apps.views.firstPage')}
              disabled={query.page === 1}
              icon={<FirstPageIcon />}
              onClick={() => update({ page: 1 })}
            />
            <Button
              appearance="subtle"
              aria-label={t('apps.views.previousPage')}
              disabled={query.page === 1}
              icon={<PreviousPageIcon />}
              onClick={() => update({ page: query.page - 1 })}
            />
            {rows.length > 0 && (
              <Text className={styles.range}>
                {page.total !== undefined
                  ? t(page.totalMore ? 'apps.views.rangeTotalMore' : 'apps.views.rangeTotal', { first, last, total: page.total })
                  : t('apps.views.range', { first, last })}
              </Text>
            )}
            <Button
              appearance="subtle"
              aria-label={t('apps.views.nextPage')}
              disabled={!page.more}
              icon={<NextPageIcon />}
              onClick={() => update({ page: query.page + 1, cookies: [...query.cookies.slice(0, query.page), page.cookie] })}
            />
          </div>
        </div>
      )}
    </section>
  );
}
