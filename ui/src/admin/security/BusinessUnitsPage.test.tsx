import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { API, filterOf, onPath, signInAsAdministrator } from './testSupport';

const ROOT = 'bbbbbbbb-0000-0000-0000-000000000001';
const SALES = 'bbbbbbbb-0000-0000-0000-000000000002';
const EMEA = 'bbbbbbbb-0000-0000-0000-000000000003';

let created: unknown;
let patched: Record<string, unknown> | undefined;

beforeEach(() => {
  created = undefined;
  patched = undefined;
  signInAsAdministrator();
  server.use(
    http.get(`${API}/businessunits`, ({ request }) => {
      const filter = filterOf(request);
      if (filter === '_parentbusinessunitid_value eq null') return HttpResponse.json({ value: [{ businessunitid: ROOT, name: 'Acme', isdisabled: false }] });
      if (filter === `_parentbusinessunitid_value eq ${ROOT}`)
        return HttpResponse.json({ value: [{ businessunitid: SALES, name: 'Sales', isdisabled: false }] });
      if (filter === `_parentbusinessunitid_value eq ${SALES}`)
        return HttpResponse.json({ value: [{ businessunitid: EMEA, name: 'EMEA', isdisabled: true }] });
      return HttpResponse.json({ value: [] });
    }),
    http.post(`${API}/businessunits`, async ({ request }) => {
      created = await request.json();
      return new HttpResponse(null, { status: 204 });
    }),
  );
});
afterEach(() => sessionStorage.clear());

describe('BusinessUnitsPage', () => {
  it('Bu_tree_renders_hierarchy', async () => {
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/businessunits']} />);
    const tree = await screen.findByRole('tree', { hidden: true, name: en['nav.businessUnits'] });
    // The root is open; its child is listed beneath it and is closed until expanded.
    const root = within(tree).getByText('Acme').closest('li')!;
    expect(root).toHaveAttribute('aria-expanded', 'true');
    const sales = (await within(root).findByText('Sales')).closest('li')!;
    expect(root.contains(sales)).toBe(true);
    expect(sales).toHaveAttribute('aria-expanded', 'false');
    expect(within(sales).queryByText('EMEA')).toBeNull();
    await user.click(within(sales).getByRole('button', { hidden: true, name: 'Expand Sales' }));
    const emea = (await within(sales).findByText('EMEA')).closest('li')!;
    expect(sales.contains(emea)).toBe(true);
    expect(within(emea).getByText(new RegExp(en['bu.status.disabled'].replace(/[()]/g, '\\$&')))).toBeInTheDocument();
  });

  it('Create_child_posts_parent_bind_and_shows_the_new_unit', async () => {
    let children = [{ businessunitid: SALES, name: 'Sales', isdisabled: false }];
    server.use(
      http.get(`${API}/businessunits`, ({ request }) => {
        const filter = filterOf(request);
        if (filter === '_parentbusinessunitid_value eq null') return HttpResponse.json({ value: [{ businessunitid: ROOT, name: 'Acme', isdisabled: false }] });
        if (filter === `_parentbusinessunitid_value eq ${ROOT}`) return HttpResponse.json({ value: children });
        return HttpResponse.json({ value: [] });
      }),
      http.post(`${API}/businessunits`, async ({ request }) => {
        created = await request.json();
        children = [...children, { businessunitid: EMEA, name: 'Support', isdisabled: false }];
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/businessunits']} />);
    await user.click(await screen.findByText('Acme'));
    await user.click(await screen.findByRole('button', { hidden: true, name: 'Add child to Acme' }));
    await user.type(await screen.findByLabelText(new RegExp(`^${en['bu.field.name']}`)), 'Support');
    await user.click(screen.getByRole('button', { hidden: true, name: en['bu.child.submit'] }));
    expect(await screen.findByText('Support')).toBeInTheDocument();
    expect(created).toEqual({ name: 'Support', 'parentbusinessunitid@odata.bind': `/businessunits(${ROOT})` });
  });

  it('Rename_patches_the_name', async () => {
    server.use(
      onPath('patch', `businessunits(${SALES})`, async ({ request }) => {
        patched = (await request.json()) as Record<string, unknown>;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/businessunits']} />);
    await user.click(await screen.findByText('Sales'));
    expect(screen.getByText('Sales').closest('li')).toHaveAttribute('aria-selected', 'true');
    await user.click(await screen.findByRole('button', { hidden: true, name: 'Rename Sales' }));
    const input = await screen.findByLabelText(new RegExp(`^${en['bu.field.name']}`));
    await user.clear(input);
    await user.type(input, 'Revenue');
    await user.click(screen.getByRole('button', { hidden: true, name: en['bu.rename.submit'] }));
    expect(await screen.findByText('Business unit renamed to "Revenue".')).toBeInTheDocument();
    expect(patched).toEqual({ name: 'Revenue' });
  });

  it('Disable_confirms_then_sets_isdisabled', async () => {
    server.use(
      onPath('patch', `businessunits(${SALES})`, async ({ request }) => {
        patched = (await request.json()) as Record<string, unknown>;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/businessunits']} />);
    await user.click(await screen.findByText('Sales'));
    await user.click(await screen.findByRole('button', { hidden: true, name: 'Disable Sales' }));
    expect(patched).toBeUndefined();
    await user.click(await screen.findByRole('button', { hidden: true, name: en['bu.disable.confirm'] }));
    expect(await screen.findByText('Business unit "Sales" disabled.')).toBeInTheDocument();
    expect(patched).toEqual({ isdisabled: true });
  });

  it('Disable_refused_by_server_shows_its_code_and_message', async () => {
    server.use(
      onPath('patch', `businessunits(${ROOT})`, () =>
        HttpResponse.json({ error: { code: '0x80040217', message: 'Could not find attribute isdisabled of entity businessunit.' } }, { status: 400 }),
      ),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/businessunits']} />);
    await user.click(await screen.findByText('Acme'));
    await user.click(await screen.findByRole('button', { hidden: true, name: 'Disable Acme' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: en['bu.disable.confirm'] }));
    expect(await screen.findByText('0x80040217')).toBeInTheDocument();
    expect(screen.getByText('Could not find attribute isdisabled of entity businessunit.')).toBeInTheDocument();
  });
});
