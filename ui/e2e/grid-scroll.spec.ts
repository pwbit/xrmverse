import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { expect, request, test, type APIRequestContext, type Browser, type Page } from '@playwright/test';
import { ADMIN_OBJECT_ID, mintDevToken, runEnv, uniqueSuffix } from './harness';

/**
 * A table's list in the modern look: the cells stay on one line, a column the view's overlay entry names (`wrap`) wraps,
 * and the grid's rows scroll on their own: the header, the navigation, the command bar, the view's bar, the column headers
 * and the paging stay in place.
 *
 * The test adds a public view of this run's 60 accounts (more than a page of 50) with a long note, and imports a
 * small solution of its own ("Grid Scroll Fixture"): an app over `account` with the modern look and an empty overlay. In
 * /manage, the administrator switches "Wrap text" on for the note in the view designer and saves; then a user
 * scrolls the list.
 *
 * Local only (not in CI). Set XRMD_E2E_SCREENSHOTS to a folder to keep screenshots (device scale factor 2).
 */

const env = runEnv();
test.use({ baseURL: env.baseURL });

const screenshots = process.env.XRMD_E2E_SCREENSHOTS;
const SOLUTION = 'GridScrollFixture';
const APP_UNIQUE = 'gs_gridscrollapp';
const APP_NAME = 'Grid Scroll Fixture';
const OVERLAY = 'gs_/grid/look.json';
const OVERLAY_ID = '7d3a9e41-5f6b-4c82-9da1-4e5f6a7b8c02';
const VIEW_ID = '7d3a9e41-5f6b-4c82-9da1-4e5f6a7b8c01';
/** Role template ids (Xrmd.Security.StandardRoles), which are also the role ids in the root business unit of every organization. */
const BASIC_USER = '236750cd-45ae-4939-ab12-b24b920ced93';
const SYSTEM_ADMINISTRATOR = '627090ff-40a3-4053-8790-584edc5be201';

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

