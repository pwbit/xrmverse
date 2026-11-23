import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, request, test, type APIRequestContext, type Browser, type Page } from '@playwright/test';
import { ADMIN_OBJECT_ID, mintDevToken, uniqueSuffix } from './harness';

/**
 * Compose, send and receive an email in a model-driven app (plan 4.1b, task 9), against a real SMTP server: GreenMail
 * (greenmail/standalone:2.1.8, a throwaway container this spec starts and removes; SMTP without STARTTLS, so the host runs
 * with Mail:AllowInsecureSmtp=true, which e2e/global-setup.ts sets).
 *
 * It runs on both engines:
 *  - SQL Server: the main run (playwright.config.ts), on the global setup's host.
 *  - PostgreSQL: playwright.postgres.config.ts, against a Development-mode host on PostgreSQL started with
 *    Mail__AllowInsecureSmtp=true (XRMD_E2E_PG_BASE_URL, XRMD_E2E_PG_SIGNING_KEY, XRMD_E2E_PG_ADMIN_OBJECT_ID; see
 *    postgres-smoke.spec.ts).
 * XRMD_E2E_SCREENSHOT_DIR, when set, receives ui-compose.png, ui-lookup.png, ui-preview.png, ui-sent.png and ui-failed.png
 * (deviceScaleFactor 2).
 *
 * Setup through the Web API: an app over email and contact with a main form of email (From, To, Cc, Subject, Description); an
 * SMTP server profile on GreenMail; a queue whose mailbox is approved, on that profile, and tested by the server's mailbox test
 * job; a contact. Then in Chromium: a new email from the queue (chosen in From) to the contact (typed address, resolved) with an
 * unresolved Cc, saved, two attachments uploaded, the saved Cc emptied (cleared by the next save), the PDF previewed in the browser's
 * viewer, the lookup dialog of To, Send; the banner shows Pending Send then Sent, the Web API
 * shows status Sent with a Message-ID, and GreenMail received the message with its attachments. A second email, from the
 * administrator (who has no mailbox ready to send), ends Failed, and its form shows the job's reason.
 */

const pg = !!process.env.XRMD_E2E_PG_BASE_URL;
const baseURL = (pg ? process.env.XRMD_E2E_PG_BASE_URL : process.env.XRMD_E2E_BASE_URL) ?? '';
const signingKey = (pg ? process.env.XRMD_E2E_PG_SIGNING_KEY : process.env.XRMD_E2E_SIGNING_KEY) ?? '';
const adminObjectId = (pg ? process.env.XRMD_E2E_PG_ADMIN_OBJECT_ID : undefined) ?? ADMIN_OBJECT_ID;
const screenshotDir = process.env.XRMD_E2E_SCREENSHOT_DIR;
test.skip(!baseURL || !signingKey, 'No host to test: run with the global setup (SQL Server) or XRMD_E2E_PG_* (PostgreSQL).');
test.use({ baseURL });

const GREENMAIL_IMAGE = 'greenmail/standalone:2.1.8';
const SYSTEM_ADMINISTRATOR = '627090ff-40a3-4053-8790-584edc5be201';
const sfx = uniqueSuffix();
const SOLUTION = `EmailE2E${sfx}`;
const APP_UNIQUE = `eml_emaile2e${sfx}`;
const APP_NAME = `Email E2E ${sfx}`;
const FORM_ID = randomUUID();
const QUEUE_NAME = `Support ${sfx}`;
const QUEUE_ADDRESS = `support.${sfx}@example.com`;
const CONTACT_ADDRESS = `grace.${sfx}@example.com`;
/** The contact's full name ends with its last name, whatever the organization's name format. */
const CONTACT_NAME = `Example${sfx}`;
const SUBJECT = `Invoice 1042 ${sfx}`;
const SMTP_USER = 'support';
const SMTP_PASSWORD = 'secret';

const webApi = (path: string) => `/api/data/v9.2/${path}`;
const guid = () => `{${randomUUID()}}`;

function cell(attribute: string, label: string, classid: string): string {
  return (
    `<row><cell id="${guid()}" showlabel="true"><labels><label description="${label}" languagecode="1033"/></labels>` +
    `<control id="${attribute}" classid="${classid}" datafieldname="${attribute}"/></cell></row>`
  );
}

