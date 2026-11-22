import { render } from '@testing-library/react';
import { CUSTOMERS_FETCH, CUSTOMERS_LAYOUT, QUICK_FIND_FETCH, QUICK_FIND_LAYOUT } from '../../test/accountViews';
import { mockApps, ROLE_SALES, SITEMAP_ID, titled, type AppsMock, type MockTable, type MockView } from '../../apps/testSupport';
import { App } from '../../shell/App';

export const APP_ID = '11111111-0000-0000-0000-000000000001';
export const FV = '@OData.Community.Display.V1.FormattedValue';


const SITEMAP = `<SiteMap><Area Id="sales">${titled('Sales')}<Group Id="customers">${titled('Customers')}
  <SubArea Id="accounts" Entity="account">${titled('Accounts', 'Aziende')}</SubArea>
  <SubArea Id="contacts" Entity="contact">${titled('Contacts')}</SubArea>
</Group></Area></SiteMap>`;

/**
 * Invented (ruling R7: OpenProject365 has no link-entity view): the default public view with a link-entity column
 * (`pc.emailaddress1`) and a hidden cell.
 */
export const ACTIVE: MockView = {
  id: 'a0000000-0000-0000-0000-000000000001',
  name: 'Active Accounts',
  entity: 'account',
  isdefault: true,
  layoutxml: `<grid name="resultset" jump="name" select="1" icon="1" preview="1"><row name="result" id="accountid">
    <cell name="name" width="250" /><cell name="op365_vatnumber" width="130" /><cell name="primarycontactid" width="180" />
    <cell name="pc.emailaddress1" width="200" /><cell name="accountid" width="100" ishidden="1" /></row></grid>`,
  fetchxml: `<fetch version="1.0" mapping="logical"><entity name="account">
    <attribute name="name" /><attribute name="op365_vatnumber" /><attribute name="primarycontactid" /><attribute name="accountid" />
    <order attribute="name" descending="false" />
    <filter type="and"><condition attribute="statecode" operator="eq" value="0" /></filter>
    <link-entity name="contact" from="contactid" to="primarycontactid" link-type="outer" alias="pc"><attribute name="emailaddress1" /></link-entity>
  </entity></fetch>`,
};

/** A public view that is not the default (src/test/accountViews.ts). */
export const CUSTOMERS: MockView = {
  id: 'd04e2911-e2cf-f111-a05b-7ced8d2d53e4',
  name: 'Customers with Projects',
  entity: 'account',
  fetchxml: CUSTOMERS_FETCH,
  layoutxml: CUSTOMERS_LAYOUT,
};

/** The quick find view of account (src/test/accountViews.ts). */
export const QUICK_FIND: MockView = {
  id: '2d1187c4-23fe-4bb5-9647-43bb1c6ddbd1',
  name: 'Quick Find Active Accounts',
  entity: 'account',
  querytype: 4,
  isdefault: true,
  fetchxml: QUICK_FIND_FETCH,
  layoutxml: QUICK_FIND_LAYOUT,
};

export const MINE: MockView = {
  id: 'e0000000-0000-0000-0000-000000000001',
  name: 'My big accounts',
  entity: 'account',
  fetchxml: '<fetch><entity name="account"><attribute name="name" /><order attribute="name" /></entity></fetch>',
  layoutxml: '<grid><row><cell name="name" width="300" /></row></grid>',
};

export const TABLES: Record<string, MockTable> = {
  account: {
    entitySetName: 'accounts',
    primaryId: 'accountid',
    labels: {
      name: { 1033: 'Account Name', 1040: 'Nome account' },
      op365_vatnumber: { 1033: 'VAT Number', 1040: 'Partita IVA' },
      primarycontactid: { 1033: 'Primary Contact', 1040: 'Referente principale' },
      op365_customertype: { 1033: 'Customer Type' },
    },
  },
  contact: { entitySetName: 'contacts', primaryId: 'contactid', labels: { emailaddress1: { 1033: 'Email' } } },
};

export const ROW = {
  accountid: 'f0000000-0000-0000-0000-000000000001',
  name: 'Contoso',
  op365_vatnumber: 'IT01234567890',
  _primarycontactid_value: 'c0000000-0000-0000-0000-000000000001',
  [`_primarycontactid_value${FV}`]: 'Ada Lovelace',
  '_primarycontactid_value@Microsoft.Dynamics.CRM.lookuplogicalname': 'contact',
  'pc.emailaddress1': 'ada@example.com',
};

/** `n` rows `Account 1`… with distinct ids. */
export const rows = (n: number, from = 1) =>
  Array.from({ length: n }, (_, i) => ({
    accountid: `f0000000-0000-0000-0000-${String(from + i).padStart(12, '0')}`,
    name: `Account ${from + i}`,
  }));

/** The `fetchXml` of each records request, parsed. */
export type Fetches = Element[];

/** Opens the Accounts list of the app (any extra `main.aspx` parameters in `query`); returns the fetches sent. */
export function openAccounts(
  options: Partial<AppsMock> & { query?: string; wrapper?: React.JSXElementConstructor<{ children: React.ReactNode }>; answer?: (fetch: Element) => Record<string, unknown> } = {},
): { fetches: Fetches; requests: string[] } {
  const fetches: Fetches = [];
  const { query = '', wrapper, answer = () => ({ value: [ROW] }), ...mock } = options;
  const requests = mockApps({
    apps: [{ appmoduleid: APP_ID, name: 'Sales Hub', roles: [ROLE_SALES], siteMapId: SITEMAP_ID }],
    userRoles: [ROLE_SALES],
    privileges: ['prvReadAccount', 'prvReadContact'],
    siteMapXml: SITEMAP,
    savedQueries: [CUSTOMERS, ACTIVE, QUICK_FIND],
    userQueries: [MINE],
    tables: TABLES,
    records: (_set, fetch) => {
      fetches.push(fetch);
      return answer(fetch);
    },
    ...mock,
  });
  render(<App initialEntries={[`/main.aspx?appid=${APP_ID}&pagetype=entitylist&etn=account${query}`]} />, { wrapper });
  return { fetches, requests };
}

/** The values of a parsed fetch the tests check. */
export const orderOf = (fetch: Element) =>
  [...fetch.getElementsByTagName('order')].map((o) => `${o.getAttribute('attribute')} ${o.getAttribute('descending') === 'true' ? 'desc' : 'asc'}`);
