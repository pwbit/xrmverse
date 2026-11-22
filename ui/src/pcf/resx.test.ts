import { describe, expect, it, vi } from 'vitest';
import { loadStrings, parseResx } from './resx';
import { parseManifest } from './manifest';
import { resx, standardControl } from '../test/pcf/controls';

const manifest = parseManifest(standardControl.manifest);

/** A fetch that serves the test control's files under `/Controls/{name}/`; anything else is a 404. */
function serving(files: Record<string, string>) {
  return vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input);
    const prefix = `/Controls/${encodeURIComponent(standardControl.name)}/`;
    const body = url.startsWith(prefix) ? files[decodeURI(url.slice(prefix.length))] : undefined;
    return body === undefined ? new Response('', { status: 404 }) : new Response(body, { status: 200 });
  });
}

describe('parseResx', () => {
  it('reads_string_entries_and_skips_typed_ones', () => {
    const xml = resx({ A: 'One', B: 'Two &amp; three' }).replace(
      '</root>',
      '<data name="Pic" type="System.Byte[], mscorlib" mimetype="application/x-microsoft.net.object.bytearray.base64"><value>AAAA</value></data></root>',
    );
    expect(parseResx(xml)).toEqual({ A: 'One', B: 'Two & three' });
  });

  it('returns_nothing_for_a_malformed_document', () => {
    expect(parseResx('<root')).toEqual({});
  });
});

describe('loadStrings', () => {
  it('uses_the_users_language_and_fills_missing_keys_from_1033', async () => {
    const fetch = serving(standardControl.files);
    const strings = await loadStrings(standardControl.name, manifest, 1040, { fetch });
    expect(strings).toEqual({ Doubler_Name: 'Raddoppia', Value_Name: 'Valore', Doubler_Desc: 'Doubles a number' });
    expect(fetch).toHaveBeenCalledWith('/Controls/xv_XvTest.Doubler/strings/Doubler.1040.resx', expect.anything());
  });

  it('falls_back_to_1033_for_a_language_without_resx', async () => {
    const strings = await loadStrings(standardControl.name, manifest, 1036, { fetch: serving(standardControl.files) });
    expect(strings).toEqual({ Doubler_Name: 'Doubler', Doubler_Desc: 'Doubles a number', Value_Name: 'Value' });
  });

  it('falls_back_to_the_first_resx_without_1033', async () => {
    const m = parseManifest(
      standardControl.manifest.replace('<resx path="strings/Doubler.1033.resx" version="1.0.0" />', ''),
    );
    const strings = await loadStrings(standardControl.name, m, 1036, { fetch: serving(standardControl.files) });
    expect(strings).toEqual({ Doubler_Name: 'Raddoppia', Value_Name: 'Valore' });
  });

  it('skips_a_resx_that_cannot_be_fetched', async () => {
    const files = { ...standardControl.files };
    delete files['strings/Doubler.1040.resx'];
    const strings = await loadStrings(standardControl.name, manifest, 1040, { fetch: serving(files) });
    expect(strings.Doubler_Name).toBe('Doubler');
  });

  it('returns_no_strings_for_a_control_without_resx', async () => {
    const fetch = serving({});
    const m = parseManifest(standardControl.manifest.replace(/<resx [^>]*\/>/g, ''));
    expect(await loadStrings(standardControl.name, m, 1033, { fetch })).toEqual({});
    expect(fetch).not.toHaveBeenCalled();
  });
});
