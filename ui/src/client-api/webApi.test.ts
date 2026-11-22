import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { XrmApi } from '../shell/api/client';
import { server } from '../test/server';
import { createWebApi, type WebApi } from './webApi';

const ID = '11111111-2222-3333-4444-555555555555';

interface Seen {
  method: string;
  path: string;
  search: string;
  headers: Headers;
  body?: unknown;
}

/** A Web API over MSW: `EntityDefinitions` answers the set names below and counts its reads; every other request is recorded. */
function setup(answer: (s: Seen) => Response | undefined = () => undefined) {
  const seen: Seen[] = [];
  let metadataReads = 0;
  const sets: Record<string, [string, string]> = { account: ['accounts', 'accountid'], op365_project: ['op365_projects', 'op365_projectid'] };
  server.use(
    http.all('*/api/data/v9.2/*', async ({ request }) => {
      const url = new URL(request.url);
      const path = decodeURIComponent(url.pathname.replace(/^.*\/api\/data\/v9\.2\//, ''));
      const table = /^EntityDefinitions\(LogicalName='(\w+)'\)$/.exec(path);
      if (table) {
        metadataReads++;
        const found = sets[table[1]!];
        if (!found) return HttpResponse.json({ error: { code: '0x80040217', message: 'no table' } }, { status: 404 });
        return HttpResponse.json({ LogicalName: table[1], EntitySetName: found[0], PrimaryIdAttribute: found[1] });
      }
      const text = request.method === 'GET' || request.method === 'DELETE' ? '' : await request.text();
      const s: Seen = { method: request.method, path, search: url.search, headers: request.headers, body: text ? JSON.parse(text) : undefined };
      seen.push(s);
      return answer(s) ?? HttpResponse.json({ value: [] });
    }),
  );
  const api = new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => undefined });
  return { webApi: createWebApi(api), seen, metadataReads: () => metadataReads };
}

type Fn = (...a: unknown[]) => Promise<Record<string, unknown>>;
const fn = (w: WebApi, name: string) => (w as unknown as Record<string, Fn>)[name]!;
const online = (w: WebApi, name: string) => (w.online as unknown as Record<string, Fn>)[name]!;

