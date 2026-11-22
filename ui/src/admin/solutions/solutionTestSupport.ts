import { http, HttpResponse } from 'msw';
import { server } from '../../test/server';
import { API } from '../security/testSupport';

export const SOL1 = 'aaaaaaaa-1111-0000-0000-000000000001';
export const SOL2 = 'aaaaaaaa-1111-0000-0000-000000000002';
export const SOL_DEFAULT = 'aaaaaaaa-1111-0000-0000-000000000003';
export const PUB1 = 'bbbbbbbb-1111-0000-0000-000000000001';
export const SYSTEM_CUSTOMIZER = '119f245c-3cc8-4b62-b31c-d1a046ced15d';

export const solution = (extra: object = {}) => ({
  solutionid: SOL1,
  uniquename: 'sales_kit',
  friendlyname: 'Sales Kit',
  version: '1.2.0.0',
  ismanaged: false,
  _publisherid_value: PUB1,
  ...extra,
});

/** The Web API reads every page of the Solutions page makes: the list (counted) and the publisher names. */
export function serveSolutions(rows: object[] = [solution()]) {
  const counts = { list: 0 };
  server.use(
    http.get(`${API}/solutions`, () => {
      counts.list++;
      return HttpResponse.json({ value: rows });
    }),
    http.get(`${API}/publishers`, () => HttpResponse.json({ value: [{ publisherid: PUB1, friendlyname: 'Acme Publisher' }] })),
  );
  return counts;
}

/** Makes the signed-in user a System Customizer only (the gate reads these two tables). */
export function signInAsCustomizerOnly() {
  server.use(
    http.get(`${API}/systemuserrolescollection`, () => HttpResponse.json({ value: [{ roleid: SYSTEM_CUSTOMIZER }] })),
    http.get(`${API}/roles`, () =>
      HttpResponse.json({ value: [{ roleid: SYSTEM_CUSTOMIZER, name: 'System Customizer', roletemplateid: SYSTEM_CUSTOMIZER }] }),
    ),
  );
}
