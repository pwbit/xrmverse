import { Button, makeStyles, MessageBar, MessageBarBody, Select, tokens } from '@fluentui/react-components';
import { useState } from 'react';
import type { en } from '../../shell/i18n/en';
import { t } from '../../shell/i18n/t';
import { DeleteIcon, NewIcon } from '../../shell/icons';
import type { AttributeMeta, OptionMeta } from '../tables/metadata';
import { loadGlobalChoices } from '../tables/tableApi';
import { useLoad } from '../tables/useLoad';
import { ConditionValue, valueShape, type PickedRecord } from './ConditionValue';
import type { DesignCardProps } from './designerSlots';
import { columnKind, filterColumns } from './filterColumns';
import { newKey, operatorsFor, type FilterCondition, type FilterGroup, type FilterNode } from './filterModel';

const useStyles = makeStyles({
  group: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    borderLeft: `${tokens.strokeWidthThick} solid ${tokens.colorNeutralStroke2}`,
    paddingLeft: tokens.spacingHorizontalM,
  },
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  row: { display: 'flex', alignItems: 'flex-end', flexWrap: 'wrap', gap: tokens.spacingHorizontalS },
});

const opLabel = (op: string) => t(`views.filter.op.${op}` as keyof typeof en) ?? op;

/** What every node editor needs besides its node. */
interface Shared {
  table: string;
  attributes: AttributeMeta[];
  columns: AttributeMeta[];
  globalChoices: Record<string, OptionMeta[]>;
  picked: Record<string, PickedRecord>;
  setPicked: (key: string, record: PickedRecord | undefined) => void;
}

function newCondition(s: Shared, primaryName: string): FilterCondition {
  const first = s.columns.find((c) => c.logicalName === primaryName) ?? s.columns[0];
  const attribute = first?.logicalName ?? primaryName;
  return { kind: 'condition', attribute, operator: operatorsFor(columnKind(first), first?.targets.join(','), attribute)[0], values: [], key: newKey() };
}

function ConditionRow({ node, path, shared, onChange, onDelete }: {
  node: FilterCondition;
  path: string;
  shared: Shared;
  onChange: (next: FilterCondition) => void;
  onDelete: () => void;
}) {
  const styles = useStyles();
  const name = t('views.filter.condition', { path });
  const attrOf = (n: string) => shared.attributes.find((a) => a.logicalName === n);
  const operators = (n: string) => {
    const a = attrOf(n);
    return operatorsFor(columnKind(a), a?.targets.join(','), n);
  };
  const attr = attrOf(node.attribute);
  const ops = operators(node.attribute);
  if (!ops.includes(node.operator)) ops.push(node.operator);
  // A condition on a column the editor does not offer still shows under its own name.
  const columns = shared.columns.some((c) => c.logicalName === node.attribute)
    ? shared.columns
    : [{ logicalName: node.attribute, displayName: attr?.displayName ?? node.attribute }, ...shared.columns];

  const setColumn = (attribute: string) => {
    const next = operators(attribute);
    shared.setPicked(node.key, undefined);
    onChange({ ...node, attribute, operator: next.includes(node.operator) ? node.operator : next[0], values: [] });
  };
  const setOperator = (operator: string) =>
    onChange({ ...node, operator, values: valueShape(operator) === valueShape(node.operator) ? node.values : [] });

  return (
    <div role="group" aria-label={name} className={styles.row}>
      <Select aria-label={t('views.filter.column', { condition: name })} value={node.attribute} onChange={(_, d) => setColumn(d.value)}>
        {columns.map((c) => (
          <option key={c.logicalName} value={c.logicalName}>
            {c.displayName}
          </option>
        ))}
      </Select>
      <Select aria-label={t('views.filter.operator', { condition: name })} value={node.operator} onChange={(_, d) => setOperator(d.value)}>
        {ops.map((op) => (
          <option key={op} value={op}>
            {opLabel(op)}
          </option>
        ))}
      </Select>
      <ConditionValue
        // A new column starts a new editor (the lookup field keeps the table it searches).
        key={node.attribute}
        condition={node}
        attr={attr}
        label={t('views.filter.value', { condition: name })}
        table={shared.table}
        globalChoices={shared.globalChoices}
        picked={shared.picked[node.key]}
        onValues={(values) => onChange({ ...node, values })}
        onPicked={(record) => shared.setPicked(node.key, record)}
      />
      <Button appearance="subtle" icon={<DeleteIcon />} aria-label={t('views.filter.deleteCondition', { condition: name })} onClick={onDelete} />
    </div>
  );
}

