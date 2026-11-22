import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, request, test, type APIRequestContext, type Browser, type Page } from '@playwright/test';
import { ADMIN_OBJECT_ID, mintDevToken, uniqueSuffix } from './harness';

/**
 * The model-driven UI on a PostgreSQL organization (plan 2.7b, Task 3, ruling Q10): the UI has no PostgreSQL code of its
 * own, so one pass over the app picker, a list, a form and a save proves the server side it reads.
 *
 * Not part of the SQL Server run (playwright.config.ts ignores it): run it with playwright.postgres.config.ts against a
 * Development-mode host on PostgreSQL, for instance the one of docker/docker-compose.postgres.yml:
 *   XRMD_E2E_PG_BASE_URL=http://localhost:5000 XRMD_E2E_PG_SIGNING_KEY=<AUTH_DEV_SIGNING_KEY> \
 *   XRMD_E2E_PG_ADMIN_OBJECT_ID=<SEED_ADMIN_OBJECT_ID> pnpm exec playwright test -c playwright.postgres.config.ts
 * The host's Auth:Audience must be the base URL followed by "/".
 *
 * It imports a small unmanaged solution of its own (an app over `account` for System Administrator), adds a public view and a
 * main form of `account` (a new organization has neither) and an account, then in Chromium: /apps lists the app; the app opens on the accounts list; the account opens; its name is changed and
 * saved; a reload shows the new name, and so does the Web API.
 */

const baseURL = process.env.XRMD_E2E_PG_BASE_URL ?? '';
const signingKey = process.env.XRMD_E2E_PG_SIGNING_KEY ?? '';
const adminObjectId = process.env.XRMD_E2E_PG_ADMIN_OBJECT_ID ?? ADMIN_OBJECT_ID;
test.skip(!baseURL || !signingKey, 'XRMD_E2E_PG_BASE_URL and XRMD_E2E_PG_SIGNING_KEY name the PostgreSQL host to test.');
test.use({ baseURL });

const SOLUTION = 'PostgresSmokeFixture';
const APP_UNIQUE = 'pgs_postgressmokeapp';
const APP_NAME = 'PostgreSQL Smoke';
const VIEW_ID = '7d3e9e41-5f6c-4c82-9da1-4e5f6a7b8c01';
const FORM_ID = '7d3e9e41-5f6c-4c82-9da1-4e5f6a7b8c02';
/** A main form with the account's name only. */
const FORM_XML =
  '<form><tabs><tab name="tab_1" id="{7d3e9e41-5f6c-4c82-9da1-4e5f6a7b8c03}" expanded="true" showlabel="true" verticallayout="true">' +
  '<labels><label description="General" languagecode="1033"/></labels><columns><column width="100%"><sections>' +
  '<section name="section_1" id="{7d3e9e41-5f6c-4c82-9da1-4e5f6a7b8c04}" showlabel="false" showbar="false" columns="1"><labels><label description="General" languagecode="1033"/></labels>' +
  '<rows><row><cell id="{7d3e9e41-5f6c-4c82-9da1-4e5f6a7b8c05}" showlabel="true"><labels><label description="Account Name" languagecode="1033"/></labels>' +
  '<control id="name" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="name"/></cell></row></rows></section></sections></column></columns></tab></tabs></form>';
/** The System Administrator role template id, also its role id in the root business unit of every organization. */
const SYSTEM_ADMINISTRATOR = '627090ff-40a3-4053-8790-584edc5be201';

const webApi = (path: string) => `/api/data/v9.2/${path}`;

async function json<T>(api: APIRequestContext, path: string): Promise<T> {
  const res = await api.get(webApi(path));
  expect(res.status(), `GET ${path}: ${await res.text()}`).toBe(200);
  return (await res.json()) as T;
}

