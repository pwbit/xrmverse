import { childElements, parseXml } from '../parsers/xml';
import { controlFileUrl } from './loader';
import type { PcfManifest } from './manifest';

/** The string entries of a `.resx` document (`<data name><value>`); entries with a `type` or `mimetype` are not strings. */
export function parseResx(xml: string): Record<string, string> {
  let root: Element;
  try {
    root = parseXml(xml).documentElement;
  } catch {
    return {};
  }
  const out: Record<string, string> = {};
  for (const data of childElements(root, 'data')) {
    const name = data.getAttribute('name');
    if (!name || data.hasAttribute('type') || data.hasAttribute('mimetype')) continue;
    out[name] = childElements(data, 'value')[0]?.textContent ?? '';
  }
  return out;
}

export interface StringsDeps {
  /** Default `/Controls`. */
  baseUrl?: string;
  fetch?: typeof fetch;
}

const DEFAULT_LCID = 1033;

/**
 * The control's strings for `languageId` (ruling R6): the resx for that language, then 1033 for the keys it lacks, then
 * the first resx of the manifest. A file that cannot be fetched is skipped; a key no file has is absent (the caller shows
 * the key).
 */
export async function loadStrings(
  name: string,
  manifest: PcfManifest,
  languageId: number,
  deps: StringsDeps = {},
): Promise<Record<string, string>> {
  const doFetch = deps.fetch ?? ((input, init) => globalThis.fetch(input, init));
  const chosen = [
    manifest.resx.find((r) => r.lcid === languageId),
    manifest.resx.find((r) => r.lcid === DEFAULT_LCID),
    manifest.resx[0],
  ].filter((r, i, all): r is NonNullable<typeof r> => r !== undefined && all.indexOf(r) === i);
  const texts = await Promise.all(
    chosen.map(async (r) => {
      try {
        const response = await doFetch(controlFileUrl(name, r.path, deps.baseUrl), { credentials: 'same-origin' });
        return response.ok ? await response.text() : undefined;
      } catch {
        return undefined;
      }
    }),
  );
  const strings: Record<string, string> = {};
  for (const text of texts) {
    if (text === undefined) continue;
    for (const [key, value] of Object.entries(parseResx(text))) if (!(key in strings)) strings[key] = value;
  }
  return strings;
}
