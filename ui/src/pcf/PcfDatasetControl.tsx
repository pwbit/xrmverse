import { makeStyles, MessageBar, MessageBarBody, Spinner } from '@fluentui/react-components';
import { useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import type { AttributeMeta } from '../admin/tables/metadata';
import { loadEntity } from '../admin/tables/tableApi';
import { DisplayZoneContext } from '../apps/displayZone';
import { useLoadedUserSettings } from '../apps/userSettings';
import type { SubgridParent } from '../apps/record/subgridCommands';
import { subgridFetchXml } from '../apps/record/subgridQuery';
import { runView } from '../apps/runView';
import { XrmHostContext } from '../client-api/xrm';
import type { CustomControlDataSet, DataSetControlRef, SubgridControl as SubgridModel } from '../model/form';
import { withOrder, withPaging } from '../parsers/fetchXml';
import { cachedRead, type ControlEnv } from '../renderers/classic/form/controlEnv';
import { loadSubgridSetup, type SubgridSetup } from '../renderers/classic/form/subgridSetup';
import { useActiveTheme } from '../renderers/ThemeScope';
import { useAuth } from '../shell/auth/AuthProvider';
import { t, uiLocale } from '../shell/i18n/t';
import { buildContext, literalParameter, updateContext, type PcfContext, type PcfParameter, type PcfView } from './context';
import { createDataSet, type EntityReference, type PcfDataSet, type SortStatus } from './dataset';
import { hostInput, navigationOf, PcfBoundary, reasonOf, resizeTracker, startControl, userPrivileges, type PcfRunning, type Privileges } from './host';
import { loadControl, manifestFetcher, type PcfConstructor, type PcfControl } from './loader';
import type { PcfManifest } from './manifest';
import { loadStrings } from './resx';

/**
 * A form subgrid that a dataset PCF control renders (ruling E7). The control gets the subgrid's view as a {@link PcfDataSet}
 * under its manifest data-set name: `init` once, `updateView` after each load of the data-set (`updatedProperties` names the
 * data-set), after a change of the mode or the page, after a resize (`layout`) and on `requestRender`. On a new record the
 * data-set stays empty and queries nothing until the first save gives the relationship its parent id. With no parent record
 * (`parent` null, a dashboard) the relationship is not used and the data-set is ready at once. When the control cannot run,
 * the classic grid (`fallback`) takes its place and the form shows why, as for a field control (a dashboard, which has no
 * form notifications, shows it above the grid).
 */

const useStyles = makeStyles({
  host: { minWidth: 0 },
});

export type DataSetSubgrid = SubgridModel & { customControl: DataSetControlRef & { dataSet: CustomControlDataSet } };

interface Ready {
  manifest: PcfManifest;
  ctor: PcfConstructor;
  strings: Record<string, string>;
  privileges: Privileges;
  /** The manifest's name of the data-set: the key of `context.parameters`. */
  dataSetName: string;
  /** The manifest's other properties, as literals. */
  inputs: Record<string, PcfParameter>;
  setup: SubgridSetup;
  /** The target table's columns. */
  attributes: AttributeMeta[];
  pageSize: number;
}

type Phase = { status: 'loading' } | ({ status: 'ready' } & Ready) | { status: 'failed'; reason: string };

/** The manifest data-set the FormXml's `<data-set name>` names; throws the reason the control cannot run. */
function dataSetNameOf(manifest: PcfManifest, formName: string): string {
  const found = manifest.dataSets.find((d) => d.name === formName) ?? manifest.dataSets.find((d) => d.name.toLowerCase() === formName.toLowerCase());
  if (!found) throw new Error(t('record.customControl.noDataSet', { name: formName }));
  if (found.hasPropertySet) throw new Error(t('record.customControl.propertySet'));
  return found.name;
}

/** The manifest's properties besides the data-set: the FormXml's value, else the `default-value`, as literals. */
function inputsOf(manifest: PcfManifest, custom: DataSetControlRef): Record<string, PcfParameter> {
  const out: Record<string, PcfParameter> = {};
  for (const property of manifest.properties) {
    if (property.usage === 'output') continue;
    const given = custom.parameters[property.name];
    const type = property.ofType || property.types[0];
    if (given) out[property.name] = literalParameter(given.value, given.type ?? type);
    else out[property.name] = property.defaultValue !== undefined ? literalParameter(property.defaultValue, type) : { raw: null, formatted: '' };
  }
  return out;
}

/** The subgrid as its data-set parameters complete it: the cell's own values first; no relationship without a parent record. */
function subgridOf(control: DataSetSubgrid, unparented: boolean): SubgridModel {
  const p = control.customControl.dataSet.parameters;
  return {
    ...control,
    relationship: unparented ? '' : control.relationship || p.RelationshipName || '',
    targetEntity: control.targetEntity || p.TargetEntityType || '',
    viewId: control.viewId || p.ViewId || '',
  };
}

function pageSizeOf(control: DataSetSubgrid): number {
  const n = Number(control.customControl.dataSet.parameters.RecordsPerPage);
  return Number.isInteger(n) && n > 0 ? n : control.recordsPerPage;
}

/** One page's query: the view limited to the record (when the subgrid has a relationship), in `sort` (its first entry). */
function queryOf(setup: SubgridSetup, recordId: string | null, sort: SortStatus[], pageSize: number): string {
  let fetchXml = setup.view.fetchXml;
  if (setup.relationship) {
    if (!recordId) throw new Error('The record is not saved yet.');
    fetchXml = subgridFetchXml(fetchXml, setup.relationship, recordId, setup.tables[setup.target]?.primaryId);
  }
  const first = sort[0];
  if (first) {
    const dot = first.name.indexOf('.');
    fetchXml =
      dot > 0
        ? withOrder(fetchXml, first.name.slice(dot + 1), first.sortDirection === 1, first.name.slice(0, dot))
        : withOrder(fetchXml, first.name, first.sortDirection === 1);
  }
  return withPaging(fetchXml, 1, pageSize);
}

export interface PcfDatasetControlProps {
  control: DataSetSubgrid;
  env: ControlEnv;
  /** The subgrid's label (`mode.label`). */
  label?: string;
  /** Bumped by `formContext.getControl(subgrid).refresh()`: the data-set reloads page 1. */
  reload?: number;
  /** The classic grid, shown when the custom control cannot run. */
  fallback: ReactNode;
  /** `null`: no parent record (a dashboard); otherwise the form's record. */
  parent?: SubgridParent | null;
}

export function PcfDatasetControl({ control, env, label = '', reload = 0, fallback, parent }: PcfDatasetControlProps) {
  const unparented = parent === null;
  const styles = useStyles();
  const { api } = useAuth();
  const host = useContext(XrmHostContext);
  const zone = useContext(DisplayZoneContext);
  const dateFormats = useLoadedUserSettings()?.formats;
  // The theme the control renders in, read when it starts (`fluentDesignLanguage`); a later change of look reloads the page.
  const activeTheme = useActiveTheme();
  const themeRef = useRef(activeTheme);
  themeRef.current = activeTheme;
  const name = control.customControl.name;
  const wrapperRef = useRef<HTMLDivElement>(null);
  const hostRef = useRef<HTMLDivElement>(null);
  const [phase, setPhase] = useState<Phase>({ status: 'loading' });
  const [element, setElement] = useState<ReactNode>(null);
  const [size, setSize] = useState({ width: -1, height: -1 });
  const [renderTick, setRenderTick] = useState(0);
  /** Bumped by the data-set's `onChange`. */
  const [dataTick, setDataTick] = useState(0);
  const { ctx, state, overlay } = env;

  const fail = (reason: string) => {
    setPhase((p) => (p.status === 'failed' ? p : { status: 'failed', reason }));
    overlay?.setFormNotification({ uniqueId: `pcf:${control.id}`, level: 'WARNING', message: t('record.customControl.failed', { name, reason }) });
  };

  useEffect(() => {
    if (!api) return;
    let active = true;
    (async (): Promise<Ready> => {
      const { manifest, ctor } = await loadControl(name, { fetchManifest: manifestFetcher(api) });
      const dataSetName = dataSetNameOf(manifest, control.customControl.dataSet.name);
      const inputs = inputsOf(manifest, control.customControl);
      const [strings, privileges, setup] = await Promise.all([
        loadStrings(name, manifest, env.lcid),
        userPrivileges(api, host),
        loadSubgridSetup(api, subgridOf(control, unparented), env, { relationshipOptional: true }),
      ]);
      const entity = await cachedRead(env, `entity|${setup.target}`, () => loadEntity(api, setup.target));
      return { manifest, ctor, strings, privileges, dataSetName, inputs, setup, attributes: entity.attributes, pageSize: pageSizeOf(control) };
    })().then(
      (ready) => active && setPhase({ status: 'ready', ...ready }),
      (e: unknown) => active && fail(reasonOf(e)),
    );
    return () => {
      active = false;
    };
    // One load per control on this subgrid; the rest is read live when the control runs.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, name]);

  // What the control sees now: the form's record, as for a field control (ruling R8).
  const latest = state.latest();
  const recordId = latest.id ?? ctx.fieldContext.record?.id ?? null;
  const primary = ctx.entity.primaryNameAttribute;
  const recordName = primary ? latest.original[primary] : undefined;
  const contextInfo = {
    entityId: recordId ?? '',
    entityTypeName: ctx.entity.logicalName,
    entityRecordName: typeof recordName === 'string' ? recordName : '',
  };
  const view: Omit<PcfView, 'updatedProperties' | 'parameters'> = {
    // Only a read-only form disables a dataset control.
    mode: { isControlDisabled: state.readOnly, isVisible: true, label, allocatedWidth: size.width, allocatedHeight: size.height, contextInfo },
    page: { entityId: recordId, entityTypeName: ctx.entity.logicalName, isPageReadOnly: state.readOnly },
  };
  const ready = phase.status === 'ready' ? phase : undefined;
  // A subgrid on a relationship has nothing to list before the record has an id (Review Focus 4).
  const dataSetReady = !ready?.setup.relationship || !!recordId;
  const live = useRef({ view, recordId, dataSetReady, renderTick, dataTick });
  live.current = { view, recordId, dataSetReady, renderTick, dataTick };

  /** The running instance and what it was last shown. */
  const running = useRef<
    | { instance: PcfControl; context: PcfContext; ds: PcfDataSet; parameters: PcfView['parameters']; mode: string; page: string; tick: number; dataTick: number; failed: boolean }
    | undefined
  >();

  useEffect(() => {
    if (phase.status !== 'ready' || !api) return;
    const { manifest, ctor, strings, privileges, dataSetName, inputs, setup, attributes, pageSize } = phase;
    const virtual = manifest.controlType === 'virtual';
    const stateKey = `${ctx.entity.logicalName}:${control.id}`;
    const tracker = resizeTracker(() => wrapperRef.current, setSize);
    const abort = new AbortController();
    const navigation = navigationOf(host);
    const table = setup.tables[setup.target]!;

    const ds = createDataSet({
      target: setup.target,
      viewId: setup.view.id,
      title: setup.view.name,
      columns: setup.view.columns,
      attributes,
      pageSize,
      ready: live.current.dataSetReady,
      load: (page, sort, size) => runView(api, table.entitySetName, queryOf(setup, live.current.recordId, sort, size), page, undefined, abort.signal),
      openForm: (ref: EntityReference) => {
        const opened = (navigation as { openForm(options: object): unknown }).openForm({ entityName: ref.etn, entityId: ref.id.guid });
        if (opened instanceof Promise) opened.catch((e: unknown) => console.warn(`Custom control ${name}: openForm failed`, e));
      },
      onChange: () => setDataTick((n) => n + 1),
      zone: zone ?? 'Etc/UTC',
    });

    const input = hostInput({
      manifest,
      controlName: name,
      strings,
      locale: uiLocale(),
      lcid: env.lcid,
      currencySymbol: env.currencySymbol,
      timeZone: zone,
      dateFormats,
      host,
      api,
      navigation,
      privileges,
      stateKey,
      requestRender: () => setRenderTick((n) => n + 1),
      trackContainerResize: (track) => tracker.track(track),
      theme: themeRef.current,
    });

    const first = live.current;
    const parameters: PcfView['parameters'] = { ...inputs, [dataSetName]: ds };
    // The first updateView names every parameter.
    const context = buildContext(input, { ...first.view, parameters, updatedProperties: Object.keys(parameters) });
    let handle: PcfRunning;
    try {
      handle = startControl({
        name,
        ctor,
        virtual,
        context,
        stateKey,
        parent: hostRef.current,
        // A dataset control has no bound column to write.
        notifyOutputChanged: () => undefined,
        onStop: () => {
          tracker.dispose();
          running.current = undefined;
        },
      });
    } catch (e) {
      ds.dispose();
      abort.abort();
      fail(reasonOf(e));
      return;
    }
    running.current = {
      instance: handle.instance,
      context,
      ds,
      parameters,
      mode: JSON.stringify(first.view.mode),
      page: JSON.stringify(first.view.page),
      tick: first.renderTick,
      dataTick: first.dataTick,
      failed: false,
    };
    if (virtual) setElement(handle.element as ReactNode);
    const started = handle;
    return () => {
      ds.dispose();
      abort.abort();
      started.stop();
    };
    // Once per loaded control: the instance lives as long as the subgrid.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase]);

  // The first save gives the relationship its parent id: the data-set loads.
  useEffect(() => {
    running.current?.ds.setReady(dataSetReady);
  }, [dataSetReady]);

  // A script's refresh of the subgrid, a refresh of the form, or another record: page 1 again.
  const reloadKey = `${reload}|${env.refreshKey ?? 0}`;
  const seen = useRef({ reloadKey, recordId });
  useEffect(() => {
    const before = seen.current;
    seen.current = { reloadKey, recordId };
    const otherRecord = !!before.recordId && !!recordId && before.recordId !== recordId;
    if (before.reloadKey === reloadKey && !otherRecord) return;
    if (dataSetReady) running.current?.ds.refresh();
  }, [reloadKey, recordId, dataSetReady]);

  // updateView after a change of the data-set, the mode, the page, the size, or a requestRender.
  useEffect(() => {
    const run = running.current;
    if (!run || run.failed || phase.status !== 'ready') return;
    const mode = JSON.stringify(view.mode);
    const page = JSON.stringify(view.page);
    const before = JSON.parse(run.mode) as PcfView['mode'];
    const layout = before.allocatedWidth !== size.width || before.allocatedHeight !== size.height;
    const data = run.dataTick !== dataTick;
    if (!data && mode === run.mode && page === run.page && run.tick === renderTick) return;
    run.mode = mode;
    run.page = page;
    run.tick = renderTick;
    run.dataTick = dataTick;
    const updated = [...(data ? [phase.dataSetName] : []), ...(layout ? ['layout'] : [])];
    updateContext(run.context, { ...view, parameters: run.parameters, updatedProperties: updated });
    try {
      const out = run.instance.updateView(run.context);
      if (phase.manifest.controlType === 'virtual') setElement(out as ReactNode);
    } catch (e) {
      run.failed = true;
      fail(t('record.customControl.updateFailed', { message: reasonOf(e) }));
    }
  });

  if (phase.status === 'failed') {
    if (!unparented || overlay) return <>{fallback}</>;
    return (
      <>
        <MessageBar intent="warning">
          <MessageBarBody>{t('record.customControl.failed', { name, reason: phase.reason })}</MessageBarBody>
        </MessageBar>
        {fallback}
      </>
    );
  }
  return (
    <div ref={wrapperRef} className={styles.host} data-pcf={name}>
      {phase.status === 'loading' && <Spinner size="tiny" label={t('record.customControl.loading')} />}
      <div ref={hostRef} />
      {ready?.manifest.controlType === 'virtual' && (
        <PcfBoundary onError={(e) => fail(t('record.customControl.renderFailed', { message: reasonOf(e) }))}>{element}</PcfBoundary>
      )}
    </div>
  );
}
