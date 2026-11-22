import { http, HttpResponse } from 'msw';
import { mockApps } from '../apps/testSupport';
import { server } from '../test/server';
import type { WorkflowSummary } from './api/studioApi';

export interface StudioMock {
  /** The rows of `GET /authoring/workflows`; a function is read on every request (so a test can change them). */
  workflows?: WorkflowSummary[] | (() => WorkflowSummary[]);
  /** Answers the list with this status and the Web API error envelope instead. */
  listStatus?: number;
  listMessage?: string;
  /** The ids `workflows?$select=workflowid,ismanaged` reports as managed. */
  managed?: string[];
  /** The status of activate, deactivate and delete (204/200 by default). */
  writeStatus?: number;
  writeMessage?: string;
  /** The YAML `GET /authoring/workflows/{id}?format=yaml` answers, by id (a 404 for any other id). */
  exports?: Record<string, string>;
  /**
   * The properties panel's table metadata: the raw `EntityDefinitions` entity by logical name (a table not listed answers
   * 404, and the panel falls back to typing logical names); the table list answers every entity given.
   */
  entities?: Record<string, Record<string, unknown>>;
}

export interface StudioCalls {
  lists: string[];
  states: string[];
  deletes: string[];
}

export const errorBody = (message: string) => ({ error: { code: '0x80040220', message } });

/** Signs in a user (as {@link mockApps}) and answers the studio's requests; returns what was called. */
export function mockStudio(opts: StudioMock = {}): StudioCalls {
  mockApps();
  const calls: StudioCalls = { lists: [], states: [], deletes: [] };
  const rows = () => (typeof opts.workflows === 'function' ? opts.workflows() : (opts.workflows ?? []));
  server.use(
    http.get('*/authoring/workflows', ({ request }) => {
      calls.lists.push(new URL(request.url).search);
      if (opts.listStatus) {
        return HttpResponse.json(errorBody(opts.listMessage ?? 'refused'), { status: opts.listStatus });
      }
      return HttpResponse.json(rows());
    }),
    http.post('*/authoring/workflows/:id/:verb', ({ params }) => {
      calls.states.push(`${String(params.id)}/${String(params.verb)}`);
      if (opts.writeStatus) return HttpResponse.json(errorBody(opts.writeMessage ?? 'refused'), { status: opts.writeStatus });
      const row = rows().find((r) => r.id === params.id);
      return HttpResponse.json({ ...row, state: params.verb === 'activate' ? 'activated' : 'draft' });
    }),
    http.get('*/authoring/workflows/:id', ({ params }) => {
      const yaml = opts.exports?.[String(params.id)];
      if (yaml === undefined) return HttpResponse.json(errorBody('The process does not exist.'), { status: 404 });
      return new HttpResponse(yaml, { headers: { 'Content-Type': 'text/yaml' } });
    }),
    // The editor's live dry run: nothing to report.
    http.post('*/authoring/workflows/apply', () =>
      HttpResponse.json({ id: '', name: '', action: 'unchanged', state: 'draft', stateChanged: false, assumedShapes: [], errors: [] }),
    ),
    http.get('*/api/data/v9.2/EntityDefinitions*', ({ request }) => {
      const path = decodeURIComponent(new URL(request.url).pathname);
      const name = /LogicalName='([^']*)'/.exec(path)?.[1];
      if (name === undefined) return HttpResponse.json({ value: Object.values(opts.entities ?? {}) });
      const found = opts.entities?.[name];
      return found ? HttpResponse.json(found) : HttpResponse.json(errorBody(`The table ${name} does not exist.`), { status: 404 });
    }),
    http.get('*/api/data/v9.2/workflows', () =>
      HttpResponse.json({ value: rows().map((r) => ({ workflowid: r.id, ismanaged: (opts.managed ?? []).includes(r.id) })) }),
    ),
    http.delete('*/api/data/v9.2/*', ({ request }) => {
      calls.deletes.push(decodeURIComponent(new URL(request.url).pathname.replace(/^.*\/api\/data\/v9\.2\//, '')));
      if (opts.writeStatus) return HttpResponse.json(errorBody(opts.writeMessage ?? 'refused'), { status: opts.writeStatus });
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return calls;
}

export const summary = (over: Partial<WorkflowSummary> & { id: string; name: string }): WorkflowSummary => ({
  kind: 'workflow',
  entity: 'xrmv_order',
  mode: 'realtime',
  state: 'draft',
  unsupportedSteps: 0,
  ...over,
});
