import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { XrmApi } from '../../shell/api/client';
import { XrmError } from '../../shell/api/errors';
import { publishOptions, updateOptionColor } from './tableApi';

const api = () => new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => {} });
const ROOT = '/api/data/v9.2';

interface Seen { method: string; path: string; body: unknown }

function capture(fail?: string): Seen[] {
  const seen: Seen[] = [];
  server.use(
    http.all(`*${ROOT}/*`, async ({ request }) => {
      const path = decodeURIComponent(new URL(request.url).pathname).slice(ROOT.length + 1);
      const text = await request.text();
      seen.push({ method: request.method, path, body: text ? JSON.parse(text) : undefined });
      if (path === fail) return HttpResponse.json({ error: { code: '0x80040203', message: 'Color must be #RRGGBB.' } }, { status: 400 });
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return seen;
}

describe('updateOptionColor', () => {
  it('sets the colour of a local option, without publishing', async () => {
    const seen = capture();
    await updateOptionColor(api(), { table: 'xv_project', column: 'xv_priority' }, 1, '#FF0000');
    expect(seen).toEqual([
      { method: 'POST', path: 'UpdateOptionValue', body: { EntityLogicalName: 'xv_project', AttributeLogicalName: 'xv_priority', Value: 1, Color: '#FF0000', MergeLabels: true } },
    ]);
  });

  it('clears the colour of a global option (Color "")', async () => {
    const seen = capture();
    await updateOptionColor(api(), { optionSetName: 'xv_level' }, 100000001, null);
    expect(seen).toEqual([{ method: 'POST', path: 'UpdateOptionValue', body: { OptionSetName: 'xv_level', Value: 100000001, Color: '', MergeLabels: true } }]);
  });

  it('throws the server message when the update fails', async () => {
    capture('UpdateOptionValue');
    const error = await updateOptionColor(api(), { table: 'xv_project', column: 'xv_priority' }, 1, '#FF0000').catch((e: unknown) => e);
    expect(error).toBeInstanceOf(XrmError);
    expect((error as Error).message).toContain('Color must be #RRGGBB.');
  });
});

describe('publishOptions', () => {
  it('publishes the table of a local option set', async () => {
    const seen = capture();
    await publishOptions(api(), { table: 'xv_project', column: 'xv_priority' });
    expect(seen).toEqual([
      { method: 'POST', path: 'PublishXml', body: { ParameterXml: '<importexportxml><entities><entity>xv_project</entity></entities></importexportxml>' } },
    ]);
  });

  it('publishes a global option set by its name', async () => {
    const seen = capture();
    await publishOptions(api(), { optionSetName: 'xv_level' });
    expect(seen).toEqual([
      { method: 'POST', path: 'PublishXml', body: { ParameterXml: '<importexportxml><optionsets><optionset>xv_level</optionset></optionsets></importexportxml>' } },
    ]);
  });
});
