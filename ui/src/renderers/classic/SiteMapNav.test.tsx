import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import type { SiteMapModel } from '../../model/sitemap';
import { en } from '../../shell/i18n/en';
import { SiteMapNav } from './SiteMapNav';

const MODEL: SiteMapModel = {
  areas: [
    {
      id: 'sales',
      titles: { 1033: 'Sales', 1040: 'Vendite' },
      groups: [
        {
          id: 'customers',
          titles: { 1033: 'Customers', 1040: 'Clienti' },
          subAreas: [
            { id: 'accounts', titles: { 1040: 'Aziende', 1033: 'Accounts' }, kind: 'entity', entity: 'account', privileges: [] },
            { id: 'contacts', titles: { 1033: 'Contacts' }, kind: 'entity', entity: 'contact', privileges: [] },
            { id: 'nameless', titles: {}, kind: 'entity', entity: 'lead', privileges: [] },
            { id: 'de', titles: { 1031: 'Erste', 1036: 'Premier' }, firstLcid: 1036, kind: 'entity', entity: 'x', privileges: [] },
          ],
        },
      ],
    },
    {
      id: 'service',
      titles: { 1033: 'Service' },
      groups: [
        {
          id: 'cases',
          titles: { 1033: 'Cases' },
          subAreas: [
            { id: 'web', titles: { 1033: 'Site' }, kind: 'url', url: 'https://example.com/x', privileges: [] },
            { id: 'rel', titles: { 1033: 'Help' }, kind: 'url', url: '/help/index.html', privileges: [] },
            { id: 'js', titles: { 1033: 'Script' }, kind: 'url', url: ' JavaScript:alert(1)', privileges: [] },
            { id: 'tab', titles: { 1033: 'Tabbed' }, kind: 'url', url: 'java\tscript:alert(1)', privileges: [] },
            { id: 'data', titles: { 1033: 'Data' }, kind: 'url', url: 'data:text/html,<script>alert(1)</script>', privileges: [] },
          ],
        },
      ],
    },
  ],
};

function renderNav(lcid: number, current = { appId: 'a1', pageType: 'entitylist', etn: 'contact' }) {
  render(
    <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
      <SiteMapNav siteMap={MODEL} lcid={lcid} appId="a1" current={current} label="Sales Hub" />
    </MemoryRouter>,
  );
  return screen.getByRole('navigation', { name: 'Sales Hub' });
}

describe('SiteMapNav', () => {
  it('titles follow uilanguageid 1040, falling back to 1033', () => {
    const nav = renderNav(1040);
    expect(within(nav).getByText('Clienti')).toBeInTheDocument();
    expect(within(nav).getByRole('link', { name: 'Aziende' })).toBeInTheDocument();
    expect(within(nav).getByRole('link', { name: 'Contacts' })).toBeInTheDocument();
    // No title at all shows the Id; no 1040 nor 1033 shows the first title in document order.
    expect(within(nav).getByRole('link', { name: 'nameless' })).toBeInTheDocument();
    expect(within(nav).getByRole('link', { name: 'Premier' })).toBeInTheDocument();
  });

  it('marks the current subarea and links each entity subarea to its list', () => {
    const nav = renderNav(1033);
    const current = within(nav).getByRole('link', { current: 'page' });
    expect(current).toHaveTextContent('Contacts');
    expect(within(nav).getByRole('link', { name: 'Accounts' })).toHaveAttribute(
      'href',
      '/main.aspx?appid=a1&pagetype=entitylist&etn=account',
    );
  });

  it('switches area and opens an external url in a new tab', async () => {
    const nav = renderNav(1033);
    expect(within(nav).queryByText('Cases')).toBeNull();
    await userEvent.click(within(nav).getByRole('combobox', { name: en['apps.nav.areas'] }));
    await userEvent.click(screen.getByRole('option', { name: 'Service' }));
    expect(within(nav).getByText('Cases')).toBeInTheDocument();
    const site = within(nav).getByRole('link', { name: 'Site' });
    expect(site).toHaveAttribute('href', 'https://example.com/x');
    expect(site).toHaveAttribute('target', '_blank');
    expect(within(nav).getByRole('combobox', { name: en['apps.nav.areas'] })).toHaveTextContent('Service');
  });

  it('links only http(s) and site-relative urls; any other scheme is shown disabled, never as a link', async () => {
    const nav = renderNav(1033);
    await userEvent.click(within(nav).getByRole('combobox', { name: en['apps.nav.areas'] }));
    await userEvent.click(screen.getByRole('option', { name: 'Service' }));
    expect(within(nav).getByRole('link', { name: 'Help' })).toHaveAttribute('href', '/help/index.html');
    for (const name of ['Script', 'Tabbed', 'Data']) {
      expect(within(nav).queryByRole('link', { name })).toBeNull();
      const item = within(nav).getByText(name);
      expect(item).toHaveAttribute('aria-disabled', 'true');
      expect(item).toHaveAttribute('title', en['apps.nav.blockedUrl']);
      expect(item).not.toHaveAttribute('href');
    }
    expect(nav.querySelector('a[href^="javascript" i], a[href^="data" i], a[href^="java" i]')).toBeNull();
  });
});
