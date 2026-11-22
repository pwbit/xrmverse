import {
  Button,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  MessageBar,
  MessageBarBody,
  Spinner,
} from '@fluentui/react-components';
import { useEffect, useState } from 'react';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';
import { componentTypeLabel } from './componentTypes';

/** The answer of `POST /admin/solutions/{name}/uninstall` and `/promote`. */
export interface SolutionPlan {
  deleted: { type: number; name: string }[];
  kept: { type: number; name: string; reason?: string }[];
  restored: string[];
  dataLoss: { table: string; column?: string | null; rows: number }[];
}

export interface PlanDialogLabels {
  title: string;
  intro: string;
  confirm: string;
  done: string;
}

/**
 * Shared body of the Uninstall and Apply upgrade dialogs: fetches the dry-run plan (`dryRun: true`), lists what is deleted,
 * restored and kept, and, when the plan loses data, keeps the confirm button disabled until the data-loss box is ticked.
 * Confirming posts `dropData: true` when data is lost, `false` otherwise, with `expectedDataLoss`: the tables and columns the
 * user saw. Plan drift: the plan may change between this dry run and the confirm (a row written, a column filled). The server
 * plans again under its lock and answers 409 when that plan drops anything not in `expectedDataLoss` (and 400 when it drops
 * data with `dropData: false`); on either, the dialog shows the error and loads the plan again, so the user confirms what
 * is now lost.
 */
export function PlanDialog({
  action,
  solutionName,
  labels,
  onClose,
}: {
  action: 'uninstall' | 'promote';
  solutionName: string;
  labels: PlanDialogLabels;
  onClose: (changed: boolean) => void;
}) {
  const { api } = useAuth();
  const notify = useNotify();
  const path = `solutions/${encodeURIComponent(solutionName)}/${action}`;
  const [plan, setPlan] = useState<SolutionPlan>();
  const [error, setError] = useState<string>();
  const [acknowledged, setAcknowledged] = useState(false);
  const [busy, setBusy] = useState(false);

  const [revision, setRevision] = useState(0);

  useEffect(() => {
    if (!api) return;
    let live = true;
    api
      .admin<SolutionPlan>('POST', path, { dropData: false, dryRun: true })
      .then((p) => live && setPlan(p))
      .catch((e) => live && setError(e instanceof Error ? e.message : String(e)));
    return () => {
      live = false;
    };
  }, [api, path, revision]);

  const loss = plan?.dataLoss ?? [];
  async function confirm() {
    if (!api || !plan) return;
    setBusy(true);
    try {
      await api.admin<SolutionPlan>('POST', path, {
        dropData: loss.length > 0,
        dryRun: false,
        expectedDataLoss: loss.map((l) => ({ table: l.table, column: l.column ?? null })),
      });
      notify.success(labels.done);
      onClose(true);
    } catch (e) {
      setBusy(false);
      notify.error(e);
      const status = (e as { status?: number } | null)?.status;
      if (status === 409 || status === 400) {
        // The plan changed since the dry run: show the new one, to be confirmed again.
        setPlan(undefined);
        setAcknowledged(false);
        setRevision((r) => r + 1);
      }
    }
  }

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !busy && onClose(false)}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{labels.title}</DialogTitle>
          <DialogContent>
            <p>{labels.intro}</p>
            {!plan && !error && <Spinner label={t('solutions.plan.loading')} />}
            {error && (
              <MessageBar intent="error">
                <MessageBarBody>{t('solutions.plan.failed', { detail: error })}</MessageBarBody>
              </MessageBar>
            )}
            {plan && (
              <>
                <h4>{t('solutions.plan.deleted', { count: plan.deleted.length })}</h4>
                {plan.deleted.length === 0 ? (
                  <p>{t('solutions.plan.none')}</p>
                ) : (
                  <ul>
                    {plan.deleted.map((d, i) => (
                      <li key={i}>{t('solutions.plan.item', { name: d.name, type: componentTypeLabel(d.type) })}</li>
                    ))}
                  </ul>
                )}
                <h4>{t('solutions.plan.restored', { count: plan.restored.length })}</h4>
                {plan.restored.length === 0 ? (
                  <p>{t('solutions.plan.none')}</p>
                ) : (
                  <ul>
                    {plan.restored.map((r, i) => (
                      <li key={i}>{r}</li>
                    ))}
                  </ul>
                )}
                <h4>{t('solutions.plan.kept', { count: plan.kept.length })}</h4>
                {plan.kept.length === 0 ? (
                  <p>{t('solutions.plan.none')}</p>
                ) : (
                  <ul>
                    {plan.kept.map((k, i) => (
                      <li key={i}>
                        {t('solutions.plan.keptItem', { name: k.name, type: componentTypeLabel(k.type), reason: k.reason ?? '' })}
                      </li>
                    ))}
                  </ul>
                )}
                {loss.length > 0 && (
                  <>
                    <MessageBar intent="warning">
                      <MessageBarBody>{t('solutions.plan.lossWarning')}</MessageBarBody>
                    </MessageBar>
                    <ul>
                      {loss.map((l, i) => (
                        <li key={i}>
                          {l.column
                            ? t('solutions.plan.lossColumn', { table: l.table, column: l.column, rows: l.rows })
                            : t('solutions.plan.lossTable', { table: l.table, rows: l.rows })}
                        </li>
                      ))}
                    </ul>
                    <Checkbox
                      label={t('solutions.plan.lossAck')}
                      checked={acknowledged}
                      disabled={busy}
                      onChange={(_, d) => setAcknowledged(d.checked === true)}
                    />
                  </>
                )}
              </>
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" disabled={busy} onClick={() => onClose(false)}>
              {t('common.cancel')}
            </Button>
            <Button appearance="primary" disabled={!plan || busy || (loss.length > 0 && !acknowledged)} onClick={() => void confirm()}>
              {labels.confirm}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
