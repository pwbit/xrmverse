import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { onPath, signInAsAdministrator, API } from '../security/testSupport';
import { installMetadata, rawAttr, rawEntity } from '../tables/tableTestSupport';

const PATH = '/manage/duplicates';
const ORG = '00000000-0000-0000-0000-0000000000a1';
const RULE = '11111111-0000-0000-0000-000000000001';
const DRAFT = '11111111-0000-0000-0000-000000000002';
const JOB = '22222222-0000-0000-0000-000000000002';

interface Rule {
  duplicateruleid: string;
  name: string;
  baseentityname: string;
  matchingentityname: string;
  statuscode: number;
  statecode: number;
  iscasesensitive: boolean;
  excludeinactiverecords: boolean;
  description: string | null;
}

let rules: Rule[] = [];
let conditions: { duplicateruleconditionid: string; _regardingobjectid_value: string; baseattributename: string; matchingattributename: string; operatorcode: number; operatorparam: number | null; ignoreblankvalues: boolean }[] = [];
let ruleBodies: Record<string, unknown>[] = [];
let conditionBodies: Record<string, unknown>[] = [];
let orgPatches: Record<string, unknown>[] = [];
let unpublished: unknown[] = [];
let jobStates: { statecode: number; statuscode: number; message: string | null }[] = [];
let deleted: string[] = [];
let orgFlags = { isduplicatedetectionenabled: true, isduplicatedetectionenabledforonlinecreateupdate: false, isduplicatedetectionenabledforimport: false, isduplicatedetectionenabledforofflinesync: false };

const contact = (flag: boolean) =>
  rawEntity(
    'contact',
    'contacts',
    [rawAttr('contactid', 'Uniqueidentifier', { IsPrimaryId: true }), rawAttr('emailaddress1', 'String'), rawAttr('lastname', 'String'), rawAttr('birthdate', 'DateTime')],
    { PrimaryIdAttribute: 'contactid', PrimaryNameAttribute: 'lastname', IsDuplicateDetectionEnabled: { Value: flag, CanBeChanged: true } },
  );

