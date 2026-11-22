import { parseManifest } from '../../pcf/manifest';
import type { XrmApi } from '../../shell/api/client';
import { literal } from '../../shell/api/odata';
import type { Relationships } from '../tables/tableApi';
import { listForms } from './formApi';
import { FORM_TYPE } from './formDesign';
import type { PcfControlRow } from './formPcf';

/** A relationship a subgrid of "Only related records" can list (G2), and the table on its other side. */
export interface SubgridSource {
  /** The relationship's schema name (`RelationshipName`). */
  relationship: string;
  target: string;
  kind: 'oneToMany' | 'manyToMany';
}

/**
 * The relationships a subgrid on `table` can show (G2): one-to-many where `table` is the referenced side, and
 * many-to-many where it is either side. Ordered by target table, then schema name.
 */
export function subgridSources(rels: Relationships, table: string): SubgridSource[] {
  const out: SubgridSource[] = [
    ...rels.oneToMany
      .filter((r) => r.referencedEntity === table)
      .map((r): SubgridSource => ({ relationship: r.schemaName, target: r.referencingEntity, kind: 'oneToMany' })),
    ...rels.manyToMany
      .filter((r) => r.entity1 === table || r.entity2 === table)
      .map((r): SubgridSource => ({ relationship: r.schemaName, target: r.entity1 === table ? r.entity2 : r.entity1, kind: 'manyToMany' })),
  ];
  return out.sort((a, b) => a.target.localeCompare(b.target) || a.relationship.localeCompare(b.relationship));
}

/** A public view of a table, as a subgrid offers it. */
export interface PublicView {
  id: string;
  name: string;
  isDefault: boolean;
}

async function all<T>(api: XrmApi, path: string): Promise<T[]> {
  const rows: T[] = [];
  for await (const page of api.list<T>(path)) rows.push(...page);
  return rows;
}

/** The active public views (`querytype` 0, `statecode` 0) of `table`, ordered by name (G2). */
export async function listPublicViews(api: XrmApi, table: string): Promise<PublicView[]> {
  const filter = `returnedtypecode eq ${literal(table)} and querytype eq 0 and statecode eq 0`;
  const rows = await all<{ savedqueryid: string; name: string; isdefault?: boolean }>(
    api, `savedqueries?$select=savedqueryid,name,isdefault&$filter=${encodeURIComponent(filter)}&$orderby=name`,
  );
  return rows.map((r) => ({ id: r.savedqueryid, name: r.name, isDefault: r.isdefault === true }));
}

/** A quick view form of a table. */
export interface QuickViewForm {
  id: string;
  name: string;
}

/** The active quick view forms (type 6) of `table` (G3), in the order of {@link listForms}. */
export async function listQuickViewForms(api: XrmApi, table: string): Promise<QuickViewForm[]> {
  return (await listForms(api, table))
    .filter((f) => f.type === FORM_TYPE.quickView && f.formactivationstate !== 0)
    .map((f) => ({ id: f.systemformid, name: f.name }));
}

/** The web resource types a web resource control shows (G4): HTML, PNG, JPG, GIF, ICO and SVG. */
export const WEB_RESOURCE_TYPES = [1, 5, 6, 7, 10, 11];

export interface WebResourceRow {
  id: string;
  name: string;
  displayName: string;
  type: number;
}

/** The `webresourceset` rows of the given types, ordered by name. */
export async function listWebResources(api: XrmApi, types: number[]): Promise<WebResourceRow[]> {
  const filter = types.map((n) => `webresourcetype eq ${n}`).join(' or ');
  const rows = await all<{ webresourceid: string; name: string; displayname?: string | null; webresourcetype: number }>(
    api, `webresourceset?$select=webresourceid,name,displayname,webresourcetype&$filter=${encodeURIComponent(filter)}&$orderby=name`,
  );
  return rows.map((r) => ({ id: r.webresourceid, name: r.name, displayName: r.displayname ?? '', type: r.webresourcetype }));
}

/**
 * The `customcontrols` rows (G5), ordered by name, each with its parsed manifest; a row whose manifest does not parse is
 * kept as `{ name, error }` with the parser's reason, so that a binding to it can say why it cannot be edited.
 */
export async function listCustomControls(api: XrmApi): Promise<PcfControlRow[]> {
  const rows = await all<{ name: string; manifest?: string | null }>(api, 'customcontrols?$select=name,manifest&$orderby=name');
  return rows.map((r) => {
    try {
      return { name: r.name, manifest: parseManifest(r.manifest ?? '') };
    } catch (e) {
      return { name: r.name, error: e instanceof Error ? e.message : String(e) };
    }
  });
}

/** Whether two ids name the same row: braces and case are ignored. */
export const sameId = (a: string, b: string): boolean =>
  a.replace(/[{}]/g, '').trim().toLowerCase() === b.replace(/[{}]/g, '').trim().toLowerCase();

/** An id as FormXML writes a view or form id: `{id}` in lower case. */
export const formXmlId = (id: string): string => `{${id.replace(/[{}]/g, '').trim().toLowerCase()}}`;
