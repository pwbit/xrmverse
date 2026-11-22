import { describe, expect, it } from 'vitest';
import type { ViewColumn } from '../model/view';
import { columnHeaders, columnKinds, spacedMoney } from './columnHeaders';
import type { TableInfo } from './loadViews';

const table = (logicalName: string, primaryId: string, labels: Record<string, string>, displayName?: string): TableInfo => ({
  logicalName,
  entitySetName: `${logicalName}s`,
  primaryId,
  labels,
  ...(displayName ? { displayName } : {}),
});
const col = (name: string): ViewColumn => {
  const [alias, attribute] = name.includes('.') ? name.split('.') : [undefined, name];
  return { name, width: 100, attribute: attribute!, isHidden: false, ...(alias ? { alias } : {}) };
};

const tables = {
  account: table('account', 'accountid', { name: 'Account Name', primarycontactid: 'Primary Contact', ownerid: 'Owner' }, 'Account'),
  contact: table('contact', 'contactid', { emailaddress1: 'Email', telephone1: 'Business Phone', parentcustomerid: 'Company Name' }, 'Contact'),
  systemuser: table('systemuser', 'systemuserid', { address1_telephone1: 'Main Phone' }, 'User'),
};

describe('columnHeaders (#104)', () => {
  it('N_to_1_link_header_names_the_lookup', () => {
    const fetch =
      '<fetch><entity name="account"><attribute name="name"/>' +
      '<link-entity name="contact" from="contactid" to="primarycontactid" alias="pc"><attribute name="emailaddress1"/></link-entity></entity></fetch>';
    expect(columnHeaders([col('name'), col('pc.emailaddress1')], fetch, tables)).toEqual({
      name: 'Account Name',
      'pc.emailaddress1': 'Email (Primary Contact)',
    });
  });

  it('other_link_header_names_the_related_table', () => {
    // 1:N: the link joins the contacts whose parentcustomerid is the account.
    const fetch =
      '<fetch><entity name="account"><link-entity name="contact" from="parentcustomerid" to="accountid" alias="c">' +
      '<attribute name="telephone1"/></link-entity></entity></fetch>';
    expect(columnHeaders([col('c.telephone1')], fetch, tables)).toEqual({ 'c.telephone1': 'Business Phone (Contact)' });
  });

  it('two_links_to_the_same_table_get_their_own_lookup', () => {
    const fetch =
      '<fetch><entity name="account">' +
      '<link-entity name="contact" from="contactid" to="primarycontactid" alias="pc"><attribute name="emailaddress1"/></link-entity>' +
      '<link-entity name="systemuser" from="systemuserid" to="ownerid" alias="ow"><attribute name="address1_telephone1"/></link-entity>' +
      '</entity></fetch>';
    expect(columnHeaders([col('pc.emailaddress1'), col('ow.address1_telephone1')], fetch, tables)).toEqual({
      'pc.emailaddress1': 'Email (Primary Contact)',
      'ow.address1_telephone1': 'Main Phone (Owner)',
    });
  });

  it('a nested link names the lookup of its parent link', () => {
    const fetch =
      '<fetch><entity name="account"><link-entity name="contact" from="contactid" to="primarycontactid" alias="pc">' +
      '<link-entity name="account" from="accountid" to="parentcustomerid" alias="pa"><attribute name="name"/></link-entity>' +
      '</link-entity></entity></fetch>';
    expect(columnHeaders([col('pa.name')], fetch, tables)).toEqual({ 'pa.name': 'Account Name (Company Name)' });
  });

  it('missing labels fall back to logical names', () => {
    const fetch = '<fetch><entity name="account"><link-entity name="lead" from="leadid" to="originatingleadid" alias="l"/></entity></fetch>';
    expect(columnHeaders([col('l.subject'), col('xyz')], fetch, tables)).toEqual({
      'l.subject': 'subject (originatingleadid)',
      xyz: 'xyz',
    });
  });
});

describe('columnKinds and spacedMoney', () => {
  it('marks numbers, money and dates of the root and linked tables', () => {
    const fetchXml =
      '<fetch><entity name="xv_project"><attribute name="xv_name"/><attribute name="xv_budget"/><attribute name="xv_start"/><link-entity name="account" from="accountid" to="xv_accountid" alias="a"><attribute name="numberofemployees"/></link-entity></entity></fetch>';
    const tables = {
      xv_project: { logicalName: 'xv_project', entitySetName: 'xv_projects', primaryId: 'xv_projectid', labels: {}, types: { xv_name: 'String', xv_budget: 'Money', xv_start: 'DateTime' } },
      account: { logicalName: 'account', entitySetName: 'accounts', primaryId: 'accountid', labels: {}, types: { numberofemployees: 'Integer' } },
    };
    const col = (name: string, attribute: string, alias?: string) => ({ name, attribute, alias, width: 100, isHidden: false }) as ViewColumn;
    expect(columnKinds([col('xv_name', 'xv_name'), col('xv_budget', 'xv_budget'), col('xv_start', 'xv_start'), col('a.numberofemployees', 'numberofemployees', 'a')], fetchXml, tables)).toEqual({
      xv_budget: 'money',
      xv_start: 'date',
      'a.numberofemployees': 'number',
    });
  });

  it('puts a space between the symbol and the digits', () => {
    expect(spacedMoney('€420,000.00')).toBe('€ 420,000.00');
    expect(spacedMoney('-€5.00')).toBe('-€ 5.00');
    expect(spacedMoney('CHF 5.00')).toBe('CHF 5.00');
    expect(spacedMoney('5.00')).toBe('5.00');
    expect(spacedMoney('')).toBe('');
  });
});
