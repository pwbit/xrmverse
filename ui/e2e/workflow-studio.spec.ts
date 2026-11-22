import { randomUUID } from 'node:crypto';
import { mkdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { expect, request, test, type APIRequestContext, type Browser, type Locator, type Page } from '@playwright/test';
import { parseDocument } from 'yaml';
import { ADMIN_OBJECT_ID, mintDevToken, runEnv, uiDir, uniqueSuffix } from './harness';

/**
 * Plan 3.7, task 10: the workflow studio's acceptance, end to end against the real host. A user whose only grants beyond
 * Basic User are the Process privileges (and Read on account and task) builds a real-time workflow on the canvas, saves and
 * activates it; the result exports exactly as a hand-written reference file (e2e/fixtures/studio/reference.yaml); a step the
 * library lacks is shown disabled in the palette and refused in the code view; a user without the Process privileges gets the
 * studio's explanatory page and no studio tile; `/` opens `/apps`.
 *
 * The seeded account table has no revenue and no description column, so the spec adds `new_revenue` (money) and
 * `new_description` (multiple lines of text) under the default publisher's prefix and removes them at the end.
 *
 * Screenshots (list, canvas, code view, light and dark) are kept only when STUDIO_SCREENSHOTS_DIR names a folder.
 */

const env = runEnv();
test.use({ baseURL: env.baseURL });
// The reference scenario compares with the process the canvas scenario saved: the scenarios run in order, and stop at the first failure.
test.describe.configure({ mode: 'serial' });

/** Role template id of Basic User (Xrmd.Security.StandardRoles), also the role id in the root business unit. */
const BASIC_USER = '236750cd-45ae-4939-ab12-b24b920ced93';
const PROCESS_PRIVILEGES = ['prvReadWorkflow', 'prvCreateWorkflow', 'prvWriteWorkflow', 'prvDeleteWorkflow', 'prvActivateSynchronousWorkflow'];
const REVENUE = 'new_revenue';
const DESCRIPTION = 'new_description';
const REFERENCE = resolve(uiDir, 'e2e', 'fixtures', 'studio', 'reference.yaml');
const screenshots = process.env.STUDIO_SCREENSHOTS_DIR;

const webApi = (path: string) => `/api/data/v9.2/${path}`;
const authoring = (path: string) => `/authoring/workflows${path}`;

// ---- helpers ------------------------------------------------------------------------------------------------------

/** A Web API and authoring API client signed in as `objectId`. */
async function apiAs(objectId: string, name: string): Promise<APIRequestContext> {
  const token = mintDevToken(env.signingKey, `${env.baseURL}/`, objectId, name);
  return request.newContext({
    baseURL: env.baseURL,
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

async function rootBusinessUnit(api: APIRequestContext): Promise<string> {
  const r = await json<{ value: { businessunitid: string }[] }>(api, 'businessunits?$select=businessunitid&$filter=_parentbusinessunitid_value eq null');
  expect(r.value).toHaveLength(1);
  return r.value[0].businessunitid;
}

async function assignRole(api: APIRequestContext, userId: string, roleId: string): Promise<void> {
  const res = await api.post(webApi(`systemusers(${userId})/systemuserroles_association/$ref`), {
    data: { '@odata.id': `${env.baseURL}${webApi(`roles(${roleId})`)}` },
  });
  expect(res.status(), await res.text()).toBe(204);
}

/** A role with the named privileges at Global depth, granted through AddPrivilegesRole. */
async function role(api: APIRequestContext, bu: string, name: string, names: string[]): Promise<string> {
  const roleId = await create(api, 'roles', { roleid: randomUUID(), name, 'businessunitid@odata.bind': `/businessunits(${bu})` });
  const privileges = await json<{ value: { privilegeid: string; name: string }[] }>(
    api,
    `privileges?$select=privilegeid,name&$filter=${names.map((n) => `name eq '${n}'`).join(' or ')}`,
  );
  expect(privileges.value.map((p) => p.name).sort()).toEqual([...names].sort());
  const granted = await api.post(webApi(`roles(${roleId})/Microsoft.Dynamics.CRM.AddPrivilegesRole`), {
    data: { Privileges: privileges.value.map((p) => ({ Depth: 'Global', PrivilegeId: p.privilegeid, BusinessUnitId: bu })) },
  });
  expect(granted.status(), await granted.text()).toBe(204);
  return roleId;
}

async function createUser(api: APIRequestContext, bu: string, objectId: string, fullname: string, roles: string[]): Promise<string> {
  const userId = await create(api, 'systemusers', { fullname, azureactivedirectoryobjectid: objectId, 'businessunitid@odata.bind': `/businessunits(${bu})` });
  for (const r of roles) await assignRole(api, userId, r);
  return userId;
}

const label = (text: string) => ({
  LocalizedLabels: [{ Label: text, LanguageCode: 1033 }],
  UserLocalizedLabel: { Label: text, LanguageCode: 1033 },
});

/** Adds a column to account unless an earlier (crashed) run left it there. */
async function ensureAccountColumn(api: APIRequestContext, body: Record<string, unknown>): Promise<void> {
  const name = body.LogicalName as string;
  const existing = await api.get(webApi(`EntityDefinitions(LogicalName='account')/Attributes(LogicalName='${name}')?$select=LogicalName`));
  if (existing.status() === 200) return;
  expect(existing.status(), await existing.text()).toBe(404);
  const res = await api.post(webApi(`EntityDefinitions(LogicalName='account')/Attributes`), { data: { IsValidODataAttribute: false, ...body } });
  expect(res.status(), `create account.${name}: ${await res.text()}`).toBe(204);
}

/** Applies a workflow document through the authoring API (as the caller of `api`) and returns the result. */
async function applyYaml(api: APIRequestContext, yaml: string, dryRun: boolean): Promise<{ status: number; result: ApplyResult }> {
  const res = await api.post(authoring(`/apply?dryRun=${dryRun}`), { data: yaml, headers: { 'Content-Type': 'text/yaml; charset=utf-8' } });
  return { status: res.status(), result: (await res.json()) as ApplyResult };
}

interface ApplyResult {
  id: string;
  name: string;
  action: string;
  state: string;
  errors: { message: string; at?: { line: number; column: number } }[];
}

async function exportAs(api: APIRequestContext, id: string, format: 'yaml' | 'xaml'): Promise<string> {
  const res = await api.get(authoring(`/${id}?format=${format}`), { headers: { Accept: format === 'yaml' ? 'text/yaml' : 'text/xml' } });
  expect(res.status(), `export ${id} as ${format}: ${await res.text()}`).toBe(200);
  return res.text();
}

/**
 * Every failed API response (400 or more) a watched page received: a scenario must not pass while the studio hides a failed
 * request. A refused dry run (`400 POST /authoring/workflows/apply`) is the studio's normal answer while a document is being
 * built, so it is not one; a scenario lists the refusals it expects in `tolerated`.
 */
let failures: string[] = [];
let tolerated: RegExp[] = [];
function watch(page: Page): void {
  page.on('response', (r) => {
    const path = new URL(r.url()).pathname;
    if (r.status() < 400 || !(path.startsWith('/api/') || path.startsWith('/authoring/'))) return;
    const line = `${r.status()} ${r.request().method()} ${path}`;
    if (line === '400 POST /authoring/workflows/apply' || tolerated.some((t) => t.test(line))) return;
    failures.push(line);
  });
}
test.beforeEach(() => {
  failures = [];
  tolerated = [];
});
test.afterEach(() => {
  expect(failures, 'API requests of the studio that failed').toEqual([]);
});

/** Opens `path` in a fresh browser context (its own sessionStorage) and signs in through the development sign-in form. */
async function signedIn(browser: Browser, path: string, user: { objectId: string; name: string }): Promise<Page> {
  const context = await browser.newContext({ baseURL: env.baseURL, viewport: { width: 1600, height: 1000 } });
  const page = await context.newPage();
  watch(page);
  await page.goto(path);
  await page.getByRole('textbox', { name: 'Dev signing key (Auth:DevSigningKey)', exact: true }).fill(env.signingKey);
  await page.getByRole('textbox', { name: 'User object id (oid)', exact: true }).fill(user.objectId);
  await page.getByRole('textbox', { name: 'Display name', exact: true }).fill(user.name);
  await page.getByRole('button', { name: 'Sign in' }).click();
  return page;
}

/** Saves a light and a dark screenshot of the page as `<name>-light.png` and `<name>-dark.png` when STUDIO_SCREENSHOTS_DIR is set. */
async function shoot(page: Page, name: string): Promise<void> {
  if (!screenshots) return;
  mkdirSync(screenshots, { recursive: true });
  for (const colorScheme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme });
    // The theme follows the system scheme (the users' colour scheme is the default, System): let it repaint.
    await page.waitForTimeout(300);
    await page.screenshot({ path: join(screenshots, `${name}-${colorScheme}.png`), fullPage: true });
  }
  await page.emulateMedia({ colorScheme: 'light' });
}

/** The New dialog of the list: a workflow on `table` in `mode`; the editor of the new process opens. */
async function newWorkflow(page: Page, name: string, table: string, mode: 'Real-time' | 'Background'): Promise<void> {
  await expect(page.getByRole('heading', { name: 'Processes', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'New', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'New process' });
  await dialog.getByRole('textbox', { name: 'Name', exact: true }).fill(name);
  await dialog.getByRole('textbox', { name: 'Table', exact: true }).fill(table);
  await dialog.getByRole('radio', { name: mode, exact: true }).check();
  await dialog.getByRole('button', { name: 'Create', exact: true }).click();
  await expect(page).toHaveURL(/\/studio\/workflows\/new$/);
  await expect(page.getByRole('heading', { level: 1, name, exact: true })).toBeVisible();
}

const panelOf = (page: Page) => page.getByRole('complementary', { name: 'Properties', exact: true });
const flowOf = (page: Page) => page.getByRole('region', { name: 'Flow of the process', exact: true });
const card = (scope: Page | Locator, name: string) => scope.getByRole('group', { name, exact: true });

/** Opens the palette of the first "+" slot in `scope` and picks `step`. */
async function insertStep(page: Page, scope: Locator, step: string): Promise<void> {
  await scope.getByRole('button', { name: 'Add a step', exact: true }).first().click();
  await page.getByRole('dialog', { name: 'Steps', exact: true }).getByRole('button', { name: step, exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'Steps', exact: true })).toHaveCount(0);
}

/**
 * Picks `value` in a list of the properties panel. Columns and tables are lists once the table metadata has loaded (a text box
 * until then): waiting for the list waits for the metadata.
 */
async function choose(scope: Locator, name: string | RegExp, value: string): Promise<void> {
  const list = scope.getByRole('combobox', typeof name === 'string' ? { name, exact: true } : { name });
  await expect(list).toBeVisible();
  await list.selectOption(value);
}

/** Adds a row for `column` to the step's `set` and gives it the literal `value`. */
async function setColumn(panel: Locator, column: string, value: string): Promise<void> {
  await choose(panel, 'Add a column', column);
  const row = new RegExp(`\\(${column}\\)$`);
  // A new row starts as the empty value (~): "A value" turns it into a literal.
  await choose(panel, row, 'literal');
  await panel.getByLabel(new RegExp(`^Value of .*\\(${column}\\)$`)).fill(value);
}

// ---- arrangement ----------------------------------------------------------------------------------------------------

let admin: APIRequestContext;
let processApi: APIRequestContext;
const sfx = uniqueSuffix();
const processUser = { objectId: randomUUID(), name: `E2E Studio Maker ${sfx}` };
const basicUser = { objectId: randomUUID(), name: `E2E Studio Basic ${sfx}` };
const CANVAS_NAME = `Studio E2E ${sfx}`;
const REFERENCE_NAME = `Studio E2E reference ${sfx}`;
const MISSING_NAME = `Studio E2E missing ${sfx}`;
/** The processes this run saved: deactivated and deleted at the end. */
const saved: string[] = [];
let canvasId: string | undefined;

test.beforeAll(async () => {
  test.setTimeout(300_000);
  admin = await apiAs(ADMIN_OBJECT_ID, 'Administrator');

  // The columns the scenario compares and sets; account is published so that the metadata shows them.
  await ensureAccountColumn(admin, { '@odata.type': 'Microsoft.Dynamics.CRM.MoneyAttributeMetadata', AttributeType: 'Money', AttributeTypeName: { Value: 'MoneyType' }, LogicalName: REVENUE, SchemaName: 'new_Revenue', DisplayName: label('Revenue'), RequiredLevel: { Value: 'None' } });
  await ensureAccountColumn(admin, { '@odata.type': 'Microsoft.Dynamics.CRM.MemoAttributeMetadata', AttributeType: 'Memo', AttributeTypeName: { Value: 'MemoType' }, LogicalName: DESCRIPTION, SchemaName: 'new_Description', DisplayName: label('Description'), MaxLength: 2000, RequiredLevel: { Value: 'None' } });
  const published = await admin.post(webApi('PublishXml'), { data: { ParameterXml: '<importexportxml><entities><entity>account</entity></entities></importexportxml>' } });
  expect(published.status(), await published.text()).toBe(204);

  // The maker: Basic User (its own settings, the apps) plus the Process privileges and Read on account and activities; no
  // System Customizer. The other user has Basic User only.
  const bu = await rootBusinessUnit(admin);
  const processRole = await role(admin, bu, `E2E studio maker ${sfx}`, [...PROCESS_PRIVILEGES, 'prvReadAccount', 'prvReadActivity']);
  await createUser(admin, bu, processUser.objectId, processUser.name, [BASIC_USER, processRole]);
  await createUser(admin, bu, basicUser.objectId, basicUser.name, [BASIC_USER]);
  processApi = await apiAs(processUser.objectId, processUser.name);
});

test.afterAll(async () => {
  // Every cleanup step runs whatever an earlier one answered; the failures are asserted at the end.
  const problems: string[] = [];
  const attempt = async (what: string, step: () => Promise<void>) => {
    try {
      await step();
    } catch (e) {
      problems.push(`${what}: ${e instanceof Error ? e.message : String(e)}`);
    }
  };
  if (admin) {
    for (const id of saved) {
      // An activated process is deactivated first (a draft answers whatever it answers): only a draft is deleted.
      await attempt(`deactivate ${id}`, async () => void (await admin.post(authoring(`/${id}/deactivate`))));
      await attempt(`DELETE workflows(${id})`, async () => {
        const res = await admin.delete(webApi(`workflows(${id})`));
        if (![204, 404].includes(res.status())) throw new Error(`${res.status()} ${await res.text()}`);
      });
    }
    // The test columns go whenever they exist, whoever created them (an earlier crashed run included).
    for (const column of [REVENUE, DESCRIPTION]) {
      await attempt(`DELETE account.${column}`, async () => {
        const res = await admin.delete(webApi(`EntityDefinitions(LogicalName='account')/Attributes(LogicalName='${column}')`));
        if (![204, 404].includes(res.status())) throw new Error(`${res.status()} ${await res.text()}`);
      });
    }
  }
  await attempt('dispose the maker client', async () => processApi?.dispose());
  await attempt('dispose the administrator client', async () => admin?.dispose());
  expect(problems, 'cleanup steps that failed').toEqual([]);
});

// ---- scenarios ------------------------------------------------------------------------------------------------------

test('a user with only Process privileges builds a workflow on the canvas', async ({ browser }) => {
  test.setTimeout(300_000);
  const page = await signedIn(browser, '/studio/workflows', processUser);
  await newWorkflow(page, CANVAS_NAME, 'account', 'Real-time');
  const flow = flowOf(page);
  const panel = panelOf(page);
  await expect(panel).toContainText('Select a card on the canvas to see its properties.');

  // The trigger: create, and update of name (the update trigger is written with its first column).
  const trigger = card(flow, 'Trigger');
  await trigger.click();
  await expect(trigger).toHaveAttribute('aria-current', 'true');
  await panel.getByRole('checkbox', { name: 'When a record is created', exact: true }).check();
  await panel.getByRole('checkbox', { name: 'When a record is updated', exact: true }).check();
  await choose(panel, 'Add a column', 'name');
  await expect(panel.getByRole('list', { name: 'Columns that start it', exact: true }).getByRole('button', { name: 'Remove name', exact: true })).toBeVisible();
  await expect(trigger).toContainText('When a record is created');
  await expect(trigger).toContainText('When name change');

  // "+" → If new_revenue gt 1000.
  await insertStep(page, flow, 'Condition');
  await expect(card(flow, 'If: all of 0 conditions')).toHaveAttribute('aria-current', 'true');
  await choose(panel, 'Condition', 'compare');
  await expect(panel.getByRole('combobox', { name: 'Compare', exact: true })).toHaveValue('field');
  await choose(panel, 'Column', REVENUE);
  await choose(panel, 'Operator', 'gt');
  await panel.getByLabel('Value of With', { exact: true }).fill('1000');
  const ifCard = card(flow, `If: ${REVENUE} gt 1000`);
  await expect(ifCard).toBeVisible();

  // Then → Update record primary, description = big.
  await insertStep(page, ifCard.getByRole('group', { name: 'Then', exact: true }), 'Update record');
  await expect(card(flow, 'Update record: primary')).toHaveAttribute('aria-current', 'true');
  await expect(panel.getByRole('combobox', { name: 'Record', exact: true })).toHaveValue('primary');
  await setColumn(panel, DESCRIPTION, 'big');

  // Else → Create task, subject = check.
  await ifCard.getByRole('button', { name: 'Add else', exact: true }).click();
  await insertStep(page, ifCard.getByRole('group', { name: 'Else', exact: true }), 'Create record');
  await choose(panel, 'Table', 'task');
  await expect(card(flow, 'Create record: task')).toHaveAttribute('aria-current', 'true');
  // The last edit's dry run: the server accepts the document before it is saved.
  const checked = page.waitForResponse((r) => r.url().includes('/authoring/workflows/apply?dryRun=true') && (r.request().postData() ?? '').includes('subject: check'));
  await setColumn(panel, 'subject', 'check');
  const dryRun = await checked;
  expect(dryRun.status(), `the dry run of the finished document: ${await dryRun.text()}`).toBe(200);
  expect(((await dryRun.json()) as ApplyResult).errors).toEqual([]);

  // Ruling R8: Tab walks the canvas in document order, the trigger card first, then the "+" before the first step, then that
  // step's card; Enter selects the focused card.
  // The zoom toolbar is the canvas region's sibling, not inside it.
  await page.getByRole('group', { name: 'Canvas tools', exact: true }).getByRole('button', { name: 'Fit to view', exact: true }).focus();
  await page.keyboard.press('Tab');
  await expect(trigger).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(flow.getByRole('button', { name: 'Add a step', exact: true }).first()).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(ifCard).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(ifCard).toHaveAttribute('aria-current', 'true');

  // Save: the process gets its id and address; Activate: the state badge says Activated.
  const header = page.getByRole('region', { name: 'Process header', exact: true });
  await expect(header.getByText('Unsaved changes', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page).toHaveURL(/\/studio\/workflows\/[0-9a-f-]{36}$/);
  canvasId = /\/studio\/workflows\/([0-9a-f-]{36})$/.exec(page.url())![1];
  saved.push(canvasId);
  await expect(header.getByText('Draft', { exact: true })).toBeVisible();
  await expect(header.getByText('Unsaved changes', { exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Activate', exact: true }).click();
  await expect(header.getByText('Activated', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Deactivate', exact: true })).toBeVisible();
  const listed = await processApi.get(authoring(''));
  expect(listed.status(), await listed.text()).toBe(200);
  const row = ((await listed.json()) as { id: string; state: string; mode: string; entity: string }[]).find((r) => r.id.toLowerCase() === canvasId!.toLowerCase());
  expect(row).toMatchObject({ state: 'activated', mode: 'realtime', entity: 'account' });

  // The views, for the record: the canvas, the code view with its read-only diagram, the list.
  await expect(card(flow, 'Create record: task')).toBeVisible();
  await shoot(page, 'canvas');
  await page.getByRole('tab', { name: 'Code', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'YAML of the process', exact: true })).toContainText(`field: ${REVENUE}`);
  await expect(page.getByRole('complementary', { name: 'Diagram (read-only)', exact: true })).toBeVisible();
  await shoot(page, 'code');
  await header.getByRole('link', { name: 'Workflows', exact: true }).click();
  await expect(page.getByRole('link', { name: CANVAS_NAME, exact: true })).toBeVisible();
  await shoot(page, 'list');
  await page.context().close();
});

test('the canvas result equals the reference', async () => {
  expect(canvasId, 'the canvas scenario saved its process').toBeDefined();
  const reference = parseDocument(readFileSync(REFERENCE, 'utf8'));
  reference.set('name', REFERENCE_NAME);
  const applied = await applyYaml(processApi, reference.toString(), false);
  expect(applied.status, JSON.stringify(applied.result)).toBe(200);
  expect(applied.result).toMatchObject({ action: 'created', errors: [] });
  const referenceId = applied.result.id;
  saved.push(referenceId);
  expect(referenceId.toLowerCase()).not.toBe(canvasId!.toLowerCase());

  // YAML: equal once the top-level id and name are removed.
  const withoutIdentity = (yaml: string) => yaml.replace(/\r\n/g, '\n').split('\n').filter((line) => !/^(id|name):/.test(line)).join('\n');
  const canvasYaml = await exportAs(processApi, canvasId!, 'yaml');
  const referenceYaml = await exportAs(processApi, referenceId, 'yaml');
  expect(withoutIdentity(canvasYaml)).toBe(withoutIdentity(referenceYaml));

  // XAML: equal once each process's id (in any case, with or without braces) and name are replaced by placeholders.
  const normalised = (xaml: string, id: string, name: string) =>
    xaml.replace(new RegExp(id.replace(/-/g, '-?'), 'gi'), '{process-id}').split(name).join('{process-name}');
  const canvasXaml = await exportAs(processApi, canvasId!, 'xaml');
  const referenceXaml = await exportAs(processApi, referenceId, 'xaml');
  expect(normalised(canvasXaml, canvasId!, CANVAS_NAME)).toBe(normalised(referenceXaml, referenceId, REFERENCE_NAME));
});

test('a missing step is refused', async ({ browser }) => {
  test.setTimeout(180_000);
  const page = await signedIn(browser, '/studio/workflows', processUser);
  await newWorkflow(page, MISSING_NAME, 'account', 'Background');

  // The palette lists "Send email from template" as not available, with its reason, and offers no button for it.
  await flowOf(page).getByRole('button', { name: 'Add a step', exact: true }).first().click();
  const palette = page.getByRole('dialog', { name: 'Steps', exact: true });
  const missing = palette.locator('[aria-disabled="true"]').filter({ hasText: 'Send email from template' });
  await expect(missing).toBeVisible();
  await expect(missing).toContainText('The step library has no email from a template: send it from a webhook (roadmap 3.2) or a custom workflow activity.');
  await expect(palette.getByRole('button', { name: 'Send email from template' })).toHaveCount(0);
  await page.keyboard.press('Escape');
  await expect(palette).toHaveCount(0);

  // The code view: `steps: []` becomes a list holding `- sendEmailFromTemplate: {}` on line 6. Enter indents by itself, so
  // the indentation it put is selected and typed over.
  await page.getByRole('tab', { name: 'Code', exact: true }).click();
  const code = page.getByRole('textbox', { name: 'YAML of the process', exact: true });
  await code.click();
  await page.keyboard.press('ControlOrMeta+End');
  for (let i = 0; i < 4; i++) await page.keyboard.press('Backspace'); // the final newline, then "]", "[" and the space
  await page.keyboard.press('Enter');
  await page.keyboard.press('Shift+Home');
  await page.keyboard.type('  - sendEmailFromTemplate: {}');
  await expect(code).toContainText('- sendEmailFromTemplate: {}');

  // The server's own answer to the same document: its error and line.
  const text = ['kind: workflow', `name: ${MISSING_NAME}`, 'entity: account', 'mode: background', 'steps:', '  - sendEmailFromTemplate: {}'].join('\n');
  const server = await applyYaml(processApi, text, true);
  expect(server.status, JSON.stringify(server.result)).toBe(400);
  expect(server.result.errors).toHaveLength(1);
  const [error] = server.result.errors;
  expect(error.message).toContain("Unknown step 'sendEmailFromTemplate'");
  expect(error.at?.line).toBe(6);

  // The problems pane shows that error at that line, and Save is blocked.
  const problems = page.getByRole('region', { name: /^Problems \(\d+\)$/ });
  const problem = problems.getByRole('button').filter({ hasText: "Unknown step 'sendEmailFromTemplate'" });
  await expect(problem).toHaveCount(1);
  const shown = /^(\d+):(\d+)\s*([\s\S]*)$/.exec((await problem.innerText()).trim());
  expect(shown, 'the problem shows its line and column').not.toBeNull();
  expect(Number(shown![1])).toBe(error.at!.line);
  expect(shown![3].trim()).toBe(error.message);
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled();
  await page.context().close();
});

test('no Process privileges: the studio explains, and /apps has no studio tile', async ({ browser }) => {
  // The studio's gate and the apps page ask for the process list: both are refused.
  tolerated = [/^403 GET \/authoring\/workflows$/];
  const page = await signedIn(browser, '/studio/workflows', basicUser);
  await expect(
    page.getByText('You need the Read privilege on processes (prvReadWorkflow) to use the workflow studio. Ask an administrator for a security role that includes it.', { exact: true }),
  ).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Processes', exact: true })).toHaveCount(0);

  const asked = page.waitForResponse((r) => new URL(r.url()).pathname === '/authoring/workflows' && r.request().method() === 'GET');
  await page.getByRole('link', { name: 'Apps', exact: true }).click();
  await expect(page).toHaveURL(/\/apps$/);
  expect((await asked).status()).toBe(403);
  await expect(page.getByRole('heading', { level: 1, name: 'Apps', exact: true })).toBeVisible();
  await expect(page.getByRole('list', { name: 'Tools', exact: true })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Workflow studio', exact: true })).toHaveCount(0);
  await page.context().close();
});

test('/ opens /apps, and its studio tile opens the process list', async ({ browser }) => {
  const page = await signedIn(browser, '/', processUser);
  await expect(page).toHaveURL(/\/apps$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Apps', exact: true })).toBeVisible();
  await page.getByRole('list', { name: 'Tools', exact: true }).getByRole('link', { name: 'Workflow studio', exact: true }).click();
  await expect(page).toHaveURL(/\/studio\/workflows$/);
  await expect(page.getByRole('heading', { name: 'Processes', exact: true })).toBeVisible();
  await page.context().close();
});
