import { t } from '../../shell/i18n/t';
import type { AttributeMeta, EntityMeta } from '../tables/metadata';
import { allCells, lower } from './formCells';
import type { SubgridSpec } from './formControls';
import type { FormDesign } from './formDesign';

// The columns a subgrid of records matching a column of the form (X.11) can be filtered by. Customer and Owner columns
// are parsed as lookups (a party list is not one).

const byDisplayName = (a: AttributeMeta, b: AttributeMeta) => (a.displayName || a.logicalName).localeCompare(b.displayName || b.logicalName);

/** A lookup, customer or owner column a filter can use: readable, with targets, not another column's companion. */
const filterable = (a: AttributeMeta) => a.kind === 'lookup' && a.targets.length > 0 && a.validForRead && !a.attributeOf;

/** The columns (lower case) of the form's field cells, header and body. */
export function placedColumns(design: FormDesign): Set<string> {
  return new Set(allCells(design).filter((c) => c.kind === 'field' && c.attribute).map((c) => lower(c.attribute)));
}

/** The lookup, customer and owner columns of the form's table that are placed on the form, by display name. */
export function filterSources(form: Pick<EntityMeta, 'attributes'>, placed: Set<string>): AttributeMeta[] {
  return form.attributes.filter((a) => filterable(a) && placed.has(lower(a.logicalName))).sort(byDisplayName);
}

/**
 * The lookup, customer and owner columns of the subgrid's table that can hold every record `source` can: their targets
 * include each target of `source`. By display name.
 */
export function filterTargets(source: AttributeMeta, table: Pick<EntityMeta, 'attributes'>): AttributeMeta[] {
  const wanted = source.targets.map(lower);
  if (wanted.length === 0) return [];
  return table.attributes
    .filter((a) => filterable(a) && wanted.every((w) => a.targets.some((x) => lower(x) === w)))
    .sort(byDisplayName);
}

const named = (attributes: AttributeMeta[], name: string) => attributes.find((a) => lower(a.logicalName) === lower(name));

/**
 * Why the subgrid's filter no longer works, as the text to show: its source is no longer a lookup placed on the form, or
 * its target cannot match the source (checked once the subgrid's table is read). Undefined for a working filter and for
 * a subgrid without one.
 */
export function filterProblem(
  design: FormDesign, spec: SubgridSpec, form: Pick<EntityMeta, 'attributes'>, table?: Pick<EntityMeta, 'attributes' | 'displayName' | 'logicalName'>,
): string | undefined {
  const filter = spec.filter;
  if (!filter) return undefined;
  const source = named(filterSources(form, placedColumns(design)), filter.source);
  if (!source) {
    return t('forms.props.filterSourceGone', { name: named(form.attributes, filter.source)?.displayName || filter.source });
  }
  if (table && !named(filterTargets(source, table), filter.target)) {
    return t('forms.props.filterTargetGone', {
      name: named(table.attributes, filter.target)?.displayName || filter.target,
      table: table.displayName || table.logicalName,
    });
  }
  return undefined;
}