describe('Xrm.WebApi', () => {
  it('retrieve_record_with_select', async () => {
    const { webApi, seen } = setup((s) => HttpResponse.json({ accountid: ID, name: 'Contoso' }, { status: s.method === 'GET' ? 200 : 500 }));
    const record = await fn(webApi, 'retrieveRecord')('account', `{${ID.toUpperCase()}}`, '?$select=name');
    expect(record).toEqual({ accountid: ID, name: 'Contoso' });
    expect(seen[0]!.path).toBe(`accounts(${ID})`);
    expect(new URLSearchParams(seen[0]!.search).get('$select')).toBe('name');
    // The bearer header of the shell's client: the server enforces security.
    expect(seen[0]!.headers.get('Authorization')).toBe('Bearer tok');
    expect(seen[0]!.headers.get('Prefer')).toContain('odata.include-annotations="*"');
  });

  it('retrieve_multiple_odata_and_fetchxml_encoded_or_not', async () => {
    const { webApi, seen } = setup(() => HttpResponse.json({ value: [{ name: 'A' }] }));
    const odata = await fn(webApi, 'retrieveMultipleRecords')('account', '?$select=name&$filter=name eq \'A%\'');
    expect(odata).toEqual({ entities: [{ name: 'A' }] });
    expect(seen[0]!.path).toBe('accounts');
    expect(new URLSearchParams(seen[0]!.search).get('$filter')).toBe("name eq 'A%'");

    const xml = "<fetch><entity name='account'><attribute name='name'/><filter><condition attribute='name' operator='like' value='A%'/></filter></entity></fetch>";
    await fn(webApi, 'retrieveMultipleRecords')('account', `?fetchXml=${xml}`);
    await fn(webApi, 'retrieveMultipleRecords')('account', `?fetchXml=${encodeURIComponent(xml)}`);
    expect(new URLSearchParams(seen[1]!.search).get('fetchXml')).toBe(xml);
    expect(new URLSearchParams(seen[2]!.search).get('fetchXml')).toBe(xml);
  });

  it('retrieve_multiple_next_link_and_max_page_size', async () => {
    const next = `http://localhost:3000/api/data/v9.2/accounts?$select=name&$skiptoken=abc`;
    const { webApi, seen } = setup(() => HttpResponse.json({ value: [{ name: 'A' }], '@odata.nextLink': next }));
    const page = await fn(webApi, 'retrieveMultipleRecords')('account', '?$select=name', 3);
    expect(page).toEqual({ entities: [{ name: 'A' }], nextLink: next });
    expect(seen[0]!.headers.get('Prefer')).toContain('odata.maxpagesize=3');
    // The query part of nextLink is the options of the next call.
    await fn(webApi, 'retrieveMultipleRecords')('account', new URL(next).search, 3);
    expect(new URLSearchParams(seen[1]!.search).get('$skiptoken')).toBe('abc');
    // Without maxPageSize, Learn's default of 5,000 rows a page.
    await fn(webApi, 'retrieveMultipleRecords')('account', '?$select=name');
    expect(seen[2]!.headers.get('Prefer')).toContain('odata.maxpagesize=5000');
    await expect(fn(webApi, 'retrieveMultipleRecords')('account', '?$select=name', 0)).rejects.toMatchObject({ message: expect.stringContaining('maxPageSize') });
  });

  it('create_update_delete_resolve_entity_type_and_id', async () => {
    const { webApi, seen } = setup((s) =>
      s.method === 'POST'
        ? new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://localhost/api/data/v9.2/accounts(${ID})` } })
        : new HttpResponse(null, { status: 204 }),
    );
    expect(await fn(webApi, 'createRecord')('account', { name: 'New' })).toEqual({ entityType: 'account', id: ID });
    expect(await fn(webApi, 'updateRecord')('account', `{${ID.toUpperCase()}}`, { name: 'Changed' })).toEqual({ entityType: 'account', id: ID });
    expect(await fn(webApi, 'deleteRecord')('account', ID)).toEqual({ entityType: 'account', id: ID });
    expect(seen.map((s) => `${s.method} ${s.path}`)).toEqual(['POST accounts', `PATCH accounts(${ID})`, `DELETE accounts(${ID})`]);
    expect(seen[0]!.body).toEqual({ name: 'New' });
    expect(seen[1]!.body).toEqual({ name: 'Changed' });
    // The same methods under online.
    expect(await online(webApi, 'deleteRecord')('account', ID)).toEqual({ entityType: 'account', id: ID });
    expect((webApi.isAvailableOffline as (e: string) => boolean)('account')).toBe(false);
  });

  it('entity_set_from_metadata_cached', async () => {
    const { webApi, metadataReads, seen } = setup(() => HttpResponse.json({}));
    await fn(webApi, 'retrieveRecord')('op365_project', ID);
    await fn(webApi, 'retrieveRecord')('op365_project', ID);
    await Promise.all([fn(webApi, 'retrieveMultipleRecords')('account'), fn(webApi, 'retrieveMultipleRecords')('account')]);
    expect(seen.map((s) => s.path)).toEqual([`op365_projects(${ID})`, `op365_projects(${ID})`, 'accounts', 'accounts']);
    expect(metadataReads()).toBe(2);
  });

  it('error_rejects_with_error_code_message', async () => {
    const { webApi } = setup(() => HttpResponse.json({ error: { code: '0x80040217', message: 'account With Id = x Does Not Exist' } }, { status: 404 }));
    const error = await fn(webApi, 'retrieveRecord')('account', ID).catch((e: unknown) => e);
    expect(error).toEqual({ errorCode: 0x80040217, message: 'account With Id = x Does Not Exist', code: '0x80040217' });
    expect((error as { errorCode: number }).errorCode).toBe(2147746327);
    // A table the server does not know fails the same way, from the metadata read.
    await expect(fn(webApi, 'createRecord')('nope', {})).rejects.toMatchObject({ errorCode: 0x80040217, message: 'no table' });
  });

  it('execute_bound_action_from_get_metadata', async () => {
    const { webApi, seen } = setup(() => new HttpResponse(null, { status: 204 }));
    // The OpenProject365 project.form.js request, as the script builds it.
    const request = {
      entity: { entityType: 'op365_project', id: ID },
      getMetadata: () => ({
        boundParameter: 'entity',
        parameterTypes: { entity: { typeName: 'mscrm.op365_project', structuralProperty: 5 } },
        operationType: 0,
        operationName: 'op365_SaveBaseline',
      }),
    };
    const response = await online(webApi, 'execute')(request);
    expect(seen[0]!.method).toBe('POST');
    expect(seen[0]!.path).toBe(`op365_projects(${ID})/Microsoft.Dynamics.CRM.op365_SaveBaseline`);
    expect(seen[0]!.body).toEqual({});
    expect(response).toMatchObject({ ok: true, status: 204 });
  });

  it('execute_unbound_action_body_and_json_response', async () => {
    const { webApi, seen } = setup(() => HttpResponse.json({ Result: 'done' }));
    function Request(this: Record<string, unknown>) {
      this.Status = 'Cancelled';
      this.Count = 3;
    }
    // Learn: getMetadata on the prototype.
    Request.prototype.getMetadata = () => ({
      boundParameter: null,
      parameterTypes: { Status: { typeName: 'Edm.String', structuralProperty: 1 }, Count: { typeName: 'Edm.Int32', structuralProperty: 1 } },
      operationType: 0,
      operationName: 'op365_Do',
    });
    const response = (await online(webApi, 'execute')(new (Request as unknown as new () => object)())) as unknown as { ok: boolean; json(): Promise<unknown> };
    expect(seen[0]!.path).toBe('op365_Do');
    expect(seen[0]!.body).toEqual({ Status: 'Cancelled', Count: 3 });
    expect(response.ok).toBe(true);
    expect(await response.json()).toEqual({ Result: 'done' });
  });

  it('execute_unbound_function_parameters_in_url', async () => {
    const { webApi, seen } = setup(() => HttpResponse.json({ UserId: ID }));
    const whoAmI = { getMetadata: () => ({ boundParameter: null, parameterTypes: {}, operationType: 1, operationName: 'WhoAmI' }) };
    const response = (await fn(webApi, 'execute')(whoAmI)) as unknown as { json(): Promise<unknown> };
    expect(seen[0]!.method).toBe('GET');
    expect(seen[0]!.path).toBe('WhoAmI()');
    expect(await response.json()).toEqual({ UserId: ID });

    const fnRequest = {
      FieldName: "it's",
      Top: 5,
      Target: { '@odata.type': 'Microsoft.Dynamics.CRM.account', accountid: ID },
      getMetadata: () => ({
        boundParameter: null,
        parameterTypes: {
          FieldName: { typeName: 'Edm.String', structuralProperty: 1 },
          Top: { typeName: 'Edm.Int32', structuralProperty: 1 },
          Target: { typeName: 'mscrm.crmbaseentity', structuralProperty: 5 },
        },
        operationType: 1,
        operationName: 'op365_Calc',
      }),
    };
    await online(webApi, 'execute')(fnRequest);
    expect(seen[1]!.path).toBe('op365_Calc(FieldName=@p1,Top=@p2,Target=@p3)');
    const params = new URLSearchParams(seen[1]!.search);
    expect(params.get('@p1')).toBe("'it''s'");
    expect(params.get('@p2')).toBe('5');
    expect(JSON.parse(params.get('@p3')!)).toEqual({ '@odata.type': 'Microsoft.Dynamics.CRM.account', accountid: ID });

    // Edm.Guid is written bare; an enumeration by the member name Learn's enumProperties give.
    const typed = {
      Id: `{${ID.toUpperCase()}}`,
      Field: 1,
      getMetadata: () => ({
        boundParameter: null,
        parameterTypes: {
          Id: { typeName: 'Edm.Guid', structuralProperty: 1 },
          Field: {
            typeName: 'Microsoft.Dynamics.CRM.TargetFieldType',
            structuralProperty: 3,
            enumProperties: [
              { name: 'All', value: 0 },
              { name: 'ValidForCreate', value: 1 },
            ],
          },
        },
        operationType: 1,
        operationName: 'op365_Typed',
      }),
    };
    await online(webApi, 'execute')(typed);
    const typedParams = new URLSearchParams(seen[2]!.search);
    expect(typedParams.get('@p1')).toBe(ID);
    expect(typedParams.get('@p2')).toBe("Microsoft.Dynamics.CRM.TargetFieldType'ValidForCreate'");
    await expect(online(webApi, 'execute')({ ...typed, Field: 9 })).rejects.toMatchObject({ message: expect.stringContaining('not a member') });
  });

  it('create_without_entity_id_rejects', async () => {
    const { webApi } = setup(() => new HttpResponse(null, { status: 204 }));
    await expect(fn(webApi, 'createRecord')('account', { name: 'New' })).rejects.toMatchObject({ errorCode: 0, message: expect.stringContaining('did not return the id') });
  });

  it('execute_multiple_sequential', async () => {
    const order: string[] = [];
    const { webApi } = setup((s) => {
      order.push(s.path);
      return s.path === 'Fail' ? HttpResponse.json({ error: { code: '0x80040265', message: 'stop' } }, { status: 400 }) : HttpResponse.json({});
    });
    const action = (name: string) => ({ getMetadata: () => ({ boundParameter: null, parameterTypes: {}, operationType: 0, operationName: name }) });
    const responses = (await online(webApi, 'executeMultiple')([action('One'), action('Two')])) as unknown as { ok: boolean }[];
    expect(responses.map((r) => r.ok)).toEqual([true, true]);
    expect(order).toEqual(['One', 'Two']);
    // Ruling C5: one by one; a failure stops the rest and rejects.
    order.length = 0;
    await expect(online(webApi, 'executeMultiple')([action('One'), action('Fail'), action('Two')])).rejects.toMatchObject({ message: 'stop' });
    expect(order).toEqual(['One', 'Fail']);
  });

  it('execute_multiple_with_a_change_set_sends_one_batch', async () => {
    const { webApi } = setup();
    let sent = '';
    let contentType = '';
    const reply = (status: string, headers: string, body = '') =>
      `Content-Type: application/http\r\nContent-Transfer-Encoding: binary\r\n\r\nHTTP/1.1 ${status}\r\n${headers}\r\n${body}\r\n`;
    const answer = (failed: boolean) =>
      failed
        ? `--batchresponse_1\r\n${reply('400 Bad Request', 'Content-Type: application/json\r\n', '{"error":{"code":"0x80040203","message":"bad column"}}')}--batchresponse_1--\r\n`
        : `--batchresponse_1\r\nContent-Type: multipart/mixed; boundary=changesetresponse_1\r\n\r\n` +
          `--changesetresponse_1\r\n${reply('204 No Content', `OData-EntityId: http://x/api/data/v9.2/accounts(${ID})\r\n`)}` +
          `--changesetresponse_1\r\n${reply('204 No Content', '')}--changesetresponse_1--\r\n` +
          `--batchresponse_1\r\n${reply('200 OK', 'Content-Type: application/json\r\n', '{"value":[]}')}--batchresponse_1--\r\n`;
    let fail = false;
    server.use(
      http.post('*/api/data/v9.2/$batch', async ({ request }) => {
        sent = await request.text();
        contentType = request.headers.get('Content-Type') ?? '';
        return new HttpResponse(answer(fail), { status: fail ? 400 : 200, headers: { 'Content-Type': 'multipart/mixed; boundary=batchresponse_1' } });
      }),
    );
    const create = { etn: 'account', payload: { name: 'A' }, getMetadata: () => ({ operationType: 2, operationName: 'Create' }) };
    const update = { etn: 'account', id: ID, payload: { name: 'B' }, getMetadata: () => ({ operationType: 2, operationName: 'Update' }) };
    const fetchAll = { getMetadata: () => ({ boundParameter: null, parameterTypes: {}, operationType: 1, operationName: 'WhoAmI' }) };

    const responses = (await online(webApi, 'executeMultiple')([[create, update], fetchAll])) as unknown as { status: number; headers: Record<string, string> }[];
    expect(responses.map((r) => r.status)).toEqual([204, 204, 200]);
    expect(responses[0]!.headers['odata-entityid']).toContain(ID);
    const boundary = /boundary=(\S+)/.exec(contentType)![1]!;
    expect(sent.startsWith(`--${boundary}\r\nContent-Type: multipart/mixed; boundary=changeset_`)).toBe(true);
    expect(sent).toContain('Content-ID: 1\r\n\r\nPOST accounts HTTP/1.1\r\nContent-Type: application/json; type=entry\r\n\r\n{"name":"A"}\r\n');
    expect(sent).toContain(`Content-ID: 2\r\n\r\nPATCH accounts(${ID}) HTTP/1.1`);
    expect(sent).toContain('GET WhoAmI() HTTP/1.1');
    expect(sent.endsWith(`--${boundary}--\r\n`)).toBe(true);

    fail = true;
    await expect(online(webApi, 'executeMultiple')([[create]])).rejects.toMatchObject({ errorCode: 0x80040203, message: 'bad column' });
  });
});
