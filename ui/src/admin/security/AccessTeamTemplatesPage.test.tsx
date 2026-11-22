import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { onPath, signInAsAdministrator } from './testSupport';

const PATH = '/manage/security/accesstemplates';
const SALES = '11111111-0000-0000-0000-000000000001';
const SUPPORT = '11111111-0000-0000-0000-000000000002';
const READ = 1;
const WRITE = 2;
const DELETE = 65536;

interface Template {
  teamtemplateid: string;
  teamtemplatename: string;
  objecttypecode: number;
  defaultaccessrightsmask: number;
  description?: string | null;
  issystem: boolean;
}

let templates: Template[] = [];
let tables: { LogicalName: string; ObjectTypeCode: number; AutoCreateAccessTeams: boolean }[] = [];
let posts: Record<string, unknown>[] = [];
let patches: { id: string; body: Record<string, unknown> }[] = [];
let deletes: string[] = [];
let puts: Record<string, unknown>[] = [];

function mockServer() {
  templates = [
    { teamtemplateid: SALES, teamtemplatename: 'Sales team', objecttypecode: 1, defaultaccessrightsmask: READ | WRITE, issystem: false },
    { teamtemplateid: SUPPORT, teamtemplatename: 'Support team', objecttypecode: 1, defaultaccessrightsmask: DELETE, issystem: false },
  ];
  tables = [
    { LogicalName: 'account', ObjectTypeCode: 1, AutoCreateAccessTeams: true },
    { LogicalName: 'contact', ObjectTypeCode: 2, AutoCreateAccessTeams: false },
  ];
  posts = [];
  patches = [];
  deletes = [];
  puts = [];
  server.use(
    onPath('get', 'EntityDefinitions', () => HttpResponse.json({ value: tables })),
    onPath('get', "EntityDefinitions(LogicalName='contact')", () => HttpResponse.json({ LogicalName: 'contact', ObjectTypeCode: 2, AutoCreateAccessTeams: false })),
    onPath('put', "EntityDefinitions(LogicalName='contact')", async ({ request }) => {
      const body = (await request.json()) as Record<string, unknown>;
      puts.push(body);
      tables = tables.map((x) => (x.LogicalName === 'contact' ? { ...x, AutoCreateAccessTeams: body.AutoCreateAccessTeams === true } : x));
      return new HttpResponse(null, { status: 204 });
    }),
    onPath('get', 'teamtemplates', () => HttpResponse.json({ value: templates })),
    onPath('post', 'teamtemplates', async ({ request }) => {
      const body = (await request.json()) as Record<string, unknown>;
      posts.push(body);
      templates = [...templates, { teamtemplateid: 'new-id', teamtemplatename: String(body.teamtemplatename), objecttypecode: Number(body.objecttypecode), defaultaccessrightsmask: Number(body.defaultaccessrightsmask), issystem: false }];
      return new HttpResponse(null, { status: 204 });
    }),
    onPath('patch', `teamtemplates(${SALES})`, async ({ request }) => {
      const body = (await request.json()) as Record<string, unknown>;
      patches.push({ id: SALES, body });
      templates = templates.map((x) => (x.teamtemplateid === SALES ? { ...x, defaultaccessrightsmask: Number(body.defaultaccessrightsmask) } : x));
      return new HttpResponse(null, { status: 204 });
    }),
    onPath('delete', `teamtemplates(${SUPPORT})`, () => {
      deletes.push(SUPPORT);
      templates = templates.filter((x) => x.teamtemplateid !== SUPPORT);
      return new HttpResponse(null, { status: 204 });
    }),
  );
}

const rightBox = (right: string, name: string) => screen.getByRole('checkbox', { name: `${en[`accessTeams.right.${right}` as keyof typeof en]} for ${name}` });

beforeEach(() => {
  signInAsAdministrator();
  mockServer();
});
afterEach(() => sessionStorage.clear());

async function open() {
  render(<App initialEntries={[PATH]} />);
  // Wait until both mount requests have been answered: the rows name their table.
  expect(await screen.findByText('Sales team')).toBeInTheDocument();
  await waitFor(() => expect(screen.getAllByText('account').length).toBeGreaterThan(0));
}

