import { describe, expect, it } from 'vitest';
import { XrmApi } from '../shell/api/client';
import { loadApps } from './loadApps';
import { mockApps, ROLE_SALES } from './testSupport';

const api = () => new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => {} });
const app = (n: number, settings?: Record<string, string>) => ({
  appmoduleid: `11111111-0000-0000-0000-00000000000${n}`,
  name: `App ${n}`,
  roles: [ROLE_SALES],
  settings,
});

describe('loadApps app settings (#129)', () => {
  it('reads_the_app_onload_setting', async () => {
    mockApps({ apps: [app(1, { xrmverse_AppOnLoad: '[{"library":"a.js","function":"A.b"}]' }), app(2)], userRoles: [ROLE_SALES] });
    const [a, b] = await loadApps(api());
    expect(a.appOnLoad).toEqual({ handlers: [{ library: 'a.js', function: 'A.b' }] });
    expect(b.appOnLoad).toBeUndefined();
  });

  it('an_app_with_both_settings_true_awaits_both_handlers', async () => {
    mockApps({ apps: [app(1, { AsyncOnLoad: 'true', AsyncOnSave: 'true' })], userRoles: [ROLE_SALES] });
    const [loaded] = await loadApps(api());
    expect(loaded.asyncHandlers).toEqual({ onLoad: true, onSave: true });
  });

  it('an_app_without_rows_gets_the_definition_defaults', async () => {
    mockApps({ apps: [app(1)], userRoles: [ROLE_SALES] });
    expect((await loadApps(api()))[0].asyncHandlers).toEqual({ onLoad: false, onSave: false });

    mockApps({ apps: [app(1)], userRoles: [ROLE_SALES], settingDefaults: { AsyncOnSave: 'True' } });
    expect((await loadApps(api()))[0].asyncHandlers).toEqual({ onLoad: false, onSave: true });
  });

  it('only_true_in_any_case_turns_a_setting_on', async () => {
    mockApps({
      apps: [app(1, { AsyncOnLoad: 'TRUE', AsyncOnSave: '1', AppChannel: 'true' }), app(2, { AsyncOnLoad: 'false', AsyncOnSave: ' true ' })],
      userRoles: [ROLE_SALES],
      settingDefaults: { AsyncOnLoad: 'true' },
    });
    const [first, second] = await loadApps(api());
    expect(first.asyncHandlers).toEqual({ onLoad: true, onSave: false });
    // A row overrides the default, false included.
    expect(second.asyncHandlers).toEqual({ onLoad: false, onSave: true });
  });

  it('reads_the_settings_of_the_listed_apps_only', async () => {
    const requests = mockApps({ apps: [app(1, { AsyncOnSave: 'true' })], userRoles: [ROLE_SALES] });
    await loadApps(api());
    expect(requests).toContainEqual(
      "settingdefinitions?$select=settingdefinitionid,uniquename,defaultvalue&$filter=uniquename eq 'AsyncOnLoad' or uniquename eq 'AsyncOnSave' or uniquename eq 'xrmverse_TimelineGroupByDate' or uniquename eq 'xrmverse_TimelineRememberFilters' or uniquename eq 'xrmverse_TimelinePosts' or uniquename eq 'xrmverse_Renderer' or uniquename eq 'xrmverse_Presentation' or uniquename eq 'xrmverse_AppOnLoad' or uniquename eq 'xrmverse_LookupOpenMode'",
    );
    expect(requests).toContainEqual(
      'appsettings?$select=_parentappmoduleid_value,_settingdefinitionid_value,value&$filter=_parentappmoduleid_value eq 11111111-0000-0000-0000-000000000001',
    );
  });

  it('timeline_options_default_off_and_follow_appsetting', async () => {
    mockApps({ apps: [app(1)], userRoles: [ROLE_SALES] });
    expect((await loadApps(api()))[0].timeline).toEqual({ groupByDate: false, rememberFilters: false, posts: false });

    mockApps({
      apps: [app(1, { xrmverse_TimelineGroupByDate: 'true' }), app(2, { xrmverse_TimelinePosts: 'true' })],
      userRoles: [ROLE_SALES],
      settingDefaults: { xrmverse_TimelineRememberFilters: 'TRUE' },
    });
    const [a, b] = await loadApps(api());
    expect(a.timeline).toEqual({ groupByDate: true, rememberFilters: true, posts: false });
    expect(b.timeline).toEqual({ groupByDate: false, rememberFilters: true, posts: true });
  });

  it('a_missing_timeline_definition_is_off', async () => {
    // An organization that was not restarted since the upgrade has no definitions: the apps keep working, with the options off.
    mockApps({ apps: [app(1, { xrmverse_TimelineGroupByDate: 'true' })], userRoles: [ROLE_SALES], missingDefinitions: ['xrmverse_TimelineGroupByDate'] });
    expect((await loadApps(api()))[0].timeline).toEqual({ groupByDate: false, rememberFilters: false, posts: false });
  });

  it('reads_the_environment_values_in_one_request', async () => {
    const requests = mockApps({ apps: [app(1)], userRoles: [ROLE_SALES], environmentSettings: { xrmverse_Renderer: 'modern' } });
    await loadApps(api());
    const calls = requests.filter((r) => r.startsWith('organizationsettings'));
    expect(calls).toEqual([
      'organizationsettings?$select=_settingdefinitionid_value,value&$filter=_settingdefinitionid_value eq 6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e10 or _settingdefinitionid_value eq 6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e11',
    ]);
  });
});

describe('loadApps presentation (UI-2a)', () => {
  it('without_any_value_there_is_nothing_to_apply', async () => {
    mockApps({ apps: [app(1)], userRoles: [ROLE_SALES], settingDefaults: { xrmverse_Renderer: 'classic', xrmverse_Presentation: '' } });
    expect((await loadApps(api()))[0].presentation).toEqual({ renderer: 'classic', overlay: undefined });
  });

  it('app_beats_environment_beats_default', async () => {
    mockApps({
      apps: [app(1, { xrmverse_Renderer: 'modern', xrmverse_Presentation: 'app.json' }), app(2), app(3)],
      userRoles: [ROLE_SALES],
      settingDefaults: { xrmverse_Renderer: 'classic' },
      environmentSettings: { xrmverse_Renderer: 'fancy', xrmverse_Presentation: 'env.json' },
    });
    const [a, b, c] = await loadApps(api());
    expect(a.presentation).toEqual({ renderer: 'modern', overlay: 'app.json' });
    expect(b.presentation).toEqual({ renderer: 'fancy', overlay: 'env.json' });
    expect(c.presentation).toEqual({ renderer: 'fancy', overlay: 'env.json' });
  });

  it('an_empty_app_value_falls_through', async () => {
    mockApps({
      apps: [app(1, { xrmverse_Renderer: '', xrmverse_Presentation: '' })],
      userRoles: [ROLE_SALES],
      environmentSettings: { xrmverse_Renderer: 'modern' },
    });
    expect((await loadApps(api()))[0].presentation).toEqual({ renderer: 'modern', overlay: undefined });
  });

  it('missing_definitions_skip_the_environment_read', async () => {
    const requests = mockApps({
      apps: [app(1)],
      userRoles: [ROLE_SALES],
      missingDefinitions: ['xrmverse_Renderer', 'xrmverse_Presentation'],
    });
    expect((await loadApps(api()))[0].presentation).toEqual({ renderer: undefined, overlay: undefined });
    expect(requests.some((r) => r.startsWith('organizationsettings'))).toBe(false);
  });
});
