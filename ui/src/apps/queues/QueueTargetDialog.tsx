import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, MessageBar, MessageBarBody, Select } from '@fluentui/react-components';
import { useState } from 'react';
import { EntityPicker, type PickedEntity } from '../../admin/common/EntityPicker';
import { t } from '../../shell/i18n/t';
import type { RouteTarget } from './queueCommands';

const SOURCES = {
  queue: { set: 'queues', id: 'queueid', name: 'name', search: 'apps.queues.pickQueue' },
  systemuser: { set: 'systemusers', id: 'systemuserid', name: 'fullname', search: 'apps.queues.searchUser' },
  team: { set: 'teams', id: 'teamid', name: 'name', search: 'apps.queues.searchTeam' },
} as const;

export interface QueueTargetDialogProps {
  /** `add`: a queue only; `route`: a queue, a user or a team. */
  mode: 'add' | 'route';
  /** How many records are affected; the title says so when more than one. */
  count?: number;
  /** Runs the command on the picked target; false when it failed (the dialog stays open). */
  onConfirm: (target: RouteTarget) => Promise<boolean>;
  onClose: () => void;
}

/** The queue lookup of Add to queue and the target picker of Route. */
export function QueueTargetDialog({ mode, count = 1, onConfirm, onClose }: QueueTargetDialogProps) {
  const [kind, setKind] = useState<RouteTarget['kind']>('queue');
  const [picked, setPicked] = useState<PickedEntity>();
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<string>();
  const source = SOURCES[kind];
  const title =
    mode === 'add'
      ? count > 1 ? t('apps.queues.addTitleMany', { count }) : t('apps.queues.addTitle')
      : count > 1 ? t('apps.queues.routeTitleMany', { count }) : t('apps.queues.routeTitle');
  const run = async () => {
    if (!picked || busy) return;
    setBusy(true);
    setFailure(undefined);
    try {
      if (await onConfirm({ kind, id: picked.id, name: picked.name })) onClose();
    } catch (e) {
      setFailure(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{title}</DialogTitle>
          <DialogContent>
            {failure && (
              <MessageBar intent="error">
                <MessageBarBody>{failure}</MessageBarBody>
              </MessageBar>
            )}
            {mode === 'route' && (
              <Field label={t('apps.queues.targetType')}>
                <Select
                  value={kind}
                  onChange={(_, d) => {
                    setKind(d.value as RouteTarget['kind']);
                    setPicked(undefined);
                  }}
                >
                  {(['queue', 'systemuser', 'team'] as const).map((k) => (
                    <option key={k} value={k}>
                      {t(`apps.queues.type.${k}`)}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
            <EntityPicker key={kind} entitySet={source.set} idAttribute={source.id} nameAttribute={source.name} label={t(source.search)} selected={picked} onSelect={setPicked} />
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onClose}>
              {t('common.cancel')}
            </Button>
            <Button appearance="primary" disabled={!picked || busy} onClick={() => void run()}>
              {mode === 'add' ? t('apps.queues.add') : t('apps.queues.route')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
