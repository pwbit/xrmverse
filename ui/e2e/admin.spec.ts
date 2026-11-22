import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { deflateSync } from 'node:zlib';
import { expect, request, test, type APIRequestContext, type Browser, type Locator, type Page } from '@playwright/test';
import { ADMIN_OBJECT_ID, mintDevToken, runEnv, uniqueSuffix } from './harness';

/**
 * Spec acceptance (2.1): a System Administrator manages users, roles, tables and solutions without the CLI.
 * Every scenario drives the shell in Chromium against the real host and SQL Server the global setup started; the
 * Web API is called directly only to arrange other principals and to check what the server stored.
 */

const env = runEnv();
test.use({ baseURL: env.baseURL });

const SYSTEM_CUSTOMIZER = 'System Customizer';
/** The roletemplateid of System Customizer (Xrmd.Security.StandardRoles): standard roles are found by template, not name. */
const SYSTEM_CUSTOMIZER_TEMPLATE = '119f245c-3cc8-4b62-b31c-d1a046ced15d';
/** privilegedepthmask of a Global privilege (Basic 1, Local 2, Deep 4, Global 8). */
const GLOBAL_MASK = 8;
const FIXTURE = 'XrmverseFixture';
const FIXTURE_NAME = 'Xrmverse Fixture';

// ---- helpers ------------------------------------------------------------------------------------------------------

/** A Web API (or Admin API) client signed in as `objectId`. */
async function apiAs(objectId: string, name = 'E2E'): Promise<APIRequestContext> {
  const token = mintDevToken(env.signingKey, `${env.baseURL}/`, objectId, name);
  return request.newContext({
    baseURL: env.baseURL,
    extraHTTPHeaders: { Authorization: `Bearer ${token}`, Accept: 'application/json', 'OData-Version': '4.0' },
  });
}

const webApi = (path: string) => `/api/data/v9.2/${path}`;

async function json<T>(api: APIRequestContext, path: string): Promise<T> {
  const res = await api.get(webApi(path));
  expect(res.status(), `GET ${path}: ${await res.text()}`).toBe(200);
  return (await res.json()) as T;
}

async function rootBusinessUnit(api: APIRequestContext): Promise<{ businessunitid: string; name: string }> {
  const r = await json<{ value: { businessunitid: string; name: string }[] }>(
    api,
    'businessunits?$select=businessunitid,name&$filter=_parentbusinessunitid_value eq null',
  );
  expect(r.value).toHaveLength(1);
  return r.value[0];
}

/** Creates a system user in the root business unit through the Web API; returns its id. */
async function createUser(api: APIRequestContext, objectId: string, fullname: string): Promise<string> {
  const bu = await rootBusinessUnit(api);
  const res = await api.post(webApi('systemusers'), {
    data: { fullname, azureactivedirectoryobjectid: objectId, 'businessunitid@odata.bind': `/businessunits(${bu.businessunitid})` },
    headers: { Prefer: 'return=representation' },
  });
  expect(res.status(), await res.text()).toBe(201);
  return ((await res.json()) as { systemuserid: string }).systemuserid;
}

async function assignCustomizer(api: APIRequestContext, userId: string): Promise<void> {
  const bu = await rootBusinessUnit(api);
  const roles = await json<{ value: { roleid: string }[] }>(
    api,
    `roles?$select=roleid&$filter=roletemplateid eq ${SYSTEM_CUSTOMIZER_TEMPLATE} and _businessunitid_value eq ${bu.businessunitid}`,
  );
  expect(roles.value).toHaveLength(1);
  const res = await api.post(webApi(`systemusers(${userId})/systemuserroles_association/$ref`), {
    data: { '@odata.id': `${env.baseURL}${webApi(`roles(${roles.value[0].roleid})`)}` },
  });
  expect(res.status(), await res.text()).toBe(204);
}

/** Opens `path` in a fresh tab and signs in through the shell's development sign-in form. */
async function signIn(page: Page, path: string, objectId = ADMIN_OBJECT_ID, name = 'Administrator'): Promise<void> {
  await page.goto(path);
  await page.getByRole('textbox', { name: 'Dev signing key (Auth:DevSigningKey)', exact: true }).fill(env.signingKey);
  await page.getByRole('textbox', { name: 'User object id (oid)', exact: true }).fill(objectId);
  await page.getByRole('textbox', { name: 'Display name', exact: true }).fill(name);
  await page.getByRole('button', { name: 'Sign in' }).click();
}

/** A separate browser context (its own sessionStorage), signed in as another user. */
async function otherUser(browser: Browser, path: string, objectId: string, name: string): Promise<Page> {
  const context = await browser.newContext({ baseURL: env.baseURL });
  const page = await context.newPage();
  await signIn(page, path, objectId, name);
  return page;
}

/** A notification (toast) with exactly this title; the live region repeats it, so it is not matched by text alone. */
/** Selects a row of a web admin list (its radio "Select {name}") unless it already is, so the command bar shows its commands. */
async function selectRow(page: Page, name: string): Promise<void> {
  const radio = page.getByRole('radio', { name: `Select ${name}`, exact: true });
  if (!(await radio.isChecked())) await radio.click();
}

const toast = (page: Page, text: string) => page.locator('.fui-ToastTitle').filter({ hasText: new RegExp(`^${escapeRegExp(text)}$`) });
const escapeRegExp = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/**
 * Picks `name` in a search-as-you-type picker: types it, waits until the debounced search lists exactly that one
 * record, then selects it.
 */
