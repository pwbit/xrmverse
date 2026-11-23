import type { SiteMapModel, SubArea } from '../model/sitemap';
import { holds as holdsPrivilege, PRIVILEGE_VERBS } from './record/privileges';

/**
 * Keeps what the user may see (Global Constraints, "Sitemap trimming"):
 * - a subarea with `Entity` needs `prvRead{SchemaName}` at any depth;
 * - each `<Privilege Entity="x" Privilege="A,B"/>` child needs `prvA{SchemaName(x)}` and `prvB{SchemaName(x)}`;
 * - a group or an area left empty is dropped.
 * `tables` maps a logical name (lower case) to its SchemaName and `IsActivity`; an activity table is also met by the shared
 * `prv{Verb}Activity` (see `holds`). A table it does not know cannot be checked, so the subarea is hidden. Privilege names
 * are compared without regard to case. The input is not changed.
 */
export function trimSiteMap(
  model: SiteMapModel,
  privileges: Set<string>,
  tables: Record<string, { schemaName: string; isActivity: boolean }>,
): SiteMapModel {
  // Names are compared without regard to case: each held name is put in the casing `holds` builds (prv{Verb}{SchemaName}
  // or prv{Verb}Activity), and each verb of the site map in the casing of PRIVILEGE_VERBS.
  const canonical = new Map<string, string>();
  for (const verb of PRIVILEGE_VERBS) {
    canonical.set(`prv${verb}Activity`.toLowerCase(), `prv${verb}Activity`);
    for (const { schemaName } of Object.values(tables)) canonical.set(`prv${verb}${schemaName}`.toLowerCase(), `prv${verb}${schemaName}`);
  }
  const held: ReadonlySet<string> = new Set([...privileges].map((p) => canonical.get(p.toLowerCase()) ?? p));
  const holds = (verb: string, entity: string) => {
    const table = tables[entity.toLowerCase()];
    const known = PRIVILEGE_VERBS.find((v) => v.toLowerCase() === verb.toLowerCase());
    return table !== undefined && known !== undefined && holdsPrivilege(held, known, table);
  };
  const visible = (sub: SubArea) =>
    (!sub.entity || holds('Read', sub.entity)) &&
    sub.privileges.every((p) =>
      p.privilege
        .split(',')
        .map((verb) => verb.trim())
        .filter(Boolean)
        .every((verb) => holds(verb, p.entity)),
    );
  return {
    areas: model.areas
      .map((area) => ({
        ...area,
        groups: area.groups
          .map((group) => ({ ...group, subAreas: group.subAreas.filter(visible) }))
          .filter((group) => group.subAreas.length > 0),
      }))
      .filter((area) => area.groups.length > 0),
  };
}
