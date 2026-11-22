import { http, HttpResponse } from 'msw';
import { setScriptLoader } from '../../pcf/loader';
import { server } from '../server';
import type { TestControl } from './controls';

/**
 * Serves `controls` as xrmverse does: the `customcontrol` rows through the Web API (`customcontrols?$filter=name eq '...'`)
 * and their files under `/Controls/{name}/{path}`. jsdom does not run `<script src>`: the script loader evaluates the bundle
 * text instead. Returns the URLs of the scripts loaded. Call `setScriptLoader(undefined)` after the test.
 */
export function serveControls(controls: TestControl[]): { scripts: string[] } {
  const scripts: string[] = [];
  const byName = (name: string) => controls.find((c) => c.name.toLowerCase() === name.toLowerCase());
  const fileOf = (url: string): string | undefined => {
    const path = new URL(url, 'http://localhost').pathname;
    const match = /^\/Controls\/([^/]+)\/(.+)$/.exec(path);
    if (!match) return undefined;
    const control = byName(decodeURIComponent(match[1]!));
    return control?.files[match[2]!.split('/').map(decodeURIComponent).join('/')];
  };
  server.use(
    http.get('*/api/data/v9.2/customcontrols', ({ request }) => {
      const filter = new URL(request.url).searchParams.get('$filter') ?? '';
      const name = /name eq '(.*)'/.exec(filter)?.[1]?.replace(/''/g, "'") ?? '';
      const c = byName(name);
      return HttpResponse.json({ value: c ? [{ name: c.name, manifest: c.manifest, version: '1' }] : [] });
    }),
    http.get('*/Controls/*', ({ request }) => {
      const body = fileOf(request.url);
      return body === undefined ? new HttpResponse(null, { status: 404 }) : new HttpResponse(body);
    }),
  );
  setScriptLoader(async (url) => {
    scripts.push(url);
    const body = fileOf(url);
    if (body === undefined) throw new Error('404');
    new Function(body)();
  });
  return { scripts };
}

/**
 * A control whose bundle registers the constructor the test puts on `window.__xvProbe` under `XvTest.{constructor}`: the
 * test sees and drives every call the host makes.
 */
export function probeControl(constructor: string, options: { type?: 'standard' | 'virtual'; ofType?: string; extra?: string; resources?: string } = {}): TestControl {
  const virtual = options.type === 'virtual';
  return {
    name: `xv_XvTest.${constructor}`,
    manifest: `<?xml version="1.0" encoding="utf-8"?>
<manifest>
  <control namespace="XvTest" constructor="${constructor}" version="1.0.0" display-name-key="N" control-type="${virtual ? 'virtual' : 'standard'}">
    <property name="value" display-name-key="V" of-type="${options.ofType ?? 'Decimal'}" usage="bound" required="true" />
    ${options.extra ?? ''}
    <resources>
      <code path="bundle.js" order="1" />
      ${virtual ? '<platform-library name="React" version="16.14.0" /><platform-library name="Fluent" version="9.46.2" />' : ''}
      ${options.resources ?? ''}
    </resources>
  </control>
</manifest>`,
    files: { 'bundle.js': `window.ComponentFramework.registerControl('XvTest.${constructor}', window.__xvProbe);` },
  };
}

/**
 * A dataset control (`<data-set name>` in its manifest) whose bundle registers `window.__xvProbe`, as {@link probeControl}.
 * `propertySet` adds a `<property-set>` to the data-set.
 */
export function probeDatasetControl(constructor: string, options: { dataSet?: string; propertySet?: boolean; type?: 'standard' | 'virtual'; extra?: string } = {}): TestControl {
  const virtual = options.type === 'virtual';
  const propertySet = options.propertySet ? '<property-set name="title" display-name-key="T" of-type="SingleLine.Text" usage="bound" required="true" />' : '';
  return {
    name: `xv_XvTest.${constructor}`,
    manifest: `<?xml version="1.0" encoding="utf-8"?>
<manifest>
  <control namespace="XvTest" constructor="${constructor}" version="1.0.0" display-name-key="N" control-type="${virtual ? 'virtual' : 'standard'}">
    <data-set name="${options.dataSet ?? 'records'}" display-name-key="D">${propertySet}</data-set>
    ${options.extra ?? ''}
    <resources>
      <code path="bundle.js" order="1" />
      ${virtual ? '<platform-library name="React" version="16.14.0" /><platform-library name="Fluent" version="9.46.2" />' : ''}
    </resources>
  </control>
</manifest>`,
    files: { 'bundle.js': `window.ComponentFramework.registerControl('XvTest.${constructor}', window.__xvProbe);` },
  };
}
