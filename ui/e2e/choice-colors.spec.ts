import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { expect, request, test, type APIRequestContext, type Browser, type Locator, type Page } from '@playwright/test';
import { ADMIN_OBJECT_ID, mintDevToken, runEnv, uniqueSuffix } from './harness';

/**
 * Choice value colours, end to end.
 *
 * The test adds a choice column with two values (Low, High) to `account` through the Web API, a public view of this
 * run's accounts that shows it, and imports a small solution of its own ("Choice Colours Fixture"): an app over `account`
 * with the modern look and an empty presentation overlay (`cc_/colors/look.json`). Then, in Chromium:
 * - as the administrator, in /manage: account > Columns > "Colours" of the column; the overlay of the app is picked, High
 *   gets a background colour and, with "Automatic text colour" off, a text colour; Save. The option's `Color` (read back
 *   through the Web API) is the background, and the overlay holds the text colour under `choices`;
 * - a user with the modern look sees High as a badge with both colours, and Low (no colour) as plain text;
 * - the same user, whose look is then set to classic, sees plain text and no badge.
 *
 * Local only (not in CI). Set XRMD_E2E_SCREENSHOTS to a folder to keep screenshots (device scale factor 2).
 */

const env = runEnv();
test.use({ baseURL: env.baseURL });

const screenshots = process.env.XRMD_E2E_SCREENSHOTS;
const SOLUTION = 'ChoiceColorsFixture';
const APP_UNIQUE = 'cc_choicecolorsapp';
const APP_NAME = 'Choice Colours Fixture';
const OVERLAY = 'cc_/colors/look.json';
const OVERLAY_ID = '6c2f8d30-4e5a-4b71-8c90-3d4e5f6a7b02';
const VIEW_ID = '6c2f8d30-4e5a-4b71-8c90-3d4e5f6a7b01';
/** Role template ids (Xrmd.Security.StandardRoles), which are also the role ids in the root business unit of every organization. */
const BASIC_USER = '236750cd-45ae-4939-ab12-b24b920ced93';
const SYSTEM_ADMINISTRATOR = '627090ff-40a3-4053-8790-584edc5be201';
const LOW = 100000000;
const HIGH = 100000001;
/** The colours picked in the dialog (the input gives them in lower case; they are stored in upper case). */
const BACKGROUND = '#d13438';
const TEXT = '#ffff00';

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
const label = (text: string) => ({ LocalizedLabels: [{ Label: text, LanguageCode: 1033 }] });

