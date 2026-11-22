import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { expect, request, test, type APIRequestContext, type Browser, type Locator, type Page } from '@playwright/test';
import { ADMIN_OBJECT_ID, mintDevToken, runEnv, uniqueSuffix } from './harness';

/**
 * Plan 3.6 UI-2e, task 7: the view designer of the web admin, end to end.
 *
 * The test builds a small solution of its own ("View Designer Fixture"): an app over `account` with the modern look and no
 * presentation overlay of its own, plus the overlay web resource `vd_/designer/look.json` (`{"version":1,"views":{}}`),
 * which the spec makes the environment's overlay (`organizationsetting` of `xrmverse_Presentation`). Then, in Chromium:
 * - as the administrator: Tables > account > Views > New view; add Main Phone and the Email of the primary contact (a
 *   related column), sort by name descending, filter "Account Name begins with E2E {run}", Save; the view is in the Views
 *   tab, and its FetchXML and LayoutXML read back through the Web API have the link-entity, the order, the condition and
 *   three cells;
 * - a modern user opens the app's account list, picks the view in the view selector and sees its columns;
 * - Set as default in the Views tab; Save as makes a copy, which is deleted from the Views tab;
 * - in the designer, the View modes card (environment overlay) switches on a board grouped by Status Reason and a calendar on
 *   Created On, default board; Save writes `views[{id}]` in the overlay; the modern app opens the view on the board and
 *   switches to the calendar.
 * The organization is left as it was: the original default view is restored, the views and the environment setting removed.
 *
 * Local only (not in CI). Set XRMD_E2E_SCREENSHOTS to a folder to keep screenshots (device scale factor 2).
 */

const env = runEnv();
test.use({ baseURL: env.baseURL });

const screenshots = process.env.XRMD_E2E_SCREENSHOTS;
const SOLUTION = 'ViewDesignerFixture';
const APP_UNIQUE = 'vd_viewdesignerapp';
const APP_NAME = 'View Designer Fixture';
const OVERLAY = 'vd_/designer/look.json';
const OVERLAY_ID = '6c2f8d31-4e50-4b72-8c91-3d4e5f6a7b01';
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

