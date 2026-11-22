import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { onPath } from '../security/testSupport';
import { saveDevCredentials } from '../../shell/auth/devToken';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';

const SYSTEM_ADMIN = '627090ff-40a3-4053-8790-584edc5be201';
const USER_ID = 'aaaaaaaa-0000-0000-0000-000000000001';

const health = {
  status: 'Healthy',
  version: '1.2.3',
  organization: 'Acme Dev',
  database: 'xrm_acme',
  schemaVersion: 42,
  pendingMigrations: 0,
  asyncQueue: { waiting: 7, inProgress: 2 },
  authMode: 'Development',
};

function mock(opts: { healthStatus?: number; health?: object; checks?: object[]; doctorFails?: boolean; org?: object }) {
  server.use(
    http.get('*/ui/config.json', () =>
      HttpResponse.json({ authMode: 'Development', audience: 'http://localhost/', apiVersion: 'v9.2' }),
    ),
    http.get('*/api/data/v9.2/*', ({ request }) => {
      const entity = new URL(request.url).pathname.split('/').pop()!;
      if (entity === 'WhoAmI') return HttpResponse.json({ UserId: USER_ID });
      if (entity === 'systemuserrolescollection') return HttpResponse.json({ value: [{ roleid: SYSTEM_ADMIN }] });
      if (entity === 'roles') return HttpResponse.json({ value: [{ roleid: SYSTEM_ADMIN, name: 'Role', roletemplateid: SYSTEM_ADMIN }] });
      if (entity === 'organizations' && opts.org) return HttpResponse.json({ value: [opts.org] });
      return HttpResponse.json({ value: [] });
    }),
    http.get('*/admin/health', () => HttpResponse.json(opts.health ?? health, { status: opts.healthStatus ?? 200 })),
    http.get('*/admin/doctor', () =>
      opts.doctorFails
        ? HttpResponse.json({ error: { code: '0x80040265', message: 'doctor broke' } }, { status: 500 })
        : HttpResponse.json({ checks: opts.checks ?? [] }),
    ),
  );
}

beforeEach(() => {
  sessionStorage.clear();
  saveDevCredentials({ signingKey: 'k'.repeat(40), objectId: USER_ID, name: 'Ada Admin' });
});
afterEach(() => sessionStorage.clear());

