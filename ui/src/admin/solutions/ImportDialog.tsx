import {
  Button,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Label,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  useId,
} from '@fluentui/react-components';
import { useEffect, useRef, useState } from 'react';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';
import { newGuid } from '../../shell/api/uuid';
import { readBase64, summarizeImportLog, type ImportLogSummary } from './importLog';

const FV = 'OData.Community.Display.V1.FormattedValue';
const POLL_MS = 2000;
/** asyncoperation.statecode Completed, and its statuscodes Succeeded and Canceled. */
const COMPLETED = 3;
const SUCCEEDED = 30;
const CANCELED = 32;

interface Job {
  statecode: number;
  statuscode: number;
  message?: string | null;
  friendlymessage?: string | null;
  [key: string]: unknown;
}

type Phase =
  | { kind: 'idle' }
  | { kind: 'uploading' }
  | { kind: 'running'; status: string }
  | { kind: 'succeeded'; warnings: number; logError?: string }
  | { kind: 'pollError'; detail: string }
  | { kind: 'canceled' }
  | { kind: 'failed'; jobMessage?: string; log?: ImportLogSummary; logError?: string };

/**
 * Imports a solution file with `ImportSolutionAsync` and a client-generated `ImportJobId`, polls the system job every 2 s
 * until it completes, and on failure shows the failures of the `importjob` log (`importjobs(id)`, column `data`).
 */
