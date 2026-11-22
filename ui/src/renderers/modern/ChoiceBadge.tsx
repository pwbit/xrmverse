import { Badge, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { useCallback, type ReactNode } from 'react';
import type { ViewColumn } from '../../model/view';
import type { GridRow } from '../classic/ViewGrid';
import { usePresentationModel } from './PresentationContext';
import { useChoicePalette, type ChoiceBadgeValue } from './useChoicePalette';

const useStyles = makeStyles({
  // Plain text (not bold), taller than Fluent's badge and squarer, so a value reads like a label in the row.
  badge: {
    height: 'auto',
    minHeight: '28px',
    paddingBlock: tokens.spacingVerticalXXS,
    paddingInline: tokens.spacingHorizontalS,
    borderRadius: tokens.borderRadiusMedium,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    fontWeight: tokens.fontWeightRegular,
  },
  compact: {
    minHeight: '22px',
    paddingBlock: 0,
    paddingInline: tokens.spacingHorizontalSNudge,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  list: { display: 'inline-flex', flexWrap: 'wrap', alignItems: 'center', gap: tokens.spacingHorizontalXS, verticalAlign: 'middle' },
});

/**
 * A choice value as a coloured badge (modern look). The colours are the maker's data (the option's colour and the overlay's
 * text colour), so they are inline styles, not theme tokens. The label is the badge's text; it has no role of its own.
 */
export function ChoiceBadge({ label, background, text }: { label: string; background: string; text: string }) {
  const styles = useStyles();
  const compact = usePresentationModel()?.theme.density === 'compact';
  return (
    <Badge
      className={mergeClasses(styles.badge, compact && styles.compact)}
      appearance="filled"
      shape="rounded"
      size={compact ? 'small' : 'medium'}
      style={{ backgroundColor: background, color: text }}>
      {label}
    </Badge>
  );
}

/** The badges of one value (one per option of a multi-select value), in order. */
export function ChoiceBadges({ badges }: { badges: ChoiceBadgeValue[] }) {
  const styles = useStyles();
  return (
    <span className={styles.list}>
      {badges.map((b, i) => (
        <ChoiceBadge key={i} {...b} />
      ))}
    </span>
  );
}

/** The `renderCellContent` of a grid or card list on `table`: coloured choice values as badges; undefined without a table. */
export function useChoiceCells(
  table: string | undefined,
  columns: ViewColumn[],
): ((row: GridRow, column: ViewColumn, text: string) => ReactNode) | undefined {
  const palette = useChoicePalette(table, columns);
  const render = useCallback(
    (row: GridRow, column: ViewColumn, text: string): ReactNode => {
      const badges = column.alias ? undefined : palette(column, row.values?.[column.attribute]);
      return badges ? <ChoiceBadges badges={badges} /> : text;
    },
    [palette],
  );
  return table ? render : undefined;
}
