import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { API, onPath, signInAsAdministrator } from '../security/testSupport';

const PATH = '/manage/email';
const RELAY = 'e0000000-0000-0000-0000-000000000001';
const GRAPH = 'e0000000-0000-0000-0000-000000000002';
const ADA_MAILBOX = 'f0000000-0000-0000-0000-000000000001';
const ADA = 'a1000000-0000-0000-0000-000000000001';
const DEFINITION = '6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e21';

let posts: { path: string; body: Record<string, unknown> }[] = [];
let patches: { path: string; body: Record<string, unknown> }[] = [];
let settings: { organizationsettingid: string; value: string }[] = [];

function mockServer() {
  posts = [];
  patches = [];
  settings = [];
  server.use(
    onPath('get', 'emailserverprofiles', () =>
      HttpResponse.json({
        value: [
          {
            emailserverprofileid: RELAY, name: 'Relay', servertype: 4, outgoingserverlocation: 'smtp.example.com', outgoingportnumber: 587,
            outgoingusessl: false, outgoingusername: 'relay@example.com', outgoingcredentialretrieval: 1, isoutgoingpasswordset: true,
            isoauthclientsecretset: false, statecode: 0,
          },
          {
            emailserverprofileid: GRAPH, name: 'Graph', servertype: 0, exchangeonlinetenantid: 'contoso.example.com', oauthclientid: 'app-1',
            outgoingcredentialretrieval: 7, isoutgoingpasswordset: false, isoauthclientsecretset: false, statecode: 0,
          },
        ],
      }),
    ),
    onPath('get', 'mailboxes', () =>
      HttpResponse.json({
        value: [
          {
            mailboxid: ADA_MAILBOX, name: 'Ada', emailaddress: 'ada@example.com', _emailserverprofile_value: RELAY, emailrouteraccessapproval: 2,
            outgoingemaildeliverymethod: 2, enabledforoutgoingemail: true, outgoingemailstatus: 2,
            lastsyncerror: 'The SMTP server refused the user name or password.', statecode: 0,
          },
        ],
      }),
    ),
    onPath('get', 'systemusers', ({ request }) =>
      HttpResponse.json({ value: new URL(request.url).searchParams.get('$filter')?.includes(ADA_MAILBOX) ? [{ systemuserid: ADA }] : [] }),
    ),
    onPath('get', 'queues', () => HttpResponse.json({ value: [] })),
    onPath('get', 'settingdefinitions', () =>
      HttpResponse.json({ value: [{ settingdefinitionid: DEFINITION, uniquename: 'xrmverse_EmailSendFrom', defaultvalue: 'userOrQueue' }] }),
    ),
    onPath('get', 'organizationsettings', () => HttpResponse.json({ value: settings })),
    http.post(`${API}/:set`, async ({ request, params }) => {
      const body = (await request.json()) as Record<string, unknown>;
      posts.push({ path: String(params.set), body });
      if (params.set === 'organizationsettings') settings = [{ organizationsettingid: 'os-1', value: String(body.value) }];
      return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://localhost/api/data/v9.2/${String(params.set)}(00000000-0000-0000-0000-0000000000aa)` } });
    }),
    http.patch(`${API}/*`, async ({ request }) => {
      const relative = decodeURIComponent(new URL(request.url).pathname.slice('/api/data/v9.2/'.length));
      patches.push({ path: relative, body: (await request.json()) as Record<string, unknown> });
      return new HttpResponse(null, { status: 204 });
    }),
  );
}

beforeEach(() => {
  signInAsAdministrator();
  mockServer();
});
afterEach(() => sessionStorage.clear());

type User = ReturnType<typeof userEvent.setup>;

async function open() {
  render(<App initialEntries={[PATH]} />);
  expect(await screen.findByText('smtp.example.com')).toBeInTheDocument();
  expect(await screen.findByText('ada@example.com')).toBeInTheDocument();
}

const selectRow = (user: User, name: string) => user.click(screen.getByRole('radio', { hidden: true, name: `Select ${name}` }));

