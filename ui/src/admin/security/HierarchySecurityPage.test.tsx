import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { signInAsAdministrator } from './testSupport';

const PATH = '/manage/security/hierarchy';

let puts: unknown[] = [];

function mockSettings(initial = { enabled: false, depth: 3, excludedTables: ['account'] }) {
  puts = [];
  server.use(
    http.get('*/admin/hierarchy-security', () => HttpResponse.json(initial)),
    http.put('*/admin/hierarchy-security', async ({ request }) => {
      const body = (await request.json()) as typeof initial;
      puts.push(body);
      return HttpResponse.json(body);
    }),
  );
}

beforeEach(() => signInAsAdministrator());
afterEach(() => sessionStorage.clear());

describe('HierarchySecurityPage', () => {
  it('is in the navigation as "Hierarchy security" and shows the saved settings', async () => {
    mockSettings();
    render(<App initialEntries={[PATH]} />);

    expect(await screen.findByText('account')).toBeInTheDocument();
    expect(screen.getAllByText(en['nav.hierarchySecurity']).length).toBeGreaterThan(1);
    expect(screen.getByRole('switch')).not.toBeChecked();
    expect(screen.getByLabelText(new RegExp(en['hierarchySecurity.depth']))).toHaveValue('3');
  });

  it('saves the switch, the depth and the exclusions as one PUT', async () => {
    mockSettings();
    const user = userEvent.setup();
    render(<App initialEntries={[PATH]} />);
    await screen.findByText('account');

    await user.click(screen.getByRole('switch'));
    const depth = screen.getByLabelText(new RegExp(en['hierarchySecurity.depth']));
    await user.clear(depth);
    await user.type(depth, '7');
    await user.type(screen.getByLabelText(en['hierarchySecurity.table']), 'contact');
    await user.click(screen.getByRole('button', { name: en['hierarchySecurity.add'] }));
    await user.click(screen.getByRole('button', { name: 'Include account again' }));
    await user.click(screen.getByRole('button', { name: en['hierarchySecurity.save'] }));

    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0]).toEqual({ enabled: true, depth: 7, excludedTables: ['contact'] });
    expect(await screen.findByText(en['hierarchySecurity.saved'])).toBeInTheDocument();
  });

  it('shows the 400 message of an invalid depth and keeps what was typed', async () => {
    mockSettings();
    server.use(
      http.put('*/admin/hierarchy-security', () =>
        HttpResponse.json(
          { error: { code: '0x80040203', message: 'maxdepthforhierarchicalsecuritymodel must be a whole number from 1 to 100.' } },
          { status: 400 },
        ),
      ),
    );
    const user = userEvent.setup();
    render(<App initialEntries={[PATH]} />);
    await screen.findByText('account');

    const depth = screen.getByLabelText(new RegExp(en['hierarchySecurity.depth']));
    await user.clear(depth);
    await user.type(depth, '500');
    await user.click(screen.getByRole('button', { name: en['hierarchySecurity.save'] }));

    expect(await screen.findByText(/must be a whole number from 1 to 100/)).toBeInTheDocument();
    expect(depth).toHaveValue('500');
    expect(screen.queryByText(en['hierarchySecurity.saved'])).toBeNull();
  });

  it('shows why the settings could not be loaded', async () => {
    server.use(
      http.get('*/admin/hierarchy-security', () =>
        HttpResponse.json({ error: { code: '0x80040220', message: 'The Admin API requires the System Administrator role.' } }, { status: 403 }),
      ),
    );
    render(<App initialEntries={[PATH]} />);

    expect(await screen.findByText(/could not be loaded.*requires the System Administrator role/)).toBeInTheDocument();
  });
});
