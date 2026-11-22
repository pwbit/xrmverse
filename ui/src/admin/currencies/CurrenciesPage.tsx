import {
  Badge,
  Button,
  MessageBar,
  MessageBarBody,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
} from '@fluentui/react-components';
import { useCallback, useEffect, useState } from 'react';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';
import { ConfirmDialog } from '../common/ConfirmDialog';
import { RowCommand, SelectCell, SelectHeaderCell, selectableRow, useRowSelection } from '../common/rowSelection';
import { CurrencyDialog } from './CurrencyDialog';
import { PrecisionDialog, RateDialog } from './CurrencyEditDialogs';
import {
  loadCurrencies,
  loadOrganization,
  setActive,
  setPricingPrecision,
  setRate,
  type Currency,
  type CurrencyOrganization,
} from './currencyApi';
import { PageHeader } from '../../shell/layout/PageHeader';
import { ActivateIcon, DeactivateIcon, EditIcon, NewIcon } from '../../shell/icons';

const messageOf = (e: unknown) => (e instanceof Error ? e.message : String(e));

/** A stored number as the text of its field: '' for null (the server holds no value). */
const textOf = (value: number | null | undefined) => (value == null ? '' : String(value));

/**
 * Currencies: the exchange rate of each currency to the base currency, activation, new currencies and the organization's pricing
 * precision. The table is read-only: the command bar edits the selected currency (rate, activation) and the pricing precision, each
 * in a dialog. The base currency is fixed (rate 1, always active); the server judges every value and its refusal is shown as it is.
 */
