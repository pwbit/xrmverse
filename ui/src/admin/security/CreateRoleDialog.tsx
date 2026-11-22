import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input } from '@fluentui/react-components';
import { useState } from 'react';
import { EntityPicker, type PickedEntity } from '../common/EntityPicker';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';

/** Creates a role in a business unit (roles belong to one business unit). */
export function CreateRoleDialog({ onClose, onCreated }: { onClose: () => void; onCreated: () => void }) {
  const { api } = useAuth();
  const notify = useNotify();
  const [name, setName] = useState('');
  const [bu, setBu] = useState<PickedEntity>();
  const [busy, setBusy] = useState(false);

  async function create() {
    if (!api || !bu) return;
    setBusy(true);
    try {
      await api.post('roles', { name: name.trim(), 'businessunitid@odata.bind': `/businessunits(${bu.id})` });
      notify.success(t('roles.created', { name: name.trim() }));
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
          <DialogTitle>{t('roles.create.title')}</DialogTitle>
          <DialogContent>
            <Field label={t('roles.field.name')} required>
              <Input value={name} onChange={(_, d) => setName(d.value)} />
            </Field>
            <EntityPicker
              entitySet="businessunits"
              idAttribute="businessunitid"
              nameAttribute="name"
              label={t('roles.field.businessUnit')}
              selected={bu}
              onSelect={setBu}
            />
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onClose}>
              {t('common.cancel')}
            </Button>
            <Button appearance="primary" disabled={!name.trim() || !bu || busy} onClick={() => void create()}>
              {t('roles.create.submit')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
