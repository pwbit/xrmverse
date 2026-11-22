import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input } from '@fluentui/react-components';
import { useState } from 'react';
import { EntityPicker, type PickedEntity } from '../common/EntityPicker';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';

/** Creates an owner team (`teamtype` 0) in a business unit; access teams arrive with 2.16. */
export function CreateTeamDialog({ onClose, onCreated }: { onClose: () => void; onCreated: () => void }) {
  const { api } = useAuth();
  const notify = useNotify();
  const [name, setName] = useState('');
  const [bu, setBu] = useState<PickedEntity>();
  const [busy, setBusy] = useState(false);

  async function create() {
    if (!api || !bu) return;
    setBusy(true);
    try {
      await api.post('teams', { name: name.trim(), teamtype: 0, 'businessunitid@odata.bind': `/businessunits(${bu.id})` });
      notify.success(t('teams.created', { name: name.trim() }));
      onCreated();
    } catch (e) {
      notify.error(e);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('teams.create.title')}</DialogTitle>
          <DialogContent>
            <Field label={t('teams.field.name')} required>
              <Input value={name} onChange={(_, d) => setName(d.value)} />
            </Field>
            <EntityPicker
              entitySet="businessunits"
              idAttribute="businessunitid"
              nameAttribute="name"
              label={t('teams.field.businessUnit')}
              selected={bu}
              onSelect={setBu}
            />
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onClose}>
              {t('common.cancel')}
            </Button>
            <Button appearance="primary" disabled={!name.trim() || !bu || busy} onClick={() => void create()}>
              {t('teams.create.submit')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
