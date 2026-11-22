import {
  Button,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  Spinner,
  Tab,
  TabList,
  Text,
  Tooltip,
  tokens,
} from '@fluentui/react-components';
import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, Navigate, useLocation, useNavigate, useParams } from 'react-router-dom';
import { isMap, isScalar, type Document } from 'yaml';
import { ConfirmDialog } from '../../admin/common/ConfirmDialog';
import { useUnsavedGuard } from '../../apps/record/useUnsavedGuard';
import type { XrmApi } from '../../shell/api/client';
import { errorText, XrmError } from '../../shell/api/errors';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { ActivateIcon, AddExistingIcon, DeactivateIcon, DeleteIcon, DownloadIcon, MoreIcon, OpenIcon, SaveIcon } from '../../shell/icons';
import { useNotify } from '../../shell/notify/Notifications';
import { applyWorkflow, deleteWorkflow, exportWorkflow, listWorkflows, managedIds, setWorkflowState, type ApplyResult, type ProcessState, type WorkflowSummary } from '../api/studioApi';
import { Canvas } from '../canvas/Canvas';
import { setAt } from '../model/edit';
import type { StepPath, WorkflowModel } from '../model/types';
import { PropertiesPanel } from '../properties/PropertiesPanel';
import { CodeView, type CodeViewHandle } from './CodeView';
import { EditorHeader } from './EditorHeader';
import { NotRepresentable } from './NotRepresentable';
import { collectProblems, problemsByLine, stepAtLine, type EditorProblem } from './problems';
import { ProblemsPane } from './ProblemsPane';
import { SolutionDialog, type SolutionChoice } from './SolutionDialog';
import { useEditorDocument, type DocumentChange } from './useEditorDocument';
import { XamlDialog } from './XamlDialog';

/** What `/studio/workflows/new` receives in its route state: the YAML the New dialog built. */
export interface NewProcessState {
  yaml: string;
  name: string;
}

type View = 'canvas' | 'code';

/** The server's words for a process the format cannot represent (ruling W5). */
const NOT_REPRESENTABLE = 'is not representable in YAML';

/**
 * The text and view a first save hands to the editor of the new id (ruling W4): the editor keeps the user's text (plus the id)
 * instead of loading the canonical export. Read once; a reload of the page loads the export.
 */
const handoff = new Map<string, { text: string; saved: string; pending?: DocumentChange; view: View; state: ProcessState }>();


const useStyles = makeStyles({
  page: { display: 'flex', flexDirection: 'column', rowGap: tokens.spacingVerticalM, minWidth: 0 },
  body: { display: 'flex', flexDirection: 'column', minHeight: '360px', minWidth: 0 },
  canvas: { minHeight: '320px', minWidth: 0, flexGrow: 1, flexBasis: '480px' },
  // The canvas and the properties panel side by side; the panel goes below the canvas on a narrow window.
  split: { display: 'flex', flexWrap: 'wrap', alignItems: 'flex-start', gap: tokens.spacingHorizontalM, minWidth: 0 },
  code: { flexGrow: 1, flexBasis: '480px', minWidth: 0 },
  diagram: { display: 'flex', flexDirection: 'column', rowGap: tokens.spacingVerticalS, flexGrow: 1, minWidth: 0 },
  properties: { flexGrow: 0, flexShrink: 1, flexBasis: '360px', minWidth: 0, maxHeight: '70vh', display: 'flex' },
});

/** Inserts `id: <id>` right after `kind` (or sets an `id` that is already there), keeping every other node and comment. */
export function insertId(doc: Document, id: string): void {
  const root = doc.contents;
  if (!isMap(root)) throw new Error('The document is not a mapping.');
  if (root.has('id')) {
    setAt(doc, ['id'], id);
    return;
  }
  const at = root.items.findIndex((pair) => isScalar(pair.key) && pair.key.value === 'kind');
  root.items.splice(at + 1, 0, doc.createPair('id', id) as (typeof root.items)[number]);
}

