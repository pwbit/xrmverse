import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { expect, request, test, type APIRequestContext, type Browser, type Locator, type Page } from '@playwright/test';
import { ADMIN_OBJECT_ID, mintDevToken, repoDir, runEnv, uniqueSuffix } from './harness';

/**
 * Plan 3.6 UI-2g, task 9: the form designer's controls and events, end to end.
 *
 * Arranged through the Web API: the run's account, a contact whose company (`parentcustomerid`) is that account, an HTML
 * web resource, a JavaScript web resource whose `onLoad` sets a form notification, a quick view form of `account` (type 6)
 * and a small solution of its own ("Form Controls Fixture": a classic app over `account` and `contact`, and a public text
 * custom (PCF) control of our own, `fc_Fc.TextBadge`, whose bound property `value` (display name "Shown text") takes a
 * single line of text and which draws "Badge: {value}"). A second custom control is one of the OpenProject365 export, as
 * in model-driven-pcf.spec.ts: `op365_OP365.QuoteComparison`, bound to the whole number column `op365_projectcount` that
 * the export adds to `account`.
 * Then, in Chromium, as the administrator:
 * - on a new account form: TextBadge goes on Account Name; a field is dragged to another section (the insertion line shows
 *   under the pointer); QuoteComparison, a contact subgrid (related records) and a web resource are added; the JavaScript
 *   library and an OnLoad handler are added in the Events card; Save. The FormXML read back has the subgrid, the web
 *   resource, the control descriptions and the library and handler (G2, G4, G5, G6);
 * - on a new contact form: a quick view of the company (account) is added (G3); Save;
 * - the app shows the account on the new form with the contact in the subgrid, the web resource's frame, both custom
 *   controls and the OnLoad notification, and the contact on its form with the account in the quick view.
 * The forms, the records and the web resources are removed afterwards. What stays behind: the fixture solution with its
 * app and the TextBadge control (and OpenProject365, which other specs share).
 *
 * Local only (not in CI): it needs the OpenProject365 export. Set XRMD_E2E_SCREENSHOTS to a folder to keep screenshots
 * (device scale factor 2): the drag, the add-subgrid dialog, the custom control panel and the Events card.
 */

const env = runEnv();
test.use({ baseURL: env.baseURL });
// The tests share the forms made by the first ones: after a failure the rest are skipped, not rerun in a new worker.
test.describe.configure({ mode: 'serial' });

const screenshots = process.env.XRMD_E2E_SCREENSHOTS;
const OPENPROJECT365 = resolve(repoDir, 'tests', 'fixtures', 'solutions', 'RealExports', 'OpenProject365_1_0_1_1.zip');
const QUOTES = 'op365_OP365.QuoteComparison';
/** The public text custom control of the fixture solution. */
const BADGE = 'fc_Fc.TextBadge';
const SOLUTION = 'FormControlsFixture';
const APP_UNIQUE = 'fc_formcontrolsapp';
const APP_NAME = 'Form Controls Fixture';
/** Role template id of Basic User (Xrmd.Security.StandardRoles), also the role id in the root business unit. */
const BASIC_USER = '236750cd-45ae-4939-ab12-b24b920ced93';
const CLASS = {
  subgrid: '{E7A81278-8635-4D9E-8D4D-59480B391C5B}',
  quickView: '{5C5600E0-1D6E-4205-A272-BE80DA87FD42}',
  webResource: '{9FDF5F91-88B1-47F4-AD53-C11EFC01A01D}',
};

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

async function importSolution(api: APIRequestContext, zip: Buffer, overwrite: boolean): Promise<void> {
  const res = await api.post(webApi('ImportSolution'), {
    data: { CustomizationFile: zip.toString('base64'), ImportJobId: randomUUID(), OverwriteUnmanagedCustomizations: overwrite, PublishWorkflows: false },
    timeout: 300_000,
  });
  expect(res.status(), await res.text()).toBe(204);
}

async function publish(api: APIRequestContext, ...tables: string[]): Promise<void> {
  const entities = tables.map((t) => `<entity>${t}</entity>`).join('');
  const res = await api.post(webApi('PublishXml'), { data: { ParameterXml: `<importexportxml><entities>${entities}</entities></importexportxml>` } });
  expect(res.status(), await res.text()).toBe(204);
}

/** The manifest of TextBadge, in the shape of tests/Xrmd.Integration.Tests/CustomControlImportTests.cs. */
const BADGE_MANIFEST = `<?xml version="1.0" encoding="utf-8"?>
<manifest>
  <control namespace="Fc" constructor="TextBadge" version="1.0.0" control-type="standard">
    <property name="value" display-name-key="Value_Display" of-type="SingleLine.Text" usage="bound" required="true" />
    <resources>
      <code path="bundle.js" order="1" />
      <resx path="strings/TextBadge.1033.resx" version="1.0.0" />
    </resources>
  </control>
</manifest>
`;

