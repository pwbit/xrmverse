import { childElements, parseXml } from '../parsers/xml';
import { PcfLoadError } from './errors';

/**
 * A control manifest (`ControlManifest.xml`), as the manifest schema reference on Learn describes it: the `control` element,
 * its `property`, `data-set`, `resources` (`code`, `css`, `resx`, `img`, `platform-library`) and `feature-usage` children.
 */
export interface PcfProperty {
  name: string;
  /** `of-type`; empty when the property names a type group instead. */
  ofType: string;
  /** The column types it accepts: `of-type`, or the `type`s of its `of-type-group`. */
  types: string[];
  usage: 'bound' | 'input' | 'output';
  required: boolean;
  /** `default-value`, when present. */
  defaultValue?: string;
  /** `display-name-key`, when present and not blank: the key of the property's display name in the control's resx. */
  displayNameKey?: string;
}

export interface PcfResource {
  path: string;
  order: number;
}

export interface PcfManifest {
  namespace: string;
  constructor: string;
  version: string;
  controlType: 'standard' | 'virtual';
  properties: PcfProperty[];
  /** `hasPropertySet`: the `data-set` declares `property-set`s, which this version does not support. */
  dataSets: { name: string; hasPropertySet: boolean }[];
  /** Scripts, sorted by `order` (document order for equal ones). */
  code: PcfResource[];
  css: PcfResource[];
  /** `lcid` is the number before `.resx` in the file name (`strings/X.1033.resx`); undefined when there is none. */
  resx: { path: string; lcid: number | undefined }[];
  img: string[];
  platformLibraries: { name: string; version: string }[];
  features: { name: string; required: boolean }[];
}

const attr = (el: Element, name: string): string => el.getAttribute(name)?.trim() ?? '';
const yes = (el: Element, name: string): boolean => attr(el, name).toLowerCase() === 'true';

function usageOf(el: Element): PcfProperty['usage'] {
  const u = attr(el, 'usage').toLowerCase();
  return u === 'bound' || u === 'output' ? u : 'input';
}

function ordered(resources: Element[], tag: string): PcfResource[] {
  return resources
    .flatMap((r) => childElements(r, tag))
    .map((el, index) => {
      const order = Number(attr(el, 'order'));
      return { path: attr(el, 'path'), order: Number.isFinite(order) && attr(el, 'order') !== '' ? order : 1, index };
    })
    .filter((r) => r.path !== '')
    .sort((a, b) => a.order - b.order || a.index - b.index)
    .map(({ path, order }) => ({ path, order }));
}

/** Parses a manifest; a document that is not XML or has no `control` with a namespace and constructor throws `manifest`. */
export function parseManifest(xml: string): PcfManifest {
  let root: Element;
  try {
    root = parseXml(xml).documentElement;
  } catch (e) {
    throw new PcfLoadError('manifest', `the manifest is not valid XML (${e instanceof Error ? e.message : String(e)})`);
  }
  const control = root.tagName === 'control' ? root : childElements(root, 'control')[0];
  if (!control || !attr(control, 'namespace') || !attr(control, 'constructor')) {
    throw new PcfLoadError('manifest', 'the manifest has no control with a namespace and a constructor');
  }
  const groups = new Map(
    childElements(control, 'type-group').map((g) => [
      attr(g, 'name'),
      childElements(g, 'type').map((t) => t.textContent?.trim() ?? '').filter(Boolean),
    ]),
  );
  const resources = childElements(control, 'resources');
  return {
    namespace: attr(control, 'namespace'),
    constructor: attr(control, 'constructor'),
    version: attr(control, 'version'),
    controlType: attr(control, 'control-type').toLowerCase() === 'virtual' ? 'virtual' : 'standard',
    properties: childElements(control, 'property').map((p) => {
      const ofType = attr(p, 'of-type');
      const property: PcfProperty = {
        name: attr(p, 'name'),
        ofType,
        types: ofType ? [ofType] : (groups.get(attr(p, 'of-type-group')) ?? []),
        usage: usageOf(p),
        required: yes(p, 'required'),
      };
      if (p.hasAttribute('default-value')) property.defaultValue = p.getAttribute('default-value') ?? '';
      const displayNameKey = attr(p, 'display-name-key');
      if (displayNameKey) property.displayNameKey = displayNameKey;
      return property;
    }),
    dataSets: childElements(control, 'data-set').map((d) => ({ name: attr(d, 'name'), hasPropertySet: childElements(d, 'property-set').length > 0 })),
    code: ordered(resources, 'code'),
    css: ordered(resources, 'css'),
    resx: resources
      .flatMap((r) => childElements(r, 'resx'))
      .map((el) => attr(el, 'path'))
      .filter(Boolean)
      .map((path) => {
        const m = /\.(\d+)\.resx$/i.exec(path);
        return { path, lcid: m ? Number(m[1]) : undefined };
      }),
    img: resources.flatMap((r) => childElements(r, 'img')).map((el) => attr(el, 'path')).filter(Boolean),
    platformLibraries: resources
      .flatMap((r) => childElements(r, 'platform-library'))
      .map((el) => ({ name: attr(el, 'name'), version: attr(el, 'version') })),
    features: childElements(control, 'feature-usage')
      .flatMap((f) => childElements(f, 'uses-feature'))
      .map((el) => ({ name: attr(el, 'name'), required: yes(el, 'required') })),
  };
}