/**
 * The code view's right pane (spec §4.3): the diagram of the current text, read-only (no slots, menus, drag or selection). While
 * the text has problems it shows the last diagram that could be read, with a note.
 */
function LiveDiagram({ model, stale, problemsByLine }: { model?: WorkflowModel; stale: boolean; problemsByLine: Map<number, EditorProblem[]> }) {
  const styles = useStyles();
  return (
    <div className={styles.diagram}>
      <Text weight="semibold">{t('studio.editor.diagram')}</Text>
      {stale && (
        <MessageBar intent="warning">
          <MessageBarBody>{t(model ? 'studio.editor.diagram.stale' : 'studio.editor.diagram.none')}</MessageBarBody>
        </MessageBar>
      )}
      {model && <Canvas model={model} problemsByLine={stale ? new Map() : problemsByLine} />}
    </div>
  );
}

/** Saves `text` as `name.yaml` through a temporary link. */
function download(text: string, name: string) {
  const url = URL.createObjectURL(new Blob([text], { type: 'text/yaml' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = `${name.replace(/[\\/:*?"<>|]/g, '_').trim() || 'process'}.yaml`;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}

/**
 * `/studio/workflows/new` (the New dialog's YAML in the route state) and `/studio/workflows/:id`. A process is loaded by id
 * through its YAML export; one the format cannot represent gets the read-only page, any other failure the error page.
 */
export function EditorPage() {
  const { id } = useParams();
  // A new editor for every id: the first save of a new process moves to its id with a fresh editor (W4).
  return <EditorLoader key={id ?? 'new'} id={id} />;
}

type Loaded =
  | { kind: 'loading' }
  | { kind: 'yaml'; text: string; saved?: string; pending?: DocumentChange; view: View; state?: ProcessState }
  | { kind: 'readonly'; message: string }
  | { kind: 'error'; message: string };

function EditorLoader({ id }: { id?: string }) {
  const { api } = useAuth();
  const state = useLocation().state as NewProcessState | null;
  const [loaded, setLoaded] = useState<Loaded>(() => {
    if (!id) return state?.yaml ? { kind: 'yaml', text: state.yaml, view: 'canvas' } : { kind: 'error', message: '' };
    const handed = handoff.get(id);
    return handed ? { kind: 'yaml', ...handed } : { kind: 'loading' };
  });
  const [summary, setSummary] = useState<WorkflowSummary>();
  const [managed, setManaged] = useState(false);

  useEffect(() => {
    if (id) handoff.delete(id);
  }, [id]);

  useEffect(() => {
    if (!id || !api) return;
    let live = true;
    if (loaded.kind === 'loading') {
      exportWorkflow(api, id, 'yaml').then(
        (text) => live && setLoaded({ kind: 'yaml', text, view: 'canvas' }),
        (e: unknown) => {
          if (!live) return;
          // The read-only page shows the server's message verbatim (without the error code).
          if (e instanceof XrmError && e.status === 400 && e.serverMessage.includes(NOT_REPRESENTABLE)) setLoaded({ kind: 'readonly', message: e.serverMessage });
          else setLoaded({ kind: 'error', message: errorText(e) });
        },
      );
    }
    // The header's state and names, and the Managed badge (W6): a failure leaves them out.
    listWorkflows(api).then(
      (rows) => live && setSummary(rows.find((r) => r.id.toLowerCase() === id.toLowerCase())),
      () => undefined,
    );
    managedIds(api, [id]).then(
      (ids) => live && setManaged(ids.size > 0),
      () => undefined,
    );
    return () => {
      live = false;
    };
    // Loaded once per id (the editor is keyed by id).
  }, [api, id]);

  if (!id && loaded.kind === 'error') return <Navigate to="/studio/workflows" replace />;
  if (!api || loaded.kind === 'loading') return <Spinner label={t('studio.editor.loading')} />;
  if (loaded.kind === 'error') {
    return (
      <section>
        <MessageBar intent="error">
          <MessageBarBody>{t('studio.editor.loadFailed', { detail: loaded.message })}</MessageBarBody>
        </MessageBar>
        <Link to="/studio/workflows">{t('studio.editor.back')}</Link>
      </section>
    );
  }
  if (loaded.kind === 'readonly') return <NotRepresentable api={api} id={id!} message={loaded.message} summary={summary} managed={managed} />;
  return <Editor api={api} id={id} initialText={loaded.text} initialSaved={loaded.saved} initialPending={loaded.pending} initialView={loaded.view} initialState={loaded.state} summary={summary} managed={managed} fallbackName={state?.name} />;
}

interface EditorProps {
  api: XrmApi;
  /** Undefined for a new process (not saved yet). */
  id?: string;
  initialText: string;
  /** The saved text when it differs from `initialText` (an edit made during a first save, R7). */
  initialSaved?: string;
  /** The first save's id when the edit made during it could not take it yet (`useEditorDocument`'s `changePending`). */
  initialPending?: DocumentChange;
  /** The state the first save answered (W4): shown at once, before the list read confirms it. */
  initialState?: ProcessState;
  initialView: View;
  summary?: WorkflowSummary;
  managed: boolean;
  fallbackName?: string;
}

/** What waits for the "save first" answer: the state change the user asked for. */
type Pending = { activate: boolean };

function Editor({ api, id, initialText, initialSaved, initialPending, initialView, initialState, summary, managed, fallbackName }: EditorProps) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const doc = useEditorDocument(api, initialText, initialSaved, initialPending);
  const isNew = !id;
  const { blocker, navigateAnyway } = useUnsavedGuard(doc.dirty || isNew);
  const code = useRef<CodeViewHandle>(null);
  const [view, setView] = useState<View>(initialView);
  const [selected, setSelected] = useState<StepPath | 'trigger'>();
  // The canvas's zoom, kept for the editor's session while the view switches (spec §4.5).
  const [zoom, setZoom] = useState(1);
  const [processState, setProcessState] = useState<ProcessState | undefined>(summary?.state ?? initialState);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [solution, setSolution] = useState<SolutionChoice>();
  const [dialog, setDialog] = useState<'xaml' | 'solution' | 'delete'>();
  const [pending, setPending] = useState<Pending>();
  const [goTo, setGoTo] = useState<{ line: number; column?: number }>();

  useEffect(() => {
    if (summary) setProcessState(summary.state);
  }, [summary]);

  const problems = useMemo(() => collectProblems(doc.localProblems, doc.serverResult), [doc.localProblems, doc.serverResult]);
  const byLine = useMemo(() => problemsByLine(problems), [problems]);
  const name = doc.model?.name ?? summary?.name ?? fallbackName ?? id ?? '';
  const hasLocalProblems = doc.localProblems.length > 0;
  // The code view's diagram keeps the last model that could be read while the text has problems.
  const lastGood = useRef(doc.model);
  if (doc.model) lastGood.current = doc.model;
  const lastModel = doc.model ?? lastGood.current;

  // A problem opened from the canvas while there is no diagram: the code view opens at its line.
  useEffect(() => {
    if (view === 'code' && goTo) {
      code.current?.goTo(goTo.line, goTo.column);
      setGoTo(undefined);
    }
  }, [view, goTo]);

  function openProblem(p: EditorProblem) {
    if (p.line === undefined) return;
    if (view === 'code') {
      code.current?.goTo(p.line, p.column);
    } else if (doc.model) {
      const at = stepAtLine(doc.model, p.line);
      if (at) setSelected(at);
    } else {
      setGoTo({ line: p.line, column: p.column });
      setView('code');
    }
  }

  /** Saves the document; `activate` also activates it in the same transaction. Returns the result of a save that was taken. */
  async function save(activate?: boolean): Promise<ApplyResult | undefined> {
    // Never a save without the first save's id (it would create a second process): the message above says why.
    if (doc.changePending) return undefined;
    setBusy(true);
    setError(undefined);
    try {
      const sent = doc.text;
      const result = await applyWorkflow(api, sent, { dryRun: false, solution: solution?.uniquename, ...(activate ? { activate: true } : {}) });
      doc.showResult(result);
      if (result.errors.length > 0) {
        setError(t('studio.editor.saveRefused'));
        return undefined;
      }
      setSolution(undefined);
      setProcessState(result.state);
      notify.success(t('studio.editor.saved', { name: result.name }));
      if (isNew) {
        // W4: the id goes into the document right after `kind`, and the editor moves to the process's address. An edit made
        // while the save was in flight is kept, with the id, and stays unsaved (R7).
        const after = doc.markSaved(sent, (d) => insertId(d, result.id));
        handoff.set(result.id, { ...after, view, state: result.state });
        navigateAnyway(() => navigate(`/studio/workflows/${result.id}`, { replace: true }));
      } else {
        doc.markSaved(sent);
      }
      return result;
    } catch (e) {
      setError(t('studio.editor.saveFailed', { detail: errorText(e) }));
      return undefined;
    } finally {
      setBusy(false);
    }
  }

  async function changeState(activate: boolean) {
    if (!id) return;
    setBusy(true);
    setError(undefined);
    try {
      const after = await setWorkflowState(api, id, activate);
      setProcessState(after?.state ?? (activate ? 'activated' : 'draft'));
      notify.success(t(activate ? 'studio.done.activated' : 'studio.done.deactivated', { name }));
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  }

  /** Activate and Deactivate act on the saved definition: with unsaved changes (or a new process) the user is asked to save first. */
  function requestState(activate: boolean) {
    if (doc.dirty || isNew) setPending({ activate });
    else void changeState(activate);
  }

  async function saveThenChangeState({ activate }: Pending) {
    setPending(undefined);
    // Activate is taken by the save itself (one transaction); Deactivate follows a save that was taken.
    const saved = await save(activate);
    if (saved && !activate) await changeState(false);
  }

  async function remove() {
    if (!id) return;
    setDialog(undefined);
    setBusy(true);
    setError(undefined);
    try {
      await deleteWorkflow(api, id);
      notify.success(t('studio.done.deleted', { name }));
      navigateAnyway(() => navigate('/studio/workflows'));
    } catch (e) {
      setError(errorText(e));
      setBusy(false);
    }
  }

  // A first save's id still waiting for the text (changePending) blocks Save: without it the save would create another process.
  const canSave = !busy && !hasLocalProblems && !doc.changePending && (isNew || doc.dirty || solution !== undefined);
  const header = doc.model ?? summary;

  return (
    <section className={styles.page}>
      <EditorHeader
        name={name}
        kind={header?.kind}
        entity={doc.model?.entity ?? summary?.entity}
        mode={doc.model ? doc.model.mode : summary?.mode}
        state={isNew ? undefined : processState}
        managed={managed}
        unsaved={doc.dirty}
        middle={
          <TabList size="small" selectedValue={view} onTabSelect={(_, d) => setView(d.value as View)} aria-label={t('studio.editor.view')}>
            <Tab value="canvas">{t('studio.editor.view.canvas')}</Tab>
            <Tab value="code">{t('studio.editor.view.code')}</Tab>
          </TabList>
        }
        commands={
          <>
            <Tooltip content={hasLocalProblems ? t('studio.editor.saveBlocked') : solution ? t('studio.editor.solution.pending', { solution: solution.friendlyname || solution.uniquename }) : t('studio.editor.save')} relationship="description">
              <Button appearance="primary" icon={<SaveIcon />} disabled={!canSave} onClick={() => void save()}>
                {t('studio.editor.save')}
              </Button>
            </Tooltip>
            {processState === 'activated' && !isNew ? (
              <Button icon={<DeactivateIcon />} disabled={busy} onClick={() => requestState(false)}>
                {t('studio.cmd.deactivate')}
              </Button>
            ) : (
              <Button icon={<ActivateIcon />} disabled={busy || ((doc.dirty || isNew) && hasLocalProblems)} onClick={() => requestState(true)}>
                {t('studio.cmd.activate')}
              </Button>
            )}
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button icon={<MoreIcon />} aria-label={t('studio.editor.more')} disabled={busy} />
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  <MenuItem icon={<DownloadIcon />} onClick={() => download(doc.text, name)}>
                    {t('studio.editor.exportYaml')}
                  </MenuItem>
                  <MenuItem icon={<OpenIcon />} disabled={isNew} onClick={() => setDialog('xaml')}>
                    {t('studio.editor.viewXaml')}
                  </MenuItem>
                  <MenuItem icon={<AddExistingIcon />} onClick={() => setDialog('solution')}>
                    {t('studio.editor.addToSolution')}
                  </MenuItem>
                  <MenuItem icon={<DeleteIcon />} disabled={isNew} onClick={() => setDialog('delete')}>
                    {t('studio.cmd.delete')}
                  </MenuItem>
                </MenuList>
              </MenuPopover>
            </Menu>
          </>
        }
      />
      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}
      {doc.changePending && (
        <MessageBar intent="warning">
          <MessageBarBody>{t('studio.editor.idPending', { id: id ?? '' })}</MessageBarBody>
        </MessageBar>
      )}
      {solution && (
        <MessageBar intent="info">
          <MessageBarBody>{t('studio.editor.solution.pending', { solution: solution.friendlyname || solution.uniquename })}</MessageBarBody>
        </MessageBar>
      )}
      <div className={styles.body}>
        {view === 'code' ? (
          <div className={styles.split}>
            <div className={styles.code}>
              <CodeView ref={code} text={doc.text} onChange={doc.setText} problems={problems} readOnly={busy} />
            </div>
            <aside className={styles.properties} aria-label={t('studio.editor.diagram')}>
              <LiveDiagram model={lastModel} stale={!doc.model} problemsByLine={byLine} />
            </aside>
          </div>
        ) : (
          <div className={styles.split}>
            <section className={styles.canvas} aria-label={t('studio.editor.view.canvas')}>
              {doc.model ? (
                <Canvas model={doc.model} selected={selected} onSelect={setSelected} mutate={doc.mutate} problemsByLine={byLine} zoom={zoom} onZoomChange={setZoom} />
              ) : (
                <MessageBar intent="warning">
                  <MessageBarBody>{t('studio.editor.fixProblems')}</MessageBarBody>
                </MessageBar>
              )}
            </section>
            {doc.model && (
              <div className={styles.properties}>
                <PropertiesPanel api={api} model={doc.model} doc={doc.doc} selected={selected} mutate={doc.mutate} />
              </div>
            )}
          </div>
        )}
      </div>
      <ProblemsPane problems={problems} checkError={doc.checkError} onOpen={openProblem} />
      {dialog === 'xaml' && id && <XamlDialog api={api} id={id} name={name} onClose={() => setDialog(undefined)} />}
      {dialog === 'solution' && (
        <SolutionDialog
          api={api}
          current={solution?.uniquename}
          onCancel={() => setDialog(undefined)}
          onChoose={(s) => {
            setSolution(s);
            setDialog(undefined);
          }}
        />
      )}
      <ConfirmDialog
        open={dialog === 'delete'}
        title={t('studio.delete.title')}
        body={t('studio.delete.body', { name })}
        confirmLabel={t('studio.delete.confirm')}
        cancelLabel={t('common.cancel')}
        onCancel={() => setDialog(undefined)}
        onConfirm={() => void remove()}
      />
      <ConfirmDialog
        open={pending !== undefined}
        title={t('studio.editor.saveFirst.title')}
        body={t('studio.editor.saveFirst.body')}
        confirmLabel={t('studio.editor.saveFirst.confirm')}
        cancelLabel={t('common.cancel')}
        onCancel={() => setPending(undefined)}
        onConfirm={() => pending && void saveThenChangeState(pending)}
      />
      <ConfirmDialog
        open={blocker.state === 'blocked'}
        title={t('studio.editor.discard.title')}
        body={t('studio.editor.discard.body')}
        confirmLabel={t('studio.editor.discard.confirm')}
        cancelLabel={t('common.cancel')}
        onCancel={() => blocker.state === 'blocked' && blocker.reset()}
        onConfirm={() => blocker.state === 'blocked' && blocker.proceed()}
      />
    </section>
  );
}