/** The View Designer Fixture solution as a zip: an app over `account` with the modern look, and the overlay web resource. */
function buildSolutionZip(): Buffer {
  const dir = mkdtempSync(join(tmpdir(), 'xrmverse-designer-'));
  try {
    const fileName = `/WebResources/${OVERLAY}${OVERLAY_ID.toUpperCase()}`;
    const target = join(dir, fileName);
    mkdirSync(dirname(target), { recursive: true });
    writeFileSync(target, JSON.stringify({ version: 1, views: {} }, null, 2));

    writeFileSync(
      join(dir, 'solution.xml'),
      `<?xml version="1.0" encoding="utf-8"?>
<ImportExportXml version="9.2.24093.200" SolutionPackageVersion="9.2" languagecode="1033" generatedBy="CrmLive" OrganizationVersion="9.2.24093.200" OrganizationSchemaType="Standard" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <SolutionManifest>
    <UniqueName>${SOLUTION}</UniqueName>
    <LocalizedNames><LocalizedName description="${APP_NAME}" languagecode="1033" /></LocalizedNames>
    <Descriptions><Description description="The view designer test solution of the xrmverse E2E." languagecode="1033" /></Descriptions>
    <Version>1.0.0.0</Version>
    <Managed>0</Managed>
    <Publisher>
      <UniqueName>viewdesignerfixture</UniqueName>
      <LocalizedNames><LocalizedName description="${APP_NAME}" languagecode="1033" /></LocalizedNames>
      <Descriptions />
      <EMailAddress xsi:nil="true"></EMailAddress>
      <SupportingWebsiteUrl xsi:nil="true"></SupportingWebsiteUrl>
      <CustomizationPrefix>vd</CustomizationPrefix>
      <CustomizationOptionValuePrefix>27152</CustomizationOptionValuePrefix>
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
      <Description>Presentation overlay of the View Designer Fixture (the environment's overlay in the E2E).</Description>
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
        <Area Id="vd_area" ShowGroups="true" IntroducedVersion="7.0.0.0">
          <Titles><Title LCID="1033" Title="Workspace" /></Titles>
          <Group Id="vd_group" IntroducedVersion="7.0.0.0" IsProfile="false">
            <Titles><Title LCID="1033" Title="Customers" /></Titles>
            <SubArea Id="vd_subarea_account" Entity="account" Client="All,Outlook,OutlookLaptopClient,OutlookWorkstationClient,Web" AvailableOffline="true" PassParams="false" Sku="All,OnPremise,Live,SPLA">
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

async function shoot(target: Page | Locator, name: string, fullPage = false): Promise<void> {
  if (!screenshots) return;
  mkdirSync(screenshots, { recursive: true });
  const page = 'page' in target ? target.page() : target;
  await page.waitForTimeout(500);
  const path = join(screenshots, `${name}.png`);
  if ('page' in target) await target.screenshot({ path });
  else await target.screenshot({ path, fullPage });
}

let admin: APIRequestContext;
let appId: string;
/** The account's default public view before the test (restored at the end). */
let originalDefault: string | undefined;
let environmentSettingId: string | undefined;
let viewId = '';
const createdViews: string[] = [];
const sfx = uniqueSuffix();
const VIEW_NAME = `E2E designer ${sfx}`;
const COPY_NAME = `${VIEW_NAME} (copy)`;
const prefix = `E2E ${sfx}`;
const contact = { name: `Designer Contact ${sfx}`, email: `designer-${sfx}@example.com`, id: '' };
const accounts = [
  { name: `${prefix} Alder`, phone: '555-0201', id: '' },
  { name: `${prefix} Birch`, phone: '555-0202', id: '' },
];
const administrator = { objectId: ADMIN_OBJECT_ID, name: 'Administrator' };
const modern = { objectId: randomUUID(), name: `Designer Modern ${sfx}`, id: '' };
const listUrl = (view?: string) => `/main.aspx?appid=${appId}&pagetype=entitylist&etn=account${view ? `&viewid=${view}&viewType=1039` : ''}`;
/**
 * A column's label: its display name, or its logical name where the E2E seed has no label for it (the seed labels only
 * some columns of `account` and `contact`).
 */
const label = (display: string, logical: string) => new RegExp(`^(${display}|${logical})$`);
const NAME = label('Account Name', 'name');
const PHONE = label('Main Phone', 'telephone1');
const EMAIL = label('Email', 'emailaddress1');
const RELATED_EMAIL = label('Email \\(Primary Contact\\)', 'emailaddress1 \\(primarycontactid\\)');
const viewsRow = (page: Page, name: string) => page.getByRole('row').filter({ has: page.getByRole('link', { name, exact: true }) });

async function openViewsTab(page: Page): Promise<void> {
  await page.getByRole('tab', { name: 'Views', exact: true }).click();
  // The seed may have no view of the table yet: then the tab shows "No views" and no table.
  await expect(page.getByRole('link', { name: 'New view', exact: true })).toBeVisible();
}

async function readOverlay(): Promise<{ views: Record<string, unknown> }> {
  const rows = await json<{ value: { content: string }[] }>(admin, `webresourceset?$select=content&$filter=name eq '${OVERLAY}'`);
  expect(rows.value).toHaveLength(1);
  return JSON.parse(Buffer.from(rows.value[0].content, 'base64').toString('utf8')) as { views: Record<string, unknown> };
}

test.beforeAll(async () => {
  test.setTimeout(600_000);
  admin = await apiAs(env.baseURL);

  const defaults = await json<{ value: { savedqueryid: string }[] }>(
    admin,
    `savedqueries?$select=savedqueryid&$filter=returnedtypecode eq 'account' and querytype eq 0 and isdefault eq true`,
  );
  originalDefault = defaults.value[0]?.savedqueryid;

  contact.id = await create(admin, 'contacts', { lastname: contact.name, emailaddress1: contact.email });
  for (const account of accounts) {
    account.id = await create(admin, 'accounts', {
      name: account.name,
      telephone1: account.phone,
      'primarycontactid@odata.bind': `/contacts(${contact.id})`,
    });
  }

  await importSolution(admin, buildSolutionZip());
  const apps = await json<{ value: { appmoduleid: string }[] }>(admin, `appmodules?$select=appmoduleid&$filter=uniquename eq '${APP_UNIQUE}'`);
  expect(apps.value).toHaveLength(1);
  appId = apps.value[0].appmoduleid;

  // The overlay is the environment's: the app has no overlay of its own, so it follows the environment.
  environmentSettingId = await create(admin, 'organizationsettings', {
    displayname: 'xrmverse_Presentation',
    uniquename: 'xrmverse_Presentation',
    value: OVERLAY,
    'settingdefinitionid@odata.bind': `/settingdefinitions(${PRESENTATION_DEFINITION})`,
  });

  modern.id = await createUser(admin, env.baseURL, modern.objectId, modern.name);
});

test.afterAll(async () => {
  if (!admin) return;
  if (originalDefault) {
    await admin.patch(webApi(`savedqueries(${originalDefault})`), { data: { isdefault: true } });
  }
  for (const id of createdViews) {
    await admin.patch(webApi(`savedqueries(${id})`), { data: { isdefault: false } });
    await admin.delete(webApi(`savedqueries(${id})`));
  }
  await publishAccount(admin).catch(() => undefined);
  if (environmentSettingId) await admin.delete(webApi(`organizationsettings(${environmentSettingId})`));
  for (const account of accounts) if (account.id) await admin.delete(webApi(`accounts(${account.id})`));
  if (contact.id) await admin.delete(webApi(`contacts(${contact.id})`));
  await admin.dispose();
});

test('a new view built in the designer is saved, listed, and offered in the app', async ({ browser }) => {
  const page = await signedIn(browser, '/manage/tables/account', administrator);
  try {
    await expect(page.getByRole('heading', { level: 2, name: 'Account (account)' })).toBeVisible();
    await openViewsTab(page);
    await page.getByRole('link', { name: 'New view', exact: true }).click();
    await expect(page).toHaveURL(/\/manage\/tables\/account\/views\/new$/);

    // Properties.
    await page.getByRole('textbox', { name: 'Name' }).fill(VIEW_NAME);
    await page.getByRole('textbox', { name: 'Description' }).fill('Built by the view designer E2E.');

    // Columns: Main Phone of the account, and Email of the primary contact (a related column).
    const columns = page.getByRole('list', { name: 'Columns', exact: true });
    await expect(columns.getByRole('listitem', { name: NAME })).toBeVisible();
    await page.getByRole('button', { name: 'Add column', exact: true }).click();
    let dialog = page.getByRole('dialog', { name: 'Add column' });
    await dialog.getByRole('textbox', { name: 'Search columns' }).fill('telephone1');
    await dialog.getByRole('button', { name: PHONE }).click();
    await expect(dialog).toHaveCount(0);
    await expect(columns.getByRole('listitem', { name: PHONE })).toBeVisible();

    await page.getByRole('button', { name: 'Add column', exact: true }).click();
    dialog = page.getByRole('dialog', { name: 'Add column' });
    await dialog.getByRole('combobox', { name: 'Columns of' }).selectOption('primarycontactid|contact');
    await dialog.getByRole('textbox', { name: 'Search columns' }).fill('emailaddress1');
    await dialog.getByRole('button', { name: EMAIL }).click();
    await expect(dialog).toHaveCount(0);
    await expect(columns.getByRole('listitem', { name: RELATED_EMAIL })).toBeVisible();

    // Sort: the new view sorts by name ascending; make it descending.
    await expect(page.getByRole('combobox', { name: 'Sort column 1' })).toHaveValue('name');
    await page.getByRole('combobox', { name: 'Sort direction 1' }).selectOption('desc');

    // Filter: Account Name begins with "E2E {run}", next to the "Status equals Active" of a new view.
    const filter = page.getByRole('region', { name: 'Filter' });
    await filter.getByRole('button', { name: 'Add condition to the filter' }).click();
    await filter.getByRole('combobox', { name: 'Column of condition 2' }).selectOption('name');
    await filter.getByRole('combobox', { name: 'Operator of condition 2' }).selectOption('begins-with');
    await filter.getByRole('textbox', { name: 'Value of condition 2' }).fill(prefix);

    // The preview shows this run's accounts, by name descending, with the related column.
    const preview = page.getByRole('table', { name: 'Preview of the view' });
    await expect(preview.getByRole('columnheader', { name: RELATED_EMAIL })).toBeVisible();
    await expect(preview.getByRole('row')).toHaveCount(3);
    await expect(preview.getByRole('row').nth(1)).toContainText(accounts[1].name);
    await expect(preview.getByRole('row').nth(1)).toContainText(contact.email);
    await shoot(page, 'ui-2e-designer', true);

    const saved = page.waitForResponse((r) => r.url().endsWith('/savedqueries') && r.request().method() === 'POST');
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    expect((await saved).status()).toBe(204);
    await expect(page).toHaveURL(/\/manage\/tables\/account\/views\/[0-9a-f-]{36}$/);
    viewId = /views\/([0-9a-f-]{36})$/.exec(page.url())![1];
    createdViews.push(viewId);
    await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeEnabled();

    // The row read back through the Web API.
    const row = await json<{ name: string; querytype: number; fetchxml: string; layoutxml: string }>(
      admin,
      `savedqueries(${viewId})?$select=name,querytype,fetchxml,layoutxml`,
    );
    expect(row.name).toBe(VIEW_NAME);
    expect(row.querytype).toBe(0);
    expect(row.fetchxml).toMatch(
      /<link-entity (?=[^>]*name="contact")(?=[^>]*from="contactid")(?=[^>]*to="primarycontactid")(?=[^>]*link-type="outer")(?=[^>]*alias="a_primarycontactid")[^>]*>\s*<attribute name="emailaddress1"\s*\/>\s*<\/link-entity>/,
    );
    expect(row.fetchxml).toMatch(/<order attribute="name" descending="true"\s*\/>/);
    expect(row.fetchxml).toMatch(new RegExp(`<condition attribute="name" operator="begins-with" value="${prefix}"\\s*/>`));
    expect(row.fetchxml).toMatch(/<attribute name="telephone1"\s*\/>/);
    const cells = [...row.layoutxml.matchAll(/<cell name="([^"]+)" width="(\d+)"/g)].map((m) => `${m[1]}:${m[2]}`);
    expect(cells).toEqual(['name:150', 'telephone1:100', 'a_primarycontactid.emailaddress1:100']);

    // Back to the table: the Views tab lists the view.
    await page.getByRole('button', { name: 'Back', exact: true }).click();
    await expect(page).toHaveURL(/\/manage\/tables\/account$/);
    await openViewsTab(page);
    const listed = viewsRow(page, VIEW_NAME);
    await expect(listed).toBeVisible();
    await expect(listed).toContainText('Public');
    await shoot(page, 'ui-2e-views-tab');
  } finally {
    await page.context().close();
  }

  // The model-driven app offers the view in its view selector and shows its columns.
  const app = await signedIn(browser, listUrl(), modern);
  try {
    await app.getByRole('button', { name: /^Select a view: / }).click();
    await app.getByRole('menuitemradio', { name: VIEW_NAME, exact: true }).click();
    await expect(app.getByRole('button', { name: `Select a view: ${VIEW_NAME}`, exact: true })).toBeVisible();
    const grid = app.getByRole('grid', { name: VIEW_NAME });
    for (const header of [NAME, PHONE, RELATED_EMAIL]) {
      await expect(grid.getByRole('columnheader', { name: header })).toBeVisible();
    }
    await expect(grid.getByText(accounts[0].name, { exact: true })).toBeVisible();
    await expect(grid.getByText(accounts[0].phone, { exact: true })).toBeVisible();
  } finally {
    await app.context().close();
  }
});

test('Set as default and Save as, then the copy is deleted', async ({ browser }) => {
  expect(viewId).not.toBe('');
  const page = await signedIn(browser, '/manage/tables/account', administrator);
  try {
    await openViewsTab(page);
    const commands = page.getByRole('toolbar', { name: 'Views commands' });
    await viewsRow(page, VIEW_NAME).getByRole('radio').click();
    const published = page.waitForResponse((r) => r.url().endsWith('/PublishXml') && r.request().method() === 'POST');
    await commands.getByRole('button', { name: 'Set as default' }).click();
    expect((await published).status()).toBe(204);
    // The default view has no row commands (it cannot be deleted, and it is already the default).
    await expect(commands.getByRole('button', { name: 'Set as default' })).toHaveCount(0);
    await expect(commands.getByRole('button', { name: 'Delete', exact: true })).toHaveCount(0);
    const defaults = await json<{ value: { savedqueryid: string }[] }>(
      admin,
      `savedqueries?$select=savedqueryid&$filter=returnedtypecode eq 'account' and querytype eq 0 and isdefault eq true`,
    );
    expect(defaults.value.map((v) => v.savedqueryid)).toEqual([viewId]);

    // Save as: a copy named "{name} (copy)", opened in the designer.
    await page.getByRole('link', { name: VIEW_NAME, exact: true }).click();
    await expect(page.getByRole('textbox', { name: 'Name' })).toHaveValue(VIEW_NAME);
    await page.getByRole('button', { name: 'Save as', exact: true }).click();
    await expect(page.getByRole('textbox', { name: 'Name' })).toHaveValue(COPY_NAME);
    await expect(page).not.toHaveURL(new RegExp(viewId));
    const copyId = /views\/([0-9a-f-]{36})$/.exec(page.url())![1];
    createdViews.push(copyId);
    const copy = await json<{ name: string; isdefault?: boolean | null }>(admin, `savedqueries(${copyId})?$select=name,isdefault`);
    expect(copy.name).toBe(COPY_NAME);
    // The create sends isdefault false: the copy is never a default view.
    expect(copy.isdefault).toBe(false);

    // Delete the copy from the Views tab.
    await page.getByRole('button', { name: 'Back', exact: true }).click();
    await openViewsTab(page);
    await viewsRow(page, COPY_NAME).getByRole('radio').click();
    await page.getByRole('toolbar', { name: 'Views commands' }).getByRole('button', { name: 'Delete', exact: true }).click();
    const deleted = page.waitForResponse((r) => r.url().includes(`savedqueries(${copyId})`) && r.request().method() === 'DELETE');
    await page.getByRole('dialog').getByRole('button', { name: 'Delete', exact: true }).click();
    expect((await deleted).status()).toBe(204);
    await expect(viewsRow(page, COPY_NAME)).toHaveCount(0);
    await expect(viewsRow(page, VIEW_NAME)).toBeVisible();
    const gone = await admin.get(webApi(`savedqueries(${copyId})`));
    expect(gone.status()).toBe(404);
    createdViews.splice(createdViews.indexOf(copyId), 1);
  } finally {
    await page.context().close();
  }
});

test('view modes set in the designer show in the modern app', async ({ browser }) => {
  expect(viewId).not.toBe('');
  const page = await signedIn(browser, `/manage/tables/account/views/${viewId}`, administrator);
  try {
    await expect(page.getByRole('textbox', { name: 'Name' })).toHaveValue(VIEW_NAME);
    const card = page.getByRole('region', { name: 'Modern look' });
    // The environment's overlay is preselected.
    await expect(card.getByRole('combobox', { name: 'Overlay' })).toHaveValue(OVERLAY);
    await expect(card.getByRole('combobox', { name: 'Overlay' }).locator('option:checked')).toHaveText('Environment');
    await card.getByRole('combobox', { name: 'Board' }).selectOption('statuscode');
    await card.getByRole('combobox', { name: 'Calendar', exact: true }).selectOption('createdon');
    await card.getByRole('combobox', { name: 'Default mode' }).selectOption('kanban');
    await card.scrollIntoViewIfNeeded();
    await shoot(card, 'ui-2e-view-modes');

    const overlaySaved = page.waitForResponse((r) => r.url().includes('/PublishXml') && (r.request().postData() ?? '').includes('<webresources>'));
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    expect((await overlaySaved).status()).toBe(204);
    await expect(card.getByText(/could not be saved/)).toHaveCount(0);
  } finally {
    await page.context().close();
  }

  const overlay = await readOverlay();
  expect(Object.keys(overlay.views)).toEqual([viewId.toLowerCase()]);
  expect(overlay.views[viewId.toLowerCase()]).toEqual({ kanban: { groupBy: 'statuscode' }, calendar: { start: 'createdon' }, default: 'kanban' });

  // The modern app opens the view on the board (its default mode) and switches to the calendar.
  const app = await signedIn(browser, listUrl(viewId), modern);
  try {
    const switcher = app.getByRole('button', { name: /^Show as: / });
    await expect(switcher).toHaveText('Show as: Board');
    const active = app.getByRole('list', { name: 'Active', exact: true });
    await expect(active.getByRole('listitem')).toHaveCount(accounts.length);
    await switcher.click();
    await expect(app.getByRole('menuitemradio', { name: 'Cards' })).toHaveCount(0);
    await app.getByRole('menuitemradio', { name: 'Calendar' }).click();
    await expect(switcher).toHaveText('Show as: Calendar');
    // Since #302 a calendar bar is named "<record>, <start> – <end>, <n> day(s)": match the record name as the prefix.
    const barName = new RegExp(`^${accounts[0].name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}, `);
    await expect(app.getByRole('grid', { name: VIEW_NAME }).getByRole('button', { name: barName })).toBeVisible();
  } finally {
    await app.context().close();
  }
});