async function pick(scope: Locator, label: string, name: string): Promise<void> {
  await scope.getByRole('textbox', { name: label, exact: true }).fill(name);
  const options = scope.getByRole('listbox', { name: `${label} results`, exact: true }).getByRole('option');
  await expect(options).toHaveText([name]);
  await options.click();
  await expect(scope.getByText(`Selected: ${name}`, { exact: true })).toBeVisible();
}

const nav = (page: Page) => page.getByRole('navigation', { name: 'Administration' });

async function entityExists(api: APIRequestContext, logicalName: string): Promise<boolean> {
  const res = await api.get(webApi(`EntityDefinitions(LogicalName='${logicalName}')?$select=LogicalName`));
  if (res.status() === 404) return false;
  expect(res.status(), await res.text()).toBe(200);
  return true;
}

/**
 * Creates a table in the shell's Create table dialog. The suggested SchemaName is the default publisher's prefix
 * (`new`) and the display name in PascalCase; `schemaName` replaces it.
 */
async function createTable(page: Page, displayName: string, schemaName: string): Promise<void> {
  await page.getByRole('button', { name: 'Create table' }).click();
  const create = page.getByRole('dialog', { name: 'Create table' });
  await create.getByRole('textbox', { name: 'Display name', exact: true }).fill(displayName);
  await create.getByRole('textbox', { name: 'Plural display name', exact: true }).fill(`${displayName} items`);
  const schema = create.getByRole('textbox', { name: 'Schema name', exact: true });
  const pascal = displayName.split(/[^A-Za-z0-9]+/).filter(Boolean).map((w) => w[0].toUpperCase() + w.slice(1)).join('');
  await expect(schema).toHaveValue(`new_${pascal}`);
  await schema.fill(schemaName);
  await expect(create.getByRole('textbox', { name: 'Logical name', exact: true })).toHaveValue(schemaName.toLowerCase());
  await create.getByRole('button', { name: 'Create' }).click();
  await expect(toast(page, `Table "${schemaName.toLowerCase()}" created.`)).toBeVisible();
}

/** The fixture solution's import, through the shell's import dialog. */
async function importFixture(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Import solution' }).click();
  const dialog = page.getByRole('dialog', { name: 'Import solution' });
  await dialog.getByLabel('Solution file (.zip)').setInputFiles(env.solutionZip);
  await dialog.getByRole('button', { name: 'Import' }).click();
  await expect(dialog.getByText('The solution was imported.')).toBeVisible({ timeout: 90_000 });
  await dialog.getByRole('button', { name: 'Close' }).click();
  await expect(page.getByRole('row').filter({ hasText: FIXTURE })).toBeVisible();
}

/**
 * Every API response of 400 or more that a watched shell page received during the test: a scenario must not pass
 * while the shell hides a failed request. The test's page is watched; a second user's page is watched from the point
 * where it is expected to work (before that, it is expected to be refused).
 */
let failures: string[] = [];
/** The failed responses a scenario expects, in order (set by the scenario). */
let expectedFailures: string[] = [];
function watch(page: Page): void {
  page.on('response', (r) => {
    const path = new URL(r.url()).pathname;
    if (r.status() >= 400 && (path.startsWith('/api/') || path.startsWith('/admin/'))) failures.push(`${r.status()} ${r.request().method()} ${path}`);
  });
}
test.beforeEach(({ page }) => {
  failures = [];
  expectedFailures = [];
  watch(page);
});
test.afterEach(() => {
  expect(failures, 'API requests of the shell that failed').toEqual(expectedFailures);
});

/**
 * The scenarios name their tables and columns with the `xv` and `e2e` prefixes, and the server accepts only the prefix of
 * an existing publisher. `xv` is the fixture solution's publisher, but its import runs later: it is created as the fixture
 * defines it (unique name `xrmverse`, option value prefix 10000), so the import finds it by unique name and keeps it as
 * the same publisher. `e2e` is the scenarios' own.
 */
const scenarioPublishers = [
  { uniquename: 'xrmverse', friendlyname: 'xrmverse', customizationprefix: 'xv', customizationoptionvalueprefix: 10000 },
  { uniquename: 'e2e', friendlyname: 'End-to-end scenarios', customizationprefix: 'e2e', customizationoptionvalueprefix: 72700 },
];

async function ensurePublishers(api: APIRequestContext): Promise<void> {
  for (const publisher of scenarioPublishers) {
    const existing = await json<{ value: unknown[] }>(
      api,
      `publishers?$select=publisherid&$filter=customizationprefix eq '${publisher.customizationprefix}'`,
    );
    if (existing.value.length > 0) continue;
    const res = await api.post(webApi('publishers'), { data: publisher });
    expect(res.status(), `POST publishers (${publisher.uniquename}): ${await res.text()}`).toBe(204);
  }
}

let admin: APIRequestContext;
test.beforeAll(async () => {
  admin = await apiAs(ADMIN_OBJECT_ID, 'Administrator');
  await ensurePublishers(admin);
});
test.afterAll(async () => {
  await admin.dispose();
});

// ---- scenarios ----------------------------------------------------------------------------------------------------