function mockServer() {
  rules = [
    { duplicateruleid: RULE, name: 'Same email', baseentityname: 'contact', matchingentityname: 'contact', statuscode: 2, statecode: 1, iscasesensitive: false, excludeinactiverecords: false, description: null },
    { duplicateruleid: DRAFT, name: 'Same name', baseentityname: 'contact', matchingentityname: 'contact', statuscode: 0, statecode: 0, iscasesensitive: false, excludeinactiverecords: false, description: null },
  ];
  conditions = [
    { duplicateruleconditionid: 'c1', _regardingobjectid_value: RULE, baseattributename: 'emailaddress1', matchingattributename: 'emailaddress1', operatorcode: 0, operatorparam: null, ignoreblankvalues: true },
  ];
  ruleBodies = [];
  conditionBodies = [];
  orgPatches = [];
  unpublished = [];
  deleted = [];
  jobStates = [];
  orgFlags = { isduplicatedetectionenabled: true, isduplicatedetectionenabledforonlinecreateupdate: false, isduplicatedetectionenabledforimport: false, isduplicatedetectionenabledforofflinesync: false };
  installMetadata([contact(true), rawEntity('account', 'accounts', [rawAttr('accountid', 'Uniqueidentifier', { IsPrimaryId: true })], { IsDuplicateDetectionEnabled: { Value: false, CanBeChanged: true } })]);
  server.use(
    http.get(`${API}/organizations`, () => HttpResponse.json({ value: [{ name: 'Acme Dev', organizationid: ORG, ...orgFlags }] })),
    onPath('patch', `organizations(${ORG})`, async ({ request }) => {
      const body = (await request.json()) as Record<string, unknown>;
      orgPatches.push(body);
      orgFlags = { ...orgFlags, ...(body as object) };
      return new HttpResponse(null, { status: 204 });
    }),
    onPath('get', 'duplicaterules', () => HttpResponse.json({ value: rules })),
    onPath('post', 'duplicaterules', async ({ request }) => {
      const body = (await request.json()) as Record<string, unknown>;
      ruleBodies.push(body);
      rules = [...rules, { duplicateruleid: 'new-rule', name: String(body.name), baseentityname: String(body.baseentityname), matchingentityname: String(body.matchingentityname), statuscode: 0, statecode: 0, iscasesensitive: body.iscasesensitive === true, excludeinactiverecords: body.excludeinactiverecords === true, description: null }];
      return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `${new URL(request.url).origin}/api/data/v9.2/duplicaterules(new-rule)` } });
    }),
    onPath('get', 'duplicateruleconditions', () => HttpResponse.json({ value: conditions })),
    onPath('post', 'duplicateruleconditions', async ({ request }) => {
      conditionBodies.push((await request.json()) as Record<string, unknown>);
      return new HttpResponse(null, { status: 204 });
    }),
    onPath('post', 'UnpublishDuplicateRule', async ({ request }) => {
      unpublished.push(await request.json());
      rules = rules.map((r) => (r.duplicateruleid === RULE ? { ...r, statuscode: 0, statecode: 0 } : r));
      return new HttpResponse(null, { status: 204 });
    }),
    onPath('post', `duplicaterules(${DRAFT})/Microsoft.Dynamics.CRM.PublishDuplicateRule`, () => {
      rules = rules.map((r) => (r.duplicateruleid === DRAFT ? { ...r, statuscode: 1, statecode: 1 } : r));
      return HttpResponse.json({ asyncoperationid: JOB, operationtype: 7 });
    }),
    onPath('get', `asyncoperations(${JOB})`, () => {
      const next = jobStates.length > 1 ? jobStates.shift()! : jobStates[0];
      if (next.statecode === 3) rules = rules.map((r) => (r.duplicateruleid === DRAFT ? { ...r, statuscode: 2 } : r));
      return HttpResponse.json(next);
    }),
    onPath('delete', `duplicaterules(${DRAFT})`, () => {
      deleted.push(DRAFT);
      rules = rules.filter((r) => r.duplicateruleid !== DRAFT);
      return new HttpResponse(null, { status: 204 });
    }),
  );
}

beforeEach(() => {
  signInAsAdministrator();
  mockServer();
});
afterEach(() => sessionStorage.clear());

async function open() {
  render(<App initialEntries={[PATH]} />);
  // Wait until all three mount requests have been answered: the rules, the switches and the tables.
  expect(await screen.findByText('Same email')).toBeInTheDocument();
  await waitFor(() => expect(screen.getByRole('switch', { name: en['duplicates.switch.enabled'] })).toBeChecked());
}

const rowOf = (name: string) => screen.getByText(name).closest('tr') as HTMLElement;
const selectRow = (user: ReturnType<typeof userEvent.setup>, name: string) =>
  user.click(screen.getByRole('radio', { hidden: true, name: `Select ${name}` }));