describe('EnvironmentPage', () => {
  it('Healthy_report_shows_badge_version_database_auth_and_queue_counts', async () => {
    mock({ checks: [{ name: 'database', status: 'ok', detail: 'reachable' }] });
    render(<App initialEntries={['/manage/environment']} />);
    expect(await screen.findByText(en['environment.status.Healthy'])).toBeInTheDocument();
    expect(screen.getByText('1.2.3')).toBeInTheDocument();
    expect(screen.getByText('xrm_acme')).toBeInTheDocument();
    expect(screen.getByText('Development')).toBeInTheDocument();
    expect(screen.getByTestId('async-waiting')).toHaveTextContent('7');
    expect(screen.getByTestId('async-inprogress')).toHaveTextContent('2');
    expect(await screen.findByText('reachable')).toBeInTheDocument();
  });

  it('Health_503_renders_as_unhealthy_report', async () => {
    mock({
      healthStatus: 503,
      health: { ...health, status: 'Unhealthy', organization: '', schemaVersion: 0, asyncQueue: { waiting: 0, inProgress: 0 } },
      checks: [{ name: 'database', status: 'error', detail: 'cannot open' }],
    });
    render(<App initialEntries={['/manage/environment']} />);
    expect(await screen.findByText(en['environment.status.Unhealthy'])).toBeInTheDocument();
    expect(screen.getByText('1.2.3')).toBeInTheDocument();
    expect(screen.getByText('cannot open')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('Doctor_error_check_is_highlighted', async () => {
    mock({
      checks: [
        { name: 'database', status: 'ok', detail: 'fine' },
        { name: 'plugins', status: 'warning', detail: 'one stale' },
        { name: 'schema', status: 'error', detail: 'pending migrations' },
      ],
    });
    render(<App initialEntries={['/manage/environment']} />);
    const errorRow = (await screen.findByText('pending migrations')).closest('tr')!;
    expect(errorRow).toHaveAttribute('data-status', 'error');
    expect(within(errorRow).getByText(en['environment.check.error'])).toBeInTheDocument();
    expect(screen.getByText('one stale').closest('tr')).toHaveAttribute('data-status', 'warning');
    expect(screen.getByText('fine').closest('tr')).toHaveAttribute('data-status', 'ok');
  });

  it('Doctor_failure_shows_code_and_message', async () => {
    mock({ doctorFails: true });
    render(<App initialEntries={['/manage/environment']} />);
    expect(await screen.findAllByText('0x80040265')).not.toHaveLength(0);
    expect(screen.getAllByText(/doctor broke/).length).toBeGreaterThan(0);
  });

  it('Auto_save_switch_reads_and_patches_the_organization', async () => {
    const ORG = 'dddddddd-0000-0000-0000-000000000001';
    const patches: unknown[] = [];
    mock({ org: { organizationid: ORG, isautosaveenabled: true } });
    server.use(
      onPath('patch', `organizations(${ORG})`, async ({ request }) => {
        patches.push(await request.json());
        return new HttpResponse(null, { status: 204 });
      }),
    );
    render(<App initialEntries={['/manage/environment']} />);
    const toggle = await screen.findByRole('switch', { name: en['environment.forms.autoSave'] });
    expect(toggle).toBeChecked();
    await userEvent.click(toggle);
    await waitFor(() => expect(patches).toEqual([{ isautosaveenabled: false }]));
    await waitFor(() => expect(toggle).not.toBeChecked());
  });
});

describe('EnvironmentPage: model-driven apps look', () => {
  const RENDERER_DEF = '6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e10';
  const PRESENTATION_DEF = '6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e11';
  const ROW = 'cccccccc-0000-0000-0000-000000000001';
  type Row = { organizationsettingid: string; _settingdefinitionid_value: string; value: string };

  /** The look definitions, the `organizationsetting` rows (`rows`, changed by the writes) and two web resources. */
  function look(rows: Row[], over: { writeStatus?: number } = {}) {
    mock({});
    const writes: { method: string; path: string; body?: unknown }[] = [];
    const fail = () => HttpResponse.json({ error: { code: '0x80040220', message: 'Principal user is missing prvWriteorganizationsetting' } }, { status: 403 });
    server.use(
      onPath('get', 'webresourceset', () => HttpResponse.json({ value: [{ webresourceid: 'w1', name: 'x_/look.json' }, { webresourceid: 'w2', name: 'x_/other.js' }] })),
      // The web admin reads the environment's overlay for its own theme.
      http.get('*/WebResources/x_/look.json', () => HttpResponse.json({ version: 1 })),
      http.get('*/api/data/v9.2/settingdefinitions', () =>
        HttpResponse.json({
          value: [
            { settingdefinitionid: RENDERER_DEF, uniquename: 'xrmverse_Renderer', defaultvalue: 'classic' },
            { settingdefinitionid: PRESENTATION_DEF, uniquename: 'xrmverse_Presentation', defaultvalue: '' },
          ],
        }),
      ),
      onPath('get', 'organizationsettings', () => HttpResponse.json({ value: rows })),
      onPath('post', 'organizationsettings', async ({ request }) => {
        if (over.writeStatus) return fail();
        const body = (await request.json()) as Record<string, string>;
        writes.push({ method: 'POST', path: 'organizationsettings', body });
        const id = `dddddddd-0000-0000-0000-00000000000${rows.length + 1}`;
        const def = /\(([^)]+)\)/.exec(body['settingdefinitionid@odata.bind'] ?? '')![1]!;
        rows.push({ organizationsettingid: id, _settingdefinitionid_value: def, value: body.value! });
        return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://x/api/data/v9.2/organizationsettings(${id})` } });
      }),
      http.patch('*/api/data/v9.2/organizationsettings*', async ({ request }) => {
        if (over.writeStatus) return fail();
        const id = /\(([^)]+)\)/.exec(new URL(request.url).pathname)![1]!;
        const body = (await request.json()) as { value: string };
        writes.push({ method: 'PATCH', path: `organizationsettings(${id})`, body });
        rows.find((r) => r.organizationsettingid === id)!.value = body.value;
        return new HttpResponse(null, { status: 204 });
      }),
      http.delete('*/api/data/v9.2/organizationsettings*', ({ request }) => {
        if (over.writeStatus) return fail();
        const id = /\(([^)]+)\)/.exec(new URL(request.url).pathname)![1]!;
        writes.push({ method: 'DELETE', path: `organizationsettings(${id})` });
        rows.splice(rows.findIndex((r) => r.organizationsettingid === id), 1);
        return new HttpResponse(null, { status: 204 });
      }),
    );
    return writes;
  }

  const section = async () => within(await screen.findByRole('region', { name: en['environment.look.title'] }));

  it('creates_the_environment_value_then_updates_the_same_row', async () => {
    const writes = look([]);
    render(<App initialEntries={['/manage/environment']} />);
    const look_ = await section();
    await userEvent.selectOptions(await look_.findByLabelText(en['environment.look.renderer']), 'Modern');
    await userEvent.click(look_.getByRole('button', { name: en['environment.look.save'] }));
    await waitFor(() =>
      expect(writes).toEqual([
        {
          method: 'POST',
          path: 'organizationsettings',
          body: {
            displayname: 'xrmverse_Renderer',
            uniquename: 'xrmverse_Renderer',
            value: 'modern',
            'settingdefinitionid@odata.bind': `/settingdefinitions(${RENDERER_DEF})`,
          },
        },
      ]),
    );
    await userEvent.selectOptions(look_.getByLabelText(en['environment.look.renderer']), 'Classic');
    await userEvent.click(look_.getByRole('button', { name: en['environment.look.save'] }));
    await waitFor(() => expect(writes).toHaveLength(2));
    expect(writes[1]).toEqual({ method: 'PATCH', path: 'organizationsettings(dddddddd-0000-0000-0000-000000000001)', body: { value: 'classic' } });
  });

  it('clearing_the_presentation_deletes_its_row', async () => {
    const writes = look([{ organizationsettingid: ROW, _settingdefinitionid_value: PRESENTATION_DEF, value: 'x_/look.json' }]);
    render(<App initialEntries={['/manage/environment']} />);
    const look_ = await section();
    const box = await look_.findByRole('combobox', { name: en['environment.look.presentation'] });
    await waitFor(() => expect(box).toHaveValue('x_/look.json'));
    await userEvent.click(box);
    await userEvent.click(await screen.findByRole('option', { name: en['environment.look.presentationNone'] }));
    await userEvent.click(look_.getByRole('button', { name: en['environment.look.save'] }));
    await waitFor(() => expect(writes).toEqual([{ method: 'DELETE', path: `organizationsettings(${ROW})` }]));
  });

  it('a_failing_web_resource_list_leaves_the_look_settings_working', async () => {
    const writes = look([]);
    server.use(onPath('get', 'webresourceset', () => HttpResponse.json({ error: { message: 'boom' } }, { status: 500 })));
    render(<App initialEntries={['/manage/environment']} />);
    const look_ = await section();
    await userEvent.selectOptions(await look_.findByLabelText(en['environment.look.renderer']), 'Modern');
    expect(await look_.findByText(/could not be loaded/)).toBeInTheDocument();
    await userEvent.click(look_.getByRole('button', { name: en['environment.look.save'] }));
    await waitFor(() => expect(writes).toHaveLength(1));
  });

  it('a_403_shows_the_api_error_and_the_page_stays', async () => {
    look([], { writeStatus: 403 });
    render(<App initialEntries={['/manage/environment']} />);
    const look_ = await section();
    await userEvent.selectOptions(await look_.findByLabelText(en['environment.look.renderer']), 'Modern');
    await userEvent.click(look_.getByRole('button', { name: en['environment.look.save'] }));
    expect(await screen.findByText(/missing prvWriteorganizationsetting/)).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: en['environment.title'] })).toBeInTheDocument();
  });
});