const PARTY_LIST = '{CBFB742C-14E7-4a17-96BB-1A13F7F64AA2}';
const TEXT = '{4273EDBD-AC1D-40d3-9FB2-095C621B552D}';
const MEMO = '{E0DECE4B-6FC8-4a8f-A065-082708572369}';
const FORM_XML =
  `<form><tabs><tab name="general" id="${guid()}" expanded="true" showlabel="false" verticallayout="true">` +
  '<labels><label description="Email" languagecode="1033"/></labels><columns><column width="100%"><sections>' +
  `<section name="message" id="${guid()}" showlabel="false" showbar="false" columns="1"><labels><label description="Message" languagecode="1033"/></labels><rows>` +
  cell('from', 'From', PARTY_LIST) +
  cell('to', 'To', PARTY_LIST) +
  cell('cc', 'Cc', PARTY_LIST) +
  cell('subject', 'Subject', TEXT) +
  cell('description', 'Description', MEMO) +
  '</rows></section></sections></column></columns></tab></tabs></form>';

/** The solution: an app over email and contact, whose only email form is FORM_ID, for System Administrator. */
function buildSolutionZip(): Buffer {
  const dir = mkdtempSync(join(tmpdir(), 'xrmverse-email-e2e-'));
  try {
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
      <UniqueName>emaile2e</UniqueName>
      <LocalizedNames><LocalizedName description="Email E2E" languagecode="1033" /></LocalizedNames>
      <Descriptions />
      <EMailAddress xsi:nil="true"></EMailAddress>
      <SupportingWebsiteUrl xsi:nil="true"></SupportingWebsiteUrl>
      <CustomizationPrefix>eml</CustomizationPrefix>
      <CustomizationOptionValuePrefix>27157</CustomizationOptionValuePrefix>
      <Addresses />
    </Publisher>
    <RootComponents>
      <RootComponent type="62" schemaName="${APP_UNIQUE}" behavior="0" />
      <RootComponent type="80" schemaName="${APP_UNIQUE}" behavior="0" />
    </RootComponents>
    <MissingDependencies />
  </SolutionManifest>
</ImportExportXml>
`,
    );
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
  <AppModuleSiteMaps>
    <AppModuleSiteMap>
      <SiteMapUniqueName>${APP_UNIQUE}</SiteMapUniqueName>
      <SiteMap IntroducedVersion="7.0.0.0">
        <Area Id="eml_area" ShowGroups="true" IntroducedVersion="7.0.0.0">
          <Titles><Title LCID="1033" Title="Mail" /></Titles>
          <Group Id="eml_group" IntroducedVersion="7.0.0.0" IsProfile="false">
            <Titles><Title LCID="1033" Title="Activities" /></Titles>
            <SubArea Id="eml_subarea_email" Entity="email" Client="All,Outlook,OutlookLaptopClient,OutlookWorkstationClient,Web" AvailableOffline="true" PassParams="false" Sku="All,OnPremise,Live,SPLA">
              <Titles><Title LCID="1033" Title="Emails" /></Titles>
            </SubArea>
            <SubArea Id="eml_subarea_contact" Entity="contact" Client="All,Outlook,OutlookLaptopClient,OutlookWorkstationClient,Web" AvailableOffline="true" PassParams="false" Sku="All,OnPremise,Live,SPLA">
              <Titles><Title LCID="1033" Title="Contacts" /></Titles>
            </SubArea>
          </Group>
        </Area>
      </SiteMap>
      <ShowHome>False</ShowHome>
      <ShowPinned>False</ShowPinned>
      <ShowRecents>False</ShowRecents>
      <EnableCollapsibleGroups>False</EnableCollapsibleGroups>
      <LocalizedNames><LocalizedName description="${APP_NAME}" languagecode="1033" /></LocalizedNames>
    </AppModuleSiteMap>
  </AppModuleSiteMaps>
  <AppModules>
    <AppModule>
      <UniqueName>${APP_UNIQUE}</UniqueName>
      <IntroducedVersion>1.0.0.0</IntroducedVersion>
      <WebResourceId>953b9fac-1e5e-e611-80d6-00155ded156f</WebResourceId>
      <OptimizedFor></OptimizedFor>
      <statecode>0</statecode>
      <statuscode>1</statuscode>
      <FormFactor>1</FormFactor>
      <ClientType>4</ClientType>
      <NavigationType>0</NavigationType>
      <AppModuleComponents>
        <AppModuleComponent type="1" schemaName="email" />
        <AppModuleComponent type="1" schemaName="contact" />
        <AppModuleComponent type="60" id="{${FORM_ID}}" />
        <AppModuleComponent type="62" schemaName="${APP_UNIQUE}" />
      </AppModuleComponents>
      <AppModuleRoleMaps>
        <Role id="{${SYSTEM_ADMINISTRATOR}}" />
      </AppModuleRoleMaps>
      <LocalizedNames><LocalizedName description="${APP_NAME}" languagecode="1033" /></LocalizedNames>
      <Descriptions><Description description="Emails, for the email E2E." languagecode="1033" /></Descriptions>
    </AppModule>
  </AppModules>
  <Languages>
    <Language>1033</Language>
  </Languages>
</ImportExportXml>
`,
    );
    writeFileSync(
      join(dir, '[Content_Types].xml'),
      `<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/octet-stream" /></Types>`,
    );
    const zip = join(dir, `${SOLUTION}.zip`);
    execFileSync('zip', ['-X', '-q', zip, 'solution.xml', 'customizations.xml', '[Content_Types].xml'], { cwd: dir });
    return readFileSync(zip);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

/** A one-page PDF with a line of text, small enough to inline. */
function samplePdf(): Buffer {
  const stream = 'BT /F1 24 Tf 72 720 Td (Invoice 1042) Tj ET';
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>',
    `<< /Length ${stream.length} >>\nstream\n${stream}\nendstream`,
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
  ];
  let out = '%PDF-1.4\n';
  const offsets: number[] = [];
  objects.forEach((o, i) => {
    offsets.push(out.length);
    out += `${i + 1} 0 obj\n${o}\nendobj\n`;
  });
  const xref = out.length;
  out += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n${offsets.map((o) => `${String(o).padStart(10, '0')} 00000 n \n`).join('')}`;
  out += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(out, 'latin1');
}

async function json<T>(api: APIRequestContext, path: string): Promise<T> {
  const res = await api.get(webApi(path));
  expect(res.status(), `GET ${path}: ${await res.text()}`).toBe(200);
  return (await res.json()) as T;
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

async function create(api: APIRequestContext, set: string, data: Record<string, unknown>): Promise<string> {
  const res = await api.post(webApi(set), { data });
  expect(res.status(), `POST ${set}: ${await res.text()}`).toBe(204);
  return /\(([0-9a-f-]{36})\)$/.exec(res.headers()['odata-entityid'] ?? '')![1];
}

async function patch(api: APIRequestContext, path: string, data: Record<string, unknown>): Promise<void> {
  const res = await api.patch(webApi(path), { data });
  expect(res.status(), `PATCH ${path}: ${await res.text()}`).toBe(204);
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
  await page.screenshot({ path: join(screenshotDir, name) });
}

/** A fresh browser context (deviceScaleFactor 2, for the screenshots) signed in as the administrator. */
async function signedIn(browser: Browser, path: string): Promise<Page> {
  const context = await browser.newContext({ baseURL, viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2 });
  const page = await context.newPage();
  await page.goto(path);
  await page.getByRole('textbox', { name: 'Dev signing key (Auth:DevSigningKey)', exact: true }).fill(signingKey);
  await page.getByRole('textbox', { name: 'User object id (oid)', exact: true }).fill(adminObjectId);
  await page.getByRole('textbox', { name: 'Display name', exact: true }).fill('Administrator');
  await page.getByRole('button', { name: 'Sign in' }).click();
  return page;
}

let admin: APIRequestContext;
let greenMail = '';
let greenMailApi = '';
let appId = '';
let adminUserId = '';
let contactId = '';
let queueId = '';
let profileId = '';
const emails: string[] = [];

test.beforeAll(async () => {
  test.setTimeout(420_000);
  // GreenMail: SMTP with authentication (any password of a configured user), its REST API to read what arrived.
  greenMail = `xrmverse-e2e-greenmail-${sfx}`;
  execFileSync('docker', [
    'run', '-d', '--name', greenMail, '-p', '127.0.0.1::3025', '-p', '127.0.0.1::8080',
    '-e', `GREENMAIL_OPTS=-Dgreenmail.setup.test.smtp -Dgreenmail.setup.test.imap -Dgreenmail.hostname=0.0.0.0 -Dgreenmail.auth.disabled=false -Dgreenmail.users=${SMTP_USER}:${SMTP_PASSWORD}@example.com`,
    GREENMAIL_IMAGE,
  ]);
  const port = (p: number) => execFileSync('docker', ['port', greenMail, `${p}/tcp`], { encoding: 'utf8' }).trim().split('\n')[0].split(':').pop()!;
  await until(async () => execFileSync('docker', ['logs', greenMail], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }), (l) => l.includes('Starting GreenMail API server'), 60_000, 'GreenMail start');
  const smtpPort = Number(port(3025));
  greenMailApi = `http://127.0.0.1:${port(8080)}`;

  const token = mintDevToken(signingKey, `${baseURL}/`, adminObjectId, 'Administrator');
  admin = await request.newContext({ baseURL, extraHTTPHeaders: { Authorization: `Bearer ${token}`, Accept: 'application/json', 'OData-Version': '4.0' } });
  adminUserId = (await json<{ UserId: string }>(admin, 'WhoAmI')).UserId;

  // The app's email form first: the app names it, so it is the only email form the app opens.
  await create(admin, 'systemforms', { systemformid: FORM_ID, name: `Email E2E ${sfx}`, objecttypecode: 'email', type: 2, formxml: FORM_XML });
  const imported = await admin.post(webApi('ImportSolution'), {
    data: { CustomizationFile: buildSolutionZip().toString('base64'), ImportJobId: randomUUID(), OverwriteUnmanagedCustomizations: true, PublishWorkflows: false },
    timeout: 300_000,
  });
  expect(imported.status(), await imported.text()).toBe(204);
  const published = await admin.post(webApi('PublishXml'), { data: { ParameterXml: '<importexportxml><entities><entity>email</entity></entities></importexportxml>' } });
  expect(published.status(), await published.text()).toBe(204);
  appId = (await json<{ value: { appmoduleid: string }[] }>(admin, `appmodules?$select=appmoduleid&$filter=uniquename eq '${APP_UNIQUE}'`)).value[0].appmoduleid;

  contactId = await create(admin, 'contacts', { firstname: 'Grace', lastname: `Example${sfx}`, emailaddress1: CONTACT_ADDRESS });

  // The queue that sends: its mailbox approved, on the GreenMail profile, enabled, and tested by the server.
  profileId = await create(admin, 'emailserverprofiles', {
    name: `GreenMail ${sfx}`, servertype: 4, outgoingserverlocation: '127.0.0.1', outgoingportnumber: smtpPort, outgoingusessl: false,
    outgoingauthenticationprotocol: 3, outgoingcredentialretrieval: 1, outgoingusername: SMTP_USER, outgoingpassword: SMTP_PASSWORD,
  });
  queueId = await create(admin, 'queues', { name: QUEUE_NAME, emailaddress: QUEUE_ADDRESS, queueviewtype: 0, queuetypecode: 1 });
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
});