export function ImportDialog({ onClose, onImported }: { onClose: () => void; onImported: () => void }) {
  const { api } = useAuth();
  const notify = useNotify();
  const fileInputId = useId('import-file');
  const modeId = useId('import-mode');
  const [file, setFile] = useState<File>();
  const [mode, setMode] = useState<'update' | 'upgrade' | 'stage'>('update');
  const [overwrite, setOverwrite] = useState(false);
  const [publishWorkflows, setPublishWorkflows] = useState(false);
  const [phase, setPhase] = useState<Phase>({ kind: 'idle' });
  const [attempt, setAttempt] = useState(0);
  const [job, setJob] = useState<{ asyncId: string; importJobId: string }>();
  const onImportedRef = useRef(onImported);
  onImportedRef.current = onImported;

  async function start() {
    if (!api || !file) return;
    setPhase({ kind: 'uploading' });
    try {
      const importJobId = newGuid();
      const body: Record<string, unknown> = {
        OverwriteUnmanagedCustomizations: overwrite,
        PublishWorkflows: publishWorkflows,
        CustomizationFile: await readBase64(file),
        ImportJobId: importJobId,
      };
      if (mode === 'stage') body.HoldingSolution = true;
      const response = await api.action<{ AsyncOperationId: string }>(
        mode === 'upgrade' ? 'StageAndUpgradeAsync' : 'ImportSolutionAsync',
        body,
      );
      setPhase({ kind: 'running', status: '' });
      setJob({ asyncId: response.AsyncOperationId, importJobId });
    } catch (e) {
      setPhase({ kind: 'idle' });
      notify.error(e);
    }
  }

  useEffect(() => {
    if (!api || !job) return;
    let live = true;
    let timer: ReturnType<typeof setTimeout> | undefined;

    async function readLog(): Promise<{ log?: ImportLogSummary; logError?: string }> {
      try {
        const row = await api!.get<{ data?: string | null }>(`importjobs(${job!.importJobId})?$select=data,progress`);
        return row.data ? { log: summarizeImportLog(row.data) } : {};
      } catch (e) {
        return { logError: e instanceof Error ? e.message : String(e) };
      }
    }

    async function poll() {
      try {
        const current = await api!.get<Job>(
          `asyncoperations(${job!.asyncId})?$select=statecode,statuscode,message,friendlymessage`,
          { headers: { Prefer: `odata.include-annotations="${FV}"` } },
        );
        if (!live) return;
        if (current.statecode !== COMPLETED) {
          const status = current[`statuscode@${FV}`];
          setPhase({ kind: 'running', status: typeof status === 'string' ? status : '' });
          timer = setTimeout(() => void poll(), POLL_MS);
          return;
        }
        if (current.statuscode === CANCELED) {
          setPhase({ kind: 'canceled' });
        } else if (current.statuscode === SUCCEEDED) {
          const { log, logError } = await readLog();
          if (!live) return;
          setPhase({ kind: 'succeeded', warnings: log?.warnings ?? 0, logError });
          notify.success(t('solutions.import.succeeded'));
          onImportedRef.current();
        } else {
          const { log, logError } = await readLog();
          if (!live) return;
          setPhase({ kind: 'failed', jobMessage: current.friendlymessage || current.message || undefined, log, logError });
        }
      } catch (e) {
        if (!live) return;
        // The job keeps running on the server: keep it, so the user can check again and cannot start a duplicate import.
        setPhase({ kind: 'pollError', detail: e instanceof Error ? e.message : String(e) });
        notify.error(e);
      }
    }

    void poll();
    return () => {
      live = false;
      if (timer) clearTimeout(timer);
    };
  }, [api, job, attempt, notify]);

  const busy = phase.kind === 'uploading' || phase.kind === 'running' || phase.kind === 'pollError';
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !busy && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('solutions.import.title')}</DialogTitle>
          <DialogContent>
            <div>
              <Label htmlFor={fileInputId}>{t('solutions.import.file')}</Label>
              <br />
              <input
                id={fileInputId}
                type="file"
                accept=".zip,application/zip"
                disabled={busy}
                onChange={(e) => setFile(e.target.files?.[0])}
              />
            </div>
            <div>
              <Label htmlFor={modeId}>{t('solutions.import.mode')}</Label>
              <br />
              <Select
                id={modeId}
                value={mode}
                disabled={busy || !!job}
                onChange={(_, d) => setMode(d.value as 'update' | 'upgrade' | 'stage')}
              >
                <option value="update">{t('solutions.import.mode.update')}</option>
                <option value="upgrade">{t('solutions.import.mode.upgrade')}</option>
                <option value="stage">{t('solutions.import.mode.stage')}</option>
              </Select>
            </div>
            <Checkbox
              label={t('solutions.import.overwrite')}
              checked={overwrite}
              disabled={busy}
              onChange={(_, d) => setOverwrite(d.checked === true)}
            />
            <Checkbox
              label={t('solutions.import.publishWorkflows')}
              checked={publishWorkflows}
              disabled={busy}
              onChange={(_, d) => setPublishWorkflows(d.checked === true)}
            />
            {phase.kind === 'uploading' && <Spinner label={t('solutions.import.uploading')} />}
            {phase.kind === 'running' && <Spinner label={t('solutions.import.running', { status: phase.status })} />}
            {phase.kind === 'succeeded' && (
              <MessageBar intent="success">
                <MessageBarBody>
                  {t('solutions.import.succeeded')}
                  {phase.warnings > 0 && ` ${t('solutions.import.warnings', { count: phase.warnings })}`}
                </MessageBarBody>
              </MessageBar>
            )}
            {phase.kind === 'succeeded' && phase.logError && (
              <MessageBar intent="warning">
                <MessageBarBody>{t('solutions.import.logWarning', { detail: phase.logError })}</MessageBarBody>
              </MessageBar>
            )}
            {phase.kind === 'pollError' && job && (
              <MessageBar intent="error">
                <MessageBarBody>
                  {t('solutions.import.pollFailed', { id: job.asyncId, detail: phase.detail })}
                  <Button
                    onClick={() => {
                      setPhase({ kind: 'running', status: '' });
                      setAttempt((n) => n + 1);
                    }}
                  >
                    {t('solutions.import.keepChecking')}
                  </Button>
                </MessageBarBody>
              </MessageBar>
            )}
            {phase.kind === 'canceled' && (
              <MessageBar intent="warning">
                <MessageBarBody>{t('solutions.import.canceled')}</MessageBarBody>
              </MessageBar>
            )}
            {phase.kind === 'failed' && (
              <MessageBar intent="error">
                <MessageBarBody>
                  <p>{t('solutions.import.failed')}</p>
                  {phase.log && phase.log.failures.length > 0 && (
                    <>
                      <h4>{t('solutions.import.failureTitle')}</h4>
                      <ul>
                        {phase.log.failures.map((f, i) => (
                          <li key={i}>{t('solutions.import.failureItem', { code: f.code, text: f.text })}</li>
                        ))}
                      </ul>
                    </>
                  )}
                  {phase.jobMessage && <p>{t('solutions.import.jobMessage', { message: phase.jobMessage })}</p>}
                  {phase.logError && <p>{t('solutions.import.logUnavailable', { detail: phase.logError })}</p>}
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" disabled={phase.kind === 'uploading' || phase.kind === 'running'} onClick={onClose}>
              {t('solutions.import.close')}
            </Button>
            <Button
              appearance="primary"
              disabled={!file || busy || !!job}
              onClick={() => void start()}
            >
              {t('solutions.import.submit')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