/** The Choice Colours Fixture solution as a zip: an app over `account`, the modern look and an empty overlay. */
function buildSolutionZip(): Buffer {
  const dir = mkdtempSync(join(tmpdir(), 'xrmverse-colors-'));
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
    <Descriptions><Description description="The choice colours test solution of the xrmverse E2E." languagecode="1033" /></Descriptions>
    <Version>1.0.0.0</Version>
    <Managed>0</Managed>
    <Publisher>
      <UniqueName>choicecolorsfixture</UniqueName>
      <LocalizedNames><LocalizedName description="${APP_NAME}" languagecode="1033" /></LocalizedNames>
      <Descriptions />
      <EMailAddress xsi:nil="true"></EMailAddress>
      <SupportingWebsiteUrl xsi:nil="true"></SupportingWebsiteUrl>
      <CustomizationPrefix>cc</CustomizationPrefix>
      <CustomizationOptionValuePrefix>27155</CustomizationOptionValuePrefix>
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
      <Description>Presentation overlay of the Choice Colours Fixture app.</Description>
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
        <Area Id="cc_area" ShowGroups="true" IntroducedVersion="7.0.0.0">
          <Titles><Title LCID="1033" Title="Workspace" /></Titles>
          <Group Id="cc_group" IntroducedVersion="7.0.0.0" IsProfile="false">
            <Titles><Title LCID="1033" Title="Customers" /></Titles>
            <SubArea Id="cc_subarea_account" Entity="account" Client="All,Outlook,OutlookLaptopClient,OutlookWorkstationClient,Web" AvailableOffline="true" PassParams="false" Sku="All,OnPremise,Live,SPLA">
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
      <Descriptions><Description description="An app with the modern look and coloured choice values on accounts." languagecode="1033" /></Descriptions>
      <appsettings>
        <appsetting settingdefinitionid.uniquename="xrmverse_Renderer"><iscustomizable>1</iscustomizable><value>modern</value></appsetting>
        <appsetting settingdefinitionid.uniquename="xrmverse_Presentation"><iscustomizable>1</iscustomizable><value>${OVERLAY}</value></appsetting>
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
async function signedIn(browser: Browser, path: string, user: { objectId: string; name: string }): Promise<Page> {
  const context = await browser.newContext({ baseURL: env.baseURL, viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2, colorScheme: 'light' });
  const page = await context.newPage();
  await page.goto(path);
  await page.getByRole('textbox', { name: 'Dev signing key (Auth:DevSigningKey)', exact: true }).fill(env.signingKey);
  await page.getByRole('textbox', { name: 'User object id (oid)', exact: true }).fill(user.objectId);
  await page.getByRole('textbox', { name: 'Display name', exact: true }).fill(user.name);
  await page.getByRole('button', { name: 'Sign in' }).click();
  return page;
}

async function shoot(page: Page, name: string): Promise<void> {
  if (!screenshots) return;
  mkdirSync(screenshots, { recursive: true });
  await page.waitForTimeout(500);
  await page.screenshot({ path: join(screenshots, `${name}.png`) });
}

let admin: APIRequestContext;
let appId: string;
const sfx = uniqueSuffix();
const COLUMN = `new_ccpriority${sfx}`;
const VIEW_NAME = `Choice Colours ${sfx}`;
const prefix = `CC ${sfx}`;
const accounts = [
  { name: `${prefix} Alder`, priority: HIGH, id: '' },
  { name: `${prefix} Birch`, priority: LOW, id: '' },
];
const administrator = { objectId: ADMIN_OBJECT_ID, name: 'Administrator' };
const user = { objectId: randomUUID(), name: `Colours User ${sfx}`, id: '' };
const listUrl = () => `/main.aspx?appid=${appId}&pagetype=entitylist&etn=account&viewid=${VIEW_ID}&viewType=1039`;

/** The cell of the choice column in the row of `account` (the grid's columns: name, then the choice). */
const choiceCell = (page: Page, account: string): Locator =>
  page.getByRole('grid', { name: VIEW_NAME }).getByRole('row').filter({ hasText: account }).getByRole('gridcell').nth(1);

test.beforeAll(async () => {
  test.setTimeout(600_000);
  admin = await apiAs(env.baseURL);

  // The choice column: two values, no colours yet.
  const column = await admin.post(webApi(`EntityDefinitions(LogicalName='account')/Attributes`), {
    data: {
      '@odata.type': 'Microsoft.Dynamics.CRM.PicklistAttributeMetadata',
      SchemaName: COLUMN,
      LogicalName: COLUMN,
      DisplayName: label('CC Priority'),
      RequiredLevel: { Value: 'None' },
      OptionSet: {
        '@odata.type': 'Microsoft.Dynamics.CRM.OptionSetMetadata',
        IsGlobal: false,
        OptionSetType: 'Picklist',
        Options: [
          { Value: LOW, Label: label('Low') },
          { Value: HIGH, Label: label('High') },
        ],
      },
    },
  });
  expect(column.status(), await column.text()).toBe(204);
  const published = await admin.post(webApi('PublishXml'), {
    data: { ParameterXml: '<importexportxml><entities><entity>account</entity></entities></importexportxml>' },
  });
  expect(published.status(), await published.text()).toBe(204);

  // The public view: this run's accounts, with name and the choice.
  await create(admin, 'savedqueries', {
    savedqueryid: VIEW_ID,
    name: VIEW_NAME,
    returnedtypecode: 'account',
    querytype: 0,
    fetchxml:
      `<fetch version="1.0" mapping="logical"><entity name="account"><attribute name="name" /><attribute name="${COLUMN}" />` +
      `<attribute name="accountid" /><order attribute="name" descending="false" />` +
      `<filter type="and"><condition attribute="name" operator="like" value="${prefix}%" /></filter></entity></fetch>`,
    layoutxml:
      `<grid name="resultset" object="1" jump="name" select="1" icon="1" preview="1"><row name="result" id="accountid">` +
      `<cell name="name" width="300" /><cell name="${COLUMN}" width="160" /></row></grid>`,
  });
  for (const account of accounts) account.id = await create(admin, 'accounts', { name: account.name, [COLUMN]: account.priority });

  await importSolution(admin, buildSolutionZip());
  const apps = await json<{ value: { appmoduleid: string }[] }>(admin, `appmodules?$select=appmoduleid&$filter=uniquename eq '${APP_UNIQUE}'`);
  expect(apps.value).toHaveLength(1);
  appId = apps.value[0].appmoduleid;

  user.id = await createUser(admin, env.baseURL, user.objectId, user.name);
});

test.afterAll(async () => {
  if (!admin) return;
  for (const account of accounts) if (account.id) await admin.delete(webApi(`accounts(${account.id})`));
  await admin.delete(webApi(`savedqueries(${VIEW_ID})`));
  await admin.delete(webApi(`EntityDefinitions(LogicalName='account')/Attributes(LogicalName='${COLUMN}')`));
  await admin.dispose();
});

test('colours set in /manage show as a badge in the modern look, and as plain text in the classic look', async ({ browser }) => {
  // The maker: a background and a text colour for High, the text colour in the app's overlay.
  const manage = await signedIn(browser, '/manage/tables/account', administrator);
  try {
    await manage.getByRole('button', { name: `Colours of ${COLUMN}`, exact: true }).click();
    const dialog = manage.getByRole('dialog', { name: 'Colours of CC Priority' });
    await expect(dialog).toBeVisible();
    await dialog.getByRole('combobox', { name: 'Overlay' }).selectOption({ label: APP_NAME });
    const high = dialog.getByRole('group', { name: 'High' });
    const automatic = high.getByRole('switch', { name: 'Automatic text colour' });
    await expect(automatic).toBeEnabled();
    await high.getByLabel('Background', { exact: true }).fill(BACKGROUND);
    await automatic.click();
    await high.getByLabel('Text', { exact: true }).fill(TEXT);
    // The preview already shows both colours.
    const preview = high.getByText('High', { exact: true }).last();
    await expect(preview).toHaveCSS('background-color', 'rgb(209, 52, 56)');
    await expect(preview).toHaveCSS('color', 'rgb(255, 255, 0)');
    await shoot(manage, 'choice-colors-dialog');
    await dialog.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(dialog).toHaveCount(0);
  } finally {
    await manage.context().close();
  }

  // The background is the option's native Color; the text colour lives in the overlay, under the column's key.
  const attribute = await json<{ OptionSet: { Options: { Value: number; Color?: string | null }[] } }>(
    admin,
    `EntityDefinitions(LogicalName='account')/Attributes(LogicalName='${COLUMN}')/Microsoft.Dynamics.CRM.PicklistAttributeMetadata?$select=LogicalName&$expand=OptionSet`,
  );
  const colorOf = (value: number) => attribute.OptionSet.Options.find((o) => o.Value === value)?.Color ?? null;
  expect(colorOf(HIGH)).toBe(BACKGROUND.toUpperCase());
  expect(colorOf(LOW)).toBeNull();
  const resource = await json<{ value: { content: string }[] }>(admin, `webresourceset?$select=content&$filter=name eq '${OVERLAY}'`);
  const overlay = JSON.parse(Buffer.from(resource.value[0].content, 'base64').toString('utf8')) as { choices?: Record<string, unknown> };
  expect(overlay.choices).toEqual({ [`account.${COLUMN}`]: { [String(HIGH)]: { text: TEXT.toUpperCase() } } });

  // The modern look: High is a badge with both colours; Low, without a colour, is plain text.
  const modern = await signedIn(browser, listUrl(), user);
  try {
    const badge = choiceCell(modern, accounts[0].name).locator('.fui-Badge');
    await expect(badge).toHaveText('High');
    await expect(badge).toHaveCSS('background-color', 'rgb(209, 52, 56)');
    await expect(badge).toHaveCSS('color', 'rgb(255, 255, 0)');
    const plain = choiceCell(modern, accounts[1].name);
    await expect(plain).toHaveText('Low');
    await expect(plain.locator('.fui-Badge')).toHaveCount(0);
    await shoot(modern, 'choice-colors-modern');
  } finally {
    await modern.context().close();
  }

  // The same user switches to the classic look: plain text, no badge.
  const res = await admin.patch(webApi(`usersettingscollection(${user.id})`), { data: { xrmverse_renderer: 'classic' } });
  expect(res.status(), await res.text()).toBe(204);
  const classic = await signedIn(browser, listUrl(), user);
  try {
    const cell = choiceCell(classic, accounts[0].name);
    await expect(cell).toHaveText('High');
    await expect(classic.getByRole('grid', { name: VIEW_NAME }).locator('.fui-Badge')).toHaveCount(0);
    await shoot(classic, 'choice-colors-classic');
  } finally {
    await classic.context().close();
  }
});
