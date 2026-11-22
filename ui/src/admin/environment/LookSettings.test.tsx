import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { saveDevCredentials } from '../../shell/auth/devToken';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { mockAdmin, PRESENTATION_DEF, providerOf, RAMP, tokenOf, USER_ID, type SettingRow } from '../../shell/layout/themeTestSupport';
import { xrmverseTheme } from '../../shell/theme';
import { PAGE_TEST_TIMEOUT } from '../forms/controlsTestSupport';
import { onPath } from '../security/testSupport';

const LOOK = 'x_/look.json';
const ROW = 'cccccccc-0000-0000-0000-000000000001';

/** The web admin with the look settings writable (the writes change `rows`) and one overlay web resource. */
function mock(rows: SettingRow[]) {
  mockAdmin({ rows, overlays: { [LOOK]: () => HttpResponse.json({ version: 1, theme: { brandRamp: RAMP } }) as unknown as Response } });
  server.use(
    http.get('*/admin/health', () => HttpResponse.json({ status: 'Healthy', version: '1', organization: 'Acme Dev', database: 'x', schemaVersion: 1, pendingMigrations: 0, asyncQueue: { waiting: 0, inProgress: 0 }, authMode: 'Development' })),
    http.get('*/admin/doctor', () => HttpResponse.json({ checks: [] })),
    onPath('get', 'webresourceset', () => HttpResponse.json({ value: [{ webresourceid: 'w1', name: LOOK }] })),
    onPath('post', 'organizationsettings', async ({ request }) => {
      const body = (await request.json()) as Record<string, string>;
      const def = /\(([^)]+)\)/.exec(body['settingdefinitionid@odata.bind'] ?? '')![1]!;
      rows.push({ organizationsettingid: ROW, _settingdefinitionid_value: def, value: body.value! });
      return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://x/api/data/v9.2/organizationsettings(${ROW})` } });
    }),
    http.delete('*/api/data/v9.2/organizationsettings*', () => {
      rows.length = 0;
      return new HttpResponse(null, { status: 204 });
    }),
  );
}

beforeEach(() => {
  sessionStorage.clear();
  saveDevCredentials({ signingKey: 'k'.repeat(40), objectId: USER_ID, name: 'Ada Admin' });
});
afterEach(() => sessionStorage.clear());

const brandOfMain = () => tokenOf(providerOf(screen.getByRole('main')), 'colorBrandBackground')?.toLowerCase();
const section = async () => within(await screen.findByRole('region', { name: en['environment.look.title'] }));

describe('LookSettings: the web admin follows the saved look', { timeout: PAGE_TEST_TIMEOUT }, () => {
  it('saving a presentation themes the admin without a reload', async () => {
    mock([]);
    render(<App initialEntries={['/manage/environment']} />);
    const look = await section();
    expect(brandOfMain()).toBe(xrmverseTheme.colorBrandBackground.toLowerCase());
    await userEvent.click(await look.findByRole('combobox', { name: en['environment.look.presentation'] }));
    await userEvent.click(await screen.findByRole('option', { name: LOOK }));
    await userEvent.click(look.getByRole('button', { name: en['environment.look.save'] }));
    await waitFor(() => expect(brandOfMain()).toBe(RAMP[80].toLowerCase()));
  });

  it('saving "None" brings the xrmverse theme back without a reload', async () => {
    mock([{ organizationsettingid: ROW, _settingdefinitionid_value: PRESENTATION_DEF, value: LOOK }]);
    render(<App initialEntries={['/manage/environment']} />);
    const look = await section();
    await waitFor(() => expect(brandOfMain()).toBe(RAMP[80].toLowerCase()));
    const box = await look.findByRole('combobox', { name: en['environment.look.presentation'] });
    await waitFor(() => expect(box).toHaveValue(LOOK));
    await userEvent.click(box);
    await userEvent.click(await screen.findByRole('option', { name: en['environment.look.presentationNone'] }));
    await userEvent.click(look.getByRole('button', { name: en['environment.look.save'] }));
    await waitFor(() => expect(brandOfMain()).toBe(xrmverseTheme.colorBrandBackground.toLowerCase()));
  });
});
