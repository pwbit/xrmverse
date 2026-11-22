import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { XrmApi } from '../../shell/api/client';
import { XrmError } from '../../shell/api/errors';
import { server } from '../../test/server';
import { applyWorkflow, deleteWorkflow, exportWorkflow, listWorkflows, managedIds, setWorkflowState, type ApplyResult } from './studioApi';

const api = () => new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => {} });
const ID = '3e1f0307-d8de-f111-84e8-002248872b66';
const RESULT: ApplyResult = { id: ID, name: 'OnCreate', action: 'unchanged', state: 'draft', stateChanged: false, assumedShapes: [], errors: [] };

describe('studioApi', () => {
  it('apply posts the YAML as text/yaml to /authoring/workflows/apply with dryRun', async () => {
    const seen: { url: string; type: string | null; auth: string | null; body: string }[] = [];
    server.use(
      http.post('*/authoring/workflows/apply', async ({ request }) => {
        seen.push({ url: request.url, type: request.headers.get('Content-Type'), auth: request.headers.get('Authorization'), body: await request.text() });
        return HttpResponse.json(RESULT);
      }),
    );
    const result = await applyWorkflow(api(), 'kind: workflow\n', { dryRun: true });
    expect(result).toEqual(RESULT);
    expect(seen).toHaveLength(1);
    const url = new URL(seen[0]!.url);
    expect(url.pathname).toBe('/authoring/workflows/apply');
    expect(url.search).toBe('?dryRun=true');
    expect(seen[0]!.type).toMatch(/^text\/yaml/);
    expect(seen[0]!.auth).toBe('Bearer tok');
    expect(seen[0]!.body).toBe('kind: workflow\n');
  });

  it('apply passes dryRun=false, the solution and activate', async () => {
    let search = '';
    server.use(
      http.post('*/authoring/workflows/apply', ({ request }) => {
        search = new URL(request.url).search;
        return HttpResponse.json(RESULT);
      }),
    );
    await applyWorkflow(api(), 'x', { dryRun: false, solution: 'my sol', activate: true });
    expect(search).toBe('?dryRun=false&solution=my+sol&activate=true');
  });

  it('a 400 apply with an ApplyResult body resolves with its errors', async () => {
    const failed = { ...RESULT, errors: [{ message: "Unknown key 'foo'.", at: { line: 3, column: 1 } }] };
    server.use(http.post('*/authoring/workflows/apply', () => HttpResponse.json(failed, { status: 400 })));
    const result = await applyWorkflow(api(), 'foo: 1', { dryRun: true });
    expect(result.errors).toEqual(failed.errors);
  });

  it('a 400 apply with the error envelope throws the server message', async () => {
    server.use(
      http.post('*/authoring/workflows/apply', () =>
        HttpResponse.json({ error: { code: '0x80040203', message: 'The request body must be the workflow YAML.' } }, { status: 400 }),
      ),
    );
    const error = await applyWorkflow(api(), ' ', { dryRun: true }).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(XrmError);
    expect((error as XrmError).serverMessage).toBe('The request body must be the workflow YAML.');
  });

  it('list passes the filter and returns the summaries', async () => {
    let search = '';
    const rows = [{ id: ID, name: 'OnCreate', kind: 'workflow', entity: 'xrmv_order', mode: 'realtime', state: 'draft', unsupportedSteps: 0 }];
    server.use(
      http.get('*/authoring/workflows', ({ request }) => {
        search = new URL(request.url).search;
        return HttpResponse.json(rows);
      }),
    );
    expect(await listWorkflows(api(), { kind: 'workflow', entity: 'xrmv_order' })).toEqual(rows);
    expect(search).toBe('?kind=workflow&entity=xrmv_order');
    await listWorkflows(api());
    expect(search).toBe('');
  });

  it('export reads the text of the process by its encoded id', async () => {
    let path = '';
    let format: string | null = null;
    server.use(
      http.get('*/authoring/workflows/:id', ({ request }) => {
        const url = new URL(request.url);
        path = url.pathname;
        format = url.searchParams.get('format');
        return new HttpResponse('kind: workflow\nname: OnCreate\n', { headers: { 'Content-Type': 'text/yaml' } });
      }),
    );
    expect(await exportWorkflow(api(), 'a b/c', 'yaml')).toBe('kind: workflow\nname: OnCreate\n');
    expect(path).toBe('/authoring/workflows/a%20b%2Fc');
    expect(format).toBe('yaml');
    await exportWorkflow(api(), ID, 'xaml');
    expect(format).toBe('xaml');
  });

  it('export throws an XrmError with the server message', async () => {
    server.use(
      http.get('*/authoring/workflows/:id', () =>
        HttpResponse.json({ error: { code: '0x80040217', message: 'No process has the id.' } }, { status: 404 }),
      ),
    );
    const error = await exportWorkflow(api(), ID, 'yaml').catch((e: unknown) => e);
    expect(error).toBeInstanceOf(XrmError);
    expect((error as XrmError).status).toBe(404);
    expect((error as XrmError).serverMessage).toBe('No process has the id.');
  });

  it('setWorkflowState posts activate or deactivate by id', async () => {
    const paths: string[] = [];
    server.use(
      http.post('*/authoring/workflows/:id/:verb', ({ request }) => {
        paths.push(new URL(request.url).pathname);
        return HttpResponse.json({ id: ID, name: 'OnCreate', kind: 'workflow', entity: 'xrmv_order', mode: 'realtime', state: 'activated', unsupportedSteps: 0 });
      }),
    );
    expect((await setWorkflowState(api(), ID, true)).state).toBe('activated');
    await setWorkflowState(api(), ID, false);
    expect(paths).toEqual([`/authoring/workflows/${ID}/activate`, `/authoring/workflows/${ID}/deactivate`]);
  });

  it('delete hits the Web API workflows(<id>)', async () => {
    const deleted: string[] = [];
    server.use(
      http.delete('*/api/data/v9.2/*', ({ request }) => {
        deleted.push(decodeURIComponent(new URL(request.url).pathname));
        return new HttpResponse(null, { status: 204 });
      }),
    );
    await deleteWorkflow(api(), ID);
    expect(deleted).toEqual([`/api/data/v9.2/workflows(${ID})`]);
  });

  it('managedIds reads ismanaged of the ids and returns the managed ones', async () => {
    const OTHER = '11111111-0000-0000-0000-000000000001';
    let query = '';
    server.use(
      http.get('*/api/data/v9.2/workflows', ({ request }) => {
        query = decodeURIComponent(new URL(request.url).search);
        return HttpResponse.json({ value: [{ workflowid: ID, ismanaged: true }, { workflowid: OTHER, ismanaged: false }] });
      }),
    );
    expect(await managedIds(api(), [ID, OTHER])).toEqual(new Set([ID]));
    expect(query).toContain('$select=workflowid,ismanaged');
    expect(query).toContain(`workflowid eq ${ID}`);
    expect(await managedIds(api(), [])).toEqual(new Set());
  });
});
