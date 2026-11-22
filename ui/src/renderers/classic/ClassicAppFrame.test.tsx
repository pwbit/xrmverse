import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { mockApps, ROLE_SALES, SITEMAP_ID, titled } from '../../apps/testSupport';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';

const APP_ID = '11111111-0000-0000-0000-000000000001';
const SITEMAP = `<SiteMap>
  <Area Id="sales">${titled('Sales')}
    <Group Id="customers">${titled('Customers')}
      <SubArea Id="accounts" Entity="account">${titled('Accounts')}</SubArea>
    </Group>
  </Area>
</SiteMap>`;

function renderClassic() {
  mockApps({
    apps: [{ appmoduleid: APP_ID, name: 'Sales Hub', roles: [ROLE_SALES], siteMapId: SITEMAP_ID, settings: { xrmverse_Renderer: 'classic' } }],
    userRoles: [ROLE_SALES],
    privileges: ['prvReadAccount'],
    siteMapXml: SITEMAP,
  });
  return render(<App initialEntries={[`/main.aspx?appid=${APP_ID}&pagetype=entityrecord`]} />);
}

describe('ClassicAppFrame navigation toggle', () => {
  it('hides and shows the navigation, aria-expanded following', async () => {
    renderClassic();
    const toggle = await screen.findByRole('button', { name: en['apps.nav.toggle'] });
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    const nav = await screen.findByRole('navigation', { name: 'Sales Hub' });
    expect(document.getElementById(toggle.getAttribute('aria-controls') ?? '')).toContainElement(nav);

    await userEvent.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('navigation', { name: 'Sales Hub' })).toBeNull();

    await userEvent.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('navigation', { name: 'Sales Hub' })).toBeInTheDocument();
  });

  it('stays collapsed after a remount', async () => {
    const first = renderClassic();
    await userEvent.click(await screen.findByRole('button', { name: en['apps.nav.toggle'] }));
    first.unmount();
    renderClassic();
    const toggle = await screen.findByRole('button', { name: en['apps.nav.toggle'] });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('navigation', { name: 'Sales Hub' })).toBeNull();
  });
});
