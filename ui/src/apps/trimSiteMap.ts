import type { SiteMapModel, SubArea } from '../model/sitemap';

/**
 * Keeps what the user may see (Global Constraints, "Sitemap trimming"):
 * - a subarea with `Entity` needs `prvRead{SchemaName}` at any depth;
 * - each `<Privilege Entity="x" Privilege="A,B"/>` child needs `prvA{SchemaName(x)}` and `prvB{SchemaName(x)}`;
 * - a group or an area left empty is dropped.
 * `entitySchemaNames` maps a logical name to its SchemaName; a table it does not know cannot be checked, so the
 * subarea is hidden. Privilege names are compared without regard to case. The input is not changed.
 */
export function trimSiteMap(
  model: SiteMapModel,
  privileges: Set<string>,
  entitySchemaNames: Record<string, string>,
): SiteMapModel {
  const held = new Set([...privileges].map((p) => p.toLowerCase()));
  const holds = (verb: string, entity: string) => {
    const schema = entitySchemaNames[entity.toLowerCase()];
    return schema !== undefined && held.has(`prv${verb}${schema}`.toLowerCase());
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
