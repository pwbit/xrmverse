import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../../test/server';
import { setUiLanguage } from '../../shell/i18n/t';
import { FV, openAccounts, orderOf, ROW, rows } from './viewTestSupport';

afterEach(() => {
  sessionStorage.clear();
  setUiLanguage(undefined);
});

const grid = () => screen.findByRole('grid', { name: 'Active Accounts' });
const headers = async () => within(await grid()).getAllByRole('columnheader');
const cellsOf = (rowIndex: number) => within(screen.getAllByRole('row')[rowIndex]).getAllByRole('gridcell').map((c) => c.textContent);

describe('ViewGrid', () => {
  it('grid shows LayoutXml columns in order with their widths', async () => {
    openAccounts();
    const cells = await headers();
    // The hidden accountid cell is not rendered.
    expect(cells.map((h) => h.textContent)).toEqual(['Account Name', 'VAT Number', 'Primary Contact', 'Email (Primary Contact)']);
    expect(cells.map((h) => h.style.width)).toEqual(['250px', '130px', '180px', '200px']);
  });

  it("header labels in the user's language", async () => {
    openAccounts({ lcid: 1040 });
    await screen.findByRole('heading', { name: 'Aziende' });
    // Italian when the attribute has it, else the 1033 label.
    expect((await headers()).map((h) => h.textContent)).toEqual(['Nome account', 'Partita IVA', 'Referente principale', 'Email (Referente principale)']);
  });

  it('formatted values and lookup names are shown', async () => {
    openAccounts({
      answer: () => ({ value: [{ ...ROW, op365_vatnumber: 1234, [`op365_vatnumber${FV}`]: '1.234' }] }),
    });
    await grid();
    await waitFor(() => expect(cellsOf(1)).toEqual(['Contoso', '1.234', 'Ada Lovelace', 'ada@example.com']));
    // No GUID of the lookup anywhere.
    expect(screen.queryByText(ROW._primarycontactid_value)).toBeNull();
  });

  it('a link-entity column (alias.attr) is shown', async () => {
    const { fetches } = openAccounts();
    expect(await screen.findByText('ada@example.com')).toBeInTheDocument();
    // The link-entity of the view is sent as is.
    expect(fetches[0].querySelector('link-entity[alias="pc"] > attribute')?.getAttribute('name')).toBe('emailaddress1');
  });

  it('its header sorts inside the link-entity (#100)', async () => {
    const { fetches } = openAccounts();
    await screen.findByText('ada@example.com');
    const email = (await headers())[3];
    expect(email).toHaveAttribute('aria-sort', 'none');
    await userEvent.click(within(email).getByRole('button'));
    await waitFor(() => expect(fetches).toHaveLength(2));
    const orders = [...fetches[1].getElementsByTagName('order')];
    expect(orders.map((o) => [o.parentElement!.getAttribute('alias'), o.getAttribute('attribute'), o.getAttribute('descending')])).toEqual([
      ['pc', 'emailaddress1', 'false'],
    ]);
    await waitFor(async () => expect((await headers())[3]).toHaveAttribute('aria-sort', 'ascending'));
    expect((await headers())[0]).toHaveAttribute('aria-sort', 'none');
  });

  it('a masked field-security column shows an empty cell', async () => {
    // The Web API leaves out a secured column the caller cannot read.
    const masked = Object.fromEntries(Object.entries(ROW).filter(([key]) => key !== 'op365_vatnumber'));
    openAccounts({ answer: () => ({ value: [masked] }) });
    await screen.findByText('Contoso');
    expect(cellsOf(1)).toEqual(['Contoso', '', 'Ada Lovelace', 'ada@example.com']);
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('header click sorts ascending, then descending, and resets paging', async () => {
    const cookie = '<cookie page="1"><accountid last="{X}" first="{Y}" /></cookie>';
    const { fetches } = openAccounts({
      answer: () => ({
        value: rows(50),
        '@Microsoft.Dynamics.CRM.fetchxmlpagingcookie': `<cookie pagenumber="1" pagingcookie="${encodeURIComponent(encodeURIComponent(cookie))}" istracking="False" />`,
        '@Microsoft.Dynamics.CRM.morerecords': true,
      }),
    });
    await screen.findByText('Account 50');
    expect(orderOf(fetches[0])).toEqual(['name asc']);
    await userEvent.click(screen.getByRole('button', { name: 'Next page' }));
    await waitFor(() => expect(fetches).toHaveLength(2));
    expect(fetches[1].getAttribute('page')).toBe('2');

    await userEvent.click(within((await headers())[1]).getByRole('button'));
    await waitFor(() => expect(fetches).toHaveLength(3));
    expect(orderOf(fetches[2])).toEqual(['op365_vatnumber asc']);
    expect(fetches[2].getAttribute('page')).toBe('1');
    expect(fetches[2].hasAttribute('paging-cookie')).toBe(false);
    await waitFor(async () => expect((await headers())[1]).toHaveAttribute('aria-sort', 'ascending'));

    await userEvent.click(within((await headers())[1]).getByRole('button'));
    await waitFor(() => expect(fetches).toHaveLength(4));
    expect(orderOf(fetches[3])).toEqual(['op365_vatnumber desc']);
    expect(fetches[3].getAttribute('page')).toBe('1');
    await waitFor(async () => expect((await headers())[1]).toHaveAttribute('aria-sort', 'descending'));
  });

  it('next and previous use the paging cookie; the label is "1 - 50 of 213" or "1 - 50"', async () => {
    const cookieOf = (page: number) => `<cookie page="${page}"><accountid last="{L${page}}" first="{F${page}}" /></cookie>`;
    const { fetches } = openAccounts({
      answer: (fetch) => {
        const page = Number(fetch.getAttribute('page'));
        return {
          value: rows(50, (page - 1) * 50 + 1),
          '@Microsoft.Dynamics.CRM.totalrecordcount': fetch.getAttribute('returntotalrecordcount') === 'true' ? 213 : -1,
          '@Microsoft.Dynamics.CRM.fetchxmlpagingcookie': `<cookie pagenumber="${page}" pagingcookie="${encodeURIComponent(encodeURIComponent(cookieOf(page)))}" istracking="False" />`,
          '@Microsoft.Dynamics.CRM.morerecords': true,
        };
      },
    });
    expect(await screen.findByText('1 - 50 of 213')).toBeInTheDocument();
    expect(fetches[0].getAttribute('count')).toBe('50');
    expect(fetches[0].hasAttribute('paging-cookie')).toBe(false);
    expect(screen.getByRole('button', { name: 'Previous page' })).toBeDisabled();

    await userEvent.click(screen.getByRole('button', { name: 'Next page' }));
    expect(await screen.findByText('51 - 100 of 213')).toBeInTheDocument();
    expect(fetches[1].getAttribute('page')).toBe('2');
    expect(fetches[1].getAttribute('paging-cookie')).toBe(cookieOf(1));

    await userEvent.click(screen.getByRole('button', { name: 'Next page' }));
    expect(await screen.findByText('101 - 150 of 213')).toBeInTheDocument();
    expect(fetches[2].getAttribute('paging-cookie')).toBe(cookieOf(2));

    await userEvent.click(screen.getByRole('button', { name: 'Previous page' }));
    expect(await screen.findByText('51 - 100 of 213')).toBeInTheDocument();
    expect(fetches[3].getAttribute('page')).toBe('2');
    expect(fetches[3].getAttribute('paging-cookie')).toBe(cookieOf(1));

    await userEvent.click(screen.getByRole('button', { name: 'First page' }));
    expect(await screen.findByText('1 - 50 of 213')).toBeInTheDocument();
    expect(fetches[4].getAttribute('page')).toBe('1');
    expect(fetches[4].hasAttribute('paging-cookie')).toBe(false);
  });

  it('without a total the label is "1 - 50"; the last page disables next', async () => {
    openAccounts({ answer: () => ({ value: rows(50) }) });
    expect(await screen.findByText('1 - 50')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next page' })).toBeDisabled();
  });

  it('page size follows usersettings.paginglimit', async () => {
    const { fetches } = openAccounts({ pagingLimit: 25 });
    await screen.findByText('Contoso');
    expect(fetches[0].getAttribute('count')).toBe('25');
  });

  it('a paginglimit outside 25/50/75/100/250 pages by 50', async () => {
    const { fetches } = openAccounts({ pagingLimit: 30 });
    await screen.findByText('Contoso');
    expect(fetches[0].getAttribute('count')).toBe('50');
  });

  /** The row opens the record page; these tests need no form: the page says so. */
  const NO_FORM = 'No form of this table is available in this app.';
  const withoutForms = () => server.use(http.get('*/api/data/v9.2/systemforms', () => HttpResponse.json({ value: [] })));

  it('a row click opens the record page', async () => {
    openAccounts();
    withoutForms();
    await userEvent.click(await screen.findByText('ada@example.com'));
    expect(await screen.findByText(NO_FORM)).toBeInTheDocument();
  });

  it('the first cell is a link-styled button that opens the record', async () => {
    openAccounts();
    withoutForms();
    await userEvent.click(await screen.findByRole('button', { name: 'Contoso' }));
    expect(await screen.findByText(NO_FORM)).toBeInTheDocument();
  });

  it('Enter on the first-cell link opens the record', async () => {
    openAccounts();
    withoutForms();
    const link = await screen.findByRole('button', { name: 'Contoso' });
    link.focus();
    await userEvent.keyboard('{Enter}');
    expect(await screen.findByText(NO_FORM)).toBeInTheDocument();
  });

  it('next and previous page the inline link-entity view with its cookie, link columns included', async () => {
    const cookieOf = (page: number) => `<cookie page="${page}"><accountid last="{L${page}}" first="{F${page}}" /></cookie>`;
    const { fetches } = openAccounts({
      answer: (fetch) => {
        const page = Number(fetch.getAttribute('page'));
        return {
          value: [{ ...ROW, accountid: `f000000${page}-0000-0000-0000-000000000001`, 'pc.emailaddress1': `p${page}@example.com` }],
          '@Microsoft.Dynamics.CRM.fetchxmlpagingcookie': `<cookie pagenumber="${page}" pagingcookie="${encodeURIComponent(encodeURIComponent(cookieOf(page)))}" istracking="False" />`,
          '@Microsoft.Dynamics.CRM.morerecords': true,
        };
      },
    });
    expect(await screen.findByText('p1@example.com')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Next page' }));
    expect(await screen.findByText('p2@example.com')).toBeInTheDocument();
    expect(fetches[1].getAttribute('page')).toBe('2');
    expect(fetches[1].getAttribute('paging-cookie')).toBe(cookieOf(1));
    expect(fetches[1].querySelector('link-entity[alias="pc"] > attribute')?.getAttribute('name')).toBe('emailaddress1');
    await userEvent.click(screen.getByRole('button', { name: 'Next page' }));
    expect(await screen.findByText('p3@example.com')).toBeInTheDocument();
    expect(fetches[2].getAttribute('paging-cookie')).toBe(cookieOf(2));
    await userEvent.click(screen.getByRole('button', { name: 'Previous page' }));
    expect(await screen.findByText('p2@example.com')).toBeInTheDocument();
    expect(fetches[3].getAttribute('page')).toBe('2');
    expect(fetches[3].getAttribute('paging-cookie')).toBe(cookieOf(1));
    expect(fetches[3].querySelector('link-entity[alias="pc"]')).not.toBeNull();
  });

  it('a records read failure shows an error, not a blank grid', async () => {
    openAccounts({ fail: 'accounts' });
    const text = await screen.findByText(/accounts read failed/);
    expect(text.closest('.fui-MessageBar')).not.toBeNull();
  });

  it('a table with no view says so', async () => {
    openAccounts({ savedQueries: [], userQueries: [] });
    expect(await screen.findByText('This table has no view that you may open.')).toBeInTheDocument();
  });
});
