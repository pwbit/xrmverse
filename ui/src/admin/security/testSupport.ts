import { http, HttpResponse } from 'msw';
import { server } from '../../test/server';
import { saveDevCredentials } from '../../shell/auth/devToken';

export const SYSTEM_ADMIN = '627090ff-40a3-4053-8790-584edc5be201';
export const ADMIN_USER = 'aaaaaaaa-0000-0000-0000-000000000001';
export const API = '*/api/data/v9.2';

/** Signs in a System Administrator (dev token in sessionStorage) and answers the access gate's requests. */
export function signInAsAdministrator() {
  sessionStorage.clear();
  saveDevCredentials({ signingKey: 'k'.repeat(40), objectId: ADMIN_USER, name: 'Ada Admin' });
  server.use(
    http.get('*/ui/config.json', () =>
      HttpResponse.json({ authMode: 'Development', audience: 'http://localhost/', apiVersion: 'v9.2' }),
    ),
    http.get(`${API}/WhoAmI`, () => HttpResponse.json({ UserId: ADMIN_USER })),
    http.get(`${API}/systemuserrolescollection`, () => HttpResponse.json({ value: [{ roleid: SYSTEM_ADMIN }] })),
    http.get(`${API}/roles`, () => HttpResponse.json({ value: [{ roleid: SYSTEM_ADMIN, name: 'Role', roletemplateid: SYSTEM_ADMIN }] })),
    http.get(`${API}/teammemberships`, () => HttpResponse.json({ value: [] })),
    http.get(`${API}/organizations`, () => HttpResponse.json({ value: [{ name: 'Acme Dev' }] })),
  );
}

/** True for the access gate's own reads (they name the signed-in user or the System Administrator role). */
export function isGateRequest(request: Request): boolean {
  const filter = new URL(request.url).searchParams.get('$filter') ?? '';
  return filter.includes(ADMIN_USER) || filter.includes(SYSTEM_ADMIN);
}

export const filterOf = (request: Request) => new URL(request.url).searchParams.get('$filter');

type Method = 'get' | 'post' | 'patch' | 'put' | 'delete';
type Info = Parameters<Parameters<typeof http.get>[1]>[0];

/**
 * A handler for one Web API path that has a key in it (`systemusers(<id>)/…`); MSW path patterns read parentheses as
 * regex groups, so the path is compared as text instead. `path` is relative to `/api/data/v9.2/`.
 */
export function onPath(method: Method, path: string, resolver: (info: Info) => Response | Promise<Response>) {
  return http[method]('*/api/data/v9.2/*', (info) => {
    const relative = decodeURIComponent(new URL(info.request.url).pathname.slice('/api/data/v9.2/'.length));
    return relative === path ? resolver(info) : undefined;
  });
}
