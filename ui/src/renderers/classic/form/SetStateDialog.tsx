import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, MessageBar, MessageBarBody, Select } from '@fluentui/react-components';
import { useState } from 'react';
import { defaultStatusOf, type RecordStateMeta } from '../../../apps/record/recordStates';
import { t } from '../../../shell/i18n/t';

export interface SetStateDialogProps {
  /** `activate` moves the record to state 0; `deactivate` to one of the other states. */
  mode: 'activate' | 'deactivate';
  states: RecordStateMeta[];
  /** Changes the state and status reason (one PATCH); false when it failed (the dialog stays open). */
  onSetState: (state: number, status?: number) => Promise<boolean>;
  onClose: () => void;
  /** The failure of the last change, as the form reports it. */
  error?: string;
  /** How many records change (a view's selection); the title says so when more than one. */
  count?: number;
}

/**
 * Activate or Deactivate: the status reasons of the target state, its `DefaultStatus` selected. A table with more than one
 * inactive state (an activity: Completed, Canceled) also asks which state.
 */
export function SetStateDialog({ mode, states, onSetState, onClose, error, count = 1 }: SetStateDialogProps) {
  const targets = states.filter((s) => (mode === 'activate' ? s.value === 0 : s.value !== 0));
  const [target, setTarget] = useState(targets[0]);
  const [status, setStatus] = useState(target ? defaultStatusOf(target) : undefined);
  const [busy, setBusy] = useState(false);
  const [attempted, setAttempted] = useState(false);
  const label = t(mode === 'activate' ? 'record.cmd.activate' : 'record.cmd.deactivate');

  const pickState = (value: string) => {
    const next = targets.find((s) => String(s.value) === value);
    if (!next) return;
    setTarget(next);
    setStatus(defaultStatusOf(next));
  };
  const confirm = async () => {
    if (!target || busy) return;
    setBusy(true);
    setAttempted(true);
    try {
      if (await onSetState(target.value, status)) onClose();
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>
            {count > 1
              ? t(mode === 'activate' ? 'record.setState.activateTitleMany' : 'record.setState.deactivateTitleMany', { count })
              : t(mode === 'activate' ? 'record.setState.activateTitle' : 'record.setState.deactivateTitle')}
          </DialogTitle>
          <DialogContent>
            {attempted && error && (
              <MessageBar intent="error">
                <MessageBarBody>{error}</MessageBarBody>
              </MessageBar>
            )}
            {targets.length > 1 && (
              <Field label={t('record.setState.state')}>
                <Select value={String(target?.value)} onChange={(_, d) => pickState(d.value)}>
                  {targets.map((s) => (
                    <option key={s.value} value={s.value}>
                      {s.label}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
            {target && target.statuses.length > 0 && (
              <Field label={t('record.setState.status')}>
                <Select value={String(status)} onChange={(_, d) => setStatus(Number(d.value))}>
                  {target.statuses.map((s) => (
                    <option key={s.value} value={s.value}>
                      {s.label}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onClose}>
              {t('common.cancel')}
            </Button>
            <Button appearance="primary" disabled={!target || busy} onClick={() => void confirm()}>
              {label}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