/** The solution as a zip: an app over `account` with its site map, for System Administrator. */
function buildSolutionZip(): Buffer {
  const dir = mkdtempSync(join(tmpdir(), 'xrmverse-pg-smoke-'));
  try {
    writeFileSync(
      join(dir, 'solution.xml'),
      `<?xml version="1.0" encoding="utf-8"?>
<ImportExportXml version="9.2.24093.200" SolutionPackageVersion="9.2" languagecode="1033" generatedBy="CrmLive" OrganizationVersion="9.2.24093.200" OrganizationSchemaType="Standard" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <SolutionManifest>
    <UniqueName>${SOLUTION}</UniqueName>
    <LocalizedNames><LocalizedName description="${APP_NAME}" languagecode="1033" /></LocalizedNames>
    <Descriptions><Description description="The PostgreSQL smoke test solution of the xrmverse E2E." languagecode="1033" /></Descriptions>
    <Version>1.0.0.0</Version>
    <Managed>0</Managed>
    <Publisher>
      <UniqueName>postgressmokefixture</UniqueName>
      <LocalizedNames><LocalizedName description="${APP_NAME}" languagecode="1033" /></LocalizedNames>
      <Descriptions />
      <EMailAddress xsi:nil="true"></EMailAddress>
      <SupportingWebsiteUrl xsi:nil="true"></SupportingWebsiteUrl>
      <CustomizationPrefix>pgs</CustomizationPrefix>
      <CustomizationOptionValuePrefix>27156</CustomizationOptionValuePrefix>
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
        <Area Id="pgs_area" ShowGroups="true" IntroducedVersion="7.0.0.0">
          <Titles><Title LCID="1033" Title="Workspace" /></Titles>
          <Group Id="pgs_group" IntroducedVersion="7.0.0.0" IsProfile="false">
            <Titles><Title LCID="1033" Title="Customers" /></Titles>
            <SubArea Id="pgs_subarea_account" Entity="account" Client="All,Outlook,OutlookLaptopClient,OutlookWorkstationClient,Web" AvailableOffline="true" PassParams="false" Sku="All,OnPremise,Live,SPLA">
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
        <Role id="{${SYSTEM_ADMINISTRATOR}}" />
      </AppModuleRoleMaps>
      <LocalizedNames><LocalizedName description="${APP_NAME}" languagecode="1033" /></LocalizedNames>
      <Descriptions><Description description="Accounts, for the PostgreSQL smoke test." languagecode="1033" /></Descriptions>
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

/** A fresh browser context signed in as the administrator through the dev sign-in form. */
async function signedIn(browser: Browser, path: string): Promise<Page> {
  const context = await browser.newContext({ baseURL, viewport: { width: 1440, height: 900 } });
  const page = await context.newPage();
  await page.goto(path);
  await page.getByRole('textbox', { name: 'Dev signing key (Auth:DevSigningKey)', exact: true }).fill(signingKey);
  await page.getByRole('textbox', { name: 'User object id (oid)', exact: true }).fill(adminObjectId);
  await page.getByRole('textbox', { name: 'Display name', exact: true }).fill('Administrator');
  await page.getByRole('button', { name: 'Sign in' }).click();
  return page;
}

let admin: APIRequestContext;
const sfx = uniqueSuffix();
const accountName = `PG Smoke ${sfx}`;
const VIEW_NAME = `PG Smoke Accounts ${sfx}`;
const editedName = `PG Smoke ${sfx} edited`;
let accountId = '';

test.beforeAll(async () => {
  test.setTimeout(300_000);
  const token = mintDevToken(signingKey, `${baseURL}/`, adminObjectId, 'Administrator');
  admin = await request.newContext({
    baseURL,
    extraHTTPHeaders: { Authorization: `Bearer ${token}`, Accept: 'application/json', 'OData-Version': '4.0' },
  });
  const imported = await admin.post(webApi('ImportSolution'), {
    data: { CustomizationFile: buildSolutionZip().toString('base64'), ImportJobId: randomUUID(), OverwriteUnmanagedCustomizations: true, PublishWorkflows: false },
    timeout: 300_000,
  });
  expect(imported.status(), await imported.text()).toBe(204);
  // The view (this run's accounts) and the main form of the list and the record pages.
  for (const [set, data] of [
    [
      'savedqueries',
      {
        savedqueryid: VIEW_ID,
        name: VIEW_NAME,
        returnedtypecode: 'account',
        querytype: 0,
        fetchxml:
          '<fetch version="1.0" mapping="logical"><entity name="account"><attribute name="name" /><attribute name="accountid" />' +
          `<order attribute="name" descending="false" /><filter type="and"><condition attribute="name" operator="like" value="PG Smoke ${sfx}%" /></filter></entity></fetch>`,
        layoutxml: '<grid name="resultset" object="1" jump="name" select="1" icon="1" preview="1"><row name="result" id="accountid"><cell name="name" width="300" /></row></grid>',
      },
    ],
    ['systemforms', { systemformid: FORM_ID, name: `PG Smoke ${sfx}`, objecttypecode: 'account', type: 2, formxml: FORM_XML }],
  ] as const) {
    const res = await admin.post(webApi(set), { data });
    expect(res.status(), `POST ${set}: ${await res.text()}`).toBe(204);
  }
  const published = await admin.post(webApi('PublishXml'), {
    data: { ParameterXml: '<importexportxml><entities><entity>account</entity></entities></importexportxml>' },
  });
  expect(published.status(), await published.text()).toBe(204);
  const created = await admin.post(webApi('accounts'), { data: { name: accountName } });
  expect(created.status(), await created.text()).toBe(204);
  accountId = /\(([0-9a-f-]{36})\)$/.exec(created.headers()['odata-entityid'] ?? '')![1];
});

test.afterAll(async () => {
  if (!admin) return;
  if (accountId) await admin.delete(webApi(`accounts(${accountId})`));
  await admin.delete(webApi(`savedqueries(${VIEW_ID})`));
  await admin.delete(webApi(`systemforms(${FORM_ID})`));
  await admin.dispose();
});

test('the app picker, a list, a form and a save work on a PostgreSQL organization', async ({ browser }) => {
  const page = await signedIn(browser, '/apps');
  const failures: string[] = [];
  page.on('response', (res) => {
    if (res.url().includes('/api/') && res.status() >= 500) failures.push(`${res.status()} ${res.request().method()} ${res.url()}`);
  });
  try {
    // The app picker lists the imported app; it opens on its only page, the accounts list.
    const apps = page.getByRole('list', { name: 'Apps', exact: true });
    await expect(apps.getByRole('link', { name: APP_NAME })).toBeVisible();
    await apps.getByRole('link', { name: APP_NAME }).click();
    await expect(page).toHaveURL(/main\.aspx/);
    const nav = page.getByRole('navigation', { name: APP_NAME, exact: true });
    await expect(nav.getByRole('link', { name: 'Accounts', exact: true })).toHaveAttribute('aria-current', 'page');

    // The list finds the account; its row opens the record.
    await expect(page.getByRole('button', { name: `Select a view: ${VIEW_NAME}`, exact: true })).toBeVisible();
    const row = page.getByRole('row').filter({ hasText: accountName });
    await expect(row).toHaveCount(1);
    await row.getByRole('button', { name: accountName }).click();
    await expect(page).toHaveURL(/pagetype=entityrecord/i);
    await expect(page.getByRole('heading', { level: 1, name: accountName })).toBeVisible();

    // A change of the name, saved.
    const name = page.getByRole('textbox', { name: /^Account Name/ });
    await name.fill(editedName);
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled();

    // A reload reads it back from the database.
    await page.reload();
    await expect(page.getByRole('textbox', { name: /^Account Name/ })).toHaveValue(editedName);
    await expect(page.getByRole('heading', { level: 1, name: editedName })).toBeVisible();
  } finally {
    await page.context().close();
  }
  const saved = await json<{ name: string }>(admin, `accounts(${accountId})?$select=name`);
  expect(saved.name).toBe(editedName);
  expect(failures, 'API requests of the shell that failed').toEqual([]);
});
