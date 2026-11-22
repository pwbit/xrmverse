import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Select,
} from '@fluentui/react-components';
import { useState } from 'react';
import { t } from '../../shell/i18n/t';

/** What the page passes to an edit dialog: the write runs on the page, which closes the dialog once it succeeds. */
interface EditDialogProps {
  /** The page's write is running. */
  busy: boolean;
  /** The server's refusal of the last write, shown as it is; the dialog stays open with what was typed. */
  error?: string;
  onClose: () => void;
}

/** Edits the exchange rate of one currency. Only "a number" is checked here: the server judges the value. */
export function RateDialog({ code, stored, busy, error, onSave, onClose }: EditDialogProps & { code: string; stored: string; onSave: (rate: number) => void }) {
  const [text, setText] = useState(stored);
  const [invalid, setInvalid] = useState<string>();

  function save() {
    const value = Number(text);
    if (text.trim() === '' || !Number.isFinite(value)) {
      setInvalid(t('currencies.err.rate', { value: text }));
      return;
    }
    setInvalid(undefined);
    onSave(value);
  }

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('currencies.rateFor', { code })}</DialogTitle>
          <DialogContent>
            {error && (
              <MessageBar intent="error">
                <MessageBarBody>{error}</MessageBarBody>
              </MessageBar>
            )}
            <Field label={t('currencies.rateFor', { code })} validationMessage={invalid} hint={t('currencies.field.rateHint')}>
              <Input value={text} disabled={busy} onChange={(_, d) => setText(d.value)} />
            </Field>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onClose}>
              {t('common.cancel')}
            </Button>
            <Button appearance="primary" disabled={busy || text === stored} onClick={save}>
              {t('currencies.saveRate')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

/** Edits the organization's pricing precision (0 to 4). `stored` is '' when the organization holds no value. */
export function PrecisionDialog({ stored, busy, error, onSave, onClose }: EditDialogProps & { stored: string; onSave: (precision: number) => void }) {
  const [value, setValue] = useState(stored);
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('currencies.pricingPrecision')}</DialogTitle>
          <DialogContent>
            {error && (
              <MessageBar intent="error">
                <MessageBarBody>{error}</MessageBarBody>
              </MessageBar>
            )}
            <Field label={t('currencies.pricingPrecision')} hint={t('currencies.pricingPrecisionHint')}>
              <Select value={value} disabled={busy} onChange={(_, d) => setValue(d.value)}>
                {stored === '' && <option value="" />}
                {[0, 1, 2, 3, 4].map((p) => (
                  <option key={p} value={String(p)}>
                    {p}
                  </option>
                ))}
              </Select>
            </Field>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onClose}>
              {t('common.cancel')}
            </Button>
            <Button appearance="primary" disabled={busy || value === '' || value === stored} onClick={() => onSave(Number(value))}>
              {t('currencies.precisionSave')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