export function CurrenciesPage() {
  const { api } = useAuth();
  const notify = useNotify();
  const [currencies, setCurrencies] = useState<Currency[]>();
  const [org, setOrg] = useState<CurrencyOrganization>();
  const [loadError, setLoadError] = useState<string>();
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [creating, setCreating] = useState(false);
  const [deactivating, setDeactivating] = useState<Currency>();
  const [editingRate, setEditingRate] = useState<Currency>();
  const [editingPrecision, setEditingPrecision] = useState(false);
  const selection = useRowSelection(currencies ?? [], (c) => c.id);
  const sel = selection.selected;

  const reload = useCallback(
    async (live: () => boolean = () => true) => {
      if (!api) return;
      try {
        const [rows, organization] = await Promise.all([loadCurrencies(api), loadOrganization(api)]);
        if (!live()) return;
        setLoadError(undefined);
        setCurrencies(rows);
        setOrg(organization);
      } catch (e) {
        if (live()) setLoadError(messageOf(e));
      }
    },
    [api],
  );

  useEffect(() => {
    let live = true;
    void reload(() => live);
    return () => {
      live = false;
    };
  }, [reload]);

  /** Runs one write, then reads everything again, so the page shows what the server stored. Resolves whether the write succeeded. */
  async function write(action: () => Promise<void>, done: string): Promise<boolean> {
    setBusy(true);
    setError(undefined);
    try {
      await action();
      notify.success(done);
      await reload();
      return true;
    } catch (e) {
      setError(messageOf(e));
      return false;
    } finally {
      setBusy(false);
    }
  }

  /** Closes an edit dialog; a refusal shown in it goes with it. */
  function closeDialog() {
    setEditingRate(undefined);
    setEditingPrecision(false);
    setError(undefined);
  }

  const isBase = (c: Currency) => c.id === org?.baseCurrencyId;
  const loaded = !!(currencies && org);
  const dialogOpen = !!editingRate || editingPrecision;

  return (
    <section>
      <PageHeader
        title={t('nav.currencies')}
        actions={
          <>
            <Button appearance="primary" icon={<NewIcon />} disabled={!loaded || busy} onClick={() => setCreating(true)}>
              {t('currencies.new.button')}
            </Button>
            <Button appearance="subtle" disabled={!loaded || busy} onClick={() => setEditingPrecision(true)}>
              {t('currencies.precisionOpen')}
            </Button>
            {sel && (
              <>
                <RowCommand
                  icon={<EditIcon />}
                  aria-label={t('currencies.editRateFor', { code: sel.code })}
                  // The base currency's rate is always 1: the command stays visible, disabled, with the reason as its description.
                  title={isBase(sel) ? t('currencies.baseHint') : undefined}
                  disabled={busy || isBase(sel)}
                  onClick={() => setEditingRate(sel)}
                >
                  {t('currencies.editRate')}
                </RowCommand>
                {!isBase(sel) &&
                  (sel.active ? (
                    <RowCommand icon={<DeactivateIcon />} disabled={busy} aria-label={t('currencies.deactivateFor', { code: sel.code })} onClick={() => setDeactivating(sel)}>
                      {t('currencies.deactivate')}
                    </RowCommand>
                  ) : (
                    <RowCommand
                      icon={<ActivateIcon />}
                      disabled={busy}
                      aria-label={t('currencies.activateFor', { code: sel.code })}
                      onClick={() => void write(() => setActive(api!, sel.id, true), t('currencies.activated', { code: sel.code }))}
                    >
                      {t('currencies.activate')}
                    </RowCommand>
                  ))}
              </>
            )}
          </>
        }
      />
      <p>{t('currencies.intro')}</p>
      {loadError && (
        <MessageBar intent="error">
          <MessageBarBody>{t('currencies.loadFailed', { detail: loadError })}</MessageBarBody>
        </MessageBar>
      )}
      {!loaded && !loadError && <Spinner label={t('common.loading')} />}
      {currencies && org && (
        <>
          {error && !dialogOpen && (
            <MessageBar intent="error">
              <MessageBarBody>{error}</MessageBarBody>
            </MessageBar>
          )}
          <Table aria-label={t('nav.currencies')}>
            <TableHeader>
              <TableRow>
                <SelectHeaderCell />
                <TableHeaderCell>{t('currencies.col.code')}</TableHeaderCell>
                <TableHeaderCell>{t('currencies.col.name')}</TableHeaderCell>
                <TableHeaderCell>{t('currencies.col.symbol')}</TableHeaderCell>
                <TableHeaderCell>{t('currencies.col.precision')}</TableHeaderCell>
                <TableHeaderCell>{t('currencies.col.rate')}</TableHeaderCell>
                <TableHeaderCell>{t('currencies.col.state')}</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {currencies.map((c) => (
                <TableRow key={c.id} {...selectableRow(selection, c)}>
                  <SelectCell selection={selection} item={c} name={c.code} />
                  <TableCell>
                    {c.code} {isBase(c) && <Badge appearance="filled">{t('currencies.base')}</Badge>}
                  </TableCell>
                  <TableCell>{c.name ?? ''}</TableCell>
                  <TableCell>{c.symbol ?? ''}</TableCell>
                  <TableCell>{textOf(c.precision)}</TableCell>
                  <TableCell>{textOf(c.rate)}</TableCell>
                  <TableCell>{c.active ? t('currencies.state.active') : t('currencies.state.inactive')}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </>
      )}
      {creating && (
        <CurrencyDialog
          onClose={() => setCreating(false)}
          onCreated={() => {
            setCreating(false);
            void reload();
          }}
        />
      )}
      {editingRate && (
        <RateDialog
          code={editingRate.code}
          stored={textOf(editingRate.rate)}
          busy={busy}
          error={error}
          onClose={closeDialog}
          onSave={(rate) =>
            void write(() => setRate(api!, editingRate.id, rate), t('currencies.rateSaved', { code: editingRate.code })).then((ok) => ok && closeDialog())
          }
        />
      )}
      {editingPrecision && org && (
        <PrecisionDialog
          stored={textOf(org.pricingPrecision)}
          busy={busy}
          error={error}
          onClose={closeDialog}
          onSave={(value) =>
            void write(() => setPricingPrecision(api!, org.id, value), t('currencies.precisionSaved', { value: String(value) })).then((ok) => ok && closeDialog())
          }
        />
      )}
      <ConfirmDialog
        open={!!deactivating}
        title={t('currencies.confirmTitle')}
        body={deactivating ? t('currencies.confirmBody', { code: deactivating.code }) : ''}
        confirmLabel={t('currencies.deactivate')}
        cancelLabel={t('common.cancel')}
        onConfirm={() => {
          const c = deactivating;
          setDeactivating(undefined);
          if (c) void write(() => setActive(api!, c.id, false), t('currencies.deactivated', { code: c.code }));
        }}
        onCancel={() => setDeactivating(undefined)}
      />
    </section>
  );
}
