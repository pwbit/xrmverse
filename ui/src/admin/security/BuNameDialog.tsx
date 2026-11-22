import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input } from '@fluentui/react-components';
import { useState } from 'react';
import { t } from '../../shell/i18n/t';

/** Asks for one business unit name (create child, rename); `onSubmit` owns the request and reports its own errors. */
export function BuNameDialog(p: {
  title: string;
  initial: string;
  submitLabel: string;
  onSubmit: (name: string) => Promise<boolean>;
  onClose: () => void;
}) {
  const [name, setName] = useState(p.initial);
  const [busy, setBusy] = useState(false);

  async function submit() {
    setBusy(true);
    const ok = await p.onSubmit(name.trim());
    setBusy(false);
    if (ok) p.onClose();
  }

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && p.onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{p.title}</DialogTitle>
          <DialogContent>
            <Field label={t('bu.field.name')} required>
              <Input value={name} onChange={(_, d) => setName(d.value)} />
            </Field>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={p.onClose}>
              {t('common.cancel')}
            </Button>
            <Button appearance="primary" disabled={!name.trim() || busy} onClick={() => void submit()}>
              {p.submitLabel}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
