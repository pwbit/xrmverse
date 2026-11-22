import { Body1, Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, MessageBar, MessageBarBody } from '@fluentui/react-components';
import { useState } from 'react';
import { EntityPicker, type PickedEntity } from '../../../admin/common/EntityPicker';
import { t } from '../../../shell/i18n/t';

export interface AddExistingDialogProps {
  /** The related table's display name. */
  tableLabel: string;
  entitySetName: string;
  primaryId: string;
  primaryName: string;
  /** An OData `$filter` that narrows the search (one-to-many: rows not yet related to this record). */
  filter?: string;
  /** Relates the chosen rows; a failure is shown in the dialog, which stays open. */
  onAdd: (ids: string[]) => Promise<void>;
  onClose: () => void;
}

/** Add existing of a subgrid (#118): a search of the related table where the user picks one or more rows. */
export function AddExistingDialog({ tableLabel, entitySetName, primaryId, primaryName, filter, onAdd, onClose }: AddExistingDialogProps) {
  const [picked, setPicked] = useState<PickedEntity[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const toggle = (p: PickedEntity) => setPicked((all) => (all.some((x) => x.id === p.id) ? all.filter((x) => x.id !== p.id) : [...all, p]));
  const add = async () => {
    setBusy(true);
    setError(undefined);
    try {
      await onAdd(picked.map((p) => p.id));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !busy && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('record.subgrid.addExistingTitle', { table: tableLabel })}</DialogTitle>
          <DialogContent>
            <EntityPicker
              entitySet={entitySetName}
              idAttribute={primaryId}
              nameAttribute={primaryName}
              filter={filter}
              label={t('xrm.lookup.search', { table: tableLabel })}
              selected={picked.length === 1 ? picked[0] : undefined}
              onSelect={toggle}
            />
            {picked.length > 0 && <Body1 block>{t('xrm.lookup.selected', { names: picked.map((p) => p.name).join(', ') })}</Body1>}
            {error && (
              <MessageBar intent="error">
                <MessageBarBody>{error}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" disabled={picked.length === 0 || busy} onClick={() => void add()}>
              {t('xrm.lookup.add')}
            </Button>
            <Button appearance="secondary" disabled={busy} onClick={onClose}>
              {t('xrm.dialog.cancel')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
