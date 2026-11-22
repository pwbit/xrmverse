import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { API, filterOf, isGateRequest, onPath, signInAsAdministrator } from './testSupport';

const BU1 = 'bbbbbbbb-0000-0000-0000-000000000001';
const TEAM = 'eeeeeeee-0000-0000-0000-000000000001';
const ACCESS_TEAM = 'eeeeeeee-0000-0000-0000-000000000002';
const MEMBER = 'cccccccc-0000-0000-0000-000000000009';
const ROLE1 = 'dddddddd-0000-0000-0000-000000000001';

beforeEach(() => {
  signInAsAdministrator();
  server.use(
    http.get(`${API}/businessunits`, () => HttpResponse.json({ value: [{ businessunitid: BU1, name: 'Acme' }] })),
    http.get(`${API}/teams`, () =>
      HttpResponse.json({
        value: [
          { teamid: TEAM, name: 'Support', teamtype: 0, _businessunitid_value: BU1 },
          { teamid: ACCESS_TEAM, name: 'Shared', teamtype: 1, _businessunitid_value: BU1 },
        ],
      }),
    ),
  );
});
afterEach(() => sessionStorage.clear());

describe('TeamsPage', () => {
  it('lists teams and offers roles only for owner teams', async () => {
    render(<App initialEntries={['/manage/security/teams']} />);
    const user = userEvent.setup();
    expect(await screen.findByText('Support')).toBeInTheDocument();
    await user.click(screen.getByRole('radio', { hidden: true, name: 'Select Support' }));
    expect(screen.getByRole('button', { hidden: true, name: 'Roles of Support' })).toBeInTheDocument();
    await user.click(screen.getByRole('radio', { hidden: true, name: 'Select Shared' }));
    expect(screen.queryByRole('button', { hidden: true, name: 'Roles of Shared' })).toBeNull();
    expect(screen.getByRole('button', { hidden: true, name: 'Members of Shared' })).toBeInTheDocument();
  });

  it('Create_owner_team_posts_teamtype_0_with_business_unit_bind', async () => {
    let posted: unknown;
    server.use(
      http.post(`${API}/teams`, async ({ request }) => {
        posted = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/teams']} />);
    await user.click(await screen.findByRole('button', { hidden: true, name: en['teams.create.open'] }));
    await user.type(await screen.findByLabelText(new RegExp(`^${en['teams.field.name']}`)), 'Billing');
    await user.click(await screen.findByRole('option', { hidden: true, name: 'Acme' }));
    await user.click(screen.getByRole('button', { hidden: true, name: en['teams.create.submit'] }));
    expect(await screen.findByText('Team "Billing" created.')).toBeInTheDocument();
    expect(posted).toEqual({ name: 'Billing', teamtype: 0, 'businessunitid@odata.bind': `/businessunits(${BU1})` });
  });

  it('Team_add_member_associates_teammembership', async () => {
    let associated: { path: string; id: string } | undefined;
    server.use(
      http.get(`${API}/teammemberships`, ({ request }) => (isGateRequest(request) ? undefined : HttpResponse.json({ value: [] }))),
      http.get(`${API}/systemusers`, () => HttpResponse.json({ value: [{ systemuserid: MEMBER, fullname: 'Mia Member' }] })),
      onPath('post', `teams(${TEAM})/teammembership_association/$ref`, async ({ request }) => {
        associated = { path: new URL(request.url).pathname, id: ((await request.json()) as Record<string, string>)['@odata.id'] };
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/teams']} />);
    await user.click(await screen.findByRole('radio', { hidden: true, name: 'Select Support' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: 'Members of Support' }));
    expect(await screen.findByText(en['link.teamMembers.empty'])).toBeInTheDocument();
    await user.click(await screen.findByRole('option', { hidden: true, name: 'Mia Member' }));
    await user.click(screen.getByRole('button', { hidden: true, name: en['link.teamMembers.add'] }));
    expect(await screen.findByText('"Mia Member" added to the team.')).toBeInTheDocument();
    expect(associated?.path).toBe(`/api/data/v9.2/teams(${TEAM})/teammembership_association/$ref`);
    expect(associated?.id).toMatch(new RegExp(`/api/data/v9.2/systemusers\\(${MEMBER}\\)$`));
  });

  it('Remove_member_confirms_then_disassociates', async () => {
    let deleted: string | undefined;
    const pickerFilters: (string | null)[] = [];
    server.use(
      http.get(`${API}/teammemberships`, ({ request }) =>
        isGateRequest(request) ? undefined : HttpResponse.json({ value: deleted ? [] : [{ systemuserid: MEMBER }] }),
      ),
      http.get(`${API}/systemusers`, ({ request }) => {
        if (!filterOf(request)?.includes('systemuserid eq')) pickerFilters.push(filterOf(request));
        return HttpResponse.json({ value: [{ systemuserid: MEMBER, fullname: 'Mia Member' }] });
      }),
      onPath('delete', `teams(${TEAM})/teammembership_association(${MEMBER})/$ref`, ({ request }) => {
        deleted = new URL(request.url).pathname;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/teams']} />);
    await user.click(await screen.findByRole('radio', { hidden: true, name: 'Select Support' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: 'Members of Support' }));
    await user.click(await screen.findByRole('radio', { hidden: true, name: 'Select Mia Member' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: en['link.teamMembers.remove'] }));
    expect(deleted).toBeUndefined();
    const dialogs = await screen.findAllByRole('dialog', { hidden: true });
    await user.click(await within(dialogs.at(-1)!).findByRole('button', { hidden: true, name: en['link.teamMembers.remove'] }));
    expect(await screen.findByText(en['link.teamMembers.empty'])).toBeInTheDocument();
    expect(deleted).toBe(`/api/data/v9.2/teams(${TEAM})/teammembership_association(${MEMBER})/$ref`);
    // The picker offers the removed member again (no exclusion left); wait for that query so none outlives the test.
    await waitFor(() => expect(pickerFilters.at(-1)).toBeNull());
  });

  it('Team_role_picker_lists_roles_of_the_teams_bu_and_associates_teamroles', async () => {
    let pickerFilter: string | null = null;
    let associated: string | undefined;
    server.use(
      http.get(`${API}/teamrolescollection`, () => HttpResponse.json({ value: [] })),
      http.get(`${API}/roles`, ({ request }) => {
        if (isGateRequest(request)) return undefined;
        pickerFilter = filterOf(request);
        return HttpResponse.json({ value: [{ roleid: ROLE1, name: 'Support Agent' }] });
      }),
      onPath('post', `teams(${TEAM})/teamroles_association/$ref`, ({ request }) => {
        associated = new URL(request.url).pathname;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/teams']} />);
    await user.click(await screen.findByRole('radio', { hidden: true, name: 'Select Support' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: 'Roles of Support' }));
    await user.click(await screen.findByRole('option', { hidden: true, name: 'Support Agent' }));
    await user.click(screen.getByRole('button', { hidden: true, name: en['link.teamRoles.add'] }));
    expect(await screen.findByText('Role "Support Agent" assigned.')).toBeInTheDocument();
    expect(pickerFilter).toBe(`(_businessunitid_value eq ${BU1})`);
    expect(associated).toBe(`/api/data/v9.2/teams(${TEAM})/teamroles_association/$ref`);
  });
});
