import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { API, onPath, signInAsAdministrator } from '../security/testSupport';

const PATH = '/manage/currencies';
const EUR = 'c0000000-0000-0000-0000-000000000001';
const USD = 'c0000000-0000-0000-0000-000000000002';
const ORG = 'b0000000-0000-0000-0000-000000000001';

interface Row {
  transactioncurrencyid: string;
  isocurrencycode: string;
  currencyname?: string;
  currencysymbol?: string;
  currencyprecision?: number;
  exchangerate?: number;
  currencytype: number;
  statecode: number;
}

let rows: Row[] = [];
let precision = 2;
let posts: Record<string, unknown>[] = [];
let patches: { path: string; body: Record<string, unknown> }[] = [];
let refuse: string | undefined;
let failListing = false;

const row = (id: string, code: string, name: string, symbol: string, rate: number, statecode = 0): Row => ({
  transactioncurrencyid: id,
  isocurrencycode: code,
  currencyname: name,
  currencysymbol: symbol,
  currencyprecision: 2,
  exchangerate: rate,
  currencytype: 0,
  statecode,
});

function mockServer() {
  rows = [row(EUR, 'EUR', 'Euro', '€', 1), row(USD, 'USD', 'US Dollar', '$', 1.1)];
  precision = 2;
  posts = [];
  patches = [];
  refuse = undefined;
  failListing = false;
  server.use(
    // The Header reads the organization name; the page reads the base currency and the precision from the same set.
    http.get(`${API}/organizations`, () =>
      HttpResponse.json({ value: [{ name: 'Acme Dev', organizationid: ORG, _basecurrencyid_value: EUR, pricingdecimalprecision: precision }] }),
    ),
    onPath('get', 'transactioncurrencies', () =>
      failListing ? HttpResponse.json({ error: { code: '0x80040216', message: 'listing failed' } }, { status: 500 }) : HttpResponse.json({ value: rows }),
    ),
    onPath('post', 'transactioncurrencies', async ({ request }) => {
      const body = (await request.json()) as Record<string, unknown>;
      posts.push(body);
      if (refuse) return HttpResponse.json({ error: { code: '0x80048cf3', message: refuse } }, { status: 400 });
      rows = [...rows, { ...row('c0000000-0000-0000-0000-000000000003', String(body.isocurrencycode), String(body.currencyname), String(body.currencysymbol), Number(body.exchangerate)) }];
      return new HttpResponse(null, { status: 204 });
    }),
    onPath('patch', `transactioncurrencies(${USD})`, async ({ request }) => {
      const body = (await request.json()) as Record<string, unknown>;
      patches.push({ path: 'usd', body });
      if (refuse) return HttpResponse.json({ error: { code: '0x80048cfd', message: refuse } }, { status: 400 });
      rows = rows.map((r) =>
        r.transactioncurrencyid === USD
          ? { ...r, exchangerate: body.exchangerate === undefined ? r.exchangerate : Number(body.exchangerate), statecode: body.statecode === undefined ? r.statecode : Number(body.statecode) }
          : r,
      );
      return new HttpResponse(null, { status: 204 });
    }),
    onPath('patch', `organizations(${ORG})`, async ({ request }) => {
      const body = (await request.json()) as Record<string, unknown>;
      patches.push({ path: 'org', body });
      precision = Number(body.pricingdecimalprecision);
      return new HttpResponse(null, { status: 204 });
    }),
  );
}

beforeEach(() => {
  signInAsAdministrator();
  mockServer();
});
afterEach(() => sessionStorage.clear());

async function open() {
  render(<App initialEntries={[PATH]} />);
  // Both mount requests answered: the rows are there and the command bar is enabled.
  expect(await screen.findByText('US Dollar')).toBeInTheDocument();
  await waitFor(() => expect(screen.getByRole('button', { name: en['currencies.precisionOpen'] })).toBeEnabled());
}

const rowOf = (code: string) => screen.getByRole('row', { name: new RegExp(`\\b${code}\\b`) });
type User = ReturnType<typeof userEvent.setup>;
const selectRow = (user: User, code: string) => user.click(screen.getByRole('radio', { hidden: true, name: `Select ${code}` }));

