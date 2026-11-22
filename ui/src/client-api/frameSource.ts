import type { RecordContext } from '../apps/record/loadRecord';
import type { WebResourceControl } from '../model/form';
import { webResourceSrc } from './scriptHost';
import { bracedUpper } from './values';

/** An address an IFRAME may not show (a scheme other than http and https, or no address at all). */
export class FrameUrlError extends Error {}

/** What the form passes to a frame whose control has `PassParameters` (Learn: id, typename, orgname, userlcid, orglcid). */
export interface FrameParameters {
  id: string;
  typename: string;
  orgname: string;
  userlcid: number;
  orglcid: number;
}

/**
 * The record parameters of a frame. `id` is empty until the record is saved, as Learn states. Learn also lists `type` (the
 * table type code), and recommends `typename` instead because the code differs between organizations: it is not passed (ruling R-T6-2).
 */
export function frameParameters(env: { ctx: RecordContext; lcid: number }, globalContext: unknown): FrameParameters {
  const org = (globalContext as { organizationSettings?: { uniqueName?: unknown; languageId?: unknown } } | null)?.organizationSettings;
  const id = env.ctx.record?.[env.ctx.entity.primaryIdAttribute];
  return {
    id: typeof id === 'string' && id ? bracedUpper(id) : '',
    typename: env.ctx.entity.logicalName,
    orgname: typeof org?.uniqueName === 'string' ? org.uniqueName : '',
    userlcid: env.lcid,
    orglcid: typeof org?.languageId === 'number' ? org.languageId : env.lcid,
  };
}

const queryOf = (p: FrameParameters): Record<string, string> => ({
  id: p.id,
  orglcid: String(p.orglcid),
  orgname: p.orgname,
  typename: p.typename,
  userlcid: String(p.userlcid),
});

/** The address as a URL, refusing `javascript:`, `data:` and every other scheme but http and https. */
export function checkedFrameUrl(raw: string): URL {
  const trimmed = raw.trim();
  if (!trimmed) throw new FrameUrlError('empty');
  let url: URL;
  try {
    url = new URL(trimmed, location.origin);
  } catch {
    throw new FrameUrlError(trimmed);
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') throw new FrameUrlError(trimmed);
  return url;
}

/**
 * The `sandbox` attribute of a frame, or undefined when it has none. Another origin gets scripts, forms and pop-ups but not
 * the origin of the shell, so it cannot reach `parent.Xrm`. With restricted cross-frame scripting (`Security`), Learn's empty
 * `sandbox=""` applies to any address.
 */
export function frameSandbox(src: string, restrictScripting: boolean): string | undefined {
  if (restrictScripting) return '';
  return checkedFrameUrl(src).origin === location.origin ? undefined : 'allow-scripts allow-forms allow-popups';
}

/** `url` with `query` appended to its own query string, keeping the fragment. */
function withQuery(url: URL, query: Record<string, string>): string {
  const added = Object.entries(query).map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(v)}`);
  url.search = [url.search.replace(/^\?/, ''), ...added].filter(Boolean).join('&');
  return url.origin === location.origin ? `${url.pathname}${url.search}${url.hash}` : url.href;
}

/**
 * The address a web resource or IFRAME control starts with: `/WebResources/{name}` with its `data` for a web resource, the
 * `Url` for an IFRAME, and the record parameters when the control passes them. Throws {@link FrameUrlError}.
 */
export function initialFrameSrc(control: WebResourceControl, params: FrameParameters): string {
  const pass = control.passParameters ? queryOf(params) : {};
  if (!control.iframe) {
    if (!control.url.trim()) throw new FrameUrlError('empty');
    return webResourceSrc(control.url.trim(), { ...(control.data ? { data: control.data } : {}), ...pass });
  }
  return withQuery(checkedFrameUrl(control.url), pass);
}
