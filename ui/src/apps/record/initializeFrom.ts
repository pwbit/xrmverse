import type { FormValues, LookupValue } from '../../admin/tables/formValues';
import { valuesFromRecord } from '../../admin/tables/formValues';
import type { AttributeMeta } from '../../admin/tables/metadata';
import type { LookupRelationship } from '../../admin/tables/tableApi';
import type { XrmApi } from '../../shell/api/client';

/** The record a create form takes its mapped values from (#143): the parent of the subgrid whose New opened it. */
export interface CreateFrom {
  table: string;
  set: string;
  id: string;
  /** The parent's primary name: the label of a lookup that points to it. */
  name: string;
}

const BIND = '@odata.bind';
const KEY = /^([A-Za-z_][A-Za-z0-9_]*)\(([0-9a-fA-F-]{36})\)$/;

/**
 * The `InitializeFrom` function's address for the values a new `target` record takes from `source` (Learn: create a record from
 * another record), with the parameters as aliases: `TargetFieldType` `ValidForCreate`, the columns a create can set.
 */
export function initializeFromPath(source: { set: string; id: string }, target: string): string {
  const moniker = encodeURIComponent(`{'@odata.id':'${source.set}(${source.id})'}`);
  const type = encodeURIComponent("Microsoft.Dynamics.CRM.TargetFieldType'ValidForCreate'");
  return `InitializeFrom(EntityMoniker=@p1,TargetEntityName=@p2,TargetFieldType=@p3)?@p1=${moniker}&@p2=${encodeURIComponent(`'${target}'`)}&@p3=${type}`;
}

/**
 * The editor values of an `InitializeFrom` response for the columns the form shows (`formColumns`) and that no form parameter set
 * (`skip`): a `{nav}@odata.bind` becomes the lookup of the relationship with that navigation property, other properties are read as
 * a record's. A mapped column the form does not show is not filled (Learn: map table columns). The lookups are listed with their
 * set and id, for their names.
 */
export function mappedValues(
  attributes: AttributeMeta[],
  formColumns: string[],
  response: Record<string, unknown>,
  lookups: LookupRelationship[],
  skip: Set<string>,
): { values: FormValues; lookups: { attribute: string; set: string; id: string }[] } {
  const shown = new Map(attributes.filter((a) => formColumns.includes(a.logicalName) && !skip.has(a.logicalName)).map((a) => [a.logicalName, a]));
  const values: FormValues = {};
  const named: { attribute: string; set: string; id: string }[] = [];
  for (const [key, raw] of Object.entries(response)) {
    if (!key.endsWith(BIND)) continue;
    const nav = key.slice(0, -BIND.length);
    const relationship = lookups.find((r) => r.nav === nav);
    const match = typeof raw === 'string' ? KEY.exec(raw.replace(/^\//, '')) : null;
    if (!relationship || !match || !shown.has(relationship.referencingAttribute)) continue;
    const lookup: LookupValue = { id: match[2]!.toLowerCase(), target: relationship.referencedEntity, set: match[1], nav };
    values[relationship.referencingAttribute] = lookup;
    named.push({ attribute: relationship.referencingAttribute, set: match[1]!, id: lookup.id });
  }
  const plain = [...shown.values()].filter((a) => a.kind !== 'lookup' && Object.prototype.hasOwnProperty.call(response, a.logicalName));
  Object.assign(values, valuesFromRecord(plain, response, () => undefined));
  return { values, lookups: named };
}

/**
 * The name of each lookup of `values` (in place): the parent's name for the parent, else the record's primary name, read with the
 * table's `PrimaryNameAttribute`. A read that fails leaves the name out (the field shows the id).
 */
export async function nameLookups(api: XrmApi, values: FormValues, lookups: { attribute: string; set: string; id: string }[], parent: CreateFrom): Promise<void> {
  await Promise.all(
    lookups.map(async ({ attribute, set, id }) => {
      const value = values[attribute] as LookupValue;
      if (set === parent.set && id === parent.id.toLowerCase()) {
        value.name = parent.name;
        return;
      }
      try {
        const definition = await api.get<{ PrimaryNameAttribute?: string | null }>(`EntityDefinitions(LogicalName='${value.target}')?$select=PrimaryNameAttribute`);
        const primary = definition.PrimaryNameAttribute;
        if (!primary) return;
        const row = await api.get<Record<string, unknown>>(`${set}(${id})?$select=${primary}`);
        if (typeof row[primary] === 'string') value.name = row[primary] as string;
      } catch {
        // The id is shown instead of the name.
      }
    }),
  );
}
