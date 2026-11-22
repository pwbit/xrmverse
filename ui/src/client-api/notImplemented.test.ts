import { DEFAULT_DATE_FORMATS } from '../apps/userSettings';
import { afterEach, describe, expect, it } from 'vitest';
import type { Handler } from '../model/form';
import type { FormNotification } from '../renderers/classic/form/FormNotifications';
import { XrmApi } from '../shell/api/client';
import { createDialogHost } from './DialogHost';
import { runHandlers } from './events';
import { createExecutionContext } from './executionContext';
import { XRM_NOT_IMPLEMENTED } from './notImplemented';
import { createScriptHost, type ScriptHost } from './scriptHost';
import { sourceLoader } from './testHost';
import { createXrm, type Xrm } from './xrm';

const hosts: ScriptHost[] = [];
afterEach(() => hosts.splice(0).forEach((h) => h.dispose()));

function makeXrm(): Xrm {
  return createXrm({
    api: new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => undefined }),
    app: { id: 'a1', uniqueName: 'app', name: 'App', clientType: 4, components: { entities: [], views: [], forms: [] } },
    user: { id: 'aaaaaaaa-0000-0000-0000-000000000001', name: 'U', lcid: 1033, roles: [], timeZone: 'Etc/UTC', formats: DEFAULT_DATE_FORMATS },
    org: { id: 'o1', uniqueName: 'org', languageId: 1033, baseCurrencyId: null, isAutoSaveEnabled: true },
    navigate: () => undefined,
    dialogs: createDialogHost(),
    currentForm: () => null,
  });
}

const handler = (functionName: string): Handler => ({ functionName, libraryName: 'lib.js', enabled: true, parameters: '', passExecutionContext: false });

/** Reads `path` (`Xrm.A.b().c`) on `xrm`: dots read properties, `()` calls the member. */
function reach(xrm: Xrm, path: string): unknown {
  let value: unknown = { Xrm: xrm };
  for (const part of path.split('.')) {
    const call = part.endsWith('()');
    const name = call ? part.slice(0, -2) : part;
    value = (value as Record<string, unknown>)[name];
    if (call) value = (value as () => unknown)();
  }
  return value;
}

describe('not available members', () => {
  it('unimplemented_member_throws_with_path_and_stage', async () => {
    const xrm = makeXrm();
    // A customer script that calls a member UI-1c leaves out: the notification names the member and its stage, never
    // "undefined is not a function".
    const host = await createScriptHost(
      ['lib.js'],
      (win) => ((win as unknown as { Xrm: Xrm }).Xrm = xrm),
      sourceLoader({
        'lib.js': `var Lib = {
          panel: function () { Xrm.Panel.loadPanel('https://example.com', 'x'); },
          grid: function () { Xrm.Utility.refreshParentGrid({}); },
          offline: function () { return Xrm.WebApi.offline.retrieveRecord('account', '1'); }
        };`,
      }),
    );
    hosts.push(host);
    const notes: FormNotification[] = [];
    runHandlers(host, [handler('Lib.panel'), handler('Lib.grid'), handler('Lib.offline')], () => createExecutionContext({ formContext: {} }), (n) => notes.push(n));
    expect(notes.map((n) => n.text)).toEqual([
      'Script error in Lib.panel: Xrm.Panel is not available in this version (stage after UI-1).',
      'Script error in Lib.grid: Xrm.Utility.refreshParentGrid is not available in this version (stage after UI-1).',
      'Script error in Lib.offline: Xrm.WebApi.offline is not available in this version (stage after UI-1).',
    ]);

    // Every listed member throws with its own path and stage, read or called.
    for (const { path, stage, kind } of XRM_NOT_IMPLEMENTED) {
      const expected = `${path} is not available in this version (stage ${stage}).`;
      const read = () => reach(xrm, path);
      if (kind === 'method') {
        expect(typeof read(), path).toBe('function');
        expect(() => (read() as () => unknown)(), path).toThrow(expected);
      } else {
        expect(read, path).toThrow(expected);
      }
    }
    expect(XRM_NOT_IMPLEMENTED.map((m) => m.path)).toEqual(expect.arrayContaining(['Xrm.App', 'Xrm.Device', 'Xrm.Encoding', 'Xrm.Panel']));
  });

  it('undocumented_member_undefined', () => {
    const xrm = makeXrm() as unknown as Record<string, Record<string, unknown>>;
    expect(xrm.Internal).toBeUndefined();
    expect(xrm.Utility!.openEntityForm).toBeUndefined();
    expect(xrm.Navigation!.somethingElse).toBeUndefined();
    expect((xrm.WebApi!.online as Record<string, unknown>).nope).toBeUndefined();
    expect(Object.keys(xrm).sort()).toEqual(['Navigation', 'Page', 'Utility', 'WebApi']);
  });
});
