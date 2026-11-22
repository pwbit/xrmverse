import type { XrmApi } from '../../shell/api/client';

export interface Currency {
  id: string;
  symbol?: string;
}

/**
 * The currency a record form shows its money in, the way Dataverse picks it: the record's own `transactioncurrencyid`, else
 * (a create form) the user's default currency (`usersettings.transactioncurrencyid`), else the organization's base
 * currency (also what a record without a currency shows). Throws when a read fails: the caller says so instead of showing plain labels in silence.
 */
export async function loadCurrency(api: XrmApi, userId: string, recordCurrencyId: string | undefined, creating: boolean): Promise<Currency | undefined> {
  let id = recordCurrencyId;
  if (!id && creating) {
    const settings = await api.get<{ _transactioncurrencyid_value?: string | null }>(
      `usersettingscollection(${userId})?$select=_transactioncurrencyid_value`,
    );
    id = settings._transactioncurrencyid_value ?? undefined;
  }
  if (!id) {
    const orgs = await api.get<{ value: { _basecurrencyid_value?: string | null }[] }>('organizations?$select=_basecurrencyid_value&$top=1');
    id = orgs.value[0]?._basecurrencyid_value ?? undefined;
  }
  if (!id) return undefined;
  const row = await api.get<{ currencysymbol?: string | null }>(`transactioncurrencies(${id})?$select=currencysymbol`);
  return { id, symbol: row.currencysymbol || undefined };
}
