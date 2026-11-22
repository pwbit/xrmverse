import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { expect, request, test, type APIRequestContext, type Browser, type Locator, type Page } from '@playwright/test';
import { ADMIN_OBJECT_ID, mintDevToken, runEnv, uniqueSuffix } from './harness';

/**
 * Plan 3.6 UI-2f, task 7: the form designer of the web admin, end to end.
 *
 * The test builds a small solution of its own ("Form Designer Fixture"): an app over `account` with the modern look and no
 * presentation overlay of its own, plus the overlay web resource `fd_/designer/look.json` (`{"version":1}`: no `views`, no
 * `forms`), which the wizard test makes the environment's overlay (`organizationsetting` of `xrmverse_Presentation`).
 * Then, in Chromium:
 * - as the administrator: Tables > account > Forms > New form; add the tab "Details" with two columns, a section "Contact"
 *   with two columns, the fields Main Phone and Website (Main Phone read-only), put Account Name and Main Phone in the
 *   header, Save; the FormXML read back through the Web API has the tab, the section's `columns`, the two controls,
 *   `disabled="true"` on the phone and the header controls `header_name` and `header_telephone1`; the Forms tab lists it;
 * - a modern user opens an account in the app, picks the form in the form selector and sees the header, the tab, the
 *   fields and the phone read-only;
 * - Save as makes a copy, which is deleted from the Forms tab; the form is deactivated and activated again;
 * - with the environment overlay set, the Modern look card switches the form to the wizard and Save writes `forms[{id}]`
 *   in the overlay; the modern app opens a new account on that form as a wizard with the form's tabs as steps.
 * - on an account form with a Contacts subgrid (made through the Web API), the Modern look card's Subgrids block gives the
 *   subgrid a board by status as its default mode, and Save writes `forms[{id}].subgrids.contacts` in the overlay.
 * The forms, the account and the environment setting are removed afterwards. What stays behind: the fixture solution, its
 * app, the overlay web resource (with its `forms` entry) and one user per run.
 *
 * Local only (not in CI). Set XRMD_E2E_SCREENSHOTS to a folder to keep screenshots (device scale factor 2).
 */

const env = runEnv();
test.use({ baseURL: env.baseURL });
// The tests share `formId` from the first one: after a failure the rest are skipped, not rerun in a new worker.
test.describe.configure({ mode: 'serial' });

const screenshots = process.env.XRMD_E2E_SCREENSHOTS;
const SOLUTION = 'FormDesignerFixture';
const APP_UNIQUE = 'fd_formdesignerapp';
const APP_NAME = 'Form Designer Fixture';
const OVERLAY = 'fd_/designer/look.json';
const OVERLAY_ID = '7d3a9e42-5f61-4c83-9da2-4e5f6a7b8c01';
/** Role template ids (Xrmd.Security.StandardRoles), which are also the role ids in the root business unit of every organization. */
const BASIC_USER = '236750cd-45ae-4939-ab12-b24b920ced93';
const SYSTEM_ADMINISTRATOR = '627090ff-40a3-4053-8790-584edc5be201';
/** The seeded setting definition `xrmverse_Presentation`. */
const PRESENTATION_DEFINITION = '6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e11';

const webApi = (path: string) => `/api/data/v9.2/${path}`;

async function apiAs(baseURL: string): Promise<APIRequestContext> {
  const token = mintDevToken(env.signingKey, `${baseURL}/`, ADMIN_OBJECT_ID, 'Administrator');
  return request.newContext({
    baseURL,
    extraHTTPHeaders: { Authorization: `Bearer ${token}`, Accept: 'application/json', 'OData-Version': '4.0' },
  });
}

async function json<T>(api: APIRequestContext, path: string): Promise<T> {
  const res = await api.get(webApi(path));
  expect(res.status(), `GET ${path}: ${await res.text()}`).toBe(200);
  return (await res.json()) as T;
}

/** POST that answers 204 with OData-EntityId; returns the new record's id. */
async function create(api: APIRequestContext, set: string, data: Record<string, unknown>): Promise<string> {
  const res = await api.post(webApi(set), { data });
  expect(res.status(), `POST ${set}: ${await res.text()}`).toBe(204);
  return /\(([0-9a-f-]{36})\)$/.exec(res.headers()['odata-entityid'] ?? '')![1];
}

async function importSolution(api: APIRequestContext, zip: Buffer): Promise<void> {
  const res = await api.post(webApi('ImportSolution'), {
    data: { CustomizationFile: zip.toString('base64'), ImportJobId: randomUUID(), OverwriteUnmanagedCustomizations: true, PublishWorkflows: false },
    timeout: 300_000,
  });
  expect(res.status(), await res.text()).toBe(204);
}

async function publishAccount(api: APIRequestContext): Promise<void> {
  const res = await api.post(webApi('PublishXml'), {
    data: { ParameterXml: '<importexportxml><entities><entity>account</entity></entities></importexportxml>' },
  });
  expect(res.status(), await res.text()).toBe(204);
}