describe('EmailPage', () => {
  it('is in the navigation as Email', async () => {
    await open();
    expect(screen.getByRole('heading', { level: 2, name: en['nav.email'] })).toBeInTheDocument();
  });

  it('lists the profiles with their secret as set or not set, never a value', async () => {
    await open();
    const profiles = screen.getByRole('table', { name: en['email.profiles.title'] });
    const relay = within(profiles).getByRole('row', { name: /Relay/ });
    expect(within(relay).getByText(en['email.secret.set'])).toBeInTheDocument();
    const graph = within(profiles).getByRole('row', { name: /Graph/ });
    expect(within(graph).getByText(en['email.secret.notSet'])).toBeInTheDocument();
  });

  it('shows the secret of a profile as write-only in its form: set, kept when left empty, sent when typed', async () => {
    const user = userEvent.setup();
    await open();
    await selectRow(user, 'Relay');
    await user.click(await screen.findByRole('button', { name: en['email.profiles.edit'] }));
    const dialog = await screen.findByRole('dialog');
    const secret = within(dialog).getByLabelText(en['email.profile.password']);
    expect(secret).toHaveValue('');
    expect(secret).toHaveAttribute('type', 'password');
    expect(within(dialog).getByText(en['email.profile.secretSet'])).toBeInTheDocument();

    await user.click(within(dialog).getByRole('button', { name: en['email.profile.save'] }));
    await waitFor(() => expect(patches).toHaveLength(1));
    expect(patches[0].path).toBe(`emailserverprofiles(${RELAY})`);
    expect(patches[0].body).not.toHaveProperty('outgoingpassword');

    // The profile stays selected after the save.
    await user.click(await screen.findByRole('button', { name: en['email.profiles.edit'] }));
    const again = await screen.findByRole('dialog');
    await user.type(within(again).getByLabelText(en['email.profile.password']), 'n3w-secret');
    await user.click(within(again).getByRole('button', { name: en['email.profile.save'] }));
    await waitFor(() => expect(patches).toHaveLength(2));
    expect(patches[1].body.outgoingpassword).toBe('n3w-secret');
  });

  it('a new Graph profile says its client secret is not set and sends the Graph values', async () => {
    const user = userEvent.setup();
    await open();
    await user.click(screen.getByRole('button', { name: en['email.profiles.new'] }));
    const dialog = await screen.findByRole('dialog');
    await user.selectOptions(within(dialog).getByLabelText(en['email.profile.type']), 'graph');
    expect(within(dialog).getByText(en['email.profile.secretNotSet'])).toBeInTheDocument();
    await user.type(within(dialog).getByLabelText(en['email.profile.name'], { exact: false }), 'Exchange');
    await user.type(within(dialog).getByLabelText(en['email.profile.tenant'], { exact: false }), 'contoso.example.com');
    await user.type(within(dialog).getByLabelText(en['email.profile.clientId'], { exact: false }), 'app-2');
    await user.type(within(dialog).getByLabelText(en['email.profile.clientSecret']), 'cl1ent-secret');
    await user.click(within(dialog).getByRole('button', { name: en['email.profile.save'] }));

    await waitFor(() => expect(posts.filter((p) => p.path === 'emailserverprofiles')).toHaveLength(1));
    expect(posts[0].body).toMatchObject({
      name: 'Exchange', servertype: 0, outgoingauthenticationprotocol: 4, outgoingcredentialretrieval: 7, serverauthority: 0,
      exchangeonlinetenantid: 'contoso.example.com', oauthclientid: 'app-2', oauthclientsecret: 'cl1ent-secret',
    });
  });

  it('refuses a port that is not a whole number instead of saving the default port', async () => {
    const user = userEvent.setup();
    await open();
    await selectRow(user, 'Relay');
    await user.click(await screen.findByRole('button', { name: en['email.profiles.edit'] }));
    const dialog = await screen.findByRole('dialog');
    const port = within(dialog).getByLabelText(en['email.profile.port']);
    await user.clear(port);
    await user.type(port, '58x');
    await user.click(within(dialog).getByRole('button', { name: en['email.profile.save'] }));

    expect(await within(dialog).findByText(en['email.profile.portInvalid'].replace('{value}', '58x'))).toBeInTheDocument();
    expect(patches).toHaveLength(0);

    await user.clear(port);
    await user.type(port, '2525');
    await user.click(within(dialog).getByRole('button', { name: en['email.profile.save'] }));
    await waitFor(() => expect(patches).toHaveLength(1));
    expect(patches[0].body.outgoingportnumber).toBe(2525);
  });

  it('the mailbox list has Approve and Test', async () => {
    const user = userEvent.setup();
    await open();
    const mailboxes = screen.getByRole('table', { name: en['email.mailboxes.title'] });
    expect(within(mailboxes).getByText(en['email.approval.2'])).toBeInTheDocument();
    expect(within(mailboxes).getByText(en['email.status.2'])).toBeInTheDocument();
    await selectRow(user, 'ada@example.com');

    await user.click(await screen.findByRole('button', { name: en['email.mailboxes.approve'] }));
    await waitFor(() => expect(patches).toContainEqual({ path: `systemusers(${ADA})`, body: { emailrouteraccessapproval: 1 } }));

    await user.click(await screen.findByRole('button', { name: en['email.mailboxes.test'] }));
    await waitFor(() => expect(patches).toContainEqual({ path: `mailboxes(${ADA_MAILBOX})`, body: { testemailconfigurationscheduled: true } }));
  });

  it('links a mailbox to a profile: the profile, Server-Side Synchronization and outgoing email on', async () => {
    const user = userEvent.setup();
    await open();
    await selectRow(user, 'ada@example.com');

    await user.click(await screen.findByRole('button', { name: en['email.mailboxes.link'] }));
    const dialog = await screen.findByRole('dialog');
    const picker = within(dialog).getByLabelText(en['email.link.profile']);
    expect(picker).toHaveValue(RELAY); // the current profile
    await user.selectOptions(picker, GRAPH);
    await user.click(within(dialog).getByRole('button', { name: en['email.link.save'] }));

    await waitFor(() =>
      expect(patches).toContainEqual({
        path: `mailboxes(${ADA_MAILBOX})`,
        body: { 'emailserverprofile@odata.bind': `/emailserverprofiles(${GRAPH})`, outgoingemaildeliverymethod: 2, enabledforoutgoingemail: true },
      }),
    );
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('shows the sender policy as a switch that writes the environment setting', async () => {
    const user = userEvent.setup();
    await open();
    const policy = await screen.findByRole('switch', { name: en['email.policy.queueOnly'] });
    expect(policy).not.toBeChecked();

    await user.click(policy);

    await waitFor(() => expect(posts.filter((p) => p.path === 'organizationsettings')).toHaveLength(1));
    expect(posts.find((p) => p.path === 'organizationsettings')!.body).toMatchObject({
      uniquename: 'xrmverse_EmailSendFrom', value: 'queueOnly', 'settingdefinitionid@odata.bind': `/settingdefinitions(${DEFINITION})`,
    });
    await waitFor(() => expect(screen.getByRole('switch', { name: en['email.policy.queueOnly'] })).toBeChecked());

    await user.click(screen.getByRole('switch', { name: en['email.policy.queueOnly'] }));
    await waitFor(() => expect(patches).toContainEqual({ path: 'organizationsettings(os-1)', body: { value: 'userOrQueue' } }));
  });
});
