import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, request, test, type APIRequestContext, type Browser, type Page } from '@playwright/test';
import { ADMIN_OBJECT_ID, mintDevToken, runEnv, uniqueSuffix } from './harness';

/**
 * X.11: a subgrid filtered by a lookup of the form (an xrmverse extension), end to end.
 *
 * Arranged through the Web API: accounts A and B, a contact of each (`parentcustomerid`), an account "Holder" whose parent
 * account (`parentaccountid`, a lookup to account) is A, a public view of `contact`, and a small solution of its own
 * ("Subgrid Filter Fixture": a classic app over `account` and `contact`).
 * Then, in Chromium, as the administrator:
 * - on a new account form, Parent Account goes on General, and a subgrid of contacts with Records "Records matching a
 *   column of this form" (source `parentaccountid`, target `parentcustomerid`) is added; Save. The FormXML has the subgrid
 *   with an empty `RelationshipName`, and the environment's `xrmverse_SubgridFilters` resource has the entry;
 * - the app shows Holder on the new form: the subgrid lists only A's contact; set to B (by script, as `setValue` does) it
 *   lists only B's; cleared, it shows the fill notice.
 * The form, the view, the records and (when the designer created them) the filter resource and the environment value are
 * removed afterwards. What stays behind: the fixture solution with its app.
 *
 * Local only (not in CI).
 */

const env = runEnv();
test.use({ baseURL: env.baseURL });
test.describe.configure({ mode: 'serial' });

const SOLUTION = 'SubgridFilterFixture';
const APP_UNIQUE = 'sf_subgridfilterapp';
const APP_NAME = 'Subgrid Filter Fixture';
const SETTING = 'xrmverse_SubgridFilters';
/** Role template id of Basic User (Xrmd.Security.StandardRoles), also the role id in the root business unit. */
const BASIC_USER = '236750cd-45ae-4939-ab12-b24b920ced93';
const SUBGRID_CLASS = '{E7A81278-8635-4D9E-8D4D-59480B391C5B}';

const webApi = (path: string) => `/api/data/v9.2/${path}`;

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

async function publish(api: APIRequestContext, ...tables: string[]): Promise<void> {
  const entities = tables.map((t) => `<entity>${t}</entity>`).join('');
  const res = await api.post(webApi('PublishXml'), { data: { ParameterXml: `<importexportxml><entities>${entities}</entities></importexportxml>` } });
  expect(res.status(), await res.text()).toBe(204);
}

