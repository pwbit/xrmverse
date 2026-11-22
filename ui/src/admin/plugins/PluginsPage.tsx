import {
  Badge,
  Button,
  MessageBar,
  MessageBarBody,
  Spinner,
  Tab,
  TabList,
  Toolbar,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';
import { en as enCatalogue } from '../../shell/i18n/en';
import type { en } from '../../shell/i18n/en';
import { usePagedList, type PagedList } from '../common/usePagedList';
import { CustomApisTab } from './CustomApisTab';
import { TracesPage } from './TracesPage';
import { PageHeader } from '../../shell/layout/PageHeader';
import { useSurfaceStyles } from '../../shell/layout/surface';
import { ActivateIcon, DeactivateIcon } from '../../shell/icons';
import { RowCommand } from '../common/rowSelection';

const STEP_SELECT = 'sdkmessageprocessingstepid,name,stage,mode,rank,statecode,statuscode';
/** sdkmessageprocessingstep: Enabled is statecode 0 / statuscode 1, Disabled statecode 1 / statuscode 2. */
const ENABLED = { statecode: 0, statuscode: 1 } as const;
const DISABLED = { statecode: 1, statuscode: 2 } as const;

type Assembly = { pluginassemblyid: string; name: string; version?: string | null };
type PluginType = { plugintypeid: string; typename: string };
type Step = {
  sdkmessageprocessingstepid: string;
  name: string;
  stage?: number | null;
  mode?: number | null;
  rank?: number | null;
  statecode: number;
};

/** The name of a step stage; a null or unknown stage shows its number. */
function stageLabel(stage?: number | null): string {
  if (stage == null) return '';
  const key = `plugins.stage.${stage}`;
  return key in enCatalogue ? t(key as keyof typeof en) : String(stage);
}

const nested: React.CSSProperties = { listStyle: 'none', paddingLeft: 24, margin: 0 };

/** Loading, error and "load more" of one level of the tree. */
function LevelStatus<T>({ list, empty, failed }: { list: PagedList<T>; empty: keyof typeof en; failed: keyof typeof en }) {
  return (
    <>
      {list.error && (
        <MessageBar intent="error">
          <MessageBarBody>{t(failed as 'plugins.loadFailed', { detail: list.error })}</MessageBarBody>
        </MessageBar>
      )}
      {!list.loading && !list.error && list.items.length === 0 && <p>{t(empty)}</p>}
      {list.loading && <Spinner size="tiny" label={t('common.loading')} />}
      {list.nextLink && !list.loading && <Button onClick={list.loadMore}>{t('common.loadMore')}</Button>}
    </>
  );
}

/** The step the Assemblies tab's command bar acts on, with the callback that refreshes its row in its level. */
type StepSelection = { step: Step; onChanged: (fresh: Step) => void };

/** The selection of the step tree, owned by the Assemblies tab. */
interface StepTreeContext {
  selectedId: string | undefined;
  /** Selects the step, or clears the selection when it is already selected. */
  toggle: (selection: StepSelection) => void;
  /** Clears the selection when the step leaves the tree (its type collapsed). */
  forget: (id: string) => void;
}

const StepTree = createContext<StepTreeContext | undefined>(undefined);

const useStyles = makeStyles({
  name: { cursor: 'pointer', padding: '2px 6px', borderRadius: tokens.borderRadiusMedium },
  selected: { backgroundColor: tokens.colorBrandBackground2, color: tokens.colorBrandForeground2 },
  commandBar: { minHeight: '40px', marginBlock: tokens.spacingVerticalS },
});

function StepRow({ step, onChanged }: { step: Step; onChanged: (fresh: Step) => void }) {
  const styles = useStyles();
  const { selectedId, toggle, forget } = useContext(StepTree)!;
  const id = step.sdkmessageprocessingstepid;
  const isSelected = selectedId === id;
  const enabled = step.statecode === 0;

  useEffect(() => () => forget(id), [id, forget]);

  return (
    <li aria-selected={isSelected}>
      <span
        tabIndex={0}
        className={mergeClasses(styles.name, isSelected && styles.selected)}
        onClick={() => toggle({ step, onChanged })}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            toggle({ step, onChanged });
          }
        }}
      >
        {step.name}
      </span>{' '}
      <Badge appearance="tint" color={enabled ? 'success' : 'danger'}>{t(enabled ? 'plugins.step.enabled' : 'plugins.step.disabled')}</Badge>{' '}
      <span>{t('plugins.step.stage', { stage: stageLabel(step.stage) })}</span>{' '}
      <span>{t('plugins.step.mode', { mode: t(step.mode === 1 ? 'plugins.mode.1' : 'plugins.mode.0') })}</span>{' '}
      <span>{t('plugins.step.rank', { rank: step.rank ?? 0 })}</span>
    </li>
  );
}

