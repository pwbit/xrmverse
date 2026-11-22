import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, MessageBar, MessageBarBody, Select } from '@fluentui/react-components';
import { useState } from 'react';
import { EntityPicker, type PickedEntity } from '../../../admin/common/EntityPicker';
import type { PrincipalType } from '../../../apps/record/access';
import type { LookupValue } from '../../../admin/tables/formValues';
import { t } from '../../../shell/i18n/t';

export interface PickedPrincipal extends PickedEntity {
  type: PrincipalType;
}

const SOURCES = {
  systemuser: { set: 'systemusers', id: 'systemuserid', name: 'fullname' },
  team: { set: 'teams', id: 'teamid', name: 'name' },
} as const;

/** A user or a team, searched by name: the type first, then the search-as-you-type list of that table. */
export function PrincipalPicker({ label, picked, onPick }: { label: string; picked?: PickedPrincipal; onPick: (principal: PickedPrincipal) => void }) {
  const [type, setType] = useState<PrincipalType>('systemuser');
  const source = SOURCES[type];
  return (
    <div>
      <Field label={label}>
        <Select value={type} onChange={(_, d) => setType(d.value as PrincipalType)}>
          <option value="systemuser">{t('record.principal.user')}</option>
          <option value="team">{t('record.principal.team')}</option>
        </Select>
      </Field>
      <EntityPicker
        key={type}
        entitySet={source.set}
        idAttribute={source.id}
        nameAttribute={source.name}
        label={t(type === 'team' ? 'record.principal.searchTeam' : 'record.principal.searchUser')}
        selected={picked?.type === type ? picked : undefined}
        onSelect={(entity) => onPick({ ...entity, type })}
      />
    </div>
  );
}

export interface AssignDialogProps {
  /** Sets the owner (the record's Assign); false when it failed (the dialog stays open). */
  onAssign: (owner: LookupValue) => Promise<boolean>;
  onClose: () => void;
  /** The failure of the last assign, as the form reports it. */
  error?: string;
  /** How many records are assigned (a view's selection); the title says so when more than one. */
  count?: number;
}

/** The Assign command: pick a user or a team, then the owner changes. */
export function AssignDialog({ onAssign, onClose, error, count = 1 }: AssignDialogProps) {
  const [picked, setPicked] = useState<PickedPrincipal>();
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<string>();
  const [attempted, setAttempted] = useState(false);
  const assign = async () => {
    if (!picked || busy) return;
    setBusy(true);
    setFailure(undefined);
    setAttempted(true);
    try {
      if (await onAssign({ id: picked.id, name: picked.name, target: picked.type, set: SOURCES[picked.type].set })) onClose();
    } catch (e) {
      setFailure(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };
  const shown = failure ?? (attempted ? error : undefined);
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{count > 1 ? t('record.assign.titleMany', { count }) : t('record.assign.title')}</DialogTitle>
          <DialogContent>
            {shown && (
              <MessageBar intent="error">
                <MessageBarBody>{shown}</MessageBarBody>
              </MessageBar>
            )}
            <PrincipalPicker label={t('record.assign.to')} picked={picked} onPick={setPicked} />
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onClose}>
              {t('common.cancel')}
            </Button>
            <Button appearance="primary" disabled={!picked || busy} onClick={() => void assign()}>
              {t('record.cmd.assign')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
