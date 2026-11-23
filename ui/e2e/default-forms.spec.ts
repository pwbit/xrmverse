import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { expect, request, test, type APIRequestContext, type Browser, type Locator, type Page } from '@playwright/test';
import { ADMIN_OBJECT_ID, mintDevToken, uniqueSuffix } from './harness';

/**
 * The default forms and views the seed writes (plan 4.1d): an app over email, queue, queue item, task, phone call, appointment
 * and contact that names no form, so it opens the seeded main forms.
 *
 * - A new email shows From, To, Cc, Bcc, Subject and Regarding in that order, Send in the command bar, and, once saved, the
 *   Attachments card above the Details card in the right column. It is sent from a queue through GreenMail
 *   (greenmail/standalone:2.1.8, a throwaway container, as in email.spec.ts) and shows Sent.
 * - A queue opens with its Items subgrid (an item in it, the subgrid's Commands menu); the item opens on its own form.
 * - The email list opens on "Open Emails" with New; the view list offers the other seeded email views.
 * - A new task, phone call and appointment open with the Details card in the right column and Subject visible.
 * - A second app with the modern look and a presentation overlay colours a field and a section of the seeded task form: the
 *   field's colour fills its control box.
 *
 * XRMD_E2E_SCREENSHOT_DIR, when set, receives the screenshots (light, 1440×900, deviceScaleFactor 2): email-compose.png,
 * email-narrow.png (800 wide), email-sent.png, queue.png, subgrid-menu.png, queueitem.png, email-views.png, task.png,
 * phonecall.png, appointment.png, field-colour.png, email-list.png.
 */

const baseURL = process.env.XRMD_E2E_BASE_URL ?? '';
const signingKey = process.env.XRMD_E2E_SIGNING_KEY ?? '';
const screenshotDir = process.env.XRMD_E2E_SCREENSHOT_DIR;
test.skip(!baseURL || !signingKey, 'No host to test: run with the global setup.');
test.use({ baseURL });

const GREENMAIL_IMAGE = 'greenmail/standalone:2.1.8';
const SYSTEM_ADMINISTRATOR = '627090ff-40a3-4053-8790-584edc5be201';
/** The seeded task form (Xrmd.Metadata.PlatformLayouts.TaskFormId), which the overlay of the second app colours. */
const TASK_FORM_ID = '5a1c0002-4a2b-4c3d-8e4f-0a1b2c3d4e04';
const sfx = uniqueSuffix();
const SOLUTION = `DefaultForms${sfx}`;
const APP_UNIQUE = `dfm_defaultforms${sfx}`;
const APP_NAME = `Default Forms ${sfx}`;
const LOOK_UNIQUE = `dfm_defaultlook${sfx}`;
const LOOK_NAME = `Default Look ${sfx}`;
const OVERLAY = `dfm_/look${sfx}/look.json`;
const OVERLAY_ID = randomUUID();
const QUEUE_NAME = `Front desk ${sfx}`;
const QUEUE_ADDRESS = `desk.${sfx}@example.com`;
const CONTACT_ADDRESS = `ada.${sfx}@example.com`;
const CONTACT_NAME = `Sample${sfx}`;
const SUBJECT = `Order 77 ${sfx}`;
const TASK_SUBJECT = `Call back about order 77 ${sfx}`;
const RECEIVED_SUBJECT = `Question about order 78 ${sfx}`;
const SMTP_USER = 'desk';
const SMTP_PASSWORD = 'secret';

const webApi = (path: string) => `/api/data/v9.2/${path}`;
const xml = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

const OVERLAY_JSON = JSON.stringify({
  version: 1,
  forms: { [`{${TASK_FORM_ID.toUpperCase()}}`]: { background: { sections: { details: 'yellow' }, fields: { subject: '#c7e8ff' } } } },
});

