import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { XrmApi } from '../../shell/api/client';
import { onPath } from '../../admin/security/testSupport';
import { server } from '../../test/server';
import { grantAccess, modifyAccess, parseAccessRights, retrievePrincipalAccess, retrieveShares, revokeAccess } from './access';

const api = () => new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => {} });
const U = 'aaaaaaaa-0000-0000-0000-000000000001';
const R = 'bbbbbbbb-0000-0000-0000-000000000002';

describe('access', () => {
  it('parses_the_access_rights_string_into_rights', () => {
    expect(parseAccessRights('ReadAccess, WriteAccess, AppendToAccess')).toEqual(['Read', 'Write', 'AppendTo']);
    expect(parseAccessRights('None')).toEqual([]);
  });

  it('retrieves_principal_access_with_the_target_alias', async () => {
    let seen = '';
    server.use(
      http.get('*/api/data/v9.2/*', ({ request }) => {
        seen = decodeURIComponent(new URL(request.url).pathname + new URL(request.url).search);
        return HttpResponse.json({ AccessRights: 'ReadAccess, ShareAccess' });
      }),
    );
    expect(await retrievePrincipalAccess(api(), U, 'accounts', R)).toEqual(['Read', 'Share']);
    expect(seen).toContain(`systemusers(${U})/Microsoft.Dynamics.CRM.RetrievePrincipalAccess(Target=@t)`);
    expect(seen).toContain(`@t={"@odata.id":"accounts(${R})"}`);
  });

  it('lists_shares', async () => {
    server.use(
      onPath('get', 'RetrieveSharedPrincipalsAndAccess(Target=@t)', () =>
        HttpResponse.json({
          PrincipalAccesses: [
            { AccessMask: 'ReadAccess, WriteAccess', Principal: { '@odata.type': '#Microsoft.Dynamics.CRM.systemuser', systemuserid: U } },
            { AccessMask: 'ReadAccess', Principal: { '@odata.type': '#Microsoft.Dynamics.CRM.team', teamid: R } },
          ],
        }),
      ),
    );
    expect(await retrieveShares(api(), 'accounts', R)).toEqual([
      { principal: { type: 'systemuser', id: U }, rights: ['Read', 'Write'] },
      { principal: { type: 'team', id: R }, rights: ['Read'] },
    ]);
  });

  it('grant_modify_revoke_post_the_documented_bodies', async () => {
    const posts: [string, unknown][] = [];
    server.use(
      http.post('*/api/data/v9.2/:action', async ({ request, params }) => {
        posts.push([String(params.action), await request.json()]);
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const principal = { type: 'team' as const, id: U };
    await grantAccess(api(), 'accounts', R, principal, ['Read', 'AppendTo']);
    await modifyAccess(api(), 'accounts', R, principal, ['Write']);
    await revokeAccess(api(), 'accounts', R, principal);
    const target = { '@odata.id': `accounts(${R})` };
    expect(posts).toEqual([
      ['GrantAccess', { Target: target, PrincipalAccess: { AccessMask: 'ReadAccess, AppendToAccess', Principal: { '@odata.type': 'Microsoft.Dynamics.CRM.team', teamid: U } } }],
      ['ModifyAccess', { Target: target, PrincipalAccess: { AccessMask: 'WriteAccess', Principal: { '@odata.type': 'Microsoft.Dynamics.CRM.team', teamid: U } } }],
      ['RevokeAccess', { Target: target, Revokee: { '@odata.type': 'Microsoft.Dynamics.CRM.team', teamid: U } }],
    ]);
  });
});
