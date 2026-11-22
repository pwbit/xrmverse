import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { viewUrl } from '../../apps/EntityListPage';
import { setUiLanguage } from '../../shell/i18n/t';
import { ACTIVE, APP_ID, CUSTOMERS, MINE, openAccounts } from './viewTestSupport';

afterEach(() => {
  sessionStorage.clear();
  setUiLanguage(undefined);
});

const selector = (name: string) => screen.findByRole('button', { name: `Select a view: ${name}` });

describe('ViewSelector', () => {
  it('selector lists public views, a separator, then personal views, and opens the default', async () => {
    const { fetches } = openAccounts({
      savedQueries: [CUSTOMERS, ACTIVE, { ...ACTIVE, id: 'a0000000-0000-0000-0000-000000000002', name: 'All Accounts', isdefault: false }],
      userQueries: [{ ...MINE, name: 'Zeta mine' }, MINE],
    });
    // isdefault wins over the first by name ("All Accounts").
    await userEvent.click(await selector('Active Accounts'));
    await screen.findByRole('grid', { name: 'Active Accounts' });
    expect(fetches[0].querySelector('link-entity')).not.toBeNull();
    const menu = await screen.findByRole('menu');
    // Fluent's MenuDivider is presentational (role="presentation", aria-hidden).
    const entries = [...menu.querySelectorAll('[role="menuitemradio"], .fui-MenuDivider')].map((e) =>
      e.classList.contains('fui-MenuDivider') ? '---' : e.textContent,
    );
    expect(entries).toEqual(['Active Accounts', 'All Accounts', 'Customers with Projects', '---', 'My big accounts', 'Zeta mine']);
    expect(screen.getByRole('menuitemradio', { name: 'Active Accounts' })).toHaveAttribute('aria-checked', 'true');
  });

  it('without an isdefault view, the first by name opens', async () => {
    openAccounts({ savedQueries: [{ ...ACTIVE, isdefault: false }, { ...CUSTOMERS, name: 'Aardvark' }] });
    expect(await selector('Aardvark')).toBeInTheDocument();
    await screen.findByRole('grid', { name: 'Aardvark' });
  });

  it('viewid in the URL wins', async () => {
    const { fetches } = openAccounts({ query: `&viewid=%7B${CUSTOMERS.id.toUpperCase()}%7D&viewType=1039` });
    expect(await selector('Customers with Projects')).toBeInTheDocument();
    await screen.findByRole('grid', { name: 'Customers with Projects' });
    expect(fetches[0].querySelector('condition[attribute="op365_iscustomer"]')).not.toBeNull();
  });

  it('a personal viewid in the URL wins too', async () => {
    openAccounts({ query: `&viewid=${MINE.id}&viewType=4230` });
    expect(await selector('My big accounts')).toBeInTheDocument();
    await screen.findByRole('grid', { name: 'My big accounts' });
  });

  it('an unknown viewid opens the default', async () => {
    openAccounts({ query: '&viewid=99999999-0000-0000-0000-000000000009&viewType=1039' });
    expect(await selector('Active Accounts')).toBeInTheDocument();
    await screen.findByRole('grid', { name: 'Active Accounts' });
  });

  it('switching view shows its columns and resets paging', async () => {
    const { fetches } = openAccounts({
      answer: () => ({ value: [{ accountid: 'f1', name: 'Contoso' }], '@Microsoft.Dynamics.CRM.morerecords': true, '@Microsoft.Dynamics.CRM.fetchxmlpagingcookie': '<cookie pagenumber="1" pagingcookie="%253ccookie%252f%253e" istracking="False" />' }),
    });
    await screen.findByRole('grid', { name: 'Active Accounts' });
    await userEvent.click(screen.getByRole('button', { name: 'Next page' }));
    await waitFor(() => expect(fetches).toHaveLength(2));
    await userEvent.click(await selector('Active Accounts'));
    await userEvent.click(await screen.findByRole('menuitemradio', { name: 'My big accounts' }));
    await screen.findByRole('grid', { name: 'My big accounts' });
    await waitFor(() => expect(fetches).toHaveLength(3));
    expect(fetches[2].getAttribute('page')).toBe('1');
    expect(fetches[2].hasAttribute('paging-cookie')).toBe(false);
    expect(fetches[2].querySelector('link-entity')).toBeNull();
  });

  it('a view URL names the view and its type (1039 public, 4230 personal)', () => {
    expect(viewUrl(APP_ID, 'account', { id: 'v1', personal: false })).toBe(
      `/main.aspx?appid=${APP_ID}&pagetype=entitylist&etn=account&viewid=v1&viewType=1039`,
    );
    expect(viewUrl(APP_ID, 'account', { id: 'v2', personal: true })).toBe(
      `/main.aspx?appid=${APP_ID}&pagetype=entitylist&etn=account&viewid=v2&viewType=4230`,
    );
  });
});