/** A group and its children; the root is the implicit AND of the entity's filters (E5): it has no type and no Delete. */
function GroupEditor({ node, path, shared, primaryName, onChange, onDelete }: {
  node: FilterGroup;
  path?: string;
  shared: Shared;
  primaryName: string;
  onChange: (next: FilterGroup) => void;
  onDelete?: () => void;
}) {
  const styles = useStyles();
  const root = path === undefined;
  const name = root ? t('views.filter.root') : t('views.filter.group', { path });
  const setChild = (i: number, child: FilterNode) => onChange({ ...node, children: node.children.map((c, j) => (j === i ? child : c)) });
  const removeChild = (i: number) => onChange({ ...node, children: node.children.filter((_, j) => j !== i) });
  const add = (child: FilterNode) => onChange({ ...node, children: [...node.children, child] });

  return (
    <div role="group" aria-label={name} className={root ? styles.root : styles.group}>
      {root ? (
        <p>{t(node.children.length === 0 ? 'views.filter.empty' : 'views.filter.rootHint')}</p>
      ) : (
        <div className={styles.row}>
          <Select
            aria-label={t('views.filter.type', { group: name })}
            value={node.type}
            onChange={(_, d) => onChange({ ...node, type: d.value === 'or' ? 'or' : 'and' })}
          >
            <option value="and">{t('views.filter.and')}</option>
            <option value="or">{t('views.filter.or')}</option>
          </Select>
          <Button appearance="subtle" icon={<DeleteIcon />} aria-label={t('views.filter.deleteGroup', { group: name })} onClick={onDelete} />
        </div>
      )}
      {node.children.map((child, i) => {
        const childPath = root ? String(i + 1) : `${path}.${i + 1}`;
        if (child.kind === 'condition') {
          return (
            <ConditionRow key={child.key} node={child} path={childPath} shared={shared} onChange={(c) => setChild(i, c)} onDelete={() => removeChild(i)} />
          );
        }
        if (child.kind === 'group') {
          return (
            <GroupEditor
              key={child.key}
              node={child}
              path={childPath}
              shared={shared}
              primaryName={primaryName}
              onChange={(g) => setChild(i, g)}
              onDelete={() => removeChild(i)}
            />
          );
        }
        return (
          <MessageBar key={child.key} intent="info">
            <MessageBarBody>{t('views.filter.opaque')}</MessageBarBody>
          </MessageBar>
        );
      })}
      <div className={styles.row}>
        <Button icon={<NewIcon />} aria-label={t('views.filter.addConditionTo', { group: name })} onClick={() => add(newCondition(shared, primaryName))}>
          {t('views.filter.addCondition')}
        </Button>
        <Button
          icon={<NewIcon />}
          aria-label={t('views.filter.addGroupTo', { group: name })}
          onClick={() => add({ kind: 'group', type: 'and', children: [], key: newKey() })}
        >
          {t('views.filter.addGroup')}
        </Button>
      </div>
    </div>
  );
}

/**
 * The filter of the view (E5): groups (and/or, nested to any depth) of conditions on the table's own columns. A part the
 * model reads as opaque is shown as a read-only notice and written back unchanged; the quick find conditions are not here
 * (they are the find columns, E6). New nodes get {@link newKey}; existing ones keep their keys.
 */
export function FilterEditor({ design, attributes, onChange }: DesignCardProps) {
  const needsGlobal = attributes.some((a) => a.kind === 'choice' && a.options.length === 0 && !!a.globalOptionSet);
  const choices = useLoad(needsGlobal ? 'global' : undefined, (api) => loadGlobalChoices(api));
  const [picked, setPickedState] = useState<Record<string, PickedRecord>>({});
  const shared: Shared = {
    table: design.table,
    attributes,
    columns: filterColumns(attributes),
    globalChoices: choices.data ?? {},
    picked,
    setPicked: (key, record) =>
      setPickedState((p) => {
        const { [key]: _old, ...rest } = p;
        return record ? { ...rest, [key]: record } : rest;
      }),
  };
  return (
    <GroupEditor node={design.filter} shared={shared} primaryName={design.primaryName} onChange={(filter) => onChange({ ...design, filter })} />
  );
}
