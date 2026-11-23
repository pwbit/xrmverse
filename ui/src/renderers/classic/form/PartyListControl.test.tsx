import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import type { Party } from '../../../admin/tables/partyLists';
import { onPath } from '../../../admin/security/testSupport';
import { attr } from '../../../admin/tables/testAttrs';
import { mockApps, USER_ID } from '../../../apps/testSupport';
import { XrmHostContext, type XrmHost } from '../../../client-api/xrm';
import { server } from '../../../test/server';
import { Providers } from './formTestSupport';
import { PartyListControl, type SenderPolicy } from './PartyListControl';

const ADA = 'c0000000-0000-0000-0000-000000000001';
const ADA2 = 'c0000000-0000-0000-0000-000000000002';
const SUPPORT = 'a0000000-0000-0000-0000-000000000001';
const SALES = 'a0000000-0000-0000-0000-000000000002';

const META: Record<string, Record<string, unknown>> = {
  contact: { EntitySetName: 'contacts', PrimaryIdAttribute: 'contactid', PrimaryNameAttribute: 'fullname', DisplayCollectionName: { UserLocalizedLabel: { Label: 'Contacts' } } },
  queue: { EntitySetName: 'queues', PrimaryIdAttribute: 'queueid', PrimaryNameAttribute: 'name', DisplayCollectionName: { UserLocalizedLabel: { Label: 'Queues' } } },
};

interface Row {
  [k: string]: string;
}
const ROWS: Record<string, Row[]> = {
  contacts: [
    { contactid: ADA, fullname: 'Ada Lovelace', emailaddress1: 'ada@example.com' },
    { contactid: ADA2, fullname: 'Ada Byron', emailaddress1: 'shared@example.com' },
    { contactid: 'c0000000-0000-0000-0000-000000000003', fullname: 'Charles', emailaddress1: 'shared@example.com' },
  ],
  queues: [
    { queueid: SUPPORT, name: 'Support', emailaddress: 'support@example.com' },
    { queueid: SALES, name: 'Sales', emailaddress: 'sales@example.com' },
  ],
};

/** A tiny evaluator of the filters the control sends: `contains(col,'x')`, `col eq 'x'`, joined by `or`. */
function matches(row: Row, filter: string | null): boolean {
  if (!filter) return true;
  return filter.split(' or ').some((clause) => {
    const contains = /^contains\((\w+),'(.*)'\)$/.exec(clause.trim());
    if (contains) return (row[contains[1]!] ?? '').toLowerCase().includes(contains[2]!.toLowerCase());
    const eq = /^(\w+) eq '(.*)'$/.exec(clause.trim());
    if (eq) return (row[eq[1]!] ?? '').toLowerCase() === eq[2]!.toLowerCase();
    return false;
  });
}

function mockSearch() {
  mockApps();
  const searches: string[] = [];
  server.use(
    http.get('*/api/data/v9.2/:path', ({ request, params }) => {
      const path = decodeURIComponent(String(params.path));
      const meta = /^EntityDefinitions\(LogicalName='(\w+)'\)$/.exec(path);
      if (meta) return META[meta[1]!] ? HttpResponse.json(META[meta[1]!]) : HttpResponse.json({ error: { message: 'nope' } }, { status: 404 });
      const rows = ROWS[path];
      if (!rows) return undefined;
      const url = new URL(request.url);
      const filter = url.searchParams.get('$filter');
      searches.push(`${path}?${filter ?? ''}&top=${url.searchParams.get('$top') ?? ''}`);
      const top = Number(url.searchParams.get('$top') ?? 50);
      return HttpResponse.json({ value: rows.filter((r) => matches(r, filter)).slice(0, top) });
    }),
    onPath('get', `systemusers(${USER_ID})`, () => HttpResponse.json({ systemuserid: USER_ID, fullname: 'Anna Rossi', internalemailaddress: 'anna@example.com' })),
  );
  return searches;
}

const to = attr('to', { kind: 'partylist', typeName: 'PartyList', displayName: 'To', targets: ['contact', 'queue'] });
const from = attr('from', { kind: 'partylist', typeName: 'PartyList', displayName: 'From', targets: ['contact', 'queue', 'systemuser'] });

