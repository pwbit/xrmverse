import { render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { App } from '../shell/App';
import { en } from '../shell/i18n/en';
import { mockStudio, summary } from './testSupport';

afterEach(() => sessionStorage.clear());

describe('StudioGate', () => {
  it('a 403 on the list shows the missing privilege', async () => {
    mockStudio({ listStatus: 403, listMessage: 'Principal user is missing prvReadWorkflow privilege.' });
    render(<App initialEntries={['/studio/workflows']} />);
    expect(await screen.findByText(en['studio.gate.noPrivilege'])).toBeInTheDocument();
    expect(en['studio.gate.noPrivilege']).toContain('prvReadWorkflow');
    expect(screen.queryByRole('table')).toBeNull();
    // The studio's own header, not the web admin's shell.
    const banner = screen.getByRole('banner');
    expect(within(banner).getByText(en['studio.title'])).toBeInTheDocument();
    expect(screen.queryByRole('navigation', { name: en['nav.label'] })).toBeNull();
  });

  it('a 501 shows the server message as it is', async () => {
    mockStudio({ listStatus: 501, listMessage: 'Workflow authoring is not available on PostgreSQL.' });
    render(<App initialEntries={['/studio/workflows']} />);
    expect(await screen.findByText('Workflow authoring is not available on PostgreSQL.')).toBeInTheDocument();
    expect(screen.queryByText(en['studio.gate.noPrivilege'])).toBeNull();
  });

  it('/studio/workflows shows the list when it is readable', async () => {
    mockStudio({ workflows: [summary({ id: '11111111-0000-0000-0000-000000000001', name: 'OnCreate' })] });
    render(<App initialEntries={['/studio/workflows']} />);
    expect(await screen.findByRole('table', { name: en['studio.list.title'] })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: en['studio.header.apps'] }).getAttribute('href')).toBe('/apps');
    expect(screen.getByRole('button', { name: 'Ursula User' })).toBeInTheDocument();
  });

  it('/studio goes to the list at /studio/workflows', async () => {
    mockStudio({ workflows: [summary({ id: '11111111-0000-0000-0000-000000000001', name: 'OnCreate' })] });
    let router: { state: { location: { pathname: string } } } | undefined;
    render(<App initialEntries={['/studio']} onRouter={(r) => (router = r as unknown as typeof router)} />);
    expect(await screen.findByRole('table', { name: en['studio.list.title'] })).toBeInTheDocument();
    expect(router!.state.location.pathname).toBe('/studio/workflows');
  });

  it('/studio with a refused list shows the privilege page', async () => {
    mockStudio({ listStatus: 403 });
    render(<App initialEntries={['/studio']} />);
    expect(await screen.findByText(en['studio.gate.noPrivilege'])).toBeInTheDocument();
  });

  it('the editor routes are under the gate', async () => {
    mockStudio({ listStatus: 403 });
    render(<App initialEntries={['/studio/workflows/11111111-0000-0000-0000-000000000001']} />);
    expect(await screen.findByText(en['studio.gate.noPrivilege'])).toBeInTheDocument();
  });
});