function subArea(entity: string, title: string): string {
  return (
    `<SubArea Id="dfm_subarea_${entity}" Entity="${entity}" Client="All,Outlook,OutlookLaptopClient,OutlookWorkstationClient,Web" AvailableOffline="true" PassParams="false" Sku="All,OnPremise,Live,SPLA">` +
    `<Titles><Title LCID="1033" Title="${title}" /></Titles></SubArea>`
  );
}

function siteMap(unique: string, name: string, areas: string): string {
  return `<AppModuleSiteMap>
      <SiteMapUniqueName>${unique}</SiteMapUniqueName>
      <SiteMap IntroducedVersion="7.0.0.0">
        <Area Id="dfm_area" ShowGroups="true" IntroducedVersion="7.0.0.0">
          <Titles><Title LCID="1033" Title="Work" /></Titles>
          <Group Id="dfm_group" IntroducedVersion="7.0.0.0" IsProfile="false">
            <Titles><Title LCID="1033" Title="Activities" /></Titles>
            ${areas}
          </Group>
        </Area>
      </SiteMap>
      <ShowHome>False</ShowHome>
      <ShowPinned>False</ShowPinned>
      <ShowRecents>False</ShowRecents>
      <EnableCollapsibleGroups>False</EnableCollapsibleGroups>
      <LocalizedNames><LocalizedName description="${name}" languagecode="1033" /></LocalizedNames>
    </AppModuleSiteMap>`;
}

function appModule(unique: string, name: string, tables: string[], settings = ''): string {
  return `<AppModule>
      <UniqueName>${unique}</UniqueName>
      <IntroducedVersion>1.0.0.0</IntroducedVersion>
      <WebResourceId>953b9fac-1e5e-e611-80d6-00155ded156f</WebResourceId>
      <OptimizedFor></OptimizedFor>
      <statecode>0</statecode>
      <statuscode>1</statuscode>
      <FormFactor>1</FormFactor>
      <ClientType>4</ClientType>
      <NavigationType>0</NavigationType>
      <AppModuleComponents>
        ${tables.map((t) => `<AppModuleComponent type="1" schemaName="${t}" />`).join('\n        ')}
        <AppModuleComponent type="62" schemaName="${unique}" />
      </AppModuleComponents>
      <AppModuleRoleMaps>
        <Role id="{${SYSTEM_ADMINISTRATOR}}" />
      </AppModuleRoleMaps>
      <LocalizedNames><LocalizedName description="${name}" languagecode="1033" /></LocalizedNames>
      <Descriptions><Description description="Seeded default forms, for the default forms E2E." languagecode="1033" /></Descriptions>
      ${settings}
    </AppModule>`;
}

/**
 * The solution: the app over the activity and queue tables (no form components: the seeded forms open), and a second app
 * over task with the modern look and an overlay that colours the seeded task form.
 */
