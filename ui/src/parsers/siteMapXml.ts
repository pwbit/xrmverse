import type { Area, Group, SiteMapModel, SubArea, SubAreaKind } from '../model/sitemap';
import { childElements, parseXml } from './xml';

interface Titles {
  titles: Record<number, string>;
  firstLcid?: number;
}

/** A `<Title>` without a numeric LCID is skipped. */
function titlesOf(el: Element): Titles {
  const titles: Record<number, string> = {};
  let firstLcid: number | undefined;
  for (const holder of childElements(el, 'Titles')) {
    for (const t of childElements(holder, 'Title')) {
      const raw = t.getAttribute('LCID')?.trim() ?? '';
      const title = t.getAttribute('Title');
      if (!/^\d+$/.test(raw) || title === null) continue;
      const lcid = Number(raw);
      titles[lcid] = title;
      firstLcid ??= lcid;
    }
  }
  return firstLcid === undefined ? { titles } : { titles, firstLcid };
}

function kindOf(el: Element, url: string | undefined): SubAreaKind {
  if (el.getAttribute('Entity')) return 'entity';
  const u = (url ?? '').trim().toLowerCase();
  if (u.includes('pagetype=dashboard')) return 'dashboard';
  if (u.startsWith('$webresource:') || u.startsWith('/webresources/')) return 'webresource';
  if (u.includes('pagetype=custom') || el.getAttribute('Type') === 'PageType') return 'custom';
  return 'url';
}

function subAreaOf(el: Element): SubArea {
  const url = el.getAttribute('Url') ?? undefined;
  const entity = el.getAttribute('Entity') || undefined;
  const sub: SubArea = {
    id: el.getAttribute('Id') ?? '',
    ...titlesOf(el),
    kind: kindOf(el, url),
    privileges: childElements(el, 'Privilege').map((p) => ({
      entity: p.getAttribute('Entity') ?? '',
      privilege: p.getAttribute('Privilege') ?? '',
    })),
  };
  if (entity) sub.entity = entity;
  if (url) sub.url = url;
  const icon = el.getAttribute('VectorIcon') || el.getAttribute('Icon') || undefined;
  if (icon) sub.icon = icon;
  return sub;
}

function groupOf(el: Element): Group {
  return {
    id: el.getAttribute('Id') ?? '',
    ...titlesOf(el),
    subAreas: childElements(el, 'SubArea').map(subAreaOf),
  };
}

function areaOf(el: Element): Area {
  return {
    id: el.getAttribute('Id') ?? '',
    ...titlesOf(el),
    groups: childElements(el, 'Group').map(groupOf),
  };
}

/** Parses a `<SiteMap>` element (or a wrapper that contains one) into the semantic model. */
export function parseSiteMap(xml: string): SiteMapModel {
  const root = parseXml(xml).documentElement;
  const siteMap = root.tagName === 'SiteMap' ? root : root.getElementsByTagName('SiteMap')[0];
  if (!siteMap) throw new Error('No <SiteMap> element found.');
  return { areas: childElements(siteMap, 'Area').map(areaOf) };
}