describe('AccessTeamTemplatesPage', () => {
  it('is in the navigation and lists the templates with their table, name and rights', async () => {
    await open();

    expect(screen.getAllByText(en['nav.accessTeamTemplates']).length).toBeGreaterThan(1);
    expect(screen.getByText('Support team')).toBeInTheDocument();
    expect(rightBox('read', 'Sales team')).toBeChecked();
    expect(rightBox('write', 'Sales team')).toBeChecked();
    expect(rightBox('delete', 'Sales team')).not.toBeChecked();
    expect(rightBox('delete', 'Support team')).toBeChecked();
    expect(rightBox('read', 'Support team')).not.toBeChecked();
  });

  it('creates a template for the table with the mask of the ticked rights', async () => {
    const user = userEvent.setup();
    await open();

    await user.selectOptions(screen.getByLabelText(en['accessTeams.table']), 'account');
    await user.type(screen.getByLabelText(en['accessTeams.name']), 'Review team');
    await user.click(screen.getByRole('checkbox', { name: en['accessTeams.right.share'] }));
    await user.click(screen.getByRole('button', { name: en['accessTeams.create'] }));

    await waitFor(() => expect(posts).toHaveLength(1));
    expect(posts[0]).toEqual({ teamtemplatename: 'Review team', objecttypecode: 1, defaultaccessrightsmask: READ | 262144 });
    expect(await screen.findByText('Review team')).toBeInTheDocument();
  });

  it('edits the mask of a template with one PATCH', async () => {
    const user = userEvent.setup();
    await open();

    await user.click(rightBox('append', 'Sales team'));

    await waitFor(() => expect(patches).toHaveLength(1));
    expect(patches[0].body).toEqual({ defaultaccessrightsmask: READ | WRITE | 4 });
    await waitFor(() => expect(rightBox('append', 'Sales team')).toBeChecked());
  });

  it('deletes a template after the confirmation, which says its access teams go too', async () => {
    const user = userEvent.setup();
    await open();

    await user.click(screen.getByRole('radio', { name: 'Select Support team' }));
    await user.click(screen.getByRole('button', { name: 'Delete Support team' }));
    const dialog = await screen.findByRole('dialog', { hidden: true });
    expect(within(dialog).getByText(/access teams created from it are deleted too/)).toBeInTheDocument();
    expect(deletes).toEqual([]);
    await user.click(within(dialog).getByRole('button', { hidden: true, name: en['accessTeams.deleteButton'] }));

    await waitFor(() => expect(deletes).toEqual([SUPPORT]));
    await waitFor(() => expect(screen.queryByText('Support team')).toBeNull());
  });

  it('keeps the template when the confirmation is cancelled', async () => {
    const user = userEvent.setup();
    await open();

    await user.click(screen.getByRole('radio', { name: 'Select Support team' }));
    await user.click(screen.getByRole('button', { name: 'Delete Support team' }));
    const dialog = await screen.findByRole('dialog', { hidden: true });
    await user.click(within(dialog).getByRole('button', { hidden: true, name: en['common.cancel'] }));

    await waitFor(() => expect(screen.queryByRole('dialog', { hidden: true })).toBeNull());
    expect(deletes).toEqual([]);
    expect(screen.getByText('Support team')).toBeInTheDocument();
  });

  it("shows the server's 400 text for a refused create", async () => {
    server.use(
      onPath('post', 'teamtemplates', () =>
        HttpResponse.json({ error: { code: '0x80040203', message: 'A table can have at most 4 access team templates.' } }, { status: 400 }),
      ),
    );
    const user = userEvent.setup();
    await open();

    await user.selectOptions(screen.getByLabelText(en['accessTeams.table']), 'account');
    await user.type(screen.getByLabelText(en['accessTeams.name']), 'Fifth');
    await user.click(screen.getByRole('button', { name: en['accessTeams.create'] }));

    expect(await screen.findByText(/at most 4 access team templates/)).toBeInTheDocument();
  });

  it("shows the server's 400 text for a refused mask edit and keeps the saved mask", async () => {
    server.use(
      onPath('patch', `teamtemplates(${SALES})`, () =>
        HttpResponse.json({ error: { code: '0x80040203', message: 'defaultaccessrightsmask must not be 0.' } }, { status: 400 }),
      ),
    );
    const user = userEvent.setup();
    await open();

    await user.click(rightBox('read', 'Sales team'));
    await user.click(rightBox('write', 'Sales team'));

    expect(await screen.findByText(/must not be 0/)).toBeInTheDocument();
    expect(rightBox('write', 'Sales team')).toBeChecked();
  });

  it('switches Auto-create access teams on for the chosen table with a PUT of the whole definition', async () => {
    const user = userEvent.setup();
    await open();

    await user.selectOptions(screen.getByLabelText(en['accessTeams.table']), 'contact');
    const toggle = screen.getByRole('switch', { name: en['accessTeams.enable'] });
    expect(toggle).not.toBeChecked();
    expect(screen.getByRole('button', { name: en['accessTeams.create'] })).toBeDisabled();
    await user.click(toggle);

    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0]).toMatchObject({ LogicalName: 'contact', AutoCreateAccessTeams: true });
    await waitFor(() => expect(screen.getByRole('switch', { name: en['accessTeams.enable'] })).toBeChecked());
    expect(within(document.body).queryByRole('alert')).toBeNull();
  });

  it('shows why the templates could not be loaded', async () => {
    let entityDefinitionReads = 0;
    server.use(
      onPath('get', 'EntityDefinitions', () => {
        entityDefinitionReads += 1;
        return HttpResponse.json({ value: tables });
      }),
    );
    server.use(
      http.get('*/api/data/v9.2/teamtemplates', () =>
        HttpResponse.json({ error: { code: '0x80040220', message: 'Principal user is missing prvReadteamtemplate privilege.' } }, { status: 403 }),
      ),
    );
    render(<App initialEntries={[PATH]} />);

    expect(await screen.findByText(/could not be loaded.*prvReadteamtemplate/)).toBeInTheDocument();
    // Both mount requests of the page and the Header's organizations request must be answered before the test ends.
    expect(await screen.findByText('Acme Dev')).toBeInTheDocument();
    await waitFor(() => expect(entityDefinitionReads).toBeGreaterThan(0));
  });
});
