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
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';
import { createCurrency } from './currencyApi';

/**
 * Creates a currency. Nothing is checked here beyond "a number": the server judges code, rate and precision, and its refusal is
 * shown as it is in a message bar inside the dialog, which stays open with what was typed.
 */
export function CurrencyDialog({ onClose, onCreated }: { onClose: () => void; onCreated: () => void }) {
  const { api } = useAuth();
  const notify = useNotify();
  const [refusal, setRefusal] = useState<string>();
  const [code, setCode] = useState('');
  const [custom, setCustom] = useState(false);
  const [name, setName] = useState('');
  const [symbol, setSymbol] = useState('');
  const [precision, setPrecision] = useState('2');
  const [rate, setRate] = useState('');
  const [rateError, setRateError] = useState<string>();
  const [busy, setBusy] = useState(false);

  async function create() {
    if (!api) return;
    const value = Number(rate);
    if (rate.trim() === '' || !Number.isFinite(value)) {
      setRateError(t('currencies.err.rate', { value: rate }));
      return;
    }
    setRateError(undefined);
    setRefusal(undefined);
    setBusy(true);
    try {
      await createCurrency(api, { code: code.trim().toUpperCase(), custom, name: name.trim(), symbol: symbol.trim(), precision: Number(precision), rate: value });
      notify.success(t('currencies.created', { code: code.trim().toUpperCase() }));
      onCreated();
    } catch (e) {
      setRefusal(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('currencies.new.title')}</DialogTitle>
          <DialogContent>
            {refusal && (
              <MessageBar intent="error">
                <MessageBarBody>{refusal}</MessageBarBody>
              </MessageBar>
            )}
            <Field label={t('currencies.field.code')} required>
              <Input value={code} onChange={(_, d) => setCode(d.value)} />
            </Field>
            <Field label={t('currencies.field.type')}>
              <Select value={custom ? 'custom' : 'system'} onChange={(_, d) => setCustom(d.value === 'custom')}>
                <option value="system">{t('currencies.type.system')}</option>
                <option value="custom">{t('currencies.type.custom')}</option>
              </Select>
            </Field>
            <Field label={t('currencies.field.name')} required>
              <Input value={name} onChange={(_, d) => setName(d.value)} />
            </Field>
            <Field label={t('currencies.field.symbol')} required>
              <Input value={symbol} onChange={(_, d) => setSymbol(d.value)} />
            </Field>
            <Field label={t('currencies.field.precision')}>
              <Select value={precision} onChange={(_, d) => setPrecision(d.value)}>
                {[0, 1, 2, 3, 4].map((p) => (
                  <option key={p} value={String(p)}>
                    {p}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label={t('currencies.field.rate')} required validationMessage={rateError} hint={t('currencies.field.rateHint')}>
              <Input value={rate} onChange={(_, d) => setRate(d.value)} />
            </Field>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onClose}>
              {t('common.cancel')}
            </Button>
            <Button appearance="primary" disabled={!code.trim() || !name.trim() || !symbol.trim() || !rate.trim() || busy} onClick={() => void create()}>
              {t('currencies.new.submit')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
