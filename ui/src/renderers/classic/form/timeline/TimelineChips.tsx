import { makeStyles, shorthands, ToggleButton, tokens } from '@fluentui/react-components';
import { chipSelected, toggleChip, type ChipId, type TimelineFilters } from '../../../../apps/record/timeline/filters';
import type { TimelineCounts } from '../../../../apps/record/timeline/timelineQuery';
import { t } from '../../../../shell/i18n/t';

const useStyles = makeStyles({
  chips: { display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalXS },
  count: { color: 'inherit', opacity: 0.8, fontWeight: tokens.fontWeightRegular, marginInlineStart: tokens.spacingHorizontalXS },
  // Brand colours, not neutral grey: a tinted chip, and the selected one in the solid brand colour.
  chip: {
    backgroundColor: tokens.colorBrandBackground2,
    color: tokens.colorBrandForeground2,
    ...shorthands.borderColor(tokens.colorBrandStroke2),
    ':hover': { backgroundColor: tokens.colorBrandBackground2Hover, color: tokens.colorBrandForeground2Hover, ...shorthands.borderColor(tokens.colorBrandStroke2Hover) },
    ':hover:active': { backgroundColor: tokens.colorBrandBackground2Pressed, color: tokens.colorBrandForeground2Pressed },
  },
});

const LABELS: Record<ChipId, Parameters<typeof t>[0]> = {
  all: 'record.timeline.chip.all',
  activities: 'record.timeline.chip.activities',
  notes: 'record.timeline.chip.notes',
  posts: 'record.timeline.chip.posts',
  overdue: 'record.timeline.chip.overdue',
  mine: 'record.timeline.chip.mine',
};

/**
 * The chips: shortcuts for one pane filter each (record type, Overdue, Owner = Me), with their faceted counts. A table
 * without activities has no Activities and no Overdue chip; one without notes no Notes chip; a timeline without posts no Posts chip.
 */
export function TimelineChips({
  filters,
  counts,
  hasActivities,
  hasNotes,
  hasPosts = false,
  onChange,
}: {
  filters: TimelineFilters;
  counts?: TimelineCounts;
  hasActivities: boolean;
  hasNotes: boolean;
  hasPosts?: boolean;
  onChange: (f: TimelineFilters) => void;
}) {
  const styles = useStyles();
  const chips: ChipId[] = ['all', ...(hasActivities ? (['activities'] as const) : []), ...(hasNotes ? (['notes'] as const) : []), ...(hasPosts ? (['posts'] as const) : []), ...(hasActivities ? (['overdue'] as const) : []), 'mine'];
  return (
    <div className={styles.chips} role="toolbar" aria-label={t('record.timeline.chips')}>
      {chips.map((chip) => (
        <ToggleButton
          key={chip}
          size="small"
          shape="circular"
          appearance={chipSelected(filters, chip) ? 'primary' : undefined}
          className={chipSelected(filters, chip) ? undefined : styles.chip}
          checked={chipSelected(filters, chip)}
          onClick={() => onChange(toggleChip(filters, chip))}
        >
          {t(LABELS[chip])}
          {counts && (
            <>
              {' '}
              <span className={styles.count}>{counts[chip]}</span>
            </>
          )}
        </ToggleButton>
      ))}
    </div>
  );
}