/** #123: the product name, page titles and breadcrumbs are blocks, never one run-on line ("XRMverseSign in"). */
test('headings_do_not_run_together', async ({ page }) => {
  const below = async (upper: Locator, lower: Locator) => {
    const a = (await upper.boundingBox())!;
    const b = (await lower.boundingBox())!;
    expect(a.y + a.height).toBeLessThanOrEqual(b.y);
  };
  await page.goto('/manage/tables/account');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('XRMverse');
  await expect(page.getByRole('heading', { level: 2 })).toHaveText('Sign in');
  await below(page.getByRole('heading', { level: 1 }), page.getByRole('heading', { level: 2 }));

  await signIn(page, '/manage/tables/account');
  const title = page.getByRole('heading', { level: 2, name: 'Account (account)' });
  await expect(title).toBeVisible();
  await below(page.getByRole('navigation', { name: 'Breadcrumb' }).getByRole('link', { name: 'Tables' }), title);
});

test('admin_creates_user_and_assigns_role', async ({ page, browser }) => {
  const fullname = `E2E User ${uniqueSuffix()}`;
  const objectId = randomUUID();
  const bu = await rootBusinessUnit(admin);

  await signIn(page, '/manage/security/users');
  await page.getByRole('button', { name: 'New user' }).click();
  const create = page.getByRole('dialog', { name: 'New user' });
  await create.getByRole('textbox', { name: 'Full name', exact: true }).fill(fullname);
  await create.getByRole('textbox', { name: 'Entra object id (azureactivedirectoryobjectid)', exact: true }).fill(objectId);
  await pick(create, 'Business unit', bu.name);
  await create.getByRole('button', { name: 'Create user' }).click();
  await expect(toast(page, `User "${fullname}" created.`)).toBeVisible();
  const row = page.getByRole('row').filter({ hasText: fullname });
  await expect(row).toContainText(objectId);

  // Without a role, the new user is refused by the shell.
  const newUser = await otherUser(browser, '/manage', objectId, fullname);
  await expect(newUser.getByText('Access denied')).toBeVisible();
  await expect(newUser.getByText(/System Administrator or System Customizer/)).toBeVisible();

  // The administrator assigns System Customizer.
  await selectRow(page, fullname);
  await page.getByRole('button', { name: `Roles of ${fullname}` }).click();
  const roles = page.getByRole('dialog', { name: `Security roles of ${fullname}` });
  await expect(roles.getByText('No security roles are assigned.')).toBeVisible();
  await pick(roles, "Role (of the user's business unit)", SYSTEM_CUSTOMIZER);
  await roles.getByRole('button', { name: 'Assign role' }).click();
  await expect(toast(page, `Role "${SYSTEM_CUSTOMIZER}" assigned.`)).toBeVisible();
  await expect(roles.getByRole('table', { name: 'Roles of the user' })).toContainText(SYSTEM_CUSTOMIZER);

  // Now the new user opens the admin (as a customizer: no Environment section).
  watch(newUser);
  await newUser.reload();
  await expect(newUser.getByRole('heading', { name: 'Tables' })).toBeVisible();
  await expect(nav(newUser).getByRole('link', { name: 'Users' })).toBeVisible();
  await expect(nav(newUser).getByRole('link', { name: 'Environment' })).toHaveCount(0);
  await newUser.context().close();
});

test('admin_edits_role_privilege', async ({ page }) => {
  // A custom table made in the shell: privileges are named after its SchemaName (prefix and casing kept).
  const sfx = uniqueSuffix();
  const schemaName = `xv_Widget${sfx}`;
  const logicalName = schemaName.toLowerCase();
  const privilegeName = `prvRead${schemaName}`;
  await signIn(page, '/manage/tables');
  await createTable(page, `Widget ${sfx}`, schemaName);
  expect(await json<{ SchemaName: string }>(admin, `EntityDefinitions(LogicalName='${logicalName}')?$select=SchemaName`)).toMatchObject({
    SchemaName: schemaName,
  });

  const roleName = `E2E Role ${uniqueSuffix()}`;
  const bu = await rootBusinessUnit(admin);

  await nav(page).getByRole('link', { name: 'Security roles' }).click();
  await page.getByRole('button', { name: 'New role' }).click();
  const create = page.getByRole('dialog', { name: 'New security role' });
  await create.getByRole('textbox', { name: 'Name', exact: true }).fill(roleName);
  await pick(create, 'Business unit', bu.name);
  await create.getByRole('button', { name: 'Create role' }).click();
  await expect(toast(page, `Role "${roleName}" created.`)).toBeVisible();

  await selectRow(page, roleName);
  await page.getByRole('button', { name: `Privileges of ${roleName}` }).click();
  const drawer = page.getByRole('dialog', { name: `Privileges of ${roleName}` });
  await drawer.getByRole('textbox', { name: 'Table', exact: true }).fill(logicalName);
  await drawer.getByRole('option', { name: logicalName, exact: true }).click();
  const global = drawer.getByRole('radio', { name: `${privilegeName}: Global` });
  await expect(global).toBeEnabled();
  // A controlled radio: it shows the depth the server holds, so it turns checked only once the action succeeded.
  await global.click();
  await expect(toast(page, `${privilegeName} set to Global.`)).toBeVisible();
  await expect(global).toBeChecked();

  // What the server stored.
  const role = await json<{ value: { roleid: string }[] }>(admin, `roles?$select=roleid&$filter=name eq '${roleName}'`);
  expect(role.value).toHaveLength(1);
  const privilege = await json<{ value: { privilegeid: string }[] }>(admin, `privileges?$select=privilegeid&$filter=name eq '${privilegeName}'`);
  expect(privilege.value).toHaveLength(1);
  const granted = await json<{ value: { privilegedepthmask: number }[] }>(
    admin,
    `roleprivilegescollection?$select=privilegedepthmask&$filter=roleid eq ${role.value[0].roleid} and privilegeid eq ${privilege.value[0].privilegeid}`,
  );
  expect(granted.value).toEqual([expect.objectContaining({ privilegedepthmask: GLOBAL_MASK })]);
});

