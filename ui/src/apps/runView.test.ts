import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { CUSTOMERS_FETCH, CUSTOMERS_LAYOUT, QUICK_FIND_FETCH, QUICK_FIND_LAYOUT } from '../test/accountViews';
import { XrmApi } from '../shell/api/client';
import { server } from '../test/server';
import { loadTable, loadViews } from './loadViews';
import { cellText, pageSize, runView } from './runView';

const api = () => new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => {} });
const FV = '@OData.Community.Display.V1.FormattedValue';

interface Seen {
  path: string;
  search: URLSearchParams;
  prefer: string | null;
}

function capture(answer: (seen: Seen) => Response): Seen[] {
  const seen: Seen[] = [];
  server.use(
    http.get('*/api/data/v9.2/*', ({ request }) => {
      const url = new URL(request.url);
      const s = {
        path: decodeURIComponent(url.pathname.replace('/api/data/v9.2/', '')),
        search: url.searchParams,
        prefer: request.headers.get('Prefer'),
      };
      seen.push(s);
      return answer(s);
    }),
  );
  return seen;
}

/** The fetchxmlpagingcookie annotation as the server writes it (inner cookie URL-encoded twice). */
const annotation = (page: number, inner: string) =>
  `<cookie pagenumber="${page}" pagingcookie="${encodeURIComponent(encodeURIComponent(inner))}" istracking="False" />`;

describe('runView', () => {
  it('sends the FetchXML with page and cookie, asks for annotations, and reads cookie, total and morerecords', async () => {
    const inner = '<cookie page="1"><accountid last="{B}" first="{A}" /></cookie>';
    const seen = capture(() =>
      HttpResponse.json({
        value: [{ accountid: 'a1', name: 'Contoso' }],
        '@Microsoft.Dynamics.CRM.totalrecordcount': 213,
        '@Microsoft.Dynamics.CRM.totalrecordcountlimitexceeded': false,
        '@Microsoft.Dynamics.CRM.fetchxmlpagingcookie': annotation(1, inner),
        '@Microsoft.Dynamics.CRM.morerecords': true,
      }),
    );
    const result = await runView(api(), 'accounts', '<fetch count="50"><entity name="account"/></fetch>', 2, 'prev-cookie');
    expect(seen[0].path).toBe('accounts');
    expect(seen[0].prefer).toContain('odata.include-annotations="*"');
    const fetch = new DOMParser().parseFromString(seen[0].search.get('fetchXml')!, 'application/xml').documentElement;
    expect(fetch.getAttribute('page')).toBe('2');
    expect(fetch.getAttribute('count')).toBe('50');
    expect(fetch.getAttribute('paging-cookie')).toBe('prev-cookie');
    expect(result).toEqual({ rows: [{ accountid: 'a1', name: 'Contoso' }], cookie: inner, total: 213, more: true });
  });

  it('reads the total from @odata.count, and a last page has no cookie and more false', async () => {
    capture(() => HttpResponse.json({ value: [], '@odata.count': 7 }));
    const result = await runView(api(), 'accounts', '<fetch><entity name="account"/></fetch>', 1);
    expect(result).toEqual({ rows: [], cookie: undefined, total: 7, more: false });
  });

  it('a total past the limit is the limit and more; a total of -1 is unknown', async () => {
    capture(() =>
      HttpResponse.json({
        value: [],
        '@odata.count': 5000,
        '@Microsoft.Dynamics.CRM.totalrecordcount': 5000,
        '@Microsoft.Dynamics.CRM.totalrecordcountlimitexceeded': true,
      }),
    );
    const capped = await runView(api(), 'accounts', '<fetch><entity name="account"/></fetch>', 1);
    expect([capped.total, capped.totalMore]).toEqual([5000, true]);
    capture(() => HttpResponse.json({ value: [], '@Microsoft.Dynamics.CRM.totalrecordcount': -1 }));
    expect((await runView(api(), 'accounts', '<fetch><entity name="account"/></fetch>', 1)).total).toBeUndefined();
  });
});

describe('cellText', () => {
  it('prefers the formatted value, shows a lookup by its name and a link column by alias.attr', () => {
    const row = {
      name: 'Contoso',
      op365_customertype: 1,
      [`op365_customertype${FV}`]: 'Company',
      _primarycontactid_value: 'c1',
      [`_primarycontactid_value${FV}`]: 'Ada Lovelace',
      'pc.emailaddress1': 'ada@example.com',
      'pc.parentcustomerid': 'p1',
      [`pc.parentcustomerid${FV}`]: 'Fabrikam',
    };
    const col = (name: string) => ({ name, width: 100, attribute: name.split('.').pop()!, alias: name.includes('.') ? name.split('.')[0] : undefined, isHidden: false });
    expect(cellText(row, col('name'))).toBe('Contoso');
    expect(cellText(row, col('op365_customertype'))).toBe('Company');
    expect(cellText(row, col('primarycontactid'))).toBe('Ada Lovelace');
    expect(cellText(row, col('pc.emailaddress1'))).toBe('ada@example.com');
    expect(cellText(row, col('pc.parentcustomerid'))).toBe('Fabrikam');
    // A column the Web API left out (field security masks it): empty.
    expect(cellText(row, col('op365_vatnumber'))).toBe('');
    // A lookup without its formatted value never shows the GUID.
    expect(cellText({ _ownerid_value: 'o1' }, col('ownerid'))).toBe('');
    // Neither does a related table's lookup: by its lookuplogicalname annotation (Dataverse) or its GUID value (xrmverse).
    const guid = '0f8fad5b-d9cb-469f-a165-70867728950e';
    expect(cellText({ 'pc.parentcustomerid': guid }, col('pc.parentcustomerid'))).toBe('');
    expect(cellText({ 'pc.parentcustomerid': 'x1', 'pc.parentcustomerid@Microsoft.Dynamics.CRM.lookuplogicalname': 'account' }, col('pc.parentcustomerid'))).toBe('');
    expect(cellText({ 'pc.parentcustomerid': guid, [`pc.parentcustomerid${FV}`]: 'Fabrikam' }, col('pc.parentcustomerid'))).toBe('Fabrikam');
  });
});

