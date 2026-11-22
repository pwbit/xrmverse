import { Button, makeStyles, MessageBar, MessageBarBody, Spinner, Text, tokens, Toolbar, ToolbarButton } from '@fluentui/react-components';
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { useRecordOpener } from '../../../apps/record/recordOpener';
import { mainAspxUrl } from '../../../apps/mainAspx';
import { heldPrivileges } from '../../../apps/record/privileges';
import { subgridFetchXml, type RelationshipMeta } from '../../../apps/record/subgridQuery';
import { cellText, runView, type ViewPage } from '../../../apps/runView';
import { useLoadedUserSettings } from '../../../apps/userSettings';
import { wallClockOf } from '../../../apps/timeZones';
import { ModeView } from '../../../apps/views/ModeView';
import { useResolvedModes } from '../../../apps/views/useResolvedModes';
import { ViewModeSwitcher } from '../../../apps/views/ViewModeSwitcher';
import type { SubgridControl as SubgridModel } from '../../../model/form';
import { linkEntities, parseFetch, withOrder, withPaging } from '../../../parsers/fetchXml';
import { normaliseId, type ViewMode, type ViewPresentation } from '../../../parsers/presentationJson';
// The cards of the modern look (only a modern overlay asks for them); not through useRenderer, whose module imports this one.
import { CardList } from '../../modern/CardList';
import type { XrmApi } from '../../../shell/api/client';
import { useAuth } from '../../../shell/auth/AuthProvider';
import { t } from '../../../shell/i18n/t';
import { columnKinds } from '../../../apps/columnHeaders';
import { ViewGrid, type GridRow, type GridSort } from '../ViewGrid';
import { useChoiceCells } from '../../modern/ChoiceBadge';
import { useModernLook, useViewPresentation } from '../../modern/PresentationContext';
import { cachedRead, type ControlEnv } from './controlEnv';
import { AddExistingDialog } from './AddExistingDialog';
import { loadSubgridSetup, UnknownViewError, type SubgridSetup } from './subgridSetup';
import { PcfDatasetControl, type DataSetSubgrid } from '../../../pcf/PcfDatasetControl';
import { addExisting, addExistingFilter, subgridCommandsAllowed, subgridNew, type SubgridParent } from '../../../apps/record/subgridCommands';
import { AddExistingIcon, NewIcon, NextPageIcon, PreviousPageIcon } from '../../../shell/icons';
import { useSurfaceStyles } from '../../../shell/layout/surface';

const errorText = (e: unknown) => (e instanceof Error ? e.message : String(e));

const useStyles = makeStyles({
  title: { display: 'block', marginBlockEnd: tokens.spacingVerticalXXS },
  titleRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalS,
    marginBlockEnd: tokens.spacingVerticalXS,
  },
  titleText: { minWidth: 0 },
  commands: { display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: tokens.spacingHorizontalS, marginInlineStart: 'auto' },
  paging: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    marginBlockStart: tokens.spacingVerticalXS,
  },
  range: { marginInline: tokens.spacingHorizontalS },
});

/**
 * What a subgrid needs before it can query: its setup and the FetchXML limited to the record. Without a parent record (a
 * dashboard) there is no relationship and the FetchXML is the view's, with paging only.
 */
interface Setup extends SubgridSetup {
  fetchXml: string;
  /** The same query without paging: what the board and the calendar read (up to BOARD_LIMIT records). */
  baseFetchXml: string;
  relationship?: RelationshipMeta;
}

async function loadSetup(api: XrmApi, control: SubgridModel, recordId: string, env: ControlEnv): Promise<Setup> {
  const setup = await loadSubgridSetup(api, control, env);
  const relationship = setup.relationship!;
  const baseFetchXml = subgridFetchXml(setup.view.fetchXml, relationship, recordId, setup.tables[setup.target]?.primaryId);
  return { ...setup, fetchXml: withPaging(baseFetchXml, 1, control.recordsPerPage), baseFetchXml, relationship };
}

