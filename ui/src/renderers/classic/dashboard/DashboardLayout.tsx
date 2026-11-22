import { makeStyles, MessageBar, MessageBarBody, Text, tokens } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import type { RecordContext } from '../../../apps/record/loadRecord';
import type { RecordForm } from '../../../apps/record/useRecordForm';
import type { DashboardModel } from '../../../model/dashboard';
import type { Cell, Control, SubgridControl as SubgridModel } from '../../../model/form';
import { t } from '../../../shell/i18n/t';
import { ChartControl } from '../chart/ChartControl';
import type { ControlEnv, ReadCache } from '../form/controlEnv';
import type { RenderControl } from '../form/FormCell';
import { FormTab } from '../form/FormTab';
import { SubgridControl } from '../form/SubgridControl';
import { WebResourceControl } from '../form/WebResourceControl';

const useStyles = makeStyles({
  tabs: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalL },
  label: { display: 'block', marginBlockEnd: tokens.spacingVerticalXXS },
});

/** The table of a dashboard's `systemform` row: a dashboard belongs to no table. */
const NO_TABLE = 'none';

/**
 * What the form's controls need, for a page that has no record: a dashboard's subgrids, dataset controls and web resources
 * read only the language, the app, the shared metadata reads, and an empty record (no id, no values). A dashboard is not a
 * read-only page, so its controls are enabled (`readOnly` false); the record's writes do nothing.
 */
export function dashboardEnv(lcid: number, appId: string | undefined, cache: ReadCache): ControlEnv {
  const ctx: RecordContext = {
    entity: { logicalName: NO_TABLE, displayName: '', primaryIdAttribute: '', isCustom: false, attributes: [] },
    fieldContext: { table: NO_TABLE, lookups: [], globalChoices: {} },
    rights: [],
    canCreate: false,
  };
  const nothing = async () => undefined;
  const refused = async () => false;
  const state: RecordForm = {
    values: {},
    original: {},
    latest: () => ({ values: {}, original: {} }),
    dirty: false,
    errors: {},
    busy: false,
    isBusy: () => false,
    readOnly: false,
    isReadOnly: () => false,
    isRequired: () => false,
    checkRequired: () => true,
    setValue: () => undefined,
    setDefault: () => undefined,
    save: nothing,
    remove: refused,
    refresh: async () => undefined,
    assign: refused,
    setState: refused,
  };
  return { ctx, state, lcid, appId, cache };
}

function Notice({ intent, text }: { intent: 'info' | 'warning'; text: string }) {
  return (
    <MessageBar intent={intent}>
      <MessageBarBody>{text}</MessageBarBody>
    </MessageBar>
  );
}

/** A subgrid that shows a chart: `ChartGridMode` Chart with a `VisualizationId`. */
const isChart = (control: SubgridModel): control is SubgridModel & { visualizationId: string } =>
  control.chartGridMode === 'Chart' && !!control.visualizationId;

/**
 * The dashboard's controls (Global Constraints, "Cells"): a chart subgrid draws its chart over its view; any other subgrid lists
 * its view with no parent record (or runs its dataset control); a web resource is the UI-1c frame; anything else (an IFRAME
 * of an address, a timeline, a field) shows that it is not supported. Each cell loads on its own, so one that fails leaves
 * the others.
 */
function DashboardControl({ cell, control, label, env, appId, lcid }: { cell: Cell; control: Control; label: string | undefined; env: ControlEnv; appId?: string; lcid: number }) {
  const styles = useStyles();
  const title = (content: ReactNode) => (
    <>
      {label && (
        <Text weight="semibold" className={styles.label}>
          {label}
        </Text>
      )}
      {content}
    </>
  );
  if (control.kind === 'subgrid') {
    if (isChart(control)) return <ChartControl visualizationId={control.visualizationId} viewId={control.viewId || undefined} title={label} appId={appId} lcid={lcid} />;
    return title(<SubgridControl control={control} env={env} label={label} parent={null} />);
  }
  if (control.kind === 'webresource' && !control.iframe) return title(<WebResourceControl cell={cell} control={control} env={env} />);
  return title(<Notice intent="info" text={t('dashboard.unsupportedCell')} />);
}

/** Every subgrid of the dashboard. */
const subgridsOf = (dashboard: DashboardModel): SubgridModel[] =>
  dashboard.tabs.flatMap((tab) =>
    tab.columns.flatMap((c) => c.sections.flatMap((s) => s.rows.flat().flatMap((cell) => (cell.control?.kind === 'subgrid' ? [cell.control] : [])))),
  );

export interface DashboardLayoutProps {
  dashboard: DashboardModel;
  env: ControlEnv;
  appId?: string;
  lcid: number;
}

/**
 * A dashboard laid out by the form's rules (UI-1b): its tabs one under the other (without tab headings), each with its columns
 * and sections. An interactive dashboard (ruling F3) and chart or view pickers (ruling F4) are named once above the layout.
 */
export function DashboardLayout({ dashboard, env, appId, lcid }: DashboardLayoutProps) {
  const styles = useStyles();
  const renderControl: RenderControl = (cell, control, label) => <DashboardControl cell={cell} control={control} label={label} env={env} appId={appId} lcid={lcid} />;
  const tabs = dashboard.tabs.filter((tab) => tab.visible);
  return (
    <div className={styles.tabs}>
      {dashboard.interactive && <Notice intent="info" text={t('dashboard.interactive')} />}
      {subgridsOf(dashboard).some((s) => s.pickers) && <Notice intent="info" text={t('dashboard.pickers')} />}
      {tabs.map((tab, i) => (
        <FormTab key={`${tab.name}-${i}`} tab={tab} env={env} renderControl={renderControl} />
      ))}
    </div>
  );
}