/** TextBadge's bundle: a span that shows "Badge: " and the bound value. */
const BADGE_BUNDLE = `class TextBadge {
  init(context, notifyOutputChanged, state, container) {
    this.span = document.createElement('span');
    container.appendChild(this.span);
  }
  updateView(context) {
    this.span.textContent = \`Badge: \${context.parameters.value.raw ?? ''}\`;
  }
  getOutputs() {
    return {};
  }
  destroy() {}
}
ComponentFramework.registerControl('Fc.TextBadge', TextBadge);
`;

/** TextBadge's English strings: the display name of `value`. */
const BADGE_RESX = '<root><data name="Value_Display"><value>Shown text</value></data></root>\n';

/** The Form Controls Fixture solution as a zip: a classic app over `account` and `contact`, and the TextBadge control. */
function buildSolutionZip(): Buffer {
  const dir = mkdtempSync(join(tmpdir(), 'xrmverse-form-controls-'));
  try {
    writeFileSync(
      join(dir, 'solution.xml'),
      `<?xml version="1.0" encoding="utf-8"?>
<ImportExportXml version="9.2.24093.200" SolutionPackageVersion="9.2" languagecode="1033" generatedBy="CrmLive" OrganizationVersion="9.2.24093.200" OrganizationSchemaType="Standard" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <SolutionManifest>
    <UniqueName>${SOLUTION}</UniqueName>
    <LocalizedNames><LocalizedName description="${APP_NAME}" languagecode="1033" /></LocalizedNames>
    <Descriptions><Description description="The form designer controls test solution of the xrmverse E2E." languagecode="1033" /></Descriptions>
    <Version>1.0.0.0</Version>
    <Managed>0</Managed>
    <Publisher>
      <UniqueName>formcontrolsfixture</UniqueName>
      <LocalizedNames><LocalizedName description="${APP_NAME}" languagecode="1033" /></LocalizedNames>
      <Descriptions />
      <EMailAddress xsi:nil="true"></EMailAddress>
      <SupportingWebsiteUrl xsi:nil="true"></SupportingWebsiteUrl>
      <CustomizationPrefix>fc</CustomizationPrefix>
      <CustomizationOptionValuePrefix>27154</CustomizationOptionValuePrefix>
      <Addresses />
    </Publisher>
    <RootComponents>
      <RootComponent type="62" schemaName="${APP_UNIQUE}" behavior="0" />
      <RootComponent type="80" schemaName="${APP_UNIQUE}" behavior="0" />
      <RootComponent type="66" schemaName="${BADGE}" behavior="0" />
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
  <CustomControls><CustomControl><Name>${BADGE}</Name><FileName>/Controls/${BADGE}/ControlManifest.xml</FileName></CustomControl></CustomControls>
  <SolutionPluginAssemblies />
  <SdkMessageProcessingSteps />
  <AppModuleSiteMaps>
    <AppModuleSiteMap>
      <SiteMapUniqueName>${APP_UNIQUE}</SiteMapUniqueName>
      <SiteMap IntroducedVersion="7.0.0.0">
        <Area Id="fc_area" ShowGroups="true" IntroducedVersion="7.0.0.0">
          <Titles><Title LCID="1033" Title="Workspace" /></Titles>
          <Group Id="fc_group" IntroducedVersion="7.0.0.0" IsProfile="false">
            <Titles><Title LCID="1033" Title="Customers" /></Titles>
            <SubArea Id="fc_subarea_account" Entity="account" Client="All,Outlook,OutlookLaptopClient,OutlookWorkstationClient,Web" AvailableOffline="true" PassParams="false" Sku="All,OnPremise,Live,SPLA">
              <Titles><Title LCID="1033" Title="Accounts" /></Titles>
            </SubArea>
            <SubArea Id="fc_subarea_contact" Entity="contact" Client="All,Outlook,OutlookLaptopClient,OutlookWorkstationClient,Web" AvailableOffline="true" PassParams="false" Sku="All,OnPremise,Live,SPLA">
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
      <Descriptions><Description description="A classic app for the form designer controls E2E." languagecode="1033" /></Descriptions>
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
      '<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/octet-stream" />' +
        '<Default Extension="js" ContentType="application/octet-stream" /><Default Extension="resx" ContentType="application/octet-stream" /></Types>',
    );

    const control = join(dir, 'Controls', BADGE);
    mkdirSync(join(control, 'strings'), { recursive: true });
    writeFileSync(join(control, 'ControlManifest.xml'), BADGE_MANIFEST);
    writeFileSync(join(control, 'bundle.js'), BADGE_BUNDLE);
    writeFileSync(join(control, 'strings', 'TextBadge.1033.resx'), BADGE_RESX);

    const zip = join(dir, `${SOLUTION}.zip`);
    execFileSync('zip', ['-r', '-X', '-q', zip, 'solution.xml', 'customizations.xml', '[Content_Types].xml', 'Controls'], { cwd: dir });
    return readFileSync(zip);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

/** A fresh browser context (device scale factor 2) signed in as the administrator through the dev sign-in form. */
async function signedIn(browser: Browser, path: string): Promise<Page> {
  const context = await browser.newContext({ baseURL: env.baseURL, viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2, colorScheme: 'light' });
  const page = await context.newPage();
  await page.goto(path);
  await page.getByRole('textbox', { name: 'Dev signing key (Auth:DevSigningKey)', exact: true }).fill(env.signingKey);
  await page.getByRole('textbox', { name: 'User object id (oid)', exact: true }).fill(ADMIN_OBJECT_ID);
  await page.getByRole('textbox', { name: 'Display name', exact: true }).fill('Administrator');
  await page.getByRole('button', { name: 'Sign in' }).click();
  return page;
}

/** A screenshot of a region, or of the page (device scale factor 2). */
async function shoot(target: Page | Locator, name: string): Promise<void> {
  if (!screenshots) return;
  mkdirSync(screenshots, { recursive: true });
  const path = join(screenshots, `${name}.png`);
  if ('page' in target) await target.screenshot({ path, animations: 'disabled' });
  else await target.screenshot({ path, animations: 'disabled' });
}

/** The cards of the designer page, by their titles. */
const card = (page: Page, title: string) => page.getByRole('region', { name: title, exact: true });

/** The opening tag of the first `<tag ...>` in `xml` that has all `attributes` (in any order). */
function openTag(xml: string, tag: string, ...attributes: string[]): string | undefined {
  return [...xml.matchAll(new RegExp(`<${tag}\\s[^>]*>`, 'g'))].map((m) => m[0]).find((t) => attributes.every((a) => t.includes(a)));
}

/** The whole `<control …>…</control>` element that has all `attributes` in its opening tag. */
function element(xml: string, tag: string, ...attributes: string[]): string {
  const open = openTag(xml, tag, ...attributes);
  expect(open, `<${tag} ${attributes.join(' ')}> in ${xml}`).toBeDefined();
  const start = xml.indexOf(open!);
  if (open!.endsWith('/>')) return open!;
  const end = xml.indexOf(`</${tag}>`, start);
  return xml.slice(start, end + tag.length + 3);
}

/** The text of a parameter in a `<parameters>` block; a self-closing `<Name/>` reads as ''. */
const param = (xml: string, name: string) => {
  const m = new RegExp(`<${name}(?:>([^<]*)</${name}>|\\s*/>)`).exec(xml);
  return m ? (m[1] ?? '') : undefined;
};

/** The fallback notice of a custom control and the script error notice, either of which means a control did not run. */
const notices = (page: Page) => page.getByText(/^Custom control '.*' could not be loaded|^Script error/);

let admin: APIRequestContext;
let appId = '';
const sfx = uniqueSuffix();
const ids = { account: '', contact: '', html: '', js: '', view: '', quickView: '', accountForm: '', contactForm: '', madeQuickView: '' };
const VIEW_NAME = `UI-2g contacts ${sfx}`;
const ACCOUNT_NAME = `E2E ${sfx} Controls`;
const CONTACT = { firstname: 'Ada', lastname: `UI2G ${sfx}` };
const HTML_NAME = `fc_/ui2g/${sfx}/panel.html`;
const HTML_DISPLAY = `UI-2g panel ${sfx}`;
const JS_NAME = `fc_/ui2g/${sfx}/form.js`;
const JS = "function onLoad(ctx){ ctx.getFormContext().ui.setFormNotification('UI2G','INFO','n'); }";
const QUICK_VIEW_NAME = `UI-2g account card ${sfx}`;
const ACCOUNT_FORM = `UI-2g account ${sfx}`;
const CONTACT_FORM = `UI-2g contact ${sfx}`;
const MADE_QUICK_VIEW = `UI quick view ${sfx}`;
/** The one-to-many relationship of `contact.parentcustomerid` to `account`, read from the server. */
let relationship = '';

/** A column's label: its display name, or its logical name where the E2E seed has no label for it. */
const label = (display: string, logical: string) => new RegExp(`^(${display}|${logical})$`);
const PHONE = label('Main Phone', 'telephone1');
const PROJECTS = label('Project Count', 'op365_projectcount');
const NAME = label('Account Name', 'name');

/** The id of the form a designer's POST of `systemforms` created, from its OData-EntityId. */
const formId = (headers: Record<string, string>) => /\(([0-9a-f-]{36})\)$/.exec(headers['odata-entityid'] ?? '')?.[1] ?? '';

const QUICK_VIEW_XML =
  '<form><tabs><tab name="qv_tab" id="{2b7c41d0-6e3f-4a58-9b12-7c3d4e5f6a01}" expanded="true" showlabel="false" verticallayout="true">' +
  '<labels><label description="Account" languagecode="1033"/></labels><columns><column width="100%"><sections>' +
  '<section name="qv_section" id="{2b7c41d0-6e3f-4a58-9b12-7c3d4e5f6a02}" showlabel="false" showbar="false" columns="1"><labels><label description="Account" languagecode="1033"/></labels>' +
  '<rows><row><cell id="{2b7c41d0-6e3f-4a58-9b12-7c3d4e5f6a03}" showlabel="true"><labels><label description="Account Name" languagecode="1033"/></labels>' +
  '<control id="name" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="name"/></cell></row></rows></section></sections></column></columns></tab></tabs></form>';

const b64 = (s: string) => Buffer.from(s, 'utf8').toString('base64');
const recordUrl = (etn: string, form: string, id: string) => `/main.aspx?appid=${appId}&pagetype=entityrecord&etn=${etn}&formid=${form}&id=${id}`;

test.beforeAll(async () => {
  test.setTimeout(600_000);
  const token = mintDevToken(env.signingKey, `${env.baseURL}/`, ADMIN_OBJECT_ID, 'Administrator');
  admin = await request.newContext({
    baseURL: env.baseURL,
    extraHTTPHeaders: { Authorization: `Bearer ${token}`, Accept: 'application/json', 'OData-Version': '4.0' },
  });

  // OpenProject365 (its custom controls, and `op365_projectcount` on account), unless an earlier spec of this run imported it.
  const op365 = await json<{ value: unknown[] }>(admin, `appmodules?$select=appmoduleid&$filter=uniquename eq 'op365_OpenProject365'`);
  if (op365.value.length === 0) await importSolution(admin, readFileSync(OPENPROJECT365), false);
  const controls = await json<{ value: { name: string }[] }>(admin, `customcontrols?$select=name&$filter=name eq '${QUOTES}'`);
  expect(controls.value).toHaveLength(1);

  await importSolution(admin, buildSolutionZip(), true);
  const badge = await json<{ value: { name: string }[] }>(admin, `customcontrols?$select=name&$filter=name eq '${BADGE}'`);
  expect(badge.value).toHaveLength(1);
  const apps = await json<{ value: { appmoduleid: string }[] }>(admin, `appmodules?$select=appmoduleid&$filter=uniquename eq '${APP_UNIQUE}'`);
  expect(apps.value).toHaveLength(1);
  appId = apps.value[0].appmoduleid;

  const rels = await json<{ value: { SchemaName: string; ReferencingEntity?: string; ReferencingAttribute?: string; ReferencedEntity?: string }[] }>(
    admin,
    'RelationshipDefinitions?$select=SchemaName,ReferencingEntity,ReferencingAttribute,ReferencedEntity',
  );
  relationship = rels.value.find((r) => r.ReferencingEntity === 'contact' && r.ReferencingAttribute === 'parentcustomerid' && r.ReferencedEntity === 'account')!
    .SchemaName;
  expect(relationship).toBeTruthy();

  ids.account = await create(admin, 'accounts', { name: ACCOUNT_NAME });
  // OpenProject365's plug-in on the create of a contact reads `powerpagecomponent`, a table xrmverse does not have (known
  // issue #172): its step is off while the contact is made, and on again after.
  const steps = await json<{ value: { sdkmessageprocessingstepid: string; statecode: number }[] }>(
    admin,
    `sdkmessageprocessingsteps?$select=sdkmessageprocessingstepid,statecode&$filter=startswith(name,'SupplierContactRole: Create of contact')`,
  );
  const enabled = steps.value.filter((s) => s.statecode === 0).map((s) => s.sdkmessageprocessingstepid);
  /** The steps actually disabled: only those are enabled again, even when disabling one of them failed. */
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
    ids.contact = await create(admin, 'contacts', { ...CONTACT, 'parentcustomerid_account@odata.bind': `/accounts(${ids.account})` });
  } finally {
    await setSteps({ statecode: 0, statuscode: 1 });
  }
  ids.html = await create(admin, 'webresourceset', {
    name: HTML_NAME,
    displayname: HTML_DISPLAY,
    webresourcetype: 1,
    content: b64('<!doctype html><html><body><p>UI-2g web resource</p></body></html>'),
  });
  ids.js = await create(admin, 'webresourceset', { name: JS_NAME, displayname: 'UI-2g form script', webresourcetype: 3, content: b64(JS) });
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
  ids.quickView = await create(admin, 'systemforms', { name: QUICK_VIEW_NAME, objecttypecode: 'account', type: 6, formxml: QUICK_VIEW_XML });
  await publish(admin, 'account', 'contact');
});

test.afterAll(async () => {
  if (!admin) return;
  for (const id of [ids.accountForm, ids.contactForm, ids.quickView, ids.madeQuickView]) if (id) await admin.delete(webApi(`systemforms(${id})`));
  if (ids.view) await admin.delete(webApi(`savedqueries(${ids.view})`));
  await publish(admin, 'account', 'contact').catch(() => undefined);
  for (const [set, id] of [
    ['contacts', ids.contact],
    ['accounts', ids.account],
    ['webresourceset', ids.html],
    ['webresourceset', ids.js],
  ] as const) {
    if (!id) continue;
    const res = await admin.delete(webApi(`${set}(${id})`));
    expect([204, 404], `DELETE ${set}(${id}): ${await res.text()}`).toContain(res.status());
  }
  await admin.dispose();
});

test('an account form gets a dragged field, a subgrid, a web resource, a custom control and an OnLoad handler', async ({ browser }) => {
  const page = await signedIn(browser, '/manage/tables/account/forms/new');
  try {
    await page.getByRole('textbox', { name: /^Name/ }).fill(ACCOUNT_FORM);
    const structure = card(page, 'Structure');
    const selected = card(page, 'Selected item');
    await expect(structure.getByRole('tab')).toHaveCount(1);

    // The first section ("General") takes Main Phone; a second section, "Details", takes Project Count.
    const addField = async (section: Locator, search: string, pick: RegExp) => {
      await section.getByRole('button', { name: /^Add field to / }).click();
      const dialog = page.getByRole('dialog', { name: 'Add field' });
      await dialog.getByRole('textbox', { name: 'Search columns' }).fill(search);
      await dialog.getByRole('button', { name: pick }).click();
      await expect(dialog).toHaveCount(0);
      await expect(section.getByRole('button', { name: pick })).toBeVisible();
    };
    const general = structure.getByRole('region', { name: 'Section General', exact: true });
    await addField(general, 'telephone1', PHONE);
    await structure.getByRole('button', { name: 'Add section to column 1', exact: true }).click();
    const sections = structure.getByRole('group', { name: 'Column 1', exact: true }).getByRole('region');
    await expect(sections).toHaveCount(2);
    await sections.nth(1).getByRole('button').first().click();
    await selected.getByRole('textbox', { name: 'Label' }).fill('Details');
    const details = structure.getByRole('region', { name: 'Section Details', exact: true });
    await addField(details, 'op365_projectcount', PROJECTS);

    // Drag Main Phone from General to before Project Count in Details (G1): the insertion line shows while the pointer is
    // over the target; after the drop the phone is in Details, selected.
    // A cell's drop target is its own box (A1: pointer-only, no role or name), found from the cell's button.
    const cellBox = (section: Locator, name: RegExp) =>
      section.locator('[data-drop^="before:"]').filter({ has: page.getByRole('button', { name }) });
    const phoneCell = cellBox(general, PHONE);
    const target = cellBox(details, PROJECTS);
    // Both cells in the viewport: the mouse moves in viewport coordinates.
    await general.scrollIntoViewIfNeeded();
    await general.evaluate((el) => el.scrollIntoView({ block: 'start' }));
    const from = (await phoneCell.getByRole('button', { name: PHONE }).boundingBox())!;
    const to = (await target.boundingBox())!;
    await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
    await page.mouse.down();
    await page.mouse.move(from.x + from.width / 2 + 10, from.y + from.height / 2 + 10, { steps: 3 });
    await page.mouse.move(to.x + 12, to.y + to.height / 2, { steps: 12 });
    await expect(target).toHaveAttribute('data-drop-over', 'true');
    await shoot(structure.getByRole('group', { name: 'Column 1', exact: true }), 'ui-2g-drag');
    await page.mouse.up();
    await expect(details.getByRole('button', { name: PHONE })).toBeVisible();
    await expect(general.getByRole('button', { name: PHONE })).toHaveCount(0);
    await expect(details.getByRole('button', { name: PHONE })).toHaveAttribute('aria-pressed', 'true');

    // The custom control on Project Count (G5): its bound property `value`, labelled by its display name "Responses" (A3),
    // defaults to the cell's column.
    await details.getByRole('button', { name: PROJECTS }).click();
    await selected.getByRole('combobox', { name: 'Custom control' }).selectOption(QUOTES);
    await expect(selected.getByRole('combobox', { name: /^Responses/ })).toHaveValue('op365_projectcount');
    await shoot(selected, 'ui-2g-custom-control');

    // The public text control on Account Name in General (G5): its bound property, labelled by its display name (A3),
    // defaults to the cell's column.
    await general.getByRole('button', { name: NAME }).click();
    await selected.getByRole('combobox', { name: 'Custom control' }).selectOption(BADGE);
    await expect(selected.getByRole('combobox', { name: /^Shown text/ })).toHaveValue('name');

    // A subgrid of the account's contacts (G2), added to Details.
    await details.getByRole('button', { name: 'Details', exact: true }).click();
    await structure.getByRole('button', { name: 'Add subgrid', exact: true }).click();
    const subgrid = page.getByRole('dialog', { name: 'Add subgrid to Details' });
    await subgrid.getByRole('combobox', { name: 'Relationship' }).selectOption(relationship);
    await expect(subgrid.getByRole('combobox', { name: 'Default view' })).not.toHaveValue('');
    await expect(subgrid.getByRole('textbox', { name: /^Label/ })).not.toHaveValue('');
    await subgrid.getByRole('textbox', { name: /^Label/ }).fill('Contacts');
    await expect(subgrid.getByRole('button', { name: 'Add', exact: true })).toBeEnabled();
    await shoot(subgrid, 'ui-2g-add-subgrid');
    await subgrid.getByRole('button', { name: 'Add', exact: true }).click();
    await expect(subgrid).toHaveCount(0);
    await expect(details.getByRole('button', { name: /^Contacts/ })).toBeVisible();

    // A web resource (G4), added to the section of the selected cell (the new subgrid).
    await structure.getByRole('button', { name: 'Add web resource', exact: true }).click();
    const resource = page.getByRole('dialog', { name: 'Add web resource to Details' });
    await resource.getByRole('combobox', { name: 'Web resource' }).selectOption(ids.html);
    await expect(resource.getByRole('textbox', { name: /^Label/ })).toHaveValue(HTML_DISPLAY);
    await resource.getByRole('button', { name: 'Add', exact: true }).click();
    await expect(resource).toHaveCount(0);
    await expect(details.getByRole('button', { name: new RegExp(`^${HTML_DISPLAY}`) })).toBeVisible();

    // The library and an OnLoad handler that gets the execution context (G6).
    const events = card(page, 'Events');
    const picker = events.getByRole('combobox', { name: 'JavaScript web resource' });
    await picker.focus();
    await expect(picker.locator(`option[value="${JS_NAME}"]`)).toHaveCount(1);
    await picker.selectOption(JS_NAME);
    await events.getByRole('button', { name: 'Add library', exact: true }).click();
    await expect(events.getByRole('listitem').filter({ hasText: JS_NAME })).toBeVisible();
    const onLoad = events.getByRole('group', { name: 'On load', exact: true });
    const handler = onLoad.getByRole('group', { name: 'New handler', exact: true });
    await handler.getByRole('textbox', { name: /^Function name/ }).fill('onLoad');
    await handler.getByRole('switch', { name: 'Pass the execution context' }).click();
    await handler.getByRole('button', { name: 'Add handler', exact: true }).click();
    await expect(onLoad.getByRole('group', { name: 'Handler onLoad', exact: true })).toBeVisible();
    await events.scrollIntoViewIfNeeded();
    await shoot(events, 'ui-2g-events');

    const saved = page.waitForResponse((r) => r.url().endsWith('/systemforms') && r.request().method() === 'POST');
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    const accountSaved = await saved;
    expect(accountSaved.status()).toBe(204);
    ids.accountForm = formId(accountSaved.headers());
    expect(ids.accountForm).not.toBe('');
    await expect(page).toHaveURL(new RegExp(ids.accountForm));
    await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeEnabled();
  } finally {
    await page.context().close();
  }

  // The FormXML read back.
  const xml = (await json<{ formxml: string }>(admin, `systemforms(${ids.accountForm})?$select=formxml`)).formxml;
  // G1: the phone is in Details, before Project Count.
  const detailsAt = xml.search(/<label description="Details" languagecode="1033"\s*\/>/);
  expect(detailsAt, xml).toBeGreaterThan(0);
  expect(xml.indexOf('id="telephone1"')).toBeGreaterThan(detailsAt);
  expect(xml.indexOf('id="telephone1"')).toBeLessThan(xml.indexOf('id="op365_projectcount"'));
  // G2: the subgrid, with its parameters in Dataverse's order.
  const grid = element(xml, 'control', `classid="${CLASS.subgrid}"`);
  expect(grid).toContain('id="contact"');
  expect(grid).toContain('indicationOfSubgrid="true"');
  expect(param(grid, 'RelationshipName')).toBe(relationship);
  expect(param(grid, 'TargetEntityType')).toBe('contact');
  expect(param(grid, 'RecordsPerPage')).toBe('4');
  expect(param(grid, 'ViewId')).toBe(`{${ids.view.toLowerCase()}}`);
  for (const [name, value] of [
    ['IsUserView', 'false'],
    ['AutoExpand', 'Fixed'],
    ['EnableQuickFind', 'false'],
    ['EnableViewPicker', 'false'],
    ['EnableJumpBar', 'false'],
    ['ChartGridMode', 'Grid'],
    ['VisualizationId', ''],
    ['IsUserChart', 'false'],
    ['EnableChartPicker', 'false'],
  ]) expect(param(grid, name), `${name} in ${grid}`).toBe(value);
  // Its cell spans the one column of Details and four rows, with no automatic height.
  const gridCell = [...xml.slice(0, xml.indexOf(grid)).matchAll(/<cell\s[^>]*>/g)].at(-1)?.[0];
  expect(gridCell, xml).toContain('colspan="1"');
  expect(gridCell).toContain('rowspan="4"');
  expect(gridCell).toContain('auto="false"');
  const order = ['ViewId', 'IsUserView', 'RelationshipName', 'TargetEntityType', 'AutoExpand', 'EnableQuickFind', 'EnableViewPicker', 'EnableJumpBar',
    'ChartGridMode', 'VisualizationId', 'IsUserChart', 'EnableChartPicker', 'RecordsPerPage'];
  const at = order.map((name) => grid.search(new RegExp(`<${name}[\\s>/]`)));
  expect(at.every((n) => n > 0), grid).toBe(true);
  expect([...at].sort((a, b) => a - b)).toEqual(at);
  // G4: the web resource.
  const frame = element(xml, 'control', `classid="${CLASS.webResource}"`);
  expect(frame).toContain('id="WebResource_panelhtml"');
  expect(param(frame, 'Url')).toBe(HTML_NAME);
  expect(param(frame, 'WebResourceId')?.toLowerCase()).toBe(`{${ids.html}}`);
  expect(param(frame, 'PassParameters')).toBe('false');
  expect(param(frame, 'Security')).toBe('true');
  expect(param(frame, 'Scrolling')).toBe('auto');
  expect(param(frame, 'Border')).toBe('true');
  expect(param(frame, 'Data')).toBe('');
  // G5: the control description of Project Count, with the classic entry and the three form factors.
  const projects = openTag(xml, 'control', 'id="op365_projectcount"');
  const uniqueId = /uniqueid="(\{[^"]+\})"/.exec(projects ?? '')?.[1];
  expect(uniqueId, projects).toBeDefined();
  const description = element(xml, 'controlDescription', `forControl="${uniqueId}"`);
  for (const n of ['0', '1', '2']) expect(openTag(description, 'customControl', `formFactor="${n}"`, `name="${QUOTES}"`), description).toBeDefined();
  expect(description).toMatch(/<value>op365_projectcount<\/value>/);
  expect(description).toMatch(/<datafieldname>op365_projectcount<\/datafieldname>/);
  // G5: the control description of Account Name, with TextBadge for the three form factors.
  const nameControl = openTag(xml, 'control', 'id="name"');
  const nameId = /uniqueid="(\{[^"]+\})"/.exec(nameControl ?? '')?.[1];
  expect(nameId, nameControl).toBeDefined();
  const badge = element(xml, 'controlDescription', `forControl="${nameId}"`);
  for (const n of ['0', '1', '2']) expect(openTag(badge, 'customControl', `formFactor="${n}"`, `name="${BADGE}"`), badge).toBeDefined();
  expect(badge).toMatch(/<value>name<\/value>/);
  // G6: the library and the handler.
  const library = openTag(xml, 'Library', `name="${JS_NAME}"`);
  expect(library, xml).toMatch(/libraryUniqueId="\{[0-9a-f-]{36}\}"/i);
  const onload = element(xml, 'event', 'name="onload"');
  expect(onload).toContain('application="false"');
  const h = openTag(onload, 'Handler', 'functionName="onLoad"');
  expect(h, onload).toContain(`libraryName="${JS_NAME}"`);
  expect(h).toContain('passExecutionContext="true"');
  expect(h).toContain('enabled="true"');
  expect(h).toMatch(/handlerUniqueId="\{[0-9a-f-]{36}\}"/i);
});

test('a contact form gets a quick view of the company', async ({ browser }) => {
  const page = await signedIn(browser, '/manage/tables/contact/forms/new');
  try {
    await page.getByRole('textbox', { name: /^Name/ }).fill(CONTACT_FORM);
    const structure = card(page, 'Structure');
    const general = structure.getByRole('region', { name: 'Section General', exact: true });
    await general.getByRole('button', { name: 'General', exact: true }).click();
    await structure.getByRole('button', { name: 'Add quick view', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Add quick view to General' });
    await dialog.getByRole('combobox', { name: 'Lookup column' }).selectOption('parentcustomerid');
    await dialog.getByRole('combobox', { name: 'Table of the lookup' }).selectOption('account');
    await dialog.getByRole('combobox', { name: 'Quick view form' }).selectOption({ label: QUICK_VIEW_NAME });
    await dialog.getByRole('textbox', { name: /^Label/ }).fill('Company card');
    await dialog.getByRole('button', { name: 'Add', exact: true }).click();
    await expect(dialog).toHaveCount(0);
    await expect(general.getByRole('button', { name: /^Company card/ })).toBeVisible();

    const saved = page.waitForResponse((r) => r.url().endsWith('/systemforms') && r.request().method() === 'POST');
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    const contactSaved = await saved;
    expect(contactSaved.status()).toBe(204);
    ids.contactForm = formId(contactSaved.headers());
    expect(ids.contactForm).not.toBe('');
    await expect(page).toHaveURL(new RegExp(ids.contactForm));
  } finally {
    await page.context().close();
  }

  const xml = (await json<{ formxml: string }>(admin, `systemforms(${ids.contactForm})?$select=formxml`)).formxml;
  const quick = element(xml, 'control', `classid="${CLASS.quickView}"`);
  expect(quick).toContain('datafieldname="parentcustomerid"');
  expect(quick).toMatch(/uniqueid="\{[0-9a-f-]{36}\}"/i);
  expect(quick).toContain(ids.quickView.toLowerCase());
});

test('a quick view form made in the designer is offered by Add quick view', async ({ browser }) => {
  const page = await signedIn(browser, '/manage/tables/account');
  try {
    await page.getByRole('tab', { name: 'Forms', exact: true }).click();
    await page.getByRole('button', { name: 'New form', exact: true }).click();
    await page.getByRole('menuitem', { name: 'Quick view form', exact: true }).click();
    await expect(page).toHaveURL(/\/manage\/tables\/account\/forms\/new\?type=6$/);
    await page.getByRole('textbox', { name: /^Name/ }).fill(MADE_QUICK_VIEW);
    const structure = card(page, 'Structure');
    const general = structure.getByRole('region', { name: 'Section General', exact: true });
    await general.getByRole('button', { name: /^Add field to / }).click();
    const fields = page.getByRole('dialog', { name: 'Add field' });
    await fields.getByRole('textbox', { name: 'Search columns' }).fill('telephone1');
    await fields.getByRole('button', { name: PHONE }).click();
    await expect(fields).toHaveCount(0);

    const saved = page.waitForResponse((r) => r.url().endsWith('/systemforms') && r.request().method() === 'POST');
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    const madeSaved = await saved;
    expect(madeSaved.status()).toBe(204);
    ids.madeQuickView = formId(madeSaved.headers());
    expect(ids.madeQuickView).not.toBe('');
    await expect(page).toHaveURL(new RegExp(ids.madeQuickView));
  } finally {
    await page.context().close();
  }

  const list = await signedIn(browser, '/manage/tables/account');
  try {
    await list.getByRole('tab', { name: 'Forms', exact: true }).click();
    const row = list.getByRole('row').filter({ has: list.getByRole('link', { name: MADE_QUICK_VIEW, exact: true }) });
    await expect(row).toContainText('Quick view');
  } finally {
    await list.context().close();
  }

  const other = await signedIn(browser, '/manage/tables/contact/forms/new');
  try {
    const structure = card(other, 'Structure');
    const general = structure.getByRole('region', { name: 'Section General', exact: true });
    await general.getByRole('button', { name: 'General', exact: true }).click();
    await structure.getByRole('button', { name: 'Add quick view', exact: true }).click();
    const dialog = other.getByRole('dialog', { name: 'Add quick view to General' });
    await dialog.getByRole('combobox', { name: 'Lookup column' }).selectOption('parentcustomerid');
    await dialog.getByRole('combobox', { name: 'Table of the lookup' }).selectOption('account');
    await expect(dialog.getByRole('combobox', { name: 'Quick view form' }).getByRole('option', { name: MADE_QUICK_VIEW })).toHaveCount(1);
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(dialog).toHaveCount(0);
  } finally {
    await other.context().close();
  }
});

test('the app shows the account with the subgrid, the web resource, the custom control and the OnLoad notification', async ({ browser }) => {
  expect(ids.accountForm).not.toBe('');
  const app = await signedIn(browser, recordUrl('account', ids.accountForm, ids.account));
  try {
    await expect(app.getByRole('heading', { level: 1, name: ACCOUNT_NAME })).toBeVisible();
    await expect(app.getByRole('combobox', { name: 'Form', exact: true }).locator('option:checked')).toHaveText(ACCOUNT_FORM);
    // The OnLoad handler's notification (`setFormNotification(message, level, uniqueId)`).
    await expect(app.getByRole('region', { name: 'Form notifications' }).getByText('UI2G', { exact: true })).toBeVisible();
    // The subgrid lists the contact (the view's full name is "{last}, {first}").
    await expect(app.getByRole('grid', { name: VIEW_NAME }).getByRole('row', { name: new RegExp(CONTACT.lastname) })).toBeVisible();
    // The web resource's frame.
    const frame = app.locator(`iframe[title="Web resource ${HTML_NAME}"]`);
    await expect(frame).toBeVisible();
    await expect(frame).toHaveAttribute('src', new RegExp(HTML_NAME.replace(/[.*+?^${}()|[\]\\/]/g, '\\$&')));
    await expect(app.frameLocator(`iframe[title="Web resource ${HTML_NAME}"]`).getByText('UI-2g web resource')).toBeVisible();
    // The custom control runs in place of the standard control. QuoteComparison reads the record's id as an RFQ's, so on an
    // account it draws its own "could not be loaded" message: that is the control running, not the host's fallback.
    await expect(app.locator(`[data-pcf="${QUOTES}"] .fui-FluentProvider`)).toBeVisible();
    await expect(app.locator(`[data-pcf="${QUOTES}"]`)).not.toContainText('Loading the custom control');
    // The public text control shows the account's name.
    await expect(app.locator(`[data-pcf="${BADGE}"]`)).toHaveText(`Badge: ${ACCOUNT_NAME}`);
    expect(
      await app.evaluate(() => {
        const xrm = (window as unknown as { Xrm?: { Page?: { getControl: (c: string) => { getControlType: () => string } | null } } }).Xrm;
        return xrm?.Page?.getControl('name')?.getControlType();
      }),
    ).toBe('customcontrol');
    expect(
      await app.evaluate(() => {
        const xrm = (window as unknown as { Xrm?: { Page?: { getControl: (c: string) => { getControlType: () => string } | null } } }).Xrm;
        return xrm?.Page?.getControl('op365_projectcount')?.getControlType();
      }),
    ).toBe('customcontrol');
    await expect(notices(app)).toHaveCount(0);
  } finally {
    await app.context().close();
  }
});

test('the app shows the contact with the account in the quick view', async ({ browser }) => {
  expect(ids.contactForm).not.toBe('');
  const app = await signedIn(browser, recordUrl('contact', ids.contactForm, ids.contact));
  try {
    await expect(app.getByRole('heading', { level: 1, name: new RegExp(CONTACT.lastname) })).toBeVisible();
    const quick = app.getByTestId('quickview');
    await expect(quick).toBeVisible();
    await expect(quick.getByRole('textbox', { name: /^(Account Name|name)/ })).toHaveValue(ACCOUNT_NAME);
  } finally {
    await app.context().close();
  }
});
