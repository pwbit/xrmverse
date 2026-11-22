import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { API, onPath, signInAsAdministrator } from '../security/testSupport';

const FV = 'OData.Community.Display.V1.FormattedValue';

const row = (n: number, table: string, extra: object = {}) => ({
  auditid: `00000000-0000-0000-0000-00000000000${n}`,
  createdon: `2026-11-0${n}T10:00:00Z`,
  objecttypecode: table,
  _objectid_value: `r${n}`,
  [`action@${FV}`]: 'Update',
  [`operation@${FV}`]: 'Update',
  [`_userid_value@${FV}`]: 'Ada Admin',
  _userid_value: 'u1',
  ...extra,
});

let orgPatch: unknown;
let tableFilters: (string | null)[];
beforeEach(() => {
  signInAsAdministrator();
  orgPatch = undefined;
  tableFilters = [];
  server.use(
    http.get(`${API}/organizations`, () =>
      HttpResponse.json({ value: [{ name: 'Acme Dev', organizationid: 'o1', isauditenabled: true, auditretentionperiodv2: 30 }] }),
    ),
    onPath('patch', 'organizations(o1)', async ({ request }) => {
      orgPatch = await request.json();
      return new HttpResponse(null, { status: 204 });
    }),
    http.get(`${API}/audits`, ({ request }) => {
      const filter = new URL(request.url).searchParams.get('$filter');
      tableFilters.push(filter);
      if (filter?.includes("objecttypecode eq 'contact'")) return HttpResponse.json({ value: [row(3, 'contact')] });
      return HttpResponse.json({ value: [row(2, 'account'), row(1, 'contact')] });
    }),
  );
});
afterEach(() => sessionStorage.clear());

describe('AuditPage', () => {
  it('AuditPage lists audits newest first and filters by table', async () => {
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/audit']} />);
    const list = await screen.findByRole('table', { name: en['audit.list.title'] });
    // The organization switch has loaded too: no request is left in flight.
    await waitFor(() => expect(screen.getByRole('switch', { name: en['audit.org.enabled'] })).toBeChecked());
    const rows = within(list).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(2);
    expect(within(rows[0]).getByText('account')).toBeInTheDocument();
    expect(within(rows[1]).getByText('contact')).toBeInTheDocument();

    await user.type(screen.getByLabelText(en['audit.filter.table']), 'contact');
    await waitFor(() => expect(within(screen.getByRole('table', { name: en['audit.list.title'] })).queryByText('account')).toBeNull());
    expect(tableFilters.at(-1)).toBe("objecttypecode eq 'contact'");
    expect(within(screen.getByRole('table', { name: en['audit.list.title'] })).getAllByRole('row')).toHaveLength(2);
  });

  it('AuditPage opens the detail drawer with old and new values', async () => {
    server.use(
      onPath('get', 'audits(00000000-0000-0000-0000-000000000002)/Microsoft.Dynamics.CRM.RetrieveAuditDetails', () =>
        HttpResponse.json({
          AuditDetail: {
            '@odata.type': '#Microsoft.Dynamics.CRM.AttributeAuditDetail',
            OldValue: { name: 'Acme', revenue: 1 },
            NewValue: { name: 'Acme Corp', revenue: 2 },
          },
        }),
      ),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/audit']} />);
    await screen.findByRole('table', { name: en['audit.list.title'] });
    await waitFor(() => expect(screen.getByRole('switch', { name: en['audit.org.enabled'] })).toBeChecked());
    await user.click(screen.getAllByRole('button', { name: en['audit.row.open'] })[0]);
    expect(await screen.findByText('Acme Corp', undefined, { timeout: 3000 })).toBeInTheDocument();
    expect(screen.getByText('Acme')).toBeInTheDocument();
    expect(screen.getByText('revenue')).toBeInTheDocument();
  });

  it('AuditPage shows the privilege message on 403', async () => {
    server.use(
      http.get(`${API}/audits`, () =>
        HttpResponse.json({ error: { code: '0x80040220', message: 'Access is denied.' } }, { status: 403 }),
      ),
    );
    render(<App initialEntries={['/manage/audit']} />);
    // The privilege is named by our own text, not only by what the server says.
    expect(await screen.findByText(/prvReadAuditSummary/)).toBeInTheDocument();
    expect(screen.getByText(/Access is denied\./)).toBeInTheDocument();
    expect(screen.queryByRole('table', { name: en['audit.list.title'] })).toBeNull();
    expect(screen.queryByText(en['audit.empty'])).toBeNull();
    await waitFor(() => expect(screen.getByRole('switch', { name: en['audit.org.enabled'] })).toBeChecked());
  });

  it('AuditPage saves the retention and the organization switch', async () => {
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/audit']} />);
    const retention = await screen.findByLabelText(en['audit.org.retention']);
    await waitFor(() => expect(retention).toHaveValue(30));
    await user.clear(retention);
    await user.type(retention, '90');
    await user.click(screen.getByRole('button', { name: en['audit.org.saveRetention'] }));
    await waitFor(() => expect(orgPatch).toEqual({ auditretentionperiodv2: 90 }));
    await user.click(screen.getByRole('switch', { name: en['audit.org.enabled'] }));
    await waitFor(() => expect(orgPatch).toEqual({ isauditenabled: false }));
    await screen.findByRole('table', { name: en['audit.list.title'] });
  });
});
