import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { XrmApi } from '../../shell/api/client';
import { auditDetails, changesOf, listAudits, recordHistory, setColumnAuditing, setOrgAuditing, setTableAuditing } from './auditApi';

const api = () => new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => {} });
const FV = 'OData.Community.Display.V1.FormattedValue';

interface Seen {
  method: string;
  path: string;
  search: URLSearchParams;
  prefer: string | null;
  body: unknown;
}

function capture(answer: (seen: Seen) => Response): Seen[] {
  const seen: Seen[] = [];
  server.use(
    http.all('*/api/data/v9.2/*', async ({ request }) => {
      const url = new URL(request.url);
      const text = await request.text();
      const s: Seen = {
        method: request.method,
        path: decodeURIComponent(url.pathname.replace('/api/data/v9.2/', '')),
        search: url.searchParams,
        prefer: request.headers.get('Prefer'),
        body: text ? JSON.parse(text) : undefined,
      };
      seen.push(s);
      return answer(s);
    }),
  );
  return seen;
}

describe('auditApi', () => {
  it('listAudits asks newest first with formatted values and the filters', async () => {
    const seen = capture(() => HttpResponse.json({ value: [{ auditid: 'a1', createdon: '2026-11-09T10:00:00Z', objecttypecode: 'account', [`action@${FV}`]: 'Update', [`operation@${FV}`]: 'Update', [`_userid_value@${FV}`]: 'Ada', _userid_value: 'u1', _objectid_value: 'r1' }] }));
    const page = await listAudits(api(), { table: "o'x", operation: 2, userId: '11111111-1111-1111-1111-111111111111' });
    expect(seen[0].path).toBe('audits');
    expect(seen[0].search.get('$orderby')).toBe('createdon desc,auditid asc');
    expect(seen[0].search.get('$filter')).toBe("objecttypecode eq 'o''x' and operation eq 2 and _userid_value eq 11111111-1111-1111-1111-111111111111");
    expect(seen[0].prefer).toContain(`odata.include-annotations="${FV}"`);
    expect(page.rows).toEqual([{ auditid: 'a1', createdon: '2026-11-09T10:00:00Z', table: 'account', objectId: 'r1', action: 'Update', operation: 'Update', user: 'Ada' }]);
    expect(page.nextLink).toBeUndefined();
  });

  it('listAudits follows the next link it is given', async () => {
    const seen = capture(() => HttpResponse.json({ value: [] }));
    await listAudits(api(), { page: `${location.origin}/api/data/v9.2/audits?$skiptoken=2` });
    expect(seen[0].search.get('$skiptoken')).toBe('2');
  });

  it('auditDetails reads the bound function', async () => {
    const seen = capture(() => HttpResponse.json({ AuditDetail: { '@odata.type': '#Microsoft.Dynamics.CRM.AttributeAuditDetail' } }));
    const detail = await auditDetails(api(), 'a1');
    expect(seen[0].path).toBe('audits(a1)/Microsoft.Dynamics.CRM.RetrieveAuditDetails');
    expect(seen[0].prefer).toContain(FV);
    expect(detail['@odata.type']).toContain('AttributeAuditDetail');
  });

  it('recordHistory pages the audits of the record and reads the details of each row', async () => {
    const seen = capture((s) => {
      if (s.path.includes('RetrieveAuditDetails')) {
        const id = s.path.slice('audits('.length, s.path.indexOf(')'));
        return HttpResponse.json({ AuditDetail: { '@odata.type': '#Microsoft.Dynamics.CRM.AttributeAuditDetail', OldValue: { name: `old-${id}` }, NewValue: { name: `new-${id}` } } });
      }
      return HttpResponse.json({
        value: [
          { auditid: 'a2', createdon: '2026-11-09T10:00:00Z', [`_userid_value@${FV}`]: 'Ada', [`action@${FV}`]: 'Update' },
          { auditid: 'a1', createdon: '2026-11-09T10:00:00Z', [`_userid_value@${FV}`]: 'Bob', [`action@${FV}`]: 'Create' },
        ],
        '@odata.nextLink': `${location.origin}/api/data/v9.2/audits?$skiptoken=2`,
      });
    });
    const history = await recordHistory(api(), 'r1');
    const list = seen.find((s) => s.path === 'audits')!;
    expect(list.search.get('$filter')).toBe('_objectid_value eq r1');
    expect(list.search.get('$orderby')).toBe('createdon desc,auditid desc');
    expect(list.prefer).toContain('odata.maxpagesize=20');
    expect(seen.some((s) => s.path.startsWith('RetrieveRecordChangeHistory'))).toBe(false);
    expect(history.nextLink).toContain('$skiptoken=2');
    // Each entry carries the detail of its own audit row, whatever order the responses arrived in.
    expect(history.entries.map((e) => [e.auditid, e.user, e.action])).toEqual([['a2', 'Ada', 'Update'], ['a1', 'Bob', 'Create']]);
    expect(history.entries.map((e) => changesOf(e.detail))).toEqual([
      [{ column: 'name', oldValue: 'old-a2', newValue: 'new-a2' }],
      [{ column: 'name', oldValue: 'old-a1', newValue: 'new-a1' }],
    ]);
  });

  it('recordHistory follows the next link it is given', async () => {
    const seen = capture(() => HttpResponse.json({ value: [] }));
    await recordHistory(api(), 'r1', `${location.origin}/api/data/v9.2/audits?$skiptoken=2`);
    expect(seen[0].search.get('$skiptoken')).toBe('2');
  });

  it('recordHistory surfaces errors', async () => {
    capture(() => HttpResponse.json({ error: { code: '0x1', message: 'boom' } }, { status: 500 }));
    await expect(recordHistory(api(), 'r1')).rejects.toMatchObject({ status: 500 });
  });

  it('changesOf reads formatted values, lookups, relationships, shares and role privileges', () => {
    expect(
      changesOf({
        '@odata.type': '#Microsoft.Dynamics.CRM.AttributeAuditDetail',
        OldValue: { _owner_value: 'u1', [`_owner_value@${FV}`]: 'Ada', count: 1 },
        NewValue: { _owner_value: 'u2', [`_owner_value@${FV}`]: 'Bob', count: 2 },
      }),
    ).toEqual([
      { column: 'owner', oldValue: 'Ada', newValue: 'Bob' },
      { column: 'count', oldValue: '1', newValue: '2' },
    ]);
    expect(changesOf({ '@odata.type': '#Microsoft.Dynamics.CRM.RelationshipAuditDetail', RelationshipName: 'r', TargetRecords: [{ '@odata.type': '#Microsoft.Dynamics.CRM.contact', contactid: 'c1' }] })).toEqual([
      { column: 'r', oldValue: '', newValue: 'contact:c1' },
    ]);
    expect(changesOf({ '@odata.type': '#Microsoft.Dynamics.CRM.ShareAuditDetail', OldPrivileges: 'None', NewPrivileges: 'ReadAccess', Principal: { '@odata.type': '#Microsoft.Dynamics.CRM.team', teamid: 't1' } })).toEqual([
      { column: 'share team:t1', oldValue: 'None', newValue: 'ReadAccess' },
    ]);
    expect(changesOf({ '@odata.type': '#Microsoft.Dynamics.CRM.RolePrivilegeAuditDetail', OldRolePrivileges: [], NewRolePrivileges: [{}, {}] })).toEqual([
      { column: 'privileges', oldValue: '0 privileges', newValue: '2 privileges' },
    ]);
  });

  it('switches auditing through the organization row and a metadata PUT of the whole definition', async () => {
    const seen = capture((s) => {
      if (s.path === 'organizations') return HttpResponse.json({ value: [{ organizationid: 'o1', isauditenabled: false, auditretentionperiodv2: 30 }] });
      if (s.method === 'GET' && s.path.includes('Attributes(')) return HttpResponse.json({ LogicalName: 'name', IsAuditEnabled: { Value: false, CanBeChanged: true } });
      if (s.method === 'GET') return HttpResponse.json({ LogicalName: 'account', IsAuditEnabled: { Value: false, CanBeChanged: true } });
      return new HttpResponse(null, { status: 204 });
    });
    await setOrgAuditing(api(), true);
    await setTableAuditing(api(), 'account', true);
    await setColumnAuditing(api(), 'account', 'name', true);
    const writes = seen.filter((s) => s.method !== 'GET');
    expect(writes.map((w) => [w.method, w.path])).toEqual([
      ['PATCH', 'organizations(o1)'],
      ['PUT', "EntityDefinitions(LogicalName='account')"],
      ['PUT', "EntityDefinitions(LogicalName='account')/Attributes(LogicalName='name')"],
    ]);
    expect(writes[0].body).toEqual({ isauditenabled: true });
    expect(writes[1].body).toEqual({ LogicalName: 'account', IsAuditEnabled: { Value: true, CanBeChanged: true } });
    expect(writes[2].body).toEqual({ LogicalName: 'name', IsAuditEnabled: { Value: true, CanBeChanged: true } });
  });
});