/** The Subgrid Filter Fixture solution as a zip: a classic app over `account` and `contact`. */
function buildSolutionZip(): Buffer {
  const dir = mkdtempSync(join(tmpdir(), 'xrmverse-subgrid-filter-'));
  try {
    writeFileSync(
      join(dir, 'solution.xml'),
      `<?xml version="1.0" encoding="utf-8"?>
<ImportExportXml version="9.2.24093.200" SolutionPackageVersion="9.2" languagecode="1033" generatedBy="CrmLive" OrganizationVersion="9.2.24093.200" OrganizationSchemaType="Standard" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <SolutionManifest>
    <UniqueName>${SOLUTION}</UniqueName>
    <LocalizedNames><LocalizedName description="${APP_NAME}" languagecode="1033" /></LocalizedNames>
    <Descriptions><Description description="The subgrid filter test solution of the xrmverse E2E." languagecode="1033" /></Descriptions>
    <Version>1.0.0.0</Version>
    <Managed>0</Managed>
    <Publisher>
      <UniqueName>subgridfilterfixture</UniqueName>
      <LocalizedNames><LocalizedName description="${APP_NAME}" languagecode="1033" /></LocalizedNames>
      <Descriptions />
      <EMailAddress xsi:nil="true"></EMailAddress>
      <SupportingWebsiteUrl xsi:nil="true"></SupportingWebsiteUrl>
      <CustomizationPrefix>sf</CustomizationPrefix>
      <CustomizationOptionValuePrefix>27158</CustomizationOptionValuePrefix>
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
        <Area Id="sf_area" ShowGroups="true" IntroducedVersion="7.0.0.0">
          <Titles><Title LCID="1033" Title="Workspace" /></Titles>
          <Group Id="sf_group" IntroducedVersion="7.0.0.0" IsProfile="false">
            <Titles><Title LCID="1033" Title="Customers" /></Titles>
            <SubArea Id="sf_subarea_account" Entity="account" Client="All,Outlook,OutlookLaptopClient,OutlookWorkstationClient,Web" AvailableOffline="true" PassParams="false" Sku="All,OnPremise,Live,SPLA">
              <Titles><Title LCID="1033" Title="Accounts" /></Titles>
            </SubArea>
            <SubArea Id="sf_subarea_contact" Entity="contact" Client="All,Outlook,OutlookLaptopClient,OutlookWorkstationClient,Web" AvailableOffline="true" PassParams="false" Sku="All,OnPremise,Live,SPLA">
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
        <AppModuleComponent type="1" schemaName="account" />
        <AppModuleComponent type="1" schemaName="contact" />
        <AppModuleComponent type="62" schemaName="${APP_UNIQUE}" />
      </AppModuleComponents>
      <AppModuleRoleMaps>
        <Role id="{${BASIC_USER}}" />
      </AppModuleRoleMaps>
      <LocalizedNames><LocalizedName description="${APP_NAME}" languagecode="1033" /></LocalizedNames>
      <Descriptions><Description description="A classic app for the subgrid filter E2E." languagecode="1033" /></Descriptions>
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
      '<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/octet-stream" /></Types>',
    );

    const zip = join(dir, `${SOLUTION}.zip`);
    execFileSync('zip', ['-r', '-X', '-q', zip, 'solution.xml', 'customizations.xml', '[Content_Types].xml'], { cwd: dir });
    return readFileSync(zip);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

/** A fresh browser context signed in as the administrator through the dev sign-in form. */
async function signedIn(browser: Browser, path: string): Promise<Page> {
  const context = await browser.newContext({ baseURL: env.baseURL, viewport: { width: 1440, height: 900 }, colorScheme: 'light' });
  const page = await context.newPage();
  await page.goto(path);
  await page.getByRole('textbox', { name: 'Dev signing key (Auth:DevSigningKey)', exact: true }).fill(env.signingKey);
  await page.getByRole('textbox', { name: 'User object id (oid)', exact: true }).fill(ADMIN_OBJECT_ID);
  await page.getByRole('textbox', { name: 'Display name', exact: true }).fill('Administrator');
  await page.getByRole('button', { name: 'Sign in' }).click();
  return page;
}

/** The cards of the designer page, by their titles. */
const card = (page: Page, title: string) => page.getByRole('region', { name: title, exact: true });

/** The text of a parameter in a `<parameters>` block; a self-closing `<Name/>` reads as ''. */
const param = (xml: string, name: string) => {
  const m = new RegExp(`<${name}(?:>([^<]*)</${name}>|\\s*/>)`).exec(xml);
  return m ? (m[1] ?? '') : undefined;
};

/** The id of the form a designer's POST of `systemforms` created, from its OData-EntityId. */
const formId = (headers: Record<string, string>) => /\(([0-9a-f-]{36})\)$/.exec(headers['odata-entityid'] ?? '')?.[1] ?? '';

/** A column's label: its display name, or its logical name where the E2E seed has no label for it. */
const PARENT = /^(Parent Account|parentaccountid)$/;

let admin: APIRequestContext;
let appId = '';
const sfx = uniqueSuffix();
const ids = { a: '', b: '', holder: '', contactA: '', contactB: '', view: '', form: '' };
/** The environment's `xrmverse_SubgridFilters` row and resource the designer created (removed afterwards), if any. */
const made = { settingRow: '', resource: '' };
let settingRowBefore = false;
const VIEW_NAME = `X.11 contacts ${sfx}`;
const FORM_NAME = `X.11 account ${sfx}`;
const ACCOUNT_A = `E2E ${sfx} Alpha`;
const ACCOUNT_B = `E2E ${sfx} Beta`;
const HOLDER = `E2E ${sfx} Holder`;
const CONTACT_A = { firstname: 'Ann', lastname: `X11A ${sfx}` };
const CONTACT_B = { firstname: 'Bob', lastname: `X11B ${sfx}` };

const recordUrl = (etn: string, form: string, id: string) => `/main.aspx?appid=${appId}&pagetype=entityrecord&etn=${etn}&formid=${form}&id=${id}`;

/** The environment's `organizationsetting` rows of `xrmverse_SubgridFilters`. */
async function settingRows(): Promise<{ organizationsettingid: string; value?: string | null }[]> {
  const def = await json<{ value: { settingdefinitionid: string }[] }>(admin, `settingdefinitions?$select=settingdefinitionid&$filter=uniquename eq '${SETTING}'`);
  expect(def.value, `the ${SETTING} definition`).toHaveLength(1);
  return (
    await json<{ value: { organizationsettingid: string; value?: string | null }[] }>(
      admin,
      `organizationsettings?$select=organizationsettingid,value&$filter=_settingdefinitionid_value eq ${def.value[0].settingdefinitionid}`,
    )
  ).value;
}

test.beforeAll(async () => {
  test.setTimeout(600_000);
  const token = mintDevToken(env.signingKey, `${env.baseURL}/`, ADMIN_OBJECT_ID, 'Administrator');
  admin = await request.newContext({
    baseURL: env.baseURL,
    extraHTTPHeaders: { Authorization: `Bearer ${token}`, Accept: 'application/json', 'OData-Version': '4.0' },
  });

  await importSolution(admin, buildSolutionZip());
  const apps = await json<{ value: { appmoduleid: string }[] }>(admin, `appmodules?$select=appmoduleid&$filter=uniquename eq '${APP_UNIQUE}'`);
  expect(apps.value).toHaveLength(1);
  appId = apps.value[0].appmoduleid;
  settingRowBefore = (await settingRows()).length > 0;

  ids.a = await create(admin, 'accounts', { name: ACCOUNT_A });
  ids.b = await create(admin, 'accounts', { name: ACCOUNT_B });
  ids.holder = await create(admin, 'accounts', { name: HOLDER, 'parentaccountid@odata.bind': `/accounts(${ids.a})` });
  // OpenProject365's plug-in on the create of a contact reads a table xrmverse does not have (known issue #172): when an
  // earlier spec of this run imported it, its step is off while the contacts are made, and on again after.
  const steps = await json<{ value: { sdkmessageprocessingstepid: string; statecode: number }[] }>(
    admin,
    `sdkmessageprocessingsteps?$select=sdkmessageprocessingstepid,statecode&$filter=startswith(name,'SupplierContactRole: Create of contact')`,
  );
  const enabled = steps.value.filter((s) => s.statecode === 0).map((s) => s.sdkmessageprocessingstepid);
  const disabled: string[] = [];
  const setSteps = async (state: { statecode: number; statuscode: number }) => {
    const off = state.statecode === 1;
    for (const id of off ? enabled : [...disabled]) {
      const res = await admin.patch(webApi(`sdkmessageprocessingsteps(${id})`), { data: state });
      expect(res.status(), await res.text()).toBe(204);
      if (off) disabled.push(id);
    }
  };
  try {
    await setSteps({ statecode: 1, statuscode: 2 });
    ids.contactA = await create(admin, 'contacts', { ...CONTACT_A, 'parentcustomerid_account@odata.bind': `/accounts(${ids.a})` });
    ids.contactB = await create(admin, 'contacts', { ...CONTACT_B, 'parentcustomerid_account@odata.bind': `/accounts(${ids.b})` });
  } finally {
    await setSteps({ statecode: 0, statuscode: 1 });
  }
  // The seed has no public view of `contact`; a subgrid needs one.
  ids.view = await create(admin, 'savedqueries', {
    name: VIEW_NAME,
    returnedtypecode: 'contact',
    querytype: 0,
    fetchxml:
      '<fetch version="1.0" mapping="logical"><entity name="contact"><attribute name="fullname" /><attribute name="contactid" />' +
      '<order attribute="fullname" descending="false" /></entity></fetch>',
    layoutxml: '<grid name="resultset" object="2" jump="fullname" select="1" icon="1" preview="1"><row name="result" id="contactid"><cell name="fullname" width="300" /></row></grid>',
  });
  await publish(admin, 'account', 'contact');
});

test.afterAll(async () => {
  if (!admin) return;
  if (ids.form) await admin.delete(webApi(`systemforms(${ids.form})`));
  if (ids.view) await admin.delete(webApi(`savedqueries(${ids.view})`));
  await publish(admin, 'account', 'contact').catch(() => undefined);
  if (made.settingRow) await admin.delete(webApi(`organizationsettings(${made.settingRow})`));
  for (const [set, id] of [
    ['contacts', ids.contactA],
    ['contacts', ids.contactB],
    ['accounts', ids.holder],
    ['accounts', ids.a],
    ['accounts', ids.b],
    ['webresourceset', made.resource],
  ] as const) {
    if (!id) continue;
    const res = await admin.delete(webApi(`${set}(${id})`));
    expect([204, 404], `DELETE ${set}(${id}): ${await res.text()}`).toContain(res.status());
  }
  await admin.dispose();
});

test('the designer adds a subgrid of contacts matching the form\'s parent account', async ({ browser }) => {
  const page = await signedIn(browser, '/manage/tables/account/forms/new');
  try {
    await page.getByRole('textbox', { name: /^Name/ }).fill(FORM_NAME);
    const structure = card(page, 'Structure');
    const general = structure.getByRole('region', { name: 'Section General', exact: true });

    // The source column must be on the form: Parent Account goes on General.
    await general.getByRole('button', { name: /^Add field to / }).click();
    const fields = page.getByRole('dialog', { name: 'Add field' });
    await fields.getByRole('textbox', { name: 'Search columns' }).fill('parentaccountid');
    await fields.getByRole('button', { name: PARENT }).click();
    await expect(fields).toHaveCount(0);
    await expect(general.getByRole('button', { name: PARENT })).toBeVisible();

    await general.getByRole('button', { name: 'General', exact: true }).click();
    await structure.getByRole('button', { name: 'Add subgrid', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Add subgrid to General' });
    await dialog.getByRole('radio', { name: /^Records matching a column of this form/ }).check();
    await dialog.getByRole('combobox', { name: 'Table', exact: true }).selectOption('contact');
    await dialog.getByRole('combobox', { name: 'Column of this form', exact: true }).selectOption('parentaccountid');
    const target = dialog.getByRole('combobox', { name: /^Matches column of / });
    await expect(target.locator('option[value="parentcustomerid"]')).toHaveCount(1);
    await target.selectOption('parentcustomerid');
    await dialog.getByRole('combobox', { name: 'Default view' }).selectOption({ label: VIEW_NAME });
    await dialog.getByRole('textbox', { name: /^Label/ }).fill('Contacts of the parent');
    await expect(dialog.getByText('xrmverse extension. Exported to Dataverse, this subgrid lists all records of the view.')).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'Add', exact: true })).toBeEnabled();
    await dialog.getByRole('button', { name: 'Add', exact: true }).click();
    await expect(dialog).toHaveCount(0);
    await expect(general.getByRole('button', { name: /^Contacts of the parent/ })).toBeVisible();

    const saved = page.waitForResponse((r) => r.url().endsWith('/systemforms') && r.request().method() === 'POST');
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    const response = await saved;
    expect(response.status()).toBe(204);
    ids.form = formId(response.headers());
    expect(ids.form).not.toBe('');
    await expect(page).toHaveURL(new RegExp(ids.form));
    await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeEnabled();
  } finally {
    await page.context().close();
  }

  // The FormXML keeps a valid Dataverse subgrid: no relationship ("All records").
  const xml = (await json<{ formxml: string }>(admin, `systemforms(${ids.form})?$select=formxml`)).formxml;
  const open = [...xml.matchAll(/<control\s[^>]*>/g)].map((m) => m[0]).find((t) => t.includes(`classid="${SUBGRID_CLASS}"`));
  expect(open, xml).toBeDefined();
  const start = xml.indexOf(open!);
  const grid = xml.slice(start, xml.indexOf('</control>', start) + '</control>'.length);
  expect(param(grid, 'RelationshipName')).toBe('');
  expect(param(grid, 'TargetEntityType')).toBe('contact');
  const controlId = /\bid="([^"]+)"/.exec(open!)![1].trim().toLowerCase();

  // The environment's value names the filter resource, whose entry has the form, the control and the two columns.
  const rows = await settingRows();
  expect(rows).toHaveLength(1);
  if (!settingRowBefore) made.settingRow = rows[0].organizationsettingid;
  const names = JSON.parse(rows[0].value ?? '') as string[];
  expect(Array.isArray(names) && names.length > 0, rows[0].value ?? '').toBe(true);
  const resources = await json<{ value: { webresourceid: string; name: string; content: string }[] }>(
    admin,
    `webresourceset?$select=webresourceid,name,content&$filter=name eq '${names[0]}'`,
  );
  expect(resources.value).toHaveLength(1);
  if (!settingRowBefore) {
    expect(names[0]).toMatch(/^[a-z0-9]+_\/xrmverse\/subgridfilters\.json$/);
    made.resource = resources.value[0].webresourceid;
  }
  const doc = JSON.parse(Buffer.from(resources.value[0].content, 'base64').toString('utf8')) as {
    forms: Record<string, Record<string, { source: string; target: string }>>;
  };
  expect(doc.forms[ids.form.toLowerCase()]?.[controlId], JSON.stringify(doc)).toEqual({ source: 'parentaccountid', target: 'parentcustomerid' });
});

test('the app lists the contacts of the parent account and follows its changes', async ({ browser }) => {
  expect(ids.form).not.toBe('');
  const app = await signedIn(browser, recordUrl('account', ids.form, ids.holder));
  try {
    await expect(app.getByRole('heading', { level: 1, name: HOLDER })).toBeVisible();
    const grid = app.getByRole('grid', { name: VIEW_NAME });
    const rowA = grid.getByRole('row', { name: new RegExp(CONTACT_A.lastname) });
    const rowB = grid.getByRole('row', { name: new RegExp(CONTACT_B.lastname) });

    // Parent account A: only A's contact.
    await expect(rowA).toBeVisible();
    await expect(rowB).toHaveCount(0);

    // Set to B, as a user or a script does (`setValue` fires the change): only B's contact.
    await app.evaluate(
      ([id, name]) => {
        const xrm = (window as unknown as { Xrm: { Page: { getAttribute: (n: string) => { setValue: (v: unknown) => void } } } }).Xrm;
        xrm.Page.getAttribute('parentaccountid').setValue([{ id, name, entityType: 'account' }]);
      },
      [ids.b, ACCOUNT_B],
    );
    await expect(grid.getByRole('row', { name: new RegExp(CONTACT_B.lastname) })).toBeVisible();
    await expect(grid.getByRole('row', { name: new RegExp(CONTACT_A.lastname) })).toHaveCount(0);

    // Cleared: no rows, the fill notice.
    await app.evaluate(() => {
      const xrm = (window as unknown as { Xrm: { Page: { getAttribute: (n: string) => { setValue: (v: unknown) => void } } } }).Xrm;
      xrm.Page.getAttribute('parentaccountid').setValue(null);
    });
    await expect(app.getByText(/^Fill .+ to see the records\.$/)).toBeVisible();
    await expect(app.getByRole('row', { name: new RegExp(`${CONTACT_A.lastname}|${CONTACT_B.lastname}`) })).toHaveCount(0);
  } finally {
    await app.context().close();
  }
});
