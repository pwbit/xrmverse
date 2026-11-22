import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input } from '@fluentui/react-components';
import { useState } from 'react';
import { EntityPicker, type PickedEntity } from '../common/EntityPicker';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';

/** Creates a `systemuser` bound to a business unit (`businessunitid@odata.bind`); the server's refusal is shown as is. */
export function CreateUserDialog({ onClose, onCreated }: { onClose: () => void; onCreated: () => void }) {
  const { api } = useAuth();
  const notify = useNotify();
  const [fullname, setFullname] = useState('');
  const [domainname, setDomainname] = useState('');
  const [objectId, setObjectId] = useState('');
  const [bu, setBu] = useState<PickedEntity>();
  const [busy, setBusy] = useState(false);

  async function create() {
    if (!api || !bu) return;
    setBusy(true);
    try {
      await api.post('systemusers', {
        fullname: fullname.trim(),
        ...(domainname.trim() ? { domainname: domainname.trim() } : {}),
        ...(objectId.trim() ? { azureactivedirectoryobjectid: objectId.trim() } : {}),
        'businessunitid@odata.bind': `/businessunits(${bu.id})`,
      });
      notify.success(t('users.created', { name: fullname.trim() }));
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
          <DialogTitle>{t('users.create.title')}</DialogTitle>
          <DialogContent>
            <Field label={t('users.field.fullname')} required>
              <Input value={fullname} onChange={(_, d) => setFullname(d.value)} />
            </Field>
            <Field label={t('users.field.domainname')}>
              <Input value={domainname} onChange={(_, d) => setDomainname(d.value)} />
            </Field>
            <Field label={t('users.field.objectId')}>
              <Input value={objectId} onChange={(_, d) => setObjectId(d.value)} />
            </Field>
            <EntityPicker
              entitySet="businessunits"
              idAttribute="businessunitid"
              nameAttribute="name"
              label={t('users.field.businessUnit')}
              selected={bu}
              onSelect={setBu}
            />
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onClose}>
              {t('common.cancel')}
            </Button>
            <Button appearance="primary" disabled={!fullname.trim() || !bu || busy} onClick={() => void create()}>
              {t('users.create.submit')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