test('admin_creates_table_column_and_record', async ({ page }) => {
  const sfx = uniqueSuffix();
  const table = `e2e_t${sfx}`;
  const column = `e2e_notes${sfx}`;
  const recordName = `Record ${sfx}`;

  await signIn(page, '/manage/tables');
  await createTable(page, `E2E ${sfx}`, `e2e_T${sfx}`);
  await expect(page).toHaveURL(new RegExp(`/manage/tables/${table}$`));

  await page.getByRole('button', { name: 'Add column' }).click();
  const add = page.getByRole('dialog', { name: `Add a column to ${table}` });
  await add.getByRole('textbox', { name: 'Display name', exact: true }).fill('Notes');
  // The suggestion is <default publisher prefix>_Notes; this table's columns use the e2e prefix.
  await expect(add.getByRole('textbox', { name: 'Schema name', exact: true })).toHaveValue('new_Notes');
  await add.getByRole('textbox', { name: 'Schema name', exact: true }).fill(`e2e_Notes${sfx}`);
  await expect(add.getByRole('textbox', { name: 'Logical name', exact: true })).toHaveValue(column);
  await add.getByLabel('Type').selectOption('string');
  await add.getByRole('button', { name: 'Add' }).click();
  await expect(toast(page, `Column "${column}" added.`)).toBeVisible();
  await expect(page.getByRole('table', { name: 'Columns' }).getByRole('row').filter({ hasText: column })).toBeVisible();

  // Create a record.
  await page.getByRole('tab', { name: 'Records' }).click();
  await page.getByRole('button', { name: 'New record' }).click();
  await page.getByRole('textbox', { name: 'Name', exact: true }).fill(recordName);
  await page.getByRole('textbox', { name: 'Notes', exact: true }).fill('first');
  await page.getByRole('button', { name: 'Create' }).click();
  await expect(toast(page, 'Record created.')).toBeVisible();
  await expect(page).toHaveURL(new RegExp(`/manage/tables/${table}/records/[0-9a-f-]{36}$`));
  const id = page.url().split('/').pop()!;
  const set = (await json<{ EntitySetName: string }>(admin, `EntityDefinitions(LogicalName='${table}')?$select=EntitySetName`)).EntitySetName;
  const stored = await json<Record<string, unknown>>(admin, `${set}(${id})?$select=e2e_name,${column}`);
  expect(stored).toMatchObject({ e2e_name: recordName, [column]: 'first' });

  // Edit it.
  await page.getByRole('textbox', { name: 'Notes', exact: true }).fill('second');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(toast(page, 'Record saved.')).toBeVisible();
  expect((await json<Record<string, unknown>>(admin, `${set}(${id})?$select=${column}`))[column]).toBe('second');

  // Delete it from the list.
  await page.getByRole('navigation', { name: 'Breadcrumb' }).getByRole('link', { name: `E2E ${sfx}` }).click();
  await page.getByRole('tab', { name: 'Records' }).click();
  await selectRow(page, recordName);
  await page.getByRole('button', { name: `Delete ${recordName}` }).click();
  await page.getByRole('dialog', { name: 'Delete record' }).getByRole('button', { name: 'Delete record' }).click();
  await expect(toast(page, 'Record deleted.')).toBeVisible();
  await expect(page.getByText('There are no records.')).toBeVisible();
  expect((await admin.get(webApi(`${set}(${id})`))).status()).toBe(404);
});

test('admin_enables_auditing_and_sees_a_change_in_the_audit_page', async ({ page }) => {
  const sfx = uniqueSuffix();
  const table = `e2e_t${sfx}`;
  const org = (await json<{ value: { organizationid: string; isauditenabled: boolean }[] }>(admin, 'organizations?$select=organizationid,isauditenabled')).value[0];

  try {
    await signIn(page, '/manage/audit');
    const orgSwitch = page.getByRole('switch', { name: 'Auditing is enabled' });
    await expect(orgSwitch).toBeVisible();
    if (!org.isauditenabled) {
      await orgSwitch.click();
      await expect(toast(page, 'The audit settings were saved.')).toBeVisible();
    }
    await expect(orgSwitch).toBeChecked();

    // A table, audited, and a record created and changed.
    await page.getByRole('link', { name: 'Tables' }).click();
    await createTable(page, `E2E ${sfx}`, `e2e_T${sfx}`);
    await expect(page).toHaveURL(new RegExp(`/manage/tables/${table}$`));
    const tableSwitch = page.getByRole('switch', { name: 'Audit this table' });
    if (!(await tableSwitch.isChecked())) await tableSwitch.click();
    await expect(tableSwitch).toBeChecked();
    await page.getByRole('tab', { name: 'Records' }).click();
    await page.getByRole('button', { name: 'New record' }).click();
    await page.getByRole('textbox', { name: 'Name', exact: true }).fill('before');
    await page.getByRole('button', { name: 'Create' }).click();
    await expect(toast(page, 'Record created.')).toBeVisible();
    await page.getByRole('textbox', { name: 'Name', exact: true }).fill('after');
    await page.getByRole('button', { name: 'Save' }).click();
    await expect(toast(page, 'Record saved.')).toBeVisible();
    // The record form lists the change in its audit history.
    await expect(page.getByRole('table', { name: 'Audit history' }).getByRole('row').filter({ hasText: 'after' })).toBeVisible();

    // The Audit page lists the update and opens its old and new values.
    await page.getByRole('link', { name: 'Audit', exact: true }).click();
    await page.getByRole('textbox', { name: 'Table (logical name)', exact: true }).fill(table);
    const log = page.getByRole('table', { name: 'Audit log' });
    await expect(log.getByRole('row').filter({ hasText: 'Update' })).toHaveCount(1);
    await log.getByRole('row').filter({ hasText: 'Update' }).getByRole('button', { name: 'Show details' }).click();
    const drawer = page.getByRole('dialog');
    await expect(drawer.getByRole('row').filter({ hasText: 'e2e_name' })).toContainText('before');
    await expect(drawer.getByRole('row').filter({ hasText: 'e2e_name' })).toContainText('after');
  } finally {
    if (!org.isauditenabled) {
      const res = await admin.patch(webApi(`organizations(${org.organizationid})`), { data: { isauditenabled: false } });
      expect(res.status(), await res.text()).toBe(204);
    }
  }
});

