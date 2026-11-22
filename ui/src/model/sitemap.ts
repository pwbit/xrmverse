export type SubAreaKind = 'entity' | 'dashboard' | 'webresource' | 'url' | 'custom';

export interface SubArea {
  id: string;
  titles: Record<number, string>;
  /** LCID of the first `<Title>` in document order (`titles` keys iterate numerically). */
  firstLcid?: number;
  kind: SubAreaKind;
  entity?: string;
  url?: string;
  /** `VectorIcon` (else `Icon`) of the subarea: a `$webresource:` name, shown in the navigation. */
  icon?: string;
  privileges: { entity: string; privilege: string }[];
}

export interface Group {
  id: string;
  titles: Record<number, string>;
  firstLcid?: number;
  subAreas: SubArea[];
}

export interface Area {
  id: string;
  titles: Record<number, string>;
  firstLcid?: number;
  groups: Group[];
}

export interface SiteMapModel {
  areas: Area[];
}

/**
 * The title for `lcid`, else 1033, else the first one in document order (`firstLcid`), else the
 * element id.
 */
export function localized(
  titles: Record<number, string>,
  lcid: number,
  fallbackId: string,
  firstLcid?: number,
): string {
  const exact = titles[lcid];
  if (exact !== undefined) return exact;
  const english = titles[1033];
  if (english !== undefined) return english;
  const first = firstLcid !== undefined ? titles[firstLcid] : Object.values(titles)[0];
  return first ?? fallbackId;
}