/** The Grid Scroll Fixture solution as a zip: an app over `account`, the modern look and an empty overlay. */
function buildSolutionZip(): Buffer {
  const dir = mkdtempSync(join(tmpdir(), 'xrmverse-gridscroll-'));
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
    <Descriptions><Description description="The grid scroll test solution of the xrmverse E2E." languagecode="1033" /></Descriptions>
    <Version>1.0.0.0</Version>
    <Managed>0</Managed>
    <Publisher>
      <UniqueName>gridscrollfixture</UniqueName>
      <LocalizedNames><LocalizedName description="${APP_NAME}" languagecode="1033" /></LocalizedNames>
      <Descriptions />
      <EMailAddress xsi:nil="true"></EMailAddress>
      <SupportingWebsiteUrl xsi:nil="true"></SupportingWebsiteUrl>
      <CustomizationPrefix>gs</CustomizationPrefix>
      <CustomizationOptionValuePrefix>27156</CustomizationOptionValuePrefix>
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
      <Description>Presentation overlay of the Grid Scroll Fixture app.</Description>
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
        <Area Id="gs_area" ShowGroups="true" IntroducedVersion="7.0.0.0">
          <Titles><Title LCID="1033" Title="Workspace" /></Titles>
          <Group Id="gs_group" IntroducedVersion="7.0.0.0" IsProfile="false">
            <Titles><Title LCID="1033" Title="Customers" /></Titles>
            <SubArea Id="gs_subarea_account" Entity="account" Client="All,Outlook,OutlookLaptopClient,OutlookWorkstationClient,Web" AvailableOffline="true" PassParams="false" Sku="All,OnPremise,Live,SPLA">
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
      <Descriptions><Description description="An app with the modern look over accounts." languagecode="1033" /></Descriptions>
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
  const context = await browser.newContext({ baseURL: env.baseURL, viewport: { width: 1280, height: 720 }, deviceScaleFactor: 2, colorScheme: 'light' });
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
const VIEW_NAME = `Grid Scroll ${sfx}`;
const prefix = `GS ${sfx}`;
const LONG = 'A long note that does not fit in its column and wraps onto more lines when the view asks for it.';
const accounts = Array.from({ length: 60 }, (_, i) => ({ name: `${prefix} Account ${String(i + 1).padStart(2, '0')} with a rather long name`, id: '' }));
const administrator = { objectId: ADMIN_OBJECT_ID, name: 'Administrator' };
const user = { objectId: randomUUID(), name: `Grid User ${sfx}`, id: '' };
const listUrl = () => `/main.aspx?appid=${appId}&pagetype=entitylist&etn=account&viewid=${VIEW_ID}&viewType=1039`;
/** Text columns of this run on `account`: logical name, display name, width in the view. */
const TEXTS: [string, string, number][] = [
  [`new_gsnote${sfx}`, 'GS Note', 260],
  [`new_gsmail${sfx}`, 'GS Mail', 280],
  [`new_gsweb${sfx}`, 'GS Website', 280],
  [`new_gscity${sfx}`, 'GS City', 200],
];
const NOTE = TEXTS[0][0];
const CELLS: [string, number][] = [['name', 260], ...TEXTS.map(([c, , w]) => [c, w] as [string, number]), ['createdon', 200]];

test.beforeAll(async () => {
  test.setTimeout(600_000);
  admin = await apiAs(env.baseURL);
  for (const [name, display] of TEXTS) {
    const column = await admin.post(webApi(`EntityDefinitions(LogicalName='account')/Attributes`), {
      data: {
        '@odata.type': 'Microsoft.Dynamics.CRM.StringAttributeMetadata',
        SchemaName: name,
        LogicalName: name,
        DisplayName: { LocalizedLabels: [{ Label: display, LanguageCode: 1033 }] },
        RequiredLevel: { Value: 'None' },
        MaxLength: 400,
      },
    });
    expect(column.status(), await column.text()).toBe(204);
  }
  const published = await admin.post(webApi('PublishXml'), {
    data: { ParameterXml: '<importexportxml><entities><entity>account</entity></entities></importexportxml>' },
  });
  expect(published.status(), await published.text()).toBe(204);
  await create(admin, 'savedqueries', {
    savedqueryid: VIEW_ID,
    name: VIEW_NAME,
    returnedtypecode: 'account',
    querytype: 0,
    fetchxml:
      `<fetch version="1.0" mapping="logical"><entity name="account">${CELLS.map(([c]) => `<attribute name="${c}" />`).join('')}` +
      `<attribute name="accountid" /><order attribute="name" descending="false" />` +
      `<filter type="and"><condition attribute="name" operator="like" value="${prefix}%" /></filter></entity></fetch>`,
    layoutxml:
      `<grid name="resultset" object="1" jump="name" select="1" icon="1" preview="1"><row name="result" id="accountid">` +
      `${CELLS.map(([c, w]) => `<cell name="${c}" width="${w}" />`).join('')}</row></grid>`,
  });
  for (const [i, account] of accounts.entries()) {
    account.id = await create(admin, 'accounts', {
      name: account.name,
      [NOTE]: LONG,
      [TEXTS[1][0]]: `vendorcare-emea-${i}@example.test`,
      [TEXTS[2][0]]: `https://www.example.test/vendors/${i}/profile`,
      [TEXTS[3][0]]: 'Rome, Lazio, Italy',
    });
  }
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
  for (const [name] of TEXTS) await admin.delete(webApi(`EntityDefinitions(LogicalName='account')/Attributes(LogicalName='${name}')`));
  await admin.dispose();
});

const box = async (page: Page, selector: string) => (await page.locator(selector).first().boundingBox())!;

test('cells stay on one line, a wrapped column wraps, and only the rows scroll', async ({ browser }) => {
  // The maker: Wrap text for the description, in the app's overlay.
  const manage = await signedIn(browser, `/manage/tables/account/views/${VIEW_ID}`, administrator);
  try {
    const card = manage.getByRole('region', { name: 'Modern look' });
    await card.getByRole('combobox', { name: 'Overlay' }).selectOption({ label: APP_NAME });
    const wrap = card.getByRole('group', { name: 'Wrap text' });
    await wrap.getByRole('switch', { name: 'GS Note' }).click();
    await wrap.scrollIntoViewIfNeeded();
    await shoot(manage, 'grid-wrap-designer');
    await manage.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(manage.getByRole('button', { name: 'Save', exact: true })).toBeEnabled();
    await expect
      .poll(async () => {
        const resource = await json<{ value: { content: string }[] }>(admin, `webresourceset?$select=content&$filter=name eq '${OVERLAY}'`);
        return (JSON.parse(Buffer.from(resource.value[0].content, 'base64').toString('utf8')) as { views?: Record<string, { wrap?: string[] }> }).views?.[VIEW_ID]?.wrap;
      })
      .toEqual([NOTE]);
  } finally {
    await manage.context().close();
  }

  const list = await signedIn(browser, listUrl(), user);
  try {
    const grid = list.getByRole('grid', { name: VIEW_NAME });
    const first = grid.getByRole('row').filter({ hasText: accounts[0].name });
    await expect(first).toBeVisible();
    // The name stays on one line, its full text in the tooltip; the description wraps.
    await expect(first.locator(`[title="${accounts[0].name}"]`)).toHaveCount(1);
    await expect(first.locator('[data-wrap]')).toHaveText(LONG);
    await shoot(list, 'grid-scroll-top');

    const area = list.locator('[data-rows]');
    const before = { header: await box(list, 'header'), bar: await box(list, '[role="toolbar"]'), columns: await box(list, '[role="columnheader"]'), paging: await box(list, '[role="group"][aria-label="Pages"]') };
    // The rows overflow their area both ways; the window itself does not scroll.
    const sizes = await area.evaluate((el) => ({ sh: el.scrollHeight, ch: el.clientHeight, sw: el.scrollWidth, cw: el.clientWidth }));
    expect(sizes.sh).toBeGreaterThan(sizes.ch);
    expect(sizes.sw).toBeGreaterThan(sizes.cw);
    await area.evaluate((el) => el.scrollBy(0, 600));
    await list.mouse.wheel(0, 600);
    expect(await list.evaluate(() => window.scrollY)).toBe(0);
    expect(await area.evaluate((el) => el.scrollTop)).toBeGreaterThan(0);
    // Everything around the rows stays where it was; the column headers stay at the top of the area.
    const after = { header: await box(list, 'header'), bar: await box(list, '[role="toolbar"]'), columns: await box(list, '[role="columnheader"]'), paging: await box(list, '[role="group"][aria-label="Pages"]') };
    expect(after).toEqual(before);
    // The paging is inside the window.
    expect(after.paging.y + after.paging.height).toBeLessThanOrEqual(720);
    await shoot(list, 'grid-scroll-scrolled');
  } finally {
    await list.context().close();
  }
});