/** A user of the root business unit with Basic User (the app's role) and System Administrator (reads accounts). */
async function createUser(api: APIRequestContext, baseURL: string, objectId: string, fullname: string): Promise<string> {
  const bu = (await json<{ value: { businessunitid: string }[] }>(api, 'businessunits?$select=businessunitid&$filter=_parentbusinessunitid_value eq null')).value[0]
    .businessunitid;
  const userId = await create(api, 'systemusers', {
    fullname,
    azureactivedirectoryobjectid: objectId,
    'businessunitid@odata.bind': `/businessunits(${bu})`,
  });
  for (const role of [BASIC_USER, SYSTEM_ADMINISTRATOR]) {
    const res = await api.post(webApi(`systemusers(${userId})/systemuserroles_association/$ref`), {
      data: { '@odata.id': `${baseURL}${webApi(`roles(${role})`)}` },
    });
    expect(res.status(), await res.text()).toBe(204);
  }
  return userId;
}

const xml = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

/** The Form Designer Fixture solution as a zip: an app over `account` with the modern look, and the overlay web resource. */
function buildSolutionZip(): Buffer {
  const dir = mkdtempSync(join(tmpdir(), 'xrmverse-form-designer-'));
  try {
    const fileName = `/WebResources/${OVERLAY}${OVERLAY_ID.toUpperCase()}`;
    const target = join(dir, fileName);
    mkdirSync(dirname(target), { recursive: true });
    writeFileSync(target, JSON.stringify({ version: 1 }, null, 2));

    writeFileSync(
      join(dir, 'solution.xml'),
      `<?xml version="1.0" encoding="utf-8"?>
<ImportExportXml version="9.2.24093.200" SolutionPackageVersion="9.2" languagecode="1033" generatedBy="CrmLive" OrganizationVersion="9.2.24093.200" OrganizationSchemaType="Standard" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <SolutionManifest>
    <UniqueName>${SOLUTION}</UniqueName>
    <LocalizedNames><LocalizedName description="${APP_NAME}" languagecode="1033" /></LocalizedNames>
    <Descriptions><Description description="The form designer test solution of the xrmverse E2E." languagecode="1033" /></Descriptions>
    <Version>1.0.0.0</Version>
    <Managed>0</Managed>
    <Publisher>
      <UniqueName>formdesignerfixture</UniqueName>
      <LocalizedNames><LocalizedName description="${APP_NAME}" languagecode="1033" /></LocalizedNames>
      <Descriptions />
      <EMailAddress xsi:nil="true"></EMailAddress>
      <SupportingWebsiteUrl xsi:nil="true"></SupportingWebsiteUrl>
      <CustomizationPrefix>fd</CustomizationPrefix>
      <CustomizationOptionValuePrefix>27153</CustomizationOptionValuePrefix>
      <Addresses />
    </Publisher>
    <RootComponents>
      <RootComponent type="61" schemaName="${OVERLAY}" behavior="0" />
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
  <WebResources>
    <WebResource>
      <WebResourceId>{${OVERLAY_ID}}</WebResourceId>
      <Name>${xml(OVERLAY)}</Name>
      <DisplayName>look.json</DisplayName>
      <Description>Presentation overlay of the Form Designer Fixture (the environment's overlay in the E2E).</Description>
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
    <AppModuleSiteMap>
      <SiteMapUniqueName>${APP_UNIQUE}</SiteMapUniqueName>
      <SiteMap IntroducedVersion="7.0.0.0">
        <Area Id="fd_area" ShowGroups="true" IntroducedVersion="7.0.0.0">
          <Titles><Title LCID="1033" Title="Workspace" /></Titles>
          <Group Id="fd_group" IntroducedVersion="7.0.0.0" IsProfile="false">
            <Titles><Title LCID="1033" Title="Customers" /></Titles>
            <SubArea Id="fd_subarea_account" Entity="account" Client="All,Outlook,OutlookLaptopClient,OutlookWorkstationClient,Web" AvailableOffline="true" PassParams="false" Sku="All,OnPremise,Live,SPLA">
              <Titles><Title LCID="1033" Title="Accounts" /></Titles>
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
        <AppModuleComponent type="1" schemaName="account" />
        <AppModuleComponent type="62" schemaName="${APP_UNIQUE}" />
      </AppModuleComponents>
      <AppModuleRoleMaps>
        <Role id="{${BASIC_USER}}" />
      </AppModuleRoleMaps>
      <LocalizedNames><LocalizedName description="${APP_NAME}" languagecode="1033" /></LocalizedNames>
      <Descriptions><Description description="An app with the modern look that follows the environment's overlay." languagecode="1033" /></Descriptions>
      <appsettings>
        <appsetting settingdefinitionid.uniquename="xrmverse_Renderer"><iscustomizable>1</iscustomizable><value>modern</value></appsetting>
      </appsettings>
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
      `<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/octet-stream" /><Override PartName="${xml(fileName)}" ContentType="application/octet-stream" /></Types>`,
    );

    const zip = join(dir, `${SOLUTION}.zip`);
    execFileSync('zip', ['-r', '-X', '-q', zip, 'solution.xml', 'customizations.xml', '[Content_Types].xml', 'WebResources'], { cwd: dir });
    return readFileSync(zip);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

/** A fresh browser context (device scale factor 2) signed in through the dev sign-in form. */
async function signedIn(browser: Browser, path: string, user: { objectId: string; name: string }, width = 1440): Promise<Page> {
  const context = await browser.newContext({ baseURL: env.baseURL, viewport: { width, height: 900 }, deviceScaleFactor: 2, colorScheme: 'light' });
  const page = await context.newPage();
  await page.goto(path);
  await page.getByRole('textbox', { name: 'Dev signing key (Auth:DevSigningKey)', exact: true }).fill(env.signingKey);
  await page.getByRole('textbox', { name: 'User object id (oid)', exact: true }).fill(user.objectId);
  await page.getByRole('textbox', { name: 'Display name', exact: true }).fill(user.name);
  await page.getByRole('button', { name: 'Sign in' }).click();
  return page;
}

/** Screenshots of one or more regions, as one image of the area that holds them (device scale factor 2). */
async function shoot(target: Page | Locator | Locator[], name: string, fullPage = false): Promise<void> {
  if (!screenshots) return;
  mkdirSync(screenshots, { recursive: true });
  const path = join(screenshots, `${name}.png`);
  if (Array.isArray(target)) {
    const page = target[0].page();
    await page.waitForTimeout(500);
    const boxes = await Promise.all(target.map(async (l) => (await l.boundingBox())!));
    const x = Math.min(...boxes.map((b) => b.x));
    const y = Math.min(...boxes.map((b) => b.y));
    const right = Math.max(...boxes.map((b) => b.x + b.width));
    const bottom = Math.max(...boxes.map((b) => b.y + b.height));
    await page.screenshot({ path, clip: { x, y, width: right - x, height: bottom - y }, fullPage: true });
    return;
  }
  const page = 'page' in target ? target.page() : target;
  await page.waitForTimeout(500);
  if ('page' in target) await target.screenshot({ path });
  else await target.screenshot({ path, fullPage });
}

let admin: APIRequestContext;
let appId: string;
let environmentSettingId: string | undefined;
let formId = '';
const createdForms: string[] = [];
const sfx = uniqueSuffix();
const FORM_NAME = `E2E form ${sfx}`;
const COPY_NAME = `${FORM_NAME} (copy)`;
/** A plain form of `account` made through the Web API: with the designed form, it makes the app show its form selector. */
const BASE_NAME = `A E2E base form ${sfx}`;
const BASE_XML =
  '<form><tabs><tab name="tab_1" id="{6a1f0c52-3b7d-4e18-9c40-1d2e3f4a5b01}" expanded="true" showlabel="true" verticallayout="true">' +
  '<labels><label description="Base" languagecode="1033"/></labels><columns><column width="100%"><sections>' +
  '<section name="section_1" id="{6a1f0c52-3b7d-4e18-9c40-1d2e3f4a5b02}" showlabel="false" showbar="false" columns="1"><labels><label description="Base" languagecode="1033"/></labels>' +
  '<rows><row><cell id="{6a1f0c52-3b7d-4e18-9c40-1d2e3f4a5b03}" showlabel="true"><labels><label description="Account Name" languagecode="1033"/></labels>' +
  '<control id="name" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="name"/></cell></row></rows></section></sections></column></columns></tab></tabs></form>';
const account = { name: `E2E ${sfx} Acme`, phone: '555-0301', website: `https://acme-${sfx}.example.com`, id: '' };
const administrator = { objectId: ADMIN_OBJECT_ID, name: 'Administrator' };
const modern = { objectId: randomUUID(), name: `Form Designer Modern ${sfx}`, id: '' };
const recordUrl = (opts: { id?: string; form?: string } = {}) =>
  `/main.aspx?appid=${appId}&pagetype=entityrecord&etn=account${opts.form ? `&formid=${opts.form}` : ''}${opts.id ? `&id=${opts.id}` : ''}`;
/** A column's label: its display name, or its logical name where the E2E seed has no label for it. */
const label = (display: string, logical: string) => new RegExp(`^(${display}|${logical})$`);
const NAME = label('Account Name', 'name');
const PHONE = label('Main Phone', 'telephone1');
const WEBSITE = label('Website', 'websiteurl');
const formsRow = (page: Page, name: string) => page.getByRole('row').filter({ has: page.getByRole('link', { name, exact: true }) });

async function openFormsTab(page: Page): Promise<void> {
  await page.getByRole('tab', { name: 'Forms', exact: true }).click();
  await expect(page.getByRole('button', { name: 'New form', exact: true })).toBeVisible();
}

/** The cards of the designer page, by their titles. */
const card = (page: Page, title: string) => page.getByRole('region', { name: title, exact: true });

async function readOverlay(): Promise<{ forms?: Record<string, unknown> }> {
  const rows = await json<{ value: { content: string }[] }>(admin, `webresourceset?$select=content&$filter=name eq '${OVERLAY}'`);
  expect(rows.value).toHaveLength(1);
  return JSON.parse(Buffer.from(rows.value[0].content, 'base64').toString('utf8')) as { forms?: Record<string, unknown> };
}

/** The opening tag of the first `<tag ...>` in `xml` that has all `attributes` (in any order). */
function openTag(xml: string, tag: string, ...attributes: string[]): string | undefined {
  return [...xml.matchAll(new RegExp(`<${tag}\\s[^>]*>`, 'g'))].map((m) => m[0]).find((t) => attributes.every((a) => t.includes(a)));
}

test.beforeAll(async () => {
  test.setTimeout(600_000);
  admin = await apiAs(env.baseURL);

  account.id = await create(admin, 'accounts', { name: account.name, telephone1: account.phone, websiteurl: account.website });

  createdForms.push(await create(admin, 'systemforms', { name: BASE_NAME, objecttypecode: 'account', type: 2, formxml: BASE_XML }));

  await importSolution(admin, buildSolutionZip());
  const apps = await json<{ value: { appmoduleid: string }[] }>(admin, `appmodules?$select=appmoduleid&$filter=uniquename eq '${APP_UNIQUE}'`);
  expect(apps.value).toHaveLength(1);
  appId = apps.value[0].appmoduleid;

  modern.id = await createUser(admin, env.baseURL, modern.objectId, modern.name);
});

test.afterAll(async () => {
  if (!admin) return;
  for (const id of createdForms) {
    await admin.patch(webApi(`systemforms(${id})`), { data: { isdefault: false } });
    await admin.delete(webApi(`systemforms(${id})`));
  }
  await publishAccount(admin).catch(() => undefined);
  if (account.id) await admin.delete(webApi(`accounts(${account.id})`));
  await admin.dispose();
});

test('a new form built in the designer is saved, listed, and offered in the app', async ({ browser }) => {
  const page = await signedIn(browser, '/manage/tables/account', administrator);
  try {
    await expect(page.getByRole('heading', { level: 2, name: 'Account (account)' })).toBeVisible();
    await openFormsTab(page);
    await page.getByRole('button', { name: 'New form', exact: true }).click();
    await page.getByRole('menuitem', { name: 'Main form', exact: true }).click();
    await expect(page).toHaveURL(/\/manage\/tables\/account\/forms\/new$/);

    // Properties.
    await page.getByRole('textbox', { name: /^Name/ }).fill(FORM_NAME);
    await page.getByRole('textbox', { name: 'Description' }).fill('Built by the form designer E2E.');

    const structure = card(page, 'Structure');
    const selected = card(page, 'Selected item');
    // A new form has one tab and one section, holding the primary name field.
    await expect(structure.getByRole('tab')).toHaveCount(1);
    await expect(structure.getByRole('button', { name: NAME })).toBeVisible();

    // Add the tab "Details" with two columns (it is selected when added).
    await structure.getByRole('button', { name: 'Add tab', exact: true }).click();
    await expect(structure.getByRole('tab')).toHaveCount(2);
    await selected.getByRole('textbox', { name: 'Label' }).fill('Details');
    await expect(structure.getByRole('tab', { name: 'Details', exact: true })).toHaveAttribute('aria-selected', 'true');
    await selected.getByRole('combobox', { name: 'Layout' }).selectOption({ label: 'Two columns (50/50)' });
    await expect(structure.getByRole('group', { name: /^Column \d$/ })).toHaveCount(2);

    // Add a section to the first column (next to the one the new tab starts with), call it "Contact", with two columns.
    await structure.getByRole('button', { name: 'Add section to column 1', exact: true }).click();
    const sections = structure.getByRole('group', { name: 'Column 1', exact: true }).getByRole('region');
    await expect(sections).toHaveCount(2);
    const contact = sections.nth(1);
    await contact.getByRole('button').first().click();
    await selected.getByRole('textbox', { name: 'Label' }).fill('Contact');
    await selected.getByRole('combobox', { name: 'Columns', exact: true }).selectOption('2');

    // Add Main Phone and Website to it.
    for (const [search, pick] of [['telephone1', PHONE], ['websiteurl', WEBSITE]] as const) {
      await contact.getByRole('button', { name: /^Add field to / }).click();
      const dialog = page.getByRole('dialog', { name: 'Add field' });
      await dialog.getByRole('textbox', { name: 'Search columns' }).fill(search);
      await dialog.getByRole('button', { name: pick }).click();
      await expect(dialog).toHaveCount(0);
      await expect(contact.getByRole('button', { name: pick })).toBeVisible();
    }

    // Main Phone is read-only.
    await contact.getByRole('button', { name: PHONE }).click();
    await selected.getByRole('switch', { name: 'Read-only' }).click();
    await expect(selected.getByRole('switch', { name: 'Read-only' })).toBeChecked();
    await expect(selected.getByText('Column telephone1')).toBeVisible();
    await shoot(selected, 'ui-2f-field-properties');

    // The header takes Account Name and Main Phone.
    const header = card(page, 'Header');
    for (const [search, pick] of [['name', NAME], ['telephone1', PHONE]] as const) {
      await header.getByRole('button', { name: 'Add header field', exact: true }).click();
      const dialog = page.getByRole('dialog', { name: 'Add field' });
      await dialog.getByRole('textbox', { name: 'Search columns' }).fill(search);
      await dialog.getByRole('button', { name: pick }).click();
      await expect(dialog).toHaveCount(0);
      await expect(header.getByRole('list', { name: 'Header', exact: true }).getByRole('listitem', { name: pick })).toBeVisible();
    }
    await structure.scrollIntoViewIfNeeded();
    await shoot(structure, 'ui-2f-canvas');

    const saved = page.waitForResponse((r) => r.url().endsWith('/systemforms') && r.request().method() === 'POST');
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    expect((await saved).status()).toBe(204);
    await expect(page).toHaveURL(/\/manage\/tables\/account\/forms\/[0-9a-f-]{36}$/);
    formId = /forms\/([0-9a-f-]{36})$/.exec(page.url())![1];
    createdForms.push(formId);
    await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeEnabled();

    // The row read back through the Web API.
    const row = await json<{ name: string; type: number; formxml: string }>(admin, `systemforms(${formId})?$select=name,type,formxml`);
    expect(row.name).toBe(FORM_NAME);
    expect(row.type).toBe(2);
    const xml = row.formxml;
    // The tab "Details" (the second tab, `tab_2`) in two columns of 50%.
    const tab = openTag(xml, 'tab', 'name="tab_2"');
    expect(tab, xml).toBeDefined();
    expect(xml).toMatch(/<label description="Details" languagecode="1033"\s*\/>/);
    expect(xml.match(/<column width="50%">/g) ?? []).toHaveLength(2);
    // The section "Contact" with two columns, holding the two controls; the phone is read-only.
    expect(openTag(xml, 'section', 'columns="11"'), xml).toBeDefined();
    expect(xml).toMatch(/<label description="Contact" languagecode="1033"\s*\/>/);
    const phone = openTag(xml, 'control', 'id="telephone1"');
    const website = openTag(xml, 'control', 'id="websiteurl"');
    expect(phone).toContain('disabled="true"');
    expect(website).toContain('disabled="false"');
    expect(xml.indexOf('name="tab_2"')).toBeLessThan(xml.indexOf('id="telephone1"'));
    expect(xml.indexOf('id="telephone1"')).toBeLessThan(xml.indexOf('id="websiteurl"'));
    // The header controls, written as Dataverse does.
    const headerXml = /<header[\s>][\s\S]*?<\/header>/.exec(xml)?.[0] ?? '';
    expect(headerXml).toContain('id="header_name"');
    expect(headerXml).toContain('id="header_telephone1"');
    expect(headerXml.indexOf('header_name')).toBeLessThan(headerXml.indexOf('header_telephone1'));

    // Back to the table: the Forms tab lists the form, as an active Main form that is not the default.
    await page.getByRole('button', { name: 'Back', exact: true }).click();
    await expect(page).toHaveURL(/\/manage\/tables\/account$/);
    await openFormsTab(page);
    const listed = formsRow(page, FORM_NAME);
    await expect(listed).toBeVisible();
    await expect(listed).toContainText('Main');
    await shoot(page, 'ui-2f-forms-tab');
  } finally {
    await page.context().close();
  }
});

test('the app offers the form in its selector, with the header, the tab and the read-only phone', async ({ browser }) => {
  expect(formId).not.toBe('');
  const app = await signedIn(browser, recordUrl({ id: account.id }), modern);
  try {
    await expect(app.getByRole('heading', { level: 1, name: account.name })).toBeVisible();
    // The seed has no form of `account`, so the app opens the base form (it sorts first); then the selector shows both.
    const selector = app.getByRole('combobox', { name: 'Form', exact: true });
    await expect(selector.locator('option:checked')).toHaveText(BASE_NAME);
    await selector.selectOption({ label: FORM_NAME });
    await expect(app.getByRole('combobox', { name: 'Form', exact: true }).locator('option:checked')).toHaveText(FORM_NAME);

    // The header: Account Name and Main Phone.
    const header = app.getByRole('group', { name: 'Header', exact: true });
    await expect(header.getByRole('textbox', { name: /^(Account Name|name)/ })).toHaveValue(account.name);
    await expect(header.getByRole('textbox', { name: /^(Main Phone|telephone1)/ })).toHaveValue(account.phone);
    await shoot(app, 'form-header');
    // The chevron opens the header fields to edit.
    await app.getByRole('button', { name: 'Edit the header fields', exact: true }).click();
    const edit = app.getByRole('group', { name: 'Edit the header fields', exact: true });
    await expect(edit.getByRole('textbox', { name: /^(Account Name|name)/ })).toBeEnabled();
    await shoot(app, 'form-header-edit');
    await app.keyboard.press('Escape');

    // The tab "Details": Main Phone read-only, Website editable.
    await app.getByRole('tab', { name: 'Details', exact: true }).click();
    const panel = app.getByRole('tabpanel');
    await expect(panel.getByRole('textbox', { name: /^(Main Phone|telephone1)/ })).toHaveValue(account.phone);
    await expect(panel.getByRole('textbox', { name: /^(Main Phone|telephone1)/ })).toBeDisabled();
    await expect(panel.getByRole('textbox', { name: /^(Website|websiteurl)/ })).toHaveValue(account.website);
    await expect(panel.getByRole('textbox', { name: /^(Website|websiteurl)/ })).toBeEnabled();
  } finally {
    await app.context().close();
  }
});

test('Save as makes a copy that is deleted again; the form is deactivated and activated', async ({ browser }) => {
  expect(formId).not.toBe('');
  const page = await signedIn(browser, '/manage/tables/account', administrator);
  try {
    await openFormsTab(page);
    await page.getByRole('link', { name: FORM_NAME, exact: true }).click();
    await expect(page.getByRole('textbox', { name: /^Name/ })).toHaveValue(FORM_NAME);

    // Save as: a copy named "{name} (copy)", opened in the designer; it keeps the type and is not the default.
    await page.getByRole('button', { name: 'Save as', exact: true }).click();
    await expect(page.getByRole('textbox', { name: /^Name/ })).toHaveValue(COPY_NAME);
    await expect(page).not.toHaveURL(new RegExp(formId));
    const copyId = /forms\/([0-9a-f-]{36})$/.exec(page.url())![1];
    createdForms.push(copyId);
    const copy = await json<{ name: string; type: number; isdefault: boolean | null; formactivationstate: number; formxml: string }>(
      admin,
      `systemforms(${copyId})?$select=name,type,isdefault,formactivationstate,formxml`,
    );
    expect(copy.name).toBe(COPY_NAME);
    expect(copy.type).toBe(2);
    expect(copy.isdefault).toBe(false);
    expect(copy.formactivationstate).toBe(1);
    expect(copy.formxml).toContain('id="header_telephone1"');

    // Delete the copy from the Forms tab (it asks first).
    await page.getByRole('button', { name: 'Back', exact: true }).click();
    await openFormsTab(page);
    await formsRow(page, COPY_NAME).getByRole('radio').click();
    await page.getByRole('toolbar', { name: 'Forms commands' }).getByRole('button', { name: 'Delete', exact: true }).click();
    const deleted = page.waitForResponse((r) => r.url().includes(`systemforms(${copyId})`) && r.request().method() === 'DELETE');
    await page.getByRole('dialog').getByRole('button', { name: 'Delete', exact: true }).click();
    expect((await deleted).status()).toBe(204);
    await expect(formsRow(page, COPY_NAME)).toHaveCount(0);
    await expect(formsRow(page, FORM_NAME)).toBeVisible();
    expect((await admin.get(webApi(`systemforms(${copyId})`))).status()).toBe(404);
    createdForms.splice(createdForms.indexOf(copyId), 1);

    // Deactivate and activate (each publishes the table).
    const state = async () => (await json<{ formactivationstate: number }>(admin, `systemforms(${formId})?$select=formactivationstate`)).formactivationstate;
    const toggled = (item: 'Deactivate' | 'Activate') => async () => {
      const radio = formsRow(page, FORM_NAME).getByRole('radio');
      if (!(await radio.isChecked())) await radio.click();
      const changed = page.waitForResponse((r) => r.url().includes(`systemforms(${formId})`) && r.request().method() === 'PATCH');
      await page.getByRole('toolbar', { name: 'Forms commands' }).getByRole('button', { name: item, exact: true }).click();
      expect((await changed).status()).toBe(204);
    };
    await toggled('Deactivate')();
    await expect.poll(state).toBe(0);
    await expect(formsRow(page, FORM_NAME).getByRole('cell').nth(4)).toHaveText('');
    await toggled('Activate')();
    await expect.poll(state).toBe(1);
    await expect(formsRow(page, FORM_NAME).getByRole('cell').nth(4)).toHaveText('✓');
  } finally {
    await page.context().close();
  }
});

test('the modern look card switches the form to the wizard in the environment overlay', async ({ browser }) => {
  expect(formId).not.toBe('');
  // The environment's overlay is the fixture's. A setting left behind by an earlier run (or the seed) is removed first.
  const existing = await json<{ value: { organizationsettingid: string }[] }>(
    admin,
    `organizationsettings?$select=organizationsettingid&$filter=uniquename eq 'xrmverse_Presentation'`,
  );
  for (const row of existing.value) await admin.delete(webApi(`organizationsettings(${row.organizationsettingid})`));
  environmentSettingId = await create(admin, 'organizationsettings', {
    displayname: 'xrmverse_Presentation',
    uniquename: 'xrmverse_Presentation',
    value: OVERLAY,
    'settingdefinitionid@odata.bind': `/settingdefinitions(${PRESENTATION_DEFINITION})`,
  });
  try {
    const page = await signedIn(browser, `/manage/tables/account/forms/${formId}`, administrator);
    try {
      await expect(page.getByRole('textbox', { name: /^Name/ })).toHaveValue(FORM_NAME);
      const look = card(page, 'Modern look');
      // The environment's overlay is preselected; it has no `forms` key yet, and is readable.
      await expect(look.getByRole('combobox', { name: 'Overlay' })).toHaveValue(OVERLAY);
      await expect(look.getByRole('combobox', { name: 'Overlay' }).locator('option:checked')).toHaveText('Environment');
      await look.getByRole('combobox', { name: 'Layout', exact: true }).selectOption('wizard');
      await expect(look.getByRole('combobox', { name: 'Layout', exact: true })).toHaveValue('wizard');
      await expect(look.getByRole('combobox', { name: 'Tab', exact: true })).toBeVisible();
      await look.scrollIntoViewIfNeeded();
      await shoot([card(page, 'Header'), look], 'ui-2f-header-modern-look');
      // The first section of the tab gets a yellow background; its fields show it.
      const sectionColour = look.getByRole('group', { name: /^Background: / }).first();
      await sectionColour.getByRole('radio', { name: 'Yellow' }).click();
      await expect(sectionColour.getByRole('radio', { name: 'Yellow' })).toBeChecked();
      await sectionColour.scrollIntoViewIfNeeded();
      await shoot(look, 'form-backgrounds-designer');

      const overlaySaved = page.waitForResponse((r) => r.url().includes('/PublishXml') && (r.request().postData() ?? '').includes('<webresources>'));
      await page.getByRole('button', { name: 'Save', exact: true }).click();
      expect((await overlaySaved).status()).toBe(204);
      await expect(look.getByText(/could not be saved/)).toHaveCount(0);
    } finally {
      await page.context().close();
    }

    const overlay = await readOverlay();
    const entry = overlay.forms?.[formId.toLowerCase()] as { layout?: string; background?: { sections?: Record<string, string> } } | undefined;
    expect(entry?.layout).toBe('wizard');
    expect(Object.values(entry?.background?.sections ?? {})).toEqual(['yellow']);

    // A new account on the form is a wizard: the form's tabs are its steps.
    const app = await signedIn(browser, recordUrl({ form: formId }), modern);
    try {
      await expect(app.getByRole('heading', { level: 1, name: /^New / })).toBeVisible();
      const steps = app.getByRole('tablist', { name: 'Steps', exact: true });
      await expect(steps.getByRole('tab')).toHaveCount(2);
      await expect(steps.getByRole('tab').first()).toHaveAccessibleName('General');
      await expect(steps.getByRole('tab').nth(1)).toHaveAccessibleName('Details');
      await expect(app.getByRole('status').filter({ hasText: /^Step \d+ of \d+:/ })).toHaveText('Step 1 of 2: General');
      await expect(app.locator('[role="group"][data-background="true"]')).toHaveCount(1);
    } finally {
      await app.context().close();
    }
  } finally {
    if (environmentSettingId) await admin.delete(webApi(`organizationsettings(${environmentSettingId})`));
    environmentSettingId = undefined;
  }
});

test('the modern look card gives a subgrid a board in the environment overlay', async ({ browser }) => {
  // A view of `contact` (the seed has none) and an account form whose section holds a Contacts subgrid over it.
  const rels = await json<{ value: { SchemaName: string; ReferencingEntity?: string; ReferencingAttribute?: string; ReferencedEntity?: string }[] }>(
    admin,
    'RelationshipDefinitions?$select=SchemaName,ReferencingEntity,ReferencingAttribute,ReferencedEntity',
  );
  const relationship = rels.value.find((r) => r.ReferencingEntity === 'contact' && r.ReferencingAttribute === 'parentcustomerid' && r.ReferencedEntity === 'account')
    ?.SchemaName;
  expect(relationship).toBeTruthy();
  const viewId = await create(admin, 'savedqueries', {
    name: `E2E contacts ${sfx}`,
    returnedtypecode: 'contact',
    querytype: 0,
    fetchxml:
      '<fetch version="1.0" mapping="logical"><entity name="contact"><attribute name="lastname" /><attribute name="statuscode" /><attribute name="contactid" />' +
      '<order attribute="lastname" descending="false" /></entity></fetch>',
    layoutxml:
      '<grid name="resultset" object="2" jump="lastname" select="1" icon="1" preview="1"><row name="result" id="contactid">' +
      '<cell name="lastname" width="300" /><cell name="statuscode" width="120" /></row></grid>',
  });
  const subgridFormXml =
    '<form><tabs><tab name="tab_1" id="{6a1f0c52-3b7d-4e18-9c40-1d2e3f4a5b11}" expanded="true" showlabel="true" verticallayout="true">' +
    '<labels><label description="General" languagecode="1033"/></labels><columns><column width="100%"><sections>' +
    '<section name="section_1" id="{6a1f0c52-3b7d-4e18-9c40-1d2e3f4a5b12}" showlabel="false" showbar="false" columns="1"><labels><label description="Main" languagecode="1033"/></labels><rows>' +
    '<row><cell id="{6a1f0c52-3b7d-4e18-9c40-1d2e3f4a5b13}" showlabel="true"><labels><label description="Account Name" languagecode="1033"/></labels>' +
    '<control id="name" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="name"/></cell></row>' +
    '<row><cell id="{6a1f0c52-3b7d-4e18-9c40-1d2e3f4a5b14}" showlabel="true" rowspan="8"><labels><label description="Contacts" languagecode="1033"/></labels>' +
    '<control id="Contacts" classid="{E7A81278-8635-4D9E-8D4D-59480B391C5B}" indicationOfSubgrid="true"><parameters>' +
    `<RecordsPerPage>10</RecordsPerPage><AutoExpand>Fixed</AutoExpand><EnableQuickFind>false</EnableQuickFind><EnableViewPicker>false</EnableViewPicker>` +
    `<ViewId>{${viewId}}</ViewId><IsUserView>false</IsUserView><RelationshipName>${relationship}</RelationshipName>` +
    '<TargetEntityType>contact</TargetEntityType></parameters></control></cell></row>' +
    '</rows></section></sections></column></columns></tab></tabs></form>';
  const subgridFormId = await create(admin, 'systemforms', { name: `E2E subgrid form ${sfx}`, objecttypecode: 'account', type: 2, formxml: subgridFormXml });
  createdForms.push(subgridFormId);

  const existing = await json<{ value: { organizationsettingid: string }[] }>(
    admin,
    `organizationsettings?$select=organizationsettingid&$filter=uniquename eq 'xrmverse_Presentation'`,
  );
  for (const row of existing.value) await admin.delete(webApi(`organizationsettings(${row.organizationsettingid})`));
  environmentSettingId = await create(admin, 'organizationsettings', {
    displayname: 'xrmverse_Presentation',
    uniquename: 'xrmverse_Presentation',
    value: OVERLAY,
    'settingdefinitionid@odata.bind': `/settingdefinitions(${PRESENTATION_DEFINITION})`,
  });
  try {
    const page = await signedIn(browser, `/manage/tables/account/forms/${subgridFormId}`, administrator);
    try {
      const look = card(page, 'Modern look');
      await expect(look.getByRole('combobox', { name: 'Overlay' })).toHaveValue(OVERLAY);
      // One block per subgrid, named after its label; the contact view has no modes, so there is nothing to copy from it.
      const block = look.getByRole('group', { name: 'Contacts', exact: true });
      await expect(block).toContainText('contact');
      await expect(block.getByRole('button', { name: "Use the view's modes" })).toHaveCount(0);
      await block.getByRole('combobox', { name: 'Board', exact: true }).selectOption('statuscode');
      await block.getByRole('combobox', { name: 'Default mode', exact: true }).selectOption('kanban');
      await expect(block.getByRole('combobox', { name: 'Default mode', exact: true })).toHaveValue('kanban');
      await block.scrollIntoViewIfNeeded();
      await shoot(look, 'subgrid-modes-designer');

      const overlaySaved = page.waitForResponse((r) => r.url().includes('/PublishXml') && (r.request().postData() ?? '').includes('<webresources>'));
      await page.getByRole('button', { name: 'Save', exact: true }).click();
      expect((await overlaySaved).status()).toBe(204);
      await expect(look.getByText(/could not be saved/)).toHaveCount(0);
    } finally {
      await page.context().close();
    }

    // The entry is keyed by the control id in lower case, and holds exactly what the block set.
    const overlay = await readOverlay();
    const entry = overlay.forms?.[subgridFormId.toLowerCase()] as { subgrids?: Record<string, unknown> } | undefined;
    expect(entry?.subgrids).toEqual({ contacts: { default: 'kanban', kanban: { groupBy: 'statuscode' } } });
  } finally {
    if (environmentSettingId) await admin.delete(webApi(`organizationsettings(${environmentSettingId})`));
    environmentSettingId = undefined;
    await admin.delete(webApi(`savedqueries(${viewId})`));
  }
});
