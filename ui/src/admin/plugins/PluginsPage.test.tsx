import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { API, onPath, signInAsAdministrator } from '../security/testSupport';
import { STEP1, TYPE1, TYPE2, servePlugins, step } from './pluginTestSupport';

beforeEach(() => signInAsAdministrator());
afterEach(() => {
  sessionStorage.clear();
  vi.useRealTimers();
});

const label = (key: keyof typeof en, args: Record<string, string>) =>
  Object.entries(args).reduce((text: string, [k, v]) => text.replace(`{${k}}`, v), en[key]);

const commands = () => screen.getByRole('toolbar', { name: `${en['plugins.tab.assemblies']} commands` });

describe('PluginsPage', () => {
  it('shows the assembly, type and step tree lazily and the CLI registration hint', async () => {
    const seen = servePlugins();
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/plugins']} />);
    expect(await screen.findByText('Acme.Plugins')).toBeInTheDocument();
    expect(screen.getByText(en['plugins.cli.command'])).toBeInTheDocument();
    // Nothing below the assembly is read until it is opened.
    expect(seen.types).toEqual([]);
    await user.click(screen.getByRole('button', { name: label('plugins.assembly.expand', { name: 'Acme.Plugins' }) }));
    expect(await screen.findByText('Acme.Plugins.AccountPlugin')).toBeInTheDocument();
    expect(seen.types[0]).toContain(`_pluginassemblyid_value eq`);
    expect(seen.steps).toEqual([]);
    await user.click(screen.getByRole('button', { name: label('plugins.type.expand', { name: 'Acme.Plugins.AccountPlugin' }) }));
    expect(await screen.findByText('Acme.Plugins.AccountPlugin: Create of account')).toBeInTheDocument();
    expect(seen.steps).toEqual([`_plugintypeid_value eq ${TYPE1}`]);
    expect(screen.getByText(en['plugins.step.enabled'])).toBeInTheDocument();
    expect(screen.getByText(label('plugins.step.stage', { stage: en['plugins.stage.20'] }))).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: label('plugins.type.expand', { name: 'Acme.Plugins.ContactPlugin' }) }));
    expect(await screen.findByText(en['plugins.steps.empty'])).toBeInTheDocument();
    expect(seen.steps).toContain(`_plugintypeid_value eq ${TYPE2}`);
  });

  it('Step_disable_updates_state', async () => {
    servePlugins();
    const patches: unknown[] = [];
    let current = step();
    server.use(
      onPath('patch', `sdkmessageprocessingsteps(${STEP1})`, async ({ request }) => {
        const body = (await request.json()) as { statecode: number; statuscode: number };
        patches.push(body);
        current = step(body);
        return new HttpResponse(null, { status: 204 });
      }),
      onPath('get', `sdkmessageprocessingsteps(${STEP1})`, () => HttpResponse.json(current)),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/plugins']} />);
    await user.click(await screen.findByRole('button', { name: label('plugins.assembly.expand', { name: 'Acme.Plugins' }) }));
    await user.click(await screen.findByRole('button', { name: label('plugins.type.expand', { name: 'Acme.Plugins.AccountPlugin' }) }));
    const name = 'Acme.Plugins.AccountPlugin: Create of account';
    // Nothing selected: the command bar offers nothing; a click on the step's name selects it.
    expect(within(commands()).queryByRole('button')).not.toBeInTheDocument();
    await user.click(await screen.findByText(name));
    expect(screen.getByText(name).closest('li')).toHaveAttribute('aria-selected', 'true');

    await user.click(within(commands()).getByRole('button', { name: label('plugins.step.disableOpen', { name }) }));
    await waitFor(() => expect(patches).toEqual([{ statecode: 1, statuscode: 2 }]));
    expect(await screen.findByText(en['plugins.step.disabled'])).toBeInTheDocument();
    expect(await screen.findByText(label('plugins.step.nowDisabled', { name }))).toBeInTheDocument();

    await user.click(within(commands()).getByRole('button', { name: label('plugins.step.enableOpen', { name }) }));
    await waitFor(() => expect(patches).toEqual([{ statecode: 1, statuscode: 2 }, { statecode: 0, statuscode: 1 }]));
    expect(await screen.findByText(en['plugins.step.enabled'])).toBeInTheDocument();
  });

  it('Step_enable_keeps_success_when_the_re_read_fails_and_unknown_stage_shows_its_number', async () => {
    const seen = servePlugins();
    server.use(
      http.get(`${API}/sdkmessageprocessingsteps`, ({ request }) => {
        seen.steps.push(new URL(request.url).searchParams.get('$filter'));
        return HttpResponse.json({ value: [step({ stage: 35 })] });
      }),
      onPath('patch', `sdkmessageprocessingsteps(${STEP1})`, () => new HttpResponse(null, { status: 204 })),
      onPath('get', `sdkmessageprocessingsteps(${STEP1})`, () =>
        HttpResponse.json({ error: { code: '0x80040E14', message: 'read failed' } }, { status: 500 }),
      ),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/plugins']} />);
    await user.click(await screen.findByRole('button', { name: label('plugins.assembly.expand', { name: 'Acme.Plugins' }) }));
    await user.click(await screen.findByRole('button', { name: label('plugins.type.expand', { name: 'Acme.Plugins.AccountPlugin' }) }));
    const name = 'Acme.Plugins.AccountPlugin: Create of account';
    expect(await screen.findByText(label('plugins.step.stage', { stage: '35' }))).toBeInTheDocument();
    await user.click(screen.getByText(name));
    await user.click(within(commands()).getByRole('button', { name: label('plugins.step.disableOpen', { name }) }));
    expect(await screen.findByText(label('plugins.step.nowDisabled', { name }))).toBeInTheDocument();
    expect(await screen.findByText(en['plugins.step.disabled'])).toBeInTheDocument();
  });

  it('clears the step selection on a second click and when its type is collapsed', async () => {
    servePlugins();
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/plugins']} />);
    await user.click(await screen.findByRole('button', { name: label('plugins.assembly.expand', { name: 'Acme.Plugins' }) }));
    await user.click(await screen.findByRole('button', { name: label('plugins.type.expand', { name: 'Acme.Plugins.AccountPlugin' }) }));
    const name = 'Acme.Plugins.AccountPlugin: Create of account';
    await user.click(await screen.findByText(name));
    expect(within(commands()).getByRole('button', { name: label('plugins.step.disableOpen', { name }) })).toBeInTheDocument();
    await user.click(screen.getByText(name));
    expect(screen.getByText(name).closest('li')).toHaveAttribute('aria-selected', 'false');
    expect(within(commands()).queryByRole('button')).not.toBeInTheDocument();
    await user.click(screen.getByText(name));
    await user.click(screen.getByRole('button', { name: label('plugins.type.collapse', { name: 'Acme.Plugins.AccountPlugin' }) }));
    expect(within(commands()).queryByRole('button')).not.toBeInTheDocument();
  });

  it('Traces_filter_by_type', async () => {
    servePlugins();
    const requests: { filter: string | null; orderby: string | null; prefer: string | null }[] = [];
    const trace = (n: number, extra: object = {}) => ({
      plugintracelogid: `dddddddd-3333-0000-0000-00000000000${n}`,
      createdon: '2026-11-08T10:00:00Z',
      typename: n === 1 ? 'Acme.Plugins.AccountPlugin' : 'Acme.Plugins.ContactPlugin',
      messagename: 'Create',
      primaryentity: 'account',
      depth: 1,
      mode: 0,
      messageblock: `block ${n}`,
      exceptiondetails: null,
      ...extra,
    });
    server.use(
      http.get(`${API}/plugintracelogs`, ({ request }) => {
        const url = new URL(request.url);
        const filter = url.searchParams.get('$filter');
        requests.push({ filter, orderby: url.searchParams.get('$orderby'), prefer: request.headers.get('Prefer') });
        return HttpResponse.json({
          value: filter ? [trace(1, { exceptiondetails: 'System.InvalidOperationException: boom' })] : [trace(1, { exceptiondetails: 'System.InvalidOperationException: boom' }), trace(2)],
        });
      }),
    );
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    render(<App initialEntries={['/manage/plugins']} />);
    await screen.findByText('Acme.Plugins');
    await user.click(screen.getByRole('tab', { name: en['plugins.tab.traces'] }));
    expect(await screen.findByText('Acme.Plugins.ContactPlugin')).toBeInTheDocument();
    expect(requests).toHaveLength(1);
    expect(requests[0].filter).toBeNull();
    expect(requests[0].orderby).toBe('createdon desc');
    expect(requests[0].prefer).toContain('odata.maxpagesize=50');

    await user.type(screen.getByLabelText(en['traces.filter.type']), "Acme.Plugins.Account'Plugin");
    // Debounced: nothing is sent while the user is still typing.
    expect(requests).toHaveLength(1);
    await act(async () => {
      vi.advanceTimersByTime(400);
    });
    await waitFor(() => expect(requests).toHaveLength(2));
    expect(requests[1].filter).toBe("typename eq 'Acme.Plugins.Account''Plugin'");
    await waitFor(() => expect(screen.queryByText('Acme.Plugins.ContactPlugin')).toBeNull());

    // Detail: message block and exception details.
    await user.click(screen.getByRole('button', { name: label('traces.detail.open', { type: 'Acme.Plugins.AccountPlugin' }) }));
    const drawer = await screen.findByRole('dialog', { hidden: true });
    expect(within(drawer).getByText('block 1')).toBeInTheDocument();
    expect(within(drawer).getByText('System.InvalidOperationException: boom')).toBeInTheDocument();
  });
});
