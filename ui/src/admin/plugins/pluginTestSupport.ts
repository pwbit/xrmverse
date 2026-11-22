import { http, HttpResponse } from 'msw';
import { server } from '../../test/server';
import { API } from '../security/testSupport';

export const ASM = 'aaaaaaaa-3333-0000-0000-000000000001';
export const TYPE1 = 'bbbbbbbb-3333-0000-0000-000000000001';
export const TYPE2 = 'bbbbbbbb-3333-0000-0000-000000000002';
export const STEP1 = 'cccccccc-3333-0000-0000-000000000001';

export const step = (extra: object = {}) => ({
  sdkmessageprocessingstepid: STEP1,
  name: 'Acme.Plugins.AccountPlugin: Create of account',
  stage: 20,
  mode: 0,
  rank: 1,
  statecode: 0,
  statuscode: 1,
  ...extra,
});

/** Serves one assembly with two types; only the first has a step. Returns the paths of each request kind, for assertions. */
export function servePlugins() {
  const seen = { assemblies: 0, types: [] as (string | null)[], steps: [] as (string | null)[] };
  server.use(
    http.get(`${API}/pluginassemblies`, () => {
      seen.assemblies++;
      return HttpResponse.json({ value: [{ pluginassemblyid: ASM, name: 'Acme.Plugins', version: '1.0.0.0' }] });
    }),
    http.get(`${API}/plugintypes`, ({ request }) => {
      seen.types.push(new URL(request.url).searchParams.get('$filter'));
      return HttpResponse.json({
        value: [
          { plugintypeid: TYPE1, typename: 'Acme.Plugins.AccountPlugin' },
          { plugintypeid: TYPE2, typename: 'Acme.Plugins.ContactPlugin' },
        ],
      });
    }),
    http.get(`${API}/sdkmessageprocessingsteps`, ({ request }) => {
      const filter = new URL(request.url).searchParams.get('$filter');
      seen.steps.push(filter);
      return HttpResponse.json({ value: filter?.includes(TYPE1) ? [step()] : [] });
    }),
  );
  return seen;
}
