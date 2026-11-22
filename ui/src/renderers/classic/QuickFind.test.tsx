import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { setUiLanguage } from '../../shell/i18n/t';
import { ACTIVE, CUSTOMERS, openAccounts, rows } from './viewTestSupport';

afterEach(() => {
  sessionStorage.clear();
  setUiLanguage(undefined);
});

const quickValues = (fetch: Element) =>
  [...fetch.querySelectorAll('filter[isquickfindfields="1"] > condition')].map((c) => c.getAttribute('value'));
const headerNames = () => within(screen.getByRole('grid')).getAllByRole('columnheader').map((h) => h.textContent);

describe('QuickFind', () => {
  it('quick find runs the querytype 4 view with the text', async () => {
    const { fetches } = openAccounts({
      answer: () => ({ value: rows(50), '@Microsoft.Dynamics.CRM.morerecords': true, '@Microsoft.Dynamics.CRM.fetchxmlpagingcookie': '<cookie pagenumber="1" pagingcookie="%253ccookie%252f%253e" istracking="False" />' }),
    });
    await screen.findByRole('grid', { name: 'Active Accounts' });
    await userEvent.click(screen.getByRole('button', { name: 'Next page' }));
    await waitFor(() => expect(fetches).toHaveLength(2));

    await userEvent.type(screen.getByRole('searchbox', { name: 'Search this view' }), 'Cont{Enter}');
    await waitFor(() => expect(fetches).toHaveLength(3));
    expect(quickValues(fetches[2])).toEqual(['Cont%', 'Cont%', 'Cont%']);
    // Paging starts over.
    expect(fetches[2].getAttribute('page')).toBe('1');
    expect(fetches[2].hasAttribute('paging-cookie')).toBe(false);
    // The quick find view's columns (no Email link column).
    await waitFor(() => expect(headerNames()).toEqual(['Account Name', 'VAT Number', 'Primary Contact']));

    // Clearing goes back to the selected view.
    await userEvent.clear(screen.getByRole('searchbox', { name: 'Search this view' }));
    await waitFor(() => expect(fetches).toHaveLength(4));
    expect(fetches[3].querySelector('filter[isquickfindfields="1"]')).toBeNull();
    expect(fetches[3].querySelector('link-entity[alias="pc"]')).not.toBeNull();
    await screen.findByRole('grid', { name: 'Active Accounts' });
    await waitFor(() => expect(headerNames()).toEqual(['Account Name', 'VAT Number', 'Primary Contact', 'Email (Primary Contact)']));
  });

  it("quick find text with ', %, _ and < reaches the query as typed", async () => {
    const { fetches } = openAccounts();
    await screen.findByRole('grid', { name: 'Active Accounts' });
    await userEvent.type(screen.getByRole('searchbox', { name: 'Search this view' }), "O'B<r%_*{Enter}");
    await waitFor(() => expect(fetches).toHaveLength(2));
    expect(quickValues(fetches[1])).toEqual(["O'B<r%_%", "O'B<r%_%", "O'B<r%_%"]);
    expect(screen.queryByText(/could not be loaded/)).toBeNull();
  });

  it('switching view and back starts over: no search, page 1, empty box', async () => {
    const { fetches } = openAccounts({
      answer: () => ({ value: rows(50), '@Microsoft.Dynamics.CRM.morerecords': true, '@Microsoft.Dynamics.CRM.fetchxmlpagingcookie': '<cookie pagenumber="1" pagingcookie="%253ccookie%252f%253e" istracking="False" />' }),
    });
    await screen.findByRole('grid', { name: 'Active Accounts' });
    await userEvent.type(screen.getByRole('searchbox', { name: 'Search this view' }), 'foo{Enter}');
    await waitFor(() => expect(fetches).toHaveLength(2));
    await screen.findByRole('grid', { name: 'Quick Find Active Accounts' });
    await userEvent.click(screen.getByRole('button', { name: 'Next page' }));
    await waitFor(() => expect(fetches).toHaveLength(3));
    await screen.findByRole('grid', { name: 'Quick Find Active Accounts' });

    await userEvent.click(screen.getByRole('button', { name: 'Select a view: Active Accounts' }));
    await userEvent.click(await screen.findByRole('menuitemradio', { name: 'My big accounts' }));
    await screen.findByRole('grid', { name: 'My big accounts' });
    await waitFor(() => expect(fetches).toHaveLength(4));
    await userEvent.click(screen.getByRole('button', { name: 'Select a view: My big accounts' }));
    await userEvent.click(await screen.findByRole('menuitemradio', { name: 'Active Accounts' }));
    await screen.findByRole('grid', { name: 'Active Accounts' });
    await waitFor(() => expect(fetches).toHaveLength(5));
    const back = fetches[4];
    expect(back.querySelector('filter[isquickfindfields="1"]')).toBeNull();
    expect(back.querySelector('link-entity[alias="pc"]')).not.toBeNull();
    expect(back.getAttribute('page')).toBe('1');
    expect(back.hasAttribute('paging-cookie')).toBe(false);
    expect(screen.getByRole('searchbox', { name: 'Search this view' })).toHaveValue('');
    expect(screen.getByRole('button', { name: 'Previous page' })).toBeDisabled();
  });

  it('a table without one hides the search box', async () => {
    openAccounts({ savedQueries: [ACTIVE, CUSTOMERS] });
    await screen.findByRole('grid', { name: 'Active Accounts' });
    expect(screen.queryByRole('searchbox')).toBeNull();
  });
});