test.afterAll(async () => {
  if (admin) {
    for (const id of emails) await admin.delete(webApi(`emails(${id})`));
    if (contactId) await admin.delete(webApi(`contacts(${contactId})`));
    if (queueId) await admin.delete(webApi(`queues(${queueId})`));
    if (profileId) await admin.delete(webApi(`emailserverprofiles(${profileId})`));
    await admin.delete(webApi(`systemforms(${FORM_ID})`));
    await admin.dispose();
  }
  if (greenMail) execFileSync('docker', ['rm', '-f', greenMail], { stdio: 'ignore' });
});

const recordUrl = (id?: string) => `/main.aspx?appid=${appId}&pagetype=entityrecord&etn=email${id ? `&id=${id}` : ''}`;

test('compose, attach, preview and send an email from a queue; GreenMail receives it', async ({ browser }) => {
  test.setTimeout(300_000);
  const page = await signedIn(browser, recordUrl());
  const failures: string[] = [];
  page.on('response', (res) => {
    if (res.url().includes('/api/') && res.status() >= 500) failures.push(`${res.status()} ${res.request().method()} ${res.url()}`);
  });
  try {
    // From: the queue, chosen from the senders (the user and the readable queues).
    await page.getByRole('button', { name: 'Choose From', exact: true }).click();
    await page.getByRole('listbox', { name: 'From' }).getByRole('option', { name: new RegExp(QUEUE_NAME) }).click();
    await expect(page.getByRole('list', { name: 'From', exact: true })).toContainText(QUEUE_NAME);

    // To: the contact's address typed, resolved to the contact.
    const to = page.getByRole('combobox', { name: 'To', exact: true });
    await to.fill(CONTACT_ADDRESS);
    await to.press('Enter');
    await expect(page.getByRole('list', { name: 'To', exact: true })).toContainText(CONTACT_NAME);

    // Cc: an address nobody has stays an unresolved chip (saved with the email, removed before the send).
    const cc = page.getByRole('combobox', { name: 'Cc', exact: true });
    await cc.fill(`nobody.${sfx}@example.org`);
    await cc.press('Enter');
    await expect(page.getByRole('list', { name: 'Cc', exact: true })).toContainText('unresolved');

    await page.getByRole('textbox', { name: /^Subject/ }).fill(SUBJECT);
    await page.getByRole('textbox', { name: /^Description/ }).fill('Please find the invoice attached.');
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(page).toHaveURL(/[?&]id=[0-9a-f-]{36}/i);
    const emailId = /[?&]id=\{?([0-9a-f-]{36})/i.exec(page.url())![1].toLowerCase();
    emails.push(emailId);

    // Two attachments, uploaded beside the form.
    await page.getByLabel('Attach files').setInputFiles([
      { name: 'invoice-1042.pdf', mimeType: 'application/pdf', buffer: samplePdf() },
      { name: 'notes.txt', mimeType: 'text/plain', buffer: Buffer.from('Tracking number 1Z999.') },
    ]);
    await expect(page.getByRole('list', { name: 'Attachments (2)' })).toBeVisible({ timeout: 30_000 });
    await shot(page, 'ui-compose.png');

    // The saved Cc emptied: the next save clears it (a PATCH sends the whole party set without Cc).
    await page.getByRole('button', { name: `Remove nobody.${sfx}@example.org` }).click();
    await expect(page.getByRole('list', { name: 'Cc', exact: true })).toHaveCount(0);

    // Preview: the side pane on the PDF, then the text file.
    await page.getByRole('button', { name: 'Preview invoice-1042.pdf' }).click();
    const pane = page.getByRole('dialog', { name: 'invoice-1042.pdf' });
    await expect(pane.getByText(/1 of 2/)).toBeVisible();
    // A verified PDF (%PDF-) is framed without a sandbox, so the browser's viewer draws it.
    const frame = pane.locator('iframe');
    await expect(frame).toHaveAttribute('src', /^blob:/);
    await expect(frame).not.toHaveAttribute('sandbox');
    await (await frame.elementHandle())!.contentFrame().then((f) => f!.waitForLoadState('load'));
    // The viewer draws after the load: wait until the frame shows more than its empty dark background and stops changing.
    let previous: Buffer = Buffer.alloc(0);
    await expect
      .poll(
        async () => {
          const now = await frame.screenshot();
          const stable = now.length > 20_000 && now.equals(previous);
          previous = now;
          return stable;
        },
        { timeout: 20_000, intervals: [250] },
      )
      .toBe(true);
    await shot(page, 'ui-preview.png');
    await pane.getByRole('button', { name: 'Next' }).click();
    await expect(page.getByRole('dialog', { name: 'notes.txt' }).getByText('Tracking number 1Z999.')).toBeVisible();
    await page.getByRole('dialog', { name: 'notes.txt' }).getByRole('button', { name: 'Close' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // The lookup dialog of To (Xrm.Utility.lookupObjects): opened and closed.
    await page.getByRole('button', { name: 'Search To', exact: true }).click();
    const lookup = page.getByRole('dialog');
    await expect(lookup.getByText(CONTACT_NAME)).toBeVisible();
    await shot(page, 'ui-lookup.png');
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // Send: saves, then SendEmail; the banner follows Pending Send to Sent. A watcher notes the Pending Send banner, however
    // briefly it shows.
    await page.evaluate(() => {
      const w = window as unknown as { sawSending?: boolean };
      w.sawSending = false;
      new MutationObserver(() => {
        if (document.body.innerText.includes('Sending…')) w.sawSending = true;
      }).observe(document.body, { subtree: true, childList: true, characterData: true });
    });
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText(/^Sent/).first()).toBeVisible({ timeout: 120_000 });
    expect(await page.evaluate(() => (window as unknown as { sawSending?: boolean }).sawSending), 'the Pending Send banner').toBe(true);
    await expect(page.getByRole('button', { name: 'Send', exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Delete invoice-1042.pdf' })).toHaveCount(0);
    await shot(page, 'ui-sent.png');

    const sent = await json<{ statecode: number; statuscode: number; messageid: string | null; senton: string | null }>(
      admin,
      `emails(${emailId})?$select=statecode,statuscode,messageid,senton`,
    );
    expect(sent).toMatchObject({ statecode: 1, statuscode: 3 });
    expect(sent.messageid).toBeTruthy();
    expect(sent.senton).toBeTruthy();
    // The Cc emptied after the save was cleared.
    const parties = await json<{ email_activity_parties: { participationtypemask: number }[] }>(
      admin,
      `emails(${emailId})?$select=subject&$expand=email_activity_parties($select=participationtypemask)`,
    );
    expect(parties.email_activity_parties.map((p) => p.participationtypemask)).not.toContain(3);

    // GreenMail received it, from the queue, with both attachments.
    const inbox = await until(
      async () => (await (await fetch(`${greenMailApi}/api/user/${CONTACT_ADDRESS}/messages/INBOX`)).json()) as { subject: string; mimeMessage: string }[],
      (m) => Array.isArray(m) && m.some((x) => x.subject === SUBJECT),
      30_000,
      'The GreenMail inbox',
    );
    const message = inbox.find((m) => m.subject === SUBJECT)!.mimeMessage;
    expect(message).toContain(QUEUE_ADDRESS);
    expect(message).toContain('invoice-1042.pdf');
    expect(message).toContain('notes.txt');
    expect(message).not.toContain(`nobody.${sfx}`);
  } finally {
    await page.context().close();
  }
  expect(failures, 'API requests of the shell that failed').toEqual([]);
});

test('an email whose sender cannot send ends Failed, and its form says why', async ({ browser }) => {
  test.setTimeout(180_000);
  const emailId = await create(admin, 'emails', {
    subject: `Not sent ${sfx}`,
    email_activity_parties: [
      { 'partyid_systemuser@odata.bind': `/systemusers(${adminUserId})`, participationtypemask: 1 },
      { 'partyid_contact@odata.bind': `/contacts(${contactId})`, participationtypemask: 2 },
    ],
  });
  emails.push(emailId);
  const sent = await admin.post(webApi(`emails(${emailId})/Microsoft.Dynamics.CRM.SendEmail`), { data: { IssueSend: true } });
  expect([200, 204], await sent.text()).toContain(sent.status());
  await until(
    () => json<{ statecode: number; statuscode: number }>(admin, `emails(${emailId})?$select=statecode,statuscode`),
    (e) => e.statuscode === 8,
    90_000,
    'The failed send',
  );

  const page = await signedIn(browser, recordUrl(emailId));
  try {
    await expect(page.getByText('Not sent.')).toBeVisible();
    // The reason is the send job's: the administrator's mailbox is not approved (or there is none).
    await expect(page.getByText(/emailrouteraccessapproval|has no mailbox|has no email address/).first()).toBeVisible();
    // Failed is open again: Send is offered for another try.
    await expect(page.getByRole('button', { name: 'Send', exact: true })).toBeVisible();
    await shot(page, 'ui-failed.png');
  } finally {
    await page.context().close();
  }
});