function Harness({ initial = [], onValue, ...rest }: { initial?: Party[]; onValue?: (v: Party[]) => void; column?: typeof to; disabled?: boolean; sender?: SenderPolicy }) {
  const [value, setValue] = useState<Party[]>(initial);
  return (
    <PartyListControl
      attr={rest.column ?? to}
      value={value}
      disabled={rest.disabled}
      sender={rest.sender}
      onChange={(v) => {
        setValue(v);
        onValue?.(v);
      }}
    />
  );
}

function renderControl(props: Parameters<typeof Harness>[0] = {}, lookupObjects?: (o: unknown) => Promise<unknown>) {
  const host = lookupObjects ? ({ xrm: { Utility: { lookupObjects } } } as unknown as XrmHost) : null;
  return render(
    <Providers>
      <XrmHostContext.Provider value={host}>
        <Harness {...props} />
      </XrmHostContext.Provider>
    </Providers>,
  );
}

const input = () => screen.findByRole('combobox', { name: 'To' });
const chips = () => within(screen.getByRole('list', { name: 'To' })).queryAllByRole('listitem').map((li) => li.textContent);

describe('PartyListControl', () => {
  it('searches the primary name and the email column of each target, 5 per table, grouped', async () => {
    const searches = mockSearch();
    const onValue = vi.fn();
    renderControl({ onValue });
    await userEvent.type(await input(), 'ada');
    const list = await screen.findByRole('listbox', { name: 'To' });
    expect(within(list).getByRole('group', { name: 'Contacts' })).toBeInTheDocument();
    await waitFor(() => expect(searches).toEqual(expect.arrayContaining([
      "contacts?contains(fullname,'ada') or contains(emailaddress1,'ada')&top=5",
      "queues?contains(name,'ada') or contains(emailaddress,'ada')&top=5",
    ])));
    await userEvent.click(within(list).getByRole('option', { name: /Ada Lovelace/ }));
    expect(onValue).toHaveBeenLastCalledWith([{ id: ADA, logicalName: 'contact', name: 'Ada Lovelace', address: 'ada@example.com' }]);
    expect(chips()).toEqual(['Ada Lovelace']);
    expect(await input()).toHaveValue('');
  });

  it('keeps a typed address that matches no record as an unresolved chip', async () => {
    mockSearch();
    const onValue = vi.fn();
    renderControl({ onValue });
    await userEvent.type(await input(), 'nobody@example.com{Enter}');
    await waitFor(() => expect(onValue).toHaveBeenLastCalledWith([{ address: 'nobody@example.com' }]));
    expect(chips()[0]).toContain('nobody@example.com');
    expect(chips()[0]).toContain('unresolved');
  });

  it('resolves an address that matches exactly one record', async () => {
    mockSearch();
    const onValue = vi.fn();
    renderControl({ onValue });
    await userEvent.type(await input(), 'support@example.com{Enter}');
    await waitFor(() => expect(onValue).toHaveBeenLastCalledWith([{ id: SUPPORT, logicalName: 'queue', name: 'Support', address: 'support@example.com' }]));
  });

  it('asks when an address matches several records', async () => {
    mockSearch();
    const onValue = vi.fn();
    renderControl({ onValue });
    await userEvent.type(await input(), 'shared@example.com{Enter}');
    const list = await screen.findByRole('listbox', { name: 'To' });
    expect(within(list).getByText(/Several records use shared@example.com/)).toBeInTheDocument();
    await userEvent.click(within(list).getByRole('option', { name: /Charles/ }));
    expect(onValue).toHaveBeenLastCalledWith([{ id: 'c0000000-0000-0000-0000-000000000003', logicalName: 'contact', name: 'Charles', address: 'shared@example.com' }]);
  });

  it('adds one chip per pasted address', async () => {
    mockSearch();
    const onValue = vi.fn();
    renderControl({ onValue });
    const box = await input();
    act(() => {
      fireEvent.paste(box, { clipboardData: { getData: () => 'one@example.com; two@example.com, support@example.com' } });
    });
    await waitFor(() =>
      expect(onValue).toHaveBeenLastCalledWith([
        { address: 'one@example.com' },
        { address: 'two@example.com' },
        { id: SUPPORT, logicalName: 'queue', name: 'Support', address: 'support@example.com' },
      ]),
    );
  });

  it('removes a chip', async () => {
    mockSearch();
    const onValue = vi.fn();
    renderControl({ onValue, initial: [{ id: ADA, logicalName: 'contact', name: 'Ada Lovelace' }, { address: 'x@example.com' }] });
    await userEvent.click(await screen.findByRole('button', { name: 'Remove Ada Lovelace' }));
    expect(onValue).toHaveBeenLastCalledWith([{ address: 'x@example.com' }]);
  });

  it('read-only: the chips without remove, no search', async () => {
    mockSearch();
    renderControl({ disabled: true, initial: [{ id: ADA, logicalName: 'contact', name: 'Ada Lovelace' }, { address: 'x@example.com' }] }, vi.fn());
    await screen.findByRole('list', { name: 'To' });
    expect(chips()).toHaveLength(2);
    expect(screen.queryByRole('button', { name: /Remove/ })).toBeNull();
    expect(screen.queryByRole('combobox', { name: 'To' })).toBeNull();
    expect(screen.queryByRole('button', { name: /Search/ })).toBeNull();
  });

  it('the search button opens the lookup dialog on the targets, multi-select, and skips duplicates', async () => {
    mockSearch();
    const onValue = vi.fn();
    const lookupObjects = vi.fn(async () => [
      { id: `{${ADA.toUpperCase()}}`, name: 'Ada Lovelace', entityType: 'contact' },
      { id: `{${SALES.toUpperCase()}}`, name: 'Sales', entityType: 'queue' },
    ]);
    renderControl({ onValue, initial: [{ id: ADA, logicalName: 'contact', name: 'Ada Lovelace' }] }, lookupObjects);
    await userEvent.click(await screen.findByRole('button', { name: 'Search To' }));
    expect(lookupObjects).toHaveBeenCalledWith(expect.objectContaining({ entityTypes: ['contact', 'queue'], allowMultiSelect: true }));
    await waitFor(() =>
      expect(onValue).toHaveBeenLastCalledWith([
        { id: ADA, logicalName: 'contact', name: 'Ada Lovelace' },
        { id: SALES, logicalName: 'queue', name: 'Sales' },
      ]),
    );
  });

  it('from takes one party: the caller and the readable queues', async () => {
    mockSearch();
    const onValue = vi.fn();
    render(
      <Providers>
        <Harness column={from} sender={{ mode: 'userOrQueue', userId: USER_ID }} onValue={onValue} />
      </Providers>,
    );
    await userEvent.click(await screen.findByRole('button', { name: 'Choose From' }));
    const list = await screen.findByRole('listbox', { name: 'From' });
    await waitFor(() => expect(within(list).getAllByRole('option').map((o) => o.textContent)).toEqual([
      expect.stringContaining('Anna Rossi'),
      expect.stringContaining('Sales'),
      expect.stringContaining('Support'),
    ]));
    await userEvent.click(within(list).getByRole('option', { name: /Support/ }));
    expect(onValue).toHaveBeenLastCalledWith([{ id: SUPPORT, logicalName: 'queue', name: 'Support', address: 'support@example.com' }]);
    // One party: choosing again replaces it.
    await userEvent.click(screen.getByRole('button', { name: 'Choose From' }));
    await userEvent.click(within(await screen.findByRole('listbox', { name: 'From' })).getByRole('option', { name: /Anna Rossi/ }));
    expect(onValue).toHaveBeenLastCalledWith([{ id: USER_ID, logicalName: 'systemuser', name: 'Anna Rossi', address: 'anna@example.com' }]);
  });

  it('from offers only queues when the environment sends from queues only', async () => {
    mockSearch();
    render(
      <Providers>
        <Harness column={from} sender={{ mode: 'queueOnly', userId: USER_ID }} />
      </Providers>,
    );
    await userEvent.click(await screen.findByRole('button', { name: 'Choose From' }));
    const list = await screen.findByRole('listbox', { name: 'From' });
    await waitFor(() => expect(within(list).getAllByRole('option')).toHaveLength(3));
    const user = within(list).getByRole('option', { name: /Anna Rossi/ });
    expect(user).toHaveAttribute('aria-disabled', 'true');
    expect(user.textContent).toContain('not allowed by the environment setting');
  });
});