function TypeNode({ type }: { type: PluginType }) {
  const [open, setOpen] = useState(false);
  return (
    <li>
      <Button
        appearance="transparent"
        aria-expanded={open}
        aria-label={t(open ? 'plugins.type.collapse' : 'plugins.type.expand', { name: type.typename })}
        onClick={() => setOpen((o) => !o)}
      >
        {type.typename}
      </Button>
      {open && <Steps typeId={type.plugintypeid} />}
    </li>
  );
}

function Steps({ typeId }: { typeId: string }) {
  const list = usePagedList<Step>(
    `sdkmessageprocessingsteps?$select=${STEP_SELECT}&$filter=${encodeURIComponent(`_plugintypeid_value eq ${typeId}`)}&$orderby=name`,
  );
  return (
    <ul style={nested}>
      {list.items.map((s) => (
        <StepRow
          key={s.sdkmessageprocessingstepid}
          step={s}
          onChanged={(fresh) =>
            list.setItems((prev) => prev.map((x) => (x.sdkmessageprocessingstepid === fresh.sdkmessageprocessingstepid ? fresh : x)))
          }
        />
      ))}
      <LevelStatus list={list} empty="plugins.steps.empty" failed="plugins.steps.loadFailed" />
    </ul>
  );
}

function AssemblyNode({ assembly }: { assembly: Assembly }) {
  const [open, setOpen] = useState(false);
  return (
    <li>
      <Button
        appearance="transparent"
        aria-expanded={open}
        aria-label={t(open ? 'plugins.assembly.collapse' : 'plugins.assembly.expand', { name: assembly.name })}
        onClick={() => setOpen((o) => !o)}
      >
        {assembly.name}
      </Button>{' '}
      <span>{t('plugins.assembly.version', { version: assembly.version ?? '' })}</span>
      {open && <Types assemblyId={assembly.pluginassemblyid} />}
    </li>
  );
}

function Types({ assemblyId }: { assemblyId: string }) {
  const list = usePagedList<PluginType>(
    `plugintypes?$select=plugintypeid,typename&$filter=${encodeURIComponent(`_pluginassemblyid_value eq ${assemblyId}`)}&$orderby=typename`,
  );
  return (
    <ul style={nested}>
      {list.items.map((type) => (
        <TypeNode key={type.plugintypeid} type={type} />
      ))}
      <LevelStatus list={list} empty="plugins.types.empty" failed="plugins.types.loadFailed" />
    </ul>
  );
}

/**
 * The assembly, type and step tree. A click on a step's name selects it, and the tab's command bar enables or disables the
 * selected step, as in a model-driven view.
 */
