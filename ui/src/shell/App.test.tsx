import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { server } from '../test/server';
import { App } from './App';
import { en } from './i18n/en';
import { mockApps } from '../apps/testSupport';

describe('App', () => {
  it('App_renders_inside_FluentProvider', async () => {
    server.use(
      http.get('*/ui/config.json', () =>
        HttpResponse.json({ authMode: 'Development', audience: 'http://localhost/', apiVersion: 'v9.2' }),
      ),
    );
    const { container } = render(<App />);
    expect(container.querySelector('.fui-FluentProvider')).not.toBeNull();
    expect(await screen.findByRole('button', { name: en['auth.dev.submit'] })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: en['app.name'] })).toBeInTheDocument();
  });

  it('/ opens the apps page for any signed-in user', async () => {
    mockApps({ apps: [], userRoles: [] }); // a user with no admin role: the access probe finds nothing
    let router: { state: { location: { pathname: string } } } | undefined;
    render(<App initialEntries={['/']} onRouter={(r) => (router = r as unknown as typeof router)} />);
    expect(await screen.findByRole('heading', { name: en['apps.title'] })).toBeInTheDocument();
    expect(router!.state.location.pathname).toBe('/apps');
    expect(screen.queryByText(en['access.denied.title'])).toBeNull();
  });
});