/** Selects the currency and opens its exchange rate dialog from the command bar. */
async function openRate(user: User, code: string) {
  await selectRow(user, code);
  // hidden: true, as a dialog closed just before may still hide the page while its close transition runs.
  await user.click(await screen.findByRole('button', { hidden: true, name: `Edit exchange rate of ${code}` }));
  return screen.findByRole('dialog', { hidden: true });
}

const rateInput = (dialog: HTMLElement, code: string) => within(dialog).getByLabelText(new RegExp(`^Exchange rate of ${code}`));
const saveButton = (dialog: HTMLElement) => within(dialog).getByRole('button', { hidden: true, name: en['currencies.saveRate'] });

describe('CurrenciesPage', () => {
  it('lists currencies with base badge, read-only', async () => {
    await open();
    expect(screen.getAllByText(en['nav.currencies']).length).toBeGreaterThan(1);
    expect(within(rowOf('EUR')).getByText(en['currencies.base'])).toBeInTheDocument();
    expect(within(rowOf('USD')).queryByText(en['currencies.base'])).toBeNull();
    expect(within(rowOf('USD')).getByText('1.1')).toBeInTheDocument();
    expect(within(screen.getByRole('table')).queryByRole('textbox')).toBeNull();
    expect(within(screen.getByRole('table')).queryByRole('button')).toBeNull();
  });

  it('creates a currency', async () => {
    const user = userEvent.setup();
    await open();
    await user.click(screen.getByRole('button', { name: en['currencies.new.button'] }));
    // The dialog is in the DOM at once, but the role query skips it while Fluent's open transition keeps it inaccessible; under load
    // that outlasts any fixed timeout. Ask for it with hidden: true, as the confirmation dialog tests do.
    const dialog = await screen.findByRole('dialog', { hidden: true });
    await user.type(within(dialog).getByLabelText(/^Currency code/), 'gbp');
    await user.type(within(dialog).getByLabelText(/^Name/), 'Pound');
    await user.type(within(dialog).getByLabelText(/^Symbol/), '£');
    await user.type(within(dialog).getByLabelText(/^Exchange rate/), '0.85');
    await user.click(within(dialog).getByRole('button', { hidden: true, name: en['currencies.new.submit'] }));
    await waitFor(() => expect(posts).toHaveLength(1));
    expect(posts[0]).toEqual({ isocurrencycode: 'GBP', currencytype: 0, currencyname: 'Pound', currencysymbol: '£', currencyprecision: 2, exchangerate: 0.85 });
    expect(await screen.findByText('Pound')).toBeInTheDocument();
  });

  it('edits a rate of the selected currency', async () => {
    const user = userEvent.setup();
    await open();
    expect(screen.queryByRole('button', { name: 'Edit exchange rate of USD' })).toBeNull();
    const dialog = await openRate(user, 'USD');
    const input = rateInput(dialog, 'USD');
    expect(input).toHaveValue('1.1');
    expect(saveButton(dialog)).toBeDisabled();
    await user.clear(input);
    await user.type(input, '1.25');
    await user.click(saveButton(dialog));
    await waitFor(() => expect(patches).toEqual([{ path: 'usd', body: { exchangerate: 1.25 } }]));
    await waitFor(() => expect(within(rowOf('USD')).getByText('1.25')).toBeInTheDocument());
    await waitFor(() => expect(screen.queryByRole('dialog', { hidden: true })).toBeNull());
  });

  it('refuses a rate that is not a number without asking the server', async () => {
    const user = userEvent.setup();
    await open();
    const dialog = await openRate(user, 'USD');
    await user.clear(rateInput(dialog, 'USD'));
    await user.type(rateInput(dialog, 'USD'), 'abc');
    await user.click(saveButton(dialog));
    expect(await within(dialog).findByText(en['currencies.err.rate'].replace('{value}', 'abc'))).toBeInTheDocument();
    expect(patches).toEqual([]);
  });

  it('base currency: rate command disabled with the reason, no deactivate', async () => {
    const user = userEvent.setup();
    await open();
    await selectRow(user, 'EUR');
    const edit = screen.getByRole('button', { name: 'Edit exchange rate of EUR' });
    expect(edit).toBeDisabled();
    expect(edit).toHaveAttribute('title', en['currencies.baseHint']);
    expect(screen.queryByRole('button', { name: 'Deactivate EUR' })).toBeNull();
    await selectRow(user, 'USD');
    expect(screen.getByRole('button', { name: 'Edit exchange rate of USD' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Deactivate USD' })).toBeEnabled();
  });

  it('deactivates after confirmation and activates again', async () => {
    const user = userEvent.setup();
    await open();
    await selectRow(user, 'USD');
    await user.click(screen.getByRole('button', { name: 'Deactivate USD' }));
    const dialog = await screen.findByRole('dialog', { hidden: true });
    expect(patches).toEqual([]);
    await user.click(within(dialog).getByRole('button', { hidden: true, name: en['currencies.deactivate'] }));
    await waitFor(() => expect(patches).toEqual([{ path: 'usd', body: { statecode: 1, statuscode: 2 } }]));
    await user.click(await screen.findByRole('button', { name: 'Activate USD' }));
    await waitFor(() => expect(patches).toHaveLength(2));
    expect(patches[1].body).toEqual({ statecode: 0, statuscode: 1 });
    expect(await screen.findByRole('button', { name: 'Deactivate USD' })).toBeInTheDocument();
  });

  it('saves the pricing precision', async () => {
    const user = userEvent.setup();
    await open();
    await user.click(screen.getByRole('button', { name: en['currencies.precisionOpen'] }));
    const dialog = await screen.findByRole('dialog', { hidden: true });
    const field = within(dialog).getByLabelText(new RegExp(`^${en['currencies.pricingPrecision']}`));
    expect(field).toHaveValue('2');
    await user.selectOptions(field, '4');
    await user.click(within(dialog).getByRole('button', { hidden: true, name: en['currencies.precisionSave'] }));
    await waitFor(() => expect(patches).toEqual([{ path: 'org', body: { pricingdecimalprecision: 4 } }]));
    await waitFor(() => expect(screen.queryByRole('dialog', { hidden: true })).toBeNull());
    await user.click(await screen.findByRole('button', { hidden: true, name: en['currencies.precisionOpen'] }));
    const again = await screen.findByRole('dialog', { hidden: true });
    expect(within(again).getByLabelText(new RegExp(`^${en['currencies.pricingPrecision']}`))).toHaveValue('4');
  });

  it('shows server refusal', async () => {
    const user = userEvent.setup();
    await open();
    refuse = 'The exchange rate is outside the allowed range.';
    const dialog = await openRate(user, 'USD');
    await user.clear(rateInput(dialog, 'USD'));
    await user.type(rateInput(dialog, 'USD'), '0');
    await user.click(saveButton(dialog));
    // The refusal is in the dialog, which stays open with what was typed and stays usable.
    expect(await within(dialog).findByText(/outside the allowed range/)).toBeInTheDocument();
    expect(rows.find((r) => r.transactioncurrencyid === USD)?.exchangerate).toBe(1.1);
    expect(rateInput(dialog, 'USD')).toHaveValue('0');
    await waitFor(() => expect(saveButton(dialog)).toBeEnabled());
  });

  it('shows a refused create in the dialog', async () => {
    const user = userEvent.setup();
    await open();
    refuse = "A currency with the code 'USD' already exists.";
    await user.click(screen.getByRole('button', { name: en['currencies.new.button'] }));
    // The dialog is in the DOM at once, but the role query skips it while Fluent's open transition keeps it inaccessible; under load
    // that outlasts any fixed timeout. Ask for it with hidden: true, as the confirmation dialog tests do.
    const dialog = await screen.findByRole('dialog', { hidden: true });
    await user.type(within(dialog).getByLabelText(/^Currency code/), 'usd');
    await user.type(within(dialog).getByLabelText(/^Name/), 'Dollar');
    await user.type(within(dialog).getByLabelText(/^Symbol/), '$');
    await user.type(within(dialog).getByLabelText(/^Exchange rate/), '1.2');
    await user.click(within(dialog).getByRole('button', { hidden: true, name: en['currencies.new.submit'] }));
    await waitFor(() => expect(posts).toHaveLength(1));
    // The refusal is in the dialog, which stays open with what was typed.
    expect(await within(dialog).findByText(/already exists/)).toBeInTheDocument();
    expect(within(dialog).getByLabelText(/^Name/)).toHaveValue('Dollar');
    expect(within(dialog).getByRole('button', { hidden: true, name: en['currencies.new.submit'] })).toBeEnabled();
  });

  it('tolerates a currency without rate, precision or symbol', async () => {
    // The Web API leaves null columns out of the response.
    rows = [...rows, { transactioncurrencyid: 'c0000000-0000-0000-0000-000000000009', isocurrencycode: 'QNR', currencyname: 'No rate', currencytype: 1, statecode: 0 }];
    const user = userEvent.setup();
    await open();
    expect(within(rowOf('QNR')).queryByText('undefined')).toBeNull();
    const dialog = await openRate(user, 'QNR');
    expect(rateInput(dialog, 'QNR')).toHaveValue('');
    expect(saveButton(dialog)).toBeDisabled();
    await user.type(rateInput(dialog, 'QNR'), '2');
    expect(saveButton(dialog)).toBeEnabled();
  });

  it('keeps pricing precision save disabled when the organization has none', async () => {
    server.use(
      http.get(`${API}/organizations`, () => HttpResponse.json({ value: [{ name: 'Acme Dev', organizationid: ORG, _basecurrencyid_value: EUR }] })),
    );
    const user = userEvent.setup();
    await open();
    await user.click(screen.getByRole('button', { name: en['currencies.precisionOpen'] }));
    const dialog = await screen.findByRole('dialog', { hidden: true });
    const field = within(dialog).getByLabelText(new RegExp(`^${en['currencies.pricingPrecision']}`));
    const save = within(dialog).getByRole('button', { hidden: true, name: en['currencies.precisionSave'] });
    expect(field).toHaveValue('');
    expect(save).toBeDisabled();
    await user.selectOptions(field, '2');
    expect(save).toBeEnabled();
  });

  it('clears the load error after a successful reload', async () => {
    const user = userEvent.setup();
    await open();
    failListing = true;
    let dialog = await openRate(user, 'USD');
    await user.clear(rateInput(dialog, 'USD'));
    await user.type(rateInput(dialog, 'USD'), '1.25');
    await user.click(saveButton(dialog));
    expect(await screen.findByText(/listing failed/)).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('dialog', { hidden: true })).toBeNull());
    failListing = false;
    // USD is still selected: the command bar opens its dialog again.
    await user.click(await screen.findByRole('button', { hidden: true, name: 'Edit exchange rate of USD' }));
    dialog = await screen.findByRole('dialog', { hidden: true });
    await user.clear(rateInput(dialog, 'USD'));
    await user.type(rateInput(dialog, 'USD'), '1.3');
    await user.click(saveButton(dialog));
    await waitFor(() => expect(screen.queryByText(/listing failed/)).toBeNull());
    // The list reloads after the save: wait for its rows instead of reading them in the same tick.
    await waitFor(() => expect(within(rowOf('USD')).getByText('1.3')).toBeInTheDocument());
  });

  it('hidden for non-admin', async () => {
    const CUSTOMIZER = '119f245c-3cc8-4b62-b31c-d1a046ced15d';
    server.use(
      http.get(`${API}/systemuserrolescollection`, () => HttpResponse.json({ value: [{ roleid: CUSTOMIZER }] })),
      http.get(`${API}/roles`, () => HttpResponse.json({ value: [{ roleid: CUSTOMIZER, name: 'Role', roletemplateid: CUSTOMIZER }] })),
    );
    render(<App initialEntries={[PATH]} />);
    expect(await screen.findByText('Acme Dev')).toBeInTheDocument();
    expect(await screen.findByText(en['route.adminOnly.title'])).toBeInTheDocument();
    expect(within(screen.getByRole('navigation')).queryByText(en['nav.currencies'])).toBeNull();
    expect(screen.queryByText('US Dollar')).toBeNull();
  });
});