function Assemblies() {
  const { api } = useAuth();
  const notify = useNotify();
  const surface = useSurfaceStyles();
  const styles = useStyles();
  const list = usePagedList<Assembly>('pluginassemblies?$select=pluginassemblyid,name,version&$orderby=name');
  const [selected, setSelected] = useState<StepSelection>();
  const [busy, setBusy] = useState(false);

  const toggle = useCallback(
    (s: StepSelection) =>
      setSelected((cur) => (cur?.step.sdkmessageprocessingstepid === s.step.sdkmessageprocessingstepid ? undefined : s)),
    [],
  );
  const forget = useCallback((id: string) => setSelected((cur) => (cur?.step.sdkmessageprocessingstepid === id ? undefined : cur)), []);
  const selectedId = selected?.step.sdkmessageprocessingstepid;
  const tree = useMemo(() => ({ selectedId, toggle, forget }), [selectedId, toggle, forget]);

  /** Enables or disables the step, then re-reads its row so the tree shows what the server stored. */
  async function setEnabled({ step, onChanged }: StepSelection, enable: boolean) {
    if (!api) return;
    setBusy(true);
    const id = step.sdkmessageprocessingstepid;
    const state = enable ? ENABLED : DISABLED;
    const changed = (fresh: Step) => {
      onChanged(fresh);
      setSelected((cur) => (cur?.step.sdkmessageprocessingstepid === id ? { ...cur, step: fresh } : cur));
    };
    try {
      await api.patch(`sdkmessageprocessingsteps(${id})`, state);
    } catch (e) {
      notify.error(e);
      setBusy(false);
      return;
    }
    notify.success(t(enable ? 'plugins.step.nowEnabled' : 'plugins.step.nowDisabled', { name: step.name }));
    try {
      changed(await api.get<Step>(`sdkmessageprocessingsteps(${id})?$select=${STEP_SELECT}`));
    } catch {
      // The write succeeded; show the values it set until the row is read again.
      changed({ ...step, statecode: state.statecode });
    } finally {
      setBusy(false);
    }
  }

  const sel = selected;
  const selEnabled = sel?.step.statecode === 0;
  return (
    <StepTree.Provider value={tree}>
      <Toolbar
        aria-label={t('page.commands', { title: t('plugins.tab.assemblies') })}
        className={mergeClasses(surface.commandBar, styles.commandBar)}
      >
        {sel && (
          <RowCommand
            icon={selEnabled ? <DeactivateIcon /> : <ActivateIcon />}
            disabled={busy || !api}
            aria-label={t(selEnabled ? 'plugins.step.disableOpen' : 'plugins.step.enableOpen', { name: sel.step.name })}
            onClick={() => void setEnabled(sel, !selEnabled)}
          >
            {t(selEnabled ? 'plugins.step.disable' : 'plugins.step.enable')}
          </RowCommand>
        )}
      </Toolbar>
      <section aria-label={t('plugins.cli.title')}>
        <h3>{t('plugins.cli.title')}</h3>
        <p>{t('plugins.cli.body')}</p>
        <pre>
          <code>{t('plugins.cli.command')}</code>
        </pre>
      </section>
      <ul style={{ listStyle: 'none', paddingLeft: 0 }}>
        {list.items.map((a) => (
          <AssemblyNode key={a.pluginassemblyid} assembly={a} />
        ))}
      </ul>
      <LevelStatus list={list} empty="plugins.empty" failed="plugins.loadFailed" />
    </StepTree.Provider>
  );
}

export function PluginsPage() {
  const [tab, setTab] = useState<'assemblies' | 'traces' | 'customApis'>('assemblies');
  return (
    <section>
      <PageHeader title={t('plugins.title')} />
      <TabList selectedValue={tab} onTabSelect={(_, d) => setTab(d.value as 'assemblies' | 'traces' | 'customApis')}>
        <Tab value="assemblies">{t('plugins.tab.assemblies')}</Tab>
        <Tab value="customApis">{t('plugins.tab.customApis')}</Tab>
        <Tab value="traces">{t('plugins.tab.traces')}</Tab>
      </TabList>
      {tab === 'assemblies' ? <Assemblies /> : tab === 'customApis' ? <CustomApisTab /> : <TracesPage />}
    </section>
  );
}
