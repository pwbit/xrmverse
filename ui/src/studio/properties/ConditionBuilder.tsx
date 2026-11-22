/**
 * The condition builder of the properties panel (spec §4.6): rows `value · op · value`, groups `all` / `any` and `not`, as
 * the 2.21 format writes them (`{ field: a, op: gt, value: 1 }`, `{ all: [...] }`, `{ any: [...] }`, `{ not: ... }`). The
 * right value is typed by the left operand's column and hidden for `null` and `notNull`.
 */
import { Button, makeStyles, tokens } from '@fluentui/react-components';
import { t } from '../../shell/i18n/t';
import { DeleteIcon, NewIcon } from '../../shell/icons';
import { OPERATORS, UNARY_OPERATORS } from './stepSchema';
import { ChoicePicker, ValueEditor } from './ValueEditor';
import { childrenOf, columnOf, conditionKind, joinComparison, newComparison, normalizeCondition, splitComparison, type ConditionKind, type ValueContext } from './valueModel';

const useStyles = makeStyles({
  node: { display: 'flex', flexDirection: 'column', rowGap: tokens.spacingVerticalXS, minWidth: 0 },
  children: {
    display: 'flex',
    flexDirection: 'column',
    rowGap: tokens.spacingVerticalS,
    paddingLeft: tokens.spacingHorizontalM,
    borderLeft: `${tokens.strokeWidthThick} solid ${tokens.colorBrandStroke2}`,
  },
  row: { display: 'flex', alignItems: 'flex-start', columnGap: tokens.spacingHorizontalXS },
  grow: { flexGrow: 1, minWidth: 0 },
  actions: { display: 'flex', flexWrap: 'wrap', columnGap: tokens.spacingHorizontalS },
});

export interface ConditionBuilderProps {
  label: string;
  /** The condition as `valueAt` reads it. */
  value: unknown;
  /** The new condition, in the 2.21 shapes (write it with `conditionForYaml`). */
  onChange(value: unknown): void;
  ctx: ValueContext;
  required?: boolean;
}

export function ConditionBuilder({ label, value, onChange, ctx, required }: ConditionBuilderProps) {
  return <ConditionNode label={label} value={normalizeCondition(value)} onChange={onChange} ctx={ctx} required={required} />;
}

/** A condition of another kind: a comparison becomes the first condition of a group, a group's first condition its comparison. */
function convert(value: unknown, to: ConditionKind): unknown {
  const from = conditionKind(value);
  if (from === to) return value;
  if (to === 'all' || to === 'any') return { [to]: from === 'all' || from === 'any' ? childrenOf(value) : [value] };
  if (to === 'not') return { not: value };
  const inner = from === 'not' ? (value as { not: unknown }).not : childrenOf(value)[0];
  return inner !== undefined && conditionKind(inner) === 'compare' ? inner : newComparison();
}

function ConditionNode({ label, value, onChange, ctx, required, onRemove }: ConditionBuilderProps & { onRemove?(): void }) {
  const styles = useStyles();
  const kind = conditionKind(value);
  const head = (
    <div className={styles.row}>
      <div className={styles.grow}>
        <ChoicePicker
          label={label}
          value={kind}
          onChange={(next) => onChange(convert(value, next as ConditionKind))}
          required={required}
          options={(['compare', 'all', 'any', 'not'] as const).map((k) => [k, t(`studio.props.condition.${k}`)])}
        />
      </div>
      {onRemove && <Button appearance="subtle" icon={<DeleteIcon />} aria-label={t('studio.props.condition.remove', { label })} onClick={onRemove} />}
    </div>
  );

  if (kind === 'compare') {
    const comparison = splitComparison(value);
    const column = columnOf(comparison.left, ctx);
    return (
      <div className={styles.node} role="group" aria-label={label}>
        {head}
        <div className={styles.children}>
          <ValueEditor label={t('studio.props.condition.left')} value={comparison.left} onChange={(left) => onChange(joinComparison({ ...comparison, left: left as Record<string, unknown> }))} ctx={ctx} left required />
          <ChoicePicker
            label={t('studio.props.condition.op')}
            value={comparison.op}
            onChange={(op) => onChange(joinComparison({ ...comparison, op }))}
            required
            options={OPERATORS.map((op) => [op, t(`studio.props.op.${op}`)])}
          />
          {!UNARY_OPERATORS.includes(comparison.op) && (
            <ValueEditor label={t('studio.props.condition.right')} value={comparison.value} onChange={(right) => onChange(joinComparison({ ...comparison, value: right }))} ctx={ctx} column={column} required />
          )}
        </div>
      </div>
    );
  }

  if (kind === 'not') {
    const inner = (value as { not: unknown }).not;
    return (
      <div className={styles.node} role="group" aria-label={label}>
        {head}
        <div className={styles.children}>
          <ConditionNode label={t('studio.props.condition.negated')} value={inner} onChange={(next) => onChange({ not: next })} ctx={ctx} />
        </div>
      </div>
    );
  }

  const items = childrenOf(value);
  const write = (next: unknown[]) => onChange({ [kind]: next });
  return (
    <div className={styles.node} role="group" aria-label={label}>
      {head}
      <div className={styles.children}>
        {items.map((item, i) => (
          <ConditionNode
            key={i}
            label={t('studio.props.condition.item', { n: i + 1 })}
            value={item}
            onChange={(next) => write(items.map((old, j) => (j === i ? next : old)))}
            onRemove={() => write(items.filter((_, j) => j !== i))}
            ctx={ctx}
          />
        ))}
        <div className={styles.actions}>
          <Button size="small" icon={<NewIcon />} onClick={() => write([...items, newComparison()])}>
            {t('studio.props.condition.addRow')}
          </Button>
          <Button size="small" icon={<NewIcon />} onClick={() => write([...items, { all: [newComparison()] }])}>
            {t('studio.props.condition.addGroup')}
          </Button>
        </div>
      </div>
    </div>
  );
}