function buildSolutionZip(): Buffer {
  const dir = mkdtempSync(join(tmpdir(), 'xrmverse-default-forms-'));
  try {
    const fileName = `/WebResources/${OVERLAY}${OVERLAY_ID.toUpperCase()}`;
    mkdirSync(dirname(join(dir, fileName)), { recursive: true });
    writeFileSync(join(dir, fileName), OVERLAY_JSON);
    writeFileSync(
      join(dir, 'solution.xml'),
      `<?xml version="1.0" encoding="utf-8"?>
<ImportExportXml version="9.2.24093.200" SolutionPackageVersion="9.2" languagecode="1033" generatedBy="CrmLive" OrganizationVersion="9.2.24093.200" OrganizationSchemaType="Standard" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <SolutionManifest>
    <UniqueName>${SOLUTION}</UniqueName>
    <LocalizedNames><LocalizedName description="${APP_NAME}" languagecode="1033" /></LocalizedNames>
    <Descriptions />
    <Version>1.0.0.0</Version>
    <Managed>0</Managed>
    <Publisher>
      <UniqueName>defaultforms</UniqueName>
      <LocalizedNames><LocalizedName description="Default Forms" languagecode="1033" /></LocalizedNames>
      <Descriptions />
      <EMailAddress xsi:nil="true"></EMailAddress>
      <SupportingWebsiteUrl xsi:nil="true"></SupportingWebsiteUrl>
      <CustomizationPrefix>dfm</CustomizationPrefix>
      <CustomizationOptionValuePrefix>27158</CustomizationOptionValuePrefix>
      <Addresses />
    </Publisher>
    <RootComponents>
      <RootComponent type="61" schemaName="${OVERLAY}" behavior="0" />
      <RootComponent type="62" schemaName="${APP_UNIQUE}" behavior="0" />
      <RootComponent type="80" schemaName="${APP_UNIQUE}" behavior="0" />
      <RootComponent type="62" schemaName="${LOOK_UNIQUE}" behavior="0" />
      <RootComponent type="80" schemaName="${LOOK_UNIQUE}" behavior="0" />
    </RootComponents>
    <MissingDependencies />
  </SolutionManifest>
</ImportExportXml>
`,
    );
    const tables = ['email', 'queue', 'queueitem', 'task', 'phonecall', 'appointment', 'contact'];
    writeFileSync(
      join(dir, 'customizations.xml'),
      `<?xml version="1.0" encoding="utf-8"?>
<ImportExportXml xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" OrganizationVersion="9.2.24093.200" OrganizationSchemaType="Standard">
  <Entities />
  <Roles />
  <Workflows />
  <FieldSecurityProfiles />
  <Templates />
  <EntityMaps />
  <EntityRelationships />
  <OrganizationSettings />
  <optionsets />
  <CustomControls />
  <SolutionPluginAssemblies />
  <SdkMessageProcessingSteps />
  <WebResources>
    <WebResource>
      <WebResourceId>{${OVERLAY_ID}}</WebResourceId>
      <Name>${xml(OVERLAY)}</Name>
      <DisplayName>look.json</DisplayName>
      <Description>Presentation overlay of the default forms E2E.</Description>
      <WebResourceType>3</WebResourceType>
      <IntroducedVersion>1.0.0.0</IntroducedVersion>
      <IsEnabledForMobileClient>0</IsEnabledForMobileClient>
      <IsAvailableForMobileOffline>0</IsAvailableForMobileOffline>
      <IsCustomizable>1</IsCustomizable>
      <CanBeDeleted>1</CanBeDeleted>
      <IsHidden>0</IsHidden>
      <FileName>${xml(fileName)}</FileName>
    </WebResource>
  </WebResources>
  <AppModuleSiteMaps>
    ${siteMap(APP_UNIQUE, APP_NAME, [subArea('email', 'Emails'), subArea('queue', 'Queues'), subArea('queueitem', 'Queue Items'), subArea('task', 'Tasks'), subArea('phonecall', 'Phone Calls'), subArea('appointment', 'Appointments'), subArea('contact', 'Contacts')].join(''))}
    ${siteMap(LOOK_UNIQUE, LOOK_NAME, subArea('task', 'Tasks'))}
  </AppModuleSiteMaps>
  <AppModules>
    ${appModule(APP_UNIQUE, APP_NAME, tables)}
    ${appModule(
      LOOK_UNIQUE,
      LOOK_NAME,
      ['task'],
      `<appsettings>
        <appsetting settingdefinitionid.uniquename="xrmverse_Renderer"><iscustomizable>1</iscustomizable><value>modern</value></appsetting>
        <appsetting settingdefinitionid.uniquename="xrmverse_Presentation"><iscustomizable>1</iscustomizable><value>${xml(OVERLAY)}</value></appsetting>
      </appsettings>`,
    )}
  </AppModules>
  <Languages>
    <Language>1033</Language>
  </Languages>
</ImportExportXml>
`,
    );
    writeFileSync(
      join(dir, '[Content_Types].xml'),
      `<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/octet-stream" /><Override PartName="${xml(fileName)}" ContentType="application/octet-stream" /></Types>`,
    );
    const zip = join(dir, `${SOLUTION}.zip`);
    execFileSync('zip', ['-r', '-X', '-q', zip, 'solution.xml', 'customizations.xml', '[Content_Types].xml', 'WebResources'], { cwd: dir });
    return readFileSync(zip);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

async function json<T>(api: APIRequestContext, path: string): Promise<T> {
  const res = await api.get(webApi(path));
  expect(res.status(), `GET ${path}: ${await res.text()}`).toBe(200);
  return (await res.json()) as T;
}

async function create(api: APIRequestContext, set: string, data: Record<string, unknown>): Promise<string> {
  const res = await api.post(webApi(set), { data });
  expect(res.status(), `POST ${set}: ${await res.text()}`).toBe(204);
  return /\(([0-9a-f-]{36})\)$/.exec(res.headers()['odata-entityid'] ?? '')![1];
}

async function patch(api: APIRequestContext, path: string, data: Record<string, unknown>): Promise<void> {
  const res = await api.patch(webApi(path), { data });
  expect(res.status(), `PATCH ${path}: ${await res.text()}`).toBe(204);
}

/** The navigation property of `mailbox.emailserverprofile`, read from the relationships (every page of them). */
async function mailboxProfileNavigation(api: APIRequestContext): Promise<string> {
  type Rel = { ReferencingEntity?: string | null; ReferencingAttribute?: string | null; ReferencingEntityNavigationPropertyName?: string | null };
  let path: string | undefined = webApi('RelationshipDefinitions?$select=ReferencingEntity,ReferencingAttribute,ReferencingEntityNavigationPropertyName');
  while (path) {
    const res = await api.get(path);
    expect(res.status(), `GET ${path}: ${await res.text()}`).toBe(200);
    const page = (await res.json()) as { value: Rel[]; '@odata.nextLink'?: string };
    const found = page.value.find((r) => r.ReferencingEntity === 'mailbox' && r.ReferencingAttribute === 'emailserverprofile');
    if (found?.ReferencingEntityNavigationPropertyName) return found.ReferencingEntityNavigationPropertyName;
    path = page['@odata.nextLink'];
  }
  throw new Error('No relationship binds mailbox.emailserverprofile.');
}

/** Polls `read` until `done` holds, or fails with the last value after `ms`. */
async function until<T>(read: () => Promise<T>, done: (v: T) => boolean, ms: number, what: string): Promise<T> {
  const deadline = Date.now() + ms;
  let last = await read();
  while (!done(last)) {
    if (Date.now() > deadline) throw new Error(`${what}: still ${JSON.stringify(last)} after ${ms / 1000} s.`);
    await new Promise((r) => setTimeout(r, 1000));
    last = await read();
  }
  return last;
}

async function shot(page: Page, name: string): Promise<void> {
  if (!screenshotDir) return;
  mkdirSync(screenshotDir, { recursive: true });
  await page.waitForTimeout(400);
  await page.screenshot({ path: join(screenshotDir, name) });
}

/** A fresh light browser context (deviceScaleFactor 2, for the screenshots) signed in as the administrator. */
async function signedIn(browser: Browser, path: string, width = 1440): Promise<Page> {
  const context = await browser.newContext({ baseURL, viewport: { width, height: 900 }, deviceScaleFactor: 2, colorScheme: 'light' });
  const page = await context.newPage();
  await page.goto(path);
  await page.getByRole('textbox', { name: 'Dev signing key (Auth:DevSigningKey)', exact: true }).fill(signingKey);
  await page.getByRole('textbox', { name: 'User object id (oid)', exact: true }).fill(ADMIN_OBJECT_ID);
  await page.getByRole('textbox', { name: 'Display name', exact: true }).fill('Administrator');
  await page.getByRole('button', { name: 'Sign in' }).click();
  return page;
}

let admin: APIRequestContext;
let greenMail = '';
let greenMailApi = '';
let appId = '';
let lookAppId = '';
let contactId = '';
let queueId = '';
let profileId = '';
let taskId = '';
let queueItemId = '';
const emails: string[] = [];

test.beforeAll(async () => {
  test.setTimeout(420_000);
  greenMail = `xrmverse-e2e-greenmail-df-${sfx}`;
  execFileSync('docker', [
    'run', '-d', '--name', greenMail, '-p', '127.0.0.1::3025', '-p', '127.0.0.1::8080',
    '-e', `GREENMAIL_OPTS=-Dgreenmail.setup.test.smtp -Dgreenmail.setup.test.imap -Dgreenmail.hostname=0.0.0.0 -Dgreenmail.auth.disabled=false -Dgreenmail.users=${SMTP_USER}:${SMTP_PASSWORD}@example.com`,
    GREENMAIL_IMAGE,
  ]);
  const port = (p: number) => execFileSync('docker', ['port', greenMail, `${p}/tcp`], { encoding: 'utf8' }).trim().split('\n')[0].split(':').pop()!;
  await until(async () => execFileSync('docker', ['logs', greenMail], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }), (l) => l.includes('Starting GreenMail API server'), 60_000, 'GreenMail start');
  const smtpPort = Number(port(3025));
  greenMailApi = `http://127.0.0.1:${port(8080)}`;

  const token = mintDevToken(signingKey, `${baseURL}/`, ADMIN_OBJECT_ID, 'Administrator');
  admin = await request.newContext({ baseURL, extraHTTPHeaders: { Authorization: `Bearer ${token}`, Accept: 'application/json', 'OData-Version': '4.0' } });

  const imported = await admin.post(webApi('ImportSolution'), {
    data: { CustomizationFile: buildSolutionZip().toString('base64'), ImportJobId: randomUUID(), OverwriteUnmanagedCustomizations: true, PublishWorkflows: false },
    timeout: 300_000,
  });
  expect(imported.status(), await imported.text()).toBe(204);
  const apps = await json<{ value: { appmoduleid: string; uniquename: string }[] }>(
    admin,
    `appmodules?$select=appmoduleid,uniquename&$filter=uniquename eq '${APP_UNIQUE}' or uniquename eq '${LOOK_UNIQUE}'`,
  );
  appId = apps.value.find((a) => a.uniquename === APP_UNIQUE)!.appmoduleid;
  lookAppId = apps.value.find((a) => a.uniquename === LOOK_UNIQUE)!.appmoduleid;

  contactId = await create(admin, 'contacts', { firstname: 'Ada', lastname: CONTACT_NAME, emailaddress1: CONTACT_ADDRESS });

  // The queue that sends: its mailbox approved, on the GreenMail profile, enabled, and tested by the server.
  profileId = await create(admin, 'emailserverprofiles', {
    name: `GreenMail DF ${sfx}`, servertype: 4, outgoingserverlocation: '127.0.0.1', outgoingportnumber: smtpPort, outgoingusessl: false,
    outgoingauthenticationprotocol: 3, outgoingcredentialretrieval: 1, outgoingusername: SMTP_USER, outgoingpassword: SMTP_PASSWORD,
  });
  queueId = await create(admin, 'queues', { name: QUEUE_NAME, emailaddress: QUEUE_ADDRESS, queueviewtype: 0, queuetypecode: 1, description: 'Orders and returns.' });
  await patch(admin, `queues(${queueId})`, { emailrouteraccessapproval: 1 });
  const mailbox = (await json<{ _defaultmailbox_value: string }>(admin, `queues(${queueId})?$select=_defaultmailbox_value`))._defaultmailbox_value;
  const nav = await mailboxProfileNavigation(admin);
  await patch(admin, `mailboxes(${mailbox})`, {
    [`${nav}@odata.bind`]: `/emailserverprofiles(${profileId})`, outgoingemaildeliverymethod: 2, enabledforoutgoingemail: true,
  });
  await patch(admin, `mailboxes(${mailbox})`, { testemailconfigurationscheduled: true });
  const tested = await until(
    () => json<{ outgoingemailstatus: number | null; testemailconfigurationscheduled: boolean; lastsyncerror?: string | null }>(admin, `mailboxes(${mailbox})?$select=outgoingemailstatus,testemailconfigurationscheduled,lastsyncerror`),
    (m) => !m.testemailconfigurationscheduled && m.outgoingemailstatus !== null && m.outgoingemailstatus !== 0,
    120_000,
    'The mailbox test',
  );
  expect(tested.outgoingemailstatus, `The mailbox test failed: ${tested.lastsyncerror}`).toBe(1);

  // A task in the queue: the queue form's Items subgrid shows it.
  taskId = await create(admin, 'tasks', { subject: TASK_SUBJECT, description: 'The customer asked for a call.', prioritycode: 2 });
  const added = await admin.post(webApi(`queues(${queueId})/Microsoft.Dynamics.CRM.AddToQueue`), {
    data: { Target: { '@odata.type': 'Microsoft.Dynamics.CRM.task', activityid: taskId } },
  });
  expect(added.status(), await added.text()).toBe(200);
  queueItemId = ((await added.json()) as { QueueItemId: string }).QueueItemId;
});

test.afterAll(async () => {
  if (admin) {
    for (const id of emails) await admin.delete(webApi(`emails(${id})`));
    if (queueItemId) await admin.delete(webApi(`queueitems(${queueItemId})`));
    if (taskId) await admin.delete(webApi(`tasks(${taskId})`));
    if (contactId) await admin.delete(webApi(`contacts(${contactId})`));
    if (queueId) await admin.delete(webApi(`queues(${queueId})`));
    if (profileId) await admin.delete(webApi(`emailserverprofiles(${profileId})`));
    await admin.dispose();
  }
  if (greenMail) execFileSync('docker', ['rm', '-f', greenMail], { stdio: 'ignore' });
});

const recordUrl = (etn: string, id?: string, app = appId) => `/main.aspx?appid=${app}&pagetype=entityrecord&etn=${etn}${id ? `&id=${id}` : ''}`;
const listUrl = (etn: string) => `/main.aspx?appid=${appId}&pagetype=entitylist&etn=${etn}`;
const columns = (page: Page) => page.locator('[data-form-column]');
/** The top of an element, for order checks. */
const top = async (locator: Locator) => (await locator.boundingBox())!.y;

/** The Details card in the right column, with Subject visible: the shared shape of the seeded activity forms. */
async function expectActivityForm(page: Page): Promise<void> {
  await expect(page.getByRole('heading', { level: 1, name: /^New / })).toBeVisible();
  await expect(columns(page)).toHaveCount(2);
  await expect(columns(page).nth(1).getByText('Details', { exact: true })).toBeVisible();
  await expect(columns(page).nth(1).getByRole('textbox', { name: 'Owner', exact: true })).toBeVisible();
  await expect(page.getByRole('textbox', { name: /^Subject/ })).toBeVisible();
  const [left, right] = [(await columns(page).nth(0).boundingBox())!, (await columns(page).nth(1).boundingBox())!];
  expect(right.x).toBeGreaterThan(left.x + left.width - 1);
}

test('a new email opens on the seeded form, saves, shows Attachments then Details on the right, and sends from a queue', async ({ browser }) => {
  test.setTimeout(300_000);
  const page = await signedIn(browser, recordUrl('email'));
  try {
    await expect(page.getByRole('heading', { level: 1, name: /^New / })).toBeVisible();
    // The message fields, in order, in the left column.
    const left = columns(page).nth(0);
    const labels = ['From', 'To', 'Cc', 'Bcc', 'Subject', 'Regarding'];
    const tops: number[] = [];
    for (const label of labels) {
      const cell = left.getByTestId('form-cell').filter({ hasText: new RegExp(`^${label}`) }).first();
      await expect(cell, label).toBeVisible();
      tops.push(await top(cell));
    }
    expect(tops, `the order of ${labels.join(', ')}`).toEqual([...tops].sort((a, b) => a - b));
    expect(new Set(tops).size).toBe(labels.length);
    await expect(columns(page).nth(1).getByText('Details', { exact: true })).toBeVisible();

    await page.getByRole('button', { name: 'Choose From', exact: true }).click();
    await page.getByRole('listbox', { name: 'From' }).getByRole('option', { name: new RegExp(QUEUE_NAME) }).click();
    const to = page.getByRole('combobox', { name: 'To', exact: true });
    await to.fill(CONTACT_ADDRESS);
    await to.press('Enter');
    await expect(page.getByRole('list', { name: 'To', exact: true })).toContainText(CONTACT_NAME);
    await page.getByRole('textbox', { name: /^Subject/ }).fill(SUBJECT);
    await page.getByRole('textbox', { name: /^Description/ }).fill('Hello,\n\nyour order 77 left our warehouse today; the invoice is attached.\n\nKind regards,\nFront desk');
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(page).toHaveURL(/[?&]id=[0-9a-f-]{36}/i);
    const emailId = /[?&]id=\{?([0-9a-f-]{36})/i.exec(page.url())![1].toLowerCase();
    emails.push(emailId);
    // Send is in the command bar of a saved draft.
    await expect(page.getByRole('button', { name: 'Send', exact: true })).toBeVisible();

    // Saved: the Attachments card is at the top of the right column, above Details.
    await page.getByLabel('Attach files').setInputFiles([{ name: 'invoice-77.txt', mimeType: 'text/plain', buffer: Buffer.from('Invoice 77: 2 items.') }]);
    const attachments = page.getByRole('region', { name: 'Attachments (1)' });
    await expect(attachments).toBeVisible({ timeout: 30_000 });
    const right = columns(page).nth(1);
    await expect(right.getByRole('region', { name: 'Attachments (1)' })).toBeVisible();
    expect(await top(attachments)).toBeLessThan(await top(right.getByText('Details', { exact: true })));
    await shot(page, 'email-compose.png');

    // Narrow (800 wide): the same email.
    const narrow = await signedIn(browser, recordUrl('email', emailId), 800);
    try {
      await expect(narrow.getByRole('heading', { level: 1, name: SUBJECT })).toBeVisible();
      await expect(narrow.getByRole('region', { name: 'Attachments (1)' })).toBeVisible();
      const width = await narrow.evaluate(() => document.documentElement.scrollWidth);
      expect(width, 'no horizontal page scroll at 800 wide').toBeLessThanOrEqual(800);
      await shot(narrow, 'email-narrow.png');
    } finally {
      await narrow.context().close();
    }

    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText(/^Sent/).first()).toBeVisible({ timeout: 120_000 });
    await expect(page.getByRole('button', { name: 'Send', exact: true })).toHaveCount(0);
    await shot(page, 'email-sent.png');
    const sent = await json<{ statecode: number; statuscode: number }>(admin, `emails(${emailId})?$select=statecode,statuscode`);
    expect(sent).toMatchObject({ statecode: 1, statuscode: 3 });
    await until(
      async () => (await (await fetch(`${greenMailApi}/api/user/${CONTACT_ADDRESS}/messages/INBOX`)).json()) as { subject: string }[],
      (m) => Array.isArray(m) && m.some((x) => x.subject === SUBJECT),
      30_000,
      'The GreenMail inbox',
    );
  } finally {
    await page.context().close();
  }
});

test('a queue opens with its Items subgrid and Commands menu; the queue item opens on its own form', async ({ browser }) => {
  const page = await signedIn(browser, recordUrl('queue', queueId));
  try {
    await expect(page.getByRole('heading', { level: 1, name: QUEUE_NAME })).toBeVisible();
    await expect(page.getByText('Items', { exact: true }).first()).toBeVisible();
    await expect(page.getByRole('row').filter({ hasText: TASK_SUBJECT }).first()).toBeVisible();
    await shot(page, 'queue.png');

    // The administrator holds the queue item privileges: the Items subgrid always has its Commands menu.
    const menu = page.getByRole('button', { name: 'Queue Item commands', exact: true });
    await expect(menu).toBeVisible();
    await menu.click();
    await expect(page.getByRole('menuitem', { name: 'New Queue Item', exact: true })).toBeVisible();
    await expect(page.getByRole('menuitem', { name: 'Add Existing Queue Item', exact: true })).toBeVisible();
    await shot(page, 'subgrid-menu.png');
    await page.keyboard.press('Escape');
  } finally {
    await page.context().close();
  }

  const item = await signedIn(browser, recordUrl('queueitem', queueItemId));
  try {
    await expect(item.getByRole('heading', { level: 1, name: TASK_SUBJECT })).toBeVisible();
    for (const label of ['Title', 'Item', 'Queue', 'Worked by', 'Entered queue']) {
      await expect(item.getByTestId('form-cell').filter({ hasText: new RegExp(`^${label}`) }).first(), label).toBeVisible();
    }
    await shot(item, 'queueitem.png');
  } finally {
    await item.context().close();
  }
});

test('the email list opens on Open Emails and offers the other seeded views', async ({ browser }) => {
  // A received email of this test's own, so the check does not depend on the email the send test sent.
  const receivedId = await create(admin, 'emails', { subject: RECEIVED_SUBJECT, directioncode: false });
  emails.push(receivedId);
  await patch(admin, `emails(${receivedId})`, { statecode: 1, statuscode: 4 });
  const page = await signedIn(browser, listUrl('email'));
  try {
    const picker = page.getByRole('button', { name: /Open Emails/ }).first();
    await expect(picker).toBeVisible();
    await expect(page.getByText(/doesn't contain attribute/)).toHaveCount(0);
    // The administrator holds the shared activity privileges: the list offers New.
    await expect(page.getByRole('button', { name: 'New', exact: true }).or(page.getByRole('menuitem', { name: 'New', exact: true })).first()).toBeVisible();
    await shot(page, 'email-list.png');
    await picker.click();
    for (const view of ['All Emails', 'Sent Emails', 'Received Emails']) await expect(page.getByRole('menuitemradio', { name: view }).or(page.getByRole('menuitem', { name: view })).or(page.getByRole('option', { name: view })).first()).toBeVisible();
    await shot(page, 'email-views.png');
    // The views read their rows (the activity tables are keyed on activityid): Received Emails lists this test's email.
    await page.getByRole('menuitemradio', { name: 'Received Emails' }).or(page.getByRole('menuitem', { name: 'Received Emails' })).first().click();
    await expect(page.getByText(RECEIVED_SUBJECT).first()).toBeVisible();
    await expect(page.getByText(/doesn't contain attribute/)).toHaveCount(0);
  } finally {
    await page.context().close();
  }
});

for (const [etn, file] of [
  ['task', 'task.png'],
  ['phonecall', 'phonecall.png'],
  ['appointment', 'appointment.png'],
] as const) {
  test(`a new ${etn} opens on the seeded form: Details on the right, Subject visible`, async ({ browser }) => {
    const page = await signedIn(browser, recordUrl(etn));
    try {
      await expectActivityForm(page);
      await shot(page, file);
    } finally {
      await page.context().close();
    }
  });
}

test('the modern look colours a field box and a section of the seeded task form', async ({ browser }) => {
  const page = await signedIn(browser, recordUrl('task', taskId, lookAppId));
  try {
    await expect(page.getByRole('heading', { level: 1, name: TASK_SUBJECT })).toBeVisible();
    const subject = page.getByTestId('form-cell').filter({ has: page.getByRole('textbox', { name: /^Subject/ }) });
    await expect(subject).toHaveAttribute('data-field-fill', 'true');
    const box = subject.locator('.fui-Input').first();
    expect(await box.evaluate((el) => getComputedStyle(el).backgroundColor)).toBe('rgb(199, 232, 255)');
    expect(await subject.evaluate((el) => getComputedStyle(el).backgroundColor)).toBe('rgba(0, 0, 0, 0)');
    await expect(page.locator('[role="group"][data-background="true"]')).toHaveCount(1);
    await shot(page, 'field-colour.png');
  } finally {
    await page.context().close();
  }
});