describe('pageSize', () => {
  it.each([
    [25, 25],
    [250, 250],
    [50, 50],
    [10, 50],
    [500, 50],
  ])('paginglimit %d gives %d', (limit, size) => expect(pageSize(limit)).toBe(size));
});

describe('loadViews', () => {
  it('reads public, quick find and personal views, each sorted by name', async () => {
    const seen = capture(({ path }) => {
      if (path === 'savedqueries') {
        return HttpResponse.json({
          value: [
            { savedqueryid: 's2', name: 'Customers with Projects', querytype: 0, isdefault: false, fetchxml: CUSTOMERS_FETCH, layoutxml: CUSTOMERS_LAYOUT },
            { savedqueryid: 'qf', name: 'Quick Find Active Accounts', querytype: 4, isdefault: true, fetchxml: QUICK_FIND_FETCH, layoutxml: QUICK_FIND_LAYOUT },
            { savedqueryid: 's1', name: 'Active Accounts', querytype: 0, isdefault: true, fetchxml: '<fetch><entity name="account"><order attribute="name"/></entity></fetch>', layoutxml: null },
          ],
        });
      }
      return HttpResponse.json({
        value: [{ userqueryid: 'u1', name: 'My accounts', querytype: 0, fetchxml: '<fetch><entity name="account"/></fetch>', layoutxml: '<grid><row><cell name="name" width="200"/></row></grid>' }],
      });
    });
    const views = await loadViews(api(), 'account');
    expect(views.system.map((v) => v.id)).toEqual(['s1', 's2']);
    expect(views.system[0]).toMatchObject({ entity: 'account', isDefault: true, columns: [], order: [{ attribute: 'name', descending: false }] });
    expect(views.system[1].columns.map((c) => [c.name, c.width])).toContainEqual(['op365_vatnumber', 130]);
    expect(views.quickFind?.id).toBe('qf');
    expect(views.personal).toMatchObject([{ id: 'u1', name: 'My accounts', queryType: 0, isDefault: false }]);
    const saved = seen.find((s) => s.path === 'savedqueries')!;
    expect(saved.search.get('$filter')).toBe("returnedtypecode eq 'account' and statecode eq 0 and (querytype eq 0 or querytype eq 4 or querytype eq 2)");
    const user = seen.find((s) => s.path === 'userqueries')!;
    expect(user.search.get('$filter')).toBe("returnedtypecode eq 'account' and statecode eq 0 and querytype eq 0");
  });

  it('a user who may not read personal views gets none', async () => {
    capture(({ path }) =>
      path === 'userqueries'
        ? HttpResponse.json({ error: { code: '0x80040220', message: 'no prvReadUserQuery' } }, { status: 403 })
        : HttpResponse.json({ value: [] }),
    );
    expect(await loadViews(api(), 'account')).toEqual({ system: [], personal: [], quickFind: undefined, associated: undefined });
  });
});

describe('loadTable', () => {
  it('reads the entity set, primary key and the labels in the user language, else 1033, else the user label', async () => {
    const seen = capture(({ path }) =>
      path === "EntityDefinitions(LogicalName='account')"
        ? HttpResponse.json({ LogicalName: 'account', SchemaName: 'Account', EntitySetName: 'accounts', PrimaryIdAttribute: 'accountid' })
        : HttpResponse.json({
            value: [
              { LogicalName: 'name', DisplayName: { LocalizedLabels: [{ Label: 'Account Name', LanguageCode: 1033 }, { Label: 'Nome account', LanguageCode: 1040 }] } },
              { LogicalName: 'op365_vatnumber', DisplayName: { LocalizedLabels: [{ Label: 'VAT Number', LanguageCode: 1033 }] } },
              { LogicalName: 'x', DisplayName: { LocalizedLabels: [], UserLocalizedLabel: { Label: 'Ex', LanguageCode: 1031 } } },
              { LogicalName: 'y', DisplayName: { LocalizedLabels: [] } },
            ],
          }),
    );
    const table = await loadTable(api(), 'account', 1040);
    expect(table).toEqual({
      logicalName: 'account',
      entitySetName: 'accounts',
      primaryId: 'accountid',
      schemaName: 'Account',
      labels: { name: 'Nome account', op365_vatnumber: 'VAT Number', x: 'Ex' },
      types: {},
    });
    expect(seen.map((s) => s.path)).toContain("EntityDefinitions(LogicalName='account')/Attributes");
  });
});