/** The setup of a subgrid with no parent record: the whole view, a page of `RecordsPerPage` at a time. */
async function loadUnparentedSetup(api: XrmApi, control: SubgridModel, env: ControlEnv): Promise<Setup> {
  const setup = await loadSubgridSetup(api, { ...control, relationship: '' }, env, { relationshipOptional: true });
  return { view: setup.view, tables: setup.tables, target: setup.target, fetchXml: withPaging(setup.view.fetchXml, 1, control.recordsPerPage), baseFetchXml: setup.view.fetchXml };
}

const hasDataSet = (control: SubgridModel): control is DataSetSubgrid => !!control.customControl?.dataSet;

/** The key of a subgrid's entry in the form overlay's `subgrids`: its control id, lowercase and trimmed. */
const entryKey = (control: SubgridModel) => control.id.trim().toLowerCase();

/**
 * The overlay's modes for this subgrid (the modern look): only on the form the entries belong to (ruling S1), never for a
 * related-tables item; undefined, the subgrid is a grid.
 */
function subgridEntry(control: SubgridModel, env: ControlEnv): ViewPresentation | undefined {
  const modes = env.subgridModes;
  if (!modes || control.related || !env.formId || normaliseId(env.formId) !== normaliseId(modes.formId)) return undefined;
  const key = entryKey(control);
  return Object.hasOwn(modes.entries, key) ? modes.entries[key] : undefined;
}

export interface SubgridControlProps {
  control: SubgridModel;
  env: ControlEnv;
  reload?: number;
  label?: string;
  /** The title shown above the grid, on one line with its commands; none when the form hides the label. */
  title?: string;
  /**
   * The record the rows relate to: the form's record when absent. `null` (a dashboard) lists the view with no relationship
   * filter, a page of `RecordsPerPage` at a time, without New or Add existing; a dataset control is ready at once.
   */
  parent?: SubgridParent | null;
}

/**
 * The subgrid of a form: the rows of a view related to this record, in UI-1a's grid, or in the dataset PCF control the
 * FormXml names (UI-1e), with the grid as its fallback.
 */
