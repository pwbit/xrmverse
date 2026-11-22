import type { XrmApi } from '../../shell/api/client';

/** A currency as stored; a column the server holds as NULL is null here. */
export interface Currency {
  id: string;
  code: string;
  name: string | null;
  symbol: string | null;
  precision: number | null;
  rate: number | null;
  custom: boolean;
  active: boolean;
}

export interface CurrencyOrganization {
  id: string;
  baseCurrencyId?: string;
  pricingPrecision: number | null;
}

/** The Web API leaves a NULL column out of a $select response, so every column but the key may be missing. */
interface RawCurrency {
  transactioncurrencyid: string;
  isocurrencycode?: string | null;
  currencyname?: string | null;
  currencysymbol?: string | null;
  currencyprecision?: number | null;
  exchangerate?: number | null;
  currencytype?: number | null;
  statecode?: number | null;
}

interface RawOrganization {
  organizationid: string;
  _basecurrencyid_value?: string | null;
  pricingdecimalprecision?: number | null;
}

export async function loadCurrencies(api: XrmApi): Promise<Currency[]> {
  const select = 'transactioncurrencyid,isocurrencycode,currencyname,currencysymbol,currencyprecision,exchangerate,currencytype,statecode';
  const rows: RawCurrency[] = [];
  for await (const page of api.list<RawCurrency>(`transactioncurrencies?$select=${select}&$orderby=isocurrencycode`)) rows.push(...page);
  return rows.map((r) => ({
    id: r.transactioncurrencyid,
    code: r.isocurrencycode ?? '',
    name: r.currencyname ?? null,
    symbol: r.currencysymbol ?? null,
    precision: r.currencyprecision ?? null,
    rate: r.exchangerate ?? null,
    custom: r.currencytype === 1,
    active: r.statecode === 0,
  }));
}

export async function loadOrganization(api: XrmApi): Promise<CurrencyOrganization> {
  const page = await api.page<RawOrganization>('organizations?$select=organizationid,_basecurrencyid_value,pricingdecimalprecision');
  const org = page.value[0];
  if (!org) throw new Error('The server returned no organization.');
  return { id: org.organizationid, baseCurrencyId: org._basecurrencyid_value ?? undefined, pricingPrecision: org.pricingdecimalprecision ?? null };
}

export interface NewCurrency {
  code: string;
  custom: boolean;
  name: string;
  symbol: string;
  precision: number;
  rate: number;
}

/** The server judges every value (code, rate range, precision); its refusal is an XrmError with its message. */
export async function createCurrency(api: XrmApi, c: NewCurrency): Promise<void> {
  await api.post('transactioncurrencies', {
    isocurrencycode: c.code,
    currencytype: c.custom ? 1 : 0,
    currencyname: c.name,
    currencysymbol: c.symbol,
    currencyprecision: c.precision,
    exchangerate: c.rate,
  });
}

export const setRate = (api: XrmApi, id: string, rate: number): Promise<void> => api.patch(`transactioncurrencies(${id})`, { exchangerate: rate });

export const setActive = (api: XrmApi, id: string, active: boolean): Promise<void> =>
  api.patch(`transactioncurrencies(${id})`, { statecode: active ? 0 : 1, statuscode: active ? 1 : 2 });

export const setPricingPrecision = (api: XrmApi, organizationId: string, precision: number): Promise<void> =>
  api.patch(`organizations(${organizationId})`, { pricingdecimalprecision: precision });