/** A `width`×`height` RGB PNG (a gradient), built without an image library. */
function png(width: number, height: number): Buffer {
  const crcTable = Array.from({ length: 256 }, (_, n) => {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    return c >>> 0;
  });
  const crc = (data: Buffer) => {
    let c = 0xffffffff;
    for (const b of data) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8);
    return (c ^ 0xffffffff) >>> 0;
  };
  const chunk = (type: string, data: Buffer) => {
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    const length = Buffer.alloc(4);
    length.writeUInt32BE(data.length);
    const sum = Buffer.alloc(4);
    sum.writeUInt32BE(crc(body));
    return Buffer.concat([length, body, sum]);
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header.set([8, 2, 0, 0, 0], 8); // 8-bit RGB, no interlace
  const rows = Buffer.alloc((width * 3 + 1) * height);
  for (let y = 0; y < height; y++)
    for (let x = 0; x < width; x++) rows.set([x % 256, y % 256, 128], y * (width * 3 + 1) + 1 + x * 3);
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('IDAT', deflateSync(rows)),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

/** Adds a file or image column in the shell's Add column dialog. */
async function addFileColumn(page: Page, table: string, label: string, schemaName: string, type: 'file' | 'image', options: { maxSizeInKB?: number; fullImage?: boolean } = {}) {
  await page.getByRole('button', { name: 'Add column' }).click();
  const add = page.getByRole('dialog', { name: `Add a column to ${table}` });
  await add.getByRole('textbox', { name: 'Display name', exact: true }).fill(label);
  await expect(add.getByRole('textbox', { name: 'Schema name', exact: true })).toHaveValue(`new_${label}`);
  await add.getByRole('textbox', { name: 'Schema name', exact: true }).fill(schemaName);
  await add.getByLabel('Type').selectOption(type);
  if (options.maxSizeInKB !== undefined) await add.getByRole('textbox', { name: 'Maximum size (KB)' }).fill(String(options.maxSizeInKB));
  if (options.fullImage) await add.getByRole('checkbox', { name: 'Keep the full-size image' }).check();
  await add.getByRole('button', { name: 'Add' }).click();
  await expect(toast(page, `Column "${schemaName.toLowerCase()}" added.`)).toBeVisible();
}

test('admin_uploads_downloads_and_deletes_a_file_and_image', async ({ page }) => {
  const sfx = uniqueSuffix();
  const table = `e2e_f${sfx}`;
  const fileColumn = `e2e_contract${sfx}`;
  const imageColumn = `e2e_photo${sfx}`;
  const recordName = `Files ${sfx}`;
  // Over 4 MB: uploaded with the chunked protocol (two chunks).
  const big = Buffer.alloc(4 * 1024 * 1024 + 1000);
  for (let i = 0; i < big.length; i++) big[i] = (i * 7) % 256;
  const photo = png(300, 200);

  await signIn(page, '/manage/tables');
  await createTable(page, `Files ${sfx}`, `e2e_F${sfx}`);
  await addFileColumn(page, table, 'Contract', `e2e_Contract${sfx}`, 'file');
  await addFileColumn(page, table, 'Photo', `e2e_Photo${sfx}`, 'image', { maxSizeInKB: 2048, fullImage: true });
  const columns = page.getByRole('table', { name: 'Columns' });
  await expect(columns.getByRole('cell', { name: fileColumn, exact: true })).toBeVisible();
  await expect(columns.getByRole('cell', { name: imageColumn, exact: true })).toBeVisible();
  const set = (await json<{ EntitySetName: string }>(admin, `EntityDefinitions(LogicalName='${table}')?$select=EntitySetName`)).EntitySetName;
  const photoMeta = await json<{ MaxSizeInKB: number; CanStoreFullImage: boolean }>(
    admin,
    `EntityDefinitions(LogicalName='${table}')/Attributes(LogicalName='${imageColumn}')/Microsoft.Dynamics.CRM.ImageAttributeMetadata?$select=MaxSizeInKB,CanStoreFullImage`,
  );
  expect(photoMeta).toMatchObject({ MaxSizeInKB: 2048, CanStoreFullImage: true });

  // On the create form both are shown, disabled, with the reason.
  await page.getByRole('tab', { name: 'Records' }).click();
  await page.getByRole('button', { name: 'New record' }).click();
  const contract = page.getByRole('group', { name: 'Contract', exact: true });
  const image = page.getByRole('group', { name: 'Photo', exact: true });
  await expect(contract.getByText('Files and images can be added once the record is created.')).toBeVisible();
  await expect(contract.getByRole('button', { name: 'Upload Contract' })).toBeDisabled();
  await expect(image.getByRole('button', { name: 'Upload Photo' })).toBeDisabled();
  await page.getByRole('textbox', { name: 'Name', exact: true }).fill(recordName);
  await page.getByRole('button', { name: 'Create' }).click();
  await expect(toast(page, 'Record created.')).toBeVisible();
  await expect(page).toHaveURL(new RegExp(`/manage/tables/${table}/records/[0-9a-f-]{36}$`));
  const id = page.url().split('/').pop()!;

  // The file: uploaded in chunks, stored as sent, downloaded under its name.
  await expect(contract.getByText('No file')).toBeVisible();
  await contract.getByLabel('File for Contract').setInputFiles({ name: 'big.bin', mimeType: 'application/octet-stream', buffer: big });
  await expect(toast(page, 'File "big.bin" uploaded.')).toBeVisible();
  await expect(contract.getByText('big.bin', { exact: true })).toBeVisible();
  const stored = await admin.get(webApi(`${set}(${id})/${fileColumn}/$value`));
  expect(stored.status()).toBe(200);
  expect(stored.headers()['x-ms-file-name']).toBe('big.bin');
  expect(Buffer.from(await stored.body()).equals(big)).toBe(true);
  const fileDownload = page.waitForEvent('download');
  await contract.getByRole('button', { name: 'Download Contract' }).click();
  const saved = await fileDownload;
  expect(saved.suggestedFilename()).toBe('big.bin');
  expect(readFileSync(await saved.path()).equals(big)).toBe(true);

  // The image: its thumbnail is shown, its full image downloads as uploaded.
  await expect(image.getByText('No image')).toBeVisible();
  await image.getByLabel('File for Photo').setInputFiles({ name: 'photo.png', mimeType: 'image/png', buffer: photo });
  await expect(toast(page, 'Image "photo.png" uploaded.')).toBeVisible();
  const thumbnail = image.getByRole('img', { name: 'Photo thumbnail' });
  await expect(thumbnail).toBeVisible();
  expect(await thumbnail.evaluate((img: HTMLImageElement) => [img.naturalWidth, img.naturalHeight])).toEqual([144, 144]);
  const imageDownload = page.waitForEvent('download');
  await image.getByRole('button', { name: 'Download full Photo' }).click();
  const full = await imageDownload;
  expect(full.suggestedFilename()).toBe('photo.png');
  expect(readFileSync(await full.path()).equals(photo)).toBe(true);

  // Deletes: each after its confirmation.
  await contract.getByRole('button', { name: 'Delete Contract' }).click();
  await page.getByRole('dialog', { name: 'Delete file' }).getByRole('button', { name: 'Delete file' }).click();
  await expect(toast(page, 'Contract: the file was deleted.')).toBeVisible();
  await expect(contract.getByText('No file')).toBeVisible();
  await image.getByRole('button', { name: 'Delete Photo' }).click();
  await page.getByRole('dialog', { name: 'Delete image' }).getByRole('button', { name: 'Delete image' }).click();
  await expect(toast(page, 'Photo: the image was deleted.')).toBeVisible();
  await expect(image.getByText('No image')).toBeVisible();
  const after = await json<Record<string, unknown>>(admin, `${set}(${id})?$select=${fileColumn},${fileColumn}_name,${imageColumn}`);
  for (const name of [fileColumn, `${fileColumn}_name`, imageColumn]) expect(after[name] ?? null, name).toBeNull();
});

test('admin_imports_exports_and_deletes_solution', async ({ page }) => {
  await signIn(page, '/manage/solutions');
  await importFixture(page);
  expect(await entityExists(admin, 'xv_project')).toBe(true);

  // Export: the unmanaged solution file, named the Dataverse way.
  const download = page.waitForEvent('download');
  await selectRow(page, FIXTURE_NAME);
  await page.getByRole('button', { name: `Export ${FIXTURE_NAME}` }).click();
  const file = await download;
  expect(file.suggestedFilename()).toBe(`${FIXTURE}_1_0_0_0.zip`);
  const bytes = readFileSync(await file.path());
  expect(bytes.subarray(0, 2).toString('latin1')).toBe('PK');
  expect(bytes.includes(Buffer.from('solution.xml'))).toBe(true);
  await expect(toast(page, `Exported ${FIXTURE}_1_0_0_0.zip.`)).toBeVisible();

  // Delete (Dataverse): only the container goes; the tables stay.
  await selectRow(page, FIXTURE_NAME);
  await page.getByRole('button', { name: `Delete solution ${FIXTURE_NAME}`, exact: true }).click();
  await page.getByRole('dialog', { name: 'Delete solution' }).getByRole('button', { name: 'Delete solution' }).click();
  await expect(toast(page, `Solution "${FIXTURE_NAME}" deleted. Its components stay in the Default solution.`)).toBeVisible();
  await expect(page.getByRole('row').filter({ hasText: FIXTURE })).toHaveCount(0);
  expect(await entityExists(admin, 'xv_project')).toBe(true);
  expect(await entityExists(admin, 'xv_task')).toBe(true);

  // Import again, then delete with its components: the tables are dropped.
  await importFixture(page);
  await selectRow(page, FIXTURE_NAME);
  await page.getByRole('button', { name: `Delete solution ${FIXTURE_NAME} and its components` }).click();
  const cascade = page.getByRole('dialog', { name: 'Delete solution and its components' });
  await expect(cascade.getByText('Table: 3')).toBeVisible();
  const confirm = cascade.getByRole('button', { name: 'Delete solution and its components' });
  await expect(confirm).toBeDisabled();
  await cascade.getByRole('textbox', { name: `Type the unique name of the solution (${FIXTURE}) to confirm`, exact: true }).fill(FIXTURE);
  await confirm.click();
  const result = page.getByRole('dialog', { name: `Solution "${FIXTURE}" and its components were deleted` });
  await expect(result).toBeVisible();
  await expect(result.getByText('xv_project (Table)', { exact: true })).toBeVisible();
  await result.getByRole('button', { name: 'Close' }).click();
  await expect(page.getByRole('row').filter({ hasText: FIXTURE })).toHaveCount(0);
  expect(await entityExists(admin, 'xv_project')).toBe(false);
  expect(await entityExists(admin, 'xv_task')).toBe(false);
  expect(await entityExists(admin, 'xv_tag')).toBe(false);
});

test('customizer_has_no_environment_and_no_cascade', async ({ page }) => {
  const sfx = uniqueSuffix();
  const objectId = randomUUID();
  const fullname = `E2E Customizer ${sfx}`;
  await assignCustomizer(admin, await createUser(admin, objectId, fullname));

  // An unmanaged solution the customizer could act on.
  const publisher = await json<{ value: { publisherid: string }[] }>(admin, `publishers?$select=publisherid&$top=1`);
  const solutionName = `e2e_sol${sfx}`;
  const created = await admin.post(webApi('solutions'), {
    data: {
      uniquename: solutionName,
      friendlyname: `E2E Solution ${sfx}`,
      version: '1.0.0.0',
      'publisherid@odata.bind': `/publishers(${publisher.value[0].publisherid})`,
    },
  });
  expect(created.status(), await created.text()).toBe(204);

  await signIn(page, '/manage/tables', objectId, fullname);
  await expect(page.getByRole('heading', { name: 'Tables' })).toBeVisible();
  await expect(nav(page).getByRole('link', { name: 'Solutions' })).toBeVisible();
  await expect(nav(page).getByRole('link', { name: 'Environment' })).toHaveCount(0);

  await page.goto('/manage/environment');
  await expect(page.getByRole('heading', { name: 'Requires System Administrator' })).toBeVisible();

  await nav(page).getByRole('link', { name: 'Solutions' }).click();
  await selectRow(page, `E2E Solution ${sfx}`);
  await expect(page.getByRole('button', { name: `Delete solution E2E Solution ${sfx}`, exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: `Delete solution E2E Solution ${sfx} and its components` })).toHaveCount(0);

  // The server refuses the Admin API to a customizer with its explicit error.
  const customizer = await apiAs(objectId, fullname);
  for (const res of [await customizer.get('/admin/health'), await customizer.delete(`/admin/solutions/${solutionName}?cascade=true`)]) {
    expect(res.status()).toBe(403);
    expect((await res.json()) as unknown).toEqual({
      error: { code: '0x80040220', message: 'The Admin API requires the System Administrator role.' },
    });
  }
  await customizer.dispose();
});

test('plain_user_is_denied', async ({ page }) => {
  const objectId = randomUUID();
  const fullname = `E2E Plain ${uniqueSuffix()}`;
  await createUser(admin, objectId, fullname);

  // The server refuses a user without a security role; the shell shows its code and message under the denial.
  expectedFailures = ['403 GET /api/data/v9.2/WhoAmI'];
  const whoAmI = page.waitForResponse((r) => new URL(r.url()).pathname === '/api/data/v9.2/WhoAmI');
  await signIn(page, '/manage/tables', objectId, fullname);
  const refusal = (await (await whoAmI).json()) as { error: { code: string; message: string } };
  expect(refusal.error.code).toMatch(/^0x[0-9a-f]{8}$/i);
  await expect(page.getByText('Access denied')).toBeVisible();
  await expect(page.getByText(`${refusal.error.code}: ${refusal.error.message}`, { exact: true })).toBeVisible();
  await expect(
    page.getByText(
      'The web admin is available to users with the System Administrator or System Customizer security role. Ask an administrator to assign one of them to you.',
    ),
  ).toBeVisible();
  await expect(nav(page)).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Tables' })).toHaveCount(0);
});

test('deep_link_reload_works', async ({ page }) => {
  await signIn(page, '/manage/tables/systemuser');
  await expect(page.getByRole('table', { name: 'Columns' }).getByRole('row').filter({ hasText: 'azureactivedirectoryobjectid' })).toBeVisible();

  // A reload is answered by the host with the shell, which restores the session of this tab and the route.
  const reload = page.waitForResponse((r) => new URL(r.url()).pathname === '/manage/tables/systemuser');
  await page.reload();
  const response = await reload;
  expect(response.status()).toBe(200);
  expect(response.headers()['content-type']).toContain('text/html');
  await expect(page).toHaveURL(/\/manage\/tables\/systemuser$/);
  await expect(page.getByRole('table', { name: 'Columns' }).getByRole('row').filter({ hasText: 'azureactivedirectoryobjectid' })).toBeVisible();

  // Another deep link, typed into the address bar of the same tab.
  await page.goto('/manage/security/roles');
  await expect(page.getByRole('heading', { name: 'Security roles' })).toBeVisible();
  await expect(page.getByRole('table', { name: 'Security roles' })).toContainText(SYSTEM_CUSTOMIZER);
});

test('admin_secures_a_column_and_a_profile_lets_a_second_user_read_it', async ({ page, browser }) => {
  const sfx = uniqueSuffix();
  const schemaName = `e2e_Fs${sfx}`;
  const table = schemaName.toLowerCase();
  const column = `e2e_secret${sfx}`;
  const profileName = `E2E Readers ${sfx}`;
  const readerName = `E2E Reader ${sfx}`;
  const readerObjectId = randomUUID();

  // The administrator makes a table with a text column in the shell and secures the column there.
  await signIn(page, '/manage/tables');
  await createTable(page, `Fs ${sfx}`, schemaName);
  await page.getByRole('button', { name: 'Add column' }).click();
  const add = page.getByRole('dialog', { name: `Add a column to ${table}` });
  await add.getByRole('textbox', { name: 'Display name', exact: true }).fill('Secret');
  await add.getByRole('textbox', { name: 'Schema name', exact: true }).fill(`e2e_Secret${sfx}`);
  await add.getByLabel('Type').selectOption('string');
  await add.getByRole('button', { name: 'Add' }).click();
  await expect(toast(page, `Column "${column}" added.`)).toBeVisible();
  const secure = page.getByRole('switch', { name: `Secure ${column}` });
  await expect(secure).toBeEnabled();
  await expect(page.getByRole('switch', { name: 'Secure e2e_name' })).toBeDisabled();
  await secure.click();
  await expect(secure).toBeChecked();
  expect((await json<{ IsSecured: boolean }>(admin, `EntityDefinitions(LogicalName='${table}')/Attributes(LogicalName='${column}')`)).IsSecured).toBe(true);

  // A record, and a second user who reads the table (System Customizer for the shell, a role for the table).
  const set = (await json<{ EntitySetName: string }>(admin, `EntityDefinitions(LogicalName='${table}')?$select=EntitySetName`)).EntitySetName;
  const created = await admin.post(webApi(set), { data: { e2e_name: `Record ${sfx}`, [column]: 'classified' } });
  expect(created.status(), await created.text()).toBe(204);
  const recordId = /\(([0-9a-f-]{36})\)$/.exec(created.headers()['odata-entityid'] ?? '')![1];
  const readerId = await createUser(admin, readerObjectId, readerName);
  await assignCustomizer(admin, readerId);
  const bu = await rootBusinessUnit(admin);
  const role = await admin.post(webApi('roles'), { data: { name: `E2E Read ${sfx}`, 'businessunitid@odata.bind': `/businessunits(${bu.businessunitid})` } });
  expect(role.status(), await role.text()).toBe(204);
  const roleId = /\(([0-9a-f-]{36})\)$/.exec(role.headers()['odata-entityid'] ?? '')![1];
  const privilege = await json<{ value: { privilegeid: string }[] }>(admin, `privileges?$select=privilegeid&$filter=name eq 'prvRead${schemaName}'`);
  const granted = await admin.post(webApi(`roles(${roleId})/Microsoft.Dynamics.CRM.AddPrivilegesRole`), {
    data: { Privileges: [{ Depth: 'Global', PrivilegeId: privilege.value[0].privilegeid, BusinessUnitId: bu.businessunitid }] },
  });
  expect(granted.status(), await granted.text()).toBe(204);
  const assigned = await admin.post(webApi(`systemusers(${readerId})/systemuserroles_association/$ref`), {
    data: { '@odata.id': `${env.baseURL}${webApi(`roles(${roleId})`)}` },
  });
  expect(assigned.status(), await assigned.text()).toBe(204);

  // Without a profile, the second user sees the record but not the secured column.
  const reader = await otherUser(browser, `/manage/tables/${table}/records/${recordId}`, readerObjectId, readerName);
  watch(reader);
  await expect(reader.getByRole('textbox', { name: 'Name', exact: true })).toHaveValue(`Record ${sfx}`);
  await expect(reader.getByText('No access', { exact: true })).toBeVisible();
  await expect(reader.getByRole('img', { name: 'Secured column' })).toBeVisible();
  await expect(reader.getByRole('textbox', { name: 'Secret', exact: true })).toHaveCount(0);

  // The administrator creates a profile that reads the column and gives it to the second user.
  await nav(page).getByRole('link', { name: 'Field security' }).click();
  await page.getByRole('button', { name: 'New profile' }).click();
  const create = page.getByRole('dialog', { name: 'New field security profile' });
  await create.getByRole('textbox', { name: 'Name', exact: true }).fill(profileName);
  await create.getByRole('button', { name: 'Create profile' }).click();
  await expect(toast(page, `Profile "${profileName}" created.`)).toBeVisible();
  await selectRow(page, profileName);
  await page.getByRole('button', { name: `Permissions of ${profileName}` }).click();
  const read = page.getByRole('table', { name: `Permissions of ${profileName}` }).getByRole('checkbox', { name: `Read ${table}.${column}` });
  await read.click();
  await expect(read).toBeChecked();
  await expect(read).toBeEnabled();
  await page.getByRole('button', { name: `Users of ${profileName}` }).click();
  const users = page.getByRole('dialog', { name: `Users holding ${profileName}` });
  await expect(users.getByText('No user holds this profile.')).toBeVisible();
  await pick(users, 'User', readerName);
  await users.getByRole('button', { name: 'Give to user' }).click();
  await expect(toast(page, `Profile given to "${readerName}".`)).toBeVisible();
  await expect(users.getByRole('table', { name: 'Users of the profile' })).toContainText(readerName);

  // Now the second user reads the column.
  await reader.reload();
  await expect(reader.getByRole('textbox', { name: 'Secret', exact: true })).toHaveValue('classified');
  await expect(reader.getByText('No access', { exact: true })).toHaveCount(0);
  await reader.context().close();
});
