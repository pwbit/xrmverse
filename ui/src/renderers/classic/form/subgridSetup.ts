import { loadRelationships } from '../../../admin/tables/tableApi';
import { loadTable, loadViews, type TableInfo } from '../../../apps/loadViews';
import { findRelationship, type RelationshipMeta } from '../../../apps/record/subgridQuery';
import type { SubgridControl as SubgridModel } from '../../../model/form';
import type { ViewModel } from '../../../model/view';
import { linkEntities } from '../../../parsers/fetchXml';
import { normaliseId } from '../../../parsers/formXml';
import type { XrmApi } from '../../../shell/api/client';
import { t } from '../../../shell/i18n/t';
import { cachedRead, type ControlEnv } from './controlEnv';

/** What a subgrid needs before it can query: its view, the tables of its columns and its relationship. */
export interface SubgridSetup {
  view: ViewModel;
  /** The table the view lists, and the tables of the view's link-entities, by logical name. */
  tables: Record<string, TableInfo>;
  target: string;
  /** Absent only when `relationshipOptional` was asked and the subgrid names none: the view's rows are not limited. */
  relationship?: RelationshipMeta;
}

/** The subgrid's view is not among the table's views (Review Focus 3: a dashboard names it as not found). */
export class UnknownViewError extends Error {
  constructor(readonly viewId: string) {
    super(t('record.subgrid.unknownView', { id: viewId }));
  }
}

/** The table on the other side of `relationship` from `table`: what a related-tables item lists; undefined when it does not relate `table`'s rows. */
function relatedTable(relationship: RelationshipMeta, table: string): string | undefined {
  if ('referencingAttribute' in relationship) return relationship.referencedEntity === table ? relationship.referencingEntity : undefined;
  if (relationship.entity1 === table) return relationship.entity2;
  return relationship.entity2 === table ? relationship.entity1 : undefined;
}

/**
 * Throws (with the reason as the message) when the subgrid's relationship or view cannot be used. A related-tables item (#120)
 * names only the relationship: the table is the other side of it, and the view its associated view, else its default public
 * view, else its first public view. With `relationshipOptional`, a subgrid without a relationship lists the whole view.
 */
export async function loadSubgridSetup(api: XrmApi, control: SubgridModel, env: ControlEnv, options: { relationshipOptional?: boolean } = {}): Promise<SubgridSetup> {
  const lcid = env.lcid;
  if (!control.relationship && !options.relationshipOptional) throw new Error(t('record.subgrid.noRelationship'));
  if (!control.targetEntity && !control.related) throw new Error(t('record.subgrid.noTarget'));
  if (!control.viewId && !control.related) throw new Error(t('record.subgrid.noView'));
  let relationship: RelationshipMeta | undefined;
  if (control.relationship) {
    const rels = await cachedRead(env, 'relationships', () => loadRelationships(api));
    relationship = findRelationship(rels, control.relationship);
    if (!relationship)
      throw new Error(
        t('record.subgrid.unknownRelationship', {
          name: control.relationship,
        }),
      );
  }
  const target = control.targetEntity || (relationship && relatedTable(relationship, env.ctx.entity.logicalName));
  if (!target) throw new Error(t('record.related.notThisTable', { name: control.relationship }));
  const views = await cachedRead(env, `views|${target}`, () => loadViews(api, target));
  let view: ViewModel | undefined;
  if (control.viewId) {
    const viewId = normaliseId(control.viewId);
    view = [...views.system, ...views.personal].find((v) => v.id === viewId);
    if (!view) throw new UnknownViewError(viewId);
  } else {
    view = views.associated ?? views.system.find((v) => v.isDefault) ?? views.system[0];
    if (!view) throw new Error(t('record.subgrid.noView'));
  }
  const linked = Object.values(linkEntities(view.fetchXml)).filter((n) => n !== target);
  const infos = await Promise.all(
    [...new Set([target, ...linked])].map((name) => cachedRead(env, `table|${name}|${lcid}`, () => loadTable(api, name, lcid))),
  );
  const tables = Object.fromEntries(infos.map((info) => [info.logicalName, info]));
  return relationship ? { view, tables, target, relationship } : { view, tables, target };
}