describe('DuplicateRulesPage', () => {
  it('lists rules with status and the org switches, and says import and offline sync apply to nothing', async () => {
    await open();

    expect(screen.getAllByText(en['nav.duplicateRules']).length).toBeGreaterThan(1);
    expect(within(rowOf('Same email')).getByText(en['duplicates.status.2'])).toBeInTheDocument();
    expect(within(rowOf('Same name')).getByText(en['duplicates.status.0'])).toBeInTheDocument();
    expect(screen.getByRole('switch', { name: en['duplicates.switch.createUpdate'] })).not.toBeChecked();
    expect(screen.getByRole('switch', { name: en['duplicates.switch.import'] })).toBeInTheDocument();
    expect(screen.getByRole('switch', { name: en['duplicates.switch.offline'] })).toBeInTheDocument();
    expect(screen.getByText(en['duplicates.switch.stored'])).toBeInTheDocument();
  });

  it('switches the organization flags with a PATCH of organizations', async () => {
    const user = userEvent.setup();
    await open();

    await user.click(screen.getByRole('switch', { name: en['duplicates.switch.createUpdate'] }));

    await waitFor(() => expect(orgPatches).toEqual([{ isduplicatedetectionenabledforonlinecreateupdate: true }]));
    await waitFor(() => expect(screen.getByRole('switch', { name: en['duplicates.switch.createUpdate'] })).toBeChecked());
  });

  it('creates rule with two conditions', async () => {
    const user = userEvent.setup();
    await open();

    await user.click(screen.getByRole('button', { name: en['duplicates.new'] }));
    const dialog = await screen.findByRole('dialog', { hidden: true });
    await user.type(within(dialog).getByLabelText(en['duplicates.dialog.name']), 'Mail or name');
    await user.selectOptions(within(dialog).getByLabelText(en['duplicates.dialog.base']), 'contact');
    await user.selectOptions(within(dialog).getByLabelText(en['duplicates.dialog.matching']), 'contact');
    // The account table has duplicate detection off: it is not offered.
    expect(within(within(dialog).getByLabelText(en['duplicates.dialog.base'])).queryByRole('option', { name: 'account' })).toBeNull();
    // The label-picklist operator is never offered.
    expect(within(dialog).queryByRole('option', { name: /label/i })).toBeNull();
    await waitFor(() => expect(within(dialog).getAllByRole('option', { name: 'emailaddress1' }).length).toBeGreaterThan(1));

    await user.selectOptions(within(dialog).getByLabelText('Base column 1'), 'emailaddress1');
    await user.click(await within(dialog).findByRole('checkbox', { hidden: true, name: 'Ignore blank values 1' }));
    await user.click(within(dialog).getByRole('button', { name: en['duplicates.dialog.addCondition'] }));
    await user.selectOptions(within(dialog).getByLabelText('Base column 2'), 'lastname');
    await user.selectOptions(within(dialog).getByLabelText('Operator 2'), String(1));
    await user.type(within(dialog).getByLabelText('Characters 2'), '4');
    await user.click(await within(dialog).findByRole('checkbox', { hidden: true, name: en['duplicates.dialog.excludeInactive'] }));
    await user.click(within(dialog).getByRole('button', { name: en['duplicates.dialog.save'] }));

    await waitFor(() => expect(conditionBodies).toHaveLength(2));
    expect(ruleBodies[0]).toMatchObject({ name: 'Mail or name', baseentityname: 'contact', matchingentityname: 'contact', excludeinactiverecords: true, iscasesensitive: false });
    expect(conditionBodies[0]).toEqual({
      baseattributename: 'emailaddress1',
      matchingattributename: 'emailaddress1',
      operatorcode: 0,
      ignoreblankvalues: true,
      'regardingobjectid@odata.bind': 'duplicaterules(new-rule)',
    });
    expect(conditionBodies[1]).toMatchObject({ baseattributename: 'lastname', matchingattributename: 'lastname', operatorcode: 1, operatorparam: 4, ignoreblankvalues: false });
    expect(await screen.findByText('Mail or name')).toBeInTheDocument();
  });

  it('publishes and shows job progress', async () => {
    jobStates = [
      { statecode: 2, statuscode: 20, message: null },
      { statecode: 3, statuscode: 30, message: null },
    ];
    const user = userEvent.setup();
    await open();

    await selectRow(user, 'Same name');
    await user.click(screen.getByRole('button', { name: 'Publish Same name' }));

    expect(await screen.findByText(/Publishing "Same name": running/)).toBeInTheDocument();
    await waitFor(() => expect(within(rowOf('Same name')).getByText(en['duplicates.status.2'])).toBeInTheDocument(), { timeout: 5000 });
    await waitFor(() => expect(screen.queryByText(/Publishing "Same name"/)).toBeNull());
  });

  it('shows a failed publish job message', async () => {
    jobStates = [{ statecode: 3, statuscode: 31, message: 'Out of space.' }];
    const user = userEvent.setup();
    await open();

    await selectRow(user, 'Same name');
    await user.click(screen.getByRole('button', { name: 'Publish Same name' }));

    expect(await screen.findByText(/did not succeed: Out of space\./)).toBeInTheDocument();
  });

  it('unpublish confirms', async () => {
    const user = userEvent.setup();
    await open();

    await selectRow(user, 'Same email');
    await user.click(screen.getByRole('button', { name: 'Unpublish Same email' }));
    const dialog = await screen.findByRole('dialog', { hidden: true });
    expect(within(dialog).getByText(/match codes are dropped/)).toBeInTheDocument();
    expect(unpublished).toEqual([]);
    await user.click(within(dialog).getByRole('button', { hidden: true, name: en['duplicates.unpublishButton'] }));

    await waitFor(() => expect(unpublished).toEqual([{ DuplicateRuleId: RULE }]));
    await waitFor(() => expect(within(rowOf('Same email')).getByText(en['duplicates.status.0'])).toBeInTheDocument());
  });

  it('deletes a rule after the confirmation and keeps it when cancelled', async () => {
    const user = userEvent.setup();
    await open();

    await selectRow(user, 'Same name');
    await user.click(screen.getByRole('button', { name: 'Delete Same name' }));
    let dialog = await screen.findByRole('dialog', { hidden: true });
    await user.click(within(dialog).getByRole('button', { hidden: true, name: en['common.cancel'] }));
    await waitFor(() => expect(screen.queryByRole('dialog', { hidden: true })).toBeNull());
    expect(deleted).toEqual([]);

    // The rule is still selected.
    await user.click(screen.getByRole('button', { name: 'Delete Same name' }));
    dialog = await screen.findByRole('dialog', { hidden: true });
    await user.click(within(dialog).getByRole('button', { hidden: true, name: en['duplicates.deleteButton'] }));

    await waitFor(() => expect(deleted).toEqual([DRAFT]));
    await waitFor(() => expect(screen.queryByText('Same name')).toBeNull());
  });

  it('shows server refusal', async () => {
    server.use(
      onPath('post', `duplicaterules(${DRAFT})/Microsoft.Dynamics.CRM.PublishDuplicateRule`, () =>
        HttpResponse.json({ error: { code: '0x80040203', message: 'The rule has no conditions.' } }, { status: 400 }),
      ),
    );
    const user = userEvent.setup();
    await open();

    await selectRow(user, 'Same name');
    await user.click(screen.getByRole('button', { name: 'Publish Same name' }));

    expect(await screen.findByText(/The rule has no conditions\./)).toBeInTheDocument();
    expect(within(rowOf('Same name')).getByText(en['duplicates.status.0'])).toBeInTheDocument();
  });

  it('shows the server refusal of a rule that cannot be saved, and the dialog stays open', async () => {
    server.use(
      onPath('post', 'duplicaterules', () =>
        HttpResponse.json({ error: { code: '0x80040203', message: 'A table can have at most 5 published rules.' } }, { status: 400 }),
      ),
    );
    const user = userEvent.setup();
    await open();

    await user.click(screen.getByRole('button', { name: en['duplicates.new'] }));
    const dialog = await screen.findByRole('dialog', { hidden: true });
    await user.type(within(dialog).getByLabelText(en['duplicates.dialog.name']), 'X');
    await user.selectOptions(within(dialog).getByLabelText(en['duplicates.dialog.base']), 'contact');
    await user.selectOptions(within(dialog).getByLabelText(en['duplicates.dialog.matching']), 'contact');
    await waitFor(() => expect(within(dialog).getAllByRole('option', { name: 'lastname' }).length).toBeGreaterThan(1));
    await user.selectOptions(within(dialog).getByLabelText('Base column 1'), 'lastname');
    await user.click(within(dialog).getByRole('button', { name: en['duplicates.dialog.save'] }));

    expect(await within(dialog).findByText(/at most 5 published rules/)).toBeInTheDocument();
    expect(conditionBodies).toEqual([]);
  });

  it('edits a rule: loads its conditions and patches only the changed one', async () => {
    const patches: { path: string; body: Record<string, unknown> }[] = [];
    server.use(
      onPath('patch', `duplicaterules(${RULE})`, async ({ request }) => {
        patches.push({ path: 'rule', body: (await request.json()) as Record<string, unknown> });
        return new HttpResponse(null, { status: 204 });
      }),
      onPath('patch', 'duplicateruleconditions(c1)', async ({ request }) => {
        patches.push({ path: 'condition', body: (await request.json()) as Record<string, unknown> });
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    await open();

    await selectRow(user, 'Same email');
    await user.click(screen.getByRole('button', { name: 'Edit Same email' }));
    const dialog = await screen.findByRole('dialog', { hidden: true });
    await waitFor(() => expect(within(dialog).getByLabelText('Base column 1')).toHaveValue('emailaddress1'));
    expect(within(dialog).getByLabelText(en['duplicates.dialog.base'])).toBeDisabled();
    await user.click(await within(dialog).findByRole('checkbox', { hidden: true, name: 'Ignore blank values 1' }));
    await user.click(within(dialog).getByRole('button', { name: en['duplicates.dialog.save'] }));

    await waitFor(() => expect(patches).toHaveLength(2));
    expect(patches[0].body).toMatchObject({ name: 'Same email' });
    expect(patches[1].body).toMatchObject({ baseattributename: 'emailaddress1', operatorcode: 0, ignoreblankvalues: false });
  });

  it('after a partly failed edit, reloads the conditions, says so, and a second Save works', async () => {
    conditions.push({ duplicateruleconditionid: 'c2', _regardingobjectid_value: RULE, baseattributename: 'lastname', matchingattributename: 'lastname', operatorcode: 1, operatorparam: 3, ignoreblankvalues: false });
    let failPatch = true;
    const calls: string[] = [];
    server.use(
      onPath('patch', `duplicaterules(${RULE})`, () => new HttpResponse(null, { status: 204 })),
      onPath('delete', 'duplicateruleconditions(c1)', () => {
        calls.push('delete c1');
        if (!conditions.some((c) => c.duplicateruleconditionid === 'c1')) return new HttpResponse(null, { status: 404 });
        conditions = conditions.filter((c) => c.duplicateruleconditionid !== 'c1');
        return new HttpResponse(null, { status: 204 });
      }),
      onPath('patch', 'duplicateruleconditions(c2)', async ({ request }) => {
        const body = (await request.json()) as Record<string, unknown>;
        calls.push(`patch c2 ${JSON.stringify(body.operatorparam)}`);
        if (failPatch) return HttpResponse.json({ error: { code: '0x80040203', message: 'N is too large.' } }, { status: 400 });
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    await open();

    await selectRow(user, 'Same email');
    await user.click(screen.getByRole('button', { name: 'Edit Same email' }));
    const dialog = await screen.findByRole('dialog', { hidden: true });
    await waitFor(() => expect(within(dialog).getByLabelText('Base column 2')).toHaveValue('lastname'));
    await user.click(await within(dialog).findByRole('button', { hidden: true, name: 'Remove condition 1' }));
    // Condition 2 is now the first row; change its operator to one without N.
    await user.selectOptions(within(dialog).getByLabelText('Operator 1'), '0');
    await user.click(within(dialog).getByRole('button', { name: en['duplicates.dialog.save'] }));

    expect(await within(dialog).findByText(/N is too large\./)).toBeInTheDocument();
    expect(within(dialog).getByText(/may be partly saved/)).toBeInTheDocument();
    await waitFor(() => expect(within(dialog).queryByLabelText('Base column 2')).toBeNull());
    expect(within(dialog).getByLabelText('Base column 1')).toHaveValue('lastname');
    expect(calls).toEqual(['delete c1', 'patch c2 null']);

    failPatch = false;
    await user.click(within(dialog).getByRole('button', { name: en['duplicates.dialog.save'] }));
    await waitFor(() => expect(screen.queryByRole('dialog', { hidden: true })).toBeNull());
    expect(calls).toEqual(['delete c1', 'patch c2 null']);
  });
});