export function SubgridControl({ control, env, reload = 0, label, title, parent }: SubgridControlProps) {
  const styles = useStyles();
  // Without a parent record (a dashboard) the subgrid is a grid.
  const entry = parent === null ? undefined : subgridEntry(control, env);
  const dataSet = hasDataSet(control);
  const ignored = dataSet && !!entry;
  const controlName = control.customControl?.name;
  useEffect(() => {
    if (ignored) {
      console.warn(
        '[xrmverse presentation]',
        `The subgrids entry "${entryKey(control)}" is ignored: the subgrid is replaced by the dataset control ${controlName}.`,
      );
    }
    // The control's id is fixed for this cell.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ignored, controlName]);
  if (dataSet) {
    return (
      <>
      {title && (
        <Text weight="semibold" className={styles.title}>
          {title}
        </Text>
      )}
      <PcfDatasetControl
        control={control}
        env={env}
        reload={reload}
        label={label}
        parent={parent}
        fallback={<ClassicSubgrid control={control} env={env} reload={reload} parent={parent} />}
      />
      </>
    );
  }
  return <ClassicSubgrid control={control} env={env} reload={reload} parent={parent} title={title} entry={entry} />;
}

/** The subgrid's title row: the title at the start, the commands (if any) at the end. */
function TitleRow({ title, children }: { title?: string; children?: ReactNode }) {
  const styles = useStyles();
  if (!title && !children) return null;
  return (
    <div className={styles.titleRow}>
      {title ? (
        <Text weight="semibold" className={styles.titleText}>
          {title}
        </Text>
      ) : (
        <span />
      )}
      {children}
    </div>
  );
}

function ClassicSubgrid({
  control,
  env,
  reload = 0,
  parent,
  title,
  entry,
}: {
  control: SubgridModel;
  env: ControlEnv;
  reload?: number;
  parent?: SubgridParent | null;
  title?: string;
  /** The overlay's modes for this subgrid; absent, the grid only. */
  entry?: ViewPresentation;
}) {
  const { api } = useAuth();
  const unparented = parent === null;
  const recordId = parent ? parent.id : env.ctx.fieldContext.record?.id;
  const [state, setState] = useState<{
    key: string;
    setup?: Setup;
    /** The notice in place of the grid. */
    message?: string;
  }>({ key: '' });
  const key = [unparented ? '(none)' : recordId, control.relationship, control.targetEntity, control.viewId, control.recordsPerPage, env.lcid, env.refreshKey ?? 0, reload].join('|');

  useEffect(() => {
    if (!api || (!unparented && !recordId)) return;
    let cancelled = false;
    (unparented ? loadUnparentedSetup(api, control, env) : loadSetup(api, control, recordId!, env)).then(
      (setup) => !cancelled && setState({ key, setup }),
      (e: unknown) =>
        !cancelled &&
        setState({
          key,
          // A dashboard names a view that is not imported as not found (Review Focus 3).
          message:
            unparented && e instanceof UnknownViewError ? t('dashboard.viewNotFound', { id: e.viewId }) : t('record.subgrid.cannotShow', { reason: errorText(e) }),
        }),
    );
    return () => {
      cancelled = true;
    };
    // `key` stands for the record and the control's parameters.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, key]);

  const titled = (body: ReactNode) => (
    <>
      <TitleRow title={title} />
      {body}
    </>
  );
  if (!unparented && !recordId) return titled(<Notice intent="info" text={t('record.subgrid.saveFirst')} />);
  const current = state.key === key ? state : undefined;
  if (current?.message !== undefined) return titled(<Notice intent="warning" text={current.message} />);
  if (!current?.setup) return titled(<Spinner size="tiny" label={t('record.subgrid.loading')} />);
  // A dashboard shows one notice for every picker (ruling F4), not one per subgrid.
  return <SubgridGrid key={key} setup={current.setup} env={env} parent={parent} title={title} viewPickerNote={control.enableViewPicker && !unparented} entry={entry} />;
}

function Notice({ intent, text }: { intent: 'info' | 'warning'; text: string }) {
  return (
    <MessageBar intent={intent}>
      <MessageBarBody>{text}</MessageBarBody>
    </MessageBar>
  );
}

interface Query {
  sort?: GridSort;
  page: number;
  /** `cookies[n]` is the paging cookie that requests page n + 1. */
  cookies: (string | undefined)[];
}

function SubgridGrid({
  setup,
  env,
  parent: given,
  title,
  viewPickerNote,
  entry,
}: {
  setup: Setup;
  env: ControlEnv;
  parent?: SubgridParent | null;
  title?: string;
  viewPickerNote: boolean;
  entry?: ViewPresentation;
}) {
  const surface = useSurfaceStyles();
  const styles = useStyles();
  const { api } = useAuth();
  // Outside the app's settings (a form rendered on its own) dates are shown in UTC.
  const timeZone = useLoadedUserSettings()?.timeZone ?? 'Etc/UTC';
  const navigate = useNavigate();
  const location = useLocation();
  const { view, tables, target, relationship } = setup;
  // The columns that wrap are the view's (the overlay's `views` entry), not the subgrid entry's.
  const wrap = useViewPresentation(view.id)?.wrap;
  const table = tables[target]!;
  // View modes (the modern form's overlay): the kinds of the entry's columns are read once per form and table.
  const resolved = useResolvedModes({
    api,
    table: target,
    overlay: entry,
    lcid: env.lcid,
    subject: `the table ${target} of the subgrid`,
    read: (key, load) => cachedRead(env, key, load),
  });
  // The mode the user picked (ruling S2): component state, from the entry's default; a new record and Refresh remount the subgrid.
  const [picked, setPicked] = useState<ViewMode>();
  const mode: ViewMode | undefined = resolved && (picked && resolved.modes.includes(picked) ? picked : resolved.initial);
  // The grid and the cards page through the rows; the board and the calendar read them once.
  const paged = mode === 'grid' || mode === 'cards';
  // The calendar starts on today's month in the user's zone (read once), then follows the user's navigation.
  const [month, setShown] = useState<{ year: number; month: number }>(() => wallClockOf(new Date(), timeZone));
  // New and Add existing (#118, ruling R5), shown once the user's privileges are known.
  const [allowed, setAllowed] = useState({ canNew: false, canAddExisting: false });
  const [adding, setAdding] = useState(false);
  const [reload, setReload] = useState(0);
  // Without a parent record nothing can be related to it: no New or Add existing.
  const commands = !!relationship && given !== null;
  useEffect(() => {
    if (!api || !commands) return;
    let cancelled = false;
    cachedRead(env, 'privileges', () => heldPrivileges(api)).then(
      (held) => !cancelled && setAllowed(subgridCommandsAllowed(held, table.schemaName, env.ctx.entity.schemaName)),
      () => undefined,
    );
    return () => {
      cancelled = true;
    };
  }, [api, env, table, commands]);
  const primaryName = env.ctx.entity.primaryNameAttribute;
  const named = primaryName ? (env.state.record ?? env.ctx.record)?.[primaryName] : undefined;
  // No parent record (a dashboard): nothing to relate rows to, and no stand-in record.
  const parent: SubgridParent | undefined =
    given === null
      ? undefined
      : (given ?? {
          table: env.ctx.entity.logicalName,
          entitySetName: env.ctx.entity.entitySetName ?? '',
          id: env.ctx.fieldContext.record?.id ?? '',
          name: typeof named === 'string' ? named : '',
        });
  const recordId = parent?.id ?? '';
  const child = { table: target, entitySetName: table.entitySetName };
  const tableLabel = table.displayName ?? target;
  const here = location.pathname + location.search;
  // On a form in an app, rows and New open in a side pane (or the page, per xrmverse_LookupOpenMode); the grid reloads when it
  // closes. A dashboard's grid (no parent record) opens the page.
  const opener = useRecordOpener();
  const openRecord = given === null ? null : opener;
  const refresh = () => setReload((r) => r + 1);
  const openNew = (event: React.MouseEvent<HTMLElement>) => {
    if (!relationship || !parent) return;
    const { extraqs, associate } = subgridNew(relationship, parent, child);
    // A one-to-many's create form fills the columns the relationship's maps name from this record (#143, InitializeFrom).
    const createFrom = 'referencingAttribute' in relationship ? { table: parent.table, set: parent.entitySetName, id: parent.id, name: parent.name } : undefined;
    if (openRecord) {
      void openRecord(
        { etn: target, extraqs, state: { fromSubgrid: { associate, createFrom } }, returnTo: here },
        { event, anchor: event.currentTarget },
      ).then(refresh);
      return;
    }
    navigate(mainAspxUrl({ appId: env.appId, pageType: 'entityrecord', etn: target, extraqs }), {
      // After its first save the new row's form comes back here (RecordPage).
      state: { returnTo: here, fromSubgrid: { associate, createFrom } },
    });
  };
  const [query, setQuery] = useState<Query>({
    page: 1,
    cookies: [undefined],
  });
  const sort = query.sort ?? view.order.find((o) => !o.alias);
  const fetchXml = useMemo(
    () => (query.sort ? withOrder(setup.fetchXml, query.sort.attribute, query.sort.descending) : setup.fetchXml),
    [setup.fetchXml, query.sort],
  );
  const baseFetchXml = useMemo(
    () => (query.sort ? withOrder(setup.baseFetchXml, query.sort.attribute, query.sort.descending) : setup.baseFetchXml),
    [setup.baseFetchXml, query.sort],
  );
  const cookie = query.cookies[query.page - 1];
  const requestKey = `${query.page}\n${cookie ?? ''}\n${fetchXml}\n${reload}`;
  const [result, setResult] = useState<{
    key: string;
    page?: ViewPage;
    error?: string;
  }>({ key: '' });

  useEffect(() => {
    if (!api || !paged) return;
    let cancelled = false;
    const abort = new AbortController();
    runView(api, table.entitySetName, fetchXml, query.page, cookie, abort.signal).then(
      (page) => !cancelled && setResult({ key: requestKey, page }),
      (e: unknown) => !cancelled && setResult({ key: requestKey, error: errorText(e) }),
    );
    return () => {
      cancelled = true;
      abort.abort();
    };
    // requestKey stands for the request, its page and its cookie.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, table, requestKey, paged]);

  const columns = useMemo(() => view.columns.filter((c) => !c.isHidden), [view]);
  const headers = useMemo(() => {
    const links = linkEntities(view.fetchXml);
    return Object.fromEntries(columns.map((c) => [c.name, tables[c.alias ? (links[c.alias] ?? '') : target]?.labels[c.attribute] ?? c.name]));
  }, [view, columns, tables, target]);
  // The modern look shows coloured choice values as badges; the classic look keeps the text.
  const choiceCells = useChoiceCells(useModernLook() ? target : undefined, columns);

  const kinds = useMemo(() => columnKinds(columns, view.fetchXml, tables), [columns, view, tables]);
  // A row of the grid, the cards, the board or the calendar: in a side pane on a form (the grid reloads when it closes), else the page.
  const openRow = (id: string, event?: React.MouseEvent<HTMLElement>) =>
    openRecord
      ? void openRecord({ etn: target, id, returnTo: here }, { event, anchor: event?.currentTarget as Element | undefined }).then(refresh)
      : navigate(
          mainAspxUrl({
            appId: env.appId,
            pageType: 'entityrecord',
            etn: target,
            id,
          }),
          // Save & Close and Delete on the related record come back to this form, as from a list (EntityListPage).
          { state: { returnTo: here } },
        );

  const current = result.key === requestKey ? result : undefined;
  if (!resolved || !mode) return <Spinner size="tiny" label={t('record.subgrid.loading')} />;
  if (paged && current?.error !== undefined) return <Notice intent="warning" text={t('record.subgrid.cannotShow', { reason: current.error })} />;
  if (paged && !current?.page) return <Spinner size="tiny" label={t('record.subgrid.loading')} />;
  const page = current?.page;
  const rows: GridRow[] = (page?.rows ?? []).map((row, index) => {
    const value = row[table.primaryId];
    const id = typeof value === 'string' && value ? value : undefined;
    return {
      key: id ?? `row-${index}`,
      id,
      cells: Object.fromEntries(columns.map((c) => [c.name, cellText(row, c)])),
      values: row,
    };
  });
  const size = parseFetch(fetchXml).count ?? rows.length;
  const first = (query.page - 1) * size + 1;
  const pages = query.page > 1 || !!page?.more;
  const switcher = resolved.modes.length > 1;
  const sortShown = sort ? { attribute: sort.attribute, descending: sort.descending } : undefined;
  const onSort = (next: GridSort) => setQuery({ sort: next, page: 1, cookies: [undefined] });

  return (
    <div data-testid="subgrid">
      <TitleRow title={title}>
        {(switcher || (commands && (allowed.canNew || allowed.canAddExisting))) && (
          <div className={styles.commands}>
            <ViewModeSwitcher modes={resolved.modes} mode={mode} onChange={setPicked} />
            {commands && (allowed.canNew || allowed.canAddExisting) && (
              // The same command bar as the form's, on the title's line, at the end.
              <Toolbar size="small" className={surface.commandBar} aria-label={t('record.subgrid.commands', { table: tableLabel })}>
                {allowed.canNew && (
                  <ToolbarButton icon={<NewIcon />} onClick={openNew}>
                    {t('record.subgrid.new', { table: tableLabel })}
                  </ToolbarButton>
                )}
                {allowed.canAddExisting && table.primaryName && (
                  <ToolbarButton icon={<AddExistingIcon />} onClick={() => setAdding(true)}>
                    {t('record.subgrid.addExisting', { table: tableLabel })}
                  </ToolbarButton>
                )}
              </Toolbar>
            )}
          </div>
        )}
      </TitleRow>
      {adding && relationship && parent && table.primaryName && (
        <AddExistingDialog
          tableLabel={tableLabel}
          entitySetName={table.entitySetName}
          primaryId={table.primaryId}
          primaryName={table.primaryName}
          filter={addExistingFilter(relationship, recordId)}
          onClose={() => setAdding(false)}
          onAdd={async (ids) => {
            await addExisting(api!, relationship, parent, child, ids);
            setAdding(false);
            setQuery({ ...query, page: 1, cookies: [undefined] });
            setReload((r) => r + 1);
          }}
        />
      )}
      {!paged && (
        <ModeView
          // Rows added from Add existing: the board or the calendar reads its records again.
          key={reload}
          mode={mode as 'kanban' | 'calendar'}
          resolved={resolved}
          api={api}
          table={target}
          entitySet={table.entitySetName}
          fetchXml={baseFetchXml}
          label={view.name}
          columns={columns}
          headers={headers}
          primaryId={table.primaryId}
          // A subgrid has no quick find: the grid pages through the rest.
          limitNotice={t('record.subgrid.modesLimit')}
          calendar={{ year: month.year, month: month.month, zone: timeZone, lcid: env.lcid, onNavigate: (year, m) => setShown({ year, month: m }) }}
          onMove={async (id, body) => {
            // `If-Match: *` makes the PATCH an update only: a record deleted meanwhile answers 404 instead of being upserted.
            await api!.patch(`${table.entitySetName}(${id})`, body, { 'If-Match': '*' });
          }}
          onOpen={openRow}
        />
      )}
      {paged && mode === 'cards' && (
        <CardList label={view.name} table={target} renderCellContent={choiceCells} columns={columns} headers={headers} rows={rows} layout="cards" sort={sortShown} onSort={onSort} onOpen={openRow} />
      )}
      {paged && mode === 'grid' && <ViewGrid label={view.name} table={target} renderCellContent={choiceCells} columns={columns} headers={headers} kinds={kinds} rows={rows} wrap={wrap} sort={sortShown} onSort={onSort} onOpen={openRow} />}
      {paged && rows.length === 0 && <Text block>{t('apps.views.empty')}</Text>}
      {paged && page && pages && (
        <div className={styles.paging} role="group" aria-label={t('apps.views.paging')}>
          <Button
            appearance="subtle"
            size="small"
            aria-label={t('apps.views.previousPage')}
            disabled={query.page === 1}
            icon={<PreviousPageIcon />}
            onClick={() => setQuery({ ...query, page: query.page - 1 })}
          />
          {rows.length > 0 && (
            <Text className={styles.range}>
              {t('apps.views.range', {
                first,
                last: first + rows.length - 1,
              })}
            </Text>
          )}
          <Button
            appearance="subtle"
            size="small"
            aria-label={t('apps.views.nextPage')}
            disabled={!page.more}
            icon={<NextPageIcon />}
            onClick={() =>
              setQuery({
                ...query,
                page: query.page + 1,
                cookies: [...query.cookies.slice(0, query.page), page.cookie],
              })
            }
          />
        </div>
      )}
      {viewPickerNote && (
        <Text block size={200}>
          {t('record.subgrid.viewPickerNote')}
        </Text>
      )}
    </div>
  );
}
