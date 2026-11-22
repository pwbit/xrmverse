import type { XrmApi } from '../shell/api/client';
import { XrmError } from '../shell/api/errors';
import { MemoryRouter } from 'react-router-dom';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { DEFAULT_DATE_FORMATS, loadUserSettings, useReloadUserSettings, useUserSettings, UserSettingsGate } from './userSettings';
import { mockApps } from './testSupport';
import { AuthProvider } from '../shell/auth/AuthProvider';
import { http, HttpResponse } from 'msw';
import { server } from '../test/server';

const USER = '00000000-0000-0000-0000-0000000000aa';

/** An api whose WhoAmI is USER and whose settings row is `row` (undefined: 404). */
function api(row: Record<string, unknown> | undefined, paths: string[] = []): XrmApi {
  return {
    get: async (path: string) => {
      paths.push(path);
      if (path === 'WhoAmI') return { UserId: USER };
      if (!row) throw new XrmError(404, 'not found');
      return row;
    },
  } as unknown as XrmApi;
}

describe('loadUserSettings (#131)', () => {
  it('reads_the_time_zone_and_formats', async () => {
    const paths: string[] = [];
    const settings = await loadUserSettings(
      api(
        {
          uilanguageid: 1040,
          paginglimit: 25,
          timezonecode: 110,
          dateformatstring: 'dd/MM/yyyy',
          timeformatstring: 'HH:mm',
          dateseparator: '/',
          timeseparator: '.',
          amdesignator: 'a.m.',
          pmdesignator: 'p.m.',
        },
        paths,
      ),
    );
    expect(paths[1]).toBe(
      `usersettingscollection(${USER})?$select=uilanguageid,paginglimit,timezonecode,dateformatstring,timeformatstring,dateseparator,timeseparator,amdesignator,pmdesignator,xrmverse_renderer,xrmverse_colorscheme`,
    );
    expect(settings).toEqual({
      userId: USER,
      lcid: 1040,
      pagingLimit: 25,
      timeZoneCode: 110,
      timeZone: 'Europe/Berlin',
      renderer: undefined,
      colorScheme: 0,
      formats: { dateFormat: 'dd/MM/yyyy', timeFormat: 'HH:mm', dateSeparator: '/', timeSeparator: '.', am: 'a.m.', pm: 'p.m.' },
    });
  });

  it('falls_back_to_utc_and_en_us_formats', async () => {
    for (const row of [undefined, { timezonecode: null, dateformatstring: null }]) {
      const settings = await loadUserSettings(api(row));
      expect(settings.timeZoneCode).toBe(92);
      expect(settings.timeZone).toBe('Etc/UTC');
      expect(settings.formats).toEqual(DEFAULT_DATE_FORMATS);
    }
    expect(DEFAULT_DATE_FORMATS).toEqual({ dateFormat: 'M/d/yyyy', timeFormat: 'h:mm tt', dateSeparator: '/', timeSeparator: ':', am: 'AM', pm: 'PM' });
  });

  it('reads_the_look_and_colour_scheme', async () => {
    const s = await loadUserSettings(api({ xrmverse_renderer: 'modern', xrmverse_colorscheme: 2 }));
    expect(s.renderer).toBe('modern');
    expect(s.colorScheme).toBe(2);
  });

  it('an_old_row_or_no_row_follows_the_app_and_the_system_scheme', async () => {
    for (const row of [undefined, {}, { xrmverse_renderer: null, xrmverse_colorscheme: null }]) {
      const s = await loadUserSettings(api(row));
      expect(s.renderer).toBeUndefined();
      expect(s.colorScheme).toBe(0);
    }
  });

  it('refuses_an_unknown_time_zone_code', async () => {
    await expect(loadUserSettings(api({ timezonecode: 9999 }))).rejects.toThrow('9999');
  });
});

describe('UserSettingsGate reload', () => {
  it('reload_refetches_the_row_and_rerenders_consumers', async () => {
    mockApps();
    let look = 'classic';
    let reads = 0;
    server.use(
      http.get('*/api/data/v9.2/usersettingscollection*', () => {
        reads++;
        return HttpResponse.json({ uilanguageid: 1033, xrmverse_renderer: look, xrmverse_colorscheme: 0 });
      }),
    );
    function Probe() {
      const settings = useUserSettings();
      const reload = useReloadUserSettings();
      return <button onClick={reload}>look:{settings.renderer}</button>;
    }
    render(
      <MemoryRouter>
        <AuthProvider>
          <UserSettingsGate>
            <Probe />
          </UserSettingsGate>
        </AuthProvider>
      </MemoryRouter>,
    );
    const button = await screen.findByRole('button', { name: 'look:classic' });
    expect(reads).toBe(1);
    look = 'modern';
    await userEvent.click(button);
    expect(await screen.findByRole('button', { name: 'look:modern' })).toBeTruthy();
    expect(reads).toBe(2);
    await waitFor(() => expect(reads).toBe(2));
  });
});
